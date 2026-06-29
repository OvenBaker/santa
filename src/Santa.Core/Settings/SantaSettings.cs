using System.Text.Json;
using System.Text.Json.Serialization;

namespace Santa.Core.Settings;

public sealed class SantaSettings
{
    public string Theme { get; set; } = "slate-amber";
    /// <summary>"stacked" (list above detail) or "side-by-side" (list left, detail right).</summary>
    public string LayoutMode { get; set; } = "stacked";

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(SantaPaths.Home, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static SantaSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new SantaSettings();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<SantaSettings>(json, JsonOpts) ?? new SantaSettings();
        }
        catch { return new SantaSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }
}
