using System.Text;
using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Run;

namespace Soz.Server.Endpoints;

/// <summary>
/// A uçları: iş kartı, ölçütler, kanıt kapısı, kuyruk, proje kapısı, sabah raporu, Bugün (FABLE-VIZYON-2 bölüm 5).
/// Güvenlik SozServer ara katmanından gelir (Host, köken, X-Soz-Token). SSE olayları events tablosu yoklanarak yayınlanır
/// (kancalar ayrı süreçte çalıştığı için tek yol budur).
/// </summary>
public static class JobsProofEndpoints
{
    public static RouteGroupBuilder MapJobsProof(this RouteGroupBuilder api, SozService service, EventHub hub, RunManager runs)
    {
        var work = new WorkService(service.Db) { Actor = "api" };
        Soz.Core.Proof.VerdictRun.SweepOld(service.Db.DataDir);
        StartLivePump(((IEndpointRouteBuilder)api).ServiceProvider, service.Db, hub);

        // ---------- ajan araçları (bağlayıcılar): bulunanlar, sürüm, yetenekler, hazır mı ----------
        api.MapGet("/agents", async () => Results.Json(new { defaultAgent = SozSettings.Load(service.Db).DefaultAgent, agents = await Task.Run(() => Soz.Core.Agents.AgentProbe.All(SozSettings.Load(service.Db), true, Soz.Core.Registry.RegistryHost.Load(service.Db))) }));

        // Ön kontrol (k1): her ajan aracı için bulundu mu, sürüm, giriş yapılmış görünüyor mu. Gerçek istem gönderilmez, kimlik dosyasının içeriği okunmaz.
        api.MapGet("/agents/ready", async () => Results.Json(await Task.Run(() => Soz.Core.Run.AgentReadiness.Check(SozSettings.Load(service.Db), Soz.Core.Registry.RegistryHost.Load(service.Db)))));

        // ---------- işler ----------
        // Liste varsayılan olarak en yeni 50 işi verir (en çok 500). Cevap gövdesi eskisi gibi dizidir; toplam sayı ve "devamı var" bilgisi
        // başlıklarda gelir (X-Total-Count, X-Has-More). Gövdede de görmek isteyen ?envelope=1 ile {items, total, hasMore, limit, offset} alır.
        // Daha eski işler için ?offset= verilir.
        api.MapGet("/jobs", (HttpContext ctx, long? projectId, string? state, int? limit, int? offset, string? envelope) =>
        {
            var st = Empty(state);
            if (st is not null && !JobStates.All.Contains(st)) throw new SozException(400, "Geçersiz iş durumu.");
            var lim = Math.Clamp(limit ?? 50, 1, 500);
            var off = Math.Max(0, offset ?? 0);
            using var c = service.Db.Open();
            var total = JobStore.Count(c, projectId, st);
            var items = JobStore.List(c, projectId, st, lim, off);
            var more = off + items.Count < total;
            ctx.Response.Headers["X-Total-Count"] = total.ToString();
            ctx.Response.Headers["X-Has-More"] = more ? "true" : "false";
            if (envelope is "1" or "true") return Results.Json(new { items, total, hasMore = more, limit = lim, offset = off });
            return Results.Json(items);
        });
        api.MapPost("/jobs", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            // İş türü isteğe bağlı: yoksa "kod". Verilmişse izinli listede olmalı (büyük/küçük harf ve baş/son boşluk önemsiz).
            string? kind = null;
            if (b.ContainsKey("kind") && b["kind"] is not null)
            {
                kind = Str(b, "kind")?.Trim().ToLowerInvariant();
                if (kind is null || Array.IndexOf(JobKinds, kind) < 0)
                    throw new SozException(400, $"Bilinmeyen iş türü. Şunlardan biri olmalı: {string.Join(", ", JobKinds)}.");
            }
            var j = work.CreateJob(Long(b, "projectId") ?? throw new SozException(400, "projectId gerekli."), Str(b, "rawText") ?? "",
                Str(b, "source") ?? "yazi", Bool(b, "restate") ?? false, Bool(b, "night") ?? false, Longs(b, "checkIds"), Str(b, "title"), Str(b, "agent"), kind, Bool(b, "writeCheckFirst") ?? false);
            // İş metninde anahtar benzeri değer varsa kişi uyarılır (metin ajana ham gider). Cevaptaki metin alanları çıkış maskesinden geçer.
            var node = System.Text.Json.JsonSerializer.SerializeToNode(j, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();
            node["secretWarning"] = Soz.Core.Text.SecretFilter.WarningFor(Str(b, "rawText"));
            return Results.Json(node, statusCode: 201);
        });
        // Yazarken denetim: metin anahtar benzeri bir şey içeriyorsa uyarı cümlesi, yoksa null. Metin saklanmaz.
        api.MapPost("/jobs/text-check", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(new { secretWarning = Soz.Core.Text.SecretFilter.WarningFor(Str(b, "text")) });
        });
        api.MapGet("/jobs/{id:long}", (long id) => Results.Json(work.Job(id)));
        api.MapPatch("/jobs/{id:long}", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(work.PatchJob(id, Str(b, "state") ?? throw new SozException(400, "state gerekli (verildi ya da iptal).")));
        });

        // ---------- Söz ajanı (terminal yok): başlat, cevapla, durdur, onayla, zaman çizelgesi ----------
        api.MapPost("/jobs/{id:long}/start", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var agent = Str(b, "agent");
            EnsureConnectorInstalled(service.Db, work, id, agent);
            var run = runs.Start(id, Str(b, "model"), agent, Bool(b, "writeCheckFirst") ?? false);
            return Results.Json(new { run, job = work.Job(id) }, statusCode: 202);
        });
        api.MapPost("/jobs/{id:long}/reply", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            EnsureConnectorInstalled(service.Db, work, id, null);
            var run = runs.Reply(id, Str(b, "text") ?? "", Str(b, "model"));
            return Results.Json(new { run, job = work.Job(id) }, statusCode: 202);
        });
        // "Bu işe devam et": biten, durdurulan ya da karar bekleyen işin aynı oturumunu yeni tur olarak açar.
        api.MapPost("/jobs/{id:long}/continue", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            EnsureConnectorInstalled(service.Db, work, id, null);
            var run = runs.Continue(id, Str(b, "text"), Str(b, "model"));
            return Results.Json(new { run, job = work.Job(id) }, statusCode: 202);
        });
        // Kanıtı bozulmuş görünen işte kullanıcı kararı: accept=true "geçti (kabul ettim)", false "kaldı".
        api.MapPost("/jobs/{id:long}/accept", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var accept = Bool(b, "accept") ?? throw new SozException(400, "accept (true/false) gerekli.");
            return Results.Json(new { job = work.Accept(id, accept, CallerGate.Get(ctx, service.Db.DataDir), Str(b, "snapshotId"), requireSnapshot: true) });
        });
        // Ajanın bu işte eklediği testleri onaylı sınama kümesine kat (e2). Yalnız insan: ajan kendi testini yargıç yapamaz (CallerGate bütün POST uçlarında geçerli).
        // Gövde {scope:"added"|"edited", diffId}: gövdesiz çağrı "added". Değiştirilen onaylı testi kabul etmek fark özetinin kimliğini ister (e3).
        api.MapPost("/jobs/{id:long}/approve-new-tests", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(new { job = work.ApproveNewTests(id, Str(b, "scope"), Str(b, "diffId")) });
        });
        // Teklife "Hayır" (e3): sunucuda kayıtlı, olay defterine yazılır, aynı teklif geri gelmez. Yalnız insan.
        api.MapPost("/jobs/{id:long}/decline-new-tests", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(new { job = work.DeclineNewTests(id, Str(b, "scope")) });
        });
        // Farkı ajan da okuyabilir. Kabul için gereken "gördü" kaydını yalnız insan isteği yazar.
        api.MapGet("/jobs/{id:long}/test-offer", (long id, HttpContext ctx) => Results.Json(ReadTestOffer(ctx, service.Db, id)));
        api.MapPost("/jobs/{id:long}/stop", async (long id) =>
        {
            var stopped = await runs.StopAsync(id);
            return Results.Json(new { stopped, job = work.Job(id) });
        });
        // Eski "işi açtım" çağrısı: bakmak kapatmak değildir, artık satırı düşürmez (closed her zaman false). Gerçek "Gördüm" POST /api/attention/ack.
        api.MapPost("/jobs/{id:long}/seen", (long id) => { work.MarkSeen(id); return Results.Json(new { ok = true, closed = false }); });
        api.MapPost("/jobs/{id:long}/approve", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var allow = Bool(b, "allow") ?? throw new SozException(400, "allow (true/false) gerekli.");
            var run = runs.Approve(id, Long(b, "eventId") ?? 0, allow, Str(b, "model"));
            return Results.Json(new { run, job = work.Job(id) }, statusCode: 202);
        });
        api.MapGet("/jobs/{id:long}/timeline", (long id, int? detail) =>
        {
            var j = work.Job(id);
            return Results.Json(new { jobId = id, state = j.State, agentState = j.AgentState, run = j.Run, items = work.Timeline(id, detail == 1) });
        });

        // ---------- proje ekleme ve tek adım kurulum ----------
        api.MapPost("/projects", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            // Kullanıcı girdisi hataları (yol yok, reddedilen yer, klasör yok) 200 + {ok:false, error, code} döner: tarayıcı konsolu kırmızıya boyanmasın.
            // Yetki hataları (401/403/400 Host) ara katmanda kalır, buraya gelmez.
            var raw = Str(b, "path");
            if (string.IsNullOrWhiteSpace(raw)) return Results.Json(new { ok = false, error = "Klasör yolu gerekli.", code = "yol_gerekli" });
            if (WorkService.CheckProjectPath(raw, null, work.Db.DataDir) is { } bad) return Results.Json(new { ok = false, error = bad, code = "yol_reddedildi" });
            var full = raw.Trim().Trim('"');
            // Yazılan yol var olan bir DOSYA ise "klasör bulunamadı" denmez: kişi dosyanın yolunu yazmış, klasörün yolu gerekli.
            if (!Directory.Exists(full) && File.Exists(full)) return Results.Json(new { ok = false, error = FolderEndpoints.NotAFolder, code = "bu_dosya" });
            if (!Directory.Exists(full)) return Results.Json(new { ok = false, error = $"Bu klasör bulunamadı: {Path.GetFullPath(full)}", code = "klasor_yok" });
            var r = work.AddProject(raw);
            var node = System.Text.Json.JsonSerializer.SerializeToNode(r, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();
            node.Insert(0, "ok", true);
            return Results.Json(node, statusCode: r.Created ? 201 : 200);
        });
        api.MapPost("/projects/{id:long}/approve-setup", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var guard = b["guardTemplate"] is JsonValue gv && gv.TryGetValue<bool>(out var gb) ? (gb ? "temkinli" : null) : Str(b, "guardTemplate");
            return Results.Json(work.ApproveSetup(id, b.ContainsKey("checkIds") ? Longs(b, "checkIds") : null, Bool(b, "gate") ?? true, guard));
        });
        api.MapPost("/projects/{id:long}/hooks/install", (long id) =>
        {
            string exe;
            try { exe = runs.ResolveSozExe(); }
            catch (FileNotFoundException e) { throw new SozException(409, e.Message); }
            return Results.Json(work.InstallTerminalHooks(id, exe));
        });
        api.MapPost("/projects/{id:long}/hooks/uninstall", (long id) => Results.Json(new { removed = work.UninstallTerminalHooks(id) }));

        // ---------- ölçütler ----------
        api.MapGet("/projects/{id:long}/checks", (long id, long? jobId) => Results.Json(work.Checks(id, jobId)));
        api.MapPost("/projects/{id:long}/checks/suggest", (long id) => Results.Json(work.SuggestChecks(id)));
        api.MapGet("/projects/{id:long}/checks/templates", (long id) => Results.Json(work.CheckTemplateCatalog(id)));
        api.MapPost("/checks", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            // EK A: düz cümle şablonu; verilirse kind/spec/label yok sayılır, etiket kullanıcının cümlesidir.
            if (Str(b, "template") is { Length: > 0 } tpl)
            {
                var tc = work.AddCheckFromTemplate(Long(b, "projectId") ?? throw new SozException(400, "projectId gerekli."), Long(b, "jobId"), tpl,
                    b["values"] as JsonObject, (int?)Long(b, "timeoutS"), Bool(b, "enabled") ?? true);
                return Results.Json(tc, statusCode: 201);
            }
            var spec = b["spec"] as JsonObject ?? throw new SozException(400, "spec nesnesi gerekli.");
            var c = work.AddCheck(Long(b, "projectId") ?? throw new SozException(400, "projectId gerekli."), Long(b, "jobId"), Str(b, "kind") ?? "",
                Str(b, "label") ?? "", (JsonObject)spec.DeepClone(), (int?)Long(b, "timeoutS"), Bool(b, "enabled") ?? false);
            return Results.Json(c, statusCode: 201);
        });
        api.MapPatch("/checks/{id:long}", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(work.PatchCheck(id, Bool(b, "enabled"), Str(b, "label"), (b["spec"] as JsonObject)?.DeepClone() as JsonObject, (int?)Long(b, "timeoutS")));
        });
        // Ajanın yazdığı denemeye Hayır. CallerGate yalnız insana açar, karar sunucuda saklanır.
        api.MapPost("/checks/{id:long}/decline", (long id) => Results.Json(new { job = work.DeclineSuggestedCheck(id) }));
        api.MapDelete("/checks/{id:long}", (long id) => { work.DeleteCheck(id); return Results.Json(new { ok = true }); });

        // ---------- kanıt ----------
        api.MapPost("/projects/{id:long}/proof/run", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var runId = work.BeginManualRun(id, Long(b, "jobId"));
            if (Bool(b, "wait") == true) return Results.Json(await Task.Run(() => work.CompleteRun(runId)));
            _ = Task.Run(() =>
            {
                try { work.CompleteRun(runId); }
                catch (Exception e) { try { using var c = service.Db.Open(); c.Exec("UPDATE proof_runs SET verdict='error', summary=@s, finished_at=@n WHERE id=@id AND verdict IS NULL", null, ("@s", "Çalıştırıcı hatası: " + e.Message), ("@n", Time.NowIso()), ("@id", runId)); } catch (Exception) { } }
            });
            return Results.Json(new { runId }, statusCode: 202);
        });
        api.MapGet("/proof/runs", (long? projectId, long? jobId, int? limit) => Results.Json(work.Runs(projectId, jobId, limit ?? 20)));
        api.MapGet("/proof/runs/{id:long}", (long id) => Results.Json(work.Run(id)));
        api.MapGet("/proof/artifact/{runId:long}/{name}", (long runId, string name) =>
        {
            var path = work.ArtifactPath(runId, name);
            if (path is null) return Results.Json(new { error = "Deneme sonucu dosyası bulunamadı." }, statusCode: 404);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".txt" or ".log" or ".md" or ".csv")
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                return Results.Text(Soz.Core.Text.SecretFilter.MaskSecrets(text), "text/plain; charset=utf-8", Encoding.UTF8);
            }
            if (ext == ".json")
            {
                var bytes = File.ReadAllBytes(path);
                return Results.Bytes(OutputMask.MaskJson(bytes), "application/json; charset=utf-8");
            }
            var type = ext switch { ".png" => "image/png", _ => "application/octet-stream" };
            return Results.File(path, type);
        });

        // ---------- kuyruk ----------
        api.MapGet("/queue", (long? projectId, long? sessionId, bool? pending) => Results.Json(work.Queue(projectId, sessionId, pending ?? false)));
        api.MapPost("/queue", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            return Results.Json(work.Enqueue(Long(b, "projectId"), Long(b, "sessionId"), Str(b, "text") ?? "", Str(b, "origin") ?? "panel"), statusCode: 201);
        });

        // ---------- proje kapısı ----------
        api.MapGet("/projects/{id:long}", (long id) => Results.Json(work.ProjectGate(id)));
        api.MapPatch("/projects/{id:long}", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var g = Bool(b, "gateEnabled") ?? throw new SozException(400, "gateEnabled (true/false) gerekli.");
            return Results.Json(work.SetGate(id, g));
        });

        // ---------- sabah / Bugün ----------
        api.MapGet("/morning", (string? since) =>
        {
            DateTime? s = null;
            if (!string.IsNullOrWhiteSpace(since)) s = Time.Parse(since) ?? throw new SozException(400, "since ISO tarih olmalı.");
            return Results.Json(work.Morning(s));
        });
        api.MapGet("/now", () => Results.Json(work.Now()));
        return api;
    }

    /// <summary>
    /// Canlı olayları (job/proof/queue/session ... ve nabızlar) SSE'ye aktarır (300 ms). Anlamlı olaylar event_log'dan seq'iyle ("id:"), nabızlar events tablosundan "id:" olmadan gelir
    /// (bkz. <see cref="LiveFeed.Poll"/>). Sunucu kapanınca durur.
    /// </summary>
    private static void StartLivePump(IServiceProvider sp, SozDb db, EventHub hub)
    {
        var lifetime = sp.GetService<IHostApplicationLifetime>();
        var stop = lifetime?.ApplicationStopping ?? CancellationToken.None;
        LiveFeed.Cursor cursor;
        try { using var c = db.Open(); cursor = LiveFeed.Start(c); }
        catch (Exception) { cursor = new LiveFeed.Cursor(); }
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(300, stop);
                    using var c = db.Open();
                    LiveFeed.Poll(c, cursor, (name, payload, seq) => hub.Publish(name, payload, seq));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* yoklama hatası sunucuyu düşürmez */ }
            }
        });
    }

    /// <summary>POST /api/jobs gövdesindeki isteğe bağlı "kind" için izinli iş türleri (yoksa "kod"). Doğrulama burada, sütun serbest metindir.</summary>
    public static readonly string[] JobKinds = { "kod", "yazi", "arastirma", "tablo", "tarayici", "dosya-duzeni" };

    /// <summary>POST kabul kapısıyla aynı çağıran denetimi. Ajan ve bilinmeyen çağıran yalnız okur.</summary>
    public static TestOfferDto ReadTestOffer(HttpContext ctx, SozDb db, long id)
    {
        var human = CallerGate.Get(ctx, db.DataDir).Kind == Soz.Core.Security.CallerKind.Human;
        return new WorkService(db).TestOffer(id, human);
    }

    private static void EnsureConnectorInstalled(SozDb db, WorkService work, long jobId, string? explicitAgent)
    {
        if (!string.IsNullOrWhiteSpace(explicitAgent))
            _ = Soz.Core.Agents.AgentRegistry.Get(explicitAgent);
        JobDto? job;
        using (var c = db.Open()) job = JobStore.Get(c, jobId);
        if (job is null) return;
        var agentId = !string.IsNullOrWhiteSpace(explicitAgent) ? explicitAgent : job.Agent;
        var connector = Soz.Core.Agents.AgentRegistry.Get(agentId);
        if (!Soz.Core.Agents.AgentRegistry.IsOpenIn(connector.Id, () => Soz.Core.Registry.RegistryHost.Load(db)))
            throw new SozException(409, $"{connector.DisplayName} henüz açık değil. Ayarlar sayfasından açtıktan sonra işi başlatabilirsin.");
        var settings = SozSettings.Load(db);
        var loc = connector.Locate(settings);
        if (loc.Cmd is null)
            throw new SozException(409, loc.Error ?? $"{connector.DisplayName} bu bilgisayarda bulunamadı. Önce kurup ya da Ayarlar'dan yolunu gösterip yeniden dene.");
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

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
    private static List<long> Longs(JsonObject o, string k)
    {
        if (o[k] is null) return new();
        if (o[k] is not JsonArray arr) throw new SozException(400, $"{k} sayı dizisi olmalı.");
        return arr.Select(x => x is JsonValue v && v.TryGetValue<long>(out var l) ? l : throw new SozException(400, $"{k} sayı dizisi olmalı.")).ToList();
    }
}
