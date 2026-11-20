using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Squish.App.ViewModels;
using Squish.Core;
using Squish.Core.Ffmpeg;

namespace Squish.App;

public partial class App : Application
{
    /// <summary>Releases page for "Check for updates". Empty until there is one; the link is hidden.</summary>
    public const string ReleasesUrl = "";

    private MainWindow? _window;
    private readonly string[] _startupArgs;
    private readonly FileLog _log = new(AppPaths.Logs);
    private CapabilityDetector? _detector;
    private FfmpegTools? _tools;

    public static new App Current => (App)Application.Current;

    public App(string[] args)
    {
        _startupArgs = args;
        InitializeComponent();
        UnhandledException += (_, e) => _log.Error("Unhandled UI exception", e.Exception);
    }

    public MainWindow? Window => _window;

    private FfmpegTools Tools => _tools ??= FfmpegTools.Locate();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { AppNotificationManager.Default.Register(); } catch (Exception ex) { _log.Error("Toast registration failed", ex); }

        var catalog = PresetCatalog.Load(AppPaths.UserPresetsFile, PresetCatalog.ShippedPath);
        var settings = new SettingsStore(AppPaths.SettingsFile);

        _window = new MainWindow(shell => new MainViewModel(shell, catalog, settings, CreateQueue));
        _window.Closed += (_, _) =>
        {
            try { AppNotificationManager.Default.Unregister(); } catch { }
        };
        _window.Activate();

        _log.Info($"App launched with {_startupArgs.Length} argument(s)");
        if (_startupArgs.Contains("--demo")) DemoData.Fill(_window.ViewModel);
        else OpenFiles(_startupArgs);
    }

    private JobQueue CreateQueue()
    {
        var compressor = new Compressor(Tools, _log, Tuning.Default, GetCapabilitiesAsync);
        return new JobQueue(compressor);
    }

    public Task<Capabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        _detector ??= new CapabilityDetector(Tools, new ProcessRunner(_log), AppPaths.CapabilitiesCache, _log);
        return _detector.GetAsync(GpuDriverKey(), ct);
    }

    /// <summary>Called on the UI thread with file paths from the command line or a second launch.</summary>
    public void OpenFiles(IEnumerable<string> args)
    {
        var files = args.Where(a => !a.StartsWith("--") && (File.Exists(a) || Directory.Exists(a))).ToList();
        if (files.Count > 0) _window?.ViewModel.AddFiles(files);
    }

    /// <summary>Capability cache key: display adapter driver versions, read from the registry.</summary>
    private static string GpuDriverKey()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return "unknown";
            var parts = new List<string>();
            foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("DriverDesc") is string desc) parts.Add($"{desc}={k.GetValue("DriverVersion")}");
            }
            return parts.Count > 0 ? string.Join(';', parts) : "unknown";
        }
        catch { return "unknown"; }
    }
}
