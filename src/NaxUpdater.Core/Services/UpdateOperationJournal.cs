using NaxUpdater.Core.Models;
using System.Text.Json;

namespace NaxUpdater.Core.Services;

public interface IUpdateOperationJournal
{
    UpdateOperationRecord Begin(UpdateCheckResult update, DateTimeOffset now);
    UpdateOperationRecord Record(UpdateOperationRecord operation, UpdateTransactionStage stage, DateTimeOffset now, string? error = null);
    UpdateOperationRecord? ReadLatest();
    UpdateOperationRecord? ReadIncomplete();
}

public interface IUpdateTransactionLeaseProvider
{
    IDisposable? TryAcquire();
}

public sealed class FileUpdateTransactionLeaseProvider(string path) : IUpdateTransactionLeaseProvider
{
    private readonly string _path = Path.GetFullPath(path);

    public IDisposable? TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The lease path has no directory."));
            return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class FileUpdateOperationJournal(string path) : IUpdateOperationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path = Path.GetFullPath(path);

    public UpdateOperationRecord Begin(UpdateCheckResult update, DateTimeOffset now)
    {
        var plan = update.ExecutionPlan ?? throw new InvalidOperationException("An operation journal requires an execution plan.");
        var operation = new UpdateOperationRecord(
            Guid.NewGuid(),
            update.ApplicationIdentity,
            update.CorrelationKey,
            update.DisplayName,
            update.ProviderId,
            update.InstalledVersion,
            update.AvailableVersion,
            UpdateExecutionIntent.Fingerprint(plan),
            UpdateTransactionStage.Created,
            now,
            now);
        Write(operation);
        return operation;
    }

    public UpdateOperationRecord Record(
        UpdateOperationRecord operation,
        UpdateTransactionStage stage,
        DateTimeOffset now,
        string? error = null)
    {
        var updated = operation with { Stage = stage, UpdatedAt = now, Error = error };
        Write(updated);
        return updated;
    }

    public UpdateOperationRecord? ReadLatest()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }
                return JsonSerializer.Deserialize<UpdateOperationRecord>(File.ReadAllText(_path), JsonOptions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public UpdateOperationRecord? ReadIncomplete()
    {
        var latest = ReadLatest();
        return latest is not null && !IsTerminal(latest.Stage) ? latest : null;
    }

    private void Write(UpdateOperationRecord operation)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The journal path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(operation, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    internal static bool IsTerminal(UpdateTransactionStage stage) => stage is
        UpdateTransactionStage.Succeeded or
        UpdateTransactionStage.NoLongerApplicable or
        UpdateTransactionStage.CanceledBeforeChange or
        UpdateTransactionStage.FailedBeforeChange or
        UpdateTransactionStage.FailedNeedsAttention;
}
