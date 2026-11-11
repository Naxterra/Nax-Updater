namespace NaxUpdater.Core.Services;

internal enum StoreBatchCheck { Unavailable, NoUpdates, CheckIndividually, TimedOut }

// One fresh, read-only Store-owned query replaces the repeated per-product/SKU
// searches on a cold scan. An unrecognised response is never evidence of currency.
internal sealed class StoreUpdateBatchQuery
{
    // On the owner's PC the query answered in about 5 s on one day and in 15 to 18 s on another.
    // With a limit of 12 s the slow day fell back to one native query per app, which the Store
    // answers one at a time: 14 apps took 83 s. The limit stays below the 30 s that one source
    // check may take, and every check starts the query before its per-app Store lookups, so
    // those run beside it and not before it.
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(25);
    private readonly Func<string?> _resolve;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessQueryResult>> _run;
    private readonly TimeSpan _timeout;
    private readonly Lock _gate = new();
    private Task<StoreBatchCheck>? _check;

    public StoreUpdateBatchQuery()
        : this(() => StorePackagedExecutable.Resolve("Microsoft.WindowsStore_8wekyb3d8bbwe", "store.exe")) { }

    internal StoreUpdateBatchQuery(Func<string?> resolve)
        : this(resolve, RunStoreCli) { }

    // WinExe hosts otherwise inherit the ANSI code page, corrupting the Store CLI's UTF-8 protocol
    // even though console tests decode it well.
    //
    // The input is closed. Whenever "store.exe updates" lists updates, it ends with "Would you
    // like to install the 2 Store update(s) now? [y/n] (y)". Started from the console-less app
    // with an inherited input, it waited for that answer until the time limit, so the query ran
    // out of time and every Store app without a recent answer was reported as not checked. With a
    // closed input it answers "Failed to read input in non-interactive mode", ends and installs
    // nothing; that was checked on the owner's PC. Nothing is ever written to the input, because
    // the question offers "y" as its default and an empty line would answer with it.
    internal static Task<ProcessQueryResult> RunStoreCli(string path, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token) =>
        new ProcessQueryRunner().RunAsync(path, arguments, timeout, token,
            outputEncoding: new System.Text.UTF8Encoding(false, true), closeInput: true);

    internal StoreUpdateBatchQuery(Func<string?> resolve,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessQueryResult>> run,
        TimeSpan? timeout = null)
    {
        _resolve = resolve;
        _run = run;
        _timeout = timeout ?? QueryTimeout;
    }

    public Task<StoreBatchCheck> CheckAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // An application timing out must not cancel the shared query for siblings.
        // The read-only helper has its own bounded lifetime.
        return Start(CancellationToken.None).WaitAsync(token);
    }

    // Starts the query once, before the per-app checks need it. Only the scan's own token ends it
    // early; the token of one application's check never does. The scan starts it from the window's
    // thread, so finding the Store's program and starting it run on a pool thread.
    internal Task<StoreBatchCheck> Start(CancellationToken scanToken)
    {
        lock (_gate) return _check ??= Task.Run(() => QueryAsync(scanToken));
    }

    internal StoreUpdateBatchQuery ForNewScan() => new(_resolve, _run, _timeout);

    private async Task<StoreBatchCheck> QueryAsync(CancellationToken scanToken)
    {
        Task<ProcessQueryResult>? query = null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var path = _resolve();
            if (path is null) return StoreBatchCheck.Unavailable;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(scanToken);
            deadline.CancelAfter(_timeout);
            // No --apply, filters, shell, PATH lookup or deployment command.
            query = _run(path, ["updates"], _timeout, deadline.Token);
            var result = await query.WaitAsync(deadline.Token);
            return Classify(result, System.Diagnostics.Stopwatch.GetElapsedTime(started) >= _timeout * 0.9);
        }
        catch (Exception exception)
        {
            if (query is not null)
                _ = query.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // The deadline ended the query. A canceled scan has no reader for the outcome.
            return exception is OperationCanceledException or TimeoutException && !scanToken.IsCancellationRequested
                ? StoreBatchCheck.TimedOut
                : StoreBatchCheck.CheckIndividually;
        }
    }

    // A query that ran out of time is its own outcome: the per-app queries go through the same
    // Store service and would take longer still, so they are not started for it. Only the process
    // runner's own end at the limit (exit code -1) counts. Whatever the Store itself answered,
    // however late, is an answer: an update table or unknown text still needs the per-app checks
    // and must never be covered by an earlier "no update".
    internal static StoreBatchCheck Classify(ProcessQueryResult result, bool ranOutOfTime) =>
        result.ExitCode == -1 && ranOutOfTime ? StoreBatchCheck.TimedOut : Parse(result);

    internal static StoreBatchCheck Parse(ProcessQueryResult result)
    {
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError) ||
            result.StandardOutput.Length > 65536) return StoreBatchCheck.CheckIndividually;
        var lines = result.StandardOutput.TrimStart('\uFEFF')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Fail closed on localisation, changed output, warning/error lines,
        // partial results or an update table. Those require native per-app checks.
        return lines.Length == 2 && lines[0] is "Checking for updates…" or "Checking for updates..." &&
               lines[1] == "No updates found."
            ? StoreBatchCheck.NoUpdates : StoreBatchCheck.CheckIndividually;
    }
}
