using Santa.Core.Embedding;
using Spectre.Console;

namespace Santa.Cli;

internal static class InferenceProviderOption
{
    public static bool TryResolve(
        string? value,
        bool keywordOnlyAlias,
        out InferenceProvider? provider)
    {
        var normalized = (value ?? "cuda").Trim().ToLowerInvariant();

        if (keywordOnlyAlias)
        {
            provider = null;
            return true;
        }

        provider = normalized switch
        {
            "cpu" => InferenceProvider.Cpu,
            "cuda" or "gpu" => InferenceProvider.Cuda,
            "keyword-only" or "keyword" or "bm25" => null,
            _ => null,
        };

        if (provider is not null || normalized is "keyword-only" or "keyword" or "bm25")
            return true;

        AnsiConsole.MarkupLineInterpolated(
            $"[red]Unknown inference provider:[/] {Markup.Escape(value ?? "")}. Use cpu, cuda, or keyword-only.");
        return false;
    }

    public static EmbedderConfig Apply(EmbedderConfig config, InferenceProvider provider, int deviceId)
        => config with
        {
            Provider = provider,
            DeviceId = deviceId,
            // A fixed document count is unsafe for transformers: one long item pads the entire
            // batch and attention grows quadratically. Keep at most one full 2,048-token input
            // in flight on CUDA; short chunks can still share a batch.
            MaxBatchTokens = provider == InferenceProvider.Cuda
                ? Math.Min(config.MaxBatchTokens, config.MaxTokens)
                : config.MaxBatchTokens,
        };

    public static RerankerConfig Apply(RerankerConfig config, InferenceProvider provider, int deviceId)
        => config with
        {
            Provider = provider,
            DeviceId = deviceId,
            MaxBatchTokens = provider == InferenceProvider.Cuda
                ? Math.Min(config.MaxBatchTokens, 2_048)
                : config.MaxBatchTokens,
        };

    public static int ReportInitializationFailure(InferenceProvider provider, Exception ex)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[red]{provider.ToString().ToLowerInvariant()} inference failed:[/] {Markup.Escape(ex.Message)}");
        return 3;
    }

    public static bool TryAcquireGpuCourtesy(InferenceProvider? provider, int deviceId,
        out GpuCourtesyLease? lease)
    {
        lease = null;
        if (provider != InferenceProvider.Cuda) return true;
        var acquisition = new GpuCourtesyGate(deviceId).TryAcquireAsync().GetAwaiter().GetResult();
        lease = acquisition.Lease;
        if (lease is not null) return true;
        AnsiConsole.MarkupLineInterpolated($"[yellow]deferred:[/] {Markup.Escape(acquisition.Reason)}");
        return false;
    }
}
