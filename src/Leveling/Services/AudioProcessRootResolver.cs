using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Multiverse.Services
{
    internal readonly record struct ProcessNode(
        int ProcessId,
        int ParentProcessId,
        string ProcessName);

    internal interface IProcessTreeReader
    {
        bool TryRead(int pid, out ProcessNode node);
    }

    internal static class AudioProcessRootResolver
    {
        internal static int ResolveRoot(int pid, IProcessTreeReader reader)
        {
            if (pid <= 0 || !reader.TryRead(pid, out ProcessNode current))
                return pid;

            string family = current.ProcessName;
            int root = current.ProcessId;
            var visited = new HashSet<int> { current.ProcessId };

            while (current.ParentProcessId > 0 &&
                   !visited.Contains(current.ParentProcessId) &&
                   reader.TryRead(current.ParentProcessId, out ProcessNode parent))
            {
                if (!string.Equals(
                    parent.ProcessName,
                    family,
                    StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                root = parent.ProcessId;
                current = parent;
                visited.Add(current.ProcessId);
            }

            return root;
        }
    }

    internal sealed class ToolhelpProcessTreeReader : IProcessTreeReader, IDisposable
    {
        private const uint Th32csSnapProcess = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new(-1);
        private readonly Dictionary<int, ProcessNode> _nodes = new();

        internal ToolhelpProcessTreeReader()
        {
            LoadSnapshot();
        }

        public bool TryRead(int pid, out ProcessNode node) =>
            _nodes.TryGetValue(pid, out node);

        private void LoadSnapshot()
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == InvalidHandleValue || snapshot == IntPtr.Zero)
                return;

            try
            {
                PROCESSENTRY32 entry = new()
                {
                    dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(),
                    szExeFile = ""
                };

                if (!Process32First(snapshot, ref entry))
                    return;

                do
                {
                    int pid = unchecked((int)entry.th32ProcessID);
                    int parent = unchecked((int)entry.th32ParentProcessID);
                    if (pid > 0)
                    {
                        _nodes[pid] = new ProcessNode(
                            pid,
                            parent,
                            AudioApplicationPolicy.NormalizeExecutable(entry.szExeFile));
                    }
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        public void Dispose() { }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
