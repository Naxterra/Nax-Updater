namespace NaxUpdater.Core.Models;

public enum ManufacturerDriverStatus
{
    Current,
    Available,
    VendorSoftwareManaged,
    OfficialSourceOnly,
    NoUpdateRequired,
    NoVerifiedCatalog,
    Error
}

public sealed record InstalledHardwareDriver(
    string Identity,
    string DeviceName,
    string DeviceClass,
    string Manufacturer,
    string Provider,
    string InstalledVersion,
    string? DriverDate,
    string? HardwareId,
    string? InfName,
    bool IsPresent = true,
    int DeviceCount = 1,
    IReadOnlyList<string>? GroupMembers = null);

public sealed record ManufacturerDriverResult(
    InstalledHardwareDriver Driver,
    ManufacturerDriverStatus Status,
    string? AvailableVersion,
    string SourceName,
    Uri? SourceUri,
    string Message,
    UpdateCheckResult? ExecutableUpdate);

public sealed record ManufacturerDriverSnapshot(
    DateTimeOffset CheckedAt,
    IReadOnlyList<ManufacturerDriverResult> Results,
    IReadOnlyList<string> Issues);
