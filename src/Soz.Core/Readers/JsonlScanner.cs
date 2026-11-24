using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Soz.Core.Readers;

public delegate void LineHandler(ReadOnlySpan<byte> line, long lineOffset, long lineNo);

/// <summary>JSONL dosyasını bayt düzeyinde, akışla okur. Yalnız tam satırları işler; yarım son satır sonraki okumaya kalır.</summary>
public static class JsonlScanner
{
    /// <summary>[offset, dosya sonu) aralığındaki tam satırları işler. Dönen değer: işlenen son tam satırdan sonraki bayt konumu.</summary>
    public static long Scan(string path, long offset, long lineNo, LineHandler handler, out long endLineNo, out long fileLength, bool acceptUnterminatedTail = false)
    {
        using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0,
            Options = FileOptions.SequentialScan,
        });
        fileLength = fs.Length;
        if (offset > fileLength) { offset = 0; lineNo = 0; } // dosya kısaldı/yeniden yazıldı: baştan
        fs.Seek(offset, SeekOrigin.Begin);
        var limit = fileLength;
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var filled = 0;
            var bufStart = offset;
            while (true)
            {
                if (filled == buf.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, bigger, 0, filled);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }
                var want = (int)Math.Min(buf.Length - filled, limit - (bufStart + filled));
                if (want <= 0) break;
                var n = fs.Read(buf, filled, want);
                if (n <= 0) break;
                filled += n;
                var pos = 0;
                while (pos < filled)
                {
                    var nl = buf.AsSpan(pos, filled - pos).IndexOf((byte)'\n');
                    if (nl < 0) break;
                    var line = buf.AsSpan(pos, nl);
                    if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
                    lineNo++;
                    if (line.Length > 0) handler(line, bufStart + pos, lineNo);
                    pos += nl + 1;
                }
                if (pos > 0)
                {
                    Buffer.BlockCopy(buf, pos, buf, 0, filled - pos);
                    bufStart += pos;
                    filled -= pos;
                }
            }
            // Sonlandırılmamış son satır: yalnız dosya bir süredir değişmiyorsa ve geçerli JSON ise işlenir.
            if (filled > 0 && acceptUnterminatedTail)
            {
                var tail = buf.AsSpan(0, filled);
                if (IsCompleteJson(tail))
                {
                    lineNo++;
                    handler(tail, bufStart, lineNo);
                    bufStart += filled;
                }
            }
            endLineNo = lineNo;
            return bufStart;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static bool IsCompleteJson(ReadOnlySpan<byte> s)
    {
        try
        {
            var r = new Utf8JsonReader(s);
            if (!r.Read()) return false;
            r.Skip();
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Kök nesnenin "type" alanını, büyük değerleri ayrıştırmadan bulur. Bulunamazsa null; JSON bozuksa bad=true.</summary>
    public static string? RootType(ReadOnlySpan<byte> line, out bool bad)
    {
        bad = false;
        try
        {
            var r = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 256 });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) { bad = true; return null; }
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject) return null;
                if (r.TokenType != JsonTokenType.PropertyName) { bad = true; return null; }
                var isType = r.ValueTextEquals("type"u8);
                if (!r.Read()) { bad = true; return null; }
                if (isType && r.TokenType == JsonTokenType.String) return r.GetString();
                r.Skip();
            }
            bad = true;
            return null;
        }
        catch (JsonException) { bad = true; return null; }
    }

    public static bool Contains(ReadOnlySpan<byte> line, ReadOnlySpan<byte> needle, int within = int.MaxValue)
    {
        var s = within < line.Length ? line[..within] : line;
        return s.IndexOf(needle) >= 0;
    }

    public static JsonDocument? TryParse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 256 });
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Dosyanın ilk satırını (en çok maxBytes) okur.</summary>
    public static byte[]? ReadFirstLine(string path, int maxBytes = 1 << 20)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[Math.Min(maxBytes, (int)Math.Min(int.MaxValue, Math.Max(1, fs.Length)))];
        var total = 0;
        while (total < buf.Length)
        {
            var n = fs.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            var nl = Array.IndexOf(buf, (byte)'\n', total, n);
            total += n;
            if (nl >= 0) return buf[..nl];
        }
        return total > 0 ? buf[..total] : null;
    }

    public static string Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static string? StrN(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static bool Has(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public static JsonElement Obj(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    /// <summary>"content" alanı: düz metin ya da [{type:'text'|'input_text'|'output_text', text}] dizisi.</summary>
    public static string ContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var x in content.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var t = x.Str("type");
            if (t is "text" or "input_text" or "output_text")
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(x.Str("text"));
            }
        }
        return sb.ToString();
    }
}
