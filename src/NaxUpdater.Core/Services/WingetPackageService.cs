using Microsoft.Management.Deployment;
using NaxUpdater.Core.Models;
using System.Text.Json;
using Windows.System;

namespace NaxUpdater.Core.Services;

public sealed record WingetPackageOffer(WingetUpdateTarget? Target, string? Error);

public interface IWingetPackageService
{
    Task<WingetPackageOffer> AssessAsync(InstalledApplication application, string packageId, string version, CancellationToken token);
    Task<PreparedCatalogUpdate> PrepareAsync(UpdateCheckResult update, CancellationToken token);
}

public sealed class PreparedCatalogUpdate(
    WingetUpdateTarget target,
    Func<CancellationToken, Task<UpdateExecutionResult>> apply)
{
    public WingetUpdateTarget Target { get; } = target;
    public Task<UpdateExecutionResult> ApplyAsync(CancellationToken token) => apply(token);
}

// The package manager owns manifest authentication, download hashes, dependencies,
// installer switches and elevation. Never substitute a source alias or scraped YAML.
public sealed class WingetPackageService : IWingetPackageService
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
            var package = await FindAsync(catalog, packageId, token);
            if (package?.InstalledVersion is null)
                return new(null, "WinGet could not correlate this package with an installed application.");
            var registeredIds = RegisteredIds(application);
            var installedIds = Copy(package.InstalledVersion.ProductCodes);
            if (!registeredIds.Intersect(installedIds, StringComparer.OrdinalIgnoreCase).Any())
                return new(null, "The installed product code does not match the package selected by WinGet.");
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
        var package = await FindAsync(catalog, target.PackageId, token)
            ?? throw new InvalidOperationException("The approved WinGet package is no longer present.");
        var key = VersionKey(package, target.Version)
            ?? throw new InvalidOperationException("The approved WinGet version is no longer present.");
        var options = CreateOptions(key, target.Scope, target.InstallLocation);
        SetArchitecture(options, target.Architecture);
        options.InstallerType = Enum.Parse<PackageInstallerType>(target.InstallerType);
        ValidatePrepared(package, key, options, target);
        // Keep this catalog package, version key and options alive through Apply.
        return new PreparedCatalogUpdate(target, async cancellationToken =>
        {
            ValidatePrepared(package, key, options, target);
            var operation = manager.UpgradePackageAsync(package, options);
            try
            {
                // Once submitted, Windows owns installation and possible UAC. Await its
                // actual result even if the caller stops waiting; never kill an installer.
                var result = await operation.AsTask();
                var success = result.Status == InstallResultStatus.Ok;
                var code = result.RebootRequired ? 3010 : unchecked((int)result.InstallerErrorCode);
                if (!success && code == 0) code = result.ExtendedErrorCode?.HResult ?? -1;
                if (result.ExtendedErrorCode?.HResult == unchecked((int)0x800704C7)) code = 1223;
                return new UpdateExecutionResult(code, success,
                    success ? null : $"WinGet returned {result.Status}: {result.ExtendedErrorCode?.Message}");
            }
            catch (Exception exception)
            {
                return new UpdateExecutionResult(
                    exception.HResult == unchecked((int)0x800704C7) ? 1223 : -1, false, exception.Message);
            }
        });
    }

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
                CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs,
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

    private static async Task<CatalogPackage?> FindAsync(PackageCatalog catalog, string id, CancellationToken token)
    {
        var options = new FindPackagesOptions { ResultLimit = 2 };
        options.Selectors.Add(new PackageMatchFilter
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = id
        });
        var result = await catalog.FindPackagesAsync(options).AsTask(token);
        if (result.Status != FindPackagesResultStatus.Ok || result.WasLimitExceeded) return null;
        var packages = Copy(result.Matches).Select(static match => match.CatalogPackage)
            .Where(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
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
