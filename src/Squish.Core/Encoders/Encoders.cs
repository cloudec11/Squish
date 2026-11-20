using Squish.Core.Ffmpeg;

namespace Squish.Core.Encoders;

/// <summary>Temp files for one job, all in the output folder. Deleted on success, failure and cancel.</summary>
public sealed class WorkArea : IDisposable
{
    private readonly List<string> _files = [];
    private readonly string _prefix;

    public WorkArea(string directory)
    {
        Directory = directory;
        _prefix = $".squish-{Guid.NewGuid():N}";
        // x264 opens its stats files without long-path support, so they go somewhere short.
        PassLogPrefix = Path.Combine(ShortDirectory, _prefix);
    }

    public string Directory { get; }
    /// <summary>Two-pass log prefix; FFmpeg appends -0.log and -0.log.mbtree.</summary>
    public string PassLogPrefix { get; }

    /// <summary>
    /// A folder short enough to be a process working directory (CreateProcess limit is MAX_PATH).
    /// The output folder when it fits, otherwise the user temp folder. Swept on dispose either way.
    /// </summary>
    public string ShortDirectory => Directory.Length < 240 ? Directory : Path.GetTempPath();

    public string NewTempFile(string extension)
    {
        var path = Path.Combine(Directory, $"{_prefix}-{_files.Count}{extension}.part");
        _files.Add(path);
        return path;
    }

    /// <summary>Stops tracking a file (it was moved to its final name).</summary>
    public void Release(string path) => _files.Remove(path);

    public void Dispose()
    {
        foreach (var f in _files) TryDelete(f);
        try
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory, _prefix + "*")) TryDelete(f);
            if (ShortDirectory != Directory)
                foreach (var f in System.IO.Directory.EnumerateFiles(ShortDirectory, _prefix + "*")) TryDelete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (IOException) { Thread.Sleep(100); } // FFmpeg may still be releasing the handle after a kill.
            catch (UnauthorizedAccessException) { return; }
        }
    }
}

/// <summary>Copy and remux: no re-encoding.</summary>
public sealed class PassThroughEncoder(FfmpegTools tools, ProcessRunner runner, WorkArea work) : IEncoder
{
    public async Task<EncodeResult> RunAsync(EncodePlan plan, IProgress<JobProgress> p, CancellationToken ct)
    {
        var output = work.NewTempFile(plan.OutputExtension);
        switch (plan)
        {
            case CopyPlan:
                p.Report(new(0, "Copying"));
                FileStream src;
                try { src = new FileStream(plan.Info.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SquishException(ErrorCode.FileNotFound,
                        "Couldn't read the original file. It may have been moved, or another app has it open.", ex.Message, ex);
                }
                await using (src)
                await using (var dst = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                break;
            case RemuxPlan remux:
                p.Report(new(0, "Repackaging as MP4"));
                var parser = new FfmpegProgressParser(plan.Info.Duration, (f, eta) => p.Report(new(f * 100, "Repackaging as MP4", eta)));
                var r = await runner.RunAsync(tools.FfmpegPath, VideoArgs.Remux(remux, output), parser.OnLine, ct: ct).ConfigureAwait(false);
                if (!r.Success)
                    throw new SquishException(ErrorCode.EncoderFailed, "Couldn't repackage this video.", r.StderrTail);
                break;
            default:
                throw new ArgumentException("PassThroughEncoder only handles copy and remux plans.");
        }
        p.Report(new(100, "Done"));
        return new EncodeResult(plan, output, new FileInfo(output).Length, 1, plan.Warnings);
    }
}

/// <summary>
/// Runs a VideoPlan. One instance per job: it remembers the pass-1 log so a retry at the same
/// resolution and frame rate reruns pass 2 only (plan section 6, step 7).
/// </summary>
public sealed class VideoEncoder(FfmpegTools tools, ProcessRunner runner, WorkArea work) : IEncoder
{
    private string? _pass1Key;
    private int _pass1Count;

    public async Task<EncodeResult> RunAsync(EncodePlan plan, IProgress<JobProgress> p, CancellationToken ct)
    {
        if (plan is not VideoPlan vp) throw new ArgumentException("VideoEncoder only handles video plans.");
        var output = work.NewTempFile(".mp4");
        double dur = vp.Info.Duration;
        string retry = vp.Attempt > 1 ? $" · attempt {vp.Attempt}" : "";

        // Pass-1 stats depend on the picture going in, not on the bitrate.
        string key = $"{vp.VideoEncoder}|{vp.Width}x{vp.Height}|{vp.Fps}|{vp.ToneMapHdr}|{vp.Deinterlace}";
        string passLog = $"{work.PassLogPrefix}-p{_pass1Count}";
        bool needPass1 = vp.TwoPass && key != _pass1Key;

        // Only x265 needs a working directory (its stats path must be relative). Leave it unset otherwise:
        // CreateProcess rejects working directories longer than MAX_PATH, and output folders can be deeper.
        string? wd = vp.VideoEncoder == "libx265" ? work.ShortDirectory : null;
        const double pass1Share = 0.35;
        if (needPass1)
        {
            _pass1Count++;
            passLog = $"{work.PassLogPrefix}-p{_pass1Count}";
            string step = $"Pass 1 of 2{retry}";
            p.Report(new(0, step));
            var parser1 = new FfmpegProgressParser(dur, (f, eta) => p.Report(new(f * pass1Share * 100, step, Scale(eta, 1 / pass1Share))));
            var r1 = await runner.RunAsync(tools.FfmpegPath, VideoArgs.Pass1(vp, passLog), parser1.OnLine,
                workingDirectory: wd, ct: ct).ConfigureAwait(false);
            if (!r1.Success) throw Fail(r1);
            _pass1Key = key;
        }

        double start = needPass1 ? pass1Share * 100 : 0;
        string step2 = vp.TwoPass ? $"Pass 2 of 2{retry}" : $"Encoding{retry}";
        p.Report(new(start, step2));
        var parser2 = new FfmpegProgressParser(dur, (f, eta) => p.Report(new(start + f * (100 - start), step2, eta)));
        var r2 = await runner.RunAsync(tools.FfmpegPath, VideoArgs.Final(vp, passLog, output), parser2.OnLine,
            workingDirectory: wd, ct: ct).ConfigureAwait(false);
        if (!r2.Success) throw Fail(r2);

        return new EncodeResult(plan, output, new FileInfo(output).Length, vp.Attempt, vp.Warnings);
    }

    private static TimeSpan? Scale(TimeSpan? t, double k) => t is { } v ? TimeSpan.FromSeconds(v.TotalSeconds * k) : null;

    private static SquishException Fail(ProcessResult r)
    {
        var tail = r.StderrTail;
        if (tail.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            return new(ErrorCode.DiskFull, "The disk is full. Free some space and try again.", tail);
        if (tail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return new(ErrorCode.OutputNotWritable, "Can't write to the output folder. Pick a different folder in settings.", tail);
        return new(ErrorCode.EncoderFailed, "The video encoder stopped with an error. The file may be damaged.", tail);
    }
}
