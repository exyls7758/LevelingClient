using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Multiverse.Services
{
    internal readonly record struct AudioSessionDescriptor(
        int ProcessId,
        string ProcessName,
        string FriendlyName);

    internal sealed class AudioApplicationDescriptor
    {
        public int RootProcessId { get; init; }
        public string ProcessName { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public IReadOnlyList<int> SessionProcessIds { get; init; } = Array.Empty<int>();
        public IReadOnlyList<string> SessionProcessNames { get; init; } = Array.Empty<string>();
    }

    internal sealed class AudioApplicationDiscoveryService
    {
        internal IReadOnlyList<AudioApplicationDescriptor> EnumerateActiveApplications()
        {
            var sessions = new List<AudioSessionDescriptor>();
            int ownPid = Environment.ProcessId;

            try
            {
                using MMDeviceEnumerator enumerator = new();
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render,
                    DeviceState.Active))
                {
                    try
                    {
                        SessionCollection collection = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < collection.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = collection[i];
                                int pid = checked((int)session.GetProcessID);
                                if (pid <= 0 || pid == ownPid)
                                    continue;

                                using Process process = Process.GetProcessById(pid);
                                string processName = AudioApplicationPolicy.NormalizeExecutable(process.ProcessName);
                                if (string.IsNullOrWhiteSpace(processName))
                                    continue;

                                string friendly = "";
                                try { friendly = process.MainWindowTitle?.Trim() ?? ""; } catch { }
                                if (string.IsNullOrWhiteSpace(friendly))
                                {
                                    try { friendly = session.DisplayName?.Trim() ?? ""; } catch { }
                                }
                                if (string.IsNullOrWhiteSpace(friendly))
                                    friendly = processName;

                                sessions.Add(new AudioSessionDescriptor(pid, processName, friendly));
                            }
                            catch
                            {
                                // Sessions can disappear between enumeration and process lookup.
                            }
                            finally
                            {
                                try { session?.Dispose(); } catch { }
                            }
                        }
                    }
                    catch
                    {
                        // One broken endpoint must not prevent discovery on the other endpoints.
                    }
                    finally
                    {
                        try { device.Dispose(); } catch { }
                    }
                }
            }
            catch
            {
                return Array.Empty<AudioApplicationDescriptor>();
            }

            try
            {
                using ToolhelpProcessTreeReader tree = new();
                return GroupSessions(
                    sessions,
                    pid => AudioProcessRootResolver.ResolveRoot(pid, tree));
            }
            catch
            {
                return GroupSessions(sessions, pid => pid);
            }
        }

        internal static IReadOnlyList<AudioApplicationDescriptor> GroupSessions(
            IEnumerable<AudioSessionDescriptor> sessions,
            Func<int, int> resolveRoot)
        {
            var grouped = new Dictionary<int, List<AudioSessionDescriptor>>();
            foreach (AudioSessionDescriptor session in sessions)
            {
                int root = resolveRoot(session.ProcessId);
                if (root <= 0)
                    root = session.ProcessId;
                if (!grouped.TryGetValue(root, out List<AudioSessionDescriptor>? list))
                {
                    list = new List<AudioSessionDescriptor>();
                    grouped[root] = list;
                }
                if (!list.Any(x => x.ProcessId == session.ProcessId))
                    list.Add(session);
            }

            var result = new List<AudioApplicationDescriptor>();
            foreach ((int rootPid, List<AudioSessionDescriptor> list) in grouped)
            {
                AudioSessionDescriptor first = list[0];
                string processName = first.ProcessName;
                string friendly = list
                    .Select(x => x.FriendlyName)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) &&
                        !string.Equals(x, processName, StringComparison.OrdinalIgnoreCase))
                    ?? processName;

                // Prefer the root process title/name when it is still alive.
                try
                {
                    using Process root = Process.GetProcessById(rootPid);
                    processName = AudioApplicationPolicy.NormalizeExecutable(root.ProcessName);
                    string title = root.MainWindowTitle?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(title))
                        friendly = title;
                }
                catch { }

                result.Add(new AudioApplicationDescriptor
                {
                    RootProcessId = rootPid,
                    ProcessName = processName,
                    DisplayName = $"{friendly}  •  {processName}.exe",
                    SessionProcessIds = list.Select(x => x.ProcessId).OrderBy(x => x).ToArray(),
                    SessionProcessNames = list
                        .Select(x => AudioApplicationPolicy.NormalizeExecutable(x.ProcessName))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray()
                });
            }

            return result
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }
}
