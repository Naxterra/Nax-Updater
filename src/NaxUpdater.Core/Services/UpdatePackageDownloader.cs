using NaxUpdater.Core.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NaxUpdater.Core.Services;

public sealed class UpdatePackageDownloader(
    HttpClient httpClient,
    IAuthenticodeVerifier authenticodeVerifier,
    long segmentedDownloadThresholdBytes = 64L * 1024 * 1024)
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
            string.IsNullOrWhiteSpace(plan.FileName) ||
            (plan.RequireAuthenticode && ExpectedSigners(plan).Count == 0))
        {
            throw new InvalidOperationException("The update plan is missing its HTTPS URL, filename, hash, or signer policy.");
        }
        var hashPolicy = ResolveHashPolicy(plan);

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
            if (finalUri.Scheme != Uri.UriSchemeHttps ||
                (!plan.AllowHashVerifiedRedirects && !IsAllowedHost(finalUri.Host, plan.AllowedDownloadHosts)))
            {
                throw new InvalidOperationException($"The download redirected to untrusted host '{finalUri.Host}'.");
            }

            var contentLength = response.Content.Headers.ContentLength;
            string actualHash;
            var supportsRanges = contentLength >= segmentedDownloadThresholdBytes &&
                                 response.Headers.AcceptRanges.Any(static value => value.Equals("bytes", StringComparison.OrdinalIgnoreCase));
            if (supportsRanges)
            {
                response.Dispose();
                actualHash = await DownloadSegmentedAsync(
                    finalUri,
                    partialPath,
                    contentLength!.Value,
                    hashPolicy,
                    plan,
                    progress,
                    cancellationToken);
            }
            else
            {
                actualHash = await DownloadSingleResponseAsync(
                    response,
                    partialPath,
                    contentLength,
                    hashPolicy,
                    progress,
                    cancellationToken);
            }
            if (!actualHash.Equals(hashPolicy.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{hashPolicy.DisplayName} mismatch. Expected {hashPolicy.ExpectedHash}, received {actualHash}.");
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

    private static async Task<string> DownloadSingleResponseAsync(
        HttpResponseMessage response,
        string partialPath,
        long? contentLength,
        HashPolicy hashPolicy,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(hashPolicy.Algorithm);
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
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string> DownloadSegmentedAsync(
        Uri uri,
        string partialPath,
        long contentLength,
        HashPolicy hashPolicy,
        UpdateExecutionPlan plan,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        const int segmentCount = 6;
        var segmentPaths = Enumerable.Range(0, segmentCount)
            .Select(index => $"{partialPath}.segment{index}")
            .ToArray();
        long receivedTotal = 0;
        try
        {
            var segmentSize = (contentLength + segmentCount - 1) / segmentCount;
            var downloads = Enumerable.Range(0, segmentCount).Select(async index =>
            {
                var start = index * segmentSize;
                var end = Math.Min(contentLength - 1, start + segmentSize - 1);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Range = new RangeHeaderValue(start, end);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode != HttpStatusCode.PartialContent ||
                    response.Content.Headers.ContentRange is not { From: not null, To: not null } range ||
                    range.From.Value != start || range.To.Value != end)
                {
                    throw new InvalidDataException($"The server did not honor byte range {start}-{end}.");
                }
                var finalUri = response.RequestMessage?.RequestUri ?? uri;
                if (finalUri.Scheme != Uri.UriSchemeHttps ||
                    (!plan.AllowHashVerifiedRedirects && !IsAllowedHost(finalUri.Host, plan.AllowedDownloadHosts)))
                {
                    throw new InvalidOperationException($"A download segment redirected to untrusted host '{finalUri.Host}'.");
                }
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    segmentPaths[index],
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[1024 * 128];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    var received = Interlocked.Add(ref receivedTotal, read);
                    progress?.Report(Math.Clamp((double)received / contentLength, 0, 1));
                }
                await output.FlushAsync(cancellationToken);
                if (output.Length != end - start + 1)
                {
                    throw new InvalidDataException($"Segment {index} length was {output.Length}; expected {end - start + 1}.");
                }
            });
            await Task.WhenAll(downloads);

            using var hash = IncrementalHash.CreateHash(hashPolicy.Algorithm);
            await using var combined = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var mergeBuffer = new byte[1024 * 128];
            foreach (var segmentPath in segmentPaths)
            {
                await using var segment = File.OpenRead(segmentPath);
                while (true)
                {
                    var read = await segment.ReadAsync(mergeBuffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    await combined.WriteAsync(mergeBuffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(mergeBuffer, 0, read);
                }
            }
            await combined.FlushAsync(cancellationToken);
            if (combined.Length != contentLength)
            {
                throw new InvalidDataException($"Combined download length was {combined.Length}; expected {contentLength}.");
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            foreach (var segmentPath in segmentPaths)
            {
                if (File.Exists(segmentPath))
                {
                    File.Delete(segmentPath);
                }
            }
        }
    }

    private async Task<FileVerification> VerifyFileAsync(
        string path,
        UpdateExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hashPolicy = ResolveHashPolicy(plan);
        var hash = hashPolicy.Algorithm == HashAlgorithmName.SHA512
            ? await SHA512.HashDataAsync(stream, cancellationToken)
            : await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexString(hash);
        if (!actualHash.Equals(hashPolicy.ExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new FileVerification(false, null, $"The cached installer {hashPolicy.DisplayName} does not match the release.");
        }
        if (!plan.RequireAuthenticode)
        {
            return new FileVerification(true, "Unsigned installer; release hash verified", null);
        }
        var errors = new List<string>();
        foreach (var expectedSigner in ExpectedSigners(plan))
        {
            var signature = authenticodeVerifier.Verify(path, expectedSigner);
            if (signature.IsValid)
            {
                return new FileVerification(true, signature.Signer, null);
            }
            if (!string.IsNullOrWhiteSpace(signature.Error))
            {
                errors.Add(signature.Error);
            }
        }
        return new FileVerification(false, null, string.Join(" | ", errors));
    }

    private static bool IsAllowedHost(string host, IReadOnlyList<string> allowedHosts) =>
        allowedHosts.Any(allowed => host.Equals(allowed, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> ExpectedSigners(UpdateExecutionPlan plan)
    {
        if (plan.ExpectedSigners is { Count: > 0 })
        {
            return plan.ExpectedSigners.Where(static signer => !string.IsNullOrWhiteSpace(signer)).ToArray();
        }
        return string.IsNullOrWhiteSpace(plan.ExpectedSigner) ? [] : [plan.ExpectedSigner];
    }

    private static HashPolicy ResolveHashPolicy(UpdateExecutionPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.Sha512))
        {
            if (plan.Sha512.Length != 128 || !plan.Sha512.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException("The expected SHA-512 is invalid.");
            }
            return new HashPolicy(HashAlgorithmName.SHA512, plan.Sha512, "SHA-512");
        }
        if (!string.IsNullOrWhiteSpace(plan.Sha256) && plan.Sha256.Length == 64 && plan.Sha256.All(Uri.IsHexDigit))
        {
            return new HashPolicy(HashAlgorithmName.SHA256, plan.Sha256, "SHA-256");
        }
        throw new InvalidOperationException("The update plan does not contain a valid SHA-256 or SHA-512 hash.");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = string.Concat(value.Select(character => invalid.Contains(character) || character is ':' or '/' or '\\' ? '_' : character));
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private sealed record FileVerification(bool IsValid, string? Signer, string? Error);
    private sealed record HashPolicy(HashAlgorithmName Algorithm, string ExpectedHash, string DisplayName);
}

public sealed record VerifiedInstaller(string Path, string Signer);
