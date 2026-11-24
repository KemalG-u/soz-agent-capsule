using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Core.Hooks;

/// <summary>
/// Kullanıcının settings.json dosyasını METİN olarak düzenler: yorumlar, sıra, girinti, satır sonu (CRLF/LF), BOM ve kullanıcının
/// diğer ayarları bayt bayt korunur; yalnız Söz'ün eklediği ya da kaldırdığı satırlar değişir. (JsonNode ile yeniden yazmak yorumları siler.)
/// Okuyucu JSONC bilir: // ve /* */ yorumları, sondaki virgül.
/// </summary>
internal sealed class JsonEntry
{
    /// <summary>Üye ise anahtarın, öğe ise değerin başı.</summary>
    public int Start;
    /// <summary>Değerin sonu (dışta kalır).</summary>
    public int End;
    public string? Name;
    public JsonSpan Value = null!;
}

internal sealed class JsonSpan
{
    public char Kind; // '{', '[' ya da 'v' (değer)
    public int Start;
    public int End;
    public List<JsonEntry> Entries = new();
    public bool IsObject => Kind == '{';
    public bool IsArray => Kind == '[';
}

internal static class SettingsText
{
    // ---------------- okuyucu ----------------
    public static JsonSpan Parse(string text)
    {
        var i = 0;
        SkipWs(text, ref i);
        var root = ParseValue(text, ref i);
        SkipWs(text, ref i);
        if (i != text.Length) throw new InvalidDataException("settings.json biçimi tanınmadı (sonunda fazlalık var). Dokunulmadı.");
        return root;
    }

    private static InvalidDataException Bad(string why) => new("settings.json okunamadı (" + why + "). Dokunulmadı.");

    internal static void SkipWs(string t, ref int i)
    {
        while (i < t.Length)
        {
            var c = t[i];
            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                var e = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0) throw Bad("kapanmayan yorum");
                i = e + 2; continue;
            }
            break;
        }
    }

    private static JsonSpan ParseValue(string t, ref int i)
    {
        if (i >= t.Length) throw Bad("beklenmeyen son");
        var c = t[i];
        if (c == '{') return ParseContainer(t, ref i, '{', '}');
        if (c == '[') return ParseContainer(t, ref i, '[', ']');
        var s = i;
        if (c == '"') { SkipString(t, ref i); return new JsonSpan { Kind = 'v', Start = s, End = i }; }
        while (i < t.Length && t[i] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n' or '/' or ':')) i++;
        if (i == s) throw Bad("değer yok");
        return new JsonSpan { Kind = 'v', Start = s, End = i };
    }

    private static void SkipString(string t, ref int i)
    {
        i++; // açılış tırnağı
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '"') { i++; return; }
            if (c is '\n' or '\r') throw Bad("satır sonu içeren metin");
            i++;
        }
        throw Bad("kapanmayan metin");
    }

    private static JsonSpan ParseContainer(string t, ref int i, char open, char close)
    {
        var span = new JsonSpan { Kind = open, Start = i };
        i++;
        while (true)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) throw Bad("kapanmayan küme");
            if (t[i] == close) { i++; span.End = i; return span; }
            var entry = new JsonEntry { Start = i };
            if (open == '{')
            {
                if (t[i] != '"') throw Bad("anahtar bekleniyordu");
                var ks = i; SkipString(t, ref i);
                entry.Name = JsonSerializer.Deserialize<string>(t.AsSpan(ks, i - ks));
                SkipWs(t, ref i);
                if (i >= t.Length || t[i] != ':') throw Bad("':' bekleniyordu");
                i++;
                SkipWs(t, ref i);
            }
            entry.Value = ParseValue(t, ref i);
            entry.End = entry.Value.End;
            span.Entries.Add(entry);
            SkipWs(t, ref i);
            if (i < t.Length && t[i] == ',') { i++; continue; }
            if (i < t.Length && t[i] == close) { i++; span.End = i; return span; }
            throw Bad("',' ya da kapanış bekleniyordu");
        }
    }

    public static JsonEntry? Member(JsonSpan obj, string name) => obj.Entries.FirstOrDefault(e => e.Name == name);

    // ---------------- yerleşim ----------------
    public static string NewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int LineStart(string t, int pos)
    {
        var k = t.LastIndexOf('\n', Math.Max(0, pos - 1));
        return pos == 0 || k < 0 ? 0 : k + 1;
    }

    private static string LineIndent(string t, int pos)
    {
        var s = LineStart(t, pos);
        var e = s;
        while (e < t.Length && t[e] is ' ' or '\t') e++;
        return t.Substring(s, e - s);
    }

    private static bool OnlyWsBefore(string t, int pos)
    {
        for (var k = LineStart(t, pos); k < pos; k++) if (t[k] is not (' ' or '\t')) return false;
        return true;
    }

    /// <summary>Girinti birimi: sekme kullanılıyorsa sekme, yoksa kökün ilk üyesinin girintisi (yoksa iki boşluk).</summary>
    private static string IndentUnit(string t, JsonSpan root)
    {
        if (t.Contains("\n\t", StringComparison.Ordinal)) return "\t";
        if (root.Entries.Count > 0 && OnlyWsBefore(t, root.Entries[0].Start))
        {
            var ind = LineIndent(t, root.Entries[0].Start);
            if (ind.Length > 0 && ind.All(c => c == ' ')) return ind;
        }
        return "  ";
    }

    /// <summary>Girinti birimli (2 boşluk) çok satırlı JSON'u verilen tabana ve dosyanın girinti/satır sonu biçimine uydurur. İlk satıra girinti eklenmez.</summary>
    private static string Reindent(string pretty, string baseIndent, string unit, string nl)
    {
        var lines = pretty.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (var k = 0; k < lines.Length; k++)
        {
            var l = lines[k];
            if (k == 0) { sb.Append(l); continue; }
            var s = 0; while (s < l.Length && l[s] == ' ') s++;
            sb.Append(nl).Append(baseIndent);
            for (var u = 0; u < s / 2; u++) sb.Append(unit);
            sb.Append(l.AsSpan(s));
        }
        return sb.ToString();
    }

    internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Satır sonuna kadar boşluk, isteğe bağlı virgül, boşluk ve isteğe bağlı yorum var mı? Varsa (virgül var mı, satır içeriğinin sonu).</summary>
    private static bool TrailTail(string t, int from, out bool comma, out int commaPos, out int contentEnd)
    {
        comma = false; commaPos = -1; contentEnd = from;
        var k = from;
        while (k < t.Length && t[k] is ' ' or '\t') k++;
        if (k < t.Length && t[k] == ',') { comma = true; commaPos = k; k++; while (k < t.Length && t[k] is ' ' or '\t') k++; }
        if (k + 1 < t.Length && t[k] == '/' && t[k + 1] == '/') { while (k < t.Length && t[k] != '\n' && t[k] != '\r') k++; }
        else if (k + 1 < t.Length && t[k] == '/' && t[k + 1] == '*')
        {
            var e = t.IndexOf("*/", k + 2, StringComparison.Ordinal);
            if (e < 0) return false;
            var body = t.AsSpan(k, e + 2 - k);
            if (body.Contains('\n')) return false;
            k = e + 2; while (k < t.Length && t[k] is ' ' or '\t') k++;
        }
        if (k < t.Length && t[k] != '\n' && t[k] != '\r') return false; // satırda başka bir şey var (tek satırlık yerleşim)
        contentEnd = k;
        return true;
    }

    // ---------------- ekleme ----------------
    /// <summary>
    /// Kümenin (nesne ya da dizi) SONUNA bir üye/öğe ekler. name verilirse "ad": değer üyesi, yoksa öğe. Başka hiçbir bayta dokunmaz
    /// (önceki öğeye gerekirse bir virgül eklenir). Çok satırlı dosyada her öğe kendi satırında, tek satırlıkta ", öğe" olarak eklenir.
    /// </summary>
    public static string InsertEntry(string text, JsonSpan container, string? name, JsonNode value)
    {
        var nl = NewLine(text);
        var root = Parse(text);
        var unit = IndentUnit(text, root);
        var key = name is null ? "" : JsonSerializer.Serialize(name, Compact) + ": ";

        if (container.Entries.Count == 0)
        {
            var objIndent = LineIndent(text, container.Start);
            var memberIndent = objIndent + unit;
            var pretty = key + Reindent(value.ToJsonString(Pretty), memberIndent, unit, nl);
            var innerStart = container.Start + 1;
            var innerEnd = container.End - 1;
            var inner = text.Substring(innerStart, innerEnd - innerStart);
            if (inner.All(char.IsWhiteSpace))
                return text[..innerStart] + nl + memberIndent + pretty + nl + objIndent + text[innerEnd..];
            return text[..innerStart] + nl + memberIndent + pretty + text[innerStart..];
        }

        var last = container.Entries[^1];
        var multi = OnlyWsBefore(text, last.Start) && TrailTail(text, last.End, out var hasComma, out var commaPos, out var contentEnd);
        if (!multi)
        {
            // tek satırlık küme: , öğe
            var compact = key + value.ToJsonString(Compact);
            return text[..last.End] + ", " + compact + text[last.End..];
        }
        TrailTail(text, last.End, out hasComma, out commaPos, out contentEnd);
        var indent = LineIndent(text, last.Start);
        var item = key + Reindent(value.ToJsonString(Pretty), indent, unit, nl);
        var tail = text[contentEnd..];
        var head = text[..contentEnd];
        var add = nl + indent + item + (hasComma ? "," : "");
        var result = head + add + tail;
        if (!hasComma) result = result[..last.End] + "," + result[last.End..]; // önceki öğeye virgül
        return result;
    }

    // ---------------- kaldırma ----------------
    /// <summary>Kümenin index'inci üyesini/öğesini kaldırır. Çok satırlıda öğenin satırları (ve gerekiyorsa önceki öğenin virgülü) silinir. Küme boşalırsa ve içinde yalnız boşluk kalırsa {} / [] olur.</summary>
    public static string RemoveEntry(string text, JsonSpan container, int index)
    {
        var e = container.Entries[index];
        var isLast = index == container.Entries.Count - 1;
        var prev = index > 0 ? container.Entries[index - 1] : null;

        var ownLines = OnlyWsBefore(text, e.Start) && TrailTail(text, e.End, out var hasComma, out var commaPos, out var contentEnd);
        if (ownLines)
        {
            TrailTail(text, e.End, out hasComma, out commaPos, out contentEnd);
            var ls = LineStart(text, e.Start);
            // önceki yeni satırı (ve varsa \r'yi) da al: ekleme "\n" + satır olarak yapıldı
            var cut = ls > 0 ? ls - 1 : 0;
            if (ls > 1 && text[ls - 2] == '\r') cut = ls - 2;
            string result;
            if (ls > 0) result = text[..cut] + text[contentEnd..];
            else result = text[contentEnd..];
            if (isLast && !hasComma && prev is not null)
            {
                // son öğe sondaki virgülsüzdü: öncekinin virgülü kalkar
                var k = prev.End;
                while (k < result.Length && result[k] is ' ' or '\t') k++;
                if (k < result.Length && result[k] == ',') result = result[..k] + result[(k + 1)..];
            }
            if (container.Entries.Count == 1)
            {
                // küme boşaldı: aralarında yalnız boşluk kaldıysa {} / []
                var openPos = container.Start;
                var closeRel = FindClose(result, openPos, container.Kind);
                if (closeRel > openPos && result.AsSpan(openPos + 1, closeRel - openPos - 1).IsWhiteSpace())
                    result = result[..(openPos + 1)] + result[closeRel..];
            }
            return result;
        }

        // tek satırlık yerleşim
        if (prev is not null) return text[..prev.End] + text[e.End..];
        if (container.Entries.Count > 1)
        {
            var next = container.Entries[1];
            return text[..e.Start] + text[next.Start..];
        }
        return text[..(container.Start + 1)] + text[(container.End - 1)..];
    }

    private static int FindClose(string t, int open, char kind)
    {
        try { var s = Parse(t); return FindSpanAt(s, open)?.End - 1 ?? -1; }
        catch (InvalidDataException) { return -1; }
    }

    private static JsonSpan? FindSpanAt(JsonSpan s, int start)
    {
        if (s.Start == start) return s;
        foreach (var e in s.Entries)
        {
            if (e.Value.Start <= start && start < e.Value.End) { var f = FindSpanAt(e.Value, start); if (f is not null) return f; }
        }
        return null;
    }

    // ---------------- kıyas ----------------
    /// <summary>Boşluğu atar (metin ve yorumlar hariç): aynı anlam ve yorumlar, farklı biçim = aynı dize.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == '"') { var s = i; try { SkipString(text, ref i); } catch (InvalidDataException) { i = text.Length; } sb.Append(text, s, i - s); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { var s = i; while (i < text.Length && text[i] != '\n') i++; sb.Append(text, s, i - s).Append('\n'); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var s = i; var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? text.Length : e + 2; sb.Append(text, s, i - s); continue;
            }
            if (!char.IsWhiteSpace(c)) sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
