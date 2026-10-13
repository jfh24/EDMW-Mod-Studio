[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Endpoint,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$taskUri = $null
if (![Uri]::TryCreate($Endpoint.Trim(), [UriKind]::Absolute, [ref]$taskUri) -or
    $taskUri.Scheme -ne 'https' -or $taskUri.AbsolutePath -ne '/' -or
    ![string]::IsNullOrEmpty($taskUri.UserInfo) -or ![string]::IsNullOrEmpty($taskUri.Query) -or
    ![string]::IsNullOrEmpty($taskUri.Fragment)) {
    throw 'Endpoint must be an explicit HTTPS relay origin without credentials, a path, query, or fragment.'
}

$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$taskChecks = Join-Path $taskRoot ('.verification\live-online-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskChecks -Force | Out-Null
$taskAssembly = Join-Path $taskRoot 'EDMWMapDesigner\bin\Release\net9.0-windows\EDMW Mod Studio.dll'
$taskOutput = Join-Path $taskChecks 'desktop.log'
$taskErrors = Join-Path $taskChecks 'desktop-errors.log'
$taskReport = Join-Path $taskChecks 'desktop-live-report.json'
$taskClient = $null
$taskFailure = $null

try {
    if (!$SkipBuild) {
        & $taskDotnet build (Join-Path $taskRoot 'EDMWMapDesigner\EDMWMapDesigner.csproj') -c Release -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'Editor build failed. Run build.ps1 first if embedded resources are missing.' }
    }
    if (!(Test-Path -LiteralPath $taskAssembly -PathType Leaf)) {
        throw 'The Release desktop assembly is missing. Build the editor or omit -SkipBuild.'
    }

    $taskArguments = @(
        ('"' + $taskAssembly + '"'),
        '--verify-online-live',
        ('"' + $taskUri.AbsoluteUri + '"'),
        ('"' + $taskChecks + '"')
    )
    $taskClient = Start-Process -FilePath $taskDotnet -ArgumentList $taskArguments `
        -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $taskOutput -RedirectStandardError $taskErrors
    $taskElapsed = [Diagnostics.Stopwatch]::StartNew()
    while (!$taskClient.WaitForExit(5000)) {
        if ($taskElapsed.Elapsed -ge [TimeSpan]::FromMinutes(4)) {
            throw 'Live desktop verification exceeded four minutes.'
        }
    }
    $taskClient.Refresh()
    if ($taskClient.ExitCode -ne 0) {
        throw ('Live desktop verification failed with exit code ' + $taskClient.ExitCode)
    }
    if (!(Test-Path -LiteralPath $taskReport -PathType Leaf)) {
        throw 'Live desktop verification finished without its expected report.'
    }
}
catch { $taskFailure = $_ }
finally {
    if ($null -ne $taskClient) {
        try {
            if (!$taskClient.HasExited) {
                $taskClient.Kill()
                [void]$taskClient.WaitForExit(5000)
            }
        }
        catch { Write-Warning ('Could not stop the owned verification process: ' + $_.Exception.Message) }
        $taskClient.Dispose()
    }
    foreach ($taskLog in @($taskOutput, $taskErrors, $taskReport)) {
        if (Test-Path -LiteralPath $taskLog -PathType Leaf) {
            Write-Host ('Verification file: ' + $taskLog)
            Get-Content -LiteralPath $taskLog
        }
    }
    Write-Host ('Live desktop verification artifacts: ' + $taskChecks)
}

if ($null -ne $taskFailure) { throw $taskFailure }
