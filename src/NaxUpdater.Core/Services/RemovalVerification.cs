using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

public static class RemovalVerification
{
    // Absence is evidence only after a successful, complete inventory read.
    public static bool? IsStillPresent(InstalledApplication application, InventorySnapshot? snapshot)
    {
        if (snapshot is null) return null;
        if (snapshot.Applications.Any(installed => installed.Identity == application.Identity ||
            (installed.DisplayName.Equals(application.DisplayName, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(installed.PrimaryInstallPath, application.PrimaryInstallPath, StringComparison.OrdinalIgnoreCase))))
            return true;
        return snapshot.Issues.Count == 0 ? false : null;
    }
}
