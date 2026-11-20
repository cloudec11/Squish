# Builds a self-contained release of Squish for win-x64 and packs a portable zip.
#   powershell -File scripts\publish.ps1
# Output: artifacts\Squish-<version>-win-x64.zip  (unzip anywhere, or run install.cmd inside it)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$pub = Join-Path $root 'artifacts\publish\Squish'
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue

& (Join-Path $PSScriptRoot 'fetch-ffmpeg.ps1')

dotnet publish (Join-Path $root 'src\Squish.App\Squish.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishReadyToRun=true -p:PublishTrimmed=true -p:TrimMode=partial -o $pub -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Copy-Item (Join-Path $PSScriptRoot 'installer\install.ps1'), (Join-Path $PSScriptRoot 'installer\uninstall.ps1'), (Join-Path $PSScriptRoot 'installer\install.cmd') $pub
Remove-Item (Join-Path $pub '*.pdb') -ErrorAction SilentlyContinue

$version = (Get-Item (Join-Path $pub 'Squish.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $root "artifacts\Squish-$version-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -CompressionLevel Optimal

$size = (Get-ChildItem $pub -Recurse -File | Measure-Object Length -Sum).Sum
'{0}  ({1:N0} MB installed, {2:N0} MB zip)' -f $zip, ($size / 1MB), ((Get-Item $zip).Length / 1MB)
