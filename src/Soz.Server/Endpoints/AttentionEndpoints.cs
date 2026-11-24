using System.Text;
using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Attention;

namespace Soz.Server.Endpoints;

/// <summary>
/// "Sıra bende": kişinin sırası gelen her şeyin tek akışı (kapsül, ön bakış, panel ve telefon aynı listeyi okur).
///
/// <c>GET /api/attention[?since=&lt;rev&gt;]</c> cevabı (alanlar camelCase):
/// <code>
/// { "rev": 812, "now": "2026-11-11T20:01:02.123Z", "sentence": "1 soru, 1 bitti seni bekliyor. 2 iş çalışıyor.",
///   "counts": { "ask":1, "accept":0, "blocked":0, "done":1, "uncertain":0, "total":2, "running":2 },
///   "budget": { "dailyLimit":3, "usedToday":2, "suppressedToday":0, "day":"2026-11-11" },
///   "items": [ { "id":"ask:job:0192...:2026-11-11T20:00:01.000Z", "kind":"ask", "reason":"question", "priority":1,
///                "sentence":"«Hesap makinesi» işinde ajan sana bir soru sordu.", "detail":"Hangisini seçeyim?", "title":"Hesap makinesi",
///                "jobUid":"0192...", "jobId":7, "sessionId":null, "projectName":"deneme", "at":"2026-11-11T20:00:01.000Z", "confidence":"firm",
///                "signal":true, "isNew":false,
///                "actions":[ { "id":"reply", "label":"Cevap yaz", "method":"POST", "path":"/api/jobs/7/reply", "body":{ "text":"" } } ] } ],
///   "running": [ { "jobUid":"0192...", "jobId":8, "title":"Rapor", "projectName":"deneme" } ] }
/// </code>
/// Öğe türleri öncelik sırasıyla: ask (soru), accept (kabul bekleyen kanıt), blocked (engel), done (bitti), uncertain (belirsiz: Söz emin değil, uyandırmaz).
/// <c>since</c> son bilinen rev'dir: ondan sonra ortaya çıkan öğeler isNew=true gelir (kopukken kaçırılanlar). <c>signal</c> bugünkü istem dışı işaret hakkının
/// bu öğeye verilip verilmediğidir (günde en çok ayardaki kadar, sayaç sunucuda); kapsül yalnız signal=true olan yeni öğeyle uyanır. Liste her durumda tamdır.
///
/// <c>POST /api/attention/ack</c> gövde <c>{ "id": "&lt;öğe kimliği&gt;" }</c>: "gördüm". Biten, kanıtsız kalan, kalan ya da başlatılamayan işi listeden düşürür (iş silinmez).
/// Cevap/karar bekleyen işte 409 (önce cevap), listede olmayan öğede 404. Yalnız insandan kabul edilir (X-Soz-Token).
///
/// Canlı akış (<c>GET /api/events/stream</c>): liste değişince <c>event: attention</c> yayınlanır, veri
/// <c>{ "rev":812, "sentence":"...", "counts":{...}, "budget":{...}, "newIds":["..."], "signalIds":["..."] }</c>. Olay yalnız bildirimdir; ayrıntı için GET çağrılır.
/// </summary>
public static class AttentionEndpoints
{
    /// <summary>Hiçbir olay gelmese de liste bu aralıkla (sn) yeniden hesaplanır: zamanla değişen durumlar (takılma, çalışan süreç kaybı) için.</summary>
    public const int RefreshSeconds = 5;


    public static RouteGroupBuilder MapAttention(this RouteGroupBuilder api, SozService service, EventHub hub)
    {
        var svc = service.Attention;
        // Dışarıdaki açık oturumların canlı durumu paylaşımlı profil motorundan okunur (state / waitingFor alanları yoksa kutuya girmez). Sessiz arka plan beslemesi:
        // "biri bakıyor" sayılmaz; yenileme aralığı uyarlanır (kimse bakmıyorken seyrek, bakarken sık), kanca haberiyle öne çekilir, üst üste binmez.
        var profile = service.Profile;
        var cadence = profile.Cadence;
        svc.External ??= new BackgroundExternalSessions(new ProfileExternalSessions(() => profile.View(null, () => OnboardingEndpoints.ContextFactory(), background: true)),
            interval: () => cadence.Interval(cadence.Now), wakeAt: () => cadence.WakeAt);
        api.MapSessionActions(service);
        var pump = new Pump(svc, hub);
        pump.Start(((IEndpointRouteBuilder)api).ServiceProvider);

        // Listeyi isteyen biri bakıyor demektir (arayüz, kapsül olay sonrası); kabuğun sessiz yoklaması X-Soz-Arka başlığıyla "bakan yok" kalır.
        api.MapGet("/attention", (HttpContext ctx, long? since) =>
        {
            if (!ctx.Request.Headers.ContainsKey(Soz.Core.Profile.LiveCadence.BackgroundHeader)) cadence.Watch();
            return Results.Json(svc.Snapshot(since));
        });
        api.MapPost("/attention/ack", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var id = b["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrWhiteSpace(id)) throw new SozException(400, "id gerekli (öğe kimliği).");
            var item = svc.Ack(id, "api");
            pump.Recompute(); // "gördüm" hub'a olay düşürmez: liste değişimini bekletmeden duyur
            return Results.Json(new { ok = true, id = item.Id, kind = item.Kind });
        });
        return api;
    }

    /// <summary>
    /// Olay güdümlü: hub'a gelen her canlı olaydan sonra (120 ms toplanarak) liste yeniden hesaplanır; imza değiştiyse "attention" olayı yayınlanır.
    /// Boştayken yalnız RefreshSeconds'ta bir uyanır. Kendi yayınladığı olaya tepki vermez (döngü olmaz). Recompute kilitlidir:
    /// döngüden ve (olay üretmeyen değişikliklerde, ör. "gördüm") uçtan aynı anda çağrılabilir.
    /// </summary>
    private sealed class Pump(AttentionService svc, EventHub hub)
    {
        private readonly object _gate = new();
        private string? _lastSig;
        private HashSet<string> _lastIds = new(StringComparer.Ordinal);

        public void Recompute()
        {
            try
            {
                lock (_gate)
                {
                    var s = svc.Snapshot();
                    var sig = AttentionService.Signature(s);
                    if (sig == _lastSig) return;
                    var ids = s.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                    var first = _lastSig is null;
                    _lastSig = sig;
                    var newIds = ids.Where(i => !_lastIds.Contains(i)).ToList();
                    _lastIds = ids;
                    if (first && ids.Count == 0) return; // ilk hesap boşsa söylenecek bir şey yok
                    hub.Publish("attention", new
                    {
                        rev = s.Rev, sentence = s.Sentence, counts = s.Counts, budget = s.Budget, newIds,
                        signalIds = s.Items.Where(i => i.Signal && newIds.Contains(i.Id)).Select(i => i.Id).ToList(),
                    });
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { /* hesap hatası sunucuyu düşürmez, sonraki turda yeniden denenir */ }
        }

        public void Start(IServiceProvider sp)
        {
            var stop = sp.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
            var reader = hub.Subscribe(out var unsubscribe);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var triggered = false;
                        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
                        {
                            wait.CancelAfter(TimeSpan.FromSeconds(RefreshSeconds));
                            try
                            {
                                while (true)
                                {
                                    var msg = await reader.ReadAsync(wait.Token);
                                    if (IsOwnOrNoise(msg)) continue; // kendi olayımız ve ilerleme nabzı (sık gelir, liste değiştirmez)
                                    triggered = true; break;
                                }
                            }
                            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { /* zaman aşımı: yine de yeniden hesapla */ }
                            catch (System.Threading.Channels.ChannelClosedException) { return; }
                        }
                        if (triggered) await Task.Delay(120, stop); // art arda gelen olaylar tek hesapla
                        while (reader.TryRead(out _)) { }
                        Recompute();
                    }
                }
                catch (OperationCanceledException) { }
                finally { unsubscribe(); }
            });
        }
    }

    /// <summary>İletinin olay adı "attention" (kendimiz) ya da "job.progress" (ajanın her çıktı satırı) mı? Biçim: ["id: N\n"]"event: ad\n".</summary>
    public static bool IsOwnOrNoise(string msg)
    {
        var i = msg.IndexOf("event: ", StringComparison.Ordinal);
        if (i < 0) return false;
        var end = msg.IndexOf('\n', i);
        var name = msg.AsSpan(i + 7, (end < 0 ? msg.Length : end) - i - 7).Trim();
        return name is "attention" || name is "job.progress";
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
