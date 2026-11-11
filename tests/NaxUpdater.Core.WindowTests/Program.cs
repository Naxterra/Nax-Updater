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

// windowed_parent_query_that_asks_a_question_does_not_wait_for_input: "store.exe updates" asks
// "Would you like to install ... [y/n] (y)" whenever it lists updates. A console program started
// from this console-less parent read its hidden console and waited until the time limit, so the
// Store query timed out on every day with a listed update. cmd.exe asks the same way here.
var timer = System.Diagnostics.Stopwatch.StartNew();
var question = await new ProcessQueryRunner().RunAsync("cmd.exe",
    ["/d", "/c", "set /p answer=Would you like to install the 2 Store update(s) now? [y/n] (y): & echo not-answered"],
    TimeSpan.FromSeconds(15), CancellationToken.None);
var questionSeconds = timer.Elapsed.TotalSeconds;
var questionPassed = noConsole && question.ExitCode != -1 && questionSeconds < 5 && question.StandardOutput.Contains("not-answered");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
File.WriteAllText(report, JsonSerializer.Serialize(new
{
    Scenario = "windowed_parent_decodes_store_utf8",
    Passed = passed,
    NoConsole = noConsole,
    HostCodePage = Console.OutputEncoding.CodePage,
    Result = result.ToString(),
    QuestionScenario = "windowed_parent_query_that_asks_a_question_does_not_wait_for_input",
    QuestionPassed = questionPassed,
    QuestionSeconds = Math.Round(questionSeconds, 1),
    QuestionExitCode = question.ExitCode
}));
Environment.ExitCode = passed && questionPassed ? 0 : 1;

internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();
}
