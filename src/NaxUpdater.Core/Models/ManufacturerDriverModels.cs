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

public enum ManufacturerCheckEvidence { None, LiveCatalog, ReferenceRelease, SupportGuidance }

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
    UpdateCheckResult? ExecutableUpdate,
    ManufacturerCheckEvidence CheckEvidence = ManufacturerCheckEvidence.None,
    bool LiveCheckFailed = false)
{
    public bool IsLiveChecked => CheckEvidence == ManufacturerCheckEvidence.LiveCatalog && !HasCheckIssue;
    public bool HasCheckIssue => LiveCheckFailed || Status == ManufacturerDriverStatus.Error;
}

public sealed record ManufacturerDriverSnapshot(
    DateTimeOffset CheckedAt,
    IReadOnlyList<ManufacturerDriverResult> Results,
    IReadOnlyList<string> Issues)
{
    public int CheckedSourceCount => Results.Count(r => r.IsLiveChecked);
    public int CheckIssueCount => Issues.Count + Results.Count(r => r.HasCheckIssue);
}

public sealed record InstalledDriverSnapshot(
    IReadOnlyList<InstalledHardwareDriver> Drivers,
    IReadOnlyList<string> Issues);
