using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;

// Skipping one release or holding an application keeps it out of Update All without stopping
// its checks, and a copied diagnostics report must not carry local or secret details.
// Each scenario is one method.
internal static class PreferenceDiagnosticsRegression
{
    public static void Run(Action<bool, string> assert)
    {
        skipped_release_stays_out_until_a_newer_one_appears(assert);
        held_application_stays_out_after_its_provider_changes(assert);
        damaged_preferences_file_is_kept_aside(assert);
        diagnostics_redact_paths_names_url_parameters_and_credentials(assert);
        diagnostics_report_names_the_plan_and_the_last_outcomes(assert);
    }

    private static UpdateCheckResult Update(string available, string identity = "registry:LocalMachine:Registry64:{11111111-1111-1111-1111-111111111111}",
        string provider = "github:vendor/app", UpdateExecutionPlan? plan = null) =>
        new(identity, "Fixture App", "1.0.0", available, UpdateStatus.Available, provider, "Official GitHub release", "neutral", "fixture",
            "x64", "stable", null, "fixture", plan, CorrelationKey: "msi-upgrade:{22222222-2222-2222-2222-222222222222}");

    private static void skipped_release_stays_out_until_a_newer_one_appears(Action<bool, string> assert)
    {
        var folder = Folder();
        try
        {
            var preferences = new FileUpdatePreferences(Path.Combine(folder, "update-preferences.json"));
            preferences.SkipVersion(Update("2.0.0"));
            var skipped = preferences.ExclusionFor(Update("2.0.0"));
            var newer = preferences.ExclusionFor(Update("2.1.0"));
            preferences.ClearSkippedVersion(Update("2.0.0"));
            assert(skipped == UpdateExclusion.SkippedVersion && newer == UpdateExclusion.None &&
                   preferences.ExclusionFor(Update("2.0.0")) == UpdateExclusion.None && preferences.ReadAll().Count == 0,
                $"A skipped release did not stay out only until a newer one appeared, or clearing it left an entry: {skipped} · {newer}");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void held_application_stays_out_after_its_provider_changes(Action<bool, string> assert)
    {
        var folder = Folder();
        try
        {
            var preferences = new FileUpdatePreferences(Path.Combine(folder, "update-preferences.json"));
            preferences.SetHeld(Update("2.0.0"), true);
            // A major upgrade replaced the MSI product code and the app moved to another provider.
            var moved = Update("3.0.0", identity: "registry:LocalMachine:Registry64:{33333333-3333-3333-3333-333333333333}", provider: "official:Vendor.App");
            var held = preferences.ExclusionFor(moved);
            preferences.SetHeld(moved, false);
            assert(held == UpdateExclusion.Held && preferences.ExclusionFor(moved) == UpdateExclusion.None,
                $"A hold was lost when the application's provider and product code changed, or could not be released: {held}");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void damaged_preferences_file_is_kept_aside(Action<bool, string> assert)
    {
        var folder = Folder();
        try
        {
            var path = Path.Combine(folder, "update-preferences.json");
            File.WriteAllText(path, "[ not json");
            var preferences = new FileUpdatePreferences(path);
            var unreadable = preferences.ExclusionFor(Update("2.0.0"));
            preferences.SetHeld(Update("2.0.0"), true);
            assert(unreadable == UpdateExclusion.None && File.ReadAllText(path + ".unreadable") == "[ not json" &&
                   preferences.ExclusionFor(Update("2.0.0")) == UpdateExclusion.Held,
                "A damaged preferences file was overwritten instead of being kept aside, or the new choice was lost.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void diagnostics_redact_paths_names_url_parameters_and_credentials(Action<bool, string> assert)
    {
        const string text = """
            C:\Users\Robin\AppData\Local\NaxUpdater\Downloads\app 1.0\setup.exe failed
            https://objects.example.com/release/setup.exe?X-Amz-Signature=s3cr3tvalue&token=s3cr3tvalue
            token=ghp_abcdefghijklmnopqrstuvwxyz0123456789
            Authorization: Bearer s3cr3tvalue
            \\WORKSTATION-7\share\folder\file.msi
            signed in as robin on WORKSTATION-7
            """;
        var redacted = UpdateDiagnostics.Redact(text, "Robin", "WORKSTATION-7");
        string[] secrets = ["Robin", "robin", "WORKSTATION-7", "AppData", "X-Amz", "s3cr3tvalue", "ghp_", "Downloads"];
        var leaked = secrets.Where(secret => redacted.Contains(secret, StringComparison.Ordinal)).ToArray();
        assert(leaked.Length == 0 && redacted.Contains(@"<path>\setup.exe failed", StringComparison.Ordinal) &&
               redacted.Contains("https://objects.example.com/release/setup.exe", StringComparison.Ordinal) &&
               redacted.Contains(@"<network path>\file.msi", StringComparison.Ordinal),
            $"The diagnostics report kept a private detail or lost what is needed to read it ({string.Join(", ", leaked)}):{Environment.NewLine}{redacted}");
    }

    private static void diagnostics_report_names_the_plan_and_the_last_outcomes(Action<bool, string> assert)
    {
        var plan = new UpdateExecutionPlan(UpdateExecutionKind.DownloadedMsi, new Uri("https://vendor.example/app.msi?expires=1&sig=abc"), "app.msi",
            new string('A', 64), "Vendor Inc", null, ["/qn"], true, ["vendor.example"], []);
        var failed = new UpdateHistoryEntry(Guid.NewGuid(), "Fixture App", "registry:LocalMachine:Registry64:{11111111-1111-1111-1111-111111111111}",
            "github:vendor/app", "1.0.0", "2.0.0", "1.0.0", UpdateTransactionStage.FailedNeedsAttention, 1603, false,
            @"The updater exited with code 1603. Log: D:\Logs\private\install.log", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var report = UpdateDiagnostics.Build(Update("2.0.0", plan: plan), "Failed", "The updater exited with code 1603.", [failed], "0.17.56");
        assert(report.Contains("download host vendor.example", StringComparison.Ordinal) && report.Contains("exit code 1603", StringComparison.Ordinal) &&
               report.Contains("FailedNeedsAttention", StringComparison.Ordinal) && !report.Contains("sig=abc", StringComparison.Ordinal) &&
               !report.Contains(@"D:\Logs", StringComparison.Ordinal) && report.Contains(@"<path>\install.log", StringComparison.Ordinal),
            $"The diagnostics report lacked the plan or the last outcome, or kept a private detail:{Environment.NewLine}{report}");
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nax-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
