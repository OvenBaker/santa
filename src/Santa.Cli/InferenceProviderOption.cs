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

    /// <summary>
    /// Refuse a capability that genuinely cannot run without CUDA, VISIBLY. Commands like `related` are
    /// vector-only, so there is nothing honest to degrade to — but they are launched from cockpit's
    /// display-popup, which closes the instant the process exits, so a printed line alone is a flash the
    /// operator cannot read. Hold for a keypress when a terminal is actually attached; stay silent and
    /// non-zero when the caller is capturing output (--porcelain), where a prompt would hang the pipeline.
    /// </summary>
    public static int ReportGpuRequired(string capability, string reason)
    {
        // STDERR, deliberately: stdout is the data channel (--porcelain), so a diagnostic written there is
        // both invisible to the caller and corrupting to whatever parses the rows.
        Console.Error.WriteLine($"{capability} requires CUDA, and the GPU is presently unavailable: {reason}");
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            Console.Error.Write("press any key to exit");
            try { Console.ReadKey(intercept: true); } catch (InvalidOperationException) { /* no console */ }
            Console.Error.WriteLine();
        }
        return 4;
    }

    public static bool TryAcquireGpuCourtesy(InferenceProvider? provider, int deviceId,
        out GpuCourtesyLease? lease)
        => TryAcquireGpuCourtesy(provider, deviceId, out lease, out _, announce: true);

    /// <summary>As above, but hands the caller the deferral reason so it can refuse in its own words.</summary>
    public static bool TryAcquireGpuCourtesy(InferenceProvider? provider, int deviceId,
        out GpuCourtesyLease? lease, out string reason, bool announce = true)
    {
        lease = null;
        reason = string.Empty;
        if (provider != InferenceProvider.Cuda) return true;
        var acquisition = new GpuCourtesyGate(deviceId).TryAcquireAsync().GetAwaiter().GetResult();
        lease = acquisition.Lease;
        if (lease is not null) return true;
        reason = acquisition.Reason;
        if (announce) AnsiConsole.MarkupLineInterpolated($"[yellow]deferred:[/] {Markup.Escape(acquisition.Reason)}");
        return false;
    }
}
