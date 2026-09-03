# NaxUpdater

![NaxUpdater icon](src/NaxUpdater/Assets/AppIcon-128.png)

NaxUpdater is a native Windows 11 application for discovering and updating installed software from independent Windows and vendor evidence instead of trusting a single package-manager catalog.

## Downloads

GitHub Releases provide:

- one multilingual x64 setup EXE with English/German language selection and a fixed native bootstrapper window; this is the recommended interactive installer;
- localized x64 MSI packages for enterprise or command-line deployment, with their internal UI disabled by the setup EXE;
- a self-contained x64 portable ZIP;
- SHA-256 checksums for every release asset.

The MSI installs the application icon, creates Start Menu and Desktop shortcuts by default, registers the app in Windows, and provides the correct running/taskbar icon. Windows users can pin the Start Menu shortcut to the taskbar normally; the installer does not force a taskbar pin.

## Inventory

The inventory engine:

- enumerates visible Win32 uninstall entries from both registry views and scopes;
- enumerates current-user MSIX/AppX packages through Windows APIs;
- keeps Windows system components available behind an explicit display toggle;
- resolves executable paths from registry metadata and native Windows shortcuts;
- reads executable file-version metadata;
- queries Zero Install in offline/read-only mode when an application is managed by it;
- recognizes manifest-only MSIX integration registrations and attaches them to their real Win32 application instead of listing a duplicate;
- applies provider guard policies only when their associated application is detected;
- displays the evidence and confidence behind every result.

## Update providers

Version 0.15.18 includes:

- ChatGPT checks against OpenAI's official Windows update manifest for the exact `OpenAI.Codex` package and Store product, instead of relying on WinGet's `Unknown` Store version;
- ChatGPT process detection before deployment, so an open ChatGPT/Codex instance produces a clear close-first message rather than a false success;
- exact Store-product retry with the Windows Package Manager force option when Store metadata proves an update but the composite update catalog is stale; `NoApplicableUpgrade` is no longer reported as “already current”;
- a full-width driver workspace matching the applications and updates views: the fixed centered 1260-pixel island is removed, the device-name column remains bounded, and surplus ultra-wide space follows the action column;

- Mozilla Firefox releases from Mozilla's official product-details and release archive, preserving the effective Firefox profile language, architecture, channel, scope, and installation directory;
- Nextcloud releases from the official `nextcloud-releases/desktop` GitHub repository using its release-asset SHA-256 digest and a multi-language MSI;
- retry and immutable latest-release redirect fallback for current Nextcloud checks when the GitHub JSON API is temporarily unavailable or rate-limited;
- DeepL updates through its existing Zero Install feed and content-addressed provider;
- resilient DeepL checks that fall back to Zero Install's locally cached signed selection when a feed refresh is temporarily unavailable, avoiding a false provider failure for the installed current release;
- explicit native-updater ownership for guarded applications such as Battle.net and Brave Origin;
- native-updater policy precedence over incidental public-catalog matches, preventing component versions such as Brave's Chromium build from appearing as a Brave application update;
- capability-based discovery of standard `app-update.yml` metadata shipped with installed applications, supporting generic HTTPS and GitHub feeds without application-name recipes;
- SHA-512 verification from those installed update feeds in addition to SHA-256 and Authenticode verification;
- trusted signer inheritance from already-installed, validly signed executables when an installed updater configuration omits its publisher;
- producer-owned GitHub CLI releases from `cli/cli`, using GitHub's release-asset SHA-256 digest, the exact x64 MSI, and the `GitHub, Inc.` Authenticode signer;
- producer-owned Git for Windows releases from `git-for-windows/git`, using GitHub's release-asset SHA-256 digest, the exact x64 installer, and Johannes Schindelin's Authenticode signer;
- WinRAR checks and language-matched installers directly from RARLAB's official download page; the original producer download is SHA-256 pinned during the check and must carry the `win.rar GmbH` Authenticode signature;
- no WinGet community index, WinGet manifest, or Scoop manifest in the production provider chain; applications without an official producer feed are shown honestly as **No verifiable update source**;
- a complete assessment result for every visible application, including an explicit **No verifiable update source** status instead of silently omitting unsupported software;
- registered non-MSI product-code correlation for installer technologies such as Inno Setup;
- vendor-native GOG Galaxy updates read from GOG's own `autoupdate-verified` state, with the staged updater version matched to its metadata and its GOG Authenticode signature validated; because Windows intentionally denies direct execution inside that protected directory, the complete vendor-verified tree is copied to a fresh temporary execution directory and merged with GOG's installed `redists` runtime dependencies without replacing the new updater, then the copied updater is revalidated and GOG's own elevated command is used;
- Microsoft Store/MSIX ownership classification for unmatched packaged applications, including application-managed language and Store/MSIX servicing status instead of unknown/unsupported labels;
- MSI execution without process-name prechecks, preventing unrelated runtimes with the same filename from blocking Windows Installer updates;
- direct Microsoft Store catalog and deployment integration through the official Windows Package Manager COM API;
- installed PFN to Store Product ID resolution with exact package-family revalidation before silent install/update submission;
- Store update applicability checks through an exact installed package-family → Store Product ID → architecture-matched Store package-version comparison, avoiding false negatives when the composite catalog omits its installed-version object or reports a stale applicability flag;
- conservative Store package comparison that excludes encrypted duplicate bundles and rejects unrelated outer-bundle version schemes, preventing satellite revisions such as `.70` and calendar/rank bundle versions from becoming false application updates;
- a direct **Update** row action only when Microsoft Store reports a real applicable upgrade; current or uncorrelated Store packages remain labelled as Store-managed without a misleading button;
- an exact Microsoft Store product-install fallback when package metadata proves a newer applicable version but the composite update catalog exposes no upgrade candidate; `NoApplicableUpgrade` remains a failure instead of being converted to a false current result;
- launchable MSIX app-entry naming ahead of package identities, so Store packages such as `OpenAI.Codex` are presented by their user-facing app name, `ChatGPT`;
- six-way parallel ranged downloads for installers of at least 64 MB when the server advertises byte-range support, followed by complete reconstruction and whole-file hash verification;
- SHA-256-verified ZIP/NuGet packages containing an explicitly declared nested MSI, with exact-entry extraction, traversal rejection, and silent MSI execution;
- a sequential **Update all** queue containing verified conventional updates followed only by Store/MSIX packages with a reported applicable upgrade, with shared Store connections and one final inventory/update rescan;
- honest bulk counts that keep proven newer conventional versions separate from confirmed Store updates;
- multi-signer Authenticode policies from installed updater metadata, accepting any explicitly declared trusted publisher identity;
- Store package migration matching through a unique exact product-name and publisher pair when the Store product intentionally publishes a replacement PFN;
- MSI upgrade-family correlation that collapses side-by-side product generations and retains the newest registered version, preventing a successful MSI install from being offered repeatedly because the vendor left an older product registration behind.
- twelve-way bounded Store catalog checking, reducing the complete local application scan and update check from roughly 27 seconds to about 7–8 seconds on the validated workstation without omitting packages.
- version/architecture-aware MSIX integration correlation, attaching WinRAR's shell-extension package to the real Win32 installation instead of displaying a duplicate source-less package row;
- equivalent compact and dotted date-release versions such as PotPlayer `260819` and `26.08.19.0` are treated as the same release;

## Manufacturer drivers

The native **Drivers / Treiber** view does not use Windows Update. It correlates present PnP hardware with signed-driver data and retained installed INF registrations, then groups interface records into physical-device or driver-package rows.

- NVIDIA GeForce RTX 50-series desktop drivers are checked directly against NVIDIA's official WHQL Game Ready catalog.
- A newer NVIDIA driver receives an install button only after NaxUpdater obtains NVIDIA's official installer URL and published SHA-256 sidecar; the downloaded package must also carry the `NVIDIA Corporation` Authenticode publisher.
- The manufacturer installer remains visible so component choices stay under user control. NaxUpdater handles download verification, elevation, exit codes, restart reporting, and the post-install rescan.
- Realtek RTL8125 Ethernet is compared by its applicable driver branch rather than the catalog publication date, so installed `10.80.50.407` is correctly current for package branch `10.80.50`; Realtek's CAPTCHA-protected download remains an exact source action only for a genuinely newer branch.
- Intel I219-V is compared with the exact Windows 11 `e1d.inf` payload rather than the unrelated umbrella package number. A genuine advance receives a SHA-256-verified ZIP plan that revalidates the hardware ID, INF version, and Microsoft WHCP catalog before elevated `pnputil` installation.
- TP-Link hardware ID `USB\VID_3625&PID_010A` is retained when disconnected, mapped to Archer TBE400UH, and compared with its exact TP-Link hardware-version page.
- TP-Link's public `5002` package version is projected to the installed Windows 11 `5102` INF branch before comparison, avoiding a false downgrade display.
- Dell hardware ID `MONITOR\DELA1E4` maps to the exact AW3423DW `M46J9` monitor-driver page rather than generic Dell support.
- Dell's `A00-00` package label is kept separate from its installed `1.1.0.0` INF version.
- Present WD Elements hardware is shown with Western Digital's current SES guidance: Windows 11 keeps the Microsoft storage driver, required SES support installs automatically, the downloadable SES installer is explicitly legacy, and the official optional WD tools remain linked.
- All Razer HID/interface records are collapsed into one Razer category. The installed Synapse version and Razer's public firmware catalog are checked once; matching Huntsman V3 Pro 8KHz, Kiyo Pro, and Nommo Pro firmware tools are reported without pretending that HID driver versions are firmware versions.
- Intel devices are grouped by actual package boundary: Chipset Device Software, Management Engine components, Rapid Storage Technology, and exact Ethernet. The latest public Intel package version is shown without comparing an umbrella package number to unrelated component INF versions.
- BIOS, UEFI, device firmware, beta drivers, and Windows Update driver packages are excluded.
- Large segmented downloads are resumable. Completed segments survive interruption, merge progress is displayed separately from download progress, and a freshly downloaded file is not hashed twice before signature verification.
- Independent manufacturer source checks run concurrently, and the driver table distinguishes verified installed vendor-software ownership from rows that only have an official source and still require hardware applicability validation. Neither state claims a manually installed driver is outdated.
- The driver workspace fills maximized and ultrawide windows while the device-name column remains capped, keeping surplus width after the action column instead of centering a narrow table island.
- Applications and Updates now use the same full-width table/details workspace. Their name columns share the same responsive 280–600 px constraint, while surplus ultrawide space is placed after the last data/action column instead of inside the application name.

Provider authority is producer-owned: installed native/self-updaters and signed updater metadata, official vendor download feeds, producer-controlled GitHub releases, and Microsoft Store only for Store-owned packages. Community package-manager catalogs are not consulted.

The single **Scan and check updates** action rebuilds the installed-application list and then checks every supported provider. The **Updates (N)** control shows both available updates and the checked/installed coverage; it only switches to the results and never starts a second check.

The shared search field remains active in both the installed-applications and update-provider views. Update results can be filtered by application name, installed or available version, provider, language, status, and provider notes without changing which applications are included by **Update all**.

## Interface

- Native WinUI 3 dark mode with restrained blue, violet, green, orange, and pink status accents.
- Installed applications and update results prefer MSIX manifest artwork, then registered display icons, resolved executables, shortcuts, top-level application artwork, and Shell icons; a consistent neutral application glyph remains only when every source blocks access.
- Complete English and German resources, selected from the Windows UI language by default.
- An in-app settings dialog stores the language and verification-banner preferences.
- The Settings dialog includes a localized **About / Über** section with the running application version and a link to the project repository.
- A localized manufacturer-driver view shows device, installed version, available version, official source, status, and the safe action supported by that manufacturer.
- Manufacturer-driver rows can be filtered by status without losing the existing free-text filter.
- Update results and manufacturer-driver rows can be sorted by clicking **Name** or **Status**; clicking the active column reverses its direction.
- one multilingual WiX Burn setup EXE contains English and German setup resources, selects the Windows UI language automatically, and also supports Burn's `-lang 1033` / `-lang 1031` override; the app language remains directly selectable in Settings. The setup wrapper uses the unaffected WiX 4 Burn icon path because WiX 5/6 drops the title-bar and taskbar icon; the app window resolves its icon from the absolute installation path.
- The verification information banner can be closed permanently and restored from Settings.
- Installed applications can be sorted by clicking the **Application** or **Installed / updated** column header. Clicking the active header reverses direction; unknown dates remain last.
- The former confidence/safety summary and list column are intentionally omitted from the user interface.
- Installable updates expose a compact **Update** button directly in their list row; clicking it immediately downloads, verifies, and starts the update without a duplicate confirmation dialog.
- Running-application warnings use the non-modal status bar. Supported MSI and installed-metadata installers run silently after the explicit row-button click; Windows UAC remains available when elevation is required.
- Details panes are width-capped so ultrawide windows devote their extra space to the application list instead of an empty details column.

MSIX app-list resources and package-directory identities are used to replace opaque package GUIDs with meaningful names. Dates are labelled **Installed / updated** because Windows and MSI can replace the original installation date during servicing; when no reported date exists, NaxUpdater can show the installation folder's modification date as an explicitly identified fallback.

NaxUpdater never falls back to an English Firefox installer when the detected locale is unavailable. An update is blocked instead.

## Installation safety

- Checks are read-only and never start installation automatically.
- Every installation requires an explicit confirmation.
- Downloaded installers require HTTPS, an allow-listed final host, the release SHA-256, a valid Windows Authenticode signature, and the expected publisher name.
- The application, architecture, channel, locale, scope, and install directory are fixed in the execution plan.
- Running applications must be closed manually; NaxUpdater never terminates them.
- NaxUpdater does not run bulk WinGet, Chocolatey, or Scoop commands and never uninstalls without exact user confirmation.

## Application removal

- Removable applications expose **Uninstall / remove** in the details pane.
- NaxUpdater uses only the registered Windows uninstaller, an MSI product code, Zero Install, or the native MSIX deployment API.
- Raw installation folders are never deleted.
- Removal requires two confirmations; the second requires typing the exact application name.
- Protected Windows system components and entries without a verifiable removal method remain disabled.

## Build

From this directory:

```powershell
dotnet build NaxUpdater.slnx
dotnet run --project tests/NaxUpdater.Core.SmokeTests/NaxUpdater.Core.SmokeTests.csproj
dotnet publish src/NaxUpdater/NaxUpdater.csproj -c Release -r win-x64 --self-contained true -o artifacts/NaxUpdater-win-x64
./scripts/package-release.ps1 -Version 0.15.18
```

The desktop project uses .NET 11, WinUI 3, and the Windows App SDK. It is not an Electron or WebView application.

## Icon source

The generated project icon master is stored at `src/NaxUpdater/Assets/AppIcon-Master.png`. Derived PNG and multi-resolution ICO files can be regenerated with `tools/generate_icon_assets.py` using Pillow.
