using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soz.Core.Data;

namespace Soz.Core.Attention;

/// <summary>Ajanın hata yüzünden durduğunun ayırt edilen nedeni. Key sabit kısa ad, Cause kişiye gösterilen tek sade ifade (cümlenin ortasına girer).</summary>
public sealed record FailureInfo(string Key, string Cause, string? At);

/// <summary>
/// "Hata verip durdu" ayrımı (n6). Boşta görünen bir oturumun kaydının SON anlamlı satırı bir API hatasıysa ve ondan sonra ajan hiçbir şey yazmadıysa oturum hata yüzünden
/// durmuştur. Nedeni yalnız kaydın kendi hata işaretinden ve kısa hata metninden sınıflanır; sınıflanamazsa "unknown" döner ve kişiye "nedenini ayırt edemedim" denir (uydurulmaz).
/// Claude Code: <c>isApiErrorMessage</c> işaretli asistan satırı (alan <c>error</c> varsa o, yoksa metin). Codex: <c>event_msg</c> türü <c>error</c> (ayrıntı alanı ve ileti).
/// Kaydın sohbet içeriği saklanmaz ve dışarı verilmez, yalnız sınıf döner. Sonuç dosyanın (son değişim, uzunluk) çiftine göre önbelleklenir.
/// Hook tarafı için <see cref="FromHookCode"/> aynı sınıflamayı StopFailure ile gelen hata adına uygular.
/// </summary>
public static class FailureProbe
{
    public const string Limit = "limit", Auth = "auth", Billing = "billing", Server = "server", TooLong = "too_long", Invalid = "invalid", Unknown = "unknown";
    /// <summary>Oturumun kanca satırında (needs_user_kind) hata durması bu önekle yazılır: <c>failure:rate_limit</c>.</summary>
    public const string HookKindPrefix = "failure:";
    private const int TailBytes = 192 * 1024;
    private const int MaxLinesBack = 60;

    private static readonly ConcurrentDictionary<string, (long ticks, long len, FailureInfo? info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string CauseOf(string key) => key switch
    {
        Limit => "kullanım sınırı doldu",
        Auth => "giriş geçersiz oldu, yeniden giriş yapmak gerekiyor",
        Billing => "hesapta ödeme ya da bakiye sorunu var",
        Server => "hizmet tarafında bir sorun çıktı",
        TooLong => "yanıt ya da konuşma uzunluk sınırına takıldı",
        Invalid => "istek reddedildi",
        _ => "bir hata verdi ama nedenini ayırt edemedim",
    };

    private static readonly Regex LimitRe = new(@"(usage limit|rate[ _-]?limit|limit reached|hit your limit|limit will reset|resets? at|quota|too many requests|\b429\b|usage_limit)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BillingRe = new(@"(credit balance|billing|payment|insufficient[ _]funds|subscription)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AuthRe = new(@"(invalid api key|/login|authenticat|unauthori[sz]ed|\b401\b|oauth token|not logged in|token has expired|expired token)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TooLongRe = new(@"(max_output|output token|maximum output|prompt is too long|context[ _](length|window)|too long)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InvalidRe = new(@"(invalid[_ ]request|bad request|\b400\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ServerRe = new(@"(overloaded|internal server error|server[_ ]error|\b5\d\d\b|service unavailable|connection (error|reset|refused)|econn|timed? ?out|network)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Hata adı (varsa) ve kısa metinden sınıf. Ad Claude Code'un StopFailure adlarıyla aynıdır: rate_limit, authentication_failed, billing_error, invalid_request, server_error, max_output_tokens.</summary>
    public static string Classify(string? code, string? text)
    {
        switch ((code ?? "").Trim().ToLowerInvariant())
        {
            case "rate_limit": return Limit;
            case "authentication_failed": return Auth;
            case "billing_error": return Billing;
            case "invalid_request": return Invalid;
            case "server_error": return Server;
            case "max_output_tokens": return TooLong;
        }
        var t = (code ?? "") + " " + (text ?? "");
        if (string.IsNullOrWhiteSpace(t)) return Unknown;
        if (LimitRe.IsMatch(t)) return Limit;
        if (BillingRe.IsMatch(t)) return Billing;
        if (AuthRe.IsMatch(t)) return Auth;
        if (TooLongRe.IsMatch(t)) return TooLong;
        if (InvalidRe.IsMatch(t)) return Invalid;
        if (ServerRe.IsMatch(t)) return Server;
        return Unknown;
    }

    /// <summary>Kancadan gelen hata adı ("failure:rate_limit" ya da yalın "rate_limit") için bilgi. Hata satırı değilse null.</summary>
    public static FailureInfo? FromHookKind(string? kind, string? at)
    {
        if (kind is null || !kind.StartsWith(HookKindPrefix, StringComparison.Ordinal)) return null;
        var key = Classify(kind[HookKindPrefix.Length..], null);
        return new FailureInfo(key, CauseOf(key), at);
    }

    public static FailureInfo? FromHookCode(string? code, string? at) { var key = Classify(code, null); return new FailureInfo(key, CauseOf(key), at); }

    /// <summary>Oturum kaydının sonunda hata varsa bilgisi. Yalnız claude ve codex okunur; dosya yoksa ya da okunamazsa null (hata uydurulmaz).</summary>
    public static FailureInfo? Probe(string tool, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || tool is not ("claude" or "codex")) return null;
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return null;
            var ticks = fi.LastWriteTimeUtc.Ticks;
            if (Cache.TryGetValue(filePath, out var hit) && hit.ticks == ticks && hit.len == fi.Length) return hit.info;
            var info = ReadTail(fi, tool);
            Cache[filePath] = (ticks, fi.Length, info);
            if (Cache.Count > 4000) Cache.Clear();
            return info;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static FailureInfo? ReadTail(FileInfo fi, string tool)
    {
        byte[] buf;
        using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            buf = new byte[fs.Length - start];
            var read = 0;
            while (read < buf.Length) { var n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            if (read < buf.Length) Array.Resize(ref buf, read);
            if (start > 0)
            {
                var nl = Array.IndexOf(buf, (byte)'\n');
                buf = nl < 0 ? Array.Empty<byte>() : buf[(nl + 1)..]; // ilk satır yarım olabilir
            }
        }
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        var seen = 0;
        for (var i = lines.Length - 1; i >= 0 && seen < MaxLinesBack; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            seen++;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                var verdict = tool == "claude" ? JudgeClaude(r) : JudgeCodex(r);
                if (verdict == Verdict.Skip) continue;
                if (verdict == Verdict.Fine) return null;
                return tool == "claude" ? ClaudeInfo(r) : CodexInfo(r);
            }
        }
        return null;
    }

    private enum Verdict { Skip, Fine, Failed }

    private static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Verdict JudgeClaude(JsonElement r)
    {
        var type = Str(r, "type");
        if (type is not ("assistant" or "user")) return Verdict.Skip; // sistem, özet, anlık görüntü satırları kararı etkilemez
        if (r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return Verdict.Skip;
        if (type == "assistant" && r.TryGetProperty("isApiErrorMessage", out var e) && e.ValueKind == JsonValueKind.True) return Verdict.Failed;
        return Verdict.Fine;
    }

    private static FailureInfo ClaudeInfo(JsonElement r)
    {
        var code = Str(r, "error");
        string? text = null;
        if (r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("content", out var c))
        {
            if (c.ValueKind == JsonValueKind.String) text = c.GetString();
            else if (c.ValueKind == JsonValueKind.Array)
                foreach (var b in c.EnumerateArray()) if (Str(b, "type") == "text" && Str(b, "text") is { } t) { text = t; break; }
        }
        var key = Classify(code, text is { Length: > 600 } ? text[..600] : text);
        return new FailureInfo(key, CauseOf(key), Time.NormalizeIso(Str(r, "timestamp")));
    }

    private static Verdict JudgeCodex(JsonElement r)
    {
        if (Str(r, "type") != "event_msg") return Verdict.Skip;
        if (!r.TryGetProperty("payload", out var p)) return Verdict.Skip;
        var pt = Str(p, "type");
        if (pt is null or "token_count" or "agent_reasoning" or "agent_reasoning_raw_content") return Verdict.Skip;
        return pt == "error" ? Verdict.Failed : Verdict.Fine;
    }

    private static FailureInfo CodexInfo(JsonElement r)
    {
        var p = r.GetProperty("payload");
        var info = p.TryGetProperty("codex_error_info", out var ci) ? (ci.ValueKind == JsonValueKind.String ? ci.GetString() : ci.GetRawText()) : null;
        var msg = Str(p, "message");
        var joined = (info ?? "") + " " + (msg is { Length: > 600 } ? msg[..600] : msg);
        var key = Classify(null, joined);
        return new FailureInfo(key, CauseOf(key), Time.NormalizeIso(Str(r, "timestamp")));
    }
}
