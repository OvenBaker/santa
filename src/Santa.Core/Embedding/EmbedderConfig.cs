namespace Santa.Core.Embedding;

public sealed record EmbedderConfig(
    string ModelId,                  // e.g. "nomic-ai/nomic-embed-text-v1.5"
    int Dimensions,                  // 768 for Nomic v1.5
    int MaxTokens,                   // 8192 for Nomic v1.5
    string OnnxPath,                 // <santa-home>/models/<id>/model.onnx
    string VocabPath,                // <santa-home>/models/<id>/vocab.txt (BERT WordPiece)
    bool LowerCase = true,           // bert-base-uncased style
    InferenceProvider Provider = InferenceProvider.Cpu,
    int DeviceId = 0,                // CUDA device when Provider is Cuda
    int BatchSize = 16,
    int MaxBatchTokens = 16_384,     // bounds padding-amplified attention work
    long CudaMemoryLimitBytes = 7L * 1024 * 1024 * 1024,
    string DocumentPrefix = "search_document: ",
    string QueryPrefix = "search_query: ")
{
    public static EmbedderConfig NomicV15(string root) => new(
        ModelId: "nomic-ai/nomic-embed-text-v1.5",
        Dimensions: 768,
        // Truncate at 2048 tokens. Nomic supports 8192 but standard attention is O(n²);
        // at batch 16 × 8192 the attention matrix blows past 12 GB of VRAM. Our chunks
        // cap around ~1800 BERT tokens anyway after the prefix is added.
        MaxTokens: 2048,
        OnnxPath: Path.Combine(root, "models", "nomic-embed-text-v1.5", "model.onnx"),
        VocabPath: Path.Combine(root, "models", "nomic-embed-text-v1.5", "vocab.txt"),
        BatchSize: 8,
        MaxBatchTokens: 16_384);

    public static string DefaultRoot => SantaPaths.Home;
}
