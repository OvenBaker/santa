using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Santa.Core.Embedding;

/// <summary>
/// ONNX Runtime CUDA EP embedder. Pinned to a single device id (default 0 — RTX 4080 under WSL).
/// Tokenizes with the model's bundled tokenizer.json, runs forward, mean-pools, L2-normalises.
/// </summary>
public sealed class LocalEmbedder : IEmbedder
{
    private readonly EmbedderConfig _cfg;
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;

    public int Dimensions => _cfg.Dimensions;
    public string ModelId => _cfg.ModelId;
    public int MaxTokens => _cfg.MaxTokens;

    public LocalEmbedder(EmbedderConfig cfg)
    {
        _cfg = cfg;
        if (!File.Exists(cfg.OnnxPath))
            throw new FileNotFoundException(
                $"Embedding model not found at {cfg.OnnxPath}. Run `santa models download` first.");
        if (!File.Exists(cfg.VocabPath))
            throw new FileNotFoundException($"BERT vocab not found at {cfg.VocabPath}.");

        var opts = new SessionOptions();
        opts.AppendExecutionProvider_CUDA(cfg.DeviceId);
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // Suppress the harmless "Some nodes were not assigned to the preferred EP" warning —
        // shape-related ops always run on CPU; the warning fires once per session and is just noise.
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;

        _session = new InferenceSession(cfg.OnnxPath, opts);

        using var vocabFs = File.OpenRead(cfg.VocabPath);
        _tokenizer = BertTokenizer.Create(vocabFs, new BertOptions
        {
            LowerCaseBeforeTokenization = cfg.LowerCase,
        });
    }

    public float[] Embed(string text, EmbedKind kind = EmbedKind.Document)
        => EmbedBatch(new[] { text }, kind)[0];

    public float[][] EmbedBatch(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Document)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var prefix = kind == EmbedKind.Query ? _cfg.QueryPrefix : _cfg.DocumentPrefix;

        var results = new float[texts.Count][];
        for (int batchStart = 0; batchStart < texts.Count; batchStart += _cfg.BatchSize)
        {
            var batchEnd = Math.Min(batchStart + _cfg.BatchSize, texts.Count);
            var batchSize = batchEnd - batchStart;

            var encoded = new EncodeResults[batchSize];
            int maxLen = 0;
            for (int i = 0; i < batchSize; i++)
            {
                var ids = _tokenizer.EncodeToIds(prefix + texts[batchStart + i]);
                var truncated = ids.Take(_cfg.MaxTokens).ToArray();
                encoded[i] = new EncodeResults(truncated);
                if (truncated.Length > maxLen) maxLen = truncated.Length;
            }
            if (maxLen == 0) maxLen = 1;

            var inputIds = new long[batchSize * maxLen];
            var attention = new long[batchSize * maxLen];
            var tokenType = new long[batchSize * maxLen];
            for (int i = 0; i < batchSize; i++)
            {
                var ids = encoded[i].Ids;
                for (int j = 0; j < ids.Length; j++)
                {
                    inputIds[i * maxLen + j] = ids[j];
                    attention[i * maxLen + j] = 1;
                }
                // remaining slots stay 0 (PAD)
            }

            var shape = new[] { batchSize, maxLen };
            var idsT = new DenseTensor<long>(inputIds, shape);
            var maskT = new DenseTensor<long>(attention, shape);
            var typeT = new DenseTensor<long>(tokenType, shape);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", idsT),
                NamedOnnxValue.CreateFromTensor("attention_mask", maskT),
                NamedOnnxValue.CreateFromTensor("token_type_ids", typeT),
            };

            using var outputs = _session.Run(inputs);
            var hidden = outputs.First().AsTensor<float>(); // [batch, seq, dim]

            for (int i = 0; i < batchSize; i++)
            {
                var pooled = MeanPool(hidden, i, encoded[i].Ids.Length, _cfg.Dimensions, maxLen);
                Normalize(pooled);
                results[batchStart + i] = pooled;
            }
        }
        return results;
    }

    private static float[] MeanPool(Tensor<float> hidden, int b, int validLen, int dim, int seqLen)
    {
        var pooled = new float[dim];
        for (int t = 0; t < validLen; t++)
            for (int d = 0; d < dim; d++)
                pooled[d] += hidden[b, t, d];
        var inv = validLen == 0 ? 0f : 1f / validLen;
        for (int d = 0; d < dim; d++) pooled[d] *= inv;
        return pooled;
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var n = (float)Math.Sqrt(sum);
        if (n < 1e-12f) return;
        var inv = 1f / n;
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose() => _session.Dispose();

    private readonly record struct EncodeResults(int[] Ids);
}
