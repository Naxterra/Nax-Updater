using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NaxUpdater.Core.Services;

public static class UpdateHostLifetime
{
    private const string DesktopFlag = "--nax-desktop-launch";
    private const string EventPrefix = @"Local\NaxUpdater.DesktopLaunch.";

    public static bool IsJobBound()
    {
        using var current = Process.GetCurrentProcess();
        if (!IsProcessInJob(current.Handle, IntPtr.Zero, out var inJob))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return inJob;
    }

    // Return true only after the desktop-launched replacement confirms it is
    // outside the launcher's job. A plain Shell.Application.ShellExecute call
    // can still execute in the caller; use the existing desktop's shell object.
    public static bool TryDetachFromLauncher(string[] arguments, string executable, int show = 1)
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
            LaunchThroughDesktop(executable, $"{DesktopFlag} {name}", show);
            return ready.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Keep the current window usable. The close guard below refuses
            // destructive work from a host whose lifetime is still coupled.
            return false;
        }
    }

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
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        out ProcessBasicInformation information, int size, out int returned);
}
