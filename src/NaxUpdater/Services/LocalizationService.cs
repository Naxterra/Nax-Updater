using Microsoft.Windows.ApplicationModel.Resources;
using NaxUpdater.Core.Models;
using System.Text.RegularExpressions;

namespace NaxUpdater.Services;

public static partial class LocalizationService
{
    private static readonly ResourceLoader ResourceLoader = new();

    public static string Get(string key)
    {
        try
        {
            var value = ResourceLoader.GetString(key);
            return string.IsNullOrWhiteSpace(value)
                ? key
                : value.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);
        }
        catch
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), arguments);

    public static string ProviderName(UpdateCheckResult update)
    {
        if (update.ProviderId == "mozilla-firefox") return Get("ProviderMozilla");
        if (update.ProviderId == "zero-install") return Get("ProviderZeroInstall");
        if (update.ProviderId == "wsl-native") return Get("ProviderWsl");
        if (update.ProviderId == "native-updater") return update.ProviderDisplayName == "NaxUpdater manufacturer-driver view"
            ? Get("ProviderDriverWorkspace") : update.ProviderDisplayName;
        if (update.ProviderId == "unverified") return Get("ProviderUnverified");
        if (update.ProviderId == "msix-store") return Get("ProviderMsixStore");
        if (update.ProviderId == "openai-codex-store") return Get("ProviderOpenAiStore");
        if (update.ProviderId == "installed-updater-metadata") return Get("ProviderInstalledMetadata");
        if (update.ProviderId == "gog-galaxy-native") return Get("ProviderGogGalaxyNative");
        if (update.ProviderId == "rarlab-winrar") return Get("ProviderWinRar");
        if (update.ProviderId == "winget-fallback") return Get("ProviderWingetFallback");
        if (update.ProviderId.StartsWith("github:", StringComparison.Ordinal))
        {
            return Format("ProviderGitHub", update.ProviderId[7..]);
        }
        return update.ProviderDisplayName;
    }

    public static string ProviderMessage(UpdateCheckResult update)
    {
        if (update.AvailabilityReason == UpdateAvailabilityReason.NoApplicableStoreUpdate) return Get("NoApplicableStoreUpdateMessage");
        if (update.ProviderId == "wsl-native" && update.Status != UpdateStatus.Error) return Get("ProviderWslNote");
        if (update.AvailabilityReason == UpdateAvailabilityReason.AwaitingStoreOffer)
            return Format("StoreOfferPendingMessage", update.PublishedPackageVersion, update.InstalledVersion);
        if (update.ProviderId == "openai-codex-store" && update.Status == UpdateStatus.Available &&
            update.PublishedPackageVersion is not null)
            return Format("StorePublishedUpdateAvailableMessage", update.AvailableVersion, update.AnnouncedVersion ?? update.AvailableVersion);
        if (update.AvailabilityReason == UpdateAvailabilityReason.AwaitingStorePublication)
            return Format("StorePublicationPendingMessage", update.AvailableVersion, update.PublishedPackageVersion, update.InstalledVersion);
        if (update.Status is UpdateStatus.Error or UpdateStatus.NewerReleaseKnown)
            return update.Message ?? Get(update.Status == UpdateStatus.Error ? "StatusCheckFailed" : "ProviderReleaseKnownNotAutomatic");
        if (update.ProviderId == "native-updater") return Format("ProviderManagedUnchecked", ProviderName(update));
        if (update.ProviderId is "macrium-release" or "driver-component" or "mozilla-maintenance") return update.Message ?? Get("StatusNotChecked");
        if (update.ProviderId == "unverified") return Get("ProviderUnverifiedNote");
        if (update.ProviderId == "msix-store") return update.Status switch
        {
            UpdateStatus.Available => Get("ProviderMsixStoreAvailable"),
            UpdateStatus.Current => Get("ProviderMsixStoreCurrent"),
            _ => Get("ProviderMsixStoreNote")
        };
        if (update.ProviderId == "openai-codex-store") return update.Status switch
        {
            UpdateStatus.Available => Get("ProviderOpenAiStoreAvailable"),
            UpdateStatus.NewerReleaseKnown => Get("ProviderOpenAiReleaseKnown"),
            _ => Get("ProviderOpenAiStoreCurrent")
        };
        if (update.ProviderId == "gog-galaxy-native") return Get("ProviderGogGalaxyNativeNote");
        if (update.ProviderId == "winget-fallback") return update.Status switch
        {
            UpdateStatus.Current => Get("ProviderWingetFallbackCurrent"),
            UpdateStatus.Available when update.ExecutionPlan?.Kind == UpdateExecutionKind.WingetPackage => Get("SecurityWingetProvider"),
            _ => Get("ProviderWingetFallbackBlocked")
        };
        if (update.Status == UpdateStatus.NewerReleaseKnown) return Get("ProviderReleaseKnownNotAutomatic");
        if (update.ProviderId == "installed-updater-metadata") return Get("ProviderInstalledMetadataNote");
        if (update.ProviderId == "rarlab-winrar") return update.Status == UpdateStatus.Available
            ? Get("ProviderWinRarAvailable")
            : Get("ProviderWinRarCurrent");
        if (update.ProviderId.StartsWith("github:", StringComparison.Ordinal)) return Get("ProviderGitHubNote");
        if (update.ProviderId == "zero-install")
        {
            var digest = DigestRegex().Match(update.Message ?? string.Empty);
            return digest.Success ? Format("ProviderZeroInstallDigest", digest.Groups["digest"].Value) : update.Message ?? Get("NoAdditionalNotes");
        }
        if (update.ProviderId == "mozilla-firefox")
        {
            var language = FirefoxLanguageRegex().Match(update.Message ?? string.Empty);
            if (language.Success)
            {
                return Format(
                    "ProviderFirefoxLanguageOverride",
                    language.Groups["packaged"].Value,
                    language.Groups["effective"].Value,
                    language.Groups["path"].Value);
            }
            return update.Message ?? Get("NoAdditionalNotes");
        }
        return update.Message ?? Get("NoAdditionalNotes");
    }

    public static string LanguageSource(string source) => source switch
    {
        "Active Firefox language pack in the default installation profile" => Get("LanguageSourceFirefoxPack"),
        "Firefox default-profile locale preference" => Get("LanguageSourceFirefoxProfile"),
        "Firefox installed-package locale" => Get("LanguageSourceFirefoxPackage"),
        "Vendor multi-language installer" => Get("LanguageSourceMultiLanguage"),
        "Recipe-pinned installer language" => Get("LanguageSourceRecipe"),
        "Zero Install application feed" => Get("LanguageSourceZeroInstall"),
        "Preserved by GOG Galaxy's staged updater" or "Preserved by GOG Galaxy's updater" => Get("LanguageSourceGogGalaxy"),
        "Preserved by the application's updater" => Get("LanguageSourceNative"),
        "Preserved by Microsoft Store/MSIX package" => Get("LanguageSourceMsixStore"),
        "Installed WinRAR language file" => Get("LanguageSourceWinRar"),
        "Check failed before language could be verified" => Get("LanguageSourceFailed"),
        _ => source
    };

    public static string LanguageName(string language) => language.ToLowerInvariant() switch
    {
        "de" or "de-de" => Format("LanguageGerman", language),
        "en-us" or "en-gb" or "en" => Format("LanguageEnglish", language),
        "neutral" => Get("LanguageNeutral"),
        "provider-managed" => Get("LanguageProviderManaged"),
        "application-managed" => Get("LanguageApplicationManaged"),
        "unknown" => Get("Unknown"),
        _ => language
    };

    public static string PlatformValue(string value) => value.ToLowerInvariant() switch
    {
        "release" => Get("ChannelRelease"),
        "stable" => Get("ChannelStable"),
        "native" => Get("ChannelNative"),
        "application-managed" => Get("PlatformApplicationManaged"),
        "provider-selected" => Get("PlatformProviderSelected"),
        "unknown" => Get("Unknown"),
        _ => value
    };

    public static string EvidenceLabel(string label)
    {
        const string integrationPrefix = "MSIX integration · ";
        if (label.StartsWith(integrationPrefix, StringComparison.Ordinal))
        {
            return Format("EvidenceIntegrationFormat", EvidenceLabel(label[integrationPrefix.Length..]));
        }
        return label switch
        {
            "Uninstall registry" => Get("EvidenceLabelUninstallRegistry"),
            "Registry version" => Get("EvidenceLabelRegistryVersion"),
            "Install location" => Get("EvidenceLabelInstallLocation"),
            "Install date" => Get("EvidenceLabelInstallDate"),
            "Install or update date fallback" => Get("EvidenceLabelInstallOrUpdateFallback"),
            "Display icon path" => Get("EvidenceLabelDisplayIcon"),
            "Uninstall command" => Get("EvidenceLabelUninstallCommand"),
            "Installer technology" => Get("EvidenceLabelInstallerTechnology"),
            "Windows shortcut" => Get("EvidenceLabelWindowsShortcut"),
            "Shortcut arguments" => Get("EvidenceLabelShortcutArguments"),
            "Zero Install feed" => Get("EvidenceLabelZeroInstallFeed"),
            "Selected implementation" => Get("EvidenceLabelSelectedImplementation"),
            "Implementation path" => Get("EvidenceLabelImplementationPath"),
            "Resolved application executable" => Get("EvidenceLabelResolvedExecutable"),
            "Executable version" => Get("EvidenceLabelExecutableVersion"),
            "Executable product" => Get("EvidenceLabelExecutableProduct"),
            "MSIX package family" => Get("EvidenceLabelMsixFamily"),
            "MSIX package version" => Get("EvidenceLabelMsixVersion"),
            "MSIX package architecture" => Get("EvidenceLabelMsixArchitecture"),
            "MSIX package role" => Get("EvidenceLabelMsixRole"),
            "Externally registered executable" => Get("EvidenceLabelExternalExecutable"),
            "Registered Windows extensions" => Get("EvidenceLabelWindowsExtensions"),
            "MSIX installed location" => Get("EvidenceLabelMsixLocation"),
            "Attached MSIX integration package" => Get("EvidenceLabelAttachedIntegration"),
            "Management policy" => Get("EvidenceLabelManagementPolicy"),
            "Blocked providers" => Get("EvidenceLabelBlockedProviders"),
            "Preferred update provider" => Get("EvidenceLabelPreferredProvider"),
            "Removal method" => Get("EvidenceLabelRemovalMethod"),
            _ => label
        };
    }

    public static string EvidenceValue(string label, string value)
    {
        if (label == "Management policy")
        {
            var separator = value.IndexOf(" · ", StringComparison.Ordinal);
            var id = separator > 0 ? value[..separator] : value;
            return separator > 0 ? $"{id} · {PolicyReason(id, value[(separator + 3)..])}" : value;
        }
        if (label == "Preferred update provider")
        {
            return value switch
            {
                "zero-install" or "Zero Install native feed" => Get("PreferredZeroInstall"),
                "github:nextcloud-releases/desktop" or "Official Nextcloud GitHub release and signed MSI" => Get("PreferredNextcloud"),
                "native-updater" => Get("ProviderNative"),
                "Blizzard native updater" => Get("PreferredBlizzard"),
                "No automatic provider" => Get("PreferredNone"),
                "Brave native update channel" => Get("PreferredBrave"),
                _ => value
            };
        }
        if (label == "Blocked providers")
        {
            return string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ProviderPolicyName));
        }
        if (label.EndsWith("MSIX package role", StringComparison.Ordinal) &&
            value == "External application integration (manifest-only registration package)")
        {
            return Get("MsixIntegrationRole");
        }
        if (label == "Removal method" && Enum.TryParse<RemovalKind>(value, out var removalKind))
        {
            return RemovalMethod(removalKind);
        }
        return value;
    }

    public static string ProviderPolicyName(string providerId) => providerId switch
    {
        "winget-fallback" or "WinGet fallback" => Get("ProviderWingetFallback"),
        "zero-install" => Get("ProviderZeroInstall"),
        "native-updater" => Get("ProviderNative"),
        _ when providerId.StartsWith("github:", StringComparison.Ordinal) =>
            Format("ProviderGitHub", providerId[7..]),
        _ => providerId
    };

    public static string RemovalMethod(RemovalKind kind) => kind switch
    {
        RemovalKind.WindowsInstaller => Get("RemovalWindowsInstaller"),
        RemovalKind.MsixPackage => Get("RemovalMsix"),
        RemovalKind.ZeroInstall => Get("RemovalZeroInstall"),
        _ => Get("RemovalRegistered")
    };

    public static string PolicyReason(string id, string fallback) => id switch
    {
        "DeepL.DeepL" => Get("PolicyDeepL"),
        "Nextcloud.NextcloudDesktop" => Get("PolicyNextcloud"),
        "Blizzard.BattleNet" => Get("PolicyBattleNet"),
        "CreativeTechnology.OpenAL" => Get("PolicyOpenAL"),
        "Brave.BraveOrigin.Nightly" => Get("PolicyBrave"),
        _ => fallback
    };

    [GeneratedRegex(@"content digest (?<digest>[^.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();

    [GeneratedRegex(@"reports (?<packaged>[^,]+), but Firefox actively requests (?<effective>[^.]+)\. The [^.]+ installer will be used\. Mozilla SHA-256 verified source: (?<path>.+)\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirefoxLanguageRegex();
}
