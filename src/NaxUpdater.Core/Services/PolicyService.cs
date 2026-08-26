using NaxUpdater.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaxUpdater.Core.Services;

public sealed class PolicyService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<ApplicationPolicy>> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        var catalog = await JsonSerializer.DeserializeAsync<ApplicationPolicyCatalog>(
            stream,
            SerializerOptions,
            cancellationToken);
        return catalog?.Applications ?? [];
    }

    public static bool IsMatch(ApplicationPolicy policy, string displayName, string? publisher)
    {
        if (!string.Equals(policy.DisplayName.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(policy.PublisherContains) ||
               (!string.IsNullOrWhiteSpace(publisher) &&
                publisher.Contains(policy.PublisherContains, StringComparison.OrdinalIgnoreCase));
    }
}
