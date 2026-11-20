using System.Text.Json;
using System.Text.RegularExpressions;
using Squish.Core.Ffmpeg;

namespace Squish.Core;

/// <summary>
/// Detects which encoders work: lists ffmpeg -encoders, then runs a one-second test encode for each
/// hardware H.264 encoder. Cached on disk keyed by a caller-supplied key (GPU driver version + FFmpeg build).
/// </summary>
public sealed partial class CapabilityDetector(FfmpegTools tools, ProcessRunner runner, string? cachePath, ILog? log = null)
{
    private static readonly string[] Hardware = ["h264_nvenc", "h264_qsv", "h264_amf"];
    private Task<Capabilities>? _task;
    private readonly object _gate = new();



    /// <summary>Runs detection once per instance; concurrent callers share the same task.</summary>
    /// <remarks>The shared task never sees a caller's token, so one cancelled job can't poison it; each caller
    /// stops waiting on its own token instead. A failed detection is dropped so the next caller retries.</remarks>
    public Task<Capabilities> GetAsync(string cacheKey, CancellationToken ct = default)
    {
        Task<Capabilities> task;
        lock (_gate)
        {
            if (_task is { IsFaulted: true } or { IsCanceled: true }) _task = null;
            task = _task ??= DetectAsync(cacheKey, CancellationToken.None);
        }
        return task.WaitAsync(ct);
    }

    private async Task<Capabilities> DetectAsync(string key, CancellationToken ct)
    {
        string fullKey = $"{key}|{tools.FfmpegPath}|{File.GetLastWriteTimeUtc(tools.FfmpegPath):O}";
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                var c = JsonSerializer.Deserialize(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false), SquishJson.Default.CapabilityCacheEntry);
                if (c?.Key == fullKey) return new Capabilities { Encoders = c.Encoders.ToHashSet(), HardwareH264 = c.HardwareH264 };
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }

        var list = await runner.RunAsync(tools.FfmpegPath, ["-hide_banner", "-encoders"], lowPriority: false, ct: ct).ConfigureAwait(false);
        var encoders = ParseEncoders(list.Stdout);

        string? hw = null;
        foreach (var e in Hardware.Where(encoders.Contains))
        {
            var r = await runner.RunAsync(tools.FfmpegPath,
                ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=1",
                 "-pix_fmt", e == "h264_nvenc" ? "yuv420p" : "nv12", "-c:v", e, "-b:v", "1M", "-f", "null", "-"],
                lowPriority: false, ct: ct).ConfigureAwait(false);
            log?.Info($"Capability test {e}: {(r.Success ? "ok" : "failed")}");
            if (r.Success) { hw = e; break; }
        }

        var caps = new Capabilities { Encoders = encoders, HardwareH264 = hw };
        if (cachePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new CapabilityCacheEntry(fullKey, [.. encoders], hw), SquishJson.Default.CapabilityCacheEntry), ct).ConfigureAwait(false);
            }
            catch (IOException) { }
        }
        return caps;
    }

    internal static HashSet<string> ParseEncoders(string text)
    {
        var set = new HashSet<string>();
        foreach (Match m in EncoderLine().Matches(text)) set.Add(m.Groups[1].Value);
        return set;
    }

    // Lines look like " V....D libx264              libx264 H.264 ..."
    [GeneratedRegex(@"^\s[VAS][\.FSXBD]{5}\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex EncoderLine();
}
