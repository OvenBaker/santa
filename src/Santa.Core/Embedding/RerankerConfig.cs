namespace Santa.Core.Embedding;

public sealed record RerankerConfig(
    string ModelId,
    int MaxTokens,
    string OnnxPath,
    string VocabPath,
    bool LowerCase = true,
    InferenceProvider Provider = InferenceProvider.Cpu,
    int DeviceId = 0,
    int BatchSize = 16,
    int MaxBatchTokens = 8_192,
    long CudaMemoryLimitBytes = 2L * 1024 * 1024 * 1024)
{
    public static RerankerConfig MsMarcoMiniLmL12(string root) => new(
        ModelId: "Xenova/ms-marco-MiniLM-L-12-v2",
        MaxTokens: 512,
        OnnxPath: Path.Combine(root, "models", "ms-marco-MiniLM-L-12-v2", "model.onnx"),
        VocabPath: Path.Combine(root, "models", "ms-marco-MiniLM-L-12-v2", "vocab.txt"));
}
