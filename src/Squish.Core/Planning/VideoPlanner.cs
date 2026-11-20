namespace Squish.Core.Planning;

/// <summary>
/// Pure function from MediaInfo + target to a VideoPlan (plan section 6). No I/O.
/// </summary>
public sealed class VideoPlanner(Tuning? tuning = null) : IPlanner
{
    private readonly Tuning _t = tuning ?? Tuning.Default;

    private const int MinVideoKbps = 30;

    public EncodePlan Plan(MediaInfo info, CompressionRequest req, Capabilities caps)
    {
        if (info.Kind is not (MediaKind.Video or MediaKind.AnimatedImage))
            throw new ArgumentException("VideoPlanner only plans video.", nameof(info));

        long limit = req.TargetBytes;

        // ---- Step 1: skip or pass through
        if (info.Kind == MediaKind.Video && info.FileBytes <= limit * _t.MissFraction)
        {
            bool friendlyVideo = info.VideoCodec is "h264" or "hevc";
            bool friendlyAudio = !info.HasAudio || info.AudioCodec == "aac";
            // Copy only when the audio is AAC too: Opus/AC3/FLAC in MP4 may not play in Discord's player.
            if (info.Container == ContainerFormat.Mp4 && friendlyVideo && friendlyAudio)
                return new CopyPlan
                {
                    Info = info, Request = req, OutputExtension = ".mp4",
                    PredictedBytes = info.FileBytes, Summary = "No compression needed",
                };
            if (friendlyVideo && info.Container != ContainerFormat.Mp4)
                return new RemuxPlan
                {
                    Info = info, Request = req, OutputExtension = ".mp4",
                    PredictedBytes = info.FileBytes, IncludeAudio = friendlyAudio && info.HasAudio,
                    Summary = "No compression needed",
                    Warnings = friendlyAudio || !info.HasAudio ? [] : ["Audio dropped: its format doesn't fit in MP4 without re-encoding."],
                };
        }

        // ---- Encoder choice (step 5)
        var (encoder, isHardware) = ChooseEncoder(req, caps);
        double aim = isHardware ? _t.FastAimFraction : _t.AimFraction;

        // ---- Step 2 and 3: bitrate budget and audio
        double seconds = Math.Max(info.Duration, 0.1);
        double budgetBytes = limit * aim;
        double totalKbps = 8 * budgetBytes / seconds / 1000.0;

        int audioKbps = 0, channels = 0;
        bool copyAudio = false, includeAudio = info.HasAudio;
        if (includeAudio)
        {
            (audioKbps, channels) = AudioFor(totalKbps);
            long srcKbps = info.AudioBitrate / 1000;
            if (info.AudioCodec == "aac" && srcKbps > 0 && srcKbps <= audioKbps && info.AudioChannels is 1 or 2)
            {
                copyAudio = true;
                audioKbps = (int)srcKbps;
                channels = info.AudioChannels;
            }
        }

        double videoKbps = totalKbps * (1 - _t.ContainerOverhead) - audioKbps;
        bool capped = false;
        double srcVideoKbps = info.VideoBitrate / 1000.0;
        if (srcVideoKbps > 0)
        {
            // Short clips: never inflate. Longer clips: a re-encode never needs much more than the source had.
            double cap = info.Duration < _t.ShortClipSeconds ? srcVideoKbps : srcVideoKbps * 1.5;
            if (videoKbps > cap) { videoKbps = cap; capped = true; }
        }
        videoKbps = Math.Max(MinVideoKbps, videoKbps);

        // ---- Step 4: resolution and frame rate
        double floor = encoder is "libx265" or "libsvtav1" or "libaom-av1" ? _t.BppFloorHevcAv1 : _t.BppFloorH264;
        var choice = ChooseRung(info, videoKbps, floor);

        var warnings = new List<string>();
        double targetMb = limit / 1_000_000.0;
        if (choice.TooLong)
            warnings.Add($"Too long for {targetMb:0.#} MB: it will look very soft. Try a larger size.");
        else if (choice.ShortSide < 480 && info.ShortSide >= 480)
            warnings.Add($"Long clip: will look soft at {targetMb:0.#} MB");
        if (info.Hdr) warnings.Add("HDR converted to standard colour.");

        double predicted = (videoKbps + audioKbps) * 1000 / 8 * seconds * (1 + _t.ContainerOverhead);
        predicted = Math.Min(predicted, budgetBytes);

        return new VideoPlan
        {
            Info = info,
            Request = req,
            OutputExtension = ".mp4",
            Width = choice.Width,
            Height = choice.Height,
            Fps = choice.Fps,
            VideoKbps = (int)Math.Round(videoKbps),
            AudioKbps = audioKbps,
            AudioChannels = channels,
            IncludeAudio = includeAudio,
            CopyAudio = copyAudio,
            VideoEncoder = encoder,
            ToneMapHdr = info.Hdr,
            Deinterlace = info.Interlaced,
            Rung = choice.Rung,
            AimFraction = aim,
            TooLong = choice.TooLong,
            BitsPerPixel = choice.Bpp,
            CappedAtSource = capped,
            PredictedBytes = (long)predicted,
            Summary = $"{choice.ShortSide}p · {FormatFps(choice.Fps)} fps",
            Warnings = warnings,
        };
    }

    public (int Kbps, int Channels) AudioFor(double totalKbps)
    {
        foreach (var tier in _t.AudioTiers)
            if (totalKbps > tier.MinTotalKbps || tier.MinTotalKbps == 0)
                return (tier.AudioKbps, tier.Channels);
        var last = _t.AudioTiers[^1];
        return (last.AudioKbps, last.Channels);
    }

    private (string Encoder, bool Hardware) ChooseEncoder(CompressionRequest req, Capabilities caps)
    {
        if (req.Mode == QualityMode.Fast && caps.HardwareH264 is { } hw && req.Codec == CodecPreference.H264)
            return (hw, true);
        return req.Codec switch
        {
            CodecPreference.Hevc when caps.Has("libx265") => ("libx265", false),
            CodecPreference.Av1 when caps.Has("libsvtav1") => ("libsvtav1", false),
            CodecPreference.Av1 when caps.Has("libaom-av1") => ("libaom-av1", false),
            _ => ("libx264", false),
        };
    }

    public sealed record RungChoice(int Rung, int ShortSide, int Width, int Height, double Fps, double Bpp, bool TooLong);

    /// <summary>The short sides to try, largest first. Never upscales.</summary>
    public List<int> RungsFor(MediaInfo info)
    {
        int src = info.ShortSide;
        var rungs = _t.HeightLadder.Where(h => h <= src).OrderDescending().ToList();
        if (rungs.Count == 0 || rungs[0] != src) rungs.Insert(0, src);
        return rungs;
    }

    public RungChoice ChooseRung(MediaInfo info, double videoKbps, double floor)
    {
        double srcFps = info.FrameRate > 0 ? info.FrameRate : 30;
        double fps = Math.Min(srcFps, _t.MaxFrameRate);
        double halfFps = fps > 30 ? fps / 2 : fps;
        var rungs = RungsFor(info);
        var high = rungs.Where(r => r >= 720).ToList();
        var low = rungs.Where(r => r < 720).ToList();

        IEnumerable<(int Side, double Fps)> Candidates()
        {
            foreach (var r in high) yield return (r, fps);
            if (fps > 30) foreach (var r in high) yield return (r, halfFps);
            // A source already below 720p keeps its frame rate at its own size if the bitrate allows;
            // the halve-before-dropping rule only applies once we would go smaller than the source.
            if (high.Count == 0) yield return (low[0], fps);
            foreach (var r in low) yield return (r, halfFps);
        }

        foreach (var (side, f) in Candidates())
        {
            var (w, h) = DimsFor(info, side);
            double bpp = videoKbps * 1000 / (w * (double)h * f);
            if (bpp >= floor) return new(rungs.IndexOf(side), side, w, h, f, bpp, false);
        }

        // Nothing passes: smallest rung, at most 24 fps, flagged.
        int last = rungs[^1];
        double lastFps = Math.Min(halfFps, 24);
        var (lw, lh) = DimsFor(info, last);
        double lbpp = videoKbps * 1000 / (lw * (double)lh * lastFps);
        return new(rungs.Count - 1, last, lw, lh, lastFps, lbpp, lbpp < floor);
    }

    /// <summary>Output display dimensions for a short side, keeping aspect ratio, even numbers.</summary>
    public static (int Width, int Height) DimsFor(MediaInfo info, int shortSide)
    {
        int dw = info.DisplayWidth, dh = info.DisplayHeight;
        if (shortSide >= Math.Min(dw, dh)) return (Even(dw), Even(dh));
        if (dw >= dh)
            return (Even(shortSide * (double)dw / dh), Even(shortSide));
        return (Even(shortSide), Even(shortSide * (double)dh / dw));
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    public static string FormatFps(double fps) =>
        Math.Abs(fps - Math.Round(fps)) < 0.05 ? Math.Round(fps).ToString("0") : fps.ToString("0.##");
}
