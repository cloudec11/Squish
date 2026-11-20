using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squish.Core;

namespace Squish.App.ViewModels;

/// <summary>Platform services the view-models need. Implemented by the window.</summary>
public interface IShell
{
    void CopyFiles(IReadOnlyList<string> paths);
    void ShowInFolder(string path);
    void NotifyBatchDone(string title, string body);
    void SetTaskbarProgress(double? fraction, bool error = false);
    void KeepAwake(bool on);
    void Announce(string text);
    Task<object?> LoadThumbnailAsync(string path);
}

public sealed partial class MainViewModel : ObservableObject
{
    public const int DiscordAttachmentLimit = 10;

    private readonly SettingsStore _settingsStore;
    private AppSettings _settings;
    private readonly Func<JobQueue> _queueFactory;
    private JobQueue? _queue;

    public MainViewModel(IShell shell, PresetCatalog catalog, SettingsStore settings, Func<JobQueue> queueFactory)
    {
        Shell = shell;
        Catalog = catalog;
        _settingsStore = settings;
        _settings = settings.Load();
        _queueFactory = queueFactory;
        TargetMegabytes = _settings.TargetMegabytes > 0 ? _settings.TargetMegabytes : catalog.Default.Megabytes;
        FastMode = _settings.Mode == QualityMode.Fast;
        Items.CollectionChanged += OnItemsChanged;
    }

    public IShell Shell { get; }
    public PresetCatalog Catalog { get; }
    public AppSettings Settings => _settings;
    public ObservableCollection<FileItemViewModel> Items { get; } = [];

    /// <summary>Created on first use so FFmpeg is never touched at startup.</summary>
    public JobQueue Queue => _queue ??= _queueFactory();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetCaption))]
    public partial double TargetMegabytes { get; set; }

    [ObservableProperty] public partial bool FastMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(CanClear))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(AttachmentNote))]
    public partial bool BatchFinished { get; set; }

    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;
    public bool CanClear => HasItems && !IsBusy;
    public int PendingCount => Items.Count(IsRunnable);

    /// <summary>Queued, or failed during encoding (worth retrying). A file that failed to probe would only fail again.</summary>
    private static bool IsRunnable(FileItemViewModel i) =>
        i.State is ItemState.Queued or ItemState.Probing || (i.State == ItemState.Failed && i.Info is not null);
    public IEnumerable<FileItemViewModel> Finished => Items.Where(i => i.HasOutput);

    public string PrimaryText =>
        IsBusy ? "Compressing…"
        : BatchFinished && Finished.Any() ? (Finished.Count() == 1 ? "Copy" : $"Copy all ({Finished.Count()})")
        : PendingCount == 1 ? "Compress 1 file" : $"Compress {PendingCount} files";

    public string? AttachmentNote => BatchFinished && Finished.Count() > DiscordAttachmentLimit
        ? $"Discord takes {DiscordAttachmentLimit} files per message. Paste in batches."
        : null;

    public string TargetCaption
    {
        get
        {
            var p = Catalog.Presets.FirstOrDefault(x => Math.Abs(x.Megabytes - TargetMegabytes) < 0.001);
            return p is null ? $"Custom: {TargetMegabytes:0.#} MB" : p.Caption;
        }
    }

    partial void OnTargetMegabytesChanged(double value)
    {
        SaveSettings(_settings with { TargetMegabytes = value });
        foreach (var i in Items.Where(i => i.State == ItemState.Queued)) _ = PredictAsync(i);
    }

    partial void OnFastModeChanged(bool value)
    {
        SaveSettings(_settings with { Mode = value ? QualityMode.Fast : QualityMode.Best });
        foreach (var i in Items.Where(i => i.State == ItemState.Queued)) _ = PredictAsync(i);
    }

    public void SaveSettings(AppSettings s)
    {
        _settings = s;
        try { _settingsStore.Save(s); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        OnPropertyChanged(nameof(Settings));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) BatchFinished = false;
        RefreshCounts();
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(AttachmentNote));
    }

    // ---------------- Adding files

    public void AddFiles(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            string path;
            try { path = System.IO.Path.GetFullPath(raw); } catch { continue; }
            if (Directory.Exists(path))
            {
                AddFiles(Directory.EnumerateFiles(path).Where(f => !System.IO.Path.GetFileName(f).StartsWith('.')));
                continue;
            }
            if (!existing.Add(path)) continue;
            var item = new FileItemViewModel(this, path);
            Items.Add(item);
            _ = ProbeAsync(item);
        }
    }

    private async Task ProbeAsync(FileItemViewModel item)
    {
        try
        {
            item.Info = await Task.Run(() => Queue.ProbeAsync(item.Path, CancellationToken.None));
            item.DetailLine = Describe(item.Info);
            item.State = ItemState.Queued;
            item.InfoLoaded();
            await PredictAsync(item);
        }
        catch (Exception ex)
        {
            Fail(item, SquishException.From(ex));
        }
        finally
        {
            RefreshCounts();
        }
        try { item.Thumbnail = await Shell.LoadThumbnailAsync(item.Path); } catch { /* glyph fallback */ }
    }

    internal async Task PredictAsync(FileItemViewModel item)
    {
        if (item.Info is null || item.State is not ItemState.Queued) return;
        try
        {
            var req = BuildRequest(item);
            var (_, plan) = await Queue.Compressor.PreviewAsync(req, CancellationToken.None, item.Info);
            item.PredictionLine = plan switch
            {
                CopyPlan or RemuxPlan => "→ No compression needed",
                VideoPlan vp => $"→ {Math.Min(vp.Width, vp.Height)}p · about {Mb(plan.PredictedBytes)}",
                ImagePlan => $"→ under {Mb((long)(req.TargetBytes * Tuning.Default.AimFraction))}",
                AnimatedPlan ap => $"→ GIF · up to {Math.Max(ap.Width, ap.Height)} px",
                _ => "",
            };
            item.Warning = plan.Warnings.FirstOrDefault(w => !w.StartsWith("Saved as JPEG"));
        }
        catch (Exception ex)
        {
            Fail(item, SquishException.From(ex));
        }
    }

    public CompressionRequest BuildRequest(FileItemViewModel item)
    {
        string? outDir = _settings.OutputLocation == OutputLocation.FixedFolder && !string.IsNullOrWhiteSpace(_settings.OutputFolder)
            ? _settings.OutputFolder : null;
        var format = item.FormatOverride != OutputFormatPreference.Auto ? item.FormatOverride : _settings.ImageFormat;
        if (item.Info?.Kind == MediaKind.Video) format = OutputFormatPreference.Auto;
        var mode = FastMode && _settings.UseHardwareInFastMode ? QualityMode.Fast : QualityMode.Best;
        return new CompressionRequest(item.Path, outDir, item.TargetBytes, mode, _settings.VideoCodec, format);
    }

    public void Remove(FileItemViewModel item)
    {
        if (!item.CanRemove) return;
        Items.Remove(item);
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsBusy) return;
        Items.Clear();
        BatchFinished = false;
    }

    // ---------------- Running

    [RelayCommand]
    private async Task Primary()
    {
        if (IsBusy) return;
        if (BatchFinished && Finished.Any())
        {
            var files = Finished.Select(i => i.OutputPath!).ToList();
            if (files.Count > 0)
            {
                Shell.CopyFiles(files);
                Shell.Announce(files.Count == 1 ? "Copied. Paste into Discord." : $"Copied {files.Count} files. Paste into Discord.");
            }
            return;
        }
        await RunItemsAsync(Items.Where(i => IsRunnable(i) && i.State != ItemState.Probing).ToList());
    }

    // Everything below runs on the UI thread. A Retry (or a second Compress) while files are running joins the
    // current run instead of starting a parallel one: IsBusy stays true until every running item has finished.
    private readonly Dictionary<FileItemViewModel, double> _runProgress = [];

    public async Task RunItemsAsync(IReadOnlyList<FileItemViewModel> items)
    {
        // Skip anything already running or waiting in this run.
        items = items.Where(i => !i.InRun).ToList();
        if (items.Count == 0) return;

        if (_runProgress.Count == 0)
        {
            IsBusy = true;
            Shell.KeepAwake(true);
            Shell.SetTaskbarProgress(0);
        }
        BatchFinished = false;
        foreach (var i in items)
        {
            i.BeginWork(); // the token exists from now on, so Cancel works while the file waits its turn
            i.State = ItemState.Queued;
            i.StatusLine = "Waiting…";
            i.Progress = 0;
            _runProgress[i] = 0;
        }

        await Task.WhenAll(items.Select(item => RunOneAsync(item, p =>
        {
            if (_runProgress.ContainsKey(item)) _runProgress[item] = p;
            if (_runProgress.Count > 0) Shell.SetTaskbarProgress(_runProgress.Values.Average() / 100);
        })));

        // Only the call that finishes last closes the run.
        if (Items.Any(i => i.InRun)) return;
        var ran = _runProgress.Keys.ToList();
        _runProgress.Clear();
        IsBusy = false;
        Shell.KeepAwake(false);
        Shell.SetTaskbarProgress(null);
        BatchFinished = Items.All(i => i.State is ItemState.Done or ItemState.AlreadyFits or ItemState.Failed);
        RefreshCounts();

        int ok = ran.Count(i => i.HasOutput), failed = ran.Count(i => i.IsFailed);
        string body = failed == 0 ? $"{ok} file{(ok == 1 ? "" : "s")} ready to paste into Discord." : $"{ok} ready, {failed} failed.";
        Shell.NotifyBatchDone("Compression finished", body);
        Shell.Announce($"Compression finished. {body}");
    }

    partial void OnIsBusyChanged(bool value)
    {
        foreach (var i in Items) i.RefreshCommands();
    }

    private async Task RunOneAsync(FileItemViewModel item, Action<double> report)
    {
        var ct = item.Token;
        if (item.Info is null)
        {
            try { item.Info = await Queue.ProbeAsync(item.Path, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(item, SquishException.From(ex)); item.EndWork(); report(100); return;
            }
            catch (OperationCanceledException)
            {
                item.State = ItemState.Queued; item.StatusLine = "Cancelled"; item.EndWork(); report(100); return;
            }
        }
        var req = BuildRequest(item);
        bool started = false, finished = false;
        var progress = new Progress<JobProgress>(p =>
        {
            // Progress<T> posts asynchronously: a late report must not overwrite the final result line.
            if (finished) return;
            if (!started) { started = true; item.State = ItemState.Working; }
            item.Progress = p.Percent;
            item.StatusLine = p.Remaining is { } r && r > TimeSpan.FromSeconds(1)
                ? $"{p.Step} · {p.Percent:0}% · {FormatEta(r)} left"
                : $"{p.Step} · {p.Percent:0}%";
            report(p.Percent);
        });
        try
        {
            var result = await Task.Run(() => Queue.RunAsync(req, progress, ct, item.Info), CancellationToken.None);
            finished = true;
            item.OutputPath = result.OutputPath;
            item.State = result.Outcome == JobOutcome.AlreadyFits ? ItemState.AlreadyFits : ItemState.Done;
            item.StatusLine = result.Outcome == JobOutcome.AlreadyFits
                ? "No compression needed"
                : $"{Mb(result.InputBytes)} → {Mb(result.OutputBytes)}";
            item.Warning = null;
            if (result.Warnings.Count > 0) item.PredictionLine = result.Warnings[0];
            else item.PredictionLine = result.Summary;
            Shell.Announce(item.AutomationText);
        }
        catch (OperationCanceledException)
        {
            finished = true;
            item.State = ItemState.Queued;
            item.StatusLine = "Cancelled";
            item.Progress = 0;
        }
        catch (Exception ex)
        {
            finished = true;
            Fail(item, SquishException.From(ex));
        }
        finally
        {
            item.EndWork();
            report(100);
            RefreshCounts();
        }
    }

    public void CancelAll()
    {
        foreach (var i in Items.Where(i => i.State is ItemState.Working or ItemState.Queued)) i.CancelCommand.Execute(null);
    }

    private void Fail(FileItemViewModel item, SquishException ex)
    {
        item.State = ItemState.Failed;
        item.StatusLine = ex.Message;
        item.Progress = 0;
        if (item.DetailLine == "Reading…") item.DetailLine = Mb(SafeLength(item.Path));
        Shell.Announce(item.AutomationText);
    }

    // ---------------- Formatting

    public static string Describe(MediaInfo info) => info.Kind switch
    {
        MediaKind.Video => $"{info.ShortSide}p · {Duration(info.Duration)} · {Mb(info.FileBytes)}",
        MediaKind.AnimatedImage => $"GIF · {info.DisplayWidth}×{info.DisplayHeight} · {Duration(info.Duration)} · {Mb(info.FileBytes)}",
        _ => $"{info.DisplayWidth}×{info.DisplayHeight} · {Mb(info.FileBytes)}",
    };

    public static string Mb(long bytes) => bytes >= 100_000_000 ? $"{bytes / 1e6:0} MB"
        : bytes >= 1_000_000 ? $"{bytes / 1e6:0.0} MB"
        : bytes >= 1_000 ? $"{bytes / 1e3:0} KB" : $"{bytes} bytes";

    private static string Duration(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string FormatEta(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.Seconds} s";

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
