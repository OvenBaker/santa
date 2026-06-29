namespace Santa.Core.Jsonl;

/// <summary>
/// Streams a JSONL file line-by-line, tracking byte offsets so ingest can resume from a cursor.
/// LF-terminated; tolerates trailing CR. Skips blank or unparseable lines.
/// </summary>
public sealed class JsonlReader
{
    private readonly string _path;

    public JsonlReader(string path) => _path = path;

    public IEnumerable<(long StartOffset, long EndOffset, JsonlEvent Event)> Read(long startOffset = 0)
    {
        using var raw = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (startOffset > 0) raw.Seek(startOffset, SeekOrigin.Begin);
        using var fs = new BufferedStream(raw, 64 * 1024);

        var lineStart = startOffset;
        var line = new List<byte>(1024);
        int b;
        long pos = startOffset;
        while ((b = fs.ReadByte()) != -1)
        {
            pos++;
            if (b == '\n')
            {
                var bytes = TrimTrailingCr(line);
                if (bytes.Length > 0)
                {
                    var ev = JsonlParser.Parse(bytes);
                    if (ev is not null)
                        yield return (lineStart, pos, ev);
                }
                line.Clear();
                lineStart = pos;
            }
            else
            {
                line.Add((byte)b);
            }
        }
        // Trailing partial line (file flushed without final \n): leave cursor at lineStart
        // so next ingest resumes from the start of that incomplete record.
    }

    private static ReadOnlySpan<byte> TrimTrailingCr(List<byte> buf)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf);
        if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];
        return span;
    }
}
