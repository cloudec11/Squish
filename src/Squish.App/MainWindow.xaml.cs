using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Squish.App.ViewModels;
using Squish.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace Squish.App;

public sealed partial class MainWindow : Window, IShell
{
    private readonly nint _hwnd;
    private readonly TaskbarProgress _taskbar;
    private readonly List<ToggleButton> _segments = [];
    private ToggleButton? _customSegment;
    private bool _isActive = true;
    private bool _closeConfirmed;
    private bool _loadingSettings;

    public MainViewModel ViewModel { get; }

    public MainWindow(Func<IShell, MainViewModel> createViewModel)
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _taskbar = new TaskbarProgress(_hwnd);
        ViewModel = createViewModel(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Squish.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        ApplyPlacement();
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            double scale = Scale;
            op.PreferredMinimumWidth = (int)(400 * scale);
            op.PreferredMinimumHeight = (int)(480 * scale);
        }

        BuildSegments();
        SyncModeToggles();
        ApplyTheme(ViewModel.Settings.Theme);
        VersionText.Text = $"Squish {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

        Activated += (_, e) => _isActive = e.WindowActivationState != WindowActivationState.Deactivated;
        AppWindow.Closing += AppWindow_Closing;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FastMode)) SyncModeToggles();
            if (e.PropertyName == nameof(MainViewModel.TargetMegabytes)) SyncSegments();
        };
    }

    private double Scale => Native.GetDpiForWindow(_hwnd) / 96.0;

    // ---------------- Window placement

    private void ApplyPlacement()
    {
        var s = ViewModel.Settings;
        double scale = Scale;
        int w = s.WindowWidth ?? (int)(480 * scale), h = s.WindowHeight ?? (int)(640 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(w, (int)(400 * scale)), Math.Max(h, (int)(480 * scale))));
        if (s.WindowX is { } x && s.WindowY is { } y)
        {
            // Only restore if the spot is still on a connected display.
            var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(x + 40, y + 20), DisplayAreaFallback.None);
            if (area is not null) AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (ViewModel.IsBusy && !_closeConfirmed)
        {
            args.Cancel = true;
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Stop compressing?",
                Content = "Files still in progress will be cancelled. Finished files are kept.",
                PrimaryButtonText = "Stop and close",
                CloseButtonText = "Keep going",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _closeConfirmed = true;
            ViewModel.CancelAll();
            var sw = Stopwatch.StartNew();
            while (ViewModel.IsBusy && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);
            Close();
            return;
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            ViewModel.SaveSettings(ViewModel.Settings with
            {
                WindowX = AppWindow.Position.X, WindowY = AppWindow.Position.Y,
                WindowWidth = AppWindow.Size.Width, WindowHeight = AppWindow.Size.Height,
            });
        }
    }

    // ---------------- Target segments

    private void BuildSegments()
    {
        var presets = ViewModel.Catalog.Presets;
        int col = 0;
        foreach (var p in presets)
        {
            var b = MakeSegment(p.Label, $"{p.Label} megabytes, {p.Caption}");
            b.Tag = p.Megabytes;
            b.Click += (_, _) => { ViewModel.TargetMegabytes = (double)b.Tag; SyncSegments(); };
            Place(b, col++);
        }
        _customSegment = MakeSegment("Custom", "Custom size");
        var box = new NumberBox
        {
            Header = $"Size in MB ({ViewModel.Catalog.CustomMinMegabytes:0}–{ViewModel.Catalog.CustomMaxMegabytes:0})",
            Minimum = ViewModel.Catalog.CustomMinMegabytes,
            Maximum = ViewModel.Catalog.CustomMaxMegabytes,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1, LargeChange = 10, Width = 200,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        var flyout = new Flyout { Content = box };
        flyout.Opening += (_, _) => box.Value = ViewModel.TargetMegabytes;
        box.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) ViewModel.TargetMegabytes = Math.Round(e.NewValue, 1);
        };
        flyout.Closed += (_, _) => SyncSegments();
        _customSegment.Click += (_, _) => { SyncSegments(); flyout.ShowAt(_customSegment); };
        Place(_customSegment, col);
        SyncSegments();

        void Place(ToggleButton b, int c)
        {
            SegmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(b, c);
            SegmentGrid.Children.Add(b);
            _segments.Add(b);
        }
    }

    private ToggleButton MakeSegment(string label, string automationName)
    {
        var b = new ToggleButton { Content = label, Style = (Style)Application.Current.Resources["SegmentButton"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, automationName);
        return b;
    }

    private void SyncSegments()
    {
        bool matched = false;
        foreach (var b in _segments)
        {
            if (b == _customSegment) continue;
            bool on = Math.Abs((double)b.Tag - ViewModel.TargetMegabytes) < 0.001;
            b.IsChecked = on;
            matched |= on;
        }
        if (_customSegment is not null)
        {
            _customSegment.IsChecked = !matched;
            _customSegment.Content = matched ? "Custom" : $"{ViewModel.TargetMegabytes:0.#}";
        }
    }

    private void SyncModeToggles()
    {
        BestToggle.IsChecked = !ViewModel.FastMode;
        FastToggle.IsChecked = ViewModel.FastMode;
    }

    private void Best_Click(object sender, RoutedEventArgs e) { ViewModel.FastMode = false; SyncModeToggles(); }
    private void Fast_Click(object sender, RoutedEventArgs e) { ViewModel.FastMode = true; SyncModeToggles(); }

    // ---------------- Adding files

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to Squish";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        ViewModel.AddFiles(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
    }

    private async void Paste_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or NumberBox) return;
        args.Handled = true;
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var items = await content.GetStorageItemsAsync();
            ViewModel.AddFiles(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    private void Browse_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = BrowseAsync();
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => _ = BrowseAsync();

    private async Task BrowseAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) ViewModel.AddFiles(files.Select(f => f.Path));
    }

    private void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && ItemsList.SelectedItem is FileItemViewModel item && item.CanRemove)
        {
            int index = ItemsList.SelectedIndex;
            ViewModel.Remove(item);
            if (ViewModel.Items.Count > 0) ItemsList.SelectedIndex = Math.Min(index, ViewModel.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && ViewModel.PrimaryCommand.CanExecute(null))
        {
            ViewModel.PrimaryCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---------------- Drag out of finished cards

    private async void Card_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileItemViewModel { OutputPath: { } path }) { args.Cancel = true; return; }
        var deferral = args.GetDeferral();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            args.Data.SetStorageItems([file]);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.AllowedOperations = DataPackageOperation.Copy;
        }
        catch { args.Cancel = true; }
        finally { deferral.Complete(); }
    }

    // ---------------- x:Bind helpers

    public static ImageSource? AsImage(object? o) => o as ImageSource;
    public static Visibility Has(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility Not(bool b) => b ? Visibility.Collapsed : Visibility.Visible;

    public static Brush StateBrush(ItemState s) => (Brush)Application.Current.Resources[s == ItemState.Failed
        ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush"];

    public static Brush StatusBrush(ItemState s) => (Brush)Application.Current.Resources[s switch
    {
        ItemState.Failed => "SystemFillColorCriticalBrush",
        ItemState.Done or ItemState.AlreadyFits => "SystemFillColorSuccessBrush",
        _ => "TextFillColorSecondaryBrush",
    }];

    // ---------------- IShell

    public void CopyFiles(IReadOnlyList<string> paths) => Native.CopyFilesToClipboard(_hwnd, paths);

    public void ShowInFolder(string path) => Native.ShowInFolder(path);

    public void NotifyBatchDone(string title, string body)
    {
        if (_isActive) return;
        try
        {
            var n = new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification();
            AppNotificationManager.Default.Show(n);
        }
        catch (Exception) { /* notifications unavailable */ }
    }

    public void SetTaskbarProgress(double? fraction, bool error = false) => _taskbar.Set(fraction, error);

    public void KeepAwake(bool on) => Native.KeepAwake(on);

    public void Announce(string text)
    {
        LiveRegion.Text = text;
        var peer = FrameworkElementAutomationPeer.FromElement(LiveRegion) ?? FrameworkElementAutomationPeer.CreatePeerForElement(LiveRegion);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, text, "squish-status");
    }

    public async Task<object?> LoadThumbnailAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128, ThumbnailOptions.ResizeThumbnail);
            if (thumb is null || thumb.Type != ThumbnailType.Image) return null;
            var bmp = new BitmapImage { DecodePixelWidth = 128 };
            await bmp.SetSourceAsync(thumb);
            return bmp;
        }
        catch { return null; }
    }

    // ---------------- Settings flyout

    private async void SettingsFlyout_Opening(object sender, object e)
    {
        _loadingSettings = true;
        var s = ViewModel.Settings;
        OutputLocationRadios.SelectedIndex = s.OutputLocation == OutputLocation.FixedFolder ? 1 : 0;
        FolderText.Text = s.OutputFolder ?? "No folder chosen";
        FolderRow.Visibility = s.OutputLocation == OutputLocation.FixedFolder ? Visibility.Visible : Visibility.Collapsed;
        ImageFormatBox.SelectedIndex = s.ImageFormat switch
        {
            OutputFormatPreference.Jpeg => 1, OutputFormatPreference.Png => 2, OutputFormatPreference.WebP => 3, _ => 0,
        };
        HardwareToggle.IsOn = s.UseHardwareInFastMode;
        ThemeBox.SelectedIndex = (int)s.Theme;
        _loadingSettings = false;

        HardwareText.Text = "Checking your graphics card…";
        try
        {
            var caps = await App.Current.GetCapabilitiesAsync();
            HardwareText.Text = caps.HardwareName is { } name
                ? $"Detected: {name}. Hardware encodes are faster but slightly less sharp."
                : "No supported graphics encoder found. Fast mode uses the CPU.";
        }
        catch (Exception ex) { HardwareText.Text = SquishException.From(ex).Message; }
    }

    private void OutputLocation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        bool fixedFolder = OutputLocationRadios.SelectedIndex == 1;
        FolderRow.Visibility = fixedFolder ? Visibility.Visible : Visibility.Collapsed;
        if (fixedFolder && string.IsNullOrEmpty(ViewModel.Settings.OutputFolder))
        {
            var videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Squish");
            ViewModel.SaveSettings(ViewModel.Settings with { OutputFolder = videos });
            FolderText.Text = videos;
        }
        ViewModel.SaveSettings(ViewModel.Settings with { OutputLocation = fixedFolder ? OutputLocation.FixedFolder : OutputLocation.BesideSource });
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        ViewModel.SaveSettings(ViewModel.Settings with { OutputFolder = folder.Path, OutputLocation = OutputLocation.FixedFolder });
        FolderText.Text = folder.Path;
    }

    private void ImageFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var f = ImageFormatBox.SelectedIndex switch
        {
            1 => OutputFormatPreference.Jpeg, 2 => OutputFormatPreference.Png, 3 => OutputFormatPreference.WebP, _ => OutputFormatPreference.Auto,
        };
        ViewModel.SaveSettings(ViewModel.Settings with { ImageFormat = f });
        foreach (var i in ViewModel.Items.Where(i => i.IsQueued)) _ = ViewModel.PredictAsync(i);
    }

    private void Hardware_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        ViewModel.SaveSettings(ViewModel.Settings with { UseHardwareInFastMode = HardwareToggle.IsOn });
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeBox.SelectedIndex < 0) return;
        var t = (ThemeChoice)ThemeBox.SelectedIndex;
        ViewModel.SaveSettings(ViewModel.Settings with { Theme = t });
        ApplyTheme(t);
    }

    private void ApplyTheme(ThemeChoice t)
    {
        Root.RequestedTheme = t switch { ThemeChoice.Light => ElementTheme.Light, ThemeChoice.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        var dark = t == ThemeChoice.Dark || (t == ThemeChoice.System && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void Licences_Click(object sender, RoutedEventArgs e) => OpenPath(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Logs);
        OpenPath(AppPaths.Logs);
    }

    private static void OpenPath(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ---------------- Activation from a second launch

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized) op.Restore();
        Activate();
        Native.SetForegroundWindow(_hwnd);
    }
}
