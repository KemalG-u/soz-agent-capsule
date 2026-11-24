using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Registry;

namespace Soz.Core.Discovery;

/// <summary>
/// Keşif çekirdeğinin tek giriş noktası (uç noktalar ve komutlar buradan geçer). Tablolar henüz şemaya bağlanmamışsa
/// <see cref="Installed"/> false olur ve değişiklik/öneri çağrıları "Keşif henüz kurulmadı." diye 503 verir; arama tablo olmadan da çalışır (kullanım ağırlığı 0).
/// Yerleşimi yalnız <see cref="Place"/> ve <see cref="Clear"/> değiştirir, ikisi de kişinin açık eylemidir.
/// </summary>
public sealed class DiscoveryService
{
    public const string NotInstalled = "Keşif henüz kurulmadı.";

    private readonly SozDb _db;
    private readonly Lazy<RegistryLoadResult> _catalogLazy;
    private readonly Lazy<IReadOnlyList<IDiscoveryItemSource>> _sourcesLazy;
    private readonly IContextSource _context;
    private readonly Func<Moment> _clock;

    public DiscoveryService(SozDb db, RegistryLoadResult? catalog = null, IEnumerable<IDiscoveryItemSource>? sources = null, IContextSource? context = null, Func<Moment>? clock = null)
    {
        _db = db;
        // Kayıt defteri kurucuda değil ilk kullanımda alınır: gömülü kayıt bozuksa sunucu yine açılır, hata yalnız ona dokunan çağrıda sade cümleyle gelir.
        _catalogLazy = new Lazy<RegistryLoadResult>(() => catalog ?? RegistryHost.Load(db));
        _sourcesLazy = new Lazy<IReadOnlyList<IDiscoveryItemSource>>(() => (sources ?? new IDiscoveryItemSource[] { new RegistryItemSource(_catalogLazy.Value), new ProjectItemSource() }).ToList());
        _context = context ?? new LatestSessionContext();
        _clock = clock ?? Moment.Now;
    }

    public bool Installed() { using var c = _db.Open(); return DiscoverySchema.IsInstalled(c); }

    private DiscoveryParams Params(Microsoft.Data.Sqlite.SqliteConnection c)
        => DiscoveryParams.From(SozSettings.Load(c, _catalogLazy.Value));

    private void RequireInstalled(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        if (!DiscoverySchema.IsInstalled(c)) throw new SozException(503, NotInstalled);
    }

    private Dictionary<string, DiscoveryItem> Index(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var d = new Dictionary<string, DiscoveryItem>(StringComparer.Ordinal);
        foreach (var s in _sourcesLazy.Value) foreach (var it in s.Items(c)) d.TryAdd(it.Uid, it);
        return d;
    }

    private string? ProjectOrActive(Microsoft.Data.Sqlite.SqliteConnection c, string? projectUid)
        => string.IsNullOrWhiteSpace(projectUid) ? _context.ActiveProjectUid(c) : projectUid;

    // ---------- yerleşim ----------

    /// <summary>64 yuvanın tamamı: 8 sabit yön, her birinde 8 yuva, boşlar da görünür.</summary>
    public JsonObject Layout()
    {
        using var c = _db.Open();
        RequireInstalled(c);
        var items = Index(c);
        var placed = LayoutStore.All(c).ToDictionary(s => s.At.Index);
        var dirs = new JsonArray();
        foreach (var k in Directions.Kinds)
        {
            var slots = new JsonArray();
            for (var s = 0; s < Directions.SlotsPerKind; s++)
            {
                var at = new SlotAddress(k.Dir, s);
                placed.TryGetValue(at.Index, out var it);
                slots.Add(new JsonObject
                {
                    ["slot"] = s,
                    ["objectUid"] = it?.ObjectUid,
                    ["title"] = it is null ? null : items.GetValueOrDefault(it.ObjectUid)?.Title,
                    ["source"] = it is null ? null : it.Source == SlotSource.Fixed ? "fixed" : "user",
                    ["placedAt"] = it?.PlacedAt,
                });
            }
            dirs.Add(new JsonObject { ["dir"] = k.Dir, ["code"] = k.Code, ["title"] = k.Title, ["compass"] = k.Compass, ["slots"] = slots });
        }
        return new JsonObject { ["installed"] = true, ["total"] = Directions.TotalSlots, ["filled"] = placed.Count, ["directions"] = dirs };
    }

    /// <summary>Kişi bir yuvaya nesne koyar. Dolu yuvada `replace` olmadan 409 ve yuvadaki nesne bildirilir; hiçbir şey değişmez.</summary>
    public JsonObject Place(int kindDir, int slotDir, string? objectUid, bool replace)
    {
        using var c = _db.Open();
        RequireInstalled(c);
        var r = LayoutStore.Place(c, new SlotAddress(kindDir, slotDir), objectUid ?? "", replace);
        if (r.Ok) return new JsonObject { ["ok"] = true, ["kindDir"] = kindDir, ["slotDir"] = slotDir, ["objectUid"] = objectUid, ["replaced"] = r.Occupant?.ObjectUid };
        if (r.Occupant is not null) throw new SozException(409, r.Error + $" (Şu an: {r.Occupant.ObjectUid}.)");
        throw new SozException(400, r.Error ?? "Geçersiz yuva.");
    }

    public JsonObject Clear(int kindDir, int slotDir)
    {
        using var c = _db.Open();
        RequireInstalled(c);
        var at = new SlotAddress(kindDir, slotDir);
        if (!at.IsValid) throw new SozException(400, "Yön 0 ile 7 arasında olmalı (tür yönü ve yuva yönü).");
        return new JsonObject { ["ok"] = true, ["cleared"] = LayoutStore.Clear(c, at) };
    }

    // ---------- kullanım, öneri, susturma ----------

    public JsonObject RecordUsage(string? objectUid, string? projectUid, string? source)
    {
        if (string.IsNullOrWhiteSpace(objectUid)) throw new SozException(400, "objectUid gerekli.");
        using var c = _db.Open();
        RequireInstalled(c);
        UsageStore.Record(c, objectUid, ProjectOrActive(c, projectUid), _clock(), Params(c), string.IsNullOrWhiteSpace(source) ? "ui" : source!);
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>
    /// Öneriler. Gölge kipte `items` boştur ama hesap günlüğe yazılır. Her çağrı bir ölçüm grubu açar (batchId); bu yüzden POST'tur.
    /// </summary>
    public JsonObject Suggestions(string? projectUid, int? limit, bool forPage = false)
    {
        using var c = _db.Open();
        RequireInstalled(c);
        var items = Index(c);
        var r = SuggestionService.Suggest(c, ProjectOrActive(c, projectUid), _clock(), Params(c), limit, forPage);
        var arr = new JsonArray();
        foreach (var s in r.Items)
            arr.Add(new JsonObject
            {
                ["objectUid"] = s.ObjectUid, ["title"] = items.GetValueOrDefault(s.ObjectUid)?.Title ?? s.ObjectUid, ["kindDir"] = items.GetValueOrDefault(s.ObjectUid)?.KindDir,
                ["score"] = Math.Round(s.Score, 4), ["reason"] = s.Reason, ["reasonCode"] = s.ReasonCode,
            });
        // İlk gün: formül kullanım biriktirmeden öneri üretmez. Kurulu ama kapalı gelen araç ise gerçek bir dayanaktır (Seni tanıyorum bulgusu): ayrı alan, gölge kipten bağımsız.
        var firstDay = FirstDay(c, items, ProjectOrActive(c, projectUid));
        var res = new JsonObject { ["batchId"] = r.BatchUid, ["shadow"] = r.Shadow, ["items"] = arr, ["firstDay"] = firstDay };
        if (arr.Count == 0 && firstDay.Count == 0)
        {
            res["emptyText"] = "Henüz öneri yok. Söz birkaç gün neyi sık kullandığına bakar, sonra burada önerir.";
            if (r.Nearest is { } n)
            {
                var title = items.GetValueOrDefault(n.ObjectUid)?.Title ?? n.ObjectUid;
                res["nearest"] = new JsonObject { ["objectUid"] = n.ObjectUid, ["title"] = title, ["count"] = n.Count, ["needed"] = n.Needed, ["text"] = $"{title}: {n.Count} kez kullandın. {(n.Needed == 1 ? "Bir kez" : n.Needed + " kez")} daha kullanırsan öneririm." };
            }
        }
        return res;
    }

    /// <summary>
    /// Tanışmada kurulu bulunan ama bağlayıcısı henüz onay beklediği için kapalı olan araçlar ("Hermes kurulu görünüyor ama kapalı. Açayım mı?").
    /// Dayanak: Seni tanıyorum taramasının "kurulu" bulgusu, kapalı olduğu da kayıt defterinden. Susturulan (ya da yok sayılan) araç gelmez. Bulgu yoksa liste boştur, uydurma öneri yoktur.
    /// </summary>
    private JsonArray FirstDay(Microsoft.Data.Sqlite.SqliteConnection c, Dictionary<string, DiscoveryItem> items, string? project)
    {
        var res = new JsonArray();
        var p = Params(c);
        var dismissed = DismissStore.Load(c, project, ActiveDays.Current(c, _clock().Local), p);
        var used = UsageStore.Weights(c, project, _clock(), p);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Soz.Core.Onboarding.SuggestionStore.List(c, Soz.Core.Onboarding.OnboardingService.KindPrefix, includeDismissed: false))
        {
            if ((string?)row.Body["group"] != Soz.Core.Onboarding.FindingGroups.Tools) continue;
            if (row.Body["present"] is JsonValue pv && pv.TryGetValue<bool>(out var present) && !present) continue;
            if ((string?)row.Body["confidence"] != Soz.Core.Onboarding.Confidence.Installed) continue;
            var tool = (row.Body["facts"] as JsonObject)?["tool"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
            if (tool is null) continue;
            var uid = "connector:" + tool;
            if (!seen.Add(uid) || dismissed.Blocked.Contains(uid) || dismissed.Penalty.GetValueOrDefault(uid) > 0 || used.ContainsKey(uid)) continue;
            var e = _catalogLazy.Value.Get(uid);
            if (e is null || e.Owner != RegistryOwners.Fixed || (!RegistryHost.AwaitsApproval(e) && e.State != RegistryStates.Disabled)) continue;
            var title = items.GetValueOrDefault(uid)?.Title ?? e.Title.Tr;
            res.Add(new JsonObject
            {
                ["objectUid"] = uid, ["title"] = title, ["kindDir"] = items.GetValueOrDefault(uid)?.KindDir, ["score"] = 0,
                ["reason"] = $"{title} kurulu görünüyor ama kapalı. Açayım mı?", ["reasonCode"] = "installed-closed",
            });
        }
        return res;
    }

    public JsonObject Dismiss(string? objectUid, string? projectUid, string? mode, bool restore)
    {
        if (string.IsNullOrWhiteSpace(objectUid)) throw new SozException(400, "objectUid gerekli.");
        if (DismissStore.ParseMode(mode) is not { } m) throw new SozException(400, "mode: ignored, never-here ya da never olmalı.");
        using var c = _db.Open();
        RequireInstalled(c);
        var project = string.IsNullOrWhiteSpace(projectUid) ? null : projectUid;
        if (m == DismissMode.NeverHere && project is null) project = _context.ActiveProjectUid(c);
        if (m == DismissMode.NeverHere && project is null) throw new SozException(400, "\"Burada önerme\" için projectUid gerekli (etkin proje bulunamadı).");
        if (restore) return new JsonObject { ["ok"] = true, ["restored"] = DismissStore.Restore(c, objectUid, project, m, _clock()) };
        DismissStore.Dismiss(c, objectUid, project, m, _clock(), Params(c));
        return new JsonObject { ["ok"] = true };
    }

    public JsonObject ShadowReport(int? days)
    {
        using var c = _db.Open();
        RequireInstalled(c);
        var d = Math.Clamp(days ?? 30, 1, 365);
        var s = ShadowLog.Stats(c, d, _clock());
        return new JsonObject
        {
            ["days"] = s.Days, ["batches"] = s.Batches, ["batchesWithSuggestion"] = s.BatchesWithSuggestion, ["resolved"] = s.Resolved,
            ["resolvedWithSuggestion"] = s.ResolvedWithSuggestion, ["hits"] = s.Hits, ["hitsAtRank1"] = s.HitsAtRank1, ["hitRate"] = s.HitRate, ["rank1Rate"] = s.Rank1Rate, ["shown"] = s.Shown,
            ["note"] = "İsabet = kişinin kısa süre sonra seçtiği şey önerilenler arasındaydı. Formülün sayıları ölçülmedi. Gölge kip bu yüzden açık.",
        };
    }

    // ---------- arama ----------

    public JsonObject Search(string? query, string? projectUid, int? limit)
    {
        using var c = _db.Open();
        var items = Index(c).Values.ToList();
        Func<string, double> weightOf = _ => 0;
        var installed = DiscoverySchema.IsInstalled(c);
        if (installed)
        {
            var p = Params(c);
            var w = UsageStore.Weights(c, ProjectOrActive(c, projectUid), _clock(), p);
            weightOf = uid => w.TryGetValue(uid, out var x) ? x.Fw(p) : 0;
        }
        var hits = SearchRanker.Rank(query, items, weightOf, Math.Clamp(limit ?? 20, 1, 100));
        var arr = new JsonArray();
        foreach (var h in hits)
            arr.Add(new JsonObject { ["objectUid"] = h.Item.Uid, ["title"] = h.Item.Title, ["kindDir"] = h.Item.KindDir, ["description"] = h.Item.Description, ["match"] = h.Tier.ToString(), ["weight"] = Math.Round(h.Weight, 4) });
        return new JsonObject { ["installed"] = installed, ["query"] = query ?? "", ["items"] = arr };
    }
}
