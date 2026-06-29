namespace Santa.Core.Embedding;

public sealed record RerankerConfig(
    string ModelId,
    int MaxTokens,
    string OnnxPath,
    string VocabPath,
    bool LowerCase = true,
    int DeviceId = 0,
    int BatchSize = 16)
{
    public static RerankerConfig MsMarcoMiniLmL12(string root) => new(
        ModelId: "Xenova/ms-marco-MiniLM-L-12-v2",
        MaxTokens: 512,
        OnnxPath: Path.Combine(root, "models", "ms-marco-MiniLM-L-12-v2", "model.onnx"),
        VocabPath: Path.Combine(root, "models", "ms-marco-MiniLM-L-12-v2", "vocab.txt"));
}
