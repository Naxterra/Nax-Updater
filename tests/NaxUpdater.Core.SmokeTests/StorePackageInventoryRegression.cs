using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using System.Text.Json;

internal static class StorePackageInventoryRegression
{
    public static async Task RunAsync(Action<bool, string> assert, InventorySnapshot full, string policyPath)
    {
        var service = new ApplicationInventoryService(policyPath);
        var samples = full.Applications.Where(a => a.ManagementMode == ManagementMode.Msix && !a.IsSystemComponent).Take(3).ToArray();
        foreach (var expected in samples)
        {
            var fresh = await service.ScanStorePackageAsync(expected.Identity[5..]);
            var actual = fresh.Applications.SingleOrDefault();
            assert(fresh.Issues.Count == 0 && actual is not null && actual.Identity == expected.Identity &&
                actual.NormalizedVersion == expected.NormalizedVersion && actual.PrimaryInstallPath == expected.PrimaryInstallPath &&
                actual.Scope == expected.Scope && actual.BlockedProviders.SequenceEqual(expected.BlockedProviders),
                "The targeted Store inventory lost live identity, version, path, scope or policy: " + expected.DisplayName);
            assert(actual!.Evidence.Any(e => e.Label == "MSIX package family" && e.Verified) &&
                actual.Evidence.Any(e => e.Label == "MSIX package architecture" && e.Verified),
                "The targeted Store inventory lost verified package evidence: " + expected.DisplayName);
        }
        var absent = await service.ScanStorePackageAsync("NaxUpdater.NonexistentRegressionPackage_fixture");
        assert(absent.Applications.Count == 0 && absent.Issues.Count == 0, "A missing Store family returned an unrelated application.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await service.ScanStorePackageAsync("Fixture_family", canceled.Token); assert(false, "A canceled Store inventory completed."); }
        catch (OperationCanceledException) { assert(true, "Targeted Store cancellation propagated."); }
        if (samples.Length == 0) return;
        var policyFile = Path.Combine(Path.GetTempPath(), "nax-store-policy-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(policyFile, JsonSerializer.Serialize(new { applications = new[] { new {
                id = "fixture-policy", displayName = samples[0].DisplayName, blockedProviders = new[] { "fixture-blocked" },
                preferredProvider = "fixture-preferred"
            } } }));
            var changed = (await new ApplicationInventoryService(policyFile).ScanStorePackageAsync(samples[0].Identity[5..])).Applications.Single();
            assert(changed.BlockedProviders.Contains("fixture-blocked") &&
                changed.Evidence.Any(e => e.Label == "Preferred update provider" && e.Value == "fixture-preferred"),
                "Targeted revalidation bypassed a freshly changed application policy.");
        }
        finally { File.Delete(policyFile); }
    }
}
