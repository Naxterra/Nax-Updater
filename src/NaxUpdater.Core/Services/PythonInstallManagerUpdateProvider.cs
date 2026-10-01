using System.Text.Json;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

// Runtimes installed by the Python install manager (`py install`, uninstall key
// "pymanager-<install id>") are updated only by that manager, which verifies
// python.org's signed index and package hashes itself. Name-matched catalogs
// (Chocolatey's "python") update the separate python.org installer instead.
internal sealed class PythonInstallManagerUpdateProvider(ProcessQueryRunner processRunner) : IUpdateProvider
{
    internal const string PackageFamily = "PythonSoftwareFoundation.PythonManager_qbz5n2kfra8p0";
    internal const string UninstallKeyPrefix = "pymanager-";
    private const string ReleaseNotes = "https://docs.python.org/3/using/windows.html";
    private readonly SemaphoreSlim _listGate = new(1, 1);
    private Dictionary<string, JsonElement>? _installed;

    public string Id => "python-install-manager";
    public UpdateProviderDescriptor Descriptor { get; } = new(
        UpdateProviderAuthority.InstalledUpdateProtocol,
        100,
        "Python install manager runtime identified by its own uninstall entry",
        [ManagementMode.Registry]);

    public bool CanHandle(InstalledApplication application) => InstallId(application) is not null;

    internal static string? InstallId(InstalledApplication application)
    {
        foreach (var evidence in application.Evidence.Where(static e => e.Label == "Uninstall registry"))
        {
            var separator = evidence.Value.LastIndexOf(" · ", StringComparison.Ordinal);
            var key = separator >= 0 ? evidence.Value[(separator + 3)..].Trim() : evidence.Value.Trim();
            if (key.StartsWith(UninstallKeyPrefix, StringComparison.OrdinalIgnoreCase) && key.Length > UninstallKeyPrefix.Length)
                return key[UninstallKeyPrefix.Length..];
        }
        return null;
    }

    public async Task<UpdateCheckResult> CheckAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var id = InstallId(application)!;
        var manager = StorePackagedExecutable.Resolve(PackageFamily, "pymanager.exe");
        if (manager is null)
            return Error(application, "The Store-signed Python install manager that owns this runtime is not installed.");

        var installed = await InstalledAsync(manager, cancellationToken);
        if (!installed.TryGetValue(id, out var runtime) || IsUnmanaged(runtime))
            return Error(application, $"The Python install manager does not list the managed runtime '{id}'.");
        var installedVersion = runtime.GetProperty("sort-version").GetString();
        var tag = runtime.GetProperty("tag").GetString();
        if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(tag))
            return Error(application, "The Python install manager did not report this runtime's version.");

        var online = await processRunner.RunAsync(manager, ["list", "--online", "-f", "json", tag], TimeSpan.FromSeconds(45), cancellationToken);
        var latest = online.ExitCode == 0 ? Versions(online.StandardOutput)
            .Where(e => e.GetProperty("id").GetString() == id)
            .Select(e => e.GetProperty("sort-version").GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .OrderByDescending(v => v, Comparer<string?>.Create(VersionOrder.Compare)).FirstOrDefault() : null;
        if (latest is null)
            return Error(application, "python.org's index, as read by the Python install manager, has no entry for this runtime.");

        if (VersionOrder.Compare(latest, installedVersion) <= 0)
            return Result(application, null, UpdateStatus.Current,
                $"The Python install manager reports {installedVersion} as the newest {tag} runtime.", null);

        // Only the runtime's own interpreters are closed; other Pythons keep running.
        var directory = Path.GetDirectoryName(runtime.TryGetProperty("executable", out var exe) ? exe.GetString() : null);
        var interpreters = directory is null ? [] : new[] { "python.exe", "pythonw.exe" }
            .Select(name => Path.Combine(directory, name)).Where(File.Exists).ToArray();
        // Address the runtime as Company\Tag, not --by-id: pymanager 26.3 keeps --by-id
        // arguments as plain strings and then crashes in its final PEP 514 registry
        // step (AttributeError: 'str' object has no attribute 'satisfied_by'), after
        // replacing the files but before updating the runtime's registration.
        var company = runtime.TryGetProperty("company", out var companyValue) ? companyValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(company))
            return Error(application, "The Python install manager did not report this runtime's company.");
        var plan = new UpdateExecutionPlan(UpdateExecutionKind.NativeCommand, null, null, null, null, manager,
            ["install", "--update", "-y", $"{company}\\{tag}"], false, [],
            interpreters.Select(path => Path.GetFileNameWithoutExtension(path)).ToArray(),
            RequireAuthenticode: false, StorePackageFamilyName: PackageFamily, RunningExecutablePaths: interpreters);
        return Result(application, latest, UpdateStatus.Available,
            "The Store-signed Python install manager updates this runtime from python.org's signed index and verifies the package hash.", plan);
    }

    private async Task<Dictionary<string, JsonElement>> InstalledAsync(string manager, CancellationToken token)
    {
        await _listGate.WaitAsync(token);
        try
        {
            if (_installed is not null) return _installed;
            var list = await processRunner.RunAsync(manager, ["list", "-f", "json"], TimeSpan.FromSeconds(30), token);
            var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (list.ExitCode == 0)
                foreach (var entry in Versions(list.StandardOutput))
                    if (entry.TryGetProperty("id", out var id) && id.GetString() is { } value) map[value] = entry;
            return _installed = map;
        }
        finally { _listGate.Release(); }
    }

    private static IEnumerable<JsonElement> Versions(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            return root.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array
                ? versions.EnumerateArray().Where(static e => e.TryGetProperty("id", out _) && e.TryGetProperty("sort-version", out _)).ToArray()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static bool IsUnmanaged(JsonElement runtime) =>
        runtime.TryGetProperty("unmanaged", out var flag) && flag.ValueKind == JsonValueKind.Number && flag.GetInt32() != 0;

    private UpdateCheckResult Result(InstalledApplication application, string? available, UpdateStatus status,
        string message, UpdateExecutionPlan? plan) => new(
        application.Identity, application.DisplayName, application.NormalizedVersion, available, status, Id, "Python install manager",
        "neutral", "python.org runtime", "provider-selected", "stable", ReleaseNotes, message, plan);

    private UpdateCheckResult Error(InstalledApplication application, string message) =>
        Result(application, null, UpdateStatus.Error, message, null);
}
