using Microsoft.ML.OnnxRuntime;

namespace Santa.Core.Embedding;

public enum InferenceProvider
{
    Cpu,
    Cuda,
}

/// <summary>
/// Creates ONNX Runtime sessions without probing CUDA unless CUDA was explicitly selected.
/// The normal build contains CPU ONNX Runtime only; opt into the GPU package with
/// -p:SantaOnnxRuntimeFlavor=gpu.
/// </summary>
public static class InferenceRuntime
{
    public static string BuildFlavor
    {
        get
        {
#if SANTA_CUDA
            return "gpu";
#else
            return "cpu";
#endif
        }
    }

    public static SessionOptions CreateSessionOptions(
        InferenceProvider provider,
        int deviceId = 0,
        long cudaMemoryLimitBytes = 7L * 1024 * 1024 * 1024)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Shape-related nodes may legitimately remain on CPU in a CUDA session.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        if (provider == InferenceProvider.Cpu)
            return opts;

        try
        {
#if SANTA_CUDA
            EnsureCudaHostAccess();
            var providers = OrtEnv.Instance().GetAvailableProviders();
            if (!providers.Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This ONNX Runtime installation does not expose CUDAExecutionProvider.");
            if (cudaMemoryLimitBytes <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(cudaMemoryLimitBytes), "CUDA memory limit must be positive.");

            using var cuda = new OrtCUDAProviderOptions();
            cuda.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["gpu_mem_limit"] = cudaMemoryLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // Avoid power-of-two arena growth reserving substantially more VRAM than requested.
                ["arena_extend_strategy"] = "kSameAsRequested",
                // Avoid exhaustive cuDNN workspace searches in an unattended indexing job.
                ["cudnn_conv_algo_search"] = "DEFAULT",
                ["do_copy_in_default_stream"] = "1",
            });
            opts.AppendExecutionProvider_CUDA(cuda);
            return opts;
#else
            throw new InvalidOperationException(
                "This is Santa's CPU build. Build with -p:SantaOnnxRuntimeFlavor=gpu to use --provider cuda.");
#endif
        }
        catch
        {
            opts.Dispose();
            throw;
        }
    }

    private static void EnsureCudaHostAccess()
    {
        if (!OperatingSystem.IsLinux()) return;

        var release = File.Exists("/proc/sys/kernel/osrelease")
            ? File.ReadAllText("/proc/sys/kernel/osrelease")
            : "";
        if (release.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
            && !File.Exists("/dev/dxg"))
        {
            throw new InvalidOperationException(
                "CUDA is unavailable to this process: /dev/dxg is missing. " +
                "A sandbox may be hiding the WSL GPU bridge; otherwise repair WSL GPU access. " +
                "Use --provider cpu when GPU access is intentionally restricted.");
        }
    }
}
