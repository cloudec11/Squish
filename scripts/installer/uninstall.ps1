# Removes Squish installed by install.ps1. Offers to remove settings and logs too.
$ErrorActionPreference = 'Continue'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Squish'
Get-Process Squish -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Squish.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('SendTo')) 'Compress for Discord.lnk') -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Squish' -Recurse -ErrorAction SilentlyContinue

Add-Type -AssemblyName PresentationFramework
$data = Join-Path $env:LOCALAPPDATA 'Squish'
if (Test-Path $data) {
    $answer = [System.Windows.MessageBox]::Show("Also remove Squish settings and logs?`n$data", 'Uninstall Squish', 'YesNo', 'Question')
    if ($answer -eq 'Yes') { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
# The script lives in the folder it deletes; remove it from a detached process.
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
