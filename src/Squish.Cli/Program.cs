using System.Globalization;
using System.Text.Json;
using Squish.Core;
using Squish.Core.Ffmpeg;

// Squish command line: a thin front end over Squish.Core, used by tests and the bench.
//   squish [--target MB] [--mode best|fast] [--format auto|jpeg|png|webp|gif|video]
//          [--codec h264|hevc|av1] [--out DIR] [--plan] [--json] FILE...

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

double targetMb = 20;
var mode = QualityMode.Best;
var format = OutputFormatPreference.Auto;
var codec = CodecPreference.H264;
string? outDir = null;
bool planOnly = false, json = false;
var files = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    switch (a)
    {
        case "--target" or "-t": targetMb = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--mode": mode = Enum.Parse<QualityMode>(Next(), true); break;
        case "--format": format = Enum.Parse<OutputFormatPreference>(Next(), true); break;
        case "--codec": codec = Enum.Parse<CodecPreference>(Next(), true); break;
        case "--out" or "-o": outDir = Path.GetFullPath(Next()); break;
        case "--plan": planOnly = true; break;
        case "--json": json = true; break;
        case "-h" or "--help":
            Console.WriteLine("squish [--target MB] [--mode best|fast] [--format auto|jpeg|png|webp|gif|video] [--codec h264|hevc|av1] [--out DIR] [--plan] [--json] FILE...");
            return 0;
        default: files.Add(a); break;
    }
}
if (files.Count == 0) { Console.Error.WriteLine("No files given. Try --help."); return 2; }

var catalog = PresetCatalog.Load(AppPaths.UserPresetsFile, PresetCatalog.ShippedPath);
long targetBytes = catalog.ToBytes(targetMb);

FfmpegTools tools;
try { tools = FfmpegTools.Locate(); }
catch (SquishException ex) { Console.Error.WriteLine(ex.Message); return 3; }

var log = new FileLog(AppPaths.Logs);
var runner = new ProcessRunner(log);
var detector = new CapabilityDetector(tools, runner, AppPaths.CapabilitiesCache, log);
var compressor = new Compressor(tools, log, Tuning.Default, ct => detector.GetAsync("cli", ct));
var queue = new JobQueue(compressor);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int failures = 0;
var jsonOpts = new JsonSerializerOptions { WriteIndented = false };

var tasks = files.Select(async f =>
{
    var req = new CompressionRequest(Path.GetFullPath(f), outDir, targetBytes, mode, codec, format);
    try
    {
        if (planOnly)
        {
            var (info, plan) = await compressor.PreviewAsync(req, cts.Token);
            Console.WriteLine(json
                ? JsonSerializer.Serialize(new { file = f, kind = info.Kind.ToString(), plan = plan.GetType().Name, plan.Summary, plan.PredictedBytes, plan.Warnings }, jsonOpts)
                : $"{Path.GetFileName(f)}: {info.Kind} {info.DisplayWidth}x{info.DisplayHeight} {Mb(info.FileBytes)} -> {plan.GetType().Name} {plan.Summary}, about {Mb(plan.PredictedBytes)}"
                  + (plan.Warnings.Count > 0 ? $"  [{string.Join("; ", plan.Warnings)}]" : ""));
            return;
        }
        var lastStep = "";
        var progress = new Progress<JobProgress>(p =>
        {
            if (!json && p.Step != lastStep) { lastStep = p.Step; Console.Error.WriteLine($"  {Path.GetFileName(f)}: {p.Step}"); }
        });
        var r = await queue.RunAsync(req, progress, cts.Token);
        double pct = 100.0 * r.OutputBytes / r.TargetBytes;
        Console.WriteLine(json
            ? JsonSerializer.Serialize(new { file = f, ok = true, output = r.OutputPath, inputBytes = r.InputBytes, outputBytes = r.OutputBytes, targetBytes = r.TargetBytes, percent = Math.Round(pct, 2), outcome = r.Outcome.ToString(), r.Retries, seconds = Math.Round(r.Elapsed.TotalSeconds, 2), r.Summary, r.Warnings }, jsonOpts)
            : $"{Path.GetFileName(f)}: {Mb(r.InputBytes)} -> {Mb(r.OutputBytes)} ({pct:F1}% of target) {r.Outcome} [{r.Summary}] retries={r.Retries} {r.Elapsed.TotalSeconds:F1}s"
              + (r.Warnings.Count > 0 ? $"  [{string.Join("; ", r.Warnings)}]" : ""));
    }
    catch (OperationCanceledException)
    {
        Interlocked.Increment(ref failures);
        Console.WriteLine(json ? JsonSerializer.Serialize(new { file = f, ok = false, code = "Cancelled", message = "Cancelled." }, jsonOpts) : $"{Path.GetFileName(f)}: cancelled");
    }
    catch (SquishException ex)
    {
        Interlocked.Increment(ref failures);
        Console.WriteLine(json
            ? JsonSerializer.Serialize(new { file = f, ok = false, code = ex.Code.ToString(), message = ex.Message }, jsonOpts)
            : $"{Path.GetFileName(f)}: FAILED ({ex.Code}) {ex.Message}");
    }
}).ToList();

await Task.WhenAll(tasks);
return failures == 0 ? 0 : 1;

static string Mb(long bytes) => $"{bytes / 1_000_000.0:0.##} MB";
