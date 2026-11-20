namespace Squish.Core;

public enum MediaKind { Video, StillImage, AnimatedImage, AudioOnly, Unknown }

/// <summary>Container or image format as detected from content, never from the extension.</summary>
public enum ContainerFormat
{
    Unknown, Mp4, Mov, Mkv, WebM, Avi, Asf, Flv, MpegTs, ThreeGp, OtherVideo,
    Jpeg, Png, WebP, Gif, Heif, Avif, Bmp, Tiff, Raw, OtherImage,
}

public sealed record MediaInfo
{
    public required string Path { get; init; }
    public required MediaKind Kind { get; init; }
    public required ContainerFormat Container { get; init; }
    public long FileBytes { get; init; }

    /// <summary>Seconds. Zero for still images.</summary>
    public double Duration { get; init; }

    /// <summary>Coded width and height, before rotation is applied.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Rotation in degrees from metadata (0, 90, 180, 270).</summary>
    public int Rotation { get; init; }

    /// <summary>Average frame rate.</summary>
    public double FrameRate { get; init; }
    public bool VariableFrameRate { get; init; }
    public long FrameCount { get; init; }

    public string? VideoCodec { get; init; }
    public string? PixelFormat { get; init; }
    public long VideoBitrate { get; init; }
    public bool Interlaced { get; init; }
    public bool Hdr { get; init; }
    public bool HasAlpha { get; init; }

    public bool HasAudio { get; init; }
    public string? AudioCodec { get; init; }
    public long AudioBitrate { get; init; }
    public int AudioChannels { get; init; }

    /// <summary>Overall bitrate in bits per second (from container, or size / duration).</summary>
    public long TotalBitrate { get; init; }

    public bool Encrypted { get; init; }

    /// <summary>Width and height after rotation, as the viewer sees it.</summary>
    public int DisplayWidth => Rotation is 90 or 270 ? Height : Width;
    public int DisplayHeight => Rotation is 90 or 270 ? Width : Height;

    /// <summary>The "p" number of the video: the shorter display side.</summary>
    public int ShortSide => Math.Min(DisplayWidth, DisplayHeight);
}
