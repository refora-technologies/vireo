# Builds the installer and installs Vireo for the current user, quietly: the same path an update takes.
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1             (add -NoBuild to reuse out\installer)
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$setup = Join-Path $root 'out\installer\VireoSetup.exe'

if (-not $NoBuild) {
    & (Join-Path $root 'build.ps1') -SkipTests
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}
if (-not (Test-Path $setup)) { throw "Nothing to install: $setup is missing." }

# A running Vireo is closed by the installer (it saves its tabs first).
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CLOSEAPPLICATIONS' -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "Setup ended with code $($p.ExitCode)." }
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Vireo\Vireo.exe'
Write-Host "Installed Vireo $((Get-Item $exe).VersionInfo.ProductVersion) to $(Split-Path $exe)"
