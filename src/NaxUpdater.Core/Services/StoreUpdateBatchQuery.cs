namespace NaxUpdater.Core.Services;

internal enum StoreBatchCheck { Unavailable, NoUpdates, CheckIndividually }

// One fresh, read-only Store-owned query replaces the repeated per-product/SKU
// searches on a cold scan. An unrecognised response is never evidence of currency.
internal sealed class StoreUpdateBatchQuery
{
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(12);
    private readonly Func<string?> _resolve;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessQueryResult>> _run;
    private readonly TimeSpan _timeout;
    private readonly Lock _gate = new();
    private Task<StoreBatchCheck>? _check;

    public StoreUpdateBatchQuery()
        : this(() => StorePackagedExecutable.Resolve("Microsoft.WindowsStore_8wekyb3d8bbwe", "store.exe")) { }

    internal StoreUpdateBatchQuery(Func<string?> resolve)
        : this(resolve, (path, args, timeout, token) => new ProcessQueryRunner().RunAsync(path, args, timeout, token,
            // WinExe hosts otherwise inherit the ANSI code page, corrupting the
            // Store CLI's UTF-8 protocol even though console tests decode it well.
            outputEncoding: new System.Text.UTF8Encoding(false, true))) { }

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
        Task<StoreBatchCheck> check;
        lock (_gate) check = _check ??= QueryAsync();
        // An application timing out must not cancel the shared query for siblings.
        // The read-only helper has its own bounded lifetime.
        return check.WaitAsync(token);
    }

    internal StoreUpdateBatchQuery ForNewScan() => new(_resolve, _run, _timeout);

    private async Task<StoreBatchCheck> QueryAsync()
    {
        Task<ProcessQueryResult>? query = null;
        try
        {
            var path = _resolve();
            if (path is null) return StoreBatchCheck.Unavailable;
            using var deadline = new CancellationTokenSource(_timeout);
            // No --apply, filters, shell, PATH lookup or deployment command.
            query = _run(path, ["updates"], _timeout, deadline.Token);
            var result = await query.WaitAsync(deadline.Token);
            return Parse(result);
        }
        catch (Exception)
        {
            if (query is not null)
                _ = query.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return StoreBatchCheck.CheckIndividually;
        }
    }

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
