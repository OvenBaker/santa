namespace Santa.Core.Embedding;

public interface IReranker : IDisposable
{
    string ModelId { get; }
    int MaxTokens { get; }

    /// <summary>
    /// Cross-encoder scoring. Returns one scalar relevance logit per document; higher is better.
    /// Pairs longer than the model's max length are truncated on the document side.
    /// </summary>
    float[] Score(string query, IReadOnlyList<string> documents);
}
