using System.Net.Http.Headers;

namespace Santa.Core.Embedding;

public static class ModelDownloader
{
    private const string HfBase = "https://huggingface.co";

    /// <summary>
    /// Downloads Nomic Embed Text v1.5 ONNX (FP32 ~500MB) + vocab.txt into the configured model dir.
    /// </summary>
    public static Task DownloadNomicV15Async(EmbedderConfig cfg, Action<string>? log = null, CancellationToken ct = default)
        => DownloadAssetsAsync(new[]
        {
            ($"{HfBase}/nomic-ai/nomic-embed-text-v1.5/resolve/main/onnx/model.onnx", cfg.OnnxPath),
            ($"{HfBase}/nomic-ai/nomic-embed-text-v1.5/resolve/main/vocab.txt", cfg.VocabPath),
        }, log, ct);

    /// <summary>
    /// Downloads ms-marco-MiniLM-L-12-v2 (BERT cross-encoder, ~120MB) + vocab.txt.
    /// </summary>
    public static Task DownloadMsMarcoRerankerAsync(RerankerConfig cfg, Action<string>? log = null, CancellationToken ct = default)
        => DownloadAssetsAsync(new[]
        {
            ($"{HfBase}/Xenova/ms-marco-MiniLM-L-12-v2/resolve/main/onnx/model.onnx", cfg.OnnxPath),
            ($"{HfBase}/Xenova/ms-marco-MiniLM-L-12-v2/resolve/main/vocab.txt", cfg.VocabPath),
        }, log, ct);

    private static async Task DownloadAssetsAsync((string Url, string Path)[] assets, Action<string>? log, CancellationToken ct)
    {
        log ??= _ => { };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("santa", "0.1"));

        foreach (var (url, dest) in assets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                log($"  ✓ {Path.GetFileName(dest)} ({FormatSize(new FileInfo(dest).Length)})");
                continue;
            }
            log($"  ↓ {url}");
            await DownloadAsync(http, url, dest, log, ct);
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string dest, Action<string> log, CancellationToken ct)
    {
        var tmp = dest + ".part";
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using (var fs = File.Create(tmp))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buf = new byte[81920];
            long copied = 0;
            int read;
            var lastReport = DateTime.UtcNow;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read), ct);
                copied += read;
                if ((DateTime.UtcNow - lastReport).TotalSeconds >= 2)
                {
                    var pct = total > 0 ? $" ({100.0 * copied / total:F1}%)" : "";
                    log($"    {FormatSize(copied)}{pct}");
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        File.Move(tmp, dest, overwrite: true);
        log($"    saved {Path.GetFileName(dest)} ({FormatSize(new FileInfo(dest).Length)})");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
