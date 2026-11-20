using System.Diagnostics;
using Squish.Core.Encoders;
using Squish.Core.Ffmpeg;
using Squish.Core.Planning;
using Squish.Core.Probe;

namespace Squish.Core;

/// <summary>
/// Runs one job: probe, plan, encode, verify, retry, then rename into place.
/// Never writes over the source; temp files live in the output folder and are always cleaned up.
/// </summary>
public sealed class Compressor
{
    private readonly FfmpegTools _tools;
    private readonly ProcessRunner _runner;
    private readonly ILog _log;
    private readonly Tuning _t;
    private readonly Func<CancellationToken, Task<Capabilities>> _caps;

    public IMediaProbe Probe { get; }
    public IPlanner Planner { get; }
    public IVerifier Verifier { get; }

    public Compressor(FfmpegTools tools, ILog? log = null, Tuning? tuning = null,
        Func<CancellationToken, Task<Capabilities>>? capabilities = null)
    {
        _tools = tools;
        _log = log ?? NullLog.Instance;
        _t = tuning ?? Tuning.Default;
        _runner = new ProcessRunner(_log);
        Probe = new MediaProbe(tools, _runner);
        Planner = new Planner(_t);
        Verifier = new Verifier(tools, _runner, _t);
        _caps = capabilities ?? (_ => Task.FromResult(Capabilities.SoftwareOnly));
    }

    public ProcessRunner Runner => _runner;

    /// <summary>Probe and plan only. Cheap enough to drive the card's predicted result.</summary>
    public async Task<(MediaInfo Info, EncodePlan Plan)> PreviewAsync(CompressionRequest req, CancellationToken ct, MediaInfo? info = null)
    {
        info ??= await Probe.ProbeAsync(req.InputPath, ct).ConfigureAwait(false);
        var caps = req.Mode == QualityMode.Fast ? await _caps(ct).ConfigureAwait(false) : Capabilities.SoftwareOnly;
        return (info, Planner.Plan(info, req, caps));
    }

    public async Task<CompressionResult> RunAsync(
        CompressionRequest req, IProgress<JobProgress>? progress = null, CancellationToken ct = default, MediaInfo? info = null)
    {
        var p = progress ?? new Progress<JobProgress>();
        var sw = Stopwatch.StartNew();
        _log.Info($"JOB start {req.InputPath} target={req.TargetBytes} mode={req.Mode} format={req.Format}");

        try
        {
            p.Report(new(0, "Reading file"));
            var (mi, plan) = await PreviewAsync(req, ct, info).ConfigureAwait(false);
            string outDir = ResolveOutputDirectory(req);

            using var work = new WorkArea(outDir);
            var videoEncoder = new VideoEncoder(_tools, _runner, work);
            EncodeResult? fallback = null;
            EncodeResult? final = null;
            int retries = 0;
            var warnings = new List<string>();

            for (int guard = 0; guard < _t.MaxVideoAttempts + 4; guard++)
            {
                ct.ThrowIfCancellationRequested();
                IEncoder encoder = plan switch
                {
                    CopyPlan or RemuxPlan => new PassThroughEncoder(_tools, _runner, work),
                    VideoPlan => videoEncoder,
                    ImagePlan => new ImageEncoder(work, _t),
                    AnimatedPlan => new AnimatedEncoder(_tools, _runner, work, _t),
                    _ => throw new InvalidOperationException(plan.GetType().Name),
                };

                EncodeResult result;
                try
                {
                    result = await encoder.RunAsync(plan, p, ct).ConfigureAwait(false);
                }
                catch (SquishException ex) when (plan is RemuxPlan && ex.Code == ErrorCode.EncoderFailed)
                {
                    // Remux refused (odd stream layout): fall back to a full encode.
                    plan = ForceEncode(mi, req);
                    continue;
                }

                var verdict = await Verifier.CheckAsync(result, result.Plan, ct).ConfigureAwait(false);
                _log.Info($"VERIFY {verdict.Status} bytes={result.OutputBytes} {verdict.Message}");

                if (verdict.Status == VerifyStatus.Pass) { final = result; break; }

                if (verdict.Status == VerifyStatus.Retry)
                {
                    if (!verdict.Fits && fallback is not null) { final = fallback; break; } // a raise overshot
                    if (verdict.Fits) fallback = result;
                    plan = verdict.NextPlan!;
                    retries++;
                    continue;
                }

                // Fail
                if (fallback is not null) { final = fallback; break; }
                if (plan is CopyPlan or RemuxPlan)
                {
                    plan = ForceEncode(mi, req); // e.g. remux grew past the limit
                    continue;
                }
                throw new SquishException(ErrorCode.TargetUnreachable,
                    $"Couldn't get this under {req.TargetBytes / 1_000_000.0:0.#} MB. Pick a larger size"
                    + (mi.Kind == MediaKind.Video ? " or trim the clip." : "."), verdict.Message);
            }

            if (final is null)
                throw new SquishException(ErrorCode.TargetUnreachable, "Couldn't get this under the target. Pick a larger size.");

            // Final guarantee, measured on disk right before the rename.
            long bytes = new FileInfo(final.OutputPath).Length;
            if (bytes > req.TargetBytes)
                throw new SquishException(ErrorCode.TargetUnreachable, "Output came out over the target. Pick a larger size.");

            string finalPath = MoveIntoPlace(work, final.OutputPath, mi.Path, outDir, final.Plan.OutputExtension);
            warnings.AddRange(final.Warnings);

            var outcome = final.Plan is CopyPlan or RemuxPlan ? JobOutcome.AlreadyFits : JobOutcome.Compressed;
            _log.Info($"JOB done {finalPath} {mi.FileBytes} -> {bytes} ({100.0 * bytes / req.TargetBytes:F1}% of target) retries={retries} in {sw.Elapsed.TotalSeconds:F1}s");
            p.Report(new(100, "Done"));
            return new CompressionResult(mi.Path, finalPath, mi.FileBytes, bytes, req.TargetBytes, outcome, retries,
                sw.Elapsed, warnings.Distinct().ToList(), final.Plan.Summary);
        }
        catch (Exception ex)
        {
            var sx = SquishException.From(ex);
            if (sx.Code == ErrorCode.Cancelled) _log.Info($"JOB cancelled {req.InputPath}");
            else _log.Error($"JOB failed {req.InputPath}: {sx.Code} {sx.Message} {sx.Detail}");
            if (ex is OperationCanceledException) throw;
            throw sx;
        }
    }

    /// <summary>Re-plans as if the source were too big, so a failed copy/remux becomes a real encode of the same kind.</summary>
    private EncodePlan ForceEncode(MediaInfo mi, CompressionRequest req) =>
        Planner.Plan(mi with { FileBytes = long.MaxValue }, req, Capabilities.SoftwareOnly);

    private static string ResolveOutputDirectory(CompressionRequest req)
    {
        var dir = req.OutputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(req.InputPath))!;
        try
        {
            Directory.CreateDirectory(dir);
            // Fail fast on a read-only folder rather than after a long encode.
            var probe = Path.Combine(dir, $".squish-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SquishException.From(ex);
        }
        return dir;
    }

    /// <summary>Renames the temp file to &lt;name&gt;_discord.ext, appending " (2)", " (3)" on collision. Never overwrites.</summary>
    internal static string MoveIntoPlace(WorkArea work, string temp, string source, string outDir, string ext)
    {
        string stem = Path.GetFileNameWithoutExtension(source) + "_discord";
        string sourceFull = Path.GetFullPath(source);
        for (int n = 1; n < 1000; n++)
        {
            string name = n == 1 ? $"{stem}{ext}" : $"{stem} ({n}){ext}";
            string candidate = Path.Combine(outDir, name);
            if (string.Equals(Path.GetFullPath(candidate), sourceFull, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidate)) continue;
            try
            {
                File.Move(temp, candidate, overwrite: false);
                work.Release(temp);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate)) { /* lost a race; try the next name */ }
        }
        throw new SquishException(ErrorCode.OutputNotWritable, "Too many files with the same name in the output folder.");
    }
}
