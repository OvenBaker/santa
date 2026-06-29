using Microsoft.ML.OnnxRuntime;

namespace Santa.Core.Embedding;

public sealed record GpuInfoReport(
    string OrtVersion,
    IReadOnlyList<string> AvailableProviders,
    bool CudaAvailable,
    int? PinnedDeviceId,
    string? CudaError);

public static class GpuInfo
{
    public static GpuInfoReport Probe(int deviceId = 0)
    {
        var env = OrtEnv.Instance();
        var providers = OrtEnv.Instance().GetAvailableProviders().ToList();
        var hasCuda = providers.Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase));

        string? err = null;
        if (hasCuda)
        {
            try
            {
                using var opts = new SessionOptions();
                opts.AppendExecutionProvider_CUDA(deviceId);
            }
            catch (Exception ex) { err = ex.Message; }
        }
        return new GpuInfoReport(
            OrtVersion: typeof(OrtEnv).Assembly.GetName().Version?.ToString() ?? "unknown",
            AvailableProviders: providers,
            CudaAvailable: hasCuda && err is null,
            PinnedDeviceId: hasCuda ? deviceId : null,
            CudaError: err);
    }
}
