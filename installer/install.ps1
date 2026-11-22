# Installs LightSync for the current user (no administrator rights needed): the app and its lighting
# service go to %LOCALAPPDATA%\Programs\LightSync, with Start menu and desktop shortcuts and an entry in
# Settings > Apps that uninstalls it. Run by the setup .exe (build-release.ps1) next to LightSync.zip.
#
# Settings (%APPDATA%\LightSync) and the OpenRGB that LightSync manages (Program Files\LightSync) are
# not touched, so an update keeps everything as it was.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LightSync'
$exe = Join-Path $dest 'LightSync.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LightSync'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-LightSync {
    # Ask the service to save and exit, then close whatever is left of the app and service.
    try {
        $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', 'lightsync', [IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(1500)
        $w = New-Object IO.StreamWriter($pipe)
        $w.AutoFlush = $true
        $r = New-Object IO.StreamReader($pipe)
        $w.WriteLine('{"jsonrpc":"2.0","id":1,"method":"shutdown"}')
        $null = $r.ReadLine()
        $pipe.Dispose()
    } catch { }
    Get-Process LightSync, lightsyncd -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dest
    $s.IconLocation = "$exe,0"
    $s.Description = 'LightSync: one place for all your PC lighting'
    $s.Save()
}

try {
    Stop-LightSync

    # A clean copy of the app (an update replaces the old files).
    if (Test-Path $dest) {
        Get-ChildItem $dest -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $dest | Out-Null
    }
    # ZipFile unpacks the ~250 MB app in about a second (Expand-Archive takes several).
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $here 'LightSync.zip'), $dest)
    Copy-Item (Join-Path $here 'uninstall.ps1') $dest -Force

    # Shortcuts.
    $programs = [Environment]::GetFolderPath('Programs')
    New-Shortcut (Join-Path $programs 'LightSync.lnk')
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LightSync.lnk')

    # Settings > Apps.
    $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    $size = [int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    New-Item -Path $uninstallKey -Force | Out-Null
    $values = @{
        DisplayName     = 'LightSync'
        DisplayVersion  = $version
        Publisher       = 'Rogerio Tristao Junior'
        DisplayIcon     = "$exe,0"
        InstallLocation = $dest
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dest\uninstall.ps1`""
        URLInfoAbout    = 'https://github.com/nimesko/light-sync'
    }
    foreach ($k in $values.Keys) { Set-ItemProperty -Path $uninstallKey -Name $k -Value $values[$k] }
    Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstallKey -Name EstimatedSize -Value $size -Type DWord

    # "Start with Windows" stays as it was, now pointing at the installed app.
    if ((Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).LightSync) {
        Set-ItemProperty -Path $runKey -Name LightSync -Value "`"$exe`" --tray"
    }

    Start-Process $exe
    [System.Windows.Forms.MessageBox]::Show(
        "LightSync $version is installed and starting.`n`nIt's in the Start menu and on the desktop, and Settings > Apps can uninstall it.",
        'LightSync', 'OK', 'Information') | Out-Null
} catch {
    [System.Windows.Forms.MessageBox]::Show("LightSync could not be installed:`n`n$($_.Exception.Message)", 'LightSync', 'OK', 'Error') | Out-Null
    exit 1
}
