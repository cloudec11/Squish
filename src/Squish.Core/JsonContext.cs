using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squish.Core;

/// <summary>
/// Source-generated JSON for settings, presets and the capability cache, so a trimmed app build
/// (where reflection-based serialization is off) still reads and writes them.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PresetCatalog))]
[JsonSerializable(typeof(CapabilityCacheEntry))]
internal sealed partial class SquishJson : JsonSerializerContext;

internal sealed record CapabilityCacheEntry(string Key, string[] Encoders, string? HardwareH264);
