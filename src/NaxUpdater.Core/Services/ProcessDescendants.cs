using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NaxUpdater.Core.Services;

// Follows the processes that one process starts, directly or through its children. NSIS
// uninstallers copy themselves to the temp folder, start that copy and exit at once, so the
// first process ending says nothing about the uninstall having finished.
internal sealed class ProcessDescendants
{
    private readonly int _rootId;
    // Process ID and start time; the start time tells a descendant from a later process that
    // reuses its ID. It is null when Windows does not let this process read it.
    private readonly Dictionary<int, DateTime?> _tracked = [];

    public ProcessDescendants(Process root)
    {
        _rootId = root.Id;
        _tracked[_rootId] = StartTime(root);
    }

    // Adds every running process whose parent is already tracked.
    public void Refresh()
    {
        var snapshot = Snapshot();
        bool added;
        do
        {
            added = false;
            foreach (var (id, parentId) in snapshot)
            {
                if (_tracked.ContainsKey(id) || !_tracked.TryGetValue(parentId, out var parentStart)) continue;
                var start = StartTime(id);
                // A process that is older than its "parent" only shares a reused process ID.
                if (start is not null && parentStart is not null && start < parentStart) continue;
                _tracked[id] = start;
                added = true;
            }
        } while (added);
    }

    // With "selected", only descendants count whose program path it accepts. The path is null
    // when Windows does not let this process read it.
    public bool AnyDescendantRunning(Func<string?, bool>? selected = null)
    {
        var running = Snapshot().Select(static entry => entry.Id).ToHashSet();
        return _tracked.Any(entry => entry.Key != _rootId && running.Contains(entry.Key) &&
                                     (entry.Value is null || StartTime(entry.Key) is not { } start || start == entry.Value) &&
                                     (selected is null || selected(ImagePath(entry.Key))));
    }

    // Unlike Process.MainModule, this also reads the path of a process that runs elevated.
    internal static string? ImagePath(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var name = new char[1024];
            var length = (uint)name.Length;
            return QueryFullProcessImageName(handle, 0, name, ref length) ? new string(name, 0, (int)length) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static DateTime? StartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return StartTime(process);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null; // The process has ended.
        }
    }

    private static DateTime? StartTime(Process process)
    {
        try { return process.StartTime; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<(int Id, int ParentId)> Snapshot()
    {
        var result = new List<(int, int)>();
        var handle = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (handle == InvalidHandle) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(handle, ref entry)) return result;
            do
            {
                result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
            } while (Process32Next(handle, ref entry));
        }
        finally
        {
            CloseHandle(handle);
        }
        return result;
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
