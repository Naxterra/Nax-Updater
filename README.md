# NaxUpdater

![NaxUpdater icon](src/NaxUpdater/Assets/AppIcon-128.png)

NaxUpdater is a native Windows 11 application for discovering and updating installed software from independent Windows and vendor evidence instead of trusting a single package-manager catalog.

## Downloads

GitHub Releases provide:

- localized x64 MSI installers for English and German;
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

Version 0.8.1 includes:

- Mozilla Firefox releases from Mozilla's official product-details and release archive, preserving the effective Firefox profile language, architecture, channel, scope, and installation directory;
- Nextcloud releases from the official `nextcloud-releases/desktop` GitHub repository using its release-asset SHA-256 digest and a multi-language MSI;
- DeepL updates through its existing Zero Install feed and content-addressed provider;
- explicit native-updater ownership for guarded applications such as Battle.net and Brave Origin;
- capability-based discovery of standard `app-update.yml` metadata shipped with installed applications, supporting generic HTTPS and GitHub feeds without application-name recipes;
- SHA-512 verification from those installed update feeds in addition to SHA-256 and Authenticode verification;
- exact MSI product-code correlation against WinGet's signed local catalog index without using WinGet's installed-version or path detection;
- fresher-version comparison with Scoop manifests, with official same-host installer derivation allowed only when a vendor publishes a matching `SHASUMS256.txt`;
- trusted signer inheritance from already-installed, validly signed executables when an installed updater configuration omits its publisher;
- unique normalized name-and-publisher catalog correlation when no stable package identifier is available; ambiguous matches are rejected and weak matches never produce an install button;
- a complete assessment result for every visible application, including an explicit **No verifiable update source** status instead of silently omitting unsupported software;
- registered non-MSI product-code correlation for installer technologies such as Inno Setup;
- catalog comparison against the highest credible executable and registered package version, preventing false repeated updates when an executable embeds an older component version;
- explicitly labelled SHA-256-only update plans for unsigned vendor installers, limited to exact registered product-code matches;
- exact MSIX package-family correlation against catalog PFNs;
- Microsoft Store/MSIX ownership classification for unmatched packaged applications, including application-managed language and Store/MSIX servicing status instead of unknown/unsupported labels;
- SHA-256-enforced HTTPS mirror redirects for exact catalog plans, supporting vendors such as LibreOffice that rotate downloads across official mirrors;
- MSI execution without process-name prechecks, preventing unrelated runtimes with the same filename from blocking Windows Installer updates.

Catalogs provide candidates, never installed state. NaxUpdater still decides the installed version, location, architecture, channel, and scope from its independent inventory. A package-manager match is accepted only through a stable identifier such as an exact MSI product code; name-only search results are not installable.

The single **Scan and check updates** action rebuilds the installed-application list and then checks every supported provider. The **Updates (N)** control shows both available updates and the checked/installed coverage; it only switches to the results and never starts a second check.

## Interface

- Native WinUI 3 dark mode with restrained blue, violet, green, orange, and pink status accents.
- Complete English and German resources, selected from the Windows UI language by default.
- An in-app settings dialog stores the language and verification-banner preferences.
- The verification information banner can be closed permanently and restored from Settings.
- Installed applications can be sorted by clicking the **Application** or **Installed / updated** column header. Clicking the active header reverses direction; unknown dates remain last.
- The former confidence/safety summary and list column are intentionally omitted from the user interface.
- Installable updates expose a compact **Update** button directly in their list row; clicking it immediately downloads, verifies, and starts the update without a duplicate confirmation dialog.
- Running-application warnings use the non-modal status bar. Supported MSI and installed-metadata installers run silently after the explicit row-button click; Windows UAC remains available when elevation is required.

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
./scripts/package-release.ps1 -Version 0.8.1
```

The desktop project uses .NET 11, WinUI 3, and the Windows App SDK. It is not an Electron or WebView application.

## Icon source

The generated project icon master is stored at `src/NaxUpdater/Assets/AppIcon-Master.png`. Derived PNG and multi-resolution ICO files can be regenerated with `tools/generate_icon_assets.py` using Pillow.
