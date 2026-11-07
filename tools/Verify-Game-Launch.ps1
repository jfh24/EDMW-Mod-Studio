[CmdletBinding()]
param(
    [string]$GamePath = 'C:\Games\Empires Dawn of the Modern World',
    [switch]$TraceProviderPackets,
    [switch]$PrimeProviderDecoder,
    [switch]$DelayProviderPacket
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRelay = Join-Path $taskRoot 'relay'
$taskGame = [IO.Path]::GetFullPath($GamePath)
$taskChecks = Join-Path $taskRoot ('.verification\online-game-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskChecks -Force | Out-Null
$taskNode = (Get-Command node -ErrorAction Stop).Source
$taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
if (!(Test-Path -LiteralPath (Join-Path $taskRelay 'node_modules\ws'))) {
    throw 'Run npm ci inside relay before game-start verification.'
}
if (!(Test-Path -LiteralPath (Join-Path $taskGame 'Empires_DMW.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $taskGame 'TCP Protocol.dll'))) {
    throw 'Game-start verification needs a game folder containing Empires_DMW.exe and TCP Protocol.dll.'
}

& $taskDotnet build (Join-Path $taskRoot 'EDMWMapDesigner\EDMWMapDesigner.csproj') -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Editor build failed. Run build.ps1 first if embedded resources are missing.' }

$taskListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$taskListener.Start()
$taskPort = $taskListener.LocalEndpoint.Port
$taskListener.Stop()
$taskEndpoint = 'http://127.0.0.1:' + $taskPort
$taskOldPort = $env:PORT
$taskOldData = $env:RELAY_DATA_DIR
$taskOldControl = $env:ONLINE_VERIFICATION_CONTROL_DIR
$taskOldDiagnostics = $env:EDMW_RELAY_DIAGNOSTICS_PATH
$taskOldMultiInstance = $env:EDMW_RELAY_TEST_MULTI_INSTANCE
$taskOldProviderLoad = $env:EDMW_RELAY_TEST_LOAD_PROVIDER
$taskOldProviderPacketTrace = $env:EDMW_RELAY_TRACE_PROVIDER_PACKETS
$taskOldProviderPacketDecode = $env:EDMW_RELAY_DECODE_PROVIDER_PACKETS
$taskOldProviderPacketDelay = $env:EDMW_RELAY_DELAY_PROVIDER_PACKETS
$taskServer = $null
$taskClient = $null
try {
    $env:PORT = $taskPort.ToString()
    $env:RELAY_DATA_DIR = Join-Path $taskChecks 'data'
    $env:ONLINE_VERIFICATION_CONTROL_DIR = $taskChecks
    $taskServer = Start-Process -FilePath $taskNode -ArgumentList 'tools/OnlineVerificationRelay.mjs' -WorkingDirectory $taskRoot `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $taskChecks 'relay.log') `
        -RedirectStandardError (Join-Path $taskChecks 'relay-errors.log')
    $env:PORT = $taskOldPort
    $env:RELAY_DATA_DIR = $taskOldData
    $env:ONLINE_VERIFICATION_CONTROL_DIR = $taskOldControl
    $taskReady = $false
    $taskHealthError = 'The relay did not answer before the health-check deadline.'
    for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
        if ($taskServer.HasExited) { throw 'The test relay exited before becoming healthy.' }
        try {
            $taskResponse = Invoke-WebRequest -UseBasicParsing -Uri ($taskEndpoint + '/health') -TimeoutSec 1
            if ($taskResponse.StatusCode -eq 200) { $taskReady = $true; break }
        }
        catch { $taskHealthError = $_.Exception.Message; Start-Sleep -Milliseconds 100 }
    }
    if (!$taskReady) {
        $taskRelayOutput = Get-Content -LiteralPath (Join-Path $taskChecks 'relay.log') -Raw -ErrorAction SilentlyContinue
        $taskRelayError = Get-Content -LiteralPath (Join-Path $taskChecks 'relay-errors.log') -Raw -ErrorAction SilentlyContinue
        throw ('The test relay did not become healthy: ' + $taskHealthError + ' Relay output: ' + $taskRelayOutput + ' ' + $taskRelayError)
    }

    $taskAssembly = Join-Path $taskRoot 'EDMWMapDesigner\bin\Release\net9.0-windows\EDMW Mod Studio.dll'
    $env:EDMW_RELAY_DIAGNOSTICS_PATH = Join-Path $taskChecks 'native-debug.log'
    $env:EDMW_RELAY_TEST_MULTI_INSTANCE = '1'
    $env:EDMW_RELAY_TEST_LOAD_PROVIDER = '1'
    $env:EDMW_RELAY_TRACE_PROVIDER_PACKETS = if ($TraceProviderPackets) { '1' } else { '0' }
    $env:EDMW_RELAY_DECODE_PROVIDER_PACKETS = if ($PrimeProviderDecoder) { '1' } else { '0' }
    $env:EDMW_RELAY_DELAY_PROVIDER_PACKETS = if ($DelayProviderPacket) { '1' } else { '0' }
    $taskClient = Start-Process -FilePath $taskDotnet `
        -ArgumentList ('"' + $taskAssembly + '" --verify-online-game-start "' + $taskEndpoint + '" "' + $taskGame + '"') `
        -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $taskChecks 'desktop.log') `
        -RedirectStandardError (Join-Path $taskChecks 'desktop-errors.log')
    if (!$taskClient.WaitForExit(600000)) { throw 'Room game setup verification exceeded ten minutes.' }
    $taskClient.Refresh()
    $taskClientExited = $taskClient.HasExited
    $taskExitCode = $null
    if ($taskClientExited) { $taskExitCode = $taskClient.ExitCode }
    Get-Content -LiteralPath (Join-Path $taskChecks 'desktop.log')
    $taskPassCount = @(Select-String -LiteralPath (Join-Path $taskChecks 'desktop.log') -Pattern '^GAME PASS:' -ErrorAction SilentlyContinue).Count
    $taskErrorLog = Join-Path $taskChecks 'desktop-errors.log'
    if (!$taskClientExited -or $taskPassCount -ne 4 -or ((Test-Path -LiteralPath $taskErrorLog -PathType Leaf) -and (Get-Item -LiteralPath $taskErrorLog).Length -gt 0) -or
        ($null -ne $taskExitCode -and $taskExitCode -ne 0)) {
        Get-Content -LiteralPath (Join-Path $taskChecks 'desktop-errors.log')
        $taskExitText = if ($null -eq $taskExitCode) { 'unavailable' } else { $taskExitCode.ToString() }
        throw "Room game setup verification failed: processExited=$taskClientExited passMarkers=$taskPassCount exitCode=$taskExitText"
    }
    $taskExitText = if ($null -eq $taskExitCode) { 'unavailable (all runtime pass markers were present)' } else { $taskExitCode.ToString() }
    Write-Host ('Room game setup verification passed; process exit code ' + $taskExitText + '; logs: ' + $taskChecks)
}
finally {
    $env:PORT = $taskOldPort
    $env:RELAY_DATA_DIR = $taskOldData
    $env:ONLINE_VERIFICATION_CONTROL_DIR = $taskOldControl
    $env:EDMW_RELAY_DIAGNOSTICS_PATH = $taskOldDiagnostics
    $env:EDMW_RELAY_TEST_MULTI_INSTANCE = $taskOldMultiInstance
    $env:EDMW_RELAY_TEST_LOAD_PROVIDER = $taskOldProviderLoad
    $env:EDMW_RELAY_TRACE_PROVIDER_PACKETS = $taskOldProviderPacketTrace
    $env:EDMW_RELAY_DECODE_PROVIDER_PACKETS = $taskOldProviderPacketDecode
    $env:EDMW_RELAY_DELAY_PROVIDER_PACKETS = $taskOldProviderPacketDelay
    if ($null -ne $taskClient -and !$taskClient.HasExited) { Stop-Process -Id $taskClient.Id -Force }
    if ($null -ne $taskServer -and !$taskServer.HasExited) { Stop-Process -Id $taskServer.Id -Force }
}
