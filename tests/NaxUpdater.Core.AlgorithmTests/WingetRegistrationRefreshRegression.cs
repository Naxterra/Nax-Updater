using NaxUpdater.Core.Services;

internal static class WingetRegistrationRefreshRegression
{
    public static void Run(Action<bool, string> assert)
    {
        const string installed = "14.51.36247.0";
        assert(WingetPackageService.SameInstalledRegistration(installed, ["{X86-CODE}"], installed, ["{x86-code}"]),
            "The same installed registration could not refresh its stale WinGet availability result.");
        foreach (var (version, codes) in new (string?, string[])[]
        {
            ("14.52.37029.0", ["{X86-CODE}"]),
            (installed, ["{X64-CODE}"]),
            (installed, ["{X86-CODE}", "{X64-CODE}"]),
            (installed, []),
            (null, ["{X86-CODE}"])
        })
            assert(!WingetPackageService.SameInstalledRegistration(installed, ["{X86-CODE}"], version, codes),
                "An ID lookup for a changed, missing or merged runtime registration overrode the exact product-code lookup.");
        assert(!WingetPackageService.SameInstalledRegistration("Unknown", ["{CODE}"], "Unknown", ["{CODE}"]) &&
            !WingetPackageService.SameInstalledRegistration(installed, [], installed, []),
            "Unknown versions or absent product codes justified a WinGet availability override.");
    }
}
