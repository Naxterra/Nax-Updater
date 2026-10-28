# Must never happen

This list names what Nax-Updater must never do. Every release report marks each item as verified by a named test or as not verified.

1. Nax-Updater must never run an installer whose publisher hash or exact Authenticode signer was not verified, except for the documented hash-only and signature-only recipe modes.
2. Nax-Updater must never download an installer over plain HTTP or from a host outside the source's allowed hosts.
3. Nax-Updater must never offer or install a version that is older than the installed one, except a final release that replaces an installed beta of the same version.
4. Nax-Updater must never run two system changes (an update, a driver install or a removal) at the same time.
5. Nax-Updater must never report an update as successful when the installed version did not reach the target, or when a same-version repair did not change the package records it was meant to fix.
6. Nax-Updater must never close or terminate a process that is not bound to the executable paths of the update it is running.
7. Nax-Updater must never include a driver update in "Alle aktualisieren".
8. Nax-Updater must never offer a release that the publisher has not released to this PC, or a beta to an installation that follows final releases.
9. Nax-Updater must never let a lower-priority catalog (WinGet or Chocolatey) override a higher-priority source that reports the application as current.
10. Nax-Updater must never remove an application without both confirmations, and never by deleting its folder.
11. Nax-Updater must never delete files outside its own download cache, and never delete a cached driver package.
12. Nax-Updater must never send the GitHub token to any host other than api.github.com.
13. Nax-Updater must never close its window or restart for a language change while a system change is running.
14. A release must never ship an installed file set that differs from the published build, or an application version that differs from the release version.
15. Uninstalling Nax-Updater must never leave its program files, Start menu entries or App Paths registration behind.
