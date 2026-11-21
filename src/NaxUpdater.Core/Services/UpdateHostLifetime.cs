using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NaxUpdater.Core.Services;

public static class UpdateHostLifetime
{
    private const string DesktopFlag = "--nax-desktop-launch";
    private const string EventPrefix = @"Local\NaxUpdater.DesktopLaunch.";
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicLimitInformationClass = 2;
    private const int JobObjectBasicProcessIdListClass = 3;
    private const int ErrorMoreData = 234;

    public static bool IsJobBound()
    {
        using var current = Process.GetCurrentProcess();
        if (!IsProcessInJob(current.Handle, IntPtr.Zero, out var inJob))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!inJob) return false;
        // On Windows 11, explorer.exe itself can reside in a non-terminating desktop job
        // (such as JOB_OBJECT_LIMIT_BREAKAWAY_OK), which Start-menu and desktop launches
        // inherit. Treat only jobs that lack KILL_ON_JOB_CLOSE and directly contain the
        // desktop shell process as independent; any other job (including nested child jobs
        // created under a kill-on-close launcher) stays treated as job-bound.
        return !IsBenignDesktopShellJob();
    }

    private static bool IsBenignDesktopShellJob()
    {
        var querySucceeded = QueryInformationJobObject(
            IntPtr.Zero,
            JobObjectBasicLimitInformationClass,
            out JobObjectBasicLimitInformation limits,
            Marshal.SizeOf<JobObjectBasicLimitInformation>(),
            out _);
        if (!querySucceeded || (limits.LimitFlags & JobObjectLimitKillOnJobClose) != 0)
            return false;
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(shellWindow, out var shellPid);
        if (shellPid == 0) return false;
        return !IsJobBoundForQuery(
            inJob: true,
            querySucceeded: true,
            limitFlags: limits.LimitFlags,
            containsDesktopShell: ImmediateJobContainsProcess(shellPid));
    }

    internal static bool IsJobBoundForQuery(bool inJob, bool querySucceeded, uint limitFlags, bool containsDesktopShell) =>
        inJob && (!querySucceeded || (limitFlags & JobObjectLimitKillOnJobClose) != 0 || !containsDesktopShell);

    private static bool ImmediateJobContainsProcess(uint targetPid)
    {
        var capacity = 256;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new byte[8 + capacity * IntPtr.Size];
            var ok = QueryInformationJobObject(
                IntPtr.Zero,
                JobObjectBasicProcessIdListClass,
                buffer,
                buffer.Length,
                out _);
            var error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error != ErrorMoreData) return false;

            var countInList = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4));
            var maxSlots = (uint)((buffer.Length - 8) / IntPtr.Size);
            var entries = Math.Min(countInList, maxSlots);
            for (var i = 0; i < entries; i++)
            {
                var offset = 8 + i * IntPtr.Size;
                var pid = IntPtr.Size == 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8))
                    : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
                if (pid == targetPid) return true;
            }
            if (ok) return false;
            var assigned = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
            capacity = (int)Math.Min(Math.Max(assigned + 64, (uint)(capacity * 2)), 65536u);
        }
        return false;
    }

    // Return true only after the desktop-launched replacement confirms it is
    // outside the launcher's job. A plain Shell.Application.ShellExecute call
    // can still execute in the caller; use the existing desktop's shell object.
    public static bool TryDetachFromLauncher(string[] arguments, string executable, int show = 1) =>
        TryDetachFromLauncher(arguments, executable, show, LaunchThroughDesktop);

    // The launch step is replaceable for tests: a console test host is handed to Windows
    // Terminal (which runs it in a job) unless it is started through conhost.
    internal static bool TryDetachFromLauncher(string[] arguments, string executable, int show, Action<string, string, int> launch)
    {
        bool inJob;
        try { inJob = IsJobBound(); }
        catch (Win32Exception) { return false; }
        var marker = Array.IndexOf(arguments, DesktopFlag);
        if (marker >= 0)
        {
            if (marker + 1 < arguments.Length && arguments[marker + 1].StartsWith(EventPrefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(arguments[marker + 1][EventPrefix.Length..], "N", out _) && !inJob)
            {
                try
                {
                    using var acknowledged = EventWaitHandle.OpenExisting(arguments[marker + 1]);
                    acknowledged.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }
        if (!inJob) return false;
        var name = EventPrefix + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        try
        {
            launch(executable, $"{DesktopFlag} {name}", show);
            return ready.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (IsDesktopLaunchFailure(exception))
        {
            // Keep the current window usable. The close guard below refuses
            // destructive work from a host whose lifetime is still coupled.
            return false;
        }
    }

    // Failures of the desktop-shell relaunch that keep the current window usable.
    // A shell that denies access (restricted or remote sessions) raises
    // UnauthorizedAccessException instead of a COMException.
    internal static bool IsDesktopLaunchFailure(Exception exception) =>
        exception is COMException or InvalidOperationException or UnauthorizedAccessException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    internal static void LaunchThroughDesktop(string executable, string arguments, int show = 1)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Windows desktop shell is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic windows = shell.Windows();
        var hwnd = 0;
        dynamic desktop = windows.FindWindowSW(0, 0, 8, ref hwnd, 1);
        if (desktop is null || hwnd == 0) throw new InvalidOperationException("Windows desktop shell is unavailable.");
        desktop.Document.Application.ShellExecute(executable, arguments, Path.GetDirectoryName(executable), "open", show);
    }

    internal static void EnsureSafeToClose(IReadOnlyList<Process> targets)
    {
        if (targets.Count == 0) return;
        if (IsJobBound())
            throw new InvalidOperationException("NaxUpdater is still attached to its launching application's process job. Close NaxUpdater and launch it from the Start menu before updating running applications.");
        using var current = Process.GetCurrentProcess();
        var ancestors = new HashSet<int> { current.Id };
        var cursor = current.Id;
        for (var depth = 0; depth < 64; depth++)
        {
            try
            {
                using var process = Process.GetProcessById(cursor);
                if (NtQueryInformationProcess(process.Handle, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) break;
                var parent = basic.ParentProcessId.ToInt32();
                if (parent <= 0 || !ancestors.Add(parent)) break;
                using var parentProcess = Process.GetProcessById(parent);
                if (parentProcess.StartTime > process.StartTime) { ancestors.Remove(parent); break; }
                cursor = parent;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { break; }
        }
        if (targets.Any(process => ancestors.Contains(process.Id)))
            throw new InvalidOperationException("The application to close launched NaxUpdater. Launch NaxUpdater from the Start menu so updating that application cannot terminate the updater.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentProcessId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out JobObjectBasicLimitInformation information, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        [Out] byte[] information, int length, out int returned);
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        out ProcessBasicInformation information, int size, out int returned);
}
