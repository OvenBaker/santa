using System.Formats.Tar;
using System.IO.Compression;
using Santa.Core;
using Microsoft.Data.Sqlite;

namespace Santa.Core.Storage;

/// <summary>
/// Locates (and on first use, downloads) the sqlite-vec loadable extension and attaches it
/// to a SqliteConnection. Distributed by asg017/sqlite-vec on GitHub releases as a tar.gz
/// containing vec0.so for the target platform.
/// </summary>
public static class VecExtension
{
    private const string DefaultVersion = "v0.1.6";   // pin a known-good build
    private const string DefaultPlatform = "linux-x86_64";

    public static string DefaultLibPath => SantaPaths.VecLibPath;

    public static bool IsInstalled(string? path = null)
    {
        path ??= DefaultLibPath;
        // SQLite loads with implicit extension — we pass the filename without .so on linux,
        // but to detect installation we check for the file itself.
        var so = path.EndsWith(".so") ? path : path + ".so";
        return File.Exists(so);
    }

    public static void Load(SqliteConnection conn, string? path = null)
    {
        path ??= DefaultLibPath;
        conn.EnableExtensions(true);
        try { conn.LoadExtension(path); }
        catch (SqliteException)
        {
            // Some builds want the explicit .so suffix
            if (!path.EndsWith(".so")) conn.LoadExtension(path + ".so");
            else throw;
        }
    }

    public static async Task DownloadAsync(string? destPath = null, string version = DefaultVersion,
        string platform = DefaultPlatform, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        destPath ??= DefaultLibPath;
        var destDir = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(destDir);

        var url = $"https://github.com/asg017/sqlite-vec/releases/download/{version}/sqlite-vec-{version.TrimStart('v')}-loadable-{platform}.tar.gz";
        log($"  ↓ {url}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await using var stream = await http.GetStreamAsync(url, ct);
        await using var gz = new GZipStream(stream, CompressionMode.Decompress);
        await using var tar = new TarReader(gz, leaveOpen: false);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
        {
            // archive contains files like ./vec0.so or vec0.so
            var name = Path.GetFileName(entry.Name);
            if (name == "vec0.so" || name == "vec0.dll" || name == "vec0.dylib")
            {
                var outPath = destPath.EndsWith(".so") ? destPath : destPath + ".so";
                await using var outFs = File.Create(outPath);
                if (entry.DataStream is not null)
                    await entry.DataStream.CopyToAsync(outFs, ct);
                log($"    saved {outPath}");
                return;
            }
        }
        throw new InvalidOperationException($"Archive at {url} did not contain vec0 binary.");
    }
}
