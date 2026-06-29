using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Santa.Core.Classify;

public enum ClassificationMode { Transcript, Agent }

public sealed class ClassificationRecipe
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public ClassificationMode Mode { get; set; } = ClassificationMode.Transcript;
    public string Model { get; set; } = "haiku";   // haiku | sonnet | opus
    public List<string> AllowedTools { get; set; } = new();
    public string Prompt { get; set; } = "";
    public Dictionary<string, string> StatusMap { get; set; } = new();
    public int? TranscriptMaxTokens { get; set; }   // null = full
    public string TranscriptMode { get; set; } = "tail"; // head | tail | full

    public static ClassificationRecipe LoadFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        return Load(yaml);
    }

    public static ClassificationRecipe Load(string yaml)
    {
        var deser = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var r = deser.Deserialize<ClassificationRecipe>(yaml);
        if (string.IsNullOrEmpty(r.Id)) throw new InvalidOperationException("recipe missing id");
        if (string.IsNullOrEmpty(r.Prompt)) throw new InvalidOperationException("recipe missing prompt");
        return r;
    }
}

public sealed record ClassificationResult(
    string SessionId,
    string RecipeId,
    DateTimeOffset Ts,
    string? Status,
    string? Evidence,
    string Model,
    string RawJson);
