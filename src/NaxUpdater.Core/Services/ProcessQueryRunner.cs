using System.Diagnostics;
using System.Text;

namespace NaxUpdater.Core.Services;

internal sealed class ProcessQueryRunner
{
    public async Task<ProcessQueryResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Encoding? outputEncoding = null)
    {
        // The input is redirected and closed at once, so a tool that asks a question reads the
        // end of its input instead of waiting. Nax-Updater has no console; a child that inherited
        // its input read from its own hidden console and waited until the time limit. The Store's
        // "store.exe updates" asks "Would you like to install the 2 Store update(s) now? [y/n]
        // (y)" whenever it lists updates, so its query ran out of time and every Store app was
        // reported as not checked. With a closed input it answers "Failed to read input in
        // non-interactive mode" and ends. Nothing is ever written to the input, because the
        // question offers "y" as its default and an empty line would answer with it.
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (outputEncoding is not null)
        {
            startInfo.StandardOutputEncoding = outputEncoding;
            startInfo.StandardErrorEncoding = outputEncoding;
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new ProcessQueryResult(-1, string.Empty, "The process did not start.");
        }
        try { process.StandardInput.Close(); }
        catch (IOException) { } // The process has already ended and closed its side.

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessQueryResult(-1, await SafeResult(outputTask), "The query timed out.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProcessQueryResult(
            process.ExitCode,
            await SafeResult(outputTask),
            await SafeResult(errorTask));
    }

    private static async Task<string> SafeResult(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the state check and the kill request.
        }
    }
}

internal sealed record ProcessQueryResult(int ExitCode, string StandardOutput, string StandardError);
