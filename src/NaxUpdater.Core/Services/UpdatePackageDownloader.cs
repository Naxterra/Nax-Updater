using NaxUpdater.Core.Models;
using System.Security.Cryptography;

namespace NaxUpdater.Core.Services;

public sealed class UpdatePackageDownloader(
    HttpClient httpClient,
    IAuthenticodeVerifier authenticodeVerifier)
{
    public async Task<VerifiedInstaller> DownloadAndVerifyAsync(
        UpdateCheckResult update,
        string cacheRoot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = update.ExecutionPlan ?? throw new InvalidOperationException("The update has no execution plan.");
        if (plan.Kind == UpdateExecutionKind.NativeCommand)
        {
            throw new InvalidOperationException("Native-provider updates do not download an installer.");
        }
        if (plan.DownloadUri is null || plan.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(plan.FileName) || string.IsNullOrWhiteSpace(plan.Sha256) ||
            string.IsNullOrWhiteSpace(plan.ExpectedSigner))
        {
            throw new InvalidOperationException("The update plan is missing its HTTPS URL, filename, SHA-256, or signer policy.");
        }
        if (plan.Sha256.Length != 64 || !plan.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("The expected SHA-256 is invalid.");
        }

        var safeFileName = Path.GetFileName(plan.FileName);
        if (!string.Equals(safeFileName, plan.FileName, StringComparison.Ordinal) || safeFileName.Length == 0)
        {
            throw new InvalidOperationException("The update filename is unsafe.");
        }
        var providerDirectory = SanitizePathSegment(update.ProviderId);
        var versionDirectory = SanitizePathSegment(update.AvailableVersion ?? "unknown");
        var destinationDirectory = Path.Combine(cacheRoot, providerDirectory, versionDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, safeFileName);

        if (File.Exists(destinationPath))
        {
            var existing = await VerifyFileAsync(destinationPath, plan, cancellationToken);
            if (existing.IsValid)
            {
                progress?.Report(1);
                return new VerifiedInstaller(destinationPath, existing.Signer!);
            }
            File.Delete(destinationPath);
        }

        var partialPath = destinationPath + ".partial";
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }
        try
        {
            using var response = await httpClient.GetAsync(plan.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? plan.DownloadUri;
            if (!IsAllowedHost(finalUri.Host, plan.AllowedDownloadHosts))
            {
                throw new InvalidOperationException($"The download redirected to untrusted host '{finalUri.Host}'.");
            }

            var contentLength = response.Content.Headers.ContentLength;
            string actualHash;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 128,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 128];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (contentLength is > 0)
                    {
                        progress?.Report(Math.Clamp((double)received / contentLength.Value, 0, 1));
                    }
                }
                await output.FlushAsync(cancellationToken);
                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (!actualHash.Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 mismatch. Expected {plan.Sha256}, received {actualHash}.");
            }
            File.Move(partialPath, destinationPath, overwrite: true);

            var verified = await VerifyFileAsync(destinationPath, plan, cancellationToken);
            if (!verified.IsValid)
            {
                File.Delete(destinationPath);
                throw new InvalidDataException(verified.Error ?? "Authenticode verification failed.");
            }
            progress?.Report(1);
            return new VerifiedInstaller(destinationPath, verified.Signer!);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
            throw;
        }
    }

    private async Task<FileVerification> VerifyFileAsync(
        string path,
        UpdateExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexString(hash);
        if (!actualHash.Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new FileVerification(false, null, "The cached installer SHA-256 does not match the release.");
        }
        var signature = authenticodeVerifier.Verify(path, plan.ExpectedSigner!);
        return new FileVerification(signature.IsValid, signature.Signer, signature.Error);
    }

    private static bool IsAllowedHost(string host, IReadOnlyList<string> allowedHosts) =>
        allowedHosts.Any(allowed => host.Equals(allowed, StringComparison.OrdinalIgnoreCase));

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = string.Concat(value.Select(character => invalid.Contains(character) || character is ':' or '/' or '\\' ? '_' : character));
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private sealed record FileVerification(bool IsValid, string? Signer, string? Error);
}

public sealed record VerifiedInstaller(string Path, string Signer);
