# Downloads the pinned FFmpeg build into tools\ffmpeg and checks its SHA-256 (see docs/decisions.md).
# The zip URL always serves the newest release; if the hash differs, update decisions.md after checking -buildconf.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dest = Join-Path $root 'tools\ffmpeg'
$expected = @{
    'ffmpeg.exe'  = '3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC'
    'ffprobe.exe' = 'F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52'
}
if ((Test-Path "$dest\ffmpeg.exe") -and (Get-FileHash "$dest\ffmpeg.exe").Hash -eq $expected['ffmpeg.exe']) {
    Write-Host 'FFmpeg already present and verified.'; return
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) "squish-ffmpeg-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $tmp | Out-Null
try {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile "$tmp\ffmpeg.zip"
    Expand-Archive "$tmp\ffmpeg.zip" "$tmp\x"
    $bin = (Get-ChildItem "$tmp\x" -Recurse -Filter ffmpeg.exe | Select-Object -First 1).DirectoryName
    foreach ($f in $expected.Keys) {
        $hash = (Get-FileHash "$bin\$f").Hash
        if ($hash -ne $expected[$f]) { Write-Warning "$f hash $hash differs from the recorded build. Check -buildconf before using it." }
    }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$bin\ffmpeg.exe", "$bin\ffprobe.exe" $dest -Force
    Copy-Item (Join-Path (Split-Path $bin) 'LICENSE') "$dest\LICENSE.txt" -Force
    Copy-Item (Join-Path (Split-Path $bin) 'README.txt') "$dest\BUILD-README.txt" -Force
    Write-Host "FFmpeg installed to $dest"
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
