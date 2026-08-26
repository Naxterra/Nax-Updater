namespace NaxUpdater.Core.Models;

public enum RemovalKind
{
    RegisteredUninstaller,
    WindowsInstaller,
    ZeroInstall,
    MsixPackage
}

public sealed record RemovalPlan(
    RemovalKind Kind,
    string? Executable,
    string? Arguments,
    string? PackageFullName,
    bool RequiresElevation);

public sealed record RemovalResult(
    bool IsSuccess,
    int? ExitCode,
    bool RestartRequired,
    string? Error);
