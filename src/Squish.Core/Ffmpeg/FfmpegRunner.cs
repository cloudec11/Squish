using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Squish.Core.Ffmpeg;

public sealed record FfmpegTools(string FfmpegPath, string FfprobePath)
{
    public const string EnvVar = "SQUISH_FFMPEG_DIR";

    /// <summary>
    /// Finds ffmpeg and ffprobe: env var, then next to the app (ffmpeg\ subfolder or flat),
    /// then a repo-level tools\ffmpeg folder (dev builds), then PATH.
    /// </summary>
    public static FfmpegTools Locate()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        IEnumerable<string?> Candidates()
        {
            yield return Environment.GetEnvironmentVariable(EnvVar);
            var baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "ffmpeg");
            yield return baseDir;
            for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
                yield return Path.Combine(dir.FullName, "tools", "ffmpeg");
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                yield return p;
        }
        foreach (var dir in Candidates())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var ff = Path.Combine(dir, "ffmpeg" + exe);
            var fp = Path.Combine(dir, "ffprobe" + exe);
            if (File.Exists(ff) && File.Exists(fp)) return new(ff, fp);
        }
        throw new SquishException(ErrorCode.ToolMissing,
            "FFmpeg is missing from the install folder. Reinstall Squish.");
    }
}

public sealed record ProcessResult(int ExitCode, string Stdout, string StderrTail, TimeSpan Elapsed)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs FFmpeg tools as child processes. Arguments are always passed as an array.</summary>
public sealed class ProcessRunner(ILog? log = null)
{
    private readonly ILog _log = log ?? NullLog.Instance;
    private const int StderrKeepLines = 120;

    public async Task<ProcessResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        Action<string>? onStdoutLine = null,
        bool lowPriority = true,
        bool captureStdout = true,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _log.Info($"RUN {Quote(exe)} {string.Join(' ', args.Select(Quote))}");

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new Queue<string>();
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(); return; }
            if (captureStdout) lock (stdout) stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            lock (stderr)
            {
                stderr.Enqueue(e.Data);
                while (stderr.Count > StderrKeepLines) stderr.Dequeue();
            }
        };

        try
        {
            if (!proc.Start())
                throw new SquishException(ErrorCode.ToolMissing, "Couldn't start FFmpeg. Reinstall Squish.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new SquishException(ErrorCode.ToolMissing, "Couldn't start FFmpeg. Reinstall Squish.", ex.Message, ex);
        }

        if (lowPriority)
        {
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }
        }
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await using (ct.Register(() => Kill(proc)))
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { /* a grandchild kept a pipe open; the process itself has exited */ }
        }

        ct.ThrowIfCancellationRequested();

        string tail;
        lock (stderr) tail = string.Join('\n', stderr);
        string outText;
        lock (stdout) outText = stdout.ToString();
        var result = new ProcessResult(proc.ExitCode, outText, tail, sw.Elapsed);
        if (!result.Success) _log.Error($"EXIT {proc.ExitCode} after {sw.Elapsed.TotalSeconds:F1}s\n{tail}");
        return result;
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static string Quote(string s) => s.Contains(' ') || s.Length == 0 ? $"\"{s}\"" : s;
}

/// <summary>Parses FFmpeg's <c>-progress pipe:1</c> key=value stream.</summary>
public sealed class FfmpegProgressParser(double durationSeconds, Action<double, TimeSpan?> onFraction)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public void OnLine(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line.AsSpan(0, eq);
        var value = line.AsSpan(eq + 1).Trim();

        double? seconds = null;
        if (key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
        {
            // Both are microseconds in practice (out_time_ms is misnamed in FFmpeg).
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                seconds = us / 1_000_000.0;
        }
        else if (key.SequenceEqual("progress") && value.SequenceEqual("end"))
        {
            onFraction(1.0, TimeSpan.Zero);
            return;
        }

        if (seconds is { } s && durationSeconds > 0)
        {
            var f = Math.Clamp(s / durationSeconds, 0, 1);
            TimeSpan? eta = f > 0.02 ? TimeSpan.FromSeconds(_sw.Elapsed.TotalSeconds * (1 - f) / f) : null;
            onFraction(f, eta);
        }
    }
}
