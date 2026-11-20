using System.Globalization;
using Squish.Core.Ffmpeg;
using Squish.Core.Planning;

namespace Squish.Core.Encoders;

/// <summary>
/// GIF output through FFmpeg palettegen/paletteuse, one palette for the clip (plan section 7).
/// Search order: scale in 0.85 steps to a 320 px long edge, then 20 and 15 fps, then 128 and 64 colours.
/// </summary>
public sealed class AnimatedEncoder(FfmpegTools tools, ProcessRunner runner, WorkArea work, Tuning? tuning = null) : IEncoder
{
    private readonly Tuning _t = tuning ?? Tuning.Default;

    public static IReadOnlyList<string> Args(string input, int w, int h, double fps, int colours, string output)
    {
        string fpsArg = VideoArgs.FpsArg(fps);
        string graph =
            $"fps={fpsArg},scale={w}:{h}:flags=lanczos,split[a][b];" +
            $"[a]palettegen=max_colors={colours.ToString(CultureInfo.InvariantCulture)}:stats_mode=full[p];" +
            "[b][p]paletteuse=dither=sierra2_4a:alpha_threshold=128";
        return ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
                "-i", input, "-filter_complex", graph, "-loop", "0", "-map_metadata", "-1", "-f", "gif", output];
    }

    public async Task<EncodeResult> RunAsync(EncodePlan plan, IProgress<JobProgress> p, CancellationToken ct)
    {
        var ap = (AnimatedPlan)plan;
        long fitBytes = (long)(plan.Request.TargetBytes * _t.AimFraction);
        int srcW = ap.Info.DisplayWidth, srcH = ap.Info.DisplayHeight;
        int srcLong = Math.Max(srcW, srcH);

        int w = ap.Width, h = ap.Height;
        double fps = ap.Fps;
        int colours = ap.Colours;
        var fpsSteps = new Queue<double>(_t.GifFpsSteps.Where(f => f < fps));
        var colourSteps = new Queue<int>(_t.GifColourSteps.Where(c => c < colours));
        string? best = null;
        long bestBytes = 0;
        bool triedLarger = false;
        (int W, int H, double Fps, int Colours) bestParams = default;

        for (int attempt = 1; attempt <= _t.GifMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var output = work.NewTempFile(".gif");
            string step = attempt == 1 ? "Encoding GIF" : $"Encoding GIF · attempt {attempt}";
            double baseP = Math.Min(90, (attempt - 1) * 100.0 / _t.GifMaxAttempts);
            var parser = new FfmpegProgressParser(ap.Info.Duration,
                (f, eta) => p.Report(new(baseP + f * (100 - baseP) * 0.5, step, null)));
            p.Report(new(baseP, step));
            var r = await runner.RunAsync(tools.FfmpegPath, Args(ap.Info.Path, w, h, fps, colours, output), parser.OnLine, ct: ct)
                .ConfigureAwait(false);
            if (!r.Success)
                throw new SquishException(ErrorCode.EncoderFailed, "Couldn't convert this animation. The file may be damaged.", r.StderrTail);

            long size = new FileInfo(output).Length;
            if (size <= fitBytes)
            {
                best = output; bestBytes = size; bestParams = (w, h, fps, colours);
                // A first guess that lands far under the target leaves quality behind: try one step up, once.
                if (!triedLarger && size < fitBytes * 0.7 && w < srcW)
                {
                    triedLarger = true;
                    (w, h) = AnimatedPlanner.ScaleDims(srcW, srcH, Math.Min(1, w / (double)srcW / _t.GifScaleStep), 2);
                    continue;
                }
                break;
            }
            if (best is not null) break; // the step up overshot; keep the earlier fit

            // Over: next lever.
            int longEdge = Math.Max(w, h);
            int floor = Math.Min(_t.GifLongEdgeFloor, srcLong);
            if (longEdge > floor)
            {
                double ratio = Math.Sqrt(fitBytes / (double)size);
                double current = longEdge / (double)srcLong;
                double target = Math.Min(current * _t.GifScaleStep, AnimatedPlanner.SnapScale(current * ratio, _t.GifScaleStep));
                (w, h) = AnimatedPlanner.ScaleDims(srcW, srcH, target, _t.GifLongEdgeFloor);
            }
            else if (fpsSteps.Count > 0) fps = fpsSteps.Dequeue();
            else if (colourSteps.Count > 0) colours = colourSteps.Dequeue();
            else if (longEdge > 64)
                (w, h) = AnimatedPlanner.ScaleDims(srcW, srcH, longEdge * _t.GifScaleStep / srcLong, 2);
            else break;
        }

        if (best is null)
            throw new SquishException(ErrorCode.TargetUnreachable,
                "This animation can't get small enough as a GIF. Turn on Convert to video, or pick a larger size.");

        (w, h, fps, colours) = bestParams;
        var warnings = new List<string>(plan.Warnings);
        if (Math.Max(w, h) < Math.Min(_t.GifLongEdgeFloor, srcLong) || colours < 256 || fps < Math.Min(ap.Fps, 20))
            warnings.Add("Reduced heavily to fit. Convert to video keeps far more quality.");
        var final = ap with { Width = w, Height = h, Fps = fps, Colours = colours, Summary = $"{w}×{h} · {VideoPlanner.FormatFps(fps)} fps · {colours} colours" };
        p.Report(new(100, "Done"));
        return new EncodeResult(final, best, bestBytes, 1, warnings);
    }
}
