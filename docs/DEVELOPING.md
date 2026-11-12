# Developing NAT

## Build from source

You need the .NET SDK named in `global.json` (10.0).

```powershell
dotnet build Neuro.Nat.slnx
dotnet run --project src/Neuro.Nat.Desktop
```

The program is `NAT.exe`. The solution has four projects and three test projects:

| Project | What's in it |
|:--------|:-------------|
| `Neuro.Nat.Engine` | Accounts, connections, events, chat, the encrypted database. No UI |
| `Neuro.Nat.App` | Every screen (Avalonia), the tray, the icon |
| `Neuro.Nat.Desktop` | The program: starts it all, Windows vault, installer hooks, updates |
| `Neuro.Nat.Features.Telemetry` | App telemetry: finds the Neurons with the Neuro.App.Telemetry package. The app knows it only through `App/Features` |
| `Neuro.Nat.Engine.Tests` | Tests of the engine |
| `Neuro.Nat.App.Tests` | Tests of the screens, rendered without a window |
| `Neuro.Nat.Features.Telemetry.Tests` | Tests of app telemetry, with a stand-in for the Neuron's web API |

## Run the tests

```powershell
dotnet test Neuro.Nat.slnx
```

Tests that need a Neuron skip by themselves and name the keys they miss. Render tests write
pictures to `.impeccable/review`, which git ignores: look at them after a change to a screen.

## The local test Neuron

Live tests run against a Neuron of your own on this computer, the instance `NeuroAA`. How to set it
up and start it is in [tools/neuron-dev/README.md](../tools/neuron-dev/README.md). Its addresses and
passwords are in `.local/test-neuron.env`, which git ignores. Load them before a test run:

```powershell
Get-Content .local/test-neuron.env | Where-Object { $_ -match '^\s*NAT_\w+\s*=' } | ForEach-Object {
	$k, $v = $_ -split '=', 2; Set-Item "Env:$($k.Trim())" $v.Trim() }
dotnet test Neuro.Nat.slnx
```

| Key | What it is |
|:----|:-----------|
| `NAT_TEST_HOST`, `NAT_TEST_PORT` | Where the test Neuron's XMPP server is |
| `NAT_TEST_USER`, `NAT_TEST_PASSWORD` | The test account, an administrator of the Neuron |
| `NAT_TEST_USER2`, `NAT_TEST_PASSWORD2` | A second account, a coworker |
| `NAT_TEST_NEURON` | The Neuron's own address |
| `NAT_TEST_DOMAIN` | The XMPP domain, when it is not the host name |

## Try the app against a copy of your data

`NAT_DATA_FOLDER` makes NAT use another data folder than `%AppData%\Neuro.Nat`. Copy your data folder
somewhere, point the variable at the copy, and start NAT. Nothing is moved when it is set.

```powershell
$env:NAT_DATA_FOLDER = "C:\Temp\nat-copy"
dotnet run --project src/Neuro.Nat.Desktop
```

## Draw the icon again

The icon is drawn by code (`src/Neuro.Nat.App/Watch/IconArt.cs`). After a change to the design:

```powershell
./tools/installer/Make-Icon.ps1
```

## Kiosk

NAT can run from a config file instead of the window, for a screen on a wall.

| Option | What it does |
|:-------|:-------------|
| `--config <file>` | Signs in with the accounts in the file. Nothing is kept between runs and no data folder is used, so a kiosk never touches a normal NAT on the same computer |
| `--kiosk` | Shows only the Big screen, full screen, for good. Needs `--config` |

```powershell
NAT.exe --kiosk --config C:\Kiosk\nat-kiosk.json
```

An example `nat-kiosk.json`:

```json
{
  "accounts": [
    { "host": "neuron.example.com", "userName": "kiosk", "password": "<password>" }
  ],
  "neurons": [ "neuron.example.com" ],
  "pages": [ "neurons", "problems", "app-numbers" ],
  "seconds": 20,
  "look": "dark"
}
```

| Key | What it is |
|:----|:-----------|
| `accounts` | One or more accounts: `host`, `userName`, `password`, and optionally `port`, `domain`, `webAddress`, `trustServerCertificate` |
| `neurons` | The domains of the Neurons this kiosk shows. Their presence subscriptions are accepted without asking |
| `pages` | The Big screen pages to show. Neurons always shows |
| `seconds` | How long each page shows: 10, 20, 30 or 60 |
| `look` | `light`, `dark` or `system` |

The file holds passwords, so NAT refuses to start when another user can read it. Let only the kiosk's
own user read it: `chmod 600 nat-kiosk.json` on Linux, or on Windows:

```powershell
icacls nat-kiosk.json /inheritance:r /grant:r "${env:USERNAME}:F"
```

The account needs admin rights on a Neuron (its notification address, or the Admin privilege) for the
kiosk to show that Neuron's events. Without them it shows state, problems and notifications.
