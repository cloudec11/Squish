namespace Squish.Core.Planning;

/// <summary>Still images (plan section 7). Decides format and strategy; the encoder runs the quality search.</summary>
public sealed class ImagePlanner(Tuning? tuning = null) : IPlanner
{
    private readonly Tuning _t = tuning ?? Tuning.Default;

    public EncodePlan Plan(MediaInfo info, CompressionRequest req, Capabilities caps)
    {
        long limit = req.TargetBytes;
        var srcFormat = AsImageFormat(info.Container);
        int w = info.DisplayWidth, h = info.DisplayHeight;

        // Skip: already fits and Discord previews the format.
        if (info.FileBytes <= limit * _t.MissFraction && srcFormat is { } f
            && (req.Format == OutputFormatPreference.Auto || ToPref(f) == req.Format))
        {
            return new CopyPlan
            {
                Info = info, Request = req, OutputExtension = Extension(f),
                PredictedBytes = info.FileBytes, Summary = "No compression needed",
            };
        }

        ImageFormat format, lossy;
        bool lossless;
        switch (req.Format)
        {
            case OutputFormatPreference.Jpeg: format = lossy = ImageFormat.Jpeg; lossless = false; break;
            case OutputFormatPreference.WebP: format = lossy = ImageFormat.WebP; lossless = true; break;
            case OutputFormatPreference.Png: format = lossy = ImageFormat.Png; lossless = true; break;
            default:
                (format, lossy, lossless) = info.Container switch
                {
                    ContainerFormat.Jpeg => (ImageFormat.Jpeg, ImageFormat.Jpeg, false),
                    ContainerFormat.Png or ContainerFormat.Gif => (ImageFormat.Png, ImageFormat.WebP, true),
                    ContainerFormat.WebP => (ImageFormat.WebP, ImageFormat.WebP, true),
                    _ when info.HasAlpha => (ImageFormat.WebP, ImageFormat.WebP, false),
                    _ => (ImageFormat.Jpeg, ImageFormat.Jpeg, false),
                };
                break;
        }

        var warnings = new List<string>();
        if (info.Container is ContainerFormat.Heif or ContainerFormat.Avif or ContainerFormat.Raw
            or ContainerFormat.Tiff or ContainerFormat.Bmp && lossy == ImageFormat.Jpeg)
            warnings.Add("Saved as JPEG so it previews in Discord.");

        return new ImagePlan
        {
            Info = info,
            Request = req,
            Format = format,
            LossyFormat = lossy,
            TryLossless = lossless,
            Width = w,
            Height = h,
            OutputExtension = Extension(format),
            PredictedBytes = (long)Math.Min(info.FileBytes, limit * _t.AimFraction),
            Summary = $"{w}×{h} · {lossy.ToString().ToUpperInvariant()}",
            Warnings = warnings,
        };
    }

    internal static ImageFormat? AsImageFormat(ContainerFormat c) => c switch
    {
        ContainerFormat.Jpeg => ImageFormat.Jpeg,
        ContainerFormat.Png => ImageFormat.Png,
        ContainerFormat.WebP => ImageFormat.WebP,
        ContainerFormat.Gif => ImageFormat.Gif,
        _ => null,
    };

    internal static OutputFormatPreference ToPref(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => OutputFormatPreference.Jpeg,
        ImageFormat.Png => OutputFormatPreference.Png,
        ImageFormat.WebP => OutputFormatPreference.WebP,
        _ => OutputFormatPreference.Gif,
    };

    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.WebP => ".webp",
        _ => ".gif",
    };
}

/// <summary>Animated GIF, WebP, APNG (plan section 7). Default output stays GIF.</summary>
public sealed class AnimatedPlanner(Tuning? tuning = null) : IPlanner
{
    private readonly Tuning _t = tuning ?? Tuning.Default;

    public EncodePlan Plan(MediaInfo info, CompressionRequest req, Capabilities caps)
    {
        long limit = req.TargetBytes;
        if (req.Format == OutputFormatPreference.Video)
            return new VideoPlanner(_t).Plan(info, req, caps);

        if (info.FileBytes <= limit * _t.MissFraction && info.Container is ContainerFormat.Gif or ContainerFormat.WebP)
            return new CopyPlan
            {
                Info = info, Request = req, OutputExtension = ImagePlanner.Extension(ImagePlanner.AsImageFormat(info.Container)!.Value),
                PredictedBytes = info.FileBytes, Summary = "No compression needed",
            };

        double srcFps = info.FrameRate > 0 ? info.FrameRate : 10;
        double fps = Math.Min(srcFps, _t.GifMaxFps);
        int w = info.DisplayWidth, h = info.DisplayHeight;

        // First guess: GIF size scales with pixel count × frame count. Only meaningful for GIF sources.
        double scale = 1;
        if (info.Container == ContainerFormat.Gif && info.FileBytes > 0)
        {
            double expected = info.FileBytes * (fps / srcFps);
            double want = limit * _t.AimFraction;
            if (expected > want) scale = SnapScale(Math.Sqrt(want / expected), _t.GifScaleStep);
        }
        (w, h) = ScaleDims(w, h, scale, _t.GifLongEdgeFloor);

        var warnings = new List<string>();
        if (info.FileBytes > limit * 4)
            warnings.Add("Big GIF: Convert to video keeps far more quality.");

        return new AnimatedPlan
        {
            Info = info,
            Request = req,
            OutputExtension = ".gif",
            Width = w,
            Height = h,
            Fps = fps,
            Colours = 256,
            PredictedBytes = (long)(limit * _t.AimFraction),
            Summary = $"{w}×{h} · {VideoPlanner.FormatFps(fps)} fps GIF",
            Warnings = warnings,
        };
    }

    /// <summary>Largest step^k that is at most <paramref name="s"/>.</summary>
    internal static double SnapScale(double s, double step)
    {
        if (s >= 1) return 1;
        double k = Math.Ceiling(Math.Log(s) / Math.Log(step));
        return Math.Pow(step, k);
    }

    /// <summary>Scale keeping aspect, never below the long-edge floor (unless the source is already smaller).</summary>
    internal static (int W, int H) ScaleDims(int w, int h, double scale, int longEdgeFloor)
    {
        int longEdge = Math.Max(w, h);
        double minScale = Math.Min(1, longEdgeFloor / (double)longEdge);
        scale = Math.Clamp(scale, minScale, 1);
        return (Math.Max(2, (int)Math.Round(w * scale / 2) * 2), Math.Max(2, (int)Math.Round(h * scale / 2) * 2));
    }
}

/// <summary>Dispatches to the planner for the media kind.</summary>
public sealed class Planner(Tuning? tuning = null) : IPlanner
{
    private readonly VideoPlanner _video = new(tuning);
    private readonly ImagePlanner _image = new(tuning);
    private readonly AnimatedPlanner _animated = new(tuning);

    public EncodePlan Plan(MediaInfo info, CompressionRequest req, Capabilities caps) => info.Kind switch
    {
        MediaKind.Video => _video.Plan(info, req, caps),
        MediaKind.StillImage => _image.Plan(info, req, caps),
        MediaKind.AnimatedImage => _animated.Plan(info, req, caps),
        MediaKind.AudioOnly => throw new SquishException(ErrorCode.AudioOnly, "This is an audio file. Squish only compresses videos and images."),
        _ => throw new SquishException(ErrorCode.Unsupported, "Squish can't compress this kind of file."),
    };
}
