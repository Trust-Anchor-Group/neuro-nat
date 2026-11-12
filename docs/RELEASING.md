# Releasing NAT

## Make a release

1. Raise `<Version>` in `Directory.Build.props`, for example to `0.3.0`, and commit.
2. Tag the commit with the same version and push the tag:

   ```powershell
   git tag v0.3.0
   git push origin v0.3.0
   ```

3. GitHub builds it. The `Release` workflow runs the tests, packs the installer and the update
   packages, and uploads them as a GitHub release. If a test fails, nothing is released.

A tag that is not a full version, such as `v0.3` or `release-1`, is refused with a message.

Every push and pull request also runs the `CI` workflow: build and the tests that need no Neuron.

## What the release holds

| File | What it is |
|:-----|:-----------|
| `Neuro.Nat-win-Setup.exe` | The installer to hand out. Per user, no admin rights |
| `Neuro.Nat-<version>-full.nupkg` | The whole version, for updates |
| `Neuro.Nat-<version>-delta.nupkg` | Only what changed since the last release, for quick updates |
| `releases.win.json` | The list installed copies read to find updates |

The installer is not signed, so Windows SmartScreen warns the first time: "More info", then "Run anyway".

## Where installed copies look for updates

Installed copies look every few hours, download in the background, offer "Restart to update" in
the tray menu, and otherwise install the update when NAT quits. They look in the first of these
that is set:

| Where | Used for |
|:------|:---------|
| `NAT_UPDATE_SOURCE` | One computer, for a test |
| First line of `update-source.txt` in the data folder | One computer, for good |
| `UpdateSource` in `Directory.Build.props` | Everyone. The GitHub releases of this repository |

A source is a GitHub repository address, any other web address, or a folder or network share.
A private repository needs a token in `NAT_GITHUB_TOKEN`.

## Where the repository lives

NAT's home is https://github.com/Trust-Anchor-Group/neuro-nat. The address is written in one place:
`<UpdateSource>` in `Directory.Build.props`. If the repository ever moves, change that line, make a
release, and point your clone at the new address:

```powershell
git remote set-url origin git@github.com:Trust-Anchor-Group/<new-name>.git
```

Copies installed before this address was set look for updates at the old address,
github.com/berggrenmille/Neuro-AA, and do not find the releases here. Install them again from a
release here once.

## Build an installer on your own computer

```powershell
./tools/installer/Build-Installer.ps1
./tools/installer/Build-Installer.ps1 -Version 0.3.0 -UpdateSource '\nas\apps\NAT'
```

It publishes NAT and packs it with Velopack into `artifacts/installer/releases`. `-PublishGitHub`
also uploads the release, with a token in `GITHUB_TOKEN`.
