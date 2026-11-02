using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiverse.Services
{
    internal sealed class MicrophoneUsageSnapshot
    {
        internal bool Verified { get; init; }
        internal IReadOnlySet<int> ProcessIds { get; init; } = new HashSet<int>();
        internal int ActiveCaptureEndpoints { get; init; }
        internal string Error { get; init; } = "";
    }

    /// <summary>
    /// Discovers process trees that currently own an active capture session on
    /// any Windows microphone/input endpoint. It never opens, reads or records
    /// microphone samples; only Core Audio session metadata/PIDs are queried.
    ///
    /// Privacy policy is intentionally fail-closed: if Windows cannot verify
    /// every active capture endpoint, Verified is false and automatic audio must
    /// temporarily transmit no application sources.
    /// </summary>
    internal sealed class MicrophoneUsageDetector
    {
        internal MicrophoneUsageSnapshot Scan()
        {
            var result = new HashSet<int>();
            int ownPid = Environment.ProcessId;
            int activeEndpoints = 0;
            bool verified = true;
            string error = "";

            try
            {
                using MMDeviceEnumerator enumerator = new();
                MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
                    DataFlow.Capture,
                    DeviceState.Active);

                activeEndpoints = devices.Count;
                foreach (MMDevice device in devices)
                {
                    try
                    {
                        SessionCollection sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];

                                // Windows AudioSessionState: Inactive=0, Active=1,
                                // Expired=2. Only Active means the process currently
                                // owns an active capture session on this input.
                                if ((int)session.State != 1)
                                    continue;

                                int pid = checked((int)session.GetProcessID);
                                if (pid <= 0 || pid == ownPid)
                                    continue;

                                result.Add(pid);
                            }
                            catch (Exception ex)
                            {
                                // Strict privacy policy: even a transient session race
                                // means this 500 ms verification pass is inconclusive.
                                // Automatic audio pauses and the next pass retries.
                                verified = false;
                                if (string.IsNullOrWhiteSpace(error))
                                    error = ex.Message;
                            }
                            finally
                            {
                                try { session?.Dispose(); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // If one active input endpoint cannot be enumerated, we can no
                        // longer guarantee that a microphone-using process is absent.
                        verified = false;
                        if (string.IsNullOrWhiteSpace(error))
                            error = ex.Message;
                    }
                    finally
                    {
                        try { device.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                verified = false;
                error = ex.Message;
            }

            // Root IDs improve matching when a render session lives in a child
            // process. Exact session PIDs are kept even if tree inspection fails.
            if (result.Count > 0)
            {
                int[] exactPids = result.ToArray();
                try
                {
                    using ToolhelpProcessTreeReader tree = new();
                    foreach (int pid in exactPids)
                    {
                        int root = AudioProcessRootResolver.ResolveRoot(pid, tree);
                        if (root > 0 && root != ownPid)
                            result.Add(root);
                    }
                }
                catch (Exception ex)
                {
                    // Without process-tree resolution, a microphone capture process
                    // could be a sibling of the render process. Treat the pass as
                    // inconclusive rather than risk sending that application's audio.
                    verified = false;
                    if (string.IsNullOrWhiteSpace(error))
                        error = ex.Message;
                }
            }

            return new MicrophoneUsageSnapshot
            {
                Verified = verified,
                ProcessIds = result,
                ActiveCaptureEndpoints = activeEndpoints,
                Error = error
            };
        }
    }
}
