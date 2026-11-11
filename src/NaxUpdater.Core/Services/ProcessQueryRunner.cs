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
        Encoding? outputEncoding = null,
        bool closeInput = false)
    {
        // Nax-Updater has no console. A child that inherits its input reads from its own hidden
        // console, so a question it asks waits until the time limit. With "closeInput" the input
        // is redirected and closed at once, and the tool reads the end of its input instead. A
        // tool can take the default answer of its question on that, so only a caller that has
        // checked how its tool behaves sets it.
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = closeInput,
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
        if (closeInput)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { } // The process has already ended and closed its side.
        }

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
