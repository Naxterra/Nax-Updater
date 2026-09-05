using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Diagnostics;
using System.Text.Json;

internal static class ProcessLifetimeRegression
{
    public static async Task RunAsync(Action<bool, string> assert, string fixture)
    {
        if (UpdateHostLifetime.IsJobBound())
        {
            using var current = Process.GetCurrentProcess();
            try { UpdateHostLifetime.EnsureSafeToClose([current]); assert(false, "Job-bound close was not blocked."); }
            catch (InvalidOperationException exception) { assert(exception.Message.Contains("process job"), "Wrong close guard failure."); }
            assert(UpdateHostLifetime.TryDetachFromLauncher([], Environment.ProcessPath!, 0),
                "Desktop replacement did not acknowledge an independent process lifetime.");
        }
        var report = Path.Combine(fixture, "process-lifetime-result.json");
        UpdateHostLifetime.LaunchThroughDesktop(Environment.ProcessPath!, $"--close-fixture-worker \"{report}\"", 0);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!File.Exists(report) && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
        assert(File.Exists(report), "Process lifetime worker did not finish.");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var root = result.RootElement;
        assert(!root.TryGetProperty("Error", out var error), "Process lifetime worker failed: " + error);
        assert(root.GetProperty("Independent").GetBoolean(), "Desktop-launched worker still inherited the caller's job.");
        assert(root.GetProperty("SelfProtected").GetBoolean(), "Updater self/ancestor close protection failed.");
        assert(root.GetProperty("AncestorProtected").GetBoolean(), "Updater ancestor close protection failed.");
        assert(root.GetProperty("TargetClosed").GetBoolean(), "Approved disposable target was not force-closed.");
        assert(root.GetProperty("UnrelatedChildPreserved").GetBoolean(), "Force-close terminated an unrelated descendant.");
    }

    public static async Task RunWorkerAsync(string report)
    {
        Process? target = null, child = null;
        object result;
        try
        {
            var directory = Path.GetDirectoryName(report)!;
            var suffix = Guid.NewGuid().ToString("N")[..10];
            var targetName = "nax-close-target-" + suffix;
            var childName = "nax-close-child-" + suffix;
            var targetPath = Path.Combine(directory, targetName + ".exe");
            var childPath = Path.Combine(directory, childName + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), targetPath);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), childPath);
            var start = new ProcessStartInfo(targetPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Arguments = $"/d /s /c \"\"{childPath}\" /d /c pause\"";
            target = Process.Start(start)!;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (child is null && DateTimeOffset.UtcNow < deadline)
            {
                child = Process.GetProcessesByName(childName).SingleOrDefault();
                if (child is null) await Task.Delay(100);
            }
            if (child is null) throw new InvalidOperationException("Disposable descendant was not created.");
            using var self = Process.GetCurrentProcess();
            var selfProtected = false;
            try { UpdateHostLifetime.EnsureSafeToClose([self]); }
            catch (InvalidOperationException) { selfProtected = true; }
            using var processRecord = new System.Management.ManagementObject($"Win32_Process.Handle='{self.Id}'");
            processRecord.Get();
            using var parent = Process.GetProcessById(Convert.ToInt32(processRecord["ParentProcessId"]));
            var ancestorProtected = false;
            try { UpdateHostLifetime.EnsureSafeToClose([parent]); }
            catch (InvalidOperationException) { ancestorProtected = true; }
            var plan = new UpdateExecutionPlan(UpdateExecutionKind.NativeCommand, null, null, null, null, null,
                [], false, [], [targetName], RunningExecutablePaths: [targetPath]);
            var update = new UpdateCheckResult("fixture:close", "Disposable close fixture", "1.0", "2.0",
                UpdateStatus.Available, "fixture", "fixture", "neutral", "fixture", "x64", "stable", null, null, plan);
            var closed = await new UpdateExecutionService().CloseForUpdateAsync(update, TimeSpan.Zero, TimeSpan.FromSeconds(3));
            result = new
            {
                Independent = !UpdateHostLifetime.IsJobBound(),
                SelfProtected = selfProtected,
                AncestorProtected = ancestorProtected,
                TargetClosed = closed.AllClosed && closed.ForcedTerminationUsed && target.HasExited,
                UnrelatedChildPreserved = !child.HasExited
            };
        }
        catch (Exception exception) { result = new { Error = exception.ToString() }; }
        finally
        {
            if (child is not null) { if (!child.HasExited) child.Kill(); child.WaitForExit(3000); child.Dispose(); }
            if (target is not null) { if (!target.HasExited) target.Kill(); target.WaitForExit(3000); target.Dispose(); }
        }
        var temporary = report + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result));
        File.Move(temporary, report);
    }
}
