using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Soz.Core;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Usage;

namespace Soz.Server.Endpoints;

/// <summary>Bekçi (M4) ve Harcama (M8) uçları. Değişiklik uçları X-Soz-Token'ı SozServer ara katmanından alır.</summary>
public static class GuardUsageEndpoints
{
    public static void Map(RouteGroupBuilder api, SozService service, EventHub hub, IHostApplicationLifetime lifetime)
    {
        var db = service.Db;
        using (var c0 = db.Open()) { GuardStore.EnsureTables(c0); UsageIngest.EnsureTables(c0); }

        api.MapGet("/guard/rules", (long? projectId) =>
        {
            using var c = db.Open();
            var names = c.Query("SELECT id, name FROM projects", r => (r.GetInt64(0), r.Str(1))).ToDictionary(x => x.Item1, x => x.Item2);
            return Results.Json(GuardStore.List(c, projectId).Select(r => GuardStore.Dto(r, r.ProjectId is { } p && names.TryGetValue(p, out var n) ? n : null)));
        });
        api.MapPost("/guard/rules", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            using var c = db.Open();
            var rule = GuardStore.CreateFromBody(c, b);
            hub.Publish("guard", new { kind = "rule", id = rule.Id });
            return Results.Json(GuardStore.Dto(rule), statusCode: 201);
        });
        api.MapPatch("/guard/rules/{id:long}", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            using var c = db.Open();
            var rule = GuardStore.PatchFromBody(c, id, b);
            hub.Publish("guard", new { kind = "rule", id = rule.Id });
            return Results.Json(GuardStore.Dto(rule));
        });
        api.MapDelete("/guard/rules/{id:long}", (long id) =>
        {
            using var c = db.Open();
            if (!GuardStore.Delete(c, id)) throw new SozException(404, "Kural bulunamadı.");
            hub.Publish("guard", new { kind = "rule", id });
            return Results.Json(new { ok = true });
        });
        api.MapGet("/guard/templates", () => Results.Json(GuardTemplates.All.Select(t => new
        {
            id = t.Id, label = t.Label, description = t.Description,
            rules = t.Rules.Select(r => new { kind = r.Kind, label = r.Label, action = r.Action }),
        })));
        api.MapPost("/guard/templates/apply", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var pid = Long(b, "projectId") ?? throw new SozException(400, "projectId gerekli.");
            var tpl = Str(b, "template") ?? throw new SozException(400, "template gerekli (ör. temkinli).");
            using var c = db.Open();
            if (c.Scalar("SELECT id FROM projects WHERE id=@i", null, ("@i", pid)) is null) throw new SozException(404, "Proje bulunamadı.");
            var res = GuardStore.ApplyTemplate(c, pid, tpl);
            hub.Publish("guard", new { kind = "template", template = res.Template, created = res.Created.Count });
            return Results.Json(new { template = res.Template, created = res.Created.Select(r => GuardStore.Dto(r)), existing = res.Existing.Select(r => GuardStore.Dto(r)) });
        });
        api.MapGet("/guard/events", (int? days, long? projectId, int? limit) =>
        {
            using var c = db.Open();
            var d = days ?? 7;
            var items = GuardStore.Events(c, d, projectId, Math.Clamp(limit ?? 200, 1, 1000));
            return Results.Json(new
            {
                days = d, total = items.Count, deny = items.Count(x => x.Action == "deny"), ask = items.Count(x => x.Action == "ask"),
                items = items.Select(x => new { x.Id, x.At, x.ProjectId, x.ProjectName, sessionId = x.SessionId, x.Tool, target = x.Target, x.RuleId, x.RuleLabel, x.Action, x.Reason }),
            });
        });
        api.MapPost("/guard/test", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var tool = Str(b, "toolName") ?? "Bash";
            var r = GuardService.Test(db, Long(b, "projectId"), tool, b["toolInput"], Str(b, "cwd"));
            return Results.Json(new { verdict = r.Verdict, ruleId = r.RuleId, ruleLabel = r.RuleLabel, ruleKind = r.RuleKind, reason = r.Reason, unknown = r.Unknown, targets = r.Targets, note = r.Note });
        });
        api.MapGet("/usage", (int? days, string? tool) => Results.Json(UsageQuery.Build(db, days ?? 30, tool)));

        // Kanca ayrı süreçte çalışır: olayları sunucu tabloyu izleyerek SSE'ye çevirir.
        _ = Task.Run(() => WatchEvents(db, hub, lifetime.ApplicationStopping));
    }

    private static async Task WatchEvents(SozDb db, EventHub hub, CancellationToken ct)
    {
        long last;
        try { using var c = db.Open(); last = c.ScalarLong("SELECT COALESCE(MAX(id),0) FROM guard_events"); }
        catch (Exception) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            try
            {
                using var c = db.Open();
                var rows = c.Query("SELECT e.id, e.tool, e.action, e.target_summary, r.label FROM guard_events e LEFT JOIN guard_rules r ON r.id=e.rule_id WHERE e.id>@l ORDER BY e.id LIMIT 50",
                    r => new { id = r.GetInt64(0), tool = r.Str(1), action = r.Str(2), target = r.Str(3), rule = r.Str(4) }, null, ("@l", last));
                foreach (var row in rows) { hub.Publish("guard", new { kind = "event", row.id, row.tool, row.action, row.target, row.rule }); last = row.id; }
            }
            catch (Exception) { /* sonraki turda yeniden dene */ }
        }
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
    private static long? Long(JsonObject o, string k) => o[k] is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : null) : null;
}
