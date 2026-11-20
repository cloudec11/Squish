using ImageMagick;

namespace Squish.Core.Encoders;

/// <summary>
/// Still images through Magick.NET (plan section 7): lossless first, then a binary search on quality,
/// then downscale in 0.85 steps to a 1280 px long edge, then a last-resort quality floor of 40.
/// Every candidate is encoded in memory, so its size is exact before anything touches the disk.
/// </summary>
public sealed class ImageEncoder(WorkArea work, Tuning? tuning = null) : IEncoder
{
    private readonly Tuning _t = tuning ?? Tuning.Default;

    /// <summary>libwebp refuses images wider or taller than this.</summary>
    internal const uint WebPMaxEdge = 16383;

    public Task<EncodeResult> RunAsync(EncodePlan plan, IProgress<JobProgress> p, CancellationToken ct) =>
        Task.Run(() =>
        {
            try { return Run((ImagePlan)plan, p, ct); }
            catch (MagickException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (MagickException ex)
            {
                throw new SquishException(ErrorCode.EncoderFailed,
                    "Couldn't save this image. Try a different image format in settings.", ex.Message, ex);
            }
        }, ct);

    private EncodeResult Run(ImagePlan plan, IProgress<JobProgress> p, CancellationToken ct)
    {
        long fitBytes = (long)(plan.Request.TargetBytes * _t.AimFraction);
        var warnings = new List<string>(plan.Warnings);
        int attempts = 0;

        p.Report(new(2, "Reading image"));
        using var source = Load(plan.Info.Path);
        ct.ThrowIfCancellationRequested();

        bool hasAlpha = source.HasAlpha && !source.IsOpaque;
        var lossy = plan.LossyFormat;
        if (plan.Request.Format == OutputFormatPreference.Auto && lossy == ImageFormat.Jpeg && hasAlpha)
            lossy = ImageFormat.WebP; // JPEG can't hold transparency.

        byte[]? Fit(ImageFormat format, int quality, uint longEdge, bool lossless)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            if (format == ImageFormat.WebP) longEdge = Math.Min(longEdge, WebPMaxEdge);
            byte[] bytes;
            try { bytes = Encode(source, format, quality, longEdge, lossless, hasAlpha, ct); }
            catch (MagickException) when (!ct.IsCancellationRequested)
            {
                return null; // this candidate can't be written; a smaller one may still work
            }
            ct.ThrowIfCancellationRequested();
            return bytes.Length <= fitBytes ? bytes : null;
        }

        uint srcLong = Math.Max(source.Width, source.Height);
        byte[]? result = null;
        ImageFormat outFormat = lossy;
        string how = "";

        // 1. Lossless.
        if (plan.TryLossless)
        {
            p.Report(new(10, "Trying lossless"));
            var losslessFormat = plan.Format == ImageFormat.WebP ? ImageFormat.WebP : ImageFormat.Png;
            result = Fit(losslessFormat, 100, srcLong, lossless: true);
            if (result is not null) { outFormat = losslessFormat; how = "lossless"; }
        }

        // 2-4. Quality search, downscaling between rounds.
        if (result is null && lossy == ImageFormat.Png)
        {
            // User forced PNG: only size can change.
            uint edge = srcLong;
            while (result is null && edge >= 16)
            {
                edge = (uint)(edge * _t.ImageDownscaleStep);
                p.Report(new(30, $"Resizing to {edge} px"));
                result = Fit(ImageFormat.Png, 100, edge, lossless: true);
            }
            outFormat = ImageFormat.Png;
            how = $"lossless, resized to {edge} px";
        }

        if (result is null && lossy != ImageFormat.Png)
        {
            uint edge = lossy == ImageFormat.WebP ? Math.Min(srcLong, WebPMaxEdge) : srcLong;
            int round = 0;
            while (true)
            {
                p.Report(new(Math.Min(90, 20 + round * 12), round == 0 ? "Finding best quality" : $"Resizing to {edge} px"));

                // Above the floor: quality 60-95 only. At or below it, quality may fall to 40 (last resort).
                var (bytes, q) = SearchQuality(lossy, edge, _t.ImageQualityMin, _t.ImageQualityMax, Fit);
                if (bytes is null && edge <= _t.ImageLongEdgeFloor)
                    (bytes, q) = SearchQuality(lossy, edge, _t.ImageQualityLastResort, _t.ImageQualityMin - 1, Fit);
                if (bytes is not null)
                {
                    result = bytes;
                    outFormat = lossy;
                    how = edge < srcLong ? $"quality {q}, resized to {edge} px" : $"quality {q}";
                    if (q < _t.ImageQualityMin || edge < _t.ImageLongEdgeFloor)
                        warnings.Add("Reduced heavily to fit. Try a larger size for a sharper image.");
                    break;
                }

                if (edge <= 16)
                    throw new SquishException(ErrorCode.TargetUnreachable,
                        "This image can't get small enough for that size. Pick a larger size.");
                // Down to the floor in 0.85 steps; once at or below it, keep stepping (last resort).
                uint next = (uint)Math.Floor(edge * _t.ImageDownscaleStep);
                if (edge > _t.ImageLongEdgeFloor && next < _t.ImageLongEdgeFloor) next = (uint)_t.ImageLongEdgeFloor;
                edge = next;
                round++;
            }
        }

        if (result is null)
            throw new SquishException(ErrorCode.TargetUnreachable, "This image can't get small enough for that size. Pick a larger size.");

        p.Report(new(95, "Saving"));
        var output = work.NewTempFile(Planning.ImagePlanner.Extension(outFormat));
        using (var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.Write(result);

        var finalPlan = plan with
        {
            Format = outFormat,
            OutputExtension = Planning.ImagePlanner.Extension(outFormat),
            Summary = $"{outFormat.ToString().ToUpperInvariant()}, {how}",
        };
        p.Report(new(100, "Done"));
        return new EncodeResult(finalPlan, output, result.Length, attempts, warnings);
    }

    /// <summary>Binary search for the highest quality that fits, at most ImageMaxSearchEncodes encodes.</summary>
    private (byte[]? Bytes, int Quality) SearchQuality(
        ImageFormat format, uint edge, int lo, int hi, Func<ImageFormat, int, uint, bool, byte[]?> fit)
    {
        byte[]? best = null;
        int bestQ = 0;
        int encodes = 0;
        while (lo <= hi && encodes < _t.ImageMaxSearchEncodes)
        {
            int mid = (lo + hi + 1) / 2;
            encodes++;
            var bytes = fit(format, mid, edge, false);
            if (bytes is not null) { best = bytes; bestQ = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return (best, bestQ);
    }

    private static MagickImage Load(string path)
    {
        try
        {
            var img = new MagickImage(path);
            img.AutoOrient();

            // Convert to sRGB and drop the profile so colours match on every client.
            if (img.ColorSpace == ColorSpace.CMYK && img.GetColorProfile() is null)
                img.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
            else if (img.GetColorProfile() is not null)
                img.TransformColorSpace(ColorProfiles.SRGB);
            if (img.ColorSpace != ColorSpace.sRGB && img.ColorSpace != ColorSpace.Gray)
                img.ColorSpace = ColorSpace.sRGB;

            img.Strip(); // EXIF (including GPS), XMP, IPTC, ICC.
            img.Depth = 8;
            return img;
        }
        catch (MagickException ex)
        {
            throw new SquishException(ErrorCode.Corrupt,
                "Couldn't read this image. It may be damaged or in a format Squish doesn't support.", ex.Message, ex);
        }
    }

    internal byte[] Encode(MagickImage source, ImageFormat format, int quality, uint longEdge, bool lossless, bool hasAlpha,
        CancellationToken ct = default)
    {
        using var img = (MagickImage)source.Clone();
        // ImageMagick reports progress during resize and encode; cancelling there stops a long single encode.
        img.Progress += (_, e) => { if (ct.IsCancellationRequested) e.Cancel = true; };
        if (longEdge < Math.Max(img.Width, img.Height))
        {
            img.FilterType = FilterType.Lanczos;
            img.Resize(new MagickGeometry(longEdge, longEdge)); // fits inside, keeps aspect
        }

        switch (format)
        {
            case ImageFormat.Jpeg:
                if (hasAlpha)
                {
                    img.BackgroundColor = MagickColors.White;
                    img.Alpha(AlphaAction.Remove);
                }
                img.Format = MagickFormat.Jpeg;
                img.Quality = (uint)quality;
                img.Settings.Interlace = Interlace.Jpeg; // progressive
                img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor",
                    quality < _t.JpegChromaFullAbove ? "4:2:0" : "4:4:4");
                img.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
                break;
            case ImageFormat.WebP:
                img.Format = MagickFormat.WebP;
                if (lossless)
                {
                    img.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
                    img.Settings.SetDefine(MagickFormat.WebP, "method", "5");
                    img.Quality = 100;
                }
                else
                {
                    img.Quality = (uint)quality;
                    img.Settings.SetDefine(MagickFormat.WebP, "method", "4");
                }
                break;
            case ImageFormat.Png:
                img.Format = MagickFormat.Png;
                img.Quality = 95; // zlib level 9, adaptive filtering
                img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                if (!hasAlpha && img.HasAlpha) img.Alpha(AlphaAction.Off);
                break;
            default:
                throw new ArgumentException("Animated formats go through the animated encoder.");
        }
        return img.ToByteArray();
    }
}
