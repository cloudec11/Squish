using System.Globalization;
using System.Text.Json;
using ImageMagick;
using Squish.Core.Ffmpeg;

namespace Squish.Core.Probe;

/// <summary>Probes a file by content: images through Magick.NET ping, everything else through ffprobe.</summary>
public sealed class MediaProbe(FfmpegTools tools, ProcessRunner runner) : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new SquishException(ErrorCode.FileNotFound, "The file is gone. It may have been moved or deleted.");
        var bytes = new FileInfo(path).Length;
        if (bytes == 0)
            throw new SquishException(ErrorCode.EmptyFile, "The file is empty (0 bytes).");

        ContentSniffer.Sniff? sniff;
        try { sniff = ContentSniffer.Detect(path); }
        catch (IOException ex) { throw new SquishException(ErrorCode.Corrupt, "Couldn't read the file.", ex.Message, ex); }

        if (sniff is not null)
        {
            // Animated GIF/WebP/APNG/AVIS are handled by ffmpeg where it can decode them.
            return await Task.Run(() => ProbeImage(path, bytes, sniff), ct).ConfigureAwait(false);
        }
        return await ProbeWithFfprobeAsync(path, bytes, ct).ConfigureAwait(false);
    }

    private static MediaInfo ProbeImage(string path, long bytes, ContentSniffer.Sniff sniff)
    {
        try
        {
            int frames = 1, width, height;
            double duration = 0;
            Orientation orientation;
            bool alpha = sniff.Alpha;
            if (sniff.Format is ContainerFormat.Gif || sniff.Animated)
            {
                using var col = new MagickImageCollection();
                col.Ping(path);
                frames = col.Count;
                width = (int)col[0].Page.Width; height = (int)col[0].Page.Height;
                if (width == 0 || height == 0) { width = (int)col[0].Width; height = (int)col[0].Height; }
                orientation = Orientation.TopLeft;
                // AnimationDelay is in ticks (1/100 s by default). Browsers treat 0-1 as 10.
                duration = col.Sum(f => (f.AnimationDelay <= 1 ? 10 : f.AnimationDelay) / (double)Math.Max(1, f.AnimationTicksPerSecond));
                if (sniff.Format is ContainerFormat.Gif) alpha = true; // GIF transparency is common; treated as possible.
            }
            else
            {
                var info = new MagickImageInfo(path);
                width = (int)info.Width; height = (int)info.Height;
                orientation = info.Orientation;
            }

            if (width <= 0 || height <= 0)
                throw new SquishException(ErrorCode.Corrupt, "The image has no size. It may be damaged.");

            bool animated = frames > 1;
            int rotation = orientation switch
            {
                Orientation.RightTop or Orientation.LeftTop => 90,
                Orientation.LeftBottom or Orientation.RightBottom => 270,
                Orientation.BottomRight or Orientation.BottomLeft => 180,
                _ => 0,
            };
            return new MediaInfo
            {
                Path = path,
                Kind = animated ? MediaKind.AnimatedImage : MediaKind.StillImage,
                Container = sniff.Format,
                FileBytes = bytes,
                Width = width,
                Height = height,
                Rotation = rotation,
                HasAlpha = alpha,
                FrameCount = frames,
                Duration = animated ? duration : 0,
                FrameRate = animated && duration > 0 ? frames / duration : 0,
                TotalBitrate = animated && duration > 0 ? (long)(bytes * 8 / duration) : 0,
            };
        }
        catch (MagickException ex)
        {
            throw new SquishException(ErrorCode.Corrupt,
                "Couldn't read this image. It may be damaged or in a format Squish doesn't support.", ex.Message, ex);
        }
    }

    private async Task<MediaInfo> ProbeWithFfprobeAsync(string path, long bytes, CancellationToken ct)
    {
        var result = await runner.RunAsync(tools.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "--", path],
            lowPriority: false, ct: ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            if (result.StderrTail.Contains("drm", StringComparison.OrdinalIgnoreCase))
                throw new SquishException(ErrorCode.DrmProtected, "This file is copy-protected (DRM) and can't be compressed.");
            throw new SquishException(ErrorCode.Unsupported,
                "This isn't a video or image Squish can read. It may be damaged or in an unsupported format.",
                result.StderrTail);
        }
        return Parse(path, bytes, result.Stdout);
    }

    /// <summary>Parses ffprobe JSON. Internal so the tests can feed captured output.</summary>
    internal static MediaInfo Parse(string path, long bytes, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : [];

        string formatName = Str(format, "format_name") ?? "";
        var video = streams.FirstOrDefault(x =>
            Str(x, "codec_type") == "video" && Int(Prop(x, "disposition"), "attached_pic") != 1);
        var audio = streams.FirstOrDefault(x => Str(x, "codec_type") == "audio");
        bool hasVideo = video.ValueKind == JsonValueKind.Object;
        bool hasAudio = audio.ValueKind == JsonValueKind.Object;

        bool encrypted = streams.Any(x => Str(x, "codec_tag_string") is "encv" or "enca" or "drms" or "drmi")
                         || (Str(format, "format_name")?.Contains("drm") ?? false);
        if (encrypted)
            throw new SquishException(ErrorCode.DrmProtected, "This file is copy-protected (DRM) and can't be compressed.");

        if (!hasVideo && hasAudio)
            throw new SquishException(ErrorCode.AudioOnly, "This is an audio file. Squish only compresses videos and images.");
        if (!hasVideo)
            throw new SquishException(ErrorCode.Unsupported, "No video found in this file.");

        string? vcodec = Str(video, "codec_name");
        int width = Int(video, "width"), height = Int(video, "height");
        if (width <= 0 || height <= 0)
            throw new SquishException(ErrorCode.Corrupt, "The video has no picture size. It may be damaged.");

        double duration = Dbl(format, "duration");
        if (duration <= 0) duration = Dbl(video, "duration");
        if (duration <= 0 || double.IsNaN(duration))
        {
            // A single-frame "video" (e.g. a lone image through ffprobe) is a still image.
            if (formatName.Contains("image2") || formatName.EndsWith("_pipe"))
                return new MediaInfo { Path = path, Kind = MediaKind.StillImage, Container = ContainerFormat.OtherImage, FileBytes = bytes, Width = width, Height = height };
            throw new SquishException(ErrorCode.NoDuration, "Couldn't work out how long this video is. It may be damaged or still recording.");
        }

        double avgFps = Rate(Str(video, "avg_frame_rate"));
        double rFps = Rate(Str(video, "r_frame_rate"));
        if (avgFps <= 0 || avgFps > 1000) avgFps = rFps is > 0 and <= 1000 ? rFps : 30;
        bool vfr = rFps > 0 && Math.Abs(rFps - avgFps) / avgFps > 0.02;

        long frames = Long(video, "nb_frames");
        if (frames <= 0) frames = (long)Math.Round(duration * avgFps);

        string? transfer = Str(video, "color_transfer");
        bool hdr = transfer is "smpte2084" or "arib-std-b67";
        string? fieldOrder = Str(video, "field_order");
        bool interlaced = fieldOrder is "tt" or "bb" or "tb" or "bt";

        long totalBitrate = Long(format, "bit_rate");
        if (totalBitrate <= 0) totalBitrate = (long)(bytes * 8 / duration);
        long vBitrate = Long(video, "bit_rate");
        long aBitrate = hasAudio ? Long(audio, "bit_rate") : 0;
        if (vBitrate <= 0) vBitrate = Math.Max(0, totalBitrate - aBitrate);

        return new MediaInfo
        {
            Path = path,
            Kind = MediaKind.Video,
            Container = MapContainer(formatName, format, vcodec, hasAudio ? Str(audio, "codec_name") : null),
            FileBytes = bytes,
            Duration = duration,
            Width = width,
            Height = height,
            Rotation = ReadRotation(video),
            FrameRate = avgFps,
            VariableFrameRate = vfr,
            FrameCount = frames,
            VideoCodec = vcodec,
            PixelFormat = Str(video, "pix_fmt"),
            VideoBitrate = vBitrate,
            Interlaced = interlaced,
            Hdr = hdr,
            HasAlpha = Str(video, "pix_fmt")?.StartsWith("yuva") ?? false,
            HasAudio = hasAudio,
            AudioCodec = hasAudio ? Str(audio, "codec_name") : null,
            AudioBitrate = aBitrate,
            AudioChannels = hasAudio ? Int(audio, "channels") : 0,
            TotalBitrate = totalBitrate,
        };
    }

    private static ContainerFormat MapContainer(string name, JsonElement format, string? vcodec, string? acodec)
    {
        if (name.Contains("mp4") || name.StartsWith("mov"))
        {
            var brand = Str(Prop(format, "tags"), "major_brand")?.Trim() ?? "";
            if (brand == "qt") return ContainerFormat.Mov;
            if (brand.StartsWith("3g")) return ContainerFormat.ThreeGp;
            return ContainerFormat.Mp4;
        }
        if (name.Contains("matroska") || name.Contains("webm"))
        {
            bool webmCodecs = vcodec is "vp8" or "vp9" or "av1" && acodec is null or "opus" or "vorbis";
            return webmCodecs ? ContainerFormat.WebM : ContainerFormat.Mkv;
        }
        return name switch
        {
            "avi" => ContainerFormat.Avi,
            "asf" => ContainerFormat.Asf,
            "flv" => ContainerFormat.Flv,
            "mpegts" => ContainerFormat.MpegTs,
            "gif" => ContainerFormat.Gif,
            "apng" => ContainerFormat.Png,
            "webp_pipe" => ContainerFormat.WebP,
            _ => ContainerFormat.OtherVideo,
        };
    }

    private static int ReadRotation(JsonElement video)
    {
        double rot = 0;
        if (video.TryGetProperty("side_data_list", out var sdl) && sdl.ValueKind == JsonValueKind.Array)
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r) && r.TryGetDouble(out var d)) rot = d;
        if (rot == 0 && double.TryParse(Str(Prop(video, "tags"), "rotate"), CultureInfo.InvariantCulture, out var t)) rot = -t;
        // Display matrix rotation is counter-clockwise; normalise to 0..359 clockwise.
        int deg = (int)Math.Round(-rot) % 360;
        if (deg < 0) deg += 360;
        return deg is 90 or 180 or 270 ? deg : 0;
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private static long Long(JsonElement e, string name) => (long)Dbl(e, name);
    private static int Int(JsonElement e, string name) => (int)Dbl(e, name);

    internal static double Rate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
