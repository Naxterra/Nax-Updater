# Nax-Updater decisions

Why Nax-Updater works the way it does. Newest first; every release adds its decisions here and updates [DESIGN.md](DESIGN.md). Each entry names the decision, the evidence that led to it, and what was rejected.

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
