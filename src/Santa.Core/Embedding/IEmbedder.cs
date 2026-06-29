namespace Santa.Core.Embedding;

public enum EmbedKind { Document, Query }

public interface IEmbedder : IDisposable
{
    int Dimensions { get; }
    string ModelId { get; }
    int MaxTokens { get; }

    float[] Embed(string text, EmbedKind kind = EmbedKind.Document);
    float[][] EmbedBatch(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Document);
}
