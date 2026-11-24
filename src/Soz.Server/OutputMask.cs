using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Text;

namespace Soz.Server;

/// <summary>
/// Çıkış maskesi (denetim 5, B4): kişinin iş metnine yazdığı anahtar benzeri bir değer HİÇBİR API cevabında düz görünmez.
/// Maske alan alan değil, TEK YERDE ve cevap biçimine bakmadan çalışır: /api altındaki her JSON cevabın bütün metin değerleri
/// <see cref="SecretFilter.MaskSecrets"/>'ten geçer. Yeni bir alan (ya da yeni bir uç) eklenince kendiliğinden kapsanır.
/// Tek istisna kimlik, adres ve yol gibi "yapısal" alan adlarıdır (<see cref="IsStructural"/>): bunlar kişinin yazdığı metin değil,
/// Söz'ün kendi ürettiği değerlerdir ve maskelenirse istemci onları geri gönderemez (ör. "Gördüm" öğe kimliği).
/// Bütün cevap hiç değişmediyse gövde olduğu gibi (bayt bayt aynı) gider.
/// Diskte durum: iş metni (jobs.raw_text, prompt_text) kişinin kendi veri klasöründe HAM saklanır, çünkü ajana giden istem bozulmasın.
/// Maske yalnız dışarı okunan cevaplarda uygulanır.
/// </summary>
public static class OutputMask
{
    /// <summary>Maskelenmeyen yollar: oturum jetonu, telefon eşleştirme kodu ve canlı akış.</summary>
    private static readonly string[] SkipPrefixes = { "/api/session", "/api/phone/", "/api/events/stream", "/api/hooks/" };

    private static readonly JsonSerializerOptions Out = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool Applies(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (HttpMethods.IsHead(ctx.Request.Method)) return false;
        foreach (var p in SkipPrefixes) if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Kimlik ve yol gibi Söz'ün kendi ürettiği alanlar: değerleri sır sayılmaz ve maskelenmez.
    /// "url" bilerek YOK (denetim 5 tur 2, N3): ölçüt adresi kişinin yazdığı metindir ve içinde "?key=..." gibi sır olabilir.
    /// Adres alanları da süzgeçten geçer; süzgeç düz yolları, zaman damgalarını ve adres iskeletini zaten bozmaz.
    /// </summary>
    public static bool IsStructural(string name)
    {
        if (name.Length == 0) return false;
        var n = name;
        return n.Equals("id", StringComparison.OrdinalIgnoreCase) || n.Equals("uid", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith("Id", StringComparison.Ordinal) || n.EndsWith("Uid", StringComparison.Ordinal)
            || n.EndsWith("Ids", StringComparison.Ordinal) || n.EndsWith("Uids", StringComparison.Ordinal)
            || n.Equals("path", StringComparison.OrdinalIgnoreCase) || n.EndsWith("Path", StringComparison.Ordinal)
            || n.Equals("dir", StringComparison.OrdinalIgnoreCase) || n.EndsWith("Dir", StringComparison.Ordinal)
            || n.EndsWith("Hash", StringComparison.Ordinal) || n.Equals("sha", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ara katman: cevabı arabelleğe alır, JSON ise metin değerlerini maskeler, boyutu düzeltip yazar.</summary>
    public static async Task Middleware(HttpContext ctx, RequestDelegate next)
    {
        if (!Applies(ctx)) { await next(ctx); return; }
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await next(ctx); }
        finally { ctx.Response.Body = original; }

        var bytes = buffer.ToArray();
        var contentType = ctx.Response.ContentType ?? "";
        if (bytes.Length > 0)
        {
            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                bytes = MaskJson(bytes);
            else if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                bytes = MaskText(bytes);
        }
        if (ctx.Response.StatusCode is 204 or 304) return;
        ctx.Response.ContentLength = bytes.Length;
        if (bytes.Length > 0) await original.WriteAsync(bytes, ctx.RequestAborted);
    }

    public static byte[] MaskText(byte[] body)
    {
        if (body.Length == 0) return body;
        var text = System.Text.Encoding.UTF8.GetString(body);
        var masked = SecretFilter.MaskSecrets(text);
        return ReferenceEquals(masked, text) || masked == text ? body : System.Text.Encoding.UTF8.GetBytes(masked);
    }

    /// <summary>JSON gövdedeki bütün metin değerleri maskeler. Hiçbir şey değişmediyse aynı dizi döner; JSON olmayan gövdeye dokunmaz.</summary>
    public static byte[] MaskJson(byte[] body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return body; }
        if (root is null) return body;
        var changed = false;
        var masked = Walk(root, null, ref changed);
        if (!changed) return body;
        return System.Text.Encoding.UTF8.GetBytes(masked!.ToJsonString(Out));
    }

    private static JsonNode? Walk(JsonNode? node, string? name, ref bool changed)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o.ToList())
                {
                    var r = Walk(kv.Value, kv.Key, ref changed);
                    if (!ReferenceEquals(r, kv.Value)) o[kv.Key] = r;
                }
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var r = Walk(a[i], name, ref changed);
                    if (!ReferenceEquals(r, a[i])) a[i] = r;
                }
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (s.Length < 4 || (name is not null && IsStructural(name))) return node;
                var m = SecretFilter.MaskSecrets(s);
                if (ReferenceEquals(m, s) || m == s) return node;
                changed = true;
                return JsonValue.Create(m);
            default:
                return node;
        }
    }
}
