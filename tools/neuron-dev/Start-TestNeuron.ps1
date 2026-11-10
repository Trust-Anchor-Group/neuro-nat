<#
.SYNOPSIS
  Starts the local "NeuroAA" test Neuron used for Neuro.AA development.

.DESCRIPTION
  Runs the IoTGateway console (with the IoT Broker installed) as a separate instance named
  "NeuroAA", with its own data folder (C:\ProgramData\IoT Gateway NeuroAA) and ports
  (HTTP 8081, HTTPS 8444, XMPP 5222/5269), so it never touches the Dev instance.

  First-time setup is driven by environment variables read from .local\test-neuron.env.
  The Neuron runs without a domain name, as local setups should.
  It relies on the local, uncommitted patch in iotgateway-dev-no-tests.patch
  (GATEWAY_DEV_NO_TESTS) so the Neuron can use an account on its own XMPP server instead of
  a parent broker. Never use this for anything but local testing.
#>
param(
	[string]$ConsoleFolder = "C:\My Projects\IoTGateway\Waher.IoTGateway.Console\bin\Debug\net8.0",
	[string]$EnvFile = (Join-Path $PSScriptRoot "..\..\.local\test-neuron.env"),
	[switch]$Visible
)

$settings = @{}
Get-Content $EnvFile | Where-Object { $_ -match '^\s*(\w+)=(.*)$' } | ForEach-Object {
	$settings[$Matches[1]] = $Matches[2].Trim()
}

$domain = 'localhost'   # Local test Neurons run without a domain name.

$envVars = @{
	GATEWAY_DEV_NO_TESTS        = 'true'
	GATEWAY_XMPP_HOST           = 'localhost'
	GATEWAY_XMPP_PORT           = '5222'
	GATEWAY_XMPP_CREATE         = 'false'
	GATEWAY_XMPP_ACCOUNT        = $settings['NEUROAA_NEURON_ACCOUNT']
	GATEWAY_XMPP_PASSWORD       = $settings['NEUROAA_NEURON_PASSWORD']
	GATEWAY_XMPP_TRUST          = 'true'
	GATEWAY_XMPP_LOG            = 'false'
	GATEWAY_XMPP_OBS_AUTH       = 'false'
	GATEWAY_XMPP_CLEAR_PWD      = 'false'
	GATEWAY_ID_USE              = 'false'
	GATEWAY_NOTIFICATION_JIDS   = "$($settings['NEUROAA_TEST_USER'])@$domain"
	GATEWAY_NOTIFICATION_URLS   = ''
	BROKER_INET_GATEWAY_REG     = 'false'
	BROKER_REVIEW_USE           = 'false'
	BROKER_RELAY_USE            = 'false'
	BROKER_FIREBASE_USE         = 'false'
}

foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, $envVars[$k], 'Process') }

$exe = Join-Path $ConsoleFolder 'Waher.IoTGateway.Console.exe'
$style = if ($Visible) { 'Normal' } else { 'Minimized' }
$p = Start-Process -FilePath $exe -ArgumentList '-instance', 'NeuroAA' -WorkingDirectory $ConsoleFolder -WindowStyle $style -PassThru
"Started test Neuron, process $($p.Id). Web: $($settings['NEUROAA_NEURON_WEB'])  XMPP: localhost:5222 (no domain)"
