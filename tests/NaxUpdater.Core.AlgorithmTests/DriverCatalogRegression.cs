using NaxUpdater.Core.Services;

// Direct driver updates verify the signer of the package's catalog file (.cat), a signed
// PKCS #7 message. The signer used to be read only from signed program files, so every
// direct driver update failed with "The file does not contain an Authenticode signature".
internal static class DriverCatalogRegression
{
    public static void Run(Action<bool, string> assert)
    {
        native_verifier_reads_the_signer_of_a_driver_catalog(assert);
    }

    private static void native_verifier_reads_the_signer_of_a_driver_catalog(Action<bool, string> assert)
    {
        // 31bf3856ad364e35 is the public key token of packages that Microsoft itself signs.
        var catRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CatRoot", "{F750E6C3-38EE-11D1-85E5-00C04FC295EE}");
        var catalog = Directory.Exists(catRoot) ? Directory.EnumerateFiles(catRoot, "*31bf3856ad364e35*.cat").FirstOrDefault() : null;
        assert(catalog is not null, "No Windows catalog file was found to verify.");
        var verifier = new NativeAuthenticodeVerifier();
        var result = verifier.Verify(catalog!, "Microsoft");
        assert(result.IsValid && result.Signer?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true,
            $"The signer of a Windows catalog file was not read: {result.Error}");
        var wrong = verifier.Verify(catalog!, "=Intel Corporation");
        assert(!wrong.IsValid && wrong.Error?.StartsWith("Expected signer", StringComparison.Ordinal) == true,
            "A catalog file was accepted for a signer that did not sign it.");
    }
}
