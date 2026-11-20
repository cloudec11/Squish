using System.Text.Json;

namespace Squish.Core;

public enum OutputLocation { BesideSource, FixedFolder }
public enum ThemeChoice { System, Light, Dark }

public sealed record AppSettings
{
    /// <summary>Target in megabytes. First run: the catalogue default (20 MB).</summary>
    public double TargetMegabytes { get; init; } = 20;
    public QualityMode Mode { get; init; } = QualityMode.Best;
    public OutputLocation OutputLocation { get; init; } = OutputLocation.BesideSource;
    public string? OutputFolder { get; init; }
    public OutputFormatPreference ImageFormat { get; init; } = OutputFormatPreference.Auto;
    public CodecPreference VideoCodec { get; init; } = CodecPreference.H264;
    public bool UseHardwareInFastMode { get; init; } = true;
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    // Window placement, in raw pixels.
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
}

public static class AppPaths
{
    public static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Squish");
    public static string Logs => Path.Combine(DataRoot, "logs");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string UserPresetsFile => Path.Combine(DataRoot, "presets.json");
    public static string CapabilitiesCache => Path.Combine(DataRoot, "capabilities.json");
}

public sealed class SettingsStore(string path)
{
    private readonly object _gate = new();

    public string FilePath { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SquishJson.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SquishJson.Default.AppSettings));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}
