using NaxUpdater.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaxUpdater.Core.Services;

public static class UpdateProviderCatalogLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<UpdateProviderCatalog> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new UpdateProviderCatalog();
        }
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UpdateProviderCatalog>(stream, Options, cancellationToken) ??
               new UpdateProviderCatalog();
    }
}
