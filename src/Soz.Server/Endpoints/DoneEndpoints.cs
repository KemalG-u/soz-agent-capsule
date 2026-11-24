using System.Text;
using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;

namespace Soz.Server.Endpoints;

/// <summary>
/// "Bitti'nin tanımı" uçları (d2a): üç sade soru, kural tabanlı kanıt önerisi, öneriyi kabul/değiştirme, insan onay listesi ve güven etiketi sözlüğü.
/// Okuma uçları (GET) ajana açıktır, yazma uçları (POST: kabul, işaretleme) CallerGate gereği yalnız insana: kabul ve onay ajanın işi değildir.
/// </summary>
public static class DoneEndpoints
{
    public static RouteGroupBuilder MapDone(this RouteGroupBuilder api, SozService service)
    {
        var work = new WorkService(service.Db) { Actor = "api" };

        api.MapGet("/done/questions", () => Results.Json(new { questions = DoneDefinition.Questions() }));
        api.MapGet("/done/levels", () => Results.Json(new
        {
            levels = DoneDefinition.Levels().Select(l => new { level = l.Level, label = l.Label, meaning = l.Meaning }),
            note = "Ölçülen gerçek (A, B) ile yardımcının özeti (C) ve senin onayın (D) ayrı gösterilir. Hiçbiri ötekinin yerine geçmez.",
        }));

        api.MapGet("/done/suggest", (long? projectId, long? jobId, string? kind, string? text, bool? irreversible) =>
            Results.Json(Suggest(service, work, projectId, jobId, kind, text, irreversible ?? false)));
        api.MapPost("/done/suggest", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(Suggest(service, work, Long(b, "projectId"), Long(b, "jobId"), Str(b, "kind"), Str(b, "text"), Bool(b, "irreversible") ?? false));
        });

        // Öneriyi kabul et ya da değiştirerek kabul et: items[].template + values (kişinin yazdığı ya da önerilen) + enabled.
        // Önce HEPSİ denenir (hiçbiri kaydedilmeden), biri hatalıysa hiçbiri eklenmez.
        api.MapPost("/done/accept", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var projectId = Long(b, "projectId") ?? throw new SozException(400, "projectId gerekli.");
            var jobId = Long(b, "jobId");
            if (b["items"] is not JsonArray arr || arr.Count == 0) throw new SozException(400, "items boş olamaz: kabul edilecek en az bir madde gerekli.");
            if (arr.Count > 30) throw new SozException(400, "Bir seferde en çok 30 madde kabul edilir.");
            var root = ProjectRoot(service, projectId);
            var items = arr.OfType<JsonObject>().ToList();
            if (items.Count != arr.Count) throw new SozException(400, "items içindeki her madde bir nesne olmalı.");
            for (var i = 0; i < items.Count; i++)
            {
                var tpl = Str(items[i], "template") ?? throw new SozException(400, $"items[{i}].template gerekli.");
                try { CheckTemplates.Build(tpl, items[i]["values"] as JsonObject, root); }
                catch (SozException e) { throw new SozException(e.Status, $"{i + 1}. madde: {e.Message}"); }
            }
            // k1: kapsam. scope "job" (jobId varsa VARSAYILAN) ölçütü yalnız o işe bağlar; "project" klasörde kalıcı yapar.
            // jobId ve scope göndermeyen eski istemci: eski davranış, proje düzeyinde kalıcı (legacy=true).
            var (created, scope, legacy) = work.AcceptDone(projectId, jobId, Str(b, "scope"), items.Select(it => (Str(it, "template")!, it["values"] as JsonObject, Bool(it, "enabled") ?? true)).ToList());
            var sentence = scope == "job" ? "Bu iş için eklendi. Sonraki işlere yapışmaz." : "Bu klasörde kalıcı olarak eklendi. Sonraki işlerde de geçerli.";
            return Results.Json(new { created, count = created.Count, scope, legacy, sentence }, statusCode: 201);
        });

        // Kart ne zaman çıkmalı (k1): iş başlatmadan önce tek çağrı. Tam kart yalnız ilk işte ya da kalıcı ölçüt yokken; aksi halde arayüz tek satır özet (line) gösterir.
        api.MapGet("/done/summary", (long? projectId, long? project, long? jobId) =>
        {
            var pid = projectId ?? project ?? throw new SozException(400, "projectId gerekli.");
            ProjectRoot(service, pid);
            using var c = service.Db.Open();
            return Results.Json(DoneSummary.Build(c, pid, jobId));
        });

        // İnsan onay listesi: durum + işaretleme (işaret işe bağlı; ajan işe yeniden başlayınca düşer).
        api.MapGet("/checks/{id:long}/checklist", (long id, long? jobId) =>
        {
            using var c = service.Db.Open();
            return Results.Json(HumanChecklistStore.Get(c, id, jobId));
        });
        api.MapPost("/jobs/{jobId:long}/checklist/{checkId:long}/tick", async (long jobId, long checkId, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var item = Long(b, "item") ?? throw new SozException(400, "item gerekli (madde numarası, 0'dan başlar).");
            var ticked = Bool(b, "ticked") ?? true;
            using var c = service.Db.Open();
            return Results.Json(HumanChecklistStore.Tick(c, checkId, jobId, (int)item, ticked, "human"));
        });
        return api;
    }

    private static string? ProjectRoot(SozService service, long projectId)
    {
        using var c = service.Db.Open();
        var path = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
        if (path is null && c.Scalar("SELECT id FROM projects WHERE id=@p", null, ("@p", projectId)) is null) throw new SozException(404, $"Proje bulunamadı: {projectId}");
        return path;
    }

    private static object Suggest(SozService service, WorkService work, long? projectId, long? jobId, string? kind, string? text, bool irreversible)
    {
        JobDto? job = null;
        if (jobId is not null)
        {
            job = work.Job(jobId.Value);
            projectId ??= job.ProjectId;
            kind ??= job.Kind;
            text ??= job.RawText;
        }
        var root = projectId is null ? null : ProjectRoot(service, projectId.Value);
        var k = kind?.Trim().ToLowerInvariant();
        if (k is not null && !DoneDefinition.KindLabels.ContainsKey(k))
            throw new SozException(400, $"Bilinmeyen iş türü. Şunlardan biri olmalı: {string.Join(", ", DoneDefinition.KindLabels.Keys)}.");
        return DoneDefinition.Suggest(root, k, text, irreversible);
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
    private static bool? Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
