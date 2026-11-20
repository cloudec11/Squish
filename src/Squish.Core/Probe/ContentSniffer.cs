using System.Buffers.Binary;
using System.Text;

namespace Squish.Core.Probe;

/// <summary>Identifies image formats from their first bytes. Anything not recognised goes to ffprobe.</summary>
internal static class ContentSniffer
{
    public sealed record Sniff(ContainerFormat Format, bool Animated = false, bool Alpha = false);

    // Not a u8 string literal: "\x89" there would encode as two UTF-8 bytes.
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Sniff? Detect(string path)
    {
        Span<byte> head = stackalloc byte[64];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..n];
        if (n < 12) return null;

        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return new(ContainerFormat.Jpeg);
        if (head.StartsWith(PngSignature)) return SniffPng(path);
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return new(ContainerFormat.Gif);
        if (head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return SniffWebP(head);
        if (head.StartsWith("BM"u8)) return new(ContainerFormat.Bmp);
        if (head.StartsWith("FUJIFILMCCD-RAW"u8)) return new(ContainerFormat.Raw);
        if (head.StartsWith("IIRO"u8) || head.StartsWith("IIU\0"u8)) return new(ContainerFormat.Raw);
        if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8))
            return new(LooksLikeRaw(head) ? ContainerFormat.Raw : ContainerFormat.Tiff);

        if (head[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(head[8..12]);
            switch (brand)
            {
                case "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1":
                    return new(ContainerFormat.Heif);
                case "avif" or "avis":
                    return new(ContainerFormat.Avif, Animated: brand == "avis");
                case "crx ":
                    return new(ContainerFormat.Raw);
            }
        }
        return null;
    }

    // Canon CR2 has "CR" at byte 8. Others (NEF, ARW, DNG) are TIFF-shaped; Magick handles both.
    private static bool LooksLikeRaw(ReadOnlySpan<byte> head) => head.Length > 10 && head[8] == 'C' && head[9] == 'R';

    private static Sniff SniffWebP(ReadOnlySpan<byte> head)
    {
        var chunk = head[12..16];
        if (chunk.SequenceEqual("VP8X"u8) && head.Length > 20)
        {
            byte flags = head[20];
            return new(ContainerFormat.WebP, Animated: (flags & 0x02) != 0, Alpha: (flags & 0x10) != 0);
        }
        if (chunk.SequenceEqual("VP8L"u8) && head.Length > 24)
        {
            // Alpha hint bit is bit 28 of the 32-bit header after the 0x2F signature.
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..25]);
            return new(ContainerFormat.WebP, Alpha: ((bits >> 28) & 1) != 0);
        }
        return new(ContainerFormat.WebP);
    }

    /// <summary>Reads IHDR colour type and looks for acTL (APNG) and tRNS before the first IDAT.</summary>
    private static Sniff SniffPng(string path)
    {
        bool alpha = false, animated = false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = 8;
            Span<byte> hdr = stackalloc byte[8];
            Span<byte> ihdr = stackalloc byte[13];
            for (int i = 0; i < 64; i++)
            {
                if (fs.ReadAtLeast(hdr, 8, throwOnEndOfStream: false) < 8) break;
                uint len = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
                var type = Encoding.ASCII.GetString(hdr[4..8]);
                if (type == "IHDR")
                {
                    fs.ReadExactly(ihdr);
                    byte colourType = ihdr[9];
                    alpha = colourType is 4 or 6;
                    fs.Position += 4; // CRC
                    continue;
                }
                if (type == "acTL") animated = true;
                if (type == "tRNS") alpha = true;
                if (type == "IDAT") break;
                fs.Position += len + 4;
            }
        }
        catch (IOException) { }
        return new(ContainerFormat.Png, animated, alpha);
    }
}
