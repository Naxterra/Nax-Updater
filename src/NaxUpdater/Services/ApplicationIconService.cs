using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NaxUpdater.Core.Models;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NaxUpdater.Services;

public sealed class ApplicationIconService
{
    private const uint IconSize = 40;
    private readonly Dictionary<string, Task<ImageSource?>> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrency = new(10);

    public Task<ImageSource?> LoadAsync(InstalledApplication application)
    {
        if (_cache.TryGetValue(application.Identity, out var cached))
        {
            return cached;
        }

        var task = LoadCoreAsync(application);
        _cache[application.Identity] = task;
        return task;
    }

    private async Task<ImageSource?> LoadCoreAsync(InstalledApplication application)
    {
        await _concurrency.WaitAsync();
        try
        {
            foreach (var path in IconCandidates(application))
            {
                var icon = await TryLoadPathAsync(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
            return null;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static IEnumerable<string> IconCandidates(InstalledApplication application)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Packaged executables often expose only a generic executable icon. Prefer the
        // package manifest's Square44x44Logo/Logo assets before asking Shell for the EXE.
        if (application.ManagementMode == ManagementMode.Msix)
        {
            foreach (var evidence in application.Evidence.Where(static evidence =>
                         evidence.Label == "MSIX installed location"))
            {
                if (!string.IsNullOrWhiteSpace(evidence.Value) && seen.Add(evidence.Value))
                {
                    yield return evidence.Value;
                }
            }
        }

        foreach (var evidence in application.Evidence.Where(static evidence =>
                     evidence.Label == "Display icon path" ||
                     evidence.Label == "Executable path" ||
                     evidence.Label == "Resolved application executable" ||
                     evidence.Label == "Windows shortcut"))
        {
            if (!string.IsNullOrWhiteSpace(evidence.Value) && seen.Add(evidence.Value))
            {
                yield return evidence.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(application.PrimaryInstallPath) && seen.Add(application.PrimaryInstallPath))
        {
            yield return application.PrimaryInstallPath;
        }
    }

    private static async Task<ImageSource?> TryLoadPathAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                if (IsDirectImage(path))
                {
                    await using var stream = await file.OpenStreamForReadAsync();
                    var bitmap = new BitmapImage { DecodePixelWidth = (int)IconSize };
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                    return bitmap;
                }

                using var thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    IconSize,
                    ThumbnailOptions.UseCurrentScale);
                if (thumbnail is not null && thumbnail.Size > 0)
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = (int)IconSize };
                    await bitmap.SetSourceAsync(thumbnail);
                    return bitmap;
                }
            }
            else if (Directory.Exists(path))
            {
                var directoryIcon = FindPackageIcon(path) ?? FindDesktopIcon(path);
                if (directoryIcon is not null)
                {
                    return await TryLoadPathAsync(directoryIcon);
                }
            }
        }
        catch (Exception)
        {
            // A protected package directory or a shell thumbnail provider may reject access.
            // The list keeps its neutral fallback icon in that case.
        }
        return null;
    }

    private static string? FindDesktopIcon(string directory)
    {
        try
        {
            var image = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsDirectImage)
                .OrderBy(static path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    return name.Contains("icon", StringComparison.OrdinalIgnoreCase) ? 0 :
                           name.Contains("logo", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                })
                .FirstOrDefault();
            if (image is not null)
            {
                return image;
            }

            return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(static path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    return !name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("update", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("crash", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(static path => new FileInfo(path).Length)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindPackageIcon(string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = XDocument.Load(manifestPath, LoadOptions.None);
            var logoValues = manifest.Descendants()
                .SelectMany(static element => element.Attributes())
                .Where(static attribute => attribute.Name.LocalName is "Square44x44Logo" or "Logo" or "Square150x150Logo")
                .OrderBy(static attribute => attribute.Name.LocalName == "Square44x44Logo" ? 0 :
                                             attribute.Name.LocalName == "Logo" ? 1 : 2)
                .Select(static attribute => attribute.Value)
                .Concat(manifest.Descendants().Where(static element => element.Name.LocalName == "Logo").Select(static element => element.Value));

            foreach (var value in logoValues.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                var relativePath = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var declaredPath = Path.Combine(packageDirectory, relativePath);
                if (File.Exists(declaredPath))
                {
                    return declaredPath;
                }

                var directory = Path.GetDirectoryName(declaredPath);
                var stem = Path.GetFileNameWithoutExtension(declaredPath);
                var extension = Path.GetExtension(declaredPath);
                if (directory is null || !Directory.Exists(directory) || string.IsNullOrWhiteSpace(stem))
                {
                    continue;
                }

                var scaled = Directory.EnumerateFiles(directory, $"{stem}*{extension}", SearchOption.TopDirectoryOnly)
                    .OrderBy(IconVariantScore)
                    .FirstOrDefault();
                if (scaled is not null)
                {
                    return scaled;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    private static int IconVariantScore(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Contains("targetsize-40", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("targetsize-32", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("scale-100", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains("scale-200", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("contrast-standard", StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static bool IsDirectImage(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico";
}
