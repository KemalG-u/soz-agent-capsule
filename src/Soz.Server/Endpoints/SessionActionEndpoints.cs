using System.Text;
using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Attention;
using Soz.Core.Jobs;

namespace Soz.Server.Endpoints;

/// <summary>
/// "Benden Beklenen" kutusundaki oturum öğelerinin eylem uçları (n6). Hepsi yalnız insandan kabul edilir (X-Soz-Token, ajan çağrısı 403).
///
/// <c>POST /api/sessions/{id}/focus</c> gövde (isteğe bağlı) <c>{ "openFolder": true }</c>: oturumun açık penceresini öne getirir.
///   200 <c>{ ok, message, focused, hwnd, openedFolder }</c>; pencere bulunamazsa 404 <c>{ ok:false, error }</c>; masaüstü yoksa (Windows dışı) 501.
/// <c>POST /api/sessions/{id}/prove</c>: oturumun projesindeki kayıtlı ölçütlerle Söz'ün kanıt motorunu o klasörde çalıştırır.
///   202 <c>{ ok, message, runId, projectId, checks }</c> (sonuç /api/proof/runs/{runId} ile okunur); ölçüt yoksa 409 "Bu projede neyle sınayacağımı bilmiyorum...";
///   proje bilinmiyorsa 409; aynı proje için sınama sürüyorsa 409; oturum yoksa 404.
/// <c>GET /api/import/auto</c>: kendiliğinden içe almanın durumu <c>{ running, intervalSeconds, runs, changedRuns, skippedBusy, lastMs, maxMs, lastError }</c>.
/// <c>POST /api/attention/focus</c> ve <c>POST /api/attention/prove</c> gövde <c>{ "id": "&lt;öğe kimliği&gt;" }</c>: aynı işler, oturum kaydı olmayan dış oturum öğeleri için (öğe kimliğiyle).
/// </summary>
public static class SessionActionEndpoints
{
    public static RouteGroupBuilder MapSessionActions(this RouteGroupBuilder api, SozService service)
    {
        var actions = new SessionActions(service.Db, service.Attention);
        var work = new WorkService(service.Db) { Actor = "api" };

        // Kendiliğinden içe almanın durumu: kaç tur, kaçında değişiklik vardı, son ve en uzun tur kaç ms.
        api.MapGet("/import/auto", () => AutoImporter.Current is { } a
            ? Results.Json(new { running = true, intervalSeconds = (int)a.Interval.TotalSeconds, runs = a.Runs, changedRuns = a.ChangedRuns, skippedBusy = a.SkippedBusy, lastMs = a.LastMs, maxMs = a.MaxMs, lastError = a.LastError })
            : Results.Json(new { running = false }));
        api.MapPost("/sessions/{id:long}/focus", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Reply(await Task.Run(() => actions.Focus(id, null, Bool(b, "openFolder") ?? false)));
        });
        api.MapPost("/sessions/{id:long}/prove", (long id) => Reply(actions.Prove(id, null, work)));
        api.MapPost("/attention/focus", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var item = Str(b, "id") ?? throw new SozException(400, "id gerekli (öğe kimliği).");
            return Reply(await Task.Run(() => actions.Focus(null, item, Bool(b, "openFolder") ?? false)));
        });
        api.MapPost("/attention/prove", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var item = Str(b, "id") ?? throw new SozException(400, "id gerekli (öğe kimliği).");
            return Reply(actions.Prove(null, item, work));
        });
        return api;
    }

    private static IResult Reply(ActionOutcome o)
    {
        var body = new JsonObject { ["ok"] = o.Ok, [o.Ok ? "message" : "error"] = o.Message };
        if (o.Data is not null && System.Text.Json.JsonSerializer.SerializeToNode(o.Data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) is JsonObject extra)
            foreach (var (k, v) in extra) body[k] = v?.DeepClone();
        return Results.Json(body, statusCode: o.Status);
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
    private static bool? Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
