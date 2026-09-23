using Microsoft.Management.Deployment;
using NaxUpdater.Core.Models;
using System.Security.Principal;
using System.Text.Json;
using Windows.System;

namespace NaxUpdater.Core.Services;

public sealed record WingetPackageOffer(WingetUpdateTarget? Target, string? Error);

public interface IWingetPackageService
{
    Task<WingetPackageOffer> AssessAsync(InstalledApplication application, string packageId, string version, CancellationToken token);
    Task<PreparedCatalogUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token);
}

public sealed class PreparedCatalogUpdate
{
    private readonly Func<IProgress<double>?, CancellationToken, Task<UpdateExecutionResult>> _apply;
    public PreparedCatalogUpdate(WingetUpdateTarget target, Func<CancellationToken, Task<UpdateExecutionResult>> apply)
        : this(target, (_, token) => apply(token)) { }
    public PreparedCatalogUpdate(WingetUpdateTarget target, Func<IProgress<double>?, CancellationToken, Task<UpdateExecutionResult>> apply)
        { Target = target; _apply = apply; }
    public WingetUpdateTarget Target { get; }
    public Task<UpdateExecutionResult> ApplyAsync(CancellationToken token, IProgress<double>? progress = null) => _apply(progress, token);
}

// The package manager owns manifest authentication, download hashes, dependencies,
// installer switches and elevation. Never substitute a source alias or scraped YAML.
public sealed partial class WingetPackageService : IWingetPackageService
{
    public const string OfficialSourceId = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private (PackageManager Manager, PackageCatalog Catalog)? _connection;

    public async Task<WingetPackageOffer> AssessAsync(
        InstalledApplication application, string packageId, string version, CancellationToken token)
    {
        try
        {
            var (_, catalog) = await ConnectAsync(token);
            var registeredIds = RegisteredIds(application).ToArray();
            var package = await FindAsync(catalog, packageId, registeredIds, token);
            if (package?.InstalledVersion is null)
                return new(null, "WinGet could not correlate this package with an installed application.");
            var installedIds = Copy(package.InstalledVersion.ProductCodes);
            if (!registeredIds.Intersect(installedIds, StringComparer.OrdinalIgnoreCase).Any())
                return new(null, "The installed product code does not match the package selected by WinGet.");
            // WinGet's own bookkeeping can fail to resolve an installed version at all
            // (surfaced by its CLI as "Unknown") while still reporting IsUpdateAvailable.
            // That combination cannot be trusted to mean an update is genuinely needed:
            // it has been observed repeatedly re-offering an already-current package.
            // NaxUpdater already knows the real installed version independently
            // (application.NormalizedVersion, from its own registry/executable scan);
            // require WinGet's own tracked version to actually be resolvable before
            // deferring to its upgrade-availability signal.
            var trackedVersion = package.InstalledVersion.Version;
            if (string.IsNullOrWhiteSpace(trackedVersion) || trackedVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return new(null, "WinGet does not track a resolvable installed version for this package, so its update-availability signal cannot be trusted.");
            if (!package.IsUpdateAvailable)
                return new(null, "WinGet reports a newer release but no applicable upgrade for this installation.");
            var key = VersionKey(package, version);
            if (key is null) return new(null, "The requested version is absent from the official WinGet source.");
            var info = package.GetPackageVersionInfo(key);
            if (!info.PackageCatalog.Info.Id.Equals(OfficialSourceId, StringComparison.OrdinalIgnoreCase))
                return new(null, "The package version did not come from the official WinGet source.");
            var scope = application.Scope;
            var location = package.InstalledVersion.GetMetadata(PackageVersionMetadataField.InstalledLocation);
            var options = CreateOptions(key, scope, string.IsNullOrWhiteSpace(location) ? null : location);
            var architecture = InstalledApplicationMetadata.Architecture(application);
            if (architecture is not null) SetArchitecture(options, architecture);
            var variant = info.GetApplicableInstaller(options);
            if (variant is null) return new(null, "No compatible installer was returned by WinGet.");
            return new(new(
                packageId, OfficialSourceId, version, package.InstalledVersion.Version,
                installedIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                variant.Architecture.ToString(), variant.InstallerType.ToString(),
                variant.Locale ?? string.Empty, scope, string.IsNullOrWhiteSpace(location) ? null : location), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(null, exception.Message); }
    }

    public async Task<PreparedCatalogUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token)
    {
        var target = update.ExecutionPlan?.WingetTarget ?? throw new InvalidOperationException("The WinGet target is missing.");
        if (target.SourceId != OfficialSourceId || target.Version != update.AvailableVersion)
            throw new InvalidOperationException("The approved WinGet identity or version does not match.");
        var (manager, catalog) = await ConnectAsync(token, reopen: true);
        var package = await FindAsync(catalog, target.PackageId, target.InstalledProductCodes, token)
            ?? throw new InvalidOperationException("The approved WinGet package is no longer present.");
        var key = VersionKey(package, target.Version)
            ?? throw new InvalidOperationException("The approved WinGet version is no longer present.");
        var options = CreateOptions(key, target.Scope, target.InstallLocation);
        SetArchitecture(options, target.Architecture);
        options.InstallerType = Enum.Parse<PackageInstallerType>(target.InstallerType);
        ValidatePrepared(package, key, options, target);
        // Keep this catalog package, version key and options alive through Apply.
        return new PreparedCatalogUpdate(target, async (progress, cancellationToken) =>
        {
            ValidatePrepared(package, key, options, target);
            // The COM API installs with NaxUpdater's own (asInvoker) token and never
            // raises a UAC prompt, so a machine-scope installer just fails
            // (APPINSTALLER_CLI_ERROR_MSI_INSTALL_FAILED). Run the same pinned
            // package through an elevated winget.exe process instead.
            if (target.Scope == InstallScope.Machine && !IsElevated())
                return await RunElevatedCliUpgradeAsync(target, cancellationToken);
            var operation = manager.UpgradePackageAsync(package, options);
            try
            {
                // Once submitted, Windows owns installation and possible UAC. Await its
                // actual result even if the caller stops waiting; never kill an installer.
                var result = await operation.AsTask(new Progress<InstallProgress>(state =>
                    progress?.Report(state.State == PackageInstallProgressState.Downloading
                        ? state.DownloadProgress * 0.5 : 0.5 + state.InstallationProgress * 0.5)));
                var success = result.Status == InstallResultStatus.Ok;
                var code = result.RebootRequired ? 3010 : unchecked((int)result.InstallerErrorCode);
                if (!success && code == 0) code = result.ExtendedErrorCode?.HResult ?? -1;
                if (result.ExtendedErrorCode?.HResult == unchecked((int)0x800704C7)) code = 1223;
                // ExtendedErrorCode is frequently null even on failure; fall back to the
                // installer/HRESULT code already resolved above so the message is never
                // just "WinGet returned <Status>: " with no diagnostic content.
                var detail = result.ExtendedErrorCode?.Message;
                if (string.IsNullOrWhiteSpace(detail))
                    detail = $"code 0x{unchecked((uint)code):X8}";
                return new UpdateExecutionResult(code, success,
                    success ? null : $"WinGet returned {result.Status}: {detail}");
            }
            catch (Exception exception)
            {
                return new UpdateExecutionResult(
                    exception.HResult == unchecked((int)0x800704C7) ? 1223 : -1, false, exception.Message);
            }
        });
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<UpdateExecutionResult> RunElevatedCliUpgradeAsync(WingetUpdateTarget target, CancellationToken token)
    {
        // Every value below ends up on an elevated cmd.exe command line.
        if (!SafeToken().IsMatch(target.PackageId) || !SafeToken().IsMatch(target.Version) ||
            !SafeToken().IsMatch(target.Architecture) || !SafeToken().IsMatch(target.InstallerType) ||
            target.Locale.Length > 0 && !SafeToken().IsMatch(target.Locale) ||
            target.InstallLocation is { } location && location.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
            return new UpdateExecutionResult(-1, false, "The approved WinGet target contains characters that cannot be passed to an elevated installer safely.");
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        var winget = File.Exists(alias) ? $"\"{alias}\"" : "winget.exe";
        var arguments = new List<string>
        {
            "upgrade", "--id", target.PackageId, "--exact", "--version", target.Version, "--source", "winget",
            "--scope", "machine", "--architecture", target.Architecture.ToLowerInvariant(),
            "--installer-type", target.InstallerType.ToLowerInvariant(),
            "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        };
        if (target.Locale.Length > 0) arguments.AddRange(["--locale", target.Locale]);
        if (target.InstallLocation is not null) arguments.AddRange(["--location", $"\"{target.InstallLocation}\""]);
        var logPath = Path.Combine(Path.GetTempPath(), $"naxupdater-winget-{Guid.NewGuid():N}.log");
        try
        {
            // runas requires ShellExecute, which cannot redirect output; let cmd.exe
            // write winget's output to a log so failures stay diagnosable.
            var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/d /c \"{winget} {string.Join(' ', arguments)} > \"{logPath}\" 2>&1\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("winget.exe could not be started.");
            // Windows owns the elevated deployment once started; never abandon or kill it.
            await process.WaitForExitAsync(CancellationToken.None);
            var code = process.ExitCode;
            if (code == 0) return new UpdateExecutionResult(0, true, null);
            if (code == unchecked((int)0x8A150109)) return new UpdateExecutionResult(3010, true, null); // INSTALL_REBOOT_REQUIRED_TO_FINISH
            if (code == unchecked((int)0x8A15010C)) return new UpdateExecutionResult(1602, false, "The installer was cancelled.");
            var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, token) : "";
            var lastLine = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(static line => line.Any(char.IsLetter)) ?? "no output was captured";
            return new UpdateExecutionResult(code, false, $"WinGet returned 0x{unchecked((uint)code):X8}: {lastLine}");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new UpdateExecutionResult(1223, false, "The Windows elevation prompt was cancelled.");
        }
        finally
        {
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")]
    private static partial System.Text.RegularExpressions.Regex SafeToken();

    private static void ValidatePrepared(CatalogPackage package, PackageVersionId key, InstallOptions options, WingetUpdateTarget target)
    {
        var installed = package.InstalledVersion;
        if (installed is null || installed.Version != target.InstalledCatalogVersion ||
            !Copy(installed.ProductCodes).Intersect(target.InstalledProductCodes, StringComparer.OrdinalIgnoreCase).Any() ||
            !package.IsUpdateAvailable)
            throw new InvalidOperationException("The installed WinGet package changed after approval.");
        var info = package.GetPackageVersionInfo(key);
        if (info.PackageCatalog.Info.Id != target.SourceId || info.Version != target.Version)
            throw new InvalidOperationException("The WinGet source or version changed after approval.");
        var variant = info.GetApplicableInstaller(options);
        if (variant is null || variant.Architecture.ToString() != target.Architecture ||
            variant.InstallerType.ToString() != target.InstallerType || (variant.Locale ?? "") != target.Locale)
            throw new InvalidOperationException("The approved WinGet installer variant is no longer applicable.");
    }

    internal static InstallOptions CreateOptions(PackageVersionId? key, InstallScope scope, string? location)
    {
        var options = new InstallOptions
        {
            PackageInstallMode = PackageInstallMode.Silent,
            PackageInstallScope = scope switch
            {
                InstallScope.CurrentUser => PackageInstallScope.UserOrUnknown,
                InstallScope.Machine => PackageInstallScope.SystemOrUnknown,
                _ => PackageInstallScope.Any
            },
            AllowHashMismatch = false,
            AllowUpgradeToUnknownVersion = false,
            Force = false,
            AcceptPackageAgreements = true,
            CorrelationData = JsonSerializer.Serialize(new { caller = "NaxUpdater" })
        };
        if (key is not null) options.PackageVersionId = key;
        if (!string.IsNullOrWhiteSpace(location)) options.PreferredInstallLocation = location;
        return options;
    }

    private static void SetArchitecture(InstallOptions options, string architecture)
    {
        options.AllowedArchitectures.Clear();
        options.AllowedArchitectures.Add(architecture.ToLowerInvariant() switch
        {
            "x64" => ProcessorArchitecture.X64,
            "x86" => ProcessorArchitecture.X86,
            "arm64" => ProcessorArchitecture.Arm64,
            "arm" => ProcessorArchitecture.Arm,
            "neutral" => ProcessorArchitecture.Neutral,
            _ => throw new InvalidOperationException("Unknown installer architecture.")
        });
    }

    private async Task<(PackageManager Manager, PackageCatalog Catalog)> ConnectAsync(CancellationToken token, bool reopen = false)
    {
        await _connectionGate.WaitAsync(token);
        try
        {
            if (!reopen && _connection is not null) return _connection.Value;
            var manager = new PackageManager();
            var source = manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog);
            if (source.Info.Id != OfficialSourceId || source.Info.Type != "Microsoft.PreIndexed.Package" ||
                !Uri.TryCreate(source.Info.Argument, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidOperationException("The predefined WinGet source identity is invalid.");
            source.AcceptSourceAgreements = true;
            var composite = new CreateCompositePackageCatalogOptions
            {
                CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs,
                InstalledScope = PackageInstallScope.Any
            };
            composite.Catalogs.Add(source);
            var connection = await manager.CreateCompositePackageCatalog(composite).ConnectAsync().AsTask(token);
            if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null)
                throw new InvalidOperationException($"Official WinGet connection failed: {connection.Status}.");
            _connection = (manager, connection.PackageCatalog);
            return _connection.Value;
        }
        finally { _connectionGate.Release(); }
    }

    private static async Task<CatalogPackage?> FindAsync(PackageCatalog catalog, string id, IEnumerable<string> installedCodes, CancellationToken token)
    {
        var codes = installedCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length is 0 or > 32) return null;
        var options = new FindPackagesOptions { ResultLimit = 32 };
        // Remote-first ID lookup collapses side-by-side installations and can
        // bind x64 to an already-current x86 runtime. Search local product codes
        // first, then require the same official remote package ID and code.
        foreach (var code in codes) options.Selectors.Add(new PackageMatchFilter
        {
            Field = PackageMatchField.ProductCode,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = code
        });
        var result = await catalog.FindPackagesAsync(options).AsTask(token);
        if (result.Status != FindPackagesResultStatus.Ok || result.WasLimitExceeded) return null;
        var packages = Copy(result.Matches).Select(static match => match.CatalogPackage)
            .Where(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && package.InstalledVersion is not null &&
                Copy(package.InstalledVersion.ProductCodes).Intersect(codes, StringComparer.OrdinalIgnoreCase).Any()).ToArray();
        return packages.Length == 1 ? packages[0] : null;
    }

    private static PackageVersionId? VersionKey(CatalogPackage package, string version) =>
        Copy(package.AvailableVersions).FirstOrDefault(key =>
            key.Version.Equals(version, StringComparison.OrdinalIgnoreCase) && key.PackageCatalogId == OfficialSourceId);

    internal static T[] Copy<T>(IReadOnlyList<T> values)
    {
        // These COM vectors expose index access but do not always implement IIterable.
        var result = new T[values.Count];
        for (var i = 0; i < result.Length; i++) result[i] = values[i];
        return result;
    }

    private static IEnumerable<string> RegisteredIds(InstalledApplication application)
    {
        foreach (var item in application.Evidence.Where(static evidence => evidence.Label == "Uninstall registry" && evidence.Verified))
        {
            var separator = item.Value.LastIndexOf(" · ", StringComparison.Ordinal);
            yield return separator < 0 ? item.Value.Trim() : item.Value[(separator + 3)..].Trim();
        }
        if (application.RemovalPlan?.Kind == RemovalKind.WindowsInstaller)
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                application.RemovalPlan.Arguments ?? "", @"\{[0-9A-Fa-f-]{36}\}"))
                yield return match.Value;
    }
}
