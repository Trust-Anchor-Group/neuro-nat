# Local test Neuron ("NeuroAA")

A separate Neuron instance for developing and testing NAT. It never touches the Dev instance, and like any local setup it runs **without a domain name**.

| | |
|---|---|
| Instance name | `NeuroAA` |
| Data folder | `C:\ProgramData\IoT Gateway NeuroAA` |
| Web / admin | http://localhost:8081 (admin pages log in automatically from this machine) |
| XMPP | host `localhost`, port `5222`, no domain (bare JIDs look like `neuroaa@`) |
| Test account | `neuroaa`, set as the Neuron's notification address, so it has admin rights |
| Credentials | `.local/test-neuron.env` (git-ignored, generated). Also holds `NAT_TEST_NEURON=neuronode@`, the Neuron's own address |

## Start it

```powershell
.\tools\neuron-dev\Start-TestNeuron.ps1
```

It runs the IoTGateway console from `C:\My Projects\IoTGateway\Waher.IoTGateway.Console\bin\Debug\net8.0` with the broker installed. The first start of a fresh build is slow (a few minutes). To stop it, stop the process id the script prints (other Neurons on the machine are not touched).

## Run the live tests

```powershell
$env:NAT_TEST_HOST = "localhost"
$env:NAT_TEST_USER = "neuroaa"
$env:NAT_TEST_PASSWORD = "<from .local/test-neuron.env>"
$env:NAT_TEST_NEURON = "neuronode@"   # the Neuron's own address: no domain, so it ends with @
dotnet test Neuro.Nat.slnx
```

## Local, uncommitted change in IoTGateway

`iotgateway-dev-no-tests.patch` is applied in `C:\My Projects\IoTGateway` and is never committed:

- `Waher.IoTGateway/Setup/XmppConfiguration.cs`: when `GATEWAY_DEV_NO_TESTS=true`, environment-driven XMPP setup is accepted without a connection test. That lets a broker point at its own XMPP server (which only starts after setup) instead of a parent broker.

Without the environment variable it behaves exactly as before. To remove it:

```powershell
git -C "C:\My Projects\IoTGateway" checkout -- Waher.IoTGateway/Setup/XmppConfiguration.cs
```

## How this instance was set up

1. The personal-data consent page in the setup wizard was ticked by Mille (Claude may not give consent on someone's behalf).
2. `Start-TestNeuron.ps1` supplied the XMPP step through environment variables (`GATEWAY_XMPP_*`, `GATEWAY_DEV_NO_TESTS`).
3. Theme picked in the wizard; no domain, no encryption, no legal identity, no relay, push or peer review.
4. Account `neuroaa` created on the admin page *Create Account* (placeholder e-mail and phone).
5. Notification address set from the admin script prompt (`/Prompt.md`), because the settings page only saves addresses after a successful test notification:

```
N:=Waher.IoTGateway.Setup.NotificationConfiguration.Instance;
N.Addresses:=[Create(Waher.Persistence.CaseInsensitiveString,"neuroaa@")];
UpdateObject(N)
```

## Rebuilding after IoTGateway or broker changes

1. Build `Waher.IoTGateway.Console.csproj` (Debug) with MSBuild.
2. Register the modules with the console again, because a console build regenerates its `deps.json`:

```powershell
$install = "C:\My Projects\IoTGateway\Utilities\Waher.Utility.Install\bin\Debug\net8.0\win-x86\Waher.Utility.Install.exe"
$console = "C:\My Projects\IoTGateway\Waher.IoTGateway.Console\bin\Debug\net8.0\Waher.IoTGateway.Console.dll"
& $install -m "C:\My Projects\iotbroker\Waher.Service.IoTBroker\bin\Debug\netstandard2.1\Waher.Service.IoTBroker.manifest" -s $console -d "C:\ProgramData\IoT Gateway NeuroAA"
& $install -m "C:\My Projects\Neuro-Ledger\Waher.Service.NeuroLedger\bin\Debug\netstandard2.1\Waher.Service.NeuroLedger.manifest" -s $console -d "C:\ProgramData\IoT Gateway NeuroAA"
```

Account creation fails with a missing `Waher.Persistence.NeuroLedger` assembly if step 2 is skipped for Neuro-Ledger.

## Good to know

- Like any Neuron, it deletes files older than 7 days from the Windows user's temp folder at startup (`Gateway.DeleteOldFiles(Path.GetTempPath(), 7, ...)`).
- The Neuron's own XMPP client uses the account `neuronode` (password `NAT_NEURON_PASSWORD`). The name `neuron` cannot be used: setup creates a web admin user with the XMPP account name, and the broker refuses accounts that clash with a user. Set via the script prompt: `X:=Waher.IoTGateway.Setup.XmppConfiguration.Instance; X.Account:="neuronode"; X.Password:="..."; X.PasswordType:=""; UpdateObject(X)`, then restart.
- If its own client keeps failing, the login auditor blocks `[::1]` for everyone. Unblock from the script prompt: `Gateway.LoginAuditor.UnblockAndReset("[::1]")`.
