using System.Diagnostics;
using Squish.Core.Ffmpeg;

namespace Squish.Core.Tests;

/// <summary>
/// Runs real FFmpeg on small generated clips. Skips itself (passes trivially) when FFmpeg isn't found,
/// so the unit tests still run on a machine without tools\ffmpeg.
/// </summary>
public sealed class IntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"squish-it-{Guid.NewGuid():N}");
    private readonly FfmpegTools? _tools;

    public IntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        try { _tools = FfmpegTools.Locate(); } catch (SquishException) { _tools = null; }
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private Compressor NewCompressor() => new(_tools!);

    private async Task<string> MakeClip(string name, int seconds, string size = "1280x720", bool noise = true, string? dir = null)
    {
        var path = Path.Combine(dir ?? _dir, name);
        var vf = noise ? "noise=alls=20:allf=t" : "null";
        var r = await new ProcessRunner().RunAsync(_tools!.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=s={size}:r=30:d={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:d={seconds}",
            "-vf", vf, "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-c:a", "aac", "-shortest", path,
        ]);
        Assert.True(r.Success, r.StderrTail);
        return path;
    }

    private string[] Leftovers(string dir) =>
        Directory.EnumerateFiles(dir, ".squish*", SearchOption.AllDirectories).ToArray();

    [Fact]
    public async Task Video_lands_inside_the_size_window()
    {
        if (_tools is null) return;
        var clip = await MakeClip("clip.mp4", 10);
        long target = 2_000_000;
        Assert.True(new FileInfo(clip).Length > target, "test clip should start over the target");

        var r = await NewCompressor().RunAsync(new CompressionRequest(clip, null, target));
        Assert.Equal(JobOutcome.Compressed, r.Outcome);
        Assert.True(r.OutputBytes <= target);
        Assert.InRange(100.0 * r.OutputBytes / target, 93, 99.5);
        Assert.EndsWith("clip_discord.mp4", r.OutputPath);
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public async Task Cancel_kills_ffmpeg_within_a_second_and_cleans_up()
    {
        if (_tools is null) return;
        var clip = await MakeClip("long.mp4", 40, "1920x1080");
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var progress = new SyncProgress(p => { if (p.Percent > 1) started.TrySetResult(); });
        var job = NewCompressor().RunAsync(new CompressionRequest(clip, null, 3_000_000), progress, cts.Token);

        await Task.WhenAny(started.Task, Task.Delay(30_000));
        Assert.True(started.Task.IsCompleted, "encode never reported progress");
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"cancel took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Empty(Leftovers(_dir));
        Assert.False(File.Exists(Path.Combine(_dir, "long_discord.mp4")));
        // RunAsync only throws after WaitForExit on the killed process, so the timing above covers "process gone".
    }

    [Theory]
    [InlineData("zero.mp4", ErrorCode.EmptyFile)]
    [InlineData("text.mp4", ErrorCode.Unsupported)]
    public async Task Hostile_files_fail_cleanly(string name, ErrorCode expected)
    {
        if (_tools is null) return;
        var path = Path.Combine(_dir, name);
        if (name == "zero.mp4") File.WriteAllBytes(path, []);
        else File.WriteAllText(path, "not a video at all");
        var ex = await Assert.ThrowsAsync<SquishException>(() => NewCompressor().RunAsync(new CompressionRequest(path, null, 20_000_000)));
        Assert.Equal(expected, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public async Task Truncated_file_fails_or_succeeds_but_never_crashes()
    {
        if (_tools is null) return;
        var clip = await MakeClip("whole.mp4", 6);
        var bytes = File.ReadAllBytes(clip);
        var cut = Path.Combine(_dir, "cut.mp4");
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 3)]);
        try
        {
            var r = await NewCompressor().RunAsync(new CompressionRequest(cut, null, 500_000));
            Assert.True(r.OutputBytes <= 500_000);
        }
        catch (SquishException ex)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public async Task Emoji_spaces_and_non_latin_paths_work()
    {
        if (_tools is null) return;
        var dir = Path.Combine(_dir, "clips with spaces 😀 日本語");
        Directory.CreateDirectory(dir);
        var clip = await MakeClip("vidéo (1) 😀.mp4", 4, dir: dir);
        var r = await NewCompressor().RunAsync(new CompressionRequest(clip, null, 400_000));
        Assert.True(File.Exists(r.OutputPath));
        Assert.Contains("😀", r.OutputPath);
        Assert.True(r.OutputBytes <= 400_000);
    }

    [Fact]
    public async Task Paths_over_260_characters_work()
    {
        if (_tools is null) return;
        var dir = _dir;
        while (dir.Length < 280) dir = Path.Combine(dir, "a-fairly-long-folder-name-for-testing");
        Directory.CreateDirectory(dir);
        var clip = await MakeClip("clip.mp4", 3, "640x360");
        var deep = Path.Combine(dir, "clip.mp4");
        File.Copy(clip, deep);
        Assert.True(deep.Length > 260);
        var r = await NewCompressor().RunAsync(new CompressionRequest(deep, null, 150_000));
        Assert.True(File.Exists(r.OutputPath));
        Assert.True(r.OutputBytes <= 150_000);
    }

    [Fact]
    public async Task Name_collisions_get_numbered_and_never_overwrite()
    {
        if (_tools is null) return;
        var clip = await MakeClip("same.mp4", 3, "640x360");
        var existing = Path.Combine(_dir, "same_discord.mp4");
        File.WriteAllText(existing, "keep me");
        var r = await NewCompressor().RunAsync(new CompressionRequest(clip, null, 150_000));
        Assert.EndsWith("same_discord (2).mp4", r.OutputPath);
        Assert.Equal("keep me", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Source_already_under_target_is_copied_untouched()
    {
        if (_tools is null) return;
        var clip = await MakeClip("small.mp4", 2, "320x240", noise: false);
        var r = await NewCompressor().RunAsync(new CompressionRequest(clip, null, 20_000_000));
        Assert.Equal(JobOutcome.AlreadyFits, r.Outcome);
        Assert.Equal(File.ReadAllBytes(clip), File.ReadAllBytes(r.OutputPath));
    }

    [Fact]
    public async Task Read_only_output_folder_fails_fast()
    {
        if (_tools is null) return;
        var clip = await MakeClip("ro.mp4", 2, "320x240");
        // A path under a file (not a folder) can never be created: a portable "unwritable" folder.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "");
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<SquishException>(() =>
            NewCompressor().RunAsync(new CompressionRequest(clip, Path.Combine(blocker, "out"), 100_000)));
        Assert.Equal(ErrorCode.OutputNotWritable, ex.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Png_with_alpha_becomes_webp_when_too_big()
    {
        if (_tools is null) return;
        var png = Path.Combine(_dir, "alpha.png");
        var r0 = await new ProcessRunner().RunAsync(_tools.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1600x1200", "-frames:v", "1",
            "-vf", "noise=alls=40:allf=u,format=rgba,geq=r=r(X\\,Y):g=g(X\\,Y):b=b(X\\,Y):a=255*(X/W)", png,
        ]);
        Assert.True(r0.Success, r0.StderrTail);
        long target = new FileInfo(png).Length / 3;
        var r = await NewCompressor().RunAsync(new CompressionRequest(png, null, target));
        Assert.EndsWith(".webp", r.OutputPath);
        Assert.True(r.OutputBytes <= target);
    }

    [Fact]
    public async Task Output_folder_over_260_characters_works()
    {
        if (_tools is null) return;
        var clip = await MakeClip("deepout.mp4", 3, "640x360");
        var outDir = _dir;
        while (outDir.Length < 290) outDir = Path.Combine(outDir, "an-output-folder-that-is-deep");
        var r = await NewCompressor().RunAsync(new CompressionRequest(clip, outDir, 150_000));
        Assert.True(File.Exists(r.OutputPath));
        Assert.True(r.OutputBytes <= 150_000);
        Assert.Empty(Leftovers(outDir));
    }

    [Fact]
    public async Task Panorama_wider_than_webp_limit_still_compresses()
    {
        if (_tools is null) return;
        var png = Path.Combine(_dir, "pano.png");
        var r0 = await new ProcessRunner().RunAsync(_tools.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=20000x600", "-frames:v", "1",
            "-vf", "noise=alls=40:allf=u,format=rgba,geq=r=r(X\\,Y):g=g(X\\,Y):b=b(X\\,Y):a=255*(Y/H)", png,
        ]);
        Assert.True(r0.Success, r0.StderrTail);
        long target = new FileInfo(png).Length / 4;
        var r = await NewCompressor().RunAsync(new CompressionRequest(png, null, target));
        Assert.EndsWith(".webp", r.OutputPath);
        Assert.True(r.OutputBytes <= target);
    }

    [Fact]
    public async Task Cancelled_capability_check_does_not_poison_later_callers()
    {
        if (_tools is null) return;
        var detector = new CapabilityDetector(_tools, new ProcessRunner(), cachePath: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.GetAsync("k", cts.Token));
        var caps = await detector.GetAsync("k");
        Assert.True(caps.Has("libx264"));
    }
    /// <summary>IProgress that runs inline (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<JobProgress> a) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => a(value);
    }
}
