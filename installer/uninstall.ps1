# Uninstalls LightSync (run from Settings > Apps): closes it, removes the app, its shortcuts, its
# Settings > Apps entry and its "Start with Windows" entry.
#
# Kept: your settings and presets (%APPDATA%\LightSync), logs (%LOCALAPPDATA%\LightSync) and the OpenRGB
# that LightSync manages (Program Files\LightSync). Delete those folders by hand to remove everything.

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms

$dest = Join-Path $env:LOCALAPPDATA 'Programs\LightSync'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$answer = [System.Windows.Forms.MessageBox]::Show(
    "Uninstall LightSync?`n`nYour settings and presets are kept, and so is OpenRGB.",
    'LightSync', 'YesNo', 'Question')
if ($answer -ne 'Yes') {
    exit 0
}

# Ask the service to save and exit, then close the app and service.
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

foreach ($link in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'LightSync.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LightSync.lnk'))) {
    Remove-Item $link -Force -ErrorAction SilentlyContinue
}
$run = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).LightSync
if ($run -and $run -like "*$dest*") {
    Remove-ItemProperty -Path $runKey -Name LightSync -ErrorAction SilentlyContinue
}
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LightSync' -Recurse -Force -ErrorAction SilentlyContinue

# This script runs from the folder it removes: delete it from a separate process once this one exits.
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
[System.Windows.Forms.MessageBox]::Show('LightSync was uninstalled.', 'LightSync', 'OK', 'Information') | Out-Null
