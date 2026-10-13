[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRelay = Join-Path $taskRoot 'relay'
$taskChecks = Join-Path $taskRoot ('.verification\online-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskChecks -Force | Out-Null
$taskNode = (Get-Command node -ErrorAction Stop).Source
$taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
if (!(Test-Path -LiteralPath (Join-Path $taskRelay 'node_modules\ws'))) {
    throw 'Run npm ci inside relay before online verification.'
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
$taskServer = $null
$taskClient = $null
try {
    $env:PORT = $taskPort.ToString()
    $env:RELAY_DATA_DIR = Join-Path $taskChecks 'data'
    $taskServer = Start-Process -FilePath $taskNode -ArgumentList 'src/server.js' -WorkingDirectory $taskRelay `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $taskChecks 'relay.log') `
        -RedirectStandardError (Join-Path $taskChecks 'relay-errors.log')
    $env:PORT = $taskOldPort
    $env:RELAY_DATA_DIR = $taskOldData
    $taskReady = $false
    for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
        if ($taskServer.HasExited) { throw 'The test relay exited before becoming healthy.' }
        try {
            $taskResponse = Invoke-WebRequest -Uri ($taskEndpoint + '/health') -TimeoutSec 1
            if ($taskResponse.StatusCode -eq 200) { $taskReady = $true; break }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$taskReady) { throw 'The test relay did not become healthy.' }
    $taskAssembly = Join-Path $taskRoot 'EDMWMapDesigner\bin\Release\net9.0-windows\EDMW Mod Studio.dll'
    $taskClient = Start-Process -FilePath $taskDotnet `
        -ArgumentList ('"' + $taskAssembly + '" --verify-online ' + $taskEndpoint) `
        -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $taskChecks 'desktop.log') `
        -RedirectStandardError (Join-Path $taskChecks 'desktop-errors.log')
    if (!$taskClient.WaitForExit(120000)) { throw 'Desktop/relay verification exceeded two minutes.' }
    $taskClient.Refresh()
    Get-Content -LiteralPath (Join-Path $taskChecks 'desktop.log')
    if ($taskClient.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $taskChecks 'desktop-errors.log')
        throw ('Desktop/relay verification failed with exit code ' + $taskClient.ExitCode)
    }
    Write-Host ('Online verification logs: ' + $taskChecks)
}
finally {
    $env:PORT = $taskOldPort
    $env:RELAY_DATA_DIR = $taskOldData
    if ($null -ne $taskClient -and !$taskClient.HasExited) { Stop-Process -Id $taskClient.Id -Force }
    if ($null -ne $taskServer -and !$taskServer.HasExited) { Stop-Process -Id $taskServer.Id -Force }
}
