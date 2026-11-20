using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squish.Core;

public sealed record SizePreset(string Id, string Label, double Megabytes, string Caption);

/// <summary>Size presets loaded from presets.json so a Discord limit change needs no rebuild.</summary>
public sealed record PresetCatalog
{
    public long BytesPerMegabyte { get; init; } = 1_000_000;
    public string DefaultPresetId { get; init; } = "free";
    public IReadOnlyList<SizePreset> Presets { get; init; } = [];
    public double CustomMinMegabytes { get; init; } = 1;
    public double CustomMaxMegabytes { get; init; } = 2000;

    public long ToBytes(double megabytes) => (long)Math.Floor(megabytes * BytesPerMegabyte);

    public SizePreset Default => Presets.FirstOrDefault(p => p.Id == DefaultPresetId) ?? Presets[0];

    public static PresetCatalog Builtin { get; } = new()
    {
        Presets =
        [
            new("safe", "10", 10, "Safe for older clients"),
            new("free", "20", 20, "Free limit"),
            new("basic", "50", 50, "Nitro Basic"),
            new("boosted", "100", 100, "Level 3 boosted server"),
            new("nitro", "500", 500, "Nitro"),
        ],
    };


    /// <summary>Loads presets: user override file first, then the shipped file, then built-ins.</summary>
    public static PresetCatalog Load(params string?[] candidatePaths)
    {
        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try
            {
                var cat = JsonSerializer.Deserialize(File.ReadAllText(path), SquishJson.Default.PresetCatalog);
                if (cat is { Presets.Count: > 0 } && cat.BytesPerMegabyte > 0) return cat;
            }
            catch (JsonException) { /* fall through to the next candidate */ }
        }
        return Builtin;
    }

    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "presets.json");
}
