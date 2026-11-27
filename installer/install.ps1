# Installs LightSync for the current user: the app and its lighting service go to
# %LOCALAPPDATA%\Programs\LightSync, with Start menu and desktop shortcuts and an entry in Settings > Apps
# that uninstalls it. Run by the setup .exe (build-release.ps1) next to LightSync.zip. The install itself
# needs no administrator rights; Windows asks once so that OpenRGB (which needs them) can start without
# a prompt later, after a reboot too.
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

function Set-OpenRgbNoPrompt {
    # OpenRGB needs administrator rights. Ask Windows once, here, to let LightSync start it without a
    # prompt (two scheduled tasks that run it with highest privileges for this user), so a reboot
    # doesn't ask again. The same tasks LightSync itself sets up (OpenRgbLauncher); skipped when
    # OpenRGB isn't installed yet (LightSync sets them up with its first prompt), when the tasks are
    # already right, or when "Start OpenRGB without a prompt" was turned off.
    $settings = Join-Path $env:APPDATA 'LightSync\app-settings.json'
    if (Test-Path $settings) {
        try {
            if ((Get-Content $settings -Raw | ConvertFrom-Json).OpenRgbWithoutPrompt -eq $false) { return }
        } catch { }
    }
    $programFiles = [Environment]::GetFolderPath('ProgramFiles')
    # Only a copy in Program Files (only administrators can change it): a task that runs a program the
    # user's own programs could replace would let them run anything as administrator.
    $openrgb = @(
        (Join-Path $programFiles 'LightSync\OpenRGB\OpenRGB.exe'),
        (Join-Path $programFiles 'OpenRGB\OpenRGB.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $openrgb) { return }
    $config = Join-Path $programFiles 'LightSync\OpenRGB-config'
    # Must match OpenRgbLauncher.Arguments, or LightSync sets the tasks up again.
    $arguments = "--server --server-host 127.0.0.1 --noautoconnect --startminimized --config `"$config`""
    $task = Get-ScheduledTask -TaskPath '\LightSync\' -TaskName 'Start OpenRGB' -ErrorAction SilentlyContinue
    if ($task -and $task.Actions[0].Execute -eq $openrgb -and $task.Actions[0].Arguments -eq $arguments -and (Test-Path $config)) { return }
    $q = { param($s) "'" + ($s -replace "'", "''") + "'" }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $taskkill = Join-Path ([Environment]::SystemDirectory) 'taskkill.exe'
    $script = @"
New-Item -ItemType Directory -Path $(& $q $config) -Force | Out-Null
`$p = New-ScheduledTaskPrincipal -UserId $(& $q $user) -LogonType Interactive -RunLevel Highest
`$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
`$start = New-ScheduledTaskAction -Execute $(& $q $openrgb) -Argument $(& $q $arguments) -WorkingDirectory $(& $q (Split-Path -Parent $openrgb))
`$stop = New-ScheduledTaskAction -Execute $(& $q $taskkill) -Argument '/IM OpenRGB.exe /F'
Register-ScheduledTask -TaskPath '\LightSync\' -TaskName 'Start OpenRGB' -Action `$start -Principal `$p -Settings `$s -Description 'Started by LightSync: OpenRGB as administrator, without a prompt.' -Force | Out-Null
Register-ScheduledTask -TaskPath '\LightSync\' -TaskName 'Stop OpenRGB' -Action `$stop -Principal `$p -Settings `$s -Description 'Run by LightSync to stop OpenRGB.' -Force | Out-Null
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $powershell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    try {
        Start-Process $powershell -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $encoded" -Verb RunAs -WindowStyle Hidden -Wait
    } catch {
        # Declined: LightSync asks again when it first starts OpenRGB.
    }
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

    # The one permission prompt: OpenRGB then starts without asking, after a reboot too.
    Set-OpenRgbNoPrompt

    Start-Process $exe
    [System.Windows.Forms.MessageBox]::Show(
        "LightSync $version is installed and starting.`n`nIt's in the Start menu and on the desktop, and Settings > Apps can uninstall it.",
        'LightSync', 'OK', 'Information') | Out-Null
} catch {
    [System.Windows.Forms.MessageBox]::Show("LightSync could not be installed:`n`n$($_.Exception.Message)", 'LightSync', 'OK', 'Error') | Out-Null
    exit 1
}
