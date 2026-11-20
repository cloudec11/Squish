using Squish.Core.Probe;

namespace Squish.Core.Tests;

public class SnifferTests
{
    private static string Write(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squish-sniff-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Png(bool alpha, bool apng)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            ms.Write(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data.Length)));
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]); // CRC, unchecked
        }
        Chunk("IHDR", [0, 0, 0, 16, 0, 0, 0, 16, 8, (byte)(alpha ? 6 : 2), 0, 0, 0]);
        if (apng) Chunk("acTL", new byte[8]);
        Chunk("IDAT", new byte[4]);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Png_alpha_and_apng(bool alpha, bool apng)
    {
        var path = Write(Png(alpha, apng));
        try
        {
            var s = ContentSniffer.Detect(path);
            Assert.NotNull(s);
            Assert.Equal(ContainerFormat.Png, s!.Format);
            Assert.Equal(alpha, s.Alpha);
            Assert.Equal(apng, s.Animated);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, ContainerFormat.Jpeg)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0, 0, 0, 0, 0, 0 }, ContainerFormat.Gif)]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, ContainerFormat.Heif)]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, ContainerFormat.Avif)]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0, 0, 0 }, ContainerFormat.Tiff)]
    public void Magic_numbers(byte[] head, ContainerFormat expected)
    {
        var path = Write([.. head, .. new byte[52]]);
        try { Assert.Equal(expected, ContentSniffer.Detect(path)?.Format); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Mp4_is_left_to_ffprobe()
    {
        byte[] head = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];
        var path = Write([.. head, .. new byte[52]]);
        try { Assert.Null(ContentSniffer.Detect(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ffprobe_json_parses_rotation_hdr_and_vfr()
    {
        const string json = """
        {"streams":[
          {"codec_type":"video","codec_name":"hevc","width":1920,"height":1080,"pix_fmt":"yuv420p10le",
           "avg_frame_rate":"30000/1001","r_frame_rate":"60/1","color_transfer":"smpte2084","field_order":"progressive",
           "side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}],"disposition":{"attached_pic":0}},
          {"codec_type":"audio","codec_name":"aac","channels":6,"bit_rate":"384000"}],
         "format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"12.5","bit_rate":"8000000","tags":{"major_brand":"qt  "}}}
        """;
        var info = MediaProbe.Parse("x.mov", 12_500_000, json);
        Assert.Equal(MediaKind.Video, info.Kind);
        Assert.Equal(ContainerFormat.Mov, info.Container);
        Assert.Equal(90, info.Rotation);
        Assert.Equal(1080, info.DisplayWidth);
        Assert.True(info.Hdr);
        Assert.True(info.VariableFrameRate);
        Assert.Equal(6, info.AudioChannels);
        Assert.Equal(29.97, info.FrameRate, 2);
    }

    [Fact]
    public void Audio_only_is_rejected()
    {
        const string json = """{"streams":[{"codec_type":"audio","codec_name":"mp3"},{"codec_type":"video","codec_name":"mjpeg","width":500,"height":500,"disposition":{"attached_pic":1}}],"format":{"format_name":"mp3","duration":"200"}}""";
        var ex = Assert.Throws<SquishException>(() => MediaProbe.Parse("x.mp3", 5_000_000, json));
        Assert.Equal(ErrorCode.AudioOnly, ex.Code);
    }

    [Fact]
    public void Missing_duration_is_rejected()
    {
        const string json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":640,"height":360}],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2"}}""";
        var ex = Assert.Throws<SquishException>(() => MediaProbe.Parse("x.mp4", 5_000_000, json));
        Assert.Equal(ErrorCode.NoDuration, ex.Code);
    }
}
