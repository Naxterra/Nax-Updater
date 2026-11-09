using NaxUpdater.Core.Services;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// This fixture deliberately has no console, just like the installed WinUI app.
// Its child simulates the Store CLI's raw UTF-8 protocol without network access.
if (args is ["updates"])
{
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(Encoding.UTF8.GetBytes("Checking for updates…\r\n\r\nNo updates found.\r\n"));
    stdout.Flush();
    return;
}
if (args is not ["--verify", var report]) { Environment.ExitCode = 2; return; }
var noConsole = Native.GetConsoleWindow() == IntPtr.Zero;
var batch = new StoreUpdateBatchQuery(() => Environment.ProcessPath!);
var result = await batch.CheckAsync(CancellationToken.None);
var passed = noConsole && result == StoreBatchCheck.NoUpdates;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
File.WriteAllText(report, JsonSerializer.Serialize(new
{
    Scenario = "windowed_parent_decodes_store_utf8",
    Passed = passed,
    NoConsole = noConsole,
    HostCodePage = Console.OutputEncoding.CodePage,
    Result = result.ToString()
}));
Environment.ExitCode = passed ? 0 : 1;

internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();
}
