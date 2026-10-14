using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

internal static class SupersededRegistrationRegression
{
    public static void Run(Action<bool, string> assert)
    {
        // A VC++ 2013 12.0.30501 bundle registered itself on 2026-10-13 although its
        // packages were "Obsolete" next to the installed 12.0.40664 runtime.
        UpdateCheckResult Result(string name, string installed, UpdateStatus status, string? available) => new(
            "fixture:" + name, name, installed, available, status, "winget-fallback", "WinGet", "neutral", "fixture", "x64", "stable",
            null, "fixture", null, Applicability: status == UpdateStatus.Current ? UpdateApplicability.NotRequired : UpdateApplicability.NotApplicable);
        var results = new[]
        {
            Result("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.30501", "12.0.30501.0", UpdateStatus.NewerReleaseKnown, "12.0.40664.0"),
            Result("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664", "12.0.40664.0", UpdateStatus.Current, "12.0.40664.0"),
            Result("Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.30501", "12.0.30501.0", UpdateStatus.NewerReleaseKnown, "12.0.40664.0"),
            Result("Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.21005", "12.0.21005.0", UpdateStatus.Current, "12.0.21005.0"),
            Result("Some App", "1.0", UpdateStatus.NewerReleaseKnown, "2.0"),
            Result("Some App", "2.0", UpdateStatus.Current, "2.0")
        };
        UpdateCheckService.ReconcileSupersededRegistrations(results);
        assert(results[0] is { Status: UpdateStatus.Current, ProviderId: "superseded-registration", AvailableVersion: "12.0.40664.0", ExecutionPlan: null } &&
               results[0].Message?.Contains("12.0.40664") == true,
            "An orphaned bundle registration next to a current newer sibling was still reported as outdated.");
        assert(results[2].Status == UpdateStatus.NewerReleaseKnown,
            "An x86 registration was settled by a sibling of another architecture or an older one.");
        assert(results[4].Status == UpdateStatus.NewerReleaseKnown,
            "Names without a trailing version were treated as one family.");
        assert(UpdateCheckService.VersionedNameFamily("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.30501") == "Microsoft Visual C++ 2013 Redistributable (x64)" &&
               UpdateCheckService.VersionedNameFamily("Python 3.15.0 (64-bit)") is null,
            "The versioned name family was misread.");
    }
}
