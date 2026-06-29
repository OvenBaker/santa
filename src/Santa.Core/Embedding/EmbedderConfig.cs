namespace Santa.Core.Embedding;

public sealed record EmbedderConfig(
    string ModelId,                  // e.g. "nomic-ai/nomic-embed-text-v1.5"
    int Dimensions,                  // 768 for Nomic v1.5
    int MaxTokens,                   // 8192 for Nomic v1.5
    string OnnxPath,                 // <santa-home>/models/<id>/model.onnx
    string VocabPath,                // <santa-home>/models/<id>/vocab.txt (BERT WordPiece)
    bool LowerCase = true,           // bert-base-uncased style
    int DeviceId = 0,                // CUDA device — pinned to RTX 4080 in WSL
    int BatchSize = 16,
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
        BatchSize: 8);

    public static string DefaultRoot => SantaPaths.Home;
}
