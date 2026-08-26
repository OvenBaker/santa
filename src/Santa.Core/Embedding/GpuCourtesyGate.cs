using System.Diagnostics;
using System.Globalization;

namespace Santa.Core.Embedding;

public sealed record GpuCourtesySnapshot(bool Available, int UtilizationPercent, long UsedMemoryMiB,
    long TotalMemoryMiB, string? Error = null)
{
    public double UsedFraction => TotalMemoryMiB <= 0 ? 0 : (double)UsedMemoryMiB / TotalMemoryMiB;
}

/// <summary>
/// Serializes Santa and Shepherd inference and only grants CUDA work after nvidia-smi reports an idle GPU.
/// A durable lock file is harmless; the byte-range lock is the live lease and is released on process exit.
/// </summary>
public sealed class GpuCourtesyGate
{
    private readonly int _deviceId;
    private readonly int _maximumIdleUtilizationPercent;
    private readonly double _maximumIdleMemoryFraction;
    private readonly int _samples;
    private readonly TimeSpan _sampleInterval;

    public GpuCourtesyGate(int deviceId = 0, int maximumIdleUtilizationPercent = 10,
        double maximumIdleMemoryFraction = 0.25, int samples = 3, TimeSpan? sampleInterval = null)
    {
        _deviceId = deviceId;
        _maximumIdleUtilizationPercent = maximumIdleUtilizationPercent;
        _maximumIdleMemoryFraction = maximumIdleMemoryFraction;
        _samples = samples;
        _sampleInterval = sampleInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<(GpuCourtesyLease? Lease, string Reason)> TryAcquireAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return (null, "GPU courtesy gate currently supports Linux/WSL only");
        Directory.CreateDirectory(Path.GetDirectoryName(SantaPaths.SharedGpuLockPath)!);
        var stream = new FileStream(SantaPaths.SharedGpuLockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            stream.Lock(0, 1);
        }
        catch (IOException)
        {
            stream.Dispose();
            return (null, "another Santa/Shepherd GPU inference process holds the courtesy lock");
        }

        try
        {
            GpuCourtesySnapshot? latest = null;
            var consecutiveIdle = 0;
            var maximumAttempts = _samples * 4;
            for (var index = 0; index < maximumAttempts; index++)
            {
                latest = await ReadSnapshotAsync(cancellationToken);
                if (!latest.Available)
                {
                    Release(stream);
                    return (null, $"nvidia-smi unavailable: {latest.Error}");
                }
                var idle = latest.UtilizationPercent <= _maximumIdleUtilizationPercent &&
                    latest.UsedFraction <= _maximumIdleMemoryFraction;
                consecutiveIdle = idle ? consecutiveIdle + 1 : 0;
                if (consecutiveIdle >= _samples) return (new GpuCourtesyLease(stream), "GPU idle");
                if (index + 1 < maximumAttempts) await Task.Delay(_sampleInterval, cancellationToken);
            }
            Release(stream);
            return (null, $"GPU busy: {latest!.UtilizationPercent}% utilization, " +
                $"{latest.UsedMemoryMiB}/{latest.TotalMemoryMiB} MiB in use");
        }
        catch
        {
            Release(stream);
            throw;
        }
    }

    private async Task<GpuCourtesySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("nvidia-smi")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(_deviceId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--query-gpu=utilization.gpu,memory.used,memory.total");
        start.ArgumentList.Add("--format=csv,noheader,nounits");
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new GpuCourtesySnapshot(false, 0, 0, 0, "process did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var line = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (process.ExitCode != 0 || line is null)
                return new GpuCourtesySnapshot(false, 0, 0, 0, (await error).Trim());
            var cells = line.Split(',', StringSplitOptions.TrimEntries);
            if (cells.Length != 3 || !int.TryParse(cells[0], CultureInfo.InvariantCulture, out var utilization) ||
                !long.TryParse(cells[1], CultureInfo.InvariantCulture, out var used) ||
                !long.TryParse(cells[2], CultureInfo.InvariantCulture, out var total))
                return new GpuCourtesySnapshot(false, 0, 0, 0, $"unexpected output: {line}");
            return new GpuCourtesySnapshot(true, utilization, used, total);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new GpuCourtesySnapshot(false, 0, 0, 0, error.Message);
        }
    }

    private static void Release(FileStream stream)
    {
        if (OperatingSystem.IsLinux())
        {
            try { stream.Unlock(0, 1); } catch { }
        }
        stream.Dispose();
    }
}

public sealed class GpuCourtesyLease : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;
    internal GpuCourtesyLease(FileStream stream) => _stream = stream;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (OperatingSystem.IsLinux())
        {
            try { _stream.Unlock(0, 1); } catch { }
        }
        _stream.Dispose();
    }
}
