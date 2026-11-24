using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soz.Core;
using Soz.Core.Onboarding;
using Soz.Core.Profile;

namespace Soz.Server.Endpoints;

/// <summary>
/// "Seni tanıyorum" uçları (Tanışma). Keşif salt okunurdur; bulgular yalnız ÖNERİ olarak yazılır, kişi onaylamadan hiçbir şey etkinleşmez.
/// <code>
/// GET  /api/onboarding[?dismissed=1]  → { notice, total, pending, lastScan, groups:[{ key, title, count, items:[madde] }] }   (altı sabit grup, boş olsa da durur)
/// POST /api/onboarding/scan           → { outcome: { found, new, updated, unchanged, gone, keptDismissed, droppedAsSecret, unreadable, couldNotRead, ms }, view }
/// GET  /api/onboarding/why?id=tool:claude → { id, title, confidenceText, answer, evidence:[{ what, where, when }] }
/// POST /api/onboarding/accept   { id }           kabul (onay)
/// POST /api/onboarding/change   { id, value }    değiştir (kişinin kendi değeri)
/// POST /api/onboarding/dismiss  { id }           bir daha gösterme (kalıcı)
/// POST /api/onboarding/restore  { id }           kapattığını geri getir
/// madde: { id, group, title, detail, count, confidence, confidenceText, state (suggested|accepted|changed|dismissed), stateText, present, userValue, owner:"system", evidenceCount, firstSeen, facts }
/// </code>
/// Yazan uçlar (POST) X-Soz-Token ister (SozServer ara katmanı). Taramayı <see cref="ContextFactory"/> kurar: gerçek makinede kendi kökleri, testte sentetik ağaç.
/// </summary>
public static class OnboardingEndpoints
{
    /// <summary>Her taramada çağrılır. Varsayılan bu bilgisayarın gerçek bağlamıdır; test ve deneme sentetik bir bağlamla DEĞİŞTİRİR.</summary>
    public static Func<ScanContext> ContextFactory { get; set; } = ScanContext.ForThisMachine;

    public static RouteGroupBuilder MapOnboarding(this RouteGroupBuilder api, SozService service)
    {
        var svc = new OnboardingService(service.Db);
        var profile = service.Profile; // tek paylaşımlı örnek: canlı önbellek ve yoklama kuralları bekleyen listesiyle ortak
        api.MapProfile(profile);

        api.MapGet("/onboarding", (string? dismissed) => Results.Json(svc.View(dismissed is "1" or "true")));
        api.MapPost("/onboarding/scan", () =>
        {
            var context = ContextFactory();
            var snapshot = profile.Scan(context);
            context.ProfileProjects = snapshot.Projects;
            var outcome = svc.Scan(context);
            return Results.Json(new JsonObject { ["outcome"] = Outcome(outcome), ["view"] = svc.View(), ["profile"] = ProfileService.ToJson(snapshot) });
        });
        api.MapGet("/onboarding/why", (string? id) => Results.Json(svc.Why(id)));
        api.MapPost("/onboarding/accept", async (HttpContext ctx) => { var b = await Body(ctx); return Results.Json(svc.Accept(Str(b, "id"))); });
        api.MapPost("/onboarding/change", async (HttpContext ctx) => { var b = await Body(ctx); return Results.Json(svc.Change(Str(b, "id"), Str(b, "value"))); });
        api.MapPost("/onboarding/dismiss", async (HttpContext ctx) => { var b = await Body(ctx); return Results.Json(svc.Dismiss(Str(b, "id"))); });
        api.MapPost("/onboarding/restore", async (HttpContext ctx) => { var b = await Body(ctx); return Results.Json(svc.Restore(Str(b, "id"))); });
        return api;
    }

    private static JsonObject Outcome(ScanOutcome o) => new()
    {
        ["found"] = o.Found, ["new"] = o.New, ["updated"] = o.Updated, ["unchanged"] = o.Unchanged, ["gone"] = o.Gone, ["keptDismissed"] = o.KeptDismissed,
        ["droppedAsSecret"] = o.DroppedAsSecret, ["unreadable"] = o.Unreadable, ["ms"] = o.Ms,
        ["couldNotRead"] = new JsonArray(o.CouldNotRead.Select(s => (JsonNode?)s).ToArray()),
    };

    private static async Task<JsonObject> Body(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is 0) return new JsonObject();
        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var text = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? throw new SozException(400, "Gövde bir JSON nesnesi olmalı.");
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
