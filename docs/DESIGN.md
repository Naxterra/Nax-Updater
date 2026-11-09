# Nax-Updater design

Current as of 0.17.74. Updated with every release; the reasons behind each rule are in [DECISIONS.md](DECISIONS.md).

## Goal

One window that finds every installed Windows application, tells the truth about whether it is current, and updates all of them with one click, from the best source for each one, without installing anything it cannot verify.

Hard requirements:

- **Update all means update all.** Every installable update is in "Alle aktualisieren"; the owner never has to click apps one by one. Driver updates are the one deliberate exception (see below).
- **Systemic fixes only.** A problem with one app is fixed in the rule that produced it, never with a per-app exception.
- **Elevation shows UAC.** Nothing that needs administrator rights runs silently without a prompt.
- **Scan speed comparable to WinGet or the Store** (about 10 s for roughly 150 applications).
- **Verified or not installed.** HTTPS, an allow-listed host, the publisher's hash and the expected Authenticode publisher; documented exceptions are below.

## Projects

| Project | Role |
|---|---|
| `src/NaxUpdater.Core` | Inventory, update providers, source selection, download verification, transactions. No UI. |
| `src/NaxUpdater` | WinUI 3 app (`Nax-Updater.exe`): views, localization (en-US, de-DE), presentation models. |
| `src/NaxUpdater/Configuration` | `update-providers.json` (local source library) and `application-policies.json` (per-app policy). Shipped with the app. |
| `installer` | WiX Burn bundle, one multilingual setup EXE. |
| `tests/NaxUpdater.Core.AlgorithmTests` | Deterministic regression tests with fake HTTP and fake providers. No installers run. |
| `tests/NaxUpdater.Core.SmokeTests` | Deterministic fixtures followed by live checks against this PC and real sources. |
| `scripts/package-release.ps1` | Build, test, package Setup.exe, portable ZIP and `SHA256SUMS.txt`. |

## Pipeline

1. **Inventory** (`ApplicationInventoryService`): registry uninstall entries, MSI, MSIX packages, shortcuts and executables are merged into one `InstalledApplication` per product, with evidence (install location, display icon, product codes, publisher) and a management mode.
2. **Ownership** (`ExternalManagementClassifier`, `ChocolateyOwnership`): who installed or updates the app (Store, Chocolatey, a vendor's own updater, the driver view). Ownership can block providers that must not touch the app.
3. **Checks** (`UpdateCheckService`): every compatible provider is asked, grouped into authority tiers that run in parallel. Each source has a timeout; a slow source fails alone.
4. **Selection**: one result per app, chosen by source order (below). All source results stay visible in the details panel.
5. **Execution** (`UpdateTransactionCoordinator`): revalidate, download and verify, close bound processes, install, then re-check the installed version. A journal and a lease make interrupted updates recoverable. The journal, like the Store search cache and the Kyocera cache, is written to a temporary file of its own, forced to disk and only then moved over the old file.

## While updates run

Inventory, provider and driver checks share one scan owner and cancellation token. The scan button cancels any read stage; navigation cannot start a second scan while the first is running. Completed application assessments appear incrementally and remain visible after cancellation. Update actions wait for a complete check so that companion and superseded-registration reconciliation still applies. **Retry failed / unfinished checks** keeps successful assessments from the same inventory and checks only applications that failed or have no result; those applications still use normal provider arbitration.

Retry also refreshes expired or invalid execution plans and statuses inferred from companion applications or newer registrations. Independent successful checks and valid plans keep their original evidence and expiry. Reconciliation runs once after merging retained and fresh results, so every installed sibling participates in the decision.

Store families share a fresh read-only batch query for installed-app updates. Only a complete successful no-update response replaces native per-SKU searches, after rereading queue state. Offers, failures and unknown responses use exact individual checks and bypass cached negatives. A scan starts this query together with the inventory and the driver check, every other check starts it before its per-app checks, and it may take 25 seconds. Canceling the scan ends it. Only the query's own time limit counts as running out of time; an answer that the Store gave late is still an answer. When the query runs out of time, no per-app queries are started and the failed apps are not rechecked within the same scan, because the Store answers those one at a time through the same service and they take longer still: an app with a "no update" answer from the last six hours stays current, and any other app is reported as not checked and can be checked again. Transaction revalidation uses individual checks.

Transaction revalidation of a Store package reads only that exact registered family, including its current policy and process bindings. It does not rescan unrelated registry applications or refresh conventional catalogs. WinGet MSI/WiX execution preserves an existing all-users installation through ALLUSERS=1 in both CLI and COM routes; the scope filter alone is insufficient for dual-purpose MSIs.

Driver transaction messages (including restart requirements and journal warnings) survive the closing rescan. Removal reports confirmed absence only after a successful inventory without read issues; a canceled, failed or incomplete read reports that removal could not be verified. A removal lasts until the uninstaller and every process it started have ended or the uninstall entry is gone, because some uninstallers (NSIS) copy themselves, start the copy and end their first process at once. A left-behind process counts as the uninstaller itself when it runs from a temp folder or is Windows Installer, and it is waited for as long as it runs. Any other left-behind process (a helper, a browser, another instance of the application) is waited for at most ten minutes after the last uninstaller process ended. The first process is waited for until it ends; only when the uninstall entry is already gone and the process is not Windows Installer (a game launcher that stays open) does the wait end after two more minutes, and the removal then counts as done without an exit code. An update that Windows finished on its own between the scan and the action counts as completed only when the app has reached the offered version. After a change the lists are read again; when that rescan is stopped, the earlier offers stay locked until the next check.

Application and update columns have synchronized draggable dividers and horizontal scrolling. Their widths, driver column widths, restored window dimensions and maximized state are saved in the local view preferences. Invalid preferences fall back to defaults, and restored dimensions are bounded by the current monitor's work area.

The update list stays reachable from the application view while a change runs, and the details panel follows the selected row's live state and failure reason. One gate covers every change to the machine: updates, Update All, driver installs and removals. While it is held no other change can start (removal is checked before its dialogs and again after confirmation, and holds the same transaction lease as updates). A language change is saved and the restart waits for the gate; closing the window is deferred the same way, and the window closes on its own afterwards.

An installer exit code 1641 or 3010 (restart requested) after a verified target counts as a completed update with a restart note, and Update All continues. Only an update whose target is not yet verified waits for the restart, and so does every driver install that asks for one, because Windows reports a staged driver version before the restart loads the driver. A waiting update blocks further changes until Windows has restarted (an application update also when its target is found installed).

The update and driver lists stay enabled, so they can be scrolled and every row's details read during an update; only the row actions and "Alle aktualisieren" are locked. Each row in a run shows its own state in the status column (in Warteschlange, wird vorbereitet, Anwendung wird geschlossen, wird installiert, wird geprüft, then aktualisiert, fehlgeschlagen, nicht mehr anwendbar, Neustart erforderlich or übersprungen). Outcomes stay on the rows after the closing rescan until the next manual scan; a failed row shows its reason above the provider notes. A row that is still installable after a reported success shows the remaining offer instead of "aktualisiert".

## Source order

Authorities (`UpdateProviderAuthority`), highest first:

| Authority | Value | Sources |
|---|---|---|
| ExplicitApplicationPolicy | 500 | `application-policies.json` names the provider |
| InstalledUpdateProtocol | 400 | the app's own update protocol (Electron `app-update.yml`, Nextcloud update server, Python install manager, Zero Install, GOG Galaxy, IVPN, WSL) |
| ProducerRelease | 300 | the publisher's official site (`official` recipes), Firefox, Node.js, Macrium, WinRAR, manufacturer drivers |
| RepositoryRelease | 250 | the publisher's GitHub releases (`github` recipes) |
| PlatformStore | 200 | Microsoft Store for MSIX packages |
| FallbackCatalog | 100 | WinGet (specificity 50), Chocolatey for Chocolatey-installed apps (30), Chocolatey community feed (25) |

Selection rule: the first source, in this order, that reports the installed version as current settles the app. A lower source can neither offer an installer nor turn the app into an error. Above that point, the highest installable result wins, unless a higher source failed (a lower source never bypasses a higher source's verification failure). A Store queue item always wins, so a platform-owned deployment is never duplicated.

A publisher's **staged rollout** that has not reached this PC is reported as current: the release is not released to this PC, so it is no update for it. The held version is named only in the details, and as a current result it settles the app, so no lower catalog may deliver it early.

A **superseded registration** (an entry named "Product - 12.0.30501" next to a current "Product - 12.0.40664" of the same name family, at or above the version offered for the older one) is shown as current with the provider "Neuere installierte Registrierung". Burn bundles such as Visual C++ redistributables register themselves even when all their packages are obsolete.

## Local source library

`update-providers.json` records, per app, where the publisher lists versions and how to download and verify the installer, so nothing is searched at run time.

- `github`: repository, asset pattern, expected signer. Options: `unsigned` (GitHub asset digest only), `followInstalledPrerelease`, `privateRepository` (asset resolved through the API with the `gh` CLI token, sent only to `api.github.com`), `releaseTagPrefix`.
- `official`: version page or feed, a regex for versions (highest wins), download and hash locations, expected signer, allowed hosts. Options: `versionFormat`, `downloadPageUrl`/`downloadPattern`, `fileName`, `nestedInstaller` (MSI inside a ZIP), `hashUrl`/`hashPattern`/`hashEncoding`, `rolloutPattern`, `stayOnInstalledMajor`, `architecture: neutral`, `allowMirrorRedirects`, `alwaysElevate`, `unsigned`, `signatureOnly`, `verifyInstallerVersion`.

A `github` recipe matches the registered name exactly, followed at most by a trailing version. A recipe that also sets `currentUserInstallerArguments` accepts the scope and bitness marks that Inno Setup appends in English or German ("(Aktueller Benutzer)"), because only installers that let the user choose the scope add them. `installerArguments` serve an all-users installation (elevated when `requiresElevation` is set) and `currentUserInstallerArguments` a per-user one, so an update keeps the installation in its scope. An installation whose scope no argument set serves is reported as not applicable.

Every recipe is added only after a real download was verified against its hash and signer. When a publisher's signer changes, the update fails with "expected signer ..., found ..." and the pin is changed only after the new certificate is matched to the publisher's own legal records.

## Verification

`UpdatePackageDownloader` and `UpdatePlanValidator` require, for downloaded installers:

- HTTPS, and every redirect target in the plan's allowed hosts (mirrors only with `allowMirrorRedirects`, and then the hash must match);
- the publisher's SHA-256 or SHA-512;
- the expected Authenticode publisher.

Payload downloads use a dedicated transport with automatic redirects disabled. Each normal, fallback and ranged request checks the next URL before sending it, follows at most ten redirects, and rejects HTTP downgrades. Signature-only recipes cannot opt out of host validation. The parts of an interrupted large download are reused only for the same file: the one with the release hash of the plan, or without a hash the same server file (its ETag or Last-Modified and length); otherwise the download starts again. After a check in which every source answered and the inventory read all of its sources, cached installers that no current offer needs are deleted; driver packages are never deleted, and the sweep neither reads nor deletes through a junction or link in the cache. A version becomes its cache folder name the way Windows stores it, without dots or spaces at the end. Version pages, feeds and API answers are read through a size limit (16 MB, 8 MB for version pages), also when the server names no length.

A same-version repair (Chocolatey reinstalling a version that was installed outside it, so its record catches up) is proven only by a successful execution and matching Chocolatey records, never by the unchanged application version. A Chocolatey wrapper route names both the package it runs (`nodejs 9.0.0`) and the payload the application belongs to (`nodejs.install 2.0.0`); both records are checked afterwards.

Versions are compared by their numbers first. With equal numbers, only a known prerelease word (alpha, beta, rc, preview, dev, nightly and similar, or `a`, `b`, `m` followed by a number of one or two digits) makes a version older than the release. The unmistakable words (alpha, beta, rc, candidate, preview, prealpha, nightly, snapshot, canary, CTP) count wherever they stand after the numbers, so "3.0 Release Candidate 1" and "1.2.3-x64-beta1" are prereleases. Three or more digits after a single letter are a build number ("1.2.3 b456") and do not change the order. A single letter or a patch word with a number ("7.1a", "3.0 SP1", "9.0u1") makes it newer. Any other text after the numbers, including text in parentheses, does not change the order, so an installed "2.6.12-I001" is not older than "2.6.12". Chocolatey package IDs and versions are accepted for the elevated command only when they consist of the characters of NuGet IDs and versions; a value that ends in a line feed is refused, as it is for WinGet.

Exceptions, each modelled explicitly in the plan:

- **Unsigned publisher** (`unsigned`): the hash alone, and only when the publisher ships no signed builds.
- **Signature only** (`signatureOnly`, `SignatureOnly` plan flag): for publishers that publish no hash. The exact pinned signer is required; the validator accepts this only with `RequireAuthenticode` and a named signer. A driver package qualifies only as a signed installer from the manufacturer's own HTTPS server (TP-Link's `DownloadedZipExe`), never as a bare INF without a hash. For a ZIP the nested MSI's signer is checked before it runs. Payloads the signature does not cover (external unsigned cabinets) are not allowed. A signature proves the publisher but not the version, so when the recipe does not build the download URL from the version through an exact `{version}` or `{version_}` placeholder or version parts that a download-page pattern cannot overwrite (or when the recipe sets `verifyInstallerVersion`), the installer's own version resource must match the approved version before it runs, and a cached signature-only installer is never reused. Mirror redirects are allowed only for an installer with a published hash. A signer matches when the expected name equals the whole subject name (CN) or organization (O) of the signing certificate; the letter case, repeated spaces and a final period are ignored. A part of the name, or text from another field of the subject, does not match.
- **WinGet and Store**: the platform owns download integrity; Nax-Updater validates the exact package identity, architecture, installer type and locale before submitting. A queued Store update is shown as Windows' own queue item for the exact package family, without an installer hash.

After a verified successful update, its cached installer is deleted with its empty folders, because it can never be needed again. Driver packages are kept for rollbacks.

## Staged rollouts

Electron applications use electron-updater, which keeps a random UUID in `%APPDATA%\<app>\.updaterId` and offers a release with `stagingPercentage: N` only when the UUID's last four bytes, read big-endian and divided by `0xFFFFFFFF`, are below `N / 100`. `ElectronStagedRollout` reads the same file (folder named after the executable's product name or its install folder) and applies the same rule, in both the official-site provider and the installed-metadata provider. Without an ID the release waits until the rollout is complete. Until this PC is included the app is shown as "Aktuell"; the details name the held version (`AnnouncedVersion`) and the percentage at which this PC is included (`RolloutThresholdPercent`).

## Drivers

Intel download checks validate the exact download record and release metadata, with an official regional-page fallback for blocked English pages. If neither HTTP source is usable, the existing browser transport reads only the four exact supported main-site download pages, with the same record validation and deadline. TLS and DNS policy remain enforced. Ethernet checks the exact Windows 11 I219 INF inside Intel's current archive, verifies its published SHA-256/SHA-512 and retains the archive by hash; every scan rereads release metadata. It never infers a device version from an umbrella package number.

MSI, Dell and Razer use a bounded hidden WebView query when an HTTP request does not yield a valid catalog. Only their exact supported public catalog addresses are allowed. The query has an isolated profile, disables cached responses, denies downloads and permissions, does not take the normal app's single-instance lock, and exits after returning UTF-8 content. Core parsing verifies the model, OS and actual catalog records. Queries are shared only within a scan and their process tree is stopped on cancellation or deadline.

MSI audio requires the exact motherboard and hardware subsystem. Its reviewed Realtek 6.0.10007.1 archive can be installed individually in the Drivers view when the live catalog still publishes its exact SHA-256. The complete installer directory is extracted with safe paths, locked against replacement and bound to the approved execution fingerprint; the nested EXE must carry Realtek's exact trusted signature. MSI's supplied silent response file disables automatic restart. Changed archives remain source-only until their installer recipe is reviewed. Verification and interrupted-operation recovery require the same present audio device, Realtek provider and exact target version; the Microsoft inbox driver's 10.x version cannot prove Realtek's 6.x target. Dell INF, HID and firmware versions stay separate. Razer firmware versions come from the current catalog. NVIDIA HD Audio uses the current graphics release's published PDF module table. VMware and Xvdd show their servicing package's live catalog version explicitly as a package, retaining exact driver versions separately. Xbox Service's synthetic controller is checked against its local bundled INF; no independent latest-version claim is made. WD guidance is reread to confirm that legacy SES drivers are inappropriate on modern Windows.

The driver summary counts successful live manufacturer checks. A retained release reference or support link does not establish that a driver is current. Results carry their evidence separately from update availability, so a verified older applicable package can remain available without being counted as a fresh latest-release check. Failed catalog reads, including fallbacks to reference data, appear in the problem count and filter. HTTP success alone does not establish catalog content: mapped Razer firmware names and versions must also be present.

Drivers have their own view (Treiber) and are never part of "Alle aktualisieren". The update list shows a driver row only as information ("Treiberupdate verfügbar · im Treiber-Bereich", with the driver version when it uses the package's version scheme). Manufacturer sources are queried directly, each request capped at 10 s. A scan checks the drivers once, starting together with the application inventory; the driver rows of the update list and the driver view use that result, and opening the driver view within five minutes of the scan shows it at once. "Herstellertreiber prüfen", the check after a driver install and any change to the system run a new check. Kyocera is asked once per scan, and its files are kept with their ETag, so an unchanged file comes back as 304 without its body. A connected device's driver (version, INF, provider, date) is read from its own driver key; Win32_PnPSignedDriver is not queried, because it reported replaced drivers for over a minute and took 1.7 s per read. A package whose name says it is a driver ("... Driver", "...treiber") never becomes installable through WinGet or Chocolatey either; it is shown as a known newer release. A database driver (ODBC, JDBC, OLE DB, or a database word directly before the word) and a driver tool with one of these whole names (Display Driver Uninstaller, Driver Booster, Driver Easy, Driver Genius, Driver & Support Assistant, Windows Driver Kit) are ordinary programs and keep their catalog update. Any other name with the word counts as a driver.

Network printers have no present PnP device, so the KX drivers of Kyocera printers are read from the print spooler's driver list (MSFT_PrinterDriver), and the printer model is taken from the driver name. Kyocera's global user support site publishes one JSON file per model with every driver and utility, its version and its download address. The KX driver is checked there, and a newer one is offered as the exact download page, because Kyocera publishes no package hash. Kyocera's utilities in the application list (TWAIN driver, Status Monitor, Quick Scan, Wi-Fi Setup Tool) are checked against the same model file.

Every download sends a User-Agent, because CDNs such as Intel's download mirror refuse requests without one (403).

## Process and package context

- Starting an update authorizes closing only the processes bound to the fresh plan's executable paths.
- Nax-Updater must not run inside another application's process job or MSIX package context: a non-elevated installer would inherit it and its per-user registry writes could be redirected into that package's private hive. `UpdateHostLifetime` relaunches through the desktop shell when it is job-bound.
- One instance only (`Local\NaxUpdater.SingleInstance`).

## Speed

- Tiers run in parallel; sources within a tier run in priority order and stop at the first installable answer.
- Native Store "no update" answers are cached for 6 h (`StoreSearchCache`), keyed by package family, installed version and catalog version. Errors, offers and queue items are never cached.
- Chocolatey feed requests: 8 s per attempt, one retry on a fresh connection.
- A Store item that shows no change (state or progress) for 3 minutes is released: Nax-Updater stops waiting, reports it as not completed, and moves on; the item stays in the Store queue. Installing and restoring phases never count as stalled, and an item that keeps progressing gets the full 20 minutes.
- WinGet's COM server exits after installing a new catalog package; refresh and assessment reconnect once on `RPC_S_CALL_FAILED` or `RPC_S_SERVER_UNAVAILABLE`.

## Interface language

The checks and the update engine write their messages in English. The German interface shows every such message through the message catalog (`MessageCatalog.de.txt` in the core library), which holds a German version of each English sentence template with `{0}`, `{1}` and so on for inserted versions and names. A finished message is translated piece by piece, so messages built from several sentences and values are translated as well, and the inserted values are translated when the catalog knows them. Names, commands and identifiers are listed as `same:` and stay unchanged. A test reads every message literal in the core sources and fails when one has no catalog entry, so a new English message cannot ship untranslated.

## Naming

The product and executable are `Nax-Updater`; internal identifiers (namespaces, mutex, AppUserModelId, `%LOCALAPPDATA%\NaxUpdater`, User-Agent, release asset names) stay `NaxUpdater`.

## Release process

GitHub Actions is disabled; releases are built and verified locally.

1. Bump the version everywhere it appears (`git grep` the old version: README, bundle project, packaging script, app project, User-Agent strings).
2. Packaging refuses a `-Version` that differs from the project version and checks the published `Nax-Updater.exe` version. It never overrides versions with `-p:Version`, which would renumber `NaxUpdater.Core.dll` downwards. Algorithm, transaction and smoke tests must all pass; live smoke checks assert the shape of this machine's state, not past versions.
3. Package, push, tag `vX` (annotated), `gh release create` with Setup.exe, portable ZIP and `SHA256SUMS.txt`.
4. Download the published assets, verify `SHA256SUMS.txt`, install with `/passive`, verify the installed version and commit, and compare the installed files with the published files (all but the `.pdb` files must be present).
5. Update this file and [DECISIONS.md](DECISIONS.md).
