using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soz.Core;

namespace Soz.Server.Endpoints;

/// <summary>Masaüstü kabuğunun kendi tercihleri (Windows açılınca başlat, bildirimler, kaldırırken veriyi sil).</summary>
public sealed record ShellPrefs(bool StartWithWindows, bool Notifications, bool DeleteDataOnUninstall);

/// <summary>
/// Sunucu ile masaüstü kabuğu arasındaki tercih köprüsü. Kabuk (aynı süreçte) <see cref="Read"/> ve <see cref="Write"/> işlevlerini bağlar; tarayıcıdan açılan
/// yalnız sunucuda (<c>soz serve</c>) bağlı değildir ve uç bunu açıkça söyler. Değişen değer kabuk tarafında kalıcı yazılır.
/// <c>GET /api/shell/prefs</c> → <c>{ available, startWithWindows, notifications, deleteDataOnUninstall }</c> (kabuk yoksa <c>{ available:false }</c>).
/// <c>PUT /api/shell/prefs</c> (yazma jetonu ister) gövde: <c>{ startWithWindows?, notifications?, deleteDataOnUninstall?, confirmed? }</c>. Veriyi silme seçeneğini AÇMAK
/// için <c>confirmed:true</c> gerekir (arayüz önce kişiye sorar). Kabuk yoksa 501, anlaşılmayan değerde 400, kayıt defteri ya da dosya yazılamazsa 500.
/// </summary>
public static class ShellBridge
{
    public static Func<ShellPrefs?>? Read { get; set; }
    /// <summary>(startWithWindows, notifications, deleteDataOnUninstall) → yazılan son durum ya da null (yazılamadı). Null olan alan değiştirilmez.</summary>
    public static Func<bool?, bool?, bool?, ShellPrefs?>? Write { get; set; }

    public static RouteGroupBuilder MapShellPrefs(this RouteGroupBuilder api)
    {
        api.MapGet("/shell/prefs", () =>
        {
            var p = Read?.Invoke();
            return p is null
                ? Results.Json(new { available = false })
                : Results.Json(new { available = true, startWithWindows = p.StartWithWindows, notifications = p.Notifications, deleteDataOnUninstall = p.DeleteDataOnUninstall });
        });
        api.MapPut("/shell/prefs", async (HttpContext ctx) =>
        {
            if (Write is null || Read is null)
                return Results.Json(new { ok = false, error = "Bu ayar yalnız masaüstü uygulamasında değişir. Söz'ü masaüstü uygulaması olarak aç." }, statusCode: 501);
            var b = await Body(ctx);
            bool? Flag(string k)
            {
                if (!b.ContainsKey(k) || b[k] is null) return null;
                if (b[k] is JsonValue v && v.TryGetValue<bool>(out var x)) return x;
                throw new SozException(400, "Bu ayar için açık ya da kapalı (true ya da false) bekleniyor: " + k);
            }
            var start = Flag("startWithWindows"); var notify = Flag("notifications"); var delData = Flag("deleteDataOnUninstall"); var confirmed = Flag("confirmed") ?? false;
            if (start is null && notify is null && delData is null) throw new SozException(400, "Değiştirilecek bir ayar verilmedi.");
            if (delData == true && !confirmed)
                throw new SozException(400, "Kaldırırken verini de silmeyi açmak için önce onay vermelisin. Bu seçenek açıkken Söz kaldırılınca bütün kayıtların silinir.");
            var res = Write(start, notify, delData);
            return res is null
                ? Results.Json(new { ok = false, error = "Ayar yazılamadı. Windows ya da klasör izni engellemiş olabilir." }, statusCode: 500)
                : Results.Json(new { ok = true, startWithWindows = res.StartWithWindows, notifications = res.Notifications, deleteDataOnUninstall = res.DeleteDataOnUninstall });
        });
        return api;
    }

    private static async Task<JsonObject> Body(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is 0) return new JsonObject();
        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var text = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? throw new SozException(400, "Gövde bir JSON nesnesi olmalı.");
    }
}
