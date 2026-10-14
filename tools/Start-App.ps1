param([string]$DotNetPath,[switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DotNetPath) {
    $riderSdk = Join-Path $env:LOCALAPPDATA 'Programs/Rider/lib/ReSharperHost/windows-x64/dotnet/dotnet.exe'
    $systemDotNet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $riderSdk) {
        $DotNetPath = $riderSdk
    } elseif ($systemDotNet) {
        $DotNetPath = $systemDotNet.Source
    } else {
        throw 'Install the .NET SDK specified in global.json or pass -DotNetPath.'
    }
}
$DotNetPath = (Resolve-Path -LiteralPath $DotNetPath).Path
if ([IO.Path]::GetFileName($DotNetPath) -ne 'dotnet.exe') { throw 'DotNetPath must point to dotnet.exe.' }
$previousEnvironment = @{}
foreach ($name in @('DOTNET_ROOT','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_CLI_TELEMETRY_OPTOUT')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location -LiteralPath $repoRoot
try {
    $env:DOTNET_ROOT = Split-Path -Parent $DotNetPath
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    if (-not $NoBuild) {
        & $DotNetPath build src/UTM4WIN.App/UTM4WIN.App.csproj
        if ($LASTEXITCODE -ne 0) { throw "Application build failed with exit code $LASTEXITCODE." }
    }
    $appPath = Join-Path $repoRoot 'src/UTM4WIN.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/UTM4WIN.App.exe'
    if (-not (Test-Path -LiteralPath $appPath)) { throw 'Build the default Debug configuration before using -NoBuild.' }
    $appProcess = Start-Process -FilePath $appPath -WindowStyle Normal -PassThru
    try {
        $appProcess.WaitForExit()
        if ($appProcess.ExitCode -ne 0) { throw "Application exited with code $($appProcess.ExitCode)." }
    } finally {
        $appProcess.Dispose()
    }
} finally {
    Pop-Location
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}

