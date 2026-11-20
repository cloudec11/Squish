using Squish.Core.Encoders;
using Squish.Core.Ffmpeg;
using Squish.Core.Planning;

namespace Squish.Core;

/// <summary>
/// Measures the real file on disk, checks ffprobe can read it, and decides pass, retry or fail
/// (plan section 6, step 7). Stateless: retry state travels in the VideoPlan.
/// </summary>
public sealed class Verifier(FfmpegTools tools, ProcessRunner runner, Tuning? tuning = null) : IVerifier
{
    private readonly Tuning _t = tuning ?? Tuning.Default;

    public async Task<VerifyResult> CheckAsync(EncodeResult result, EncodePlan plan, CancellationToken ct)
    {
        if (!File.Exists(result.OutputPath))
            return new(VerifyStatus.Fail, "The encoder didn't produce a file.", false);
        long actual = new FileInfo(result.OutputPath).Length;
        if (actual == 0)
            return new(VerifyStatus.Fail, "The encoder produced an empty file.", false);

        if (!await PlayableAsync(result.OutputPath, ct).ConfigureAwait(false))
            return new(VerifyStatus.Fail, "The output file can't be read back. The source may be damaged.", false);

        return Decide(result.Plan, actual);
    }

    /// <summary>The size decision, separate from I/O so it can be unit-tested.</summary>
    public VerifyResult Decide(EncodePlan plan, long actual)
    {
        long limit = plan.Request.TargetBytes;
        bool fits = actual <= limit * _t.MissFraction;

        if (plan is not VideoPlan vp)
            return fits ? VerifyResult.Pass()
                        : new(VerifyStatus.Fail, "Output is over the target.", false);

        if (!fits)
        {
            var next = ShrinkPlan(vp, limit, actual);
            return next is null
                ? new(VerifyStatus.Fail, "Couldn't get under the target.", false)
                : new(VerifyStatus.Retry, $"Over by {actual - limit * _t.MissFraction:N0} bytes; retrying.", false, next);
        }

        if (actual < limit * _t.UnderuseFraction && !vp.CappedAtSource && !vp.Raised && vp.Attempt < _t.MaxVideoAttempts)
        {
            double factor = Math.Min(_t.MaxRaiseFactor, limit * vp.AimFraction / actual);
            if (factor > 1.01)
                return new(VerifyStatus.Retry, "Under-used the target; raising bitrate once.", true,
                    vp with { VideoKbps = (int)(vp.VideoKbps * factor), Raised = true, Attempt = vp.Attempt + 1 });
        }
        return VerifyResult.Pass();
    }

    internal VideoPlan? ShrinkPlan(VideoPlan vp, long limit, long actual)
    {
        if (vp.Attempt >= _t.MaxVideoAttempts) return null;
        double factor = limit / (double)actual * _t.RetryShrink;
        int kbps = Math.Max(20, (int)(vp.VideoKbps * factor));

        if (vp.RetriesOnRung < _t.RetriesPerRung)
            return vp with { VideoKbps = kbps, RetriesOnRung = vp.RetriesOnRung + 1, Attempt = vp.Attempt + 1 };

        // Hardware encoders that keep missing fall back to software.
        if (VideoArgs.IsHardware(vp.VideoEncoder))
            return vp with { VideoEncoder = "libx264", VideoKbps = kbps, RetriesOnRung = 0, Attempt = vp.Attempt + 1, AimFraction = _t.AimFraction };

        // Both retries on this rung missed: drop one rung and start both passes again.
        // Same rule as the planner: above 30 fps, halve the frame rate before going below 720p.
        var rungs = new VideoPlanner(_t).RungsFor(vp.Info);
        int current = Math.Clamp(vp.Rung, 0, rungs.Count - 1);
        int nextRung = current + 1;
        bool wouldGoBelow720 = nextRung < rungs.Count && rungs[nextRung] < 720;
        if (vp.Fps > 30 && wouldGoBelow720 && rungs[current] >= 720)
        {
            double half = vp.Fps / 2;
            return vp with
            {
                Fps = half, VideoKbps = kbps, RetriesOnRung = 0, Attempt = vp.Attempt + 1,
                Summary = $"{rungs[current]}p · {VideoPlanner.FormatFps(half)} fps",
            };
        }
        if (nextRung < rungs.Count)
        {
            var (w, h) = VideoPlanner.DimsFor(vp.Info, rungs[nextRung]);
            double fps = wouldGoBelow720 && vp.Fps > 30 ? vp.Fps / 2 : vp.Fps;
            return vp with
            {
                Width = w, Height = h, Rung = nextRung, Fps = fps, VideoKbps = kbps,
                RetriesOnRung = 0, Attempt = vp.Attempt + 1,
                Summary = $"{rungs[nextRung]}p · {VideoPlanner.FormatFps(fps)} fps",
            };
        }
        return vp with { VideoKbps = kbps, Attempt = vp.Attempt + 1 };
    }

    private async Task<bool> PlayableAsync(string path, CancellationToken ct)
    {
        var r = await runner.RunAsync(tools.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", "--", path],
            lowPriority: false, ct: ct).ConfigureAwait(false);
        return r.Success && (r.Stdout.Contains("video") || r.Stdout.Contains("image"));
    }
}
