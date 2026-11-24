using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soz.Core;
using Soz.Core.Discovery;

namespace Soz.Server.Endpoints;

/// <summary>
/// Keşif uçları (yalnız çekirdek mantık; görünüm yok). Değişiklik uçları X-Soz-Token'ı SozServer ara katmanından alır.
/// Tablolar şemaya bağlanmamışsa değişiklik ve öneri uçları 503 "Keşif henüz kurulmadı." der, çökmez; arama tablo olmadan da çalışır.
///   GET    /api/discovery/layout                        64 yuvanın tamamı (8 sabit yön x 8 yuva, boşlar dahil)
///   PUT    /api/discovery/layout/slot                   {kindDir, slotDir, objectUid, replace?}  kişi yerleştirir; dolu yuvada replace yoksa 409
///   DELETE /api/discovery/layout/slot?kindDir=&amp;slotDir=   kişi boşaltır (komşular kaymaz)
///   POST   /api/discovery/usage                         {objectUid, projectUid?, source?}  kullanım kaydı
///   POST   /api/discovery/suggestions                   {projectUid?, limit?, surface?}  öneriler (gölge kipte items boş, günlüğe yazar; surface:"page" kişinin kendi açtığı sayfadır, her zaman gösterir)
///   POST   /api/discovery/dismiss                       {objectUid, projectUid?, mode: ignored|never-here|never}
///   POST   /api/discovery/restore                       aynı gövde: susturmayı geri al
///   GET    /api/discovery/shadow?days=                  gölge kip isabet özeti
///   GET    /api/discovery/search?q=&amp;projectUid=&amp;limit=
/// </summary>
public static class DiscoveryEndpoints
{
    public static RouteGroupBuilder MapDiscovery(this RouteGroupBuilder api, SozService service)
    {
        // Her istekte yeni servis: kayıt defteri ilk kullanımda ve güncel hâliyle (onaylar dahil) alınır; bozuk kayıt açılışı değil yalnız o çağrıyı etkiler.
        DiscoveryService D() => new(service.Db);

        api.MapGet("/discovery/layout", () => Results.Json(D().Layout()));
        api.MapPut("/discovery/layout/slot", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(D().Place(Int(b, "kindDir"), Int(b, "slotDir"), Str(b, "objectUid"), Bool(b, "replace")));
        });
        api.MapDelete("/discovery/layout/slot", (int? kindDir, int? slotDir) =>
            Results.Json(D().Clear(kindDir ?? throw new SozException(400, "kindDir gerekli."), slotDir ?? throw new SozException(400, "slotDir gerekli."))));

        api.MapPost("/discovery/usage", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(D().RecordUsage(Str(b, "objectUid"), Str(b, "projectUid"), Str(b, "source")));
        });
        api.MapPost("/discovery/suggestions", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(D().Suggestions(Str(b, "projectUid"), b["limit"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null, forPage: Str(b, "surface") == "page"));
        });
        api.MapPost("/discovery/dismiss", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(D().Dismiss(Str(b, "objectUid"), Str(b, "projectUid"), Str(b, "mode"), restore: false));
        });
        api.MapPost("/discovery/restore", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(D().Dismiss(Str(b, "objectUid"), Str(b, "projectUid"), Str(b, "mode"), restore: true));
        });
        api.MapGet("/discovery/shadow", (int? days) => Results.Json(D().ShadowReport(days)));
        api.MapGet("/discovery/search", (string? q, string? projectUid, int? limit) => Results.Json(D().Search(q, projectUid, limit)));
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

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static int Int(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : throw new SozException(400, $"{k} gerekli (tam sayı).");
}
