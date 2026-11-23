#:package Magick.NET-Q8-AnyCPU@15.1.0
// Renders the Squish app icon (a rounded square with a squashed "S" blob) to .ico and .png.
//   dotnet run scripts/make-icon.cs -- <output folder>
using ImageMagick;
using ImageMagick.Drawing;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

MagickImage Render(uint size)
{
    var img = new MagickImage(MagickColors.Transparent, size, size);
    double s = size / 256.0;
    new Drawables()
        // Background tile: Discord-ish blurple, but our own shade.
        .FillColor(new MagickColor("#5B5BD6")).StrokeColor(MagickColors.Transparent)
        .RoundRectangle(8 * s, 8 * s, 248 * s, 248 * s, 56 * s, 56 * s)
        // Squashed ellipse: the "squish".
        .FillColor(MagickColors.White)
        .Ellipse(128 * s, 150 * s, 84 * s, 46 * s, 0, 360)
        // Down arrow pressing on it.
        .Polygon(new PointD(128 * s, 112 * s), new PointD(92 * s, 70 * s), new PointD(114 * s, 70 * s),
                 new PointD(114 * s, 36 * s), new PointD(142 * s, 36 * s), new PointD(142 * s, 70 * s), new PointD(164 * s, 70 * s))
        .Draw(img);
    return img;
}

using (var col = new MagickImageCollection())
{
    foreach (var size in new uint[] { 256, 64, 48, 32, 24, 16 }) col.Add(Render(size));
    col.Write(Path.Combine(outDir, "Squish.ico"), MagickFormat.Ico);
}
using (var png = Render(32)) png.Write(Path.Combine(outDir, "Squish-32.png"));
using (var png = Render(256)) png.Write(Path.Combine(outDir, "Squish-256.png"));
Console.WriteLine($"Icons written to {Path.GetFullPath(outDir)}");

// Optional second folder: web app icons (PWA manifest and iOS home screen).
if (args.Length > 1)
{
    var webDir = args[1];
    Directory.CreateDirectory(webDir);
    using (var p = Render(192)) p.Write(Path.Combine(webDir, "icon-192.png"));
    using (var p = Render(512)) p.Write(Path.Combine(webDir, "icon-512.png"));
    // iOS ignores transparency on home screen icons; give it a full square tile.
    using (var p = Render(180)) { p.BackgroundColor = new MagickColor("#5B5BD6"); p.Alpha(AlphaAction.Remove); p.Write(Path.Combine(webDir, "apple-touch-icon.png")); }
    Console.WriteLine($"Web icons written to {Path.GetFullPath(webDir)}");
}
