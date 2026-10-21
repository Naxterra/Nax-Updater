# Nax-Updater decisions

Why Nax-Updater works the way it does. Newest first; every release adds its decisions here and updates [DESIGN.md](DESIGN.md). Each entry names the decision, the evidence that led to it, and what was rejected.

## 0.17.28 (2026-10-21)

**WinRAR follows the installed channel and RARLAB's new link labels.**
RARLAB's download page now labels its links "English (64-bit x64)" instead of "English" and lists the 7.30 beta 1 table before the 7.23 release table. The check required the old label and failed for every WinRAR installation. It now accepts both label forms, ignores ARM64 and 32-bit links, and chooses the highest matching x64 build instead of the first one.

A beta installation (display name "WinRAR 7.30 Beta 1") follows the beta line and final releases, and a final installation is offered final releases only. WinRAR registers beta n as x.yy.n and a release as x.yy.0, so a plain version comparison would never offer 7.30 over 7.30 beta 1 (7.30.0 is lower than 7.30.1). Releases therefore rank above every beta of the same version.

## 0.17.27 (2026-10-18)

**A staged rollout that has not reached this PC is shown as current.**
CurseForge 1.324 is being released to 75 % of installations; this PC's place is 90.4 %. It was listed as "Gestaffelte Freigabe · ab 91 %" and counted among known newer versions. The owner's rule: a version not released to this PC is no update for it. Such a result is now Current, the held version and the percentage at which this PC is included appear only in the details, and it still settles the app against lower catalogs.

The 0.17.25 GitHub release (upgrades lost `NaxUpdater.Core.dll`) was deleted; its tag remains.

## 0.17.26 (2026-10-18)

**0.17.25 upgrades lost NaxUpdater.Core.dll; packaging no longer overrides versions.**
0.17.25 passed `-p:Version` to the whole build to fix the review's version finding. That also renumbered `NaxUpdater.Core.dll` from 1.0.0.0 to 0.17.25.0. On upgrade, Windows Installer kept the installed DLL as the "newer" file, then removed it with the old product, so upgraded installs could not start (fresh installs were complete). The version guard stays: packaging refuses a `-Version` that differs from the project and checks the published exe. The release check now compares the installed files with the published ones instead of only counting them.

## 0.17.25 (2026-10-18)

Fixes from the 2026-10-18 code review; each was confirmed in the source before changing it.

**One operation gate for every change.** Uninstall did not check for a running update and did not take the transaction lease, so a removal could start during an update, and whichever finished first released the other's busy state. Changing the language restarted the app, closing the host of a running installation. Now removals, updates and driver installs share one gate, removal takes the lease, and the language restart and window close wait for the gate.

**A failed same-version Chocolatey repair is no longer a success.** The installed version already equals the target before the repair runs, so a canceled UAC prompt, exit code 1603, or a record that did not change all ended as "succeeded". Only a successful execution proves a repair; real version advances keep their recovery (an installer error after the target was reached still counts).

**Chocolatey wrapper routes pass validation.** The plan compared the wrapper's version (nodejs 9.0.0) with the payload version the app was offered (nodejs.install 2.0.0) and turned valid updates into errors whenever the two numbers differed. The plan now names both packages; after `choco` exits, both records must match, and a missing record is a failure.

**Access denied from the desktop shell keeps the current window.** The relaunch fallback did not catch `UnauthorizedAccessException`, which a restricted session raises.

**Packaging cannot label a release with a version the app does not carry.** `-Version` must equal the project version, and the published exe is checked. (0.17.25 also passed it to the build; see 0.17.26.)

**WinRAR 7.30 Beta 1 keeps its shell extension.** Name matching stripped "7.23.0" but not "7.30 Beta 1", so the MSIX shell extension stopped attaching to WinRAR. Prerelease labels (alpha, beta, RC, preview) are now part of the version suffix. This was the "known WinRAR failure" in every release since; behind it, live smoke checks had pinned past machine state (DeepL 26.8.2, Nextcloud 34.0.3, Intel chipset 10.1.20658.8883). Those now check the shape, the full smoke run passes again, and the release script stops on any smoke failure.

Not done from the review's suggestions: a per-operation history, a download cache view, and catalog validation at load time.

## 0.17.24 (2026-10-14)

**Orphaned bundle registrations are recognized as superseded.**
On 2026-10-13 a program ran the Visual C++ 2013 12.0.30501 installer. Its log shows both packages as "Obsolete" (12.0.40664 was installed), so it installed nothing but still registered "Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.30501". The runtime files are 12.0.40664. That entry was shown as "newer version known, not applicable". An entry is now settled when a current sibling of the same name family (name without its trailing " - version") is installed at or above the version offered for it.
Rejected: uninstalling the orphan automatically (removal always needs the user's explicit confirmation), and matching families without the trailing version (too broad; "Some App" 1.0 and 2.0 could be different products).

**The update action column is 130 px**, because "Aktualisieren" plus the button padding did not fit in 100 px.

## 0.17.23 (2026-10-14)

**A stalled Store item no longer holds up Update All.**
PowerShell's Store update was accepted at 08:34:14, reported "DownloadingProduct, 0 of 0" once and never moved; Delivery Optimization had no job for it. Nax-Updater waited up to 20 minutes per Store item regardless. Now an item with no change in state or progress for 3 minutes is released and reported with its reason; it stays in the Store queue. The same item later finished on its own: the Store stayed idle until 08:48:24 (no deployment operation reached Windows before then), then downloaded 74.6 MB in 4 seconds and installed 7.6.7.0, which confirms that releasing the wait loses nothing.
Rejected: canceling the Store item (Windows owns it, and it may still finish), and a shorter overall timeout (large packages legitimately take longer while progressing).

## 0.17.22 (2026-10-14)

**The lists stay usable while updates run, and each row shows its own state.**
During "Alle aktualisieren" the whole update list was disabled: greyed out, not scrollable, and the only progress was one line in the status bar. Now only the actions are locked; each row moves through queued, preparing, closing, installing, verifying and its outcome, and outcomes stay visible after the closing rescan until the next manual scan.
Rejected: auto-scrolling to the running row, because it would fight the user while they read other rows.

**AusweisApp's signer pin moved to "Governikus Software GmbH".**
AusweisApp 2.6.1 is signed by Governikus Software GmbH (Bremen, HRB 18756 HB, EV certificate issued 2026-10-12) instead of Governikus GmbH & Co. KG (HRA 22041 HB). Governikus's imprint lists Governikus Software GmbH with exactly that register number as part of its restructured group, and the release and its digest come from Governikus's own repository. The update had failed safely on the old pin; a signer change is never accepted automatically.

## 0.17.21 (2026-10-12)

**NVIDIA series IDs are read from NVIDIA, not hardcoded.**
RTX 50 cards were checked with series ID 120, which is NVIDIA's RTX 30 series. That list ended at 616.56 and did not contain the installed 616.92, while the RTX 50 list had 617.42. The ID is now read by name ("GeForce RTX 50 Series" = 131) from `lookupValueSearch.aspx?TypeID=2`, so RTX 20 to 50 desktop cards each get their own list. Notebook GPUs stay without a verified catalog because their packages differ.

**Held updates get short, specific labels.**
"Gestaffelte Freigabe · ab 91 %" replaces a five-line badge, and driver rows read "Treiberupdate verfügbar · im Treiber-Bereich" with the driver version (only when it uses the package's version scheme; Intel and Realtek package and INF versions are not comparable). Per-source results in the details panel use the same labels.

**The packaging script's smoke step must not be trusted blindly.**
The local script reports any smoke failure as the known WinRAR assertion; a new NVIDIA fixture failure was hidden behind that message. Every release now reads the actual exception line.

## 0.17.20 (2026-10-12)

**Staged rollouts are decided per installation, exactly like the app's own updater.**
CurseForge 1.323 was published with `stagingPercentage: 25`. CurseForge's own log showed "Staging percentage: 0.25, percentage: 0.9036619034371483" for this PC's `.updaterId`, so its updater withheld the release. Nax-Updater now reads the same ID and applies electron-updater's rule.
Rejected: waiting for 100 % (withholds updates the app itself would install), and ignoring rollouts (delivers builds the publisher is still testing on a small group). The installed-metadata provider had been ignoring rollouts entirely; it now follows the same rule.

**A held rollout settles the app like "current".**
Otherwise WinGet or Chocolatey could deliver the release the publisher is still holding back.

## 0.17.19 (2026-10-12)

**Signature-only downloads for publishers that publish no hash.**
Approved by the owner for IDM, NVIDIA App, PDF-XChange PRO, PotPlayer, GOG Galaxy, Java 8 and Snagit. The exact Authenticode publisher is pinned (PDF-XChange signs as "TRACKER SOFTWARE PRODUCTS (CANADA) LIMITED"; GOG's name contains two spaces), the download must be HTTPS from the recipe's hosts, and drivers are excluded.
Rejected for MobaXterm: its MSI needs an unsigned external `.dat` cabinet the signature does not cover, so it stays on WinGet. Spotify stays on WinGet because it has no official version source.

## 0.17.18 (2026-10-12)

**A local source library instead of searching at run time.**
`update-providers.json` records each app's official version source, download, hash and signer (19 apps in this release), each verified with a real download. Recipe options grew from real cases:
- `architecture: neutral`: FileBot's x64 MSI installs 32-bit launchers.
- `alwaysElevate`: Kodi registers per user but installs to Program Files.
- `stayOnInstalledMajor`: OpenSSL and Snagit major lines.
- `allowMirrorRedirects`: LibreOffice mirrors, accepted only with a matching hash.
- `privateRepository`: Nax-* apps, whose token is sent only to `api.github.com`.
- `releaseTagPrefix` with underscores to dots: Inno Setup `is-7_1_0`.

Notice only, no recipe: VMware (Broadcom login), SAP GUI (S-user download), Uyap (unsigned, no hash), Kyocera tools. Nax-Updater's own recipe is not installable from Update All, because the update would close the running updater.

## 0.17.17 (2026-10-12)

**Source order: official site > Store / GitHub / GitLab > WinGet > Chocolatey.**
Set by the owner. Node.js 26.11.1 was on nodejs.org for days while Chocolatey's wrapper still pinned 26.11.0; catalogs lag publishers. GitHub releases moved to their own authority (250) below official sites, and Chocolatey-installed apps no longer get an exclusive Chocolatey route. The first "current" answer settles the app.

## 0.17.16 (2026-10-12)

**Cache Store "no update" answers for 6 hours.**
Slow full scans came from slow native searches inside Microsoft's Store service (3.5 to 7.7 s per search in the Store/Operational log, against 1.3 s normally). WinGet, DNS (14 to 21 ms) and cold services were ruled out with measurements. Only "no update" is cached, keyed by package family, installed version and catalog version, so a new release or a changed install is checked again. The second full scan went from 9.8 s to 5.6 s.

**Chocolatey feed: 8 s per attempt and one retry on a fresh connection**, because single stalled connections were timing out whole checks.

## 0.17.15 (2026-10-10)

**Reconnect once when the WinGet COM server exits.**
Installing a fresh `Microsoft.Winget.Source` package makes `WindowsPackageManagerServer` exit ("Server shutting down after exit event signaled"), which failed all 45 WinGet checks with `RPC_S_CALL_FAILED`. Only the two "server gone" RPC errors are retried.

**A vanished updater feed is "not supported", not "failed".**
Upgrade Sync's GitHub repository no longer exists; the app's own updater gets the same 404, so it is not a Nax-Updater error.

## 0.17.14 (2026-10-09)

**Chocolatey dependency pins are respected.**
A wrapper such as `nodejs` pins its payload `nodejs.install`; the payload's update is offered only through the wrapper, and after `choco` exits 0 the installed version must match the target.

**WinGet `--location` quoting doubles trailing backslashes**, because `C:\Program Files\LibreOffice\` broke msiexec's `INSTALLLOCATION`.

## 0.17.13 (2026-10-05)

**Renamed to Nax-Updater.**
Product, install folder and executable carry the hyphen; internal IDs, data folders, User-Agent and release asset names stay `NaxUpdater` so existing installs, settings and the self-update recipe keep working.

## 0.17.11 and 0.17.12 (2026-10-02)

- **Chocolatey-installed apps are updated through Chocolatey**, using its ownership records (`.chocolatey\<id>.<ver>\.registry`), so its package state stays consistent.
- **Nextcloud follows its own update server and channel** from `nextcloud.cfg` (beta users get release candidates). Release candidates register as 34.0.9x, so GitHub tags are never compared.
- **Fallback catalogs cannot override a producer's "current".** Chocolatey offered Node.js 26.x over a Node.js 22 LTS that nodejs.org confirmed current on its line.
- **Each manufacturer driver request is capped at 10 s**, so one slow site fails only its own driver.

## Earlier releases (0.4 to 0.17.10)

- **Drivers have their own view and are never in "Alle aktualisieren".** Manufacturer sources are queried directly, not Windows Update. After a bad GeForce driver, the owner applies driver updates individually.
- **Python install manager runtimes update through the manager itself** (`pymanager install --update`), never with python.org installers.
- **Store updates go through the native Store queue with exact package identity**; a queued Store item always wins over other sources, so deployments are never duplicated.
- **Running apps are closed only by path-bound process lists from a fresh plan**, never by name alone.
- **Producer sources first, WinGet as fallback; community catalogs never override producers** (September 2026).
- **Language filtering was removed**: installers keep the installed language where the source supports it; Firefox never falls back to English.
- **Single instance** (0.17.4), and machine-scope WinGet updates elevate with UAC.
- **Release candidates that GitHub marks as latest are skipped** (0.17.7) unless the recipe follows the installed prerelease line.
