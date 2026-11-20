# Per-user install, no admin rights: copies Squish to %LOCALAPPDATA%\Programs\Squish and adds
# a Start menu entry, a "Send to" entry ("Compress for Discord") and an Apps & features entry.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Squish'

Get-Process Squish -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $src '*') $dest -Recurse -Force

$exe = Join-Path $dest 'Squish.exe'
$shell = New-Object -ComObject WScript.Shell
function Shortcut($path, $description) {
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dest
    $s.IconLocation = "$exe,0"
    $s.Description = $description
    $s.Save()
}
Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Squish.lnk') 'Shrink videos and images for Discord'
Shortcut (Join-Path ([Environment]::GetFolderPath('SendTo')) 'Compress for Discord.lnk') 'Compress for Discord with Squish'

$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Squish'
New-Item $key -Force | Out-Null
Set-ItemProperty $key DisplayName 'Squish'
Set-ItemProperty $key DisplayVersion $version
Set-ItemProperty $key Publisher 'Squish'
Set-ItemProperty $key DisplayIcon "$exe,0"
Set-ItemProperty $key InstallLocation $dest
Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
Set-ItemProperty $key NoModify 1 -Type DWord
Set-ItemProperty $key NoRepair 1 -Type DWord
Set-ItemProperty $key EstimatedSize ([int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord

Write-Host "Squish $version installed to $dest"
Start-Process $exe
