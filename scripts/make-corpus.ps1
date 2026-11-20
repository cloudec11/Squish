# Generates the test corpus from FFmpeg test sources, so every file is self-made and freely usable.
# Output goes to corpus\ (git-ignored). Re-running skips files that already exist.
#   powershell -File scripts\make-corpus.ps1 [-Long]   (-Long adds the 20-minute clip)
param([switch]$Long)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$ff = Join-Path $root 'tools\ffmpeg\ffmpeg.exe'
$out = Join-Path $root 'corpus'
foreach ($d in 'video','image','animated','hostile') { New-Item -ItemType Directory -Force (Join-Path $out $d) | Out-Null }

function Make($rel, [string[]]$ffargs) {
    $path = Join-Path $out $rel
    if (Test-Path -LiteralPath $path) { return }
    Write-Host "making $rel"
    & $ff -hide_banner -loglevel error -y @ffargs $path
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $rel" }
}

# Busy picture: test pattern plus temporal noise, so it doesn't compress to nothing.
$busy = 'noise=alls=18:allf=t+u'
$tone = 'sine=frequency=440:sample_rate=48000'

# ---- Video
Make 'video\phone_1080p30.mp4'  @('-f','lavfi','-i','testsrc2=s=1920x1080:r=30:d=40','-f','lavfi','-i',"$tone`:d=40",'-vf',$busy,'-c:v','libx264','-preset','veryfast','-crf','16','-c:a','aac','-b:a','192k','-shortest')
Make 'video\phone_4k30.mp4'     @('-f','lavfi','-i','testsrc2=s=3840x2160:r=30:d=15','-f','lavfi','-i',"$tone`:d=15",'-vf',$busy,'-c:v','libx264','-preset','ultrafast','-crf','18','-c:a','aac','-b:a','192k','-shortest')
Make 'video\game_1080p60.mp4'   @('-f','lavfi','-i','testsrc2=s=1920x1080:r=60:d=60','-f','lavfi','-i',"$tone`:d=60",'-vf',$busy,'-c:v','libx264','-preset','ultrafast','-crf','18','-c:a','aac','-b:a','160k','-shortest')
Make 'video\vertical_rotated.mp4' @('-display_rotation','90','-i',(Join-Path $out 'video\phone_1080p30.mp4'),'-c','copy','-t','20')
Make 'video\screen_vfr.mp4'     @('-f','lavfi','-i','testsrc2=s=1920x1080:r=60:d=30','-vf',"$busy,select='not(mod(floor(t*2)\,3))',setpts=N/FRAME_RATE/TB*1.0",'-fps_mode','vfr','-c:v','libx264','-preset','veryfast','-crf','14','-an')
Make 'video\hdr_pq.mp4'         @('-f','lavfi','-i','testsrc2=s=1920x1080:r=30:d=20','-vf',"$busy,format=yuv420p10le",'-c:v','libx265','-preset','ultrafast','-crf','10','-x265-params','log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc','-color_primaries','bt2020','-color_trc','smpte2084','-colorspace','bt2020nc','-tag:v','hvc1')
Make 'video\silent.mp4'         @('-f','lavfi','-i','testsrc2=s=1280x720:r=30:d=40','-vf',$busy,'-c:v','libx264','-preset','veryfast','-crf','12','-an')
Make 'video\surround_51.mp4'    @('-f','lavfi','-i','testsrc2=s=1920x1080:r=30:d=30','-f','lavfi','-i','sine=frequency=300:sample_rate=48000:d=30','-filter_complex','[1:a]pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0[a]','-map','0:v','-map','[a]','-vf',$busy,'-c:v','libx264','-preset','veryfast','-crf','14','-c:a','aac','-b:a','384k')
Make 'video\short_2s.mp4'       @('-f','lavfi','-i','testsrc2=s=1920x1080:r=30:d=2','-f','lavfi','-i',"$tone`:d=2",'-vf',$busy,'-c:v','libx264','-preset','veryfast','-crf','20','-c:a','aac','-shortest')
Make 'video\interlaced.mp4'     @('-f','lavfi','-i','testsrc2=s=1920x1080:r=60:d=30','-vf',"$busy,tinterlace=interleave_top,fieldorder=tff",'-c:v','libx264','-flags','+ildct+ilme','-preset','veryfast','-crf','14','-an')
Make 'video\container.mkv'      @('-f','lavfi','-i','testsrc2=s=1280x720:r=30:d=40','-f','lavfi','-i',"$tone`:d=40",'-vf',$busy,'-c:v','libx264','-preset','veryfast','-crf','14','-c:a','aac','-shortest')
Make 'video\container.webm'     @('-f','lavfi','-i','testsrc2=s=1280x720:r=30:d=30','-f','lavfi','-i',"$tone`:d=30",'-vf',$busy,'-c:v','libvpx-vp9','-b:v','12M','-deadline','realtime','-cpu-used','8','-c:a','libopus','-shortest')
Make 'video\container.avi'      @('-f','lavfi','-i','testsrc2=s=1280x720:r=30:d=30','-f','lavfi','-i',"$tone`:d=30",'-vf',$busy,'-c:v','mpeg4','-q:v','2','-c:a','mp3','-shortest')
Make 'video\container.mov'      @('-f','lavfi','-i','testsrc2=s=1920x1080:r=30:d=30','-f','lavfi','-i',"$tone`:d=30",'-vf',$busy,'-c:v','libx264','-preset','veryfast','-crf','14','-c:a','aac','-shortest','-f','mov')
Make 'video\small_fits.mkv'     @('-f','lavfi','-i','testsrc2=s=640x360:r=30:d=5','-c:v','libx264','-preset','veryfast','-crf','28','-an')
if ($Long) {
    Make 'video\long_20min.mp4' @('-f','lavfi','-i','testsrc2=s=1280x720:r=30:d=1200','-f','lavfi','-i',"$tone`:d=1200",'-vf','noise=alls=6:allf=t','-c:v','libx264','-preset','ultrafast','-crf','23','-c:a','aac','-shortest')
}

# ---- Images
Make 'image\camera_24mp.jpg'   @('-f','lavfi','-i','mandelbrot=s=6000x4000:end_pts=1','-frames:v','1','-vf','noise=alls=30:allf=u','-q:v','1')
Make 'image\screenshot_4k.png' @('-f','lavfi','-i','testsrc2=s=3840x2160','-frames:v','1','-vf','noise=alls=10:allf=u')
Make 'image\alpha.png'         @('-f','lavfi','-i','testsrc2=s=3000x2000','-frames:v','1','-vf','noise=alls=25:allf=u,format=rgba,geq=r=r(X\,Y):g=g(X\,Y):b=b(X\,Y):a=255*(X/W)')
Make 'image\photo.avif'        @('-f','lavfi','-i','mandelbrot=s=4000x3000:end_pts=1','-frames:v','1','-vf','noise=alls=30:allf=u','-c:v','libaom-av1','-crf','10','-still-picture','1')
Make 'image\huge.tiff'         @('-f','lavfi','-i','gradients=s=7000x5000:n=5','-frames:v','1','-vf','noise=alls=30:allf=u,format=rgb24')
Make 'image\tiny.png'          @('-f','lavfi','-i','testsrc2=s=320x240','-frames:v','1')

# ---- Animated
Make 'animated\high_colour.gif' @('-f','lavfi','-i','testsrc2=s=800x450:r=25:d=12','-vf','noise=alls=12:allf=t,split[a][b];[a]palettegen[p];[b][p]paletteuse')
Make 'animated\long_low_colour.gif' @('-f','lavfi','-i','testsrc=s=640x480:r=15:d=60','-vf','split[a][b];[a]palettegen=max_colors=32[p];[b][p]paletteuse')
Make 'animated\animated.webp'   @('-f','lavfi','-i','testsrc2=s=800x450:r=25:d=20','-vf','noise=alls=12:allf=t','-c:v','libwebp_anim','-lossless','1','-loop','0')

# ---- Hostile
$h = Join-Path $out 'hostile'
if (-not (Test-Path "$h\truncated.mp4")) {
    # First third of a real clip: header present, most frames missing.
    $bytes = [IO.File]::ReadAllBytes((Join-Path $out 'video\short_2s.mp4'))
    $fs = [IO.File]::Create("$h\truncated.mp4"); $fs.Write($bytes, 0, [int]($bytes.Length / 3)); $fs.Dispose()
}
if (-not (Test-Path "$h\not_a_video.mp4")) { Set-Content "$h\not_a_video.mp4" 'This is a text file pretending to be a video.' }
if (-not (Test-Path "$h\zero.mp4")) { New-Item -ItemType File "$h\zero.mp4" | Out-Null }
$emoji = Join-Path $h ("path with spaces " + [char]::ConvertFromUtf32(0x1F600) + " " + [string]::new([char[]](0x65E5, 0x672C, 0x8A9E)))
New-Item -ItemType Directory -Force $emoji | Out-Null
if (-not (Test-Path (Join-Path $emoji 'clip.mp4'))) { Copy-Item (Join-Path $out 'video\phone_1080p30.mp4') (Join-Path $emoji 'clip.mp4') }

Get-ChildItem $out -Recurse -File | ForEach-Object { '{0,10:N1} MB  {1}' -f ($_.Length / 1MB), $_.FullName.Substring($out.Length + 1) }
