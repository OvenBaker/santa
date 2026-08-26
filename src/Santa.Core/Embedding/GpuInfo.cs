using Microsoft.ML.OnnxRuntime;

namespace Santa.Core.Embedding;

public sealed record GpuInfoReport(
    string OrtVersion,
    string BuildFlavor,
    InferenceProvider SelectedProvider,
    IReadOnlyList<string> AvailableProviders,
    bool ProviderAvailable,
    int? PinnedDeviceId,
    string? ProviderError);

public static class GpuInfo
{
    public static GpuInfoReport Probe(InferenceProvider provider, int deviceId = 0)
    {
        var providers = OrtEnv.Instance().GetAvailableProviders().ToList();

        string? err = null;
        try
        {
            using var opts = InferenceRuntime.CreateSessionOptions(provider, deviceId);
        }
        catch (Exception ex) { err = ex.Message; }

        return new GpuInfoReport(
            OrtVersion: typeof(OrtEnv).Assembly.GetName().Version?.ToString() ?? "unknown",
            BuildFlavor: InferenceRuntime.BuildFlavor,
            SelectedProvider: provider,
            AvailableProviders: providers,
            ProviderAvailable: err is null,
            PinnedDeviceId: provider == InferenceProvider.Cuda ? deviceId : null,
            ProviderError: err);
    }
}
