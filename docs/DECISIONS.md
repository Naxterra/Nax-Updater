# Nax-Updater decisions

Why Nax-Updater works the way it does. Newest first; every release adds its decisions here and updates [DESIGN.md](DESIGN.md). Each entry names the decision, the evidence that led to it, and what was rejected.

## 0.17.43 (2026-10-30)

**Direct driver updates read the signer of the driver catalog.** A driver package is verified through the signature of its catalog file (.cat), a signed PKCS #7 message that Windows accepts. The signer was read only from signed program files, so the Intel I219 update stopped with "The file does not contain an Authenticode signature". The signer is now read from the catalog's CMS signature.

**NVIDIA's driver service replaces its old search page.** The old search page still listed 617.42 as newest while NVIDIA's driver service listed 621.01, so an installed 621.01 was compared with an older driver. The service is asked for the exact product, and only final WHQL Game Ready drivers count.

**Intel chipset and RST rows compare versions.** The chipset row now compares the installed Chipset INF Utility with Intel's package. The RST row compares the storage controller's driver with the driver inside Intel's RST 18.7.6 package, whose readme lists the 10th generation SATA controller. Windows' inbox driver 15.44 is older, so Intel's package is offered as its download page, because replacing the boot storage driver is left to Intel's installer.

**SAP Cloud Connector is checked on SAP's development tools page.** SAP hands out the installer only after its license is accepted on that page. The new recipe option `licenseAcceptanceRequired` reports a newer release with SAP's page and never downloads it, because Nax-Updater does not accept terms on the owner's behalf.

**Download pages open reliably, and Kyocera checks are bounded.** "Exakten Download öffnen" did nothing when Windows' launcher declined, so the shell opens the page then, and a failure is shown. Kyocera's support site gets the same 10-second limit and 8 MB size limit as other manufacturer sites, a failed Kyocera download is tried again for the next row, every installed printer model counts, and a failed printer-driver query is reported instead of reading as "no printer".

## 0.17.42 (2026-10-30)

**Kyocera printers and their utilities are checked against Kyocera's own support site.** The Kyocera entries appeared as applications without a source, and the printer itself was missing from the driver view because a network printer has no present PnP device. Kyocera's regional download pages load their driver lists dynamically, but the global user support page that they link to publishes one JSON file per model with versions and download addresses. The KX driver now appears in the driver view, and the utilities are checked against the same model file. Kyocera publishes no hashes, so newer packages are reported with their exact download page and are not installed automatically.

**Downloads always send a User-Agent.** Intel's download mirror answered the Ethernet driver download with 403 Forbidden because the request carried no User-Agent. The downloader and the shared HTTP client now always identify Nax-Updater.

## 0.17.41 (2026-10-30)

**WinGet's catalog refresh is retried until its server stays up.** Installing a fresh catalog package makes WinGet's COM server exit, and its own log showed the next server exiting again 0.3 seconds later. Nax-Updater retried only once, so all 13 WinGet fallback checks of that scan failed with an RPC error (0x800706BE). The refresh and the package assessment now retry up to four times after a server exit, pausing one, two and three seconds between attempts; any other error is still reported at once.

## 0.17.40 (2026-10-29)

**Column headers are centered and the driver action column fits the German labels.** The plain-text column headers sat higher than the sortable ones in both lists, and the driver action buttons cut off German labels such as "Exakten Download öffnen". The headers are now vertically centered, the driver action column starts wider (the name column a little narrower), and each action button shows its full label as a tooltip. The status line no longer keeps saying that the manufacturer drivers are being checked after the check has finished or a removal has failed.

## 0.17.39 (2026-10-29)

**A driver that asks for a restart still locks further changes.** Windows reports a staged driver package's version at once but keeps the old driver loaded until the restart, so the 0.17.38 rule (a verified target with a restart request is completed) would have let further driver or application changes start on top of an unfinished driver swap. Driver installs with exit code 1641 or 3010 therefore stay pending until Windows restarts, also after the application is restarted. An application row that completed with a restart request now says so in its own result.

## 0.17.38 (2026-10-29)

**A verified update whose installer asks for a restart is completed, and Update All goes on.** LibreOffice's MSI installed 26.8.2 and returned 3010 because files were in use. The update was held as pending a restart, counted as failed, and Update All stopped, so the two remaining updates were skipped. When the installed version is verified at the target, the restart request now only adds a note, and a journal entry left waiting for a restart no longer blocks changes once its target is installed. An update whose target cannot be verified before the restart still waits for it and still stops further changes.

## 0.17.37 (2026-10-28)

**Translating a message never fails.** The translation runs inside the interface's error handlers, so an exception there would end the application. A text that cannot be translated within the time limit, or whose template does not format, is shown in English. Values inside a template are limited in length and never cross a sentence, clause or line boundary, and only a bounded number of translated texts is cached.

## 0.17.36 (2026-10-28)

**Messages from the checks are shown in German through a catalog of sentence templates.** About 570 English sentence templates in the core library produced the notes, source check results and error messages, and the German interface showed most of them in English. Translating at the source would have meant rewriting every message call and would have changed texts that tests and logs compare. The interface therefore translates the finished English text with a catalog keyed by the English template, and a test fails for any message literal without a German entry. The rejected alternative, a pattern list for only the messages seen on one PC, would have left every rarer message in English.

## 0.17.35 (2026-10-28)

**Scope marks are accepted only by recipes that serve both scopes.** Inno Setup adds "(Aktueller Benutzer)" and similar marks only when the user may choose the install scope, and such installers need per-user arguments anyway. A recipe without `currentUserInstallerArguments` therefore still requires the exact name, so a product that registers as "Name (64-bit)" cannot be claimed by a recipe for a different build. The Nax-Notepad recipe no longer marks its public repository as private, so its releases are read without the GitHub token.

## 0.17.34 (2026-10-28)

**Per-user Inno Setup registrations are matched with their scope mark.** Inno Setup installers that let the user choose the install scope append the scope and bitness to the registered name in the setup language, for example "Nax-Notepad (Aktueller Benutzer)". The repository recipe compared names exactly, so such an installation showed no verified source. The matcher now removes only these marks (English and German, as Inno Setup's message files write them) before it compares the name; any other parenthesized suffix still does not match.

**Installers that support both scopes update each installation in its own scope.** Nax-Notepad and Nax-TaskManager install per user or for all users. Their recipes only carried per-user arguments, so an all-users installation was reported as "newer version known, not applicable". The recipes now pass `/ALLUSERS` with elevation for an all-users installation and `/CURRENTUSER` without elevation for a per-user one, so an update never moves an installation to the other scope. Nax-StartMenu has a recipe as well. Its installer stops and restarts the menu itself, so the recipe lists no processes to close.

## 0.17.33 (2026-10-28)

**The Python install manager from python.org is recognized.** Python publishes its install manager through the Microsoft Store and as an MSIX that it signs itself. Only the Store package was trusted, so runtimes managed by the python.org package failed with "install manager not installed". The python.org family (`PythonSoftwareFoundation.PythonManager_3847v3x7pw1km`) is now trusted when it carries its own publisher signature: the family name is derived from the exact publisher name, and Windows validated the package signature at install.

## 0.17.32 (2026-10-28)

**One Store app no longer has its own update path.** A special path read one publisher's own update manifest and bound extra processes for a single Store app. It was removed by the owner's decision, and that app is now checked by the generic Store path like every other Store app. The generic path now also marks the case in which the catalog lists a newer package but the Store offers nothing for this installation, so the details explain why no update is shown.

**The must-never-happen checklist is part of the repository.** `CHECKLIST.md` lists what Nax-Updater must never do, and every release report marks each item as verified by a named test or as not verified.

## 0.17.31 (2026-10-28)

**A download link read from a second page no longer counts as naming the version.** A recipe that reads its link from a download page fills the link's named groups after the version is built, so such a page could overwrite the version parts the URL is built from with older values, and a correctly signed older installer would have run without the version check. A signature-only URL now counts as tied to the version only through an exact `{version}` or `{version_}` placeholder, or through version parts that the download-page pattern does not capture. No shipped recipe was affected.

## 0.17.30 (2026-10-27)

Fixes from the release review of 0.17.29. Each finding was confirmed in the source, by a probe or against live data before it was changed.

**A signature-only installer must declare the approved version.** A signature proves the publisher, not the version. PotPlayer downloads a "Latest" file whose URL names no version, and Java 8 takes its version from one page and its download link from another, so a lagging download could run an older or different build silently. When the URL does not name the version, the installer's own version resource must now match (PotPlayer's page lists 261021 and its installer declares 26.10.21.0; Java 8u511 declares 8.0.5110.33). A cached signature-only installer is never reused.

**An update across a major version is recognized by its identity.** The correlation key of a versioned display name ("7-Zip 24.09" becoming "7-Zip 25.01") changes with the major version, so a successful update was reported as not reaching its target. The installed application is now also matched by its unchanged inventory identity.

**A journal write failure no longer loses an outcome or ends the app.** A locked or full disk while saving the final journal entry threw out of the transaction after the installer had already succeeded. The outcome is now kept with a warning, an update whose journal cannot be started does not run, and the update and driver handlers catch unexpected exceptions instead of ending the process.

**A refusal before the start is recorded as such.** When an application was reopened after closing, or the verified payload was missing or changed, the executor refused to start, but the transaction was recorded as needing attention and warned at every start. Such refusals are now failures before any change, and a known failure is reported again at startup only if it turned out to have succeeded.

**Only a refused start counts as a canceled UAC prompt.** An installer that ran and returned its own ERROR_CANCELLED (1223) was recorded as never started, so nothing rechecked it. Only a start that Windows refused now counts as "not started".

**Mirror redirects require a published hash.** Without a hash, a redirect could have delivered a correctly signed installer from any HTTPS host.

**Packaging refuses file-version decreases and repeated versions.** Windows Installer skips a file whose version is lower than the installed one and then removes the old copy, which is how release 0.17.25 lost a DLL. Every DLL and EXE is now compared with the previous release, and an already released version number is refused.

**Live tests separate missing preconditions from defects.** An integration check is skipped when its package is not installed, an exhausted GitHub quota is reported as inconclusive, and the process-lifetime test starts its console copies through conhost, because Windows 11 hands console programs started from the desktop to Windows Terminal, which runs them in a job.

Not changed: a machine-wide transaction lock for several Windows users, driver archives with files in subfolders, cache limits for driver packages, and the self-update recipe (which stays non-installable).

## 0.17.29 (2026-10-23)

Fixes from the audit of 2026-10-22. Each finding was confirmed in the source or against live data before it was changed.

**WinRAR is read from the installer file names.** RARLAB changed its download page again on 2026-10-22: the language now sits in its own table cell and every link says "64-bit version". Labels have changed three times, but the file names have not (`winrar-x64-730b1d.exe` is the German 7.30 beta 1), so the version, the beta number and the language code are now read from the file name alone. German is `d`, which is distinct from Danish `dk` and Brazilian `br`.

**Recovery no longer accepts an unchanged version as proof of a repair.** The normal transaction path already required a successful execution for a same-version Chocolatey repair, but recovery after a restart still accepted the matching application version. The journal now records the Chocolatey targets, and recovery checks Chocolatey's own records for them.

**GitHub sources keep their channel and need no installer to confirm an older release.** An installed prerelease fell back to the stable "latest" release whenever its channel listing was empty or unreadable, and a release without a matching installer was an error even when it was older than the installation. The prerelease channel now never falls back to stable (an unreadable channel is reported as a failed check), and a release that is not newer settles the app without an installer.

**The update list and its details follow a running update.** The update list could not be reopened from the application view while a change ran, and the details panel kept the state from when the row was selected. Both now follow the live state.

**A queued Store update no longer claims a hash and signature check.** Its verification text now names Windows' own Store queue item for the exact package family.

**Verified installers are deleted after a successful update.** The download cache had grown to 9.3 GiB of app installers for versions installed long ago (27 versions for the Electron update feeds alone). After a verified successful update, that update's installer and its empty folders are deleted. Driver packages (2.8 GiB) stay, because an older driver installer can matter for a rollback.

Not done from the audit's suggestions: a per-operation history, batch totals with a retry action, separating driver checks from the application scan, recipe validation at load time, and a coverage filter.

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
