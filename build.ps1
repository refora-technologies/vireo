# Builds the Vireo installer: out\installer\VireoSetup.exe and its checksum, VireoSetup.exe.sha256.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# The publish is self-contained (nothing to install first) and ready-to-run (compiled ahead of time, so it starts quickly
# on slower computers). It is single-file but not compressed: the installer compresses it, and an uncompressed program
# starts faster. Native libraries stay beside Vireo.exe rather than being unpacked to %TEMP% at every start.
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content 'src\Vireo\Vireo.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in src\Vireo\Vireo.csproj.' }

# Inno Setup 6, wherever it is: INNO_ISCC when set, then the usual places.
$iscc = @($env:INNO_ISCC, 'C:\InnoSetup6\ISCC.exe', "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it, or set INNO_ISCC to the path of ISCC.exe.' }

if (-not $SkipTests) {
    Write-Output 'Testing...'
    dotnet test tests\Vireo.Tests -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The tests failed.' }
}

Write-Output "Publishing Vireo $version..."
Remove-Item -Recurse -Force out\publish -ErrorAction SilentlyContinue
dotnet publish src\Vireo -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o out\publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'The publish failed.' }
# Debug symbols are for us, not for the people installing.
Remove-Item out\publish\*.pdb, out\publish\*.xml -ErrorAction SilentlyContinue
# WebView2Loader.dll is already beside Vireo.exe; the copy under runtimes\ is the package's, never loaded.
Remove-Item -Recurse -Force out\publish\runtimes -ErrorAction SilentlyContinue

Write-Output 'Building the installer...'
New-Item -ItemType Directory -Force out\installer | Out-Null
& $iscc /Q "/DAppVersion=$version" installer\setup.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

# The checksum the updater checks every download against (and that anyone can check by hand).
$hash = (Get-FileHash 'out\installer\VireoSetup.exe' -Algorithm SHA256).Hash.ToLower()
Set-Content -Path 'out\installer\VireoSetup.exe.sha256' -Value "$hash  VireoSetup.exe" -NoNewline -Encoding ascii

$size = [math]::Round((Get-Item 'out\installer\VireoSetup.exe').Length / 1MB, 1)
Write-Output "Built out\installer\VireoSetup.exe (Vireo $version, $size MB)"
Write-Output "sha256  $hash"
