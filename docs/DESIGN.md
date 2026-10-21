# Nax-Updater design

Current as of 0.17.28. Updated with every release; the reasons behind each rule are in [DECISIONS.md](DECISIONS.md).

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
5. **Execution** (`UpdateTransactionCoordinator`): revalidate, download and verify, close bound processes, install, then re-check the installed version. A journal and a lease make interrupted updates recoverable.

## While updates run

One gate covers every change to the machine: updates, Update All, driver installs and removals. While it is held no other change can start (removal is checked before its dialogs and again after confirmation, and holds the same transaction lease as updates). A language change is saved and the restart waits for the gate; closing the window is deferred the same way, and the window closes on its own afterwards.

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
- `official`: version page or feed, a regex for versions (highest wins), download and hash locations, expected signer, allowed hosts. Options: `versionFormat`, `downloadPageUrl`/`downloadPattern`, `fileName`, `nestedInstaller` (MSI inside a ZIP), `hashUrl`/`hashPattern`/`hashEncoding`, `rolloutPattern`, `stayOnInstalledMajor`, `architecture: neutral`, `allowMirrorRedirects`, `alwaysElevate`, `unsigned`, `signatureOnly`.

Every recipe is added only after a real download was verified against its hash and signer. When a publisher's signer changes, the update fails with "expected signer ..., found ..." and the pin is changed only after the new certificate is matched to the publisher's own legal records.

## Verification

`UpdatePackageDownloader` and `UpdatePlanValidator` require, for downloaded installers:

- HTTPS, and every redirect target in the plan's allowed hosts (mirrors only with `allowMirrorRedirects`, and then the hash must match);
- the publisher's SHA-256 or SHA-512;
- the expected Authenticode publisher.

A same-version repair (Chocolatey reinstalling a version that was installed outside it, so its record catches up) is proven only by a successful execution and matching Chocolatey records, never by the unchanged application version. A Chocolatey wrapper route names both the package it runs (`nodejs 9.0.0`) and the payload the application belongs to (`nodejs.install 2.0.0`); both records are checked afterwards.

Exceptions, each modelled explicitly in the plan:

- **Unsigned publisher** (`unsigned`): the hash alone, and only when the publisher ships no signed builds.
- **Signature only** (`signatureOnly`, `SignatureOnly` plan flag): for publishers that publish no hash. The exact pinned signer is required; the validator accepts this only with `RequireAuthenticode` and a named signer, never for drivers. For a ZIP the nested MSI's signer is checked before it runs. Payloads the signature does not cover (external unsigned cabinets) are not allowed.
- **WinGet and Store**: the platform owns download integrity; Nax-Updater validates the exact package identity, architecture, installer type and locale before submitting.

## Staged rollouts

Electron applications use electron-updater, which keeps a random UUID in `%APPDATA%\<app>\.updaterId` and offers a release with `stagingPercentage: N` only when the UUID's last four bytes, read big-endian and divided by `0xFFFFFFFF`, are below `N / 100`. `ElectronStagedRollout` reads the same file (folder named after the executable's product name or its install folder) and applies the same rule, in both the official-site provider and the installed-metadata provider. Without an ID the release waits until the rollout is complete. Until this PC is included the app is shown as "Aktuell"; the details name the held version (`AnnouncedVersion`) and the percentage at which this PC is included (`RolloutThresholdPercent`).

## Drivers

Drivers have their own view (Treiber) and are never part of "Alle aktualisieren". The update list shows a driver row only as information ("Treiberupdate verfügbar · im Treiber-Bereich", with the driver version when it uses the package's version scheme). Manufacturer sources are queried directly, each request capped at 10 s. NVIDIA desktop GeForce RTX cards are checked against the WHQL Game Ready list of their own series, whose ID is read by name from NVIDIA's series list.

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

## Naming

The product and executable are `Nax-Updater`; internal identifiers (namespaces, mutex, AppUserModelId, `%LOCALAPPDATA%\NaxUpdater`, User-Agent, release asset names) stay `NaxUpdater`.

## Release process

GitHub Actions is disabled; releases are built and verified locally.

1. Bump the version everywhere it appears (`git grep` the old version: README, bundle project, packaging script, app project, User-Agent strings).
2. Packaging refuses a `-Version` that differs from the project version and checks the published `Nax-Updater.exe` version. It never overrides versions with `-p:Version`, which would renumber `NaxUpdater.Core.dll` downwards. Algorithm, transaction and smoke tests must all pass; live smoke checks assert the shape of this machine's state, not past versions.
3. Package, push, tag `vX` (annotated), `gh release create` with Setup.exe, portable ZIP and `SHA256SUMS.txt`.
4. Download the published assets, verify `SHA256SUMS.txt`, install with `/passive`, verify the installed version and commit, and compare the installed files with the published files (all but the `.pdb` files must be present).
5. Update this file and [DECISIONS.md](DECISIONS.md).
