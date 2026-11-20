using Squish.Core;
using Squish.Core.Encoders;
using Squish.Core.Planning;

namespace Squish.Core.Tests;

public class VideoPlannerTests
{
    private const long MB = 1_000_000;
    private static readonly VideoPlanner Planner = new();

    internal static MediaInfo Video(int w = 1920, int h = 1080, double seconds = 60, double fps = 30,
        long bytes = 200 * MB, string codec = "h264", ContainerFormat c = ContainerFormat.Mp4,
        bool audio = true, string audioCodec = "aac", long audioBitrate = 192_000, int channels = 2,
        int rotation = 0, bool hdr = false, bool interlaced = false, long videoBitrate = 0) => new()
    {
        Path = "in.mp4", Kind = MediaKind.Video, Container = c, FileBytes = bytes, Duration = seconds,
        Width = w, Height = h, FrameRate = fps, VideoCodec = codec, HasAudio = audio,
        AudioCodec = audio ? audioCodec : null, AudioBitrate = audio ? audioBitrate : 0, AudioChannels = audio ? channels : 0,
        Rotation = rotation, Hdr = hdr, Interlaced = interlaced,
        VideoBitrate = videoBitrate > 0 ? videoBitrate : (long)(bytes * 8 / seconds),
        TotalBitrate = (long)(bytes * 8 / seconds),
    };

    internal static CompressionRequest Req(long targetMb = 20, QualityMode mode = QualityMode.Best) =>
        new("in.mp4", null, targetMb * MB, mode);

    [Fact]
    public void Mp4_h264_under_target_is_copied()
    {
        var plan = Planner.Plan(Video(bytes: 15 * MB), Req(), Capabilities.SoftwareOnly);
        Assert.IsType<CopyPlan>(plan);
    }

    [Fact]
    public void Mkv_h264_under_target_is_remuxed()
    {
        var plan = Planner.Plan(Video(bytes: 15 * MB, c: ContainerFormat.Mkv), Req(), Capabilities.SoftwareOnly);
        var remux = Assert.IsType<RemuxPlan>(plan);
        Assert.True(remux.IncludeAudio);
    }

    [Fact]
    public void Vp9_webm_under_target_is_reencoded()
    {
        var plan = Planner.Plan(Video(bytes: 15 * MB, codec: "vp9", c: ContainerFormat.WebM, audioCodec: "opus"), Req(), Capabilities.SoftwareOnly);
        Assert.IsType<VideoPlan>(plan);
    }

    [Fact]
    public void Budget_follows_formula()
    {
        // 20 MB, 60 s: total = 8 * 20e6 * 0.97 / 60 / 1000 = 2586.7 kbps -> audio 128.
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(), Req(), Capabilities.SoftwareOnly));
        double total = 8 * 20e6 * 0.97 / 60 / 1000;
        Assert.Equal(128, vp.AudioKbps);
        Assert.Equal((int)Math.Round(total * 0.995 - 128), vp.VideoKbps);
        Assert.Equal("libx264", vp.VideoEncoder);
        Assert.True(vp.TwoPass);
    }

    [Theory]
    [InlineData(10, 2500, 128, 2)]   // total 2587 -> over 2000
    [InlineData(20, 1500, 96, 2)]    // 20 s, but target picks total
    [InlineData(60, 700, 64, 2)]
    [InlineData(600, 300, 48, 1)]
    public void Audio_tiers(int seconds, int _, int expectedKbps, int expectedChannels)
    {
        // Pick targets so the total lands in each tier.
        long target = seconds switch { 10 => 3_300_000, 20 => 3_900_000, 60 => 5_500_000, _ => 22_000_000 };
        var info = Video(seconds: seconds, audioCodec: "opus");
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(info, new("in.mp4", null, target), Capabilities.SoftwareOnly));
        Assert.Equal(expectedKbps, vp.AudioKbps);
        Assert.Equal(expectedChannels, vp.AudioChannels);
        Assert.False(vp.CopyAudio);
    }

    [Fact]
    public void Audio_tier_boundaries()
    {
        Assert.Equal((128, 2), Planner.AudioFor(2001));
        Assert.Equal((96, 2), Planner.AudioFor(2000));
        Assert.Equal((96, 2), Planner.AudioFor(1001));
        Assert.Equal((64, 2), Planner.AudioFor(1000));
        Assert.Equal((64, 2), Planner.AudioFor(401));
        Assert.Equal((48, 1), Planner.AudioFor(400));
        Assert.Equal((48, 1), Planner.AudioFor(10));
    }

    [Fact]
    public void Aac_at_or_below_tier_is_copied()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(audioBitrate: 96_000), Req(), Capabilities.SoftwareOnly));
        Assert.True(vp.CopyAudio);
        Assert.Equal(96, vp.AudioKbps);
    }

    [Fact]
    public void Surround_is_downmixed_not_copied()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(audioBitrate: 96_000, channels: 6), Req(), Capabilities.SoftwareOnly));
        Assert.False(vp.CopyAudio);
        Assert.Equal(2, vp.AudioChannels);
    }

    [Fact]
    public void Silent_source_has_no_audio()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(audio: false), Req(), Capabilities.SoftwareOnly));
        Assert.False(vp.IncludeAudio);
        Assert.Equal(0, vp.AudioKbps);
    }

    [Theory]
    // seconds, source short side, expected short side
    [InlineData(10, 2160, 2160)]
    [InlineData(20, 2160, 1440)]
    [InlineData(30, 1080, 1080)]
    [InlineData(60, 1080, 720)]
    [InlineData(120, 1080, 540)]
    [InlineData(240, 1080, 480)]
    [InlineData(300, 1080, 360)]
    public void Ladder_rungs(int seconds, int srcShort, int expectedShort)
    {
        int w = srcShort * 16 / 9;
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(w: w, h: srcShort, seconds: seconds, fps: 30, bytes: 2000 * MB), Req(), Capabilities.SoftwareOnly));
        Assert.Equal(expectedShort, Math.Min(vp.Width, vp.Height));
        if (!vp.TooLong) Assert.True(vp.BitsPerPixel >= 0.045);
    }

    [Fact]
    public void Never_upscales_and_keeps_odd_source_size_rung()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(w: 1280, h: 800, seconds: 5), Req(), Capabilities.SoftwareOnly));
        Assert.Equal(1280, vp.Width);
        Assert.Equal(800, vp.Height);
    }

    [Fact]
    public void Dimensions_are_even_and_keep_aspect()
    {
        var (w, h) = VideoPlanner.DimsFor(Video(w: 1918, h: 1078), 720);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.Equal(720, h);
        Assert.InRange(w / (double)h, 1918 / 1078.0 - 0.01, 1918 / 1078.0 + 0.01);
    }

    [Fact]
    public void High_fps_halves_before_dropping_below_720()
    {
        // 60 fps 1080p, 90 s at 20 MB: 720p60 fails the floor, 720p30 should pass.
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(fps: 60, seconds: 90), Req(), Capabilities.SoftwareOnly));
        Assert.Equal(720, vp.Height);
        Assert.Equal(30, vp.Fps);
    }

    [Fact]
    public void Too_long_is_flagged_at_360p_24fps()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(fps: 60, seconds: 1200, bytes: 3000 * MB), Req(), Capabilities.SoftwareOnly));
        Assert.True(vp.TooLong);
        Assert.Equal(360, Math.Min(vp.Width, vp.Height));
        Assert.Equal(24, vp.Fps);
        Assert.Contains(vp.Warnings, w => w.Contains("Too long"));
    }

    [Fact]
    public void Vertical_video_uses_short_side_for_ladder()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(w: 1080, h: 1920, seconds: 60), Req(), Capabilities.SoftwareOnly));
        Assert.Equal(720, vp.Width);
        Assert.True(vp.Height > vp.Width);
    }

    [Fact]
    public void Rotation_metadata_swaps_display_size()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(w: 1920, h: 1080, seconds: 60, rotation: 90), Req(), Capabilities.SoftwareOnly));
        Assert.True(vp.Height > vp.Width);
    }

    [Fact]
    public void Short_clip_caps_bitrate_at_source()
    {
        var info = Video(seconds: 2, bytes: 25 * MB, videoBitrate: 3_000_000);
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(info, Req(), Capabilities.SoftwareOnly));
        Assert.True(vp.CappedAtSource);
        Assert.Equal(3000, vp.VideoKbps);
    }

    [Fact]
    public void Hdr_and_interlaced_flags_reach_the_plan()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(hdr: true, interlaced: true), Req(), Capabilities.SoftwareOnly));
        Assert.True(vp.ToneMapHdr);
        Assert.True(vp.Deinterlace);
        Assert.Contains("tonemap", VideoArgs.Filter(vp));
        Assert.StartsWith("bwdif", VideoArgs.Filter(vp));
    }

    [Fact]
    public void Fast_mode_uses_hardware_and_lower_aim()
    {
        var caps = new Capabilities { HardwareH264 = "h264_nvenc" };
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(), Req(mode: QualityMode.Fast), caps));
        Assert.Equal("h264_nvenc", vp.VideoEncoder);
        Assert.Equal(0.94, vp.AimFraction);
        Assert.False(vp.TwoPass);
    }

    [Fact]
    public void Fast_mode_without_hardware_falls_back_to_x264()
    {
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(), Req(mode: QualityMode.Fast), Capabilities.SoftwareOnly));
        Assert.Equal("libx264", vp.VideoEncoder);
        Assert.Equal(0.97, vp.AimFraction);
    }

    [Fact]
    public void Sub_720_source_keeps_its_frame_rate_when_bitrate_allows()
    {
        // Review finding: 480p60 at 10 MB has plenty of bitrate and must not be halved to 30 fps.
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(w: 854, h: 480, fps: 60, seconds: 10), Req(10), Capabilities.SoftwareOnly));
        Assert.Equal(60, vp.Fps);
        Assert.Equal(480, vp.Height);
    }

    [Fact]
    public void Sub_720_source_halves_fps_before_shrinking()
    {
        // 480p60 for 90 s at 10 MB: 480p60 fails the floor, so 480p30 comes before 360p.
        var vp = Assert.IsType<VideoPlan>(Planner.Plan(Video(w: 854, h: 480, fps: 60, seconds: 90, bytes: 500 * MB), Req(10), Capabilities.SoftwareOnly));
        Assert.Equal(30, vp.Fps);
    }

    [Fact]
    public void Mp4_with_non_aac_audio_is_not_copied()
    {
        var plan = Planner.Plan(Video(bytes: 15 * MB, audioCodec: "opus"), Req(), Capabilities.SoftwareOnly);
        Assert.IsType<VideoPlan>(plan);
    }
    [Fact]
    public void Prediction_is_within_aim()
    {
        var vp = Planner.Plan(Video(), Req(), Capabilities.SoftwareOnly);
        Assert.InRange(vp.PredictedBytes, 18 * MB, (long)(20 * MB * 0.97));
    }
}

public class VideoArgsTests
{
    [Fact]
    public void Pass_two_matches_reference_command_shape()
    {
        var vp = Assert.IsType<VideoPlan>(new VideoPlanner().Plan(VideoPlannerTests.Video(seconds: 80), VideoPlannerTests.Req(), Capabilities.SoftwareOnly));
        var args = VideoArgs.Final(vp, "tmp/job1", "out.tmp.mp4");
        string joined = string.Join(' ', args);
        Assert.Contains("-c:v libx264 -preset slow", joined);
        Assert.Contains("-pass 2 -passlogfile tmp/job1", joined);
        Assert.Contains("-movflags +faststart", joined);
        Assert.Contains("-map_metadata -1", joined);
        Assert.Contains("-map 0:a:0?", joined);
        Assert.Contains("-profile:v high", joined);
        Assert.Contains($"-maxrate {(int)Math.Round(vp.VideoKbps * 1.5)}k", joined);
        Assert.Equal("out.tmp.mp4", args[^1]);
    }

    [Fact]
    public void Pass_one_has_no_audio_and_null_output()
    {
        var vp = Assert.IsType<VideoPlan>(new VideoPlanner().Plan(VideoPlannerTests.Video(), VideoPlannerTests.Req(), Capabilities.SoftwareOnly));
        var args = VideoArgs.Pass1(vp, "log");
        Assert.Contains("-an", args);
        Assert.Equal(["-f", "null", "-"], args.TakeLast(3));
    }

    [Theory]
    [InlineData(29.97002997, "30000/1001")]
    [InlineData(23.976, "24000/1001")]
    [InlineData(30, "30")]
    [InlineData(12.5, "12.5")]
    public void Fps_args(double fps, string expected) => Assert.Equal(expected, VideoArgs.FpsArg(fps));

    [Fact]
    public void Paths_with_spaces_stay_one_argument()
    {
        var info = VideoPlannerTests.Video() with { Path = @"C:\my clips\😀 日本 (1).mp4" };
        var vp = Assert.IsType<VideoPlan>(new VideoPlanner().Plan(info, VideoPlannerTests.Req(), Capabilities.SoftwareOnly));
        var args = VideoArgs.Final(vp, "log", @"D:\out dir\x.mp4");
        Assert.Contains(info.Path, args);
        Assert.Contains(@"D:\out dir\x.mp4", args);
    }
}

public class VerifierTests
{
    private const long MB = 1_000_000;
    private static readonly Verifier V = new(new Ffmpeg.FfmpegTools("ffmpeg", "ffprobe"), new Ffmpeg.ProcessRunner());

    private static VideoPlan Plan() =>
        (VideoPlan)new VideoPlanner().Plan(VideoPlannerTests.Video(seconds: 60), VideoPlannerTests.Req(), Capabilities.SoftwareOnly);

    [Fact]
    public void Inside_window_passes()
    {
        Assert.Equal(VerifyStatus.Pass, V.Decide(Plan(), (long)(19.4 * MB)).Status);
    }

    [Fact]
    public void Over_995_retries_pass_two_with_scaled_bitrate()
    {
        var p = Plan();
        var r = V.Decide(p, (long)(20.5 * MB));
        Assert.Equal(VerifyStatus.Retry, r.Status);
        Assert.False(r.Fits);
        var next = Assert.IsType<VideoPlan>(r.NextPlan);
        int expected = (int)(p.VideoKbps * (20.0 / 20.5) * 0.98);
        Assert.InRange(next.VideoKbps, expected - 1, expected + 1);
        Assert.Equal(p.Width, next.Width); // same rung: pass 2 only
        Assert.Equal(1, next.RetriesOnRung);
    }

    [Fact]
    public void Two_misses_drop_a_rung()
    {
        var p = Plan() with { RetriesOnRung = 2, Attempt = 3 };
        var next = Assert.IsType<VideoPlan>(V.Decide(p, 21 * MB).NextPlan);
        Assert.True(next.Height < p.Height);
        Assert.Equal(0, next.RetriesOnRung);
    }

    [Fact]
    public void Rung_drop_from_720p60_halves_fps_before_540()
    {
        var p = (VideoPlan)new VideoPlanner().Plan(VideoPlannerTests.Video(fps: 60, seconds: 40), VideoPlannerTests.Req(), Capabilities.SoftwareOnly);
        Assert.Equal(720, p.Height);
        Assert.Equal(60, p.Fps);
        var next = Assert.IsType<VideoPlan>(V.Decide(p with { RetriesOnRung = 2, Attempt = 3 }, 21 * MB).NextPlan);
        Assert.Equal(720, next.Height);
        Assert.Equal(30, next.Fps);
        var after = Assert.IsType<VideoPlan>(V.Decide(next with { RetriesOnRung = 2, Attempt = 6 }, 21 * MB).NextPlan);
        Assert.Equal(540, after.Height);
        Assert.Equal(30, after.Fps);
    }
    [Fact]
    public void Hardware_misses_fall_back_to_software()
    {
        var p = Plan() with { VideoEncoder = "h264_nvenc", RetriesOnRung = 2, Attempt = 3 };
        var next = Assert.IsType<VideoPlan>(V.Decide(p, 21 * MB).NextPlan);
        Assert.Equal("libx264", next.VideoEncoder);
    }

    [Fact]
    public void Under_93_raises_once()
    {
        var p = Plan();
        var r = V.Decide(p, 15 * MB);
        Assert.Equal(VerifyStatus.Retry, r.Status);
        Assert.True(r.Fits);
        var next = Assert.IsType<VideoPlan>(r.NextPlan);
        Assert.True(next.Raised);
        Assert.True(next.VideoKbps > p.VideoKbps);
        Assert.Equal(VerifyStatus.Pass, V.Decide(next, 15 * MB).Status); // never twice
    }

    [Fact]
    public void Capped_short_clip_is_not_raised()
    {
        var p = Plan() with { CappedAtSource = true };
        Assert.Equal(VerifyStatus.Pass, V.Decide(p, 5 * MB).Status);
    }

    [Fact]
    public void Attempts_are_bounded()
    {
        var p = Plan() with { Attempt = Tuning.Default.MaxVideoAttempts };
        Assert.Equal(VerifyStatus.Fail, V.Decide(p, 21 * MB).Status);
    }

    [Fact]
    public void Images_over_target_fail()
    {
        var img = new ImagePlan
        {
            Info = VideoPlannerTests.Video() with { Kind = MediaKind.StillImage }, Request = VideoPlannerTests.Req(),
            OutputExtension = ".jpg", Format = ImageFormat.Jpeg, Width = 10, Height = 10,
        };
        Assert.Equal(VerifyStatus.Fail, V.Decide(img, 21 * MB).Status);
    }
}

public class ImageAndAnimatedPlannerTests
{
    private const long MB = 1_000_000;

    private static MediaInfo Image(ContainerFormat c, long bytes, bool alpha = false, MediaKind kind = MediaKind.StillImage) => new()
    {
        Path = "x", Kind = kind, Container = c, FileBytes = bytes, Width = 4000, Height = 3000, HasAlpha = alpha,
        FrameRate = kind == MediaKind.AnimatedImage ? 25 : 0, Duration = kind == MediaKind.AnimatedImage ? 10 : 0,
    };

    private static CompressionRequest Req(OutputFormatPreference f = OutputFormatPreference.Auto) => new("x", null, 20 * MB, Format: f);

    [Theory]
    [InlineData(ContainerFormat.Jpeg, ".jpg")]
    [InlineData(ContainerFormat.Png, ".png")]
    [InlineData(ContainerFormat.WebP, ".webp")]
    [InlineData(ContainerFormat.Gif, ".gif")]
    public void Small_previewable_images_are_copied(ContainerFormat c, string ext)
    {
        var plan = new ImagePlanner().Plan(Image(c, 1 * MB), Req(), Capabilities.SoftwareOnly);
        Assert.IsType<CopyPlan>(plan);
        Assert.Equal(ext, plan.OutputExtension);
    }

    [Theory]
    [InlineData(ContainerFormat.Heif)]
    [InlineData(ContainerFormat.Avif)]
    [InlineData(ContainerFormat.Bmp)]
    [InlineData(ContainerFormat.Tiff)]
    [InlineData(ContainerFormat.Raw)]
    public void Non_previewable_formats_become_jpeg_even_when_small(ContainerFormat c)
    {
        var plan = Assert.IsType<ImagePlan>(new ImagePlanner().Plan(Image(c, 1 * MB), Req(), Capabilities.SoftwareOnly));
        Assert.Equal(ImageFormat.Jpeg, plan.LossyFormat);
        Assert.False(plan.TryLossless);
    }

    [Fact]
    public void Png_tries_lossless_then_webp()
    {
        var plan = Assert.IsType<ImagePlan>(new ImagePlanner().Plan(Image(ContainerFormat.Png, 40 * MB), Req(), Capabilities.SoftwareOnly));
        Assert.True(plan.TryLossless);
        Assert.Equal(ImageFormat.Png, plan.Format);
        Assert.Equal(ImageFormat.WebP, plan.LossyFormat);
    }

    [Fact]
    public void Jpeg_stays_jpeg()
    {
        var plan = Assert.IsType<ImagePlan>(new ImagePlanner().Plan(Image(ContainerFormat.Jpeg, 40 * MB), Req(), Capabilities.SoftwareOnly));
        Assert.Equal(ImageFormat.Jpeg, plan.LossyFormat);
    }

    [Fact]
    public void User_override_wins()
    {
        var plan = Assert.IsType<ImagePlan>(new ImagePlanner().Plan(Image(ContainerFormat.Png, 40 * MB), Req(OutputFormatPreference.Jpeg), Capabilities.SoftwareOnly));
        Assert.Equal(ImageFormat.Jpeg, plan.LossyFormat);
        Assert.False(plan.TryLossless);
    }

    [Fact]
    public void Big_gif_starts_scaled_down_on_the_085_grid()
    {
        var plan = Assert.IsType<AnimatedPlan>(new AnimatedPlanner().Plan(Image(ContainerFormat.Gif, 80 * MB, kind: MediaKind.AnimatedImage), Req(), Capabilities.SoftwareOnly));
        Assert.True(plan.Width < 4000);
        Assert.Equal(25, plan.Fps);
    }

    [Fact]
    public void Gif_convert_to_video_uses_video_planner()
    {
        var plan = new AnimatedPlanner().Plan(Image(ContainerFormat.Gif, 80 * MB, kind: MediaKind.AnimatedImage), Req(OutputFormatPreference.Video), Capabilities.SoftwareOnly);
        Assert.IsType<VideoPlan>(plan);
        Assert.Equal(".mp4", plan.OutputExtension);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.9, 0.85)]
    [InlineData(0.85, 0.85)]
    [InlineData(0.5, 0.85 * 0.85 * 0.85 * 0.85 * 0.85)]
    public void Snap_scale(double s, double expected) =>
        Assert.Equal(expected, AnimatedPlanner.SnapScale(s, 0.85), 6);

    [Fact]
    public void Gif_scale_respects_floor()
    {
        var (w, h) = AnimatedPlanner.ScaleDims(1600, 900, 0.01, 320);
        Assert.Equal(320, w);
        Assert.Equal(180, h);
    }
}

public class PresetTests
{
    [Fact]
    public void Shipped_presets_load_and_default_to_20mb()
    {
        var cat = PresetCatalog.Load(PresetCatalog.ShippedPath);
        Assert.Equal(20, cat.Default.Megabytes);
        Assert.Equal(20_000_000, cat.ToBytes(cat.Default.Megabytes));
        Assert.Contains(cat.Presets, p => p.Megabytes == 500);
    }

    [Fact]
    public void Broken_preset_file_falls_back_to_builtin()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{ not json");
        try { Assert.Equal(PresetCatalog.Builtin.Presets.Count, PresetCatalog.Load(path).Presets.Count); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"squish-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Equal(20, store.Load().TargetMegabytes);
            store.Save(new AppSettings { TargetMegabytes = 50, Mode = QualityMode.Fast });
            var s = store.Load();
            Assert.Equal(50, s.TargetMegabytes);
            Assert.Equal(QualityMode.Fast, s.Mode);
        }
        finally { File.Delete(path); }
    }
}
