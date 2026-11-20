using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using Squish.Core;
using Squish.Core.Ffmpeg;

// Squish.Bench: encodes the corpus at each target and reports size accuracy, quality and speed (plan section 13).
//   dotnet run --project bench/Squish.Bench -- [--corpus corpus] [--targets 10,20,50] [--only video|image|animated]
//                                              [--match substring] [--no-quality] [--keep]
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string corpus = "corpus";
double[] targets = [10, 20, 50];
string? only = null, match = null;
bool quality = true, keep = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--corpus": corpus = args[++i]; break;
        case "--targets": targets = args[++i].Split(',').Select(double.Parse).ToArray(); break;
        case "--only": only = args[++i]; break;
        case "--match": match = args[++i]; break;
        case "--no-quality": quality = false; break;
        case "--keep": keep = true; break;
    }
}

corpus = Path.GetFullPath(corpus);
var tools = FfmpegTools.Locate();
var runner = new ProcessRunner();
var log = new FileLog(Path.Combine(AppPaths.DataRoot, "bench-logs"));
var compressor = new Compressor(tools, log);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var resultsDir = Path.GetFullPath(Path.Combine("bench-results", stamp));
var outDir = Path.Combine(resultsDir, "out");
Directory.CreateDirectory(outDir);

var dirs = new[] { "video", "image", "animated", "hostile" }.Where(d => only is null || d == only);
var files = dirs.SelectMany(d => Directory.Exists(Path.Combine(corpus, d))
        ? Directory.EnumerateFiles(Path.Combine(corpus, d), "*", SearchOption.AllDirectories) : [])
    .Where(f => match is null || f.Contains(match, StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f).ToList();

var csv = new StringBuilder("file,kind,target_mb,ok,outcome,input_bytes,output_bytes,percent_of_target,metric,score,seconds,retries,summary,error\n");
var rows = new List<Row>();
Console.WriteLine($"Bench {stamp}: {files.Count} files x {targets.Length} targets -> {resultsDir}");

foreach (var file in files)
{
    string rel = Path.GetRelativePath(corpus, file);
    string kind = rel.Split(Path.DirectorySeparatorChar)[0];
    foreach (var mb in targets)
    {
        var req = new CompressionRequest(file, outDir, (long)(mb * 1_000_000));
        var sw = Stopwatch.StartNew();
        Row row;
        try
        {
            var r = await compressor.RunAsync(req);
            double pct = 100.0 * r.OutputBytes / r.TargetBytes;
            (string metric, double? score) = (null!, null);
            if (quality && r.Outcome == JobOutcome.Compressed)
                (metric, score) = kind == "image" ? ("SSIM", Ssim(file, r.OutputPath)) : ("VMAF", await Vmaf(file, r.OutputPath));
            row = new Row(rel, kind, mb, true, r.Outcome.ToString(), r.InputBytes, r.OutputBytes, pct, metric, score, sw.Elapsed.TotalSeconds, r.Retries, r.Summary, null);
            if (!keep) File.Delete(r.OutputPath);
        }
        catch (Exception ex)
        {
            var sx = SquishException.From(ex);
            row = new Row(rel, kind, mb, false, sx.Code.ToString(), new FileInfo(file).Length, 0, 0, null, null, sw.Elapsed.TotalSeconds, 0, "", sx.Message);
        }
        rows.Add(row);
        csv.AppendLine(string.Join(',', Q(row.File), row.Kind, row.TargetMb, row.Ok, row.Outcome, row.InputBytes, row.OutputBytes,
            row.Percent.ToString("F2"), row.Metric, row.Score?.ToString("F2"), row.Seconds.ToString("F1"), row.Retries, Q(row.Summary), Q(row.Error)));
        Console.WriteLine(row.Ok
            ? $"  {rel,-40} {mb,4} MB  {row.Percent,6:F1}%  {row.Outcome,-11} {row.Metric} {row.Score:F2}  {row.Seconds,6:F1}s  r={row.Retries}  {row.Summary}"
            : $"  {rel,-40} {mb,4} MB  FAILED {row.Outcome}: {row.Error}");
        File.WriteAllText(Path.Combine(resultsDir, "results.csv"), csv.ToString());
    }
}

// ---- Report against the acceptance criteria
var ok = rows.Where(r => r.Ok).ToList();
int over = ok.Count(r => r.OutputBytes > r.TargetMb * 1_000_000);
var compressedVideo = ok.Where(r => r.Kind == "video" && r.Outcome == nameof(JobOutcome.Compressed)).ToList();
int inWindow = compressedVideo.Count(r => r.Percent is >= 93 and <= 99.5);
var report = new StringBuilder();
report.AppendLine($"Squish bench {stamp}");
report.AppendLine($"Runs: {rows.Count}, succeeded: {ok.Count}, failed: {rows.Count - ok.Count}");
report.AppendLine($"Outputs over target: {over}   (criterion: 0)");
report.AppendLine($"Compressed videos in 93-99.5% of target: {inWindow}/{compressedVideo.Count}");
foreach (var r in compressedVideo.Where(r => r.Percent is < 93 or > 99.5))
    report.AppendLine($"  outside window: {r.File} @ {r.TargetMb} MB = {r.Percent:F1}%");
foreach (var g in ok.Where(r => r.Score is not null).GroupBy(r => (r.Kind, r.Metric)))
    report.AppendLine($"Mean {g.Key.Metric} ({g.Key.Kind}): {g.Average(r => r.Score!.Value):F2}");
report.AppendLine("Failures:");
foreach (var r in rows.Where(r => !r.Ok)) report.AppendLine($"  {r.File} @ {r.TargetMb} MB: {r.Outcome} - {r.Error}");
File.WriteAllText(Path.Combine(resultsDir, "report.txt"), report.ToString());
Console.WriteLine();
Console.WriteLine(report);
if (!keep) try { Directory.Delete(outDir, true); } catch { }
return over == 0 ? 0 : 1;

// ---------------- helpers

static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";

double? Ssim(string source, string output)
{
    try
    {
        using var a = new MagickImage(source);
        a.AutoOrient();
        using var b = new MagickImage(output);
        b.Resize(new MagickGeometry(a.Width, a.Height) { IgnoreAspectRatio = true });
        if (a.HasAlpha) { a.BackgroundColor = MagickColors.White; a.Alpha(AlphaAction.Remove); }
        if (b.HasAlpha) { b.BackgroundColor = MagickColors.White; b.Alpha(AlphaAction.Remove); }
        // Magick reports SSIM as a distortion (1 - similarity) in recent versions; normalise to similarity.
        double d = a.Compare(b, ErrorMetric.StructuralSimilarity);
        return d <= 0.5 ? 1 - d : d;
    }
    catch { return null; }
}

async Task<double?> Vmaf(string source, string output)
{
    // Compare at the output's size and frame rate: scale and resample the reference to match.
    var probe = await runner.RunAsync(tools.FfprobePath,
        ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate", "-of", "csv=p=0", output], lowPriority: false);
    var parts = probe.Stdout.Trim().Split(',');
    if (parts.Length < 3) return null;
    string w = parts[0], h = parts[1], fps = parts[2];
    string graph =
        $"[0:v]setpts=PTS-STARTPTS,format=yuv420p[d];" +
        $"[1:v]scale={w}:{h}:flags=bicubic,fps={fps},setpts=PTS-STARTPTS,format=yuv420p[r];" +
        $"[d][r]libvmaf=n_subsample=4:n_threads={Environment.ProcessorCount}";
    var r = await runner.RunAsync(tools.FfmpegPath,
        ["-hide_banner", "-nostdin", "-i", output, "-i", source, "-lavfi", graph, "-f", "null", "-"], lowPriority: true);
    var m = Regex.Match(r.StderrTail, @"VMAF score:\s*([0-9.]+)");
    return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
}

record Row(string File, string Kind, double TargetMb, bool Ok, string Outcome, long InputBytes, long OutputBytes,
    double Percent, string? Metric, double? Score, double Seconds, int Retries, string Summary, string? Error);
