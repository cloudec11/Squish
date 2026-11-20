using System.Text;

namespace Squish.Core;

public interface ILog
{
    void Info(string message);
    void Error(string message, Exception? ex = null);
}

public sealed class NullLog : ILog
{
    public static NullLog Instance { get; } = new();
    public void Info(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

/// <summary>Daily rolling log file; keeps the last <c>keepDays</c> files. Thread-safe, never throws.</summary>
public sealed class FileLog : ILog
{
    private readonly string _dir;
    private readonly int _keepDays;
    private readonly object _gate = new();
    private bool _pruned;

    public FileLog(string directory, int keepDays = 7)
    {
        _dir = directory;
        _keepDays = keepDays;
    }

    public string CurrentFile => Path.Combine(_dir, $"squish-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_dir);
                if (!_pruned) { Prune(); _pruned = true; }
                File.AppendAllText(CurrentFile,
                    $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* logging must never break a job */ }
    }

    private void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-_keepDays);
        foreach (var f in Directory.EnumerateFiles(_dir, "squish-*.log"))
            if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
    }
}
