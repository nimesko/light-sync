# Builds a release of LightSync and its installer: dist\LightSync-Setup-<version>.exe.
#
#   powershell -ExecutionPolicy Bypass -File installer\build-release.ps1
#
# Needs the build requirements from the README (Rust, .NET 10 SDK). The installer is made with IExpress,
# which comes with Windows: a self-extracting .exe that runs install.ps1 (a per-user install, no
# administrator rights). The app is self-contained, so the PC it goes to needs no .NET installed.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist 'publish'
$staging = Join-Path $dist 'staging'
Set-Location $root

Write-Host 'Building the lighting service (lightsyncd)...'
cargo build --release -p lightsyncd
if ($LASTEXITCODE) { throw 'cargo build failed' }

Write-Host 'Publishing the app (self-contained)...'
foreach ($d in $publish, $staging) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d | Out-Null
}
dotnet publish ui\LightSync.App -c Release -r win-x64 --self-contained true -o $publish -nologo `
    -p:SatelliteResourceLanguages=en -p:DebugType=none -p:GenerateDocumentationFile=false
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
Copy-Item target\release\lightsyncd.exe $publish
Copy-Item LICENSE, THIRD_PARTY_NOTICES.md $publish

$version = (Get-Item (Join-Path $publish 'LightSync.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$setup = Join-Path $dist "LightSync-Setup-$version.exe"

Write-Host 'Packing...'
# Stored, not compressed: the installer's cabinet compresses everything better in one go.
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $staging 'LightSync.zip') -CompressionLevel NoCompression
Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') $staging

$sed = Join-Path $staging 'LightSync.sed'
@"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=Install LightSync $version?
DisplayLicense=
FinishMessage=
TargetName=$setup
FriendlyName=LightSync $version
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
FILE0="LightSync.zip"
FILE1="install.ps1"
FILE2="uninstall.ps1"
[SourceFiles]
SourceFiles0=$staging\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@ | Set-Content -Path $sed -Encoding ASCII

Write-Host 'Making the installer...'
if (Test-Path $setup) { Remove-Item $setup -Force }
Start-Process -FilePath "$env:WINDIR\System32\iexpress.exe" -ArgumentList '/N', '/Q', $sed -Wait
if (-not (Test-Path $setup)) { throw 'IExpress did not make the installer' }
Remove-Item $staging -Recurse -Force
Write-Host ("Done: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
