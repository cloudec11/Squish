using System.Globalization;

namespace Squish.Core.Encoders;

/// <summary>Builds FFmpeg argument arrays for video plans. Pure, so tests can assert on it.</summary>
public static class VideoArgs
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool IsHardware(string encoder) => encoder is "h264_nvenc" or "h264_qsv" or "h264_amf";

    /// <summary>Keeps NTSC rates exact (29.97 stays 30000/1001), everything else to 3 decimals.</summary>
    public static string FpsArg(double fps)
    {
        foreach (var (num, label) in new[] { (24000, "24000/1001"), (30000, "30000/1001"), (60000, "60000/1001"), (15000, "15000/1001") })
            if (Math.Abs(fps - num / 1001.0) < 0.005) return label;
        return Math.Round(fps, 3).ToString(Inv);
    }

    public static string Filter(VideoPlan p)
    {
        var f = new List<string>();
        if (p.Deinterlace) f.Add("bwdif=mode=send_frame");
        f.Add($"scale={p.Width}:{p.Height}:flags=lanczos");
        if (p.ToneMapHdr)
            f.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        f.Add($"fps={FpsArg(p.Fps)}");
        f.Add("setsar=1");
        f.Add(p.VideoEncoder is "h264_qsv" or "h264_amf" ? "format=nv12" : "format=yuv420p");
        return string.Join(',', f);
    }

    private static string K(double kbps) => $"{Math.Max(1, (int)Math.Round(kbps))}k";

    private static IEnumerable<string> VideoCodec(VideoPlan p, int pass, string passLog)
    {
        int b = p.VideoKbps;
        switch (p.VideoEncoder)
        {
            case "libx264":
                return ["-c:v", "libx264", "-preset", "slow", "-b:v", K(b), "-maxrate", K(b * 1.5), "-bufsize", K(b * 2),
                        "-profile:v", "high", "-pass", pass.ToString(Inv), "-passlogfile", passLog];
            case "libx265":
                // x265 stats path goes through -x265-params, where ':' separates options. Pass a bare file
                // name and run FFmpeg in the work directory instead of passing an absolute Windows path.
                var stats = Path.GetFileName(passLog) + ".x265";
                return ["-c:v", "libx265", "-preset", "medium", "-b:v", K(b), "-tag:v", "hvc1",
                        "-x265-params", $"pass={pass}:stats={stats}:vbv-maxrate={(int)(b * 1.5)}:vbv-bufsize={b * 2}:log-level=error"];
            case "libsvtav1":
                return ["-c:v", "libsvtav1", "-preset", "8", "-b:v", K(b), "-svtav1-params", "rc=1"];
            case "libaom-av1":
                return ["-c:v", "libaom-av1", "-cpu-used", "6", "-row-mt", "1", "-b:v", K(b), "-maxrate", K(b * 1.5), "-bufsize", K(b * 2)];
            case "h264_nvenc":
                return ["-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-multipass", "qres",
                        "-b:v", K(b), "-maxrate", K(b * 1.15), "-bufsize", K(b * 2), "-profile:v", "high"];
            case "h264_qsv":
                return ["-c:v", "h264_qsv", "-preset", "slower", "-b:v", K(b), "-maxrate", K(b * 1.15), "-bufsize", K(b * 2), "-profile:v", "high"];
            case "h264_amf":
                return ["-c:v", "h264_amf", "-quality", "quality", "-rc", "vbr_peak", "-b:v", K(b), "-maxrate", K(b * 1.15),
                        "-bufsize", K(b * 2), "-profile:v", "high"];
            default:
                throw new ArgumentException($"Unknown encoder {p.VideoEncoder}");
        }
    }

    private static List<string> Head(string input) =>
        ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-i", input];

    /// <summary>Pass 1 of a two-pass encode: video only, to the null muxer.</summary>
    public static IReadOnlyList<string> Pass1(VideoPlan p, string passLog)
    {
        var a = Head(p.Info.Path);
        a.AddRange(["-map", "0:v:0", "-vf", Filter(p)]);
        a.AddRange(VideoCodec(p, 1, passLog));
        a.AddRange(["-an", "-sn", "-dn", "-f", "null", "-"]);
        return a;
    }

    /// <summary>The final encode: pass 2 for two-pass encoders, or the only pass for the rest.</summary>
    public static IReadOnlyList<string> Final(VideoPlan p, string passLog, string output)
    {
        var a = Head(p.Info.Path);
        a.AddRange(["-map", "0:v:0"]);
        if (p.IncludeAudio) a.AddRange(["-map", "0:a:0?"]);
        a.AddRange(["-vf", Filter(p)]);
        a.AddRange(VideoCodec(p, p.TwoPass ? 2 : 0, passLog));
        if (p.IncludeAudio)
        {
            if (p.CopyAudio) a.AddRange(["-c:a", "copy"]);
            else a.AddRange(["-c:a", "aac", "-b:a", $"{p.AudioKbps}k", "-ac", p.AudioChannels.ToString(Inv)]);
        }
        else a.Add("-an");
        a.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
                    "-movflags", "+faststart", "-f", "mp4", output]);
        return a;
    }

    public static IReadOnlyList<string> Remux(RemuxPlan p, string output)
    {
        var a = Head(p.Info.Path);
        a.AddRange(["-map", "0:v:0"]);
        if (p.IncludeAudio) a.AddRange(["-map", "0:a:0"]);
        else a.Add("-an");
        a.AddRange(["-c", "copy", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
                    "-movflags", "+faststart", "-f", "mp4", output]);
        if (p.Info.VideoCodec == "hevc") a.InsertRange(a.Count - 3, ["-tag:v", "hvc1"]);
        return a;
    }
}
