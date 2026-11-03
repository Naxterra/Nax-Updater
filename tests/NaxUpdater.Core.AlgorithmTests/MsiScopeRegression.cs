using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// PdfCraft 0.18.0 failed with 1603 because its MSI ran without ALLUSERS=1 over a copy installed
// for all users. Each scenario is one method.
internal static class MsiScopeRegression
{
    public static void Run(Action<bool, string> assert)
    {
        elevated_msi_update_keeps_the_all_users_scope(assert);
        msi_arguments_are_left_alone_when_the_scope_is_given_or_per_user(assert);
    }

    private static UpdateExecutionPlan Plan(UpdateExecutionKind kind, bool elevated, params string[] arguments) =>
        new(kind, new Uri("https://vendor.example/app.msi"), "app.msi", new string('A', 64), "Vendor Inc", null, arguments, elevated, ["vendor.example"], []);

    private static void elevated_msi_update_keeps_the_all_users_scope(Action<bool, string> assert)
    {
        var msi = UpdateExecutionService.InstallerArguments(Plan(UpdateExecutionKind.DownloadedMsi, true, "/qn", "/norestart"));
        var zipped = UpdateExecutionService.InstallerArguments(Plan(UpdateExecutionKind.DownloadedZipMsi, true, "/qn"));
        assert(msi.SequenceEqual(["/qn", "/norestart", "ALLUSERS=1"]) && zipped.SequenceEqual(["/qn", "ALLUSERS=1"]),
            $"An MSI update for a copy installed for all users ran without ALLUSERS=1: {string.Join(" ", msi)} · {string.Join(" ", zipped)}");
    }

    private static void msi_arguments_are_left_alone_when_the_scope_is_given_or_per_user(Action<bool, string> assert)
    {
        var explicitScope = UpdateExecutionService.InstallerArguments(Plan(UpdateExecutionKind.DownloadedMsi, true, "/qn", "allusers=2"));
        var perUser = UpdateExecutionService.InstallerArguments(Plan(UpdateExecutionKind.DownloadedMsi, false, "/qn"));
        var exe = UpdateExecutionService.InstallerArguments(Plan(UpdateExecutionKind.DownloadedExe, true, "/S"));
        assert(explicitScope.SequenceEqual(["/qn", "allusers=2"]) && perUser.SequenceEqual(["/qn"]) && exe.SequenceEqual(["/S"]),
            "ALLUSERS=1 was added over a recipe's own ALLUSERS, to a per-user MSI, or to an EXE installer.");
    }
}
