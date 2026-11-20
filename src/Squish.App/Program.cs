using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Squish.App;

/// <summary>
/// Custom entry point for single-instance behaviour: a second launch (or "Send to") hands its
/// files to the running window and exits.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var key = AppInstance.FindOrRegisterForKey("Squish.Main");
        if (!key.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Let the running instance take the foreground.
            Native.AllowSetForegroundWindow((int)key.ProcessId);
            Task.Run(() => key.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        key.Activated += OnRedirected;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }

    private static void OnRedirected(object? sender, AppActivationArguments e)
    {
        string[] files = [];
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
            files = Native.SplitCommandLine(launch.Arguments).Skip(1).ToArray(); // first token is the exe

        var app = App.Current;
        app.Window?.DispatcherQueue.TryEnqueue(() =>
        {
            app.OpenFiles(files);
            app.Window.BringToFront();
        });
    }
}
