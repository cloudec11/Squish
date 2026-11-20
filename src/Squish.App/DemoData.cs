using Squish.App.ViewModels;

namespace Squish.App;

/// <summary>"Squish.exe --demo": one card in every state, with fake data, for design review (WP6).</summary>
internal static class DemoData
{
    public static void Fill(MainViewModel vm)
    {
        Add(vm, @"C:\Demo\holiday_clip.mp4", ItemState.Queued, "1080p · 0:42 · 86.4 MB", "→ 720p · about 19.4 MB");
        var warn = Add(vm, @"C:\Demo\full_match_recording.mkv", ItemState.Queued, "1440p · 18:03 · 2.1 GB", "→ 360p · about 19.4 MB");
        warn.Warning = "Long clip: will look soft at 20 MB";
        var working = Add(vm, @"C:\Demo\boss_fight_60fps.mp4", ItemState.Working, "1080p · 1:30 · 412 MB", "");
        working.Progress = 62;
        working.StatusLine = "Pass 2 of 2 · 62% · 24 s left";
        var done = Add(vm, @"C:\Demo\cat.mov", ItemState.Done, "4K · 0:18 · 154 MB", "720p · 30 fps");
        done.StatusLine = "154 MB → 19.3 MB";
        var fits = Add(vm, @"C:\Demo\meme.png", ItemState.AlreadyFits, "1200×900 · 640 KB", "");
        fits.StatusLine = "No compression needed";
        var failed = Add(vm, @"C:\Demo\broken_download.mp4", ItemState.Failed, "3.1 MB", "");
        failed.StatusLine = "This isn't a video or image Squish can read. It may be damaged or in an unsupported format.";
        vm.RefreshCounts();
    }

    private static FileItemViewModel Add(MainViewModel vm, string path, ItemState state, string detail, string prediction)
    {
        var item = new FileItemViewModel(vm, path)
        {
            DetailLine = detail,
            PredictionLine = prediction,
            State = state,
        };
        vm.Items.Add(item);
        return item;
    }
}
