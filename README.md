# Neuro.AA

## Installing on Windows

`./tools/installer/Build-Installer.ps1` builds `artifacts/installer/releases/Neuro.AA-win-Setup.exe`
(Velopack, per user, no admin rights). The installer adds Start menu and desktop shortcuts and turns
on **Start with Windows**, which starts Neuro.AA in the tray (`--tray`) at sign-in; the tray menu
toggles it. The installer is not signed, so SmartScreen warns ("More info" > "Run anyway").

Installed copies look for updates every few hours, download them in the background, offer
"Restart to update" in the tray menu, and otherwise install them on quit. They update from the
source baked in with `-UpdateSource` (a folder, share or URL; GitHub releases of this repository by
default, which needs `NEURO_AA_GITHUB_TOKEN` while the repository is private). A machine can point
elsewhere with `NEURO_AA_UPDATE_SOURCE` or `%AppData%\Neuro.AA\update-source.txt`. To release, raise
the version (`-Version 0.2.0`) and copy the releases folder to the source, or pass `-PublishGitHub`.
