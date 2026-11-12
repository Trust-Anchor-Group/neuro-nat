# NAT

NAT (Neuro Administration Tool) is a desktop app for the people who run Neurons.

## Installing on Windows

`./tools/installer/Build-Installer.ps1` builds `artifacts/installer/releases/Neuro.Nat-win-Setup.exe`
(Velopack, per user, no admin rights). The installer adds Start menu and desktop shortcuts and turns
on **Start with Windows**, which starts NAT in the tray (`--tray`) at sign-in; the tray menu
toggles it. The installer is not signed, so SmartScreen warns ("More info" > "Run anyway").

Installed copies look for updates every few hours, download them in the background, offer
"Restart to update" in the tray menu, and otherwise install them on quit. They update from the
source built in: `UpdateSource` in `Directory.Build.props`, the only place the address is written
(GitHub releases of this repository, which needs `NAT_GITHUB_TOKEN` while the repository is
private), or the folder, share or URL passed to the installer script with `-UpdateSource`. A
machine can point elsewhere with `NAT_UPDATE_SOURCE` or the first line of `update-source.txt` in
the data folder. To release, raise the version (`-Version 0.2.0`) and copy the releases folder to
the source, or pass `-PublishGitHub`.

## Data

NAT keeps its data in `%AppData%\Neuro.Nat`. Everything it remembers is in one encrypted database
(`database\`): accounts, sign-ins, chat history and room passwords, Inbox items, settings, and the
signing keys of legal identities. Only three things are outside it:

- `secrets\database.key`, the key to the database, protected by Windows for the signed-in user;
- `logs\nat.log`, the log, which never holds message texts, passwords, tokens or sign-ins;
- `update-source.txt`, read by the updater before the app has started.

Older versions kept accounts, chats, the Inbox and settings in plain JSON files and each sign-in in
a file of its own. At start NAT moves what it finds of those into the database, checks that all of
it came across, and only then deletes the old file; what fails keeps its file and is tried again at
the next start. If the database cannot be opened, NAT says so and offers Try again, Start fresh (the
database is moved aside into a dated folder; nothing is deleted) and Quit.

On macOS and Linux there is no vault for the key yet, so there is no database: nothing is kept
between runs, and Settings says so.

The data folder of the version before the rename moves to `%AppData%\Neuro.Nat` once, at the first
start. `NAT_DATA_FOLDER` names another folder to use instead, for example a copy to try the app
against; nothing is moved then.

The icon file `src/Neuro.Nat.Desktop/Assets/NAT.ico` is drawn from the same code as the live icons
(`IconArt.cs`); `./tools/installer/Make-Icon.ps1` draws it again.
