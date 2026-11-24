using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Restore;
using Soz.Core.Run;

namespace Soz.Server.Endpoints;

/// <summary>
/// Geri alma uçları (Dalga 2, şerit B). Güvenlik SozServer ara katmanından gelir (Host, köken, X-Soz-Token).
///   GET  /api/jobs/{id}/protection        koruma düzeyi (tam | sinirli | yok) ve kartta gösterilecek cümleler
///   POST /api/jobs/{id}/undo/preview      { runId?, scope? "all"|"last", paths?: [yol...], force? }  hiçbir dosyaya dokunmaz, ne olacağını listeler
///   POST /api/jobs/{id}/undo              aynı gövde + { previewUid? }  uygular (önce şimdiki durum otomatik nokta olur)
///   POST /api/undo/{uid}/revert           { force? }  "geri almayı geri al": geri almadan önceki duruma döner
///   GET  /api/jobs/{id}/undo              bu işin geri alma kayıtları
/// Gövdedeki yollar proje klasörüne göre "/" ayraçlıdır; klasör seçimi için yol (ya da "yol/") verilir.
/// </summary>
public static class UndoEndpoints
{
    public static RouteGroupBuilder MapUndo(this RouteGroupBuilder api, RunManager runs)
    {
        var restore = runs.Restore;
        api.MapGet("/jobs/{id:long}/protection", (long id) => Results.Json(restore.ProtectionOf(id)));
        api.MapGet("/jobs/{id:long}/undo", (long id) => Results.Json(new { undo = restore.History(id) }));
        api.MapPost("/jobs/{id:long}/undo/preview", async (long id, HttpContext ctx) => Results.Json(await Task.Run(async () => restore.Preview(id, ParseRequest(await Body(ctx))))));
        api.MapPost("/jobs/{id:long}/undo", async (long id, HttpContext ctx) => Results.Json(await Task.Run(async () => restore.Apply(id, ParseRequest(await Body(ctx))))));
        api.MapPost("/undo/{uid}/revert", async (string uid, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(await Task.Run(() => restore.Revert(uid, b["force"] is JsonValue f && f.TryGetValue<bool>(out var force) && force)));
        });
        return api;
    }

    private static async Task<JsonObject> Body(HttpContext ctx)
    {
        using var r = new StreamReader(ctx.Request.Body);
        var text = await r.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? throw new SozException(400, "Gövde bir JSON nesnesi olmalı.");
    }

    private static UndoRequest ParseRequest(JsonObject b)
    {
        long? runId = b["runId"] is JsonValue rv && rv.TryGetValue<long>(out var rid) ? rid : null;
        var scope = b["scope"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
        if (scope is not null && scope is not ("all" or "last")) throw new SozException(400, "scope \"all\" ya da \"last\" olmalı.");
        List<string>? paths = null;
        if (b["paths"] is JsonArray arr)
        {
            paths = new();
            foreach (var p in arr)
            {
                if (p is JsonValue pv && pv.TryGetValue<string>(out var ps) && !string.IsNullOrWhiteSpace(ps)) paths.Add(ps);
                else throw new SozException(400, "paths yol metinlerinden oluşmalı.");
            }
        }
        var force = b["force"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;
        var previewUid = b["previewUid"] is JsonValue pu && pu.TryGetValue<string>(out var puid) ? puid : null;
        return new UndoRequest(runId, scope, paths, force, previewUid);
    }
}
