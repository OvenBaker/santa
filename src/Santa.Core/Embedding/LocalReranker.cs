using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Santa.Core.Embedding;

/// <summary>
/// BERT-based cross-encoder reranker (e.g. ms-marco-MiniLM-L-12-v2). Scores
/// (query, document) pairs by feeding [CLS] q [SEP] d [SEP] through the model
/// and reading the single classification logit.
/// </summary>
public sealed class LocalReranker : IReranker
{
    private const int CLS = 101;
    private const int SEP = 102;
    private const int PAD = 0;

    private readonly RerankerConfig _cfg;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;

    public string ModelId => _cfg.ModelId;
    public int MaxTokens => _cfg.MaxTokens;

    public LocalReranker(RerankerConfig cfg)
    {
        _cfg = cfg;
        if (!File.Exists(cfg.OnnxPath))
            throw new FileNotFoundException(
                $"Reranker model not found at {cfg.OnnxPath}. Run `santa models download` first.");
        if (!File.Exists(cfg.VocabPath))
            throw new FileNotFoundException($"BERT vocab not found at {cfg.VocabPath}.");

        using var opts = InferenceRuntime.CreateSessionOptions(
            cfg.Provider, cfg.DeviceId, cfg.CudaMemoryLimitBytes);
        _session = new InferenceSession(cfg.OnnxPath, opts);

        using var fs = File.OpenRead(cfg.VocabPath);
        _tokenizer = BertTokenizer.Create(fs, new BertOptions { LowerCaseBeforeTokenization = cfg.LowerCase });
    }

    public float[] Score(string query, IReadOnlyList<string> documents)
    {
        if (documents.Count == 0) return Array.Empty<float>();

        // Encode query once (no special tokens — we add CLS/SEP manually).
        var queryIds = _tokenizer.EncodeToIds(query, addSpecialTokens: false,
            considerPreTokenization: true, considerNormalization: true).ToList();
        // Reserve 3 special slots and a small buffer for query → cap query at maxTokens/4.
        var queryBudget = Math.Min(queryIds.Count, _cfg.MaxTokens / 4);
        if (queryIds.Count > queryBudget) queryIds = queryIds.Take(queryBudget).ToList();
        var docBudget = _cfg.MaxTokens - queryIds.Count - 3; // [CLS] q [SEP] d [SEP]

        var scores = new float[documents.Count];

        var encodedDocuments = new List<int>[documents.Count];
        for (int i = 0; i < documents.Count; i++)
        {
            var docIds = _tokenizer.EncodeToIds(documents[i], addSpecialTokens: false,
                considerPreTokenization: true, considerNormalization: true).ToList();
            if (docIds.Count > docBudget) docIds = docIds.Take(docBudget).ToList();
            encodedDocuments[i] = docIds;
        }

        for (int batchStart = 0; batchStart < documents.Count;)
        {
            var batchEnd = batchStart;
            int maxLen = 0;
            while (batchEnd < documents.Count && batchEnd - batchStart < _cfg.BatchSize)
            {
                var sequenceLength = queryIds.Count + encodedDocuments[batchEnd].Count + 3;
                var candidateMax = Math.Max(maxLen, sequenceLength);
                var candidateSize = batchEnd - batchStart + 1;
                if (candidateSize > 1 && candidateSize * candidateMax > _cfg.MaxBatchTokens)
                    break;
                maxLen = candidateMax;
                batchEnd++;
            }
            if (batchEnd == batchStart)
            {
                batchEnd++;
                maxLen = queryIds.Count + encodedDocuments[batchStart].Count + 3;
            }

            var batchSize = batchEnd - batchStart;

            var seqs = new List<(List<int> Ids, List<int> Types)>(batchSize);
            for (int i = 0; i < batchSize; i++)
            {
                var docIds = encodedDocuments[batchStart + i];

                var ids = new List<int>(_cfg.MaxTokens) { CLS };
                var types = new List<int>(_cfg.MaxTokens) { 0 };
                ids.AddRange(queryIds);    types.AddRange(Enumerable.Repeat(0, queryIds.Count));
                ids.Add(SEP);              types.Add(0);
                ids.AddRange(docIds);      types.AddRange(Enumerable.Repeat(1, docIds.Count));
                ids.Add(SEP);              types.Add(1);

                seqs.Add((ids, types));
            }

            var inputIds  = new long[batchSize * maxLen];
            var attention = new long[batchSize * maxLen];
            var tokenType = new long[batchSize * maxLen];
            for (int i = 0; i < batchSize; i++)
            {
                var (ids, types) = seqs[i];
                for (int j = 0; j < ids.Count; j++)
                {
                    inputIds[i * maxLen + j] = ids[j];
                    attention[i * maxLen + j] = 1;
                    tokenType[i * maxLen + j] = types[j];
                }
                // padding stays at 0
            }

            var shape = new[] { batchSize, maxLen };
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, shape)),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attention, shape)),
                NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenType, shape)),
            };

            using var outputs = _session.Run(inputs);
            var logits = outputs.First().AsTensor<float>(); // [batch, 1] for SequenceClassification

            for (int i = 0; i < batchSize; i++)
                scores[batchStart + i] = logits[i, 0];

            batchStart = batchEnd;
        }

        return scores;
    }

    public void Dispose() => _session.Dispose();
}
