using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Squish.Core;

namespace Squish.App.ViewModels;

public enum ItemState { Probing, Queued, Working, Done, AlreadyFits, Failed }

/// <summary>One file card. No WinUI types here except the thumbnail object, so it stays testable.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private CancellationTokenSource? _cts;

    public FileItemViewModel(MainViewModel owner, string path)
    {
        _owner = owner;
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        DetailLine = "Reading…";
    }

    public string Path { get; }
    public string Name { get; }
    public MediaInfo? Info { get; set; }

    [ObservableProperty] public partial object? Thumbnail { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationText))] public partial string DetailLine { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationText))] public partial string PredictionLine { get; set; } = "";
    [ObservableProperty] public partial string? Warning { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationText))] public partial string StatusLine { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; }
    [ObservableProperty] public partial string? OutputPath { get; set; }
    [ObservableProperty] public partial double? TargetOverrideMb { get; set; }
    [ObservableProperty] public partial OutputFormatPreference FormatOverride { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueued), nameof(IsWorking), nameof(IsDone), nameof(IsAlreadyFits),
        nameof(IsFailed), nameof(HasOutput), nameof(IsProbing), nameof(CanRemove), nameof(StateGlyph), nameof(AutomationText), nameof(HasWarning))]
    public partial ItemState State { get; set; } = ItemState.Probing;

    public bool IsProbing => State == ItemState.Probing;
    public bool IsQueued => State == ItemState.Queued;
    public bool IsWorking => State == ItemState.Working;
    public bool IsDone => State == ItemState.Done;
    public bool IsAlreadyFits => State == ItemState.AlreadyFits;
    public bool IsFailed => State == ItemState.Failed;
    public bool HasOutput => State is ItemState.Done or ItemState.AlreadyFits && OutputPath is not null;
    public bool CanRemove => State != ItemState.Working && !InRun;
    public bool HasWarning => !string.IsNullOrEmpty(Warning) && State is ItemState.Queued or ItemState.Probing;
    public bool IsVideo => Info?.Kind == MediaKind.Video;
    public bool IsImage => Info?.Kind == MediaKind.StillImage;
    public bool IsAnimated => Info?.Kind == MediaKind.AnimatedImage;

    /// <summary>Segoe Fluent Icons glyph for the state badge.</summary>
    public string StateGlyph => State switch
    {
        ItemState.Done or ItemState.AlreadyFits => "", // CheckMark
        ItemState.Failed => "",                       // Error
        _ => "",
    };

    public string AutomationText => State switch
    {
        ItemState.Done => $"{Name}, done. {StatusLine}",
        ItemState.AlreadyFits => $"{Name}, no compression needed.",
        ItemState.Failed => $"{Name}, failed. {StatusLine}",
        ItemState.Working => $"{Name}, compressing. {StatusLine}",
        _ => $"{Name}. {DetailLine}. {PredictionLine}",
    };

    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnTargetOverrideMbChanged(double? value) { OnPropertyChanged(nameof(TargetLabel)); _ = _owner.PredictAsync(this); }
    partial void OnFormatOverrideChanged(OutputFormatPreference value) => _ = _owner.PredictAsync(this);

    public string TargetLabel => TargetOverrideMb is { } mb ? $"{mb:0.#} MB (this file)" : "Same as the rest";

    public long TargetBytes => _owner.Catalog.ToBytes(TargetOverrideMb ?? _owner.TargetMegabytes);

    internal void InfoLoaded()
    {
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsAnimated));
    }

    /// <summary>True from the moment the file joins a run until it finishes, fails or is cancelled.</summary>
    public bool InRun => _cts is not null;
    internal CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    internal void BeginWork()
    {
        if (_cts is not null) return; // already in a run; never orphan a live token
        _cts = new CancellationTokenSource();
        NotifyRunChanged();
    }

    internal void EndWork()
    {
        _cts?.Dispose();
        _cts = null;
        NotifyRunChanged();
    }

    private void NotifyRunChanged()
    {
        OnPropertyChanged(nameof(InRun));
        OnPropertyChanged(nameof(CanRemove));
        RefreshCommands();
    }

    internal void RefreshCommands()
    {
        RetryCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private bool CanRetry() => !InRun;

    [RelayCommand(CanExecute = nameof(CanRemove))] private void Remove() => _owner.Remove(this);
    [RelayCommand] private void Cancel() => _cts?.Cancel();
    [RelayCommand] private void Copy() { if (OutputPath is not null) _owner.Shell.CopyFiles([OutputPath]); }
    [RelayCommand] private void ShowInFolder() { if (OutputPath is not null) _owner.Shell.ShowInFolder(OutputPath); }
    [RelayCommand(CanExecute = nameof(CanRetry))] private Task Retry() => _owner.RunItemsAsync([this]);
    [RelayCommand] private void SetTarget(string? mb) =>
        TargetOverrideMb = double.TryParse(mb, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    [RelayCommand] private void SetFormat(string? f) =>
        FormatOverride = Enum.TryParse<OutputFormatPreference>(f, out var v) ? v : OutputFormatPreference.Auto;
}
