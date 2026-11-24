using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Registry;
using Soz.Core.Text;

namespace Soz.Core.Onboarding;

/// <summary>Bir keşif koşusunun sayısal özeti (kişiye "x yeni, y değişmedi" diye söylenir).</summary>
public sealed record ScanOutcome(int Found, int New, int Updated, int Unchanged, int Gone, int KeptDismissed, int DroppedAsSecret, int Unreadable, IReadOnlyList<string> CouldNotRead, long Ms);

/// <summary>
/// "Seni tanıyorum": keşfi çalıştırır, bulguları `registry_suggestion` tablosuna ÖNERİ olarak yazar (sahip system, durum suggested) ve kişinin kararlarını işler.
/// Hiçbir bulgu kendiliğinden etkinleşmez: kabul, değiştirme ve "bir daha gösterme" kişi eylemidir, olay kaydına düşer (yük: yalnız kimlik ve grup, içerik ya da değer asla).
/// Aynı keşif ikinci kez koşunca satırlar çoğalmaz (kararlı kimlik), değişmeyen satıra dokunulmaz. "Bir daha gösterme" denmiş satır keşifte de yeniden açılmaz.
/// </summary>
public sealed class OnboardingService
{
    public const string IdPrefix = "tanisma:";
    public const string KindPrefix = "tanisma.";
    private const string UserActor = "kisi", SystemActor = "soz";

    private readonly SozDb _db;
    public OnboardingService(SozDb db) => _db = db;

    // ---------- Keşif ----------

    public ScanOutcome Scan(ScanContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var scan = OnboardingScanner.Run(ctx);
        var now = Time.NowIso();

        // Sızıntı kapısı: sır görünümlü metin içeren bulgu hiçbir yere yazılmaz.
        var ready = new List<(Finding F, JsonObject Body, JsonArray Evidence)>();
        var dropped = 0;
        foreach (var f in scan.Findings)
        {
            var body = ToBody(f, now);
            var ev = new JsonArray(f.Evidence.Select(e => (JsonNode?)new JsonObject { ["what"] = Clip(e.What), ["where"] = Clip(e.Where), ["when"] = e.When }).ToArray());
            if (Leaves(body).Concat(Leaves(ev)).Any(SecretFilter.ContainsSecret)) { dropped++; continue; }
            ready.Add((f, body, ev));
        }

        int created = 0, updated = 0, unchanged = 0, gone = 0, kept = 0;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var existing = SuggestionStore.List(c, KindPrefix, includeDismissed: true, tx).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (f, body, ev) in ready)
        {
            var id = IdPrefix + f.Id;
            seen.Add(id);
            if (!existing.TryGetValue(id, out var old))
            {
                SuggestionStore.Insert(c, tx, id, KindPrefix + f.Group, body, ev, now);
                created++;
                continue;
            }
            if (old.DismissedAt is not null) { kept++; continue; }
            // Kişinin kararı (state, userValue) ve ilk görülme korunur; yalnız bulgunun kendi alanları yenilenir.
            var merged = (JsonObject)body.DeepClone();
            merged["state"] = old.Body["state"]?.DeepClone() ?? "suggested";
            merged["userValue"] = old.Body["userValue"]?.DeepClone();
            merged["firstSeen"] = old.Body["firstSeen"]?.DeepClone() ?? now;
            if (JsonNode.DeepEquals(merged, old.Body) && JsonNode.DeepEquals(ev, old.Evidence)) { unchanged++; continue; }
            SuggestionStore.Update(c, tx, id, merged, ev);
            updated++;
        }
        foreach (var old in existing.Values)
        {
            if (seen.Contains(old.Id) || old.DismissedAt is not null) continue;
            if (!IsPresent(old.Body)) continue;
            var b = (JsonObject)old.Body.DeepClone();
            b["present"] = false;
            SuggestionStore.Update(c, tx, old.Id, b, old.Evidence);
            gone++;
        }

        var outcome = new ScanOutcome(ready.Count, created, updated, unchanged, gone, kept, dropped, scan.Unreadable, scan.CouldNotRead, sw.ElapsedMilliseconds);
        Events.Append(c, "kesif.bitti", "registry_suggestion", null, null, null, SystemActor, new JsonObject
        {
            ["found"] = outcome.Found, ["new"] = created, ["updated"] = updated, ["unchanged"] = unchanged, ["gone"] = gone, ["keptDismissed"] = kept,
            ["dropped"] = dropped, ["unreadable"] = scan.Unreadable, ["ms"] = outcome.Ms,
            ["couldNotRead"] = new JsonArray(scan.CouldNotRead.Select(s => (JsonNode?)s).ToArray()),
        }.ToJsonString(), tx);
        tx.Commit();
        return outcome;
    }

    private static JsonObject ToBody(Finding f, string now) => new()
    {
        ["owner"] = "system",
        ["state"] = "suggested",
        ["present"] = true,
        ["group"] = f.Group,
        ["title"] = Clip(f.Title),
        ["detail"] = f.Detail is null ? null : Clip(f.Detail),
        ["count"] = f.Count,
        ["confidence"] = f.Confidence,
        ["facts"] = f.Facts.DeepClone(),
        ["userValue"] = null,
        ["firstSeen"] = now,
    };

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300];

    private static IEnumerable<string> Leaves(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o: foreach (var kv in o) foreach (var s in Leaves(kv.Value)) yield return s; break;
            case JsonArray a: foreach (var x in a) foreach (var s in Leaves(x)) yield return s; break;
            case JsonValue v when v.TryGetValue<string>(out var str): yield return str; break;
        }
    }

    // ---------- Kabul edilen aracın açılması ----------

    private static string? ToolOf(SuggestionRow r) => GroupOf(r) == FindingGroups.Records ? (r.Body["facts"] as JsonObject)?["tool"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null : null;

    /// <summary>
    /// "Araçların kayıt klasörleri" maddesinin kabulü, o aracın KAPALI gelen gözle bağlayıcısını (ör. connector:hermes) açar; reddi ya da "bir daha gösterme" onayı geri alır.
    /// Bağlayıcı zaten açıksa (claude, codex) ya da araç için kapalı kayıt yoksa hiçbir şey olmaz. Cevaba bağlayıcının durumu eklenir.
    /// </summary>
    private JsonObject WithConnector(JsonObject item, SuggestionRow row, bool open)
    {
        var tool = ToolOf(row);
        if (tool is null) return item;
        var load = RegistryHost.Load(_db);
        var e = load.Get("connector:" + tool);
        if (e is null || e.Owner != RegistryOwners.Fixed) return item;
        if (open && (RegistryHost.AwaitsApproval(e) || e.State == RegistryStates.Disabled)) RegistryHost.Approve(_db, e.Id, UserActor);
        else if (!open && e.State == RegistryStates.Enabled && tool is not ("claude" or "codex")) RegistryHost.Revoke(_db, e.Id, UserActor);
        item["connector"] = new JsonObject { ["id"] = e.Id, ["state"] = RegistryHost.Load(_db).Get(e.Id)?.State };
        return item;
    }

    /// <summary>
    /// İçe aktarmanın okuyacağı araçlar: kişinin KABUL ettiği (ya da yolunu düzelttiği) "kayıt klasörü" maddeleri, araç → mutlak kök. "~" verilen ev klasörüne açılır.
    /// Bir araç buraya yalnız kişi kabul ederse girer; varsayılan gerçek klasör kendiliğinden açılmaz. Bağlayıcının açık olması ayrıca <see cref="RegistryHost"/> onayına bağlıdır.
    /// </summary>
    public Dictionary<string, string?> AcceptedObserveRoots(OnboardingRoots roots)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var c = _db.Open();
        foreach (var r in SuggestionStore.List(c, KindPrefix, includeDismissed: false))
        {
            if (ToolOf(r) is not { } tool || !Present(r)) continue;
            if ((string?)r.Body["state"] is not ("accepted" or "changed")) continue;
            var path = (string?)r.Body["userValue"] ?? ((r.Body["facts"] as JsonObject)?["root"] as JsonValue)?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path)) continue;
            d[tool] = Expand(roots, path);
        }
        return d;
    }

    /// <summary>"~" ile başlayan yolu ev klasörüne açar; başka yolu olduğu gibi bırakır.</summary>
    internal static string Expand(OnboardingRoots roots, string path)
    {
        if (path == "~") return roots.Home;
        return path.Length > 1 && path[0] == (char)126 && (path[1] == (char)92 || path[1] == (char)47) ? Path.Combine(roots.Home, path[2..]) : path;
    }

    // ---------- Okuma ----------

    public JsonObject View(bool includeDismissed = false)
    {
        using var c = _db.Open();
        var rows = SuggestionStore.List(c, KindPrefix, includeDismissed);
        var groups = new JsonArray();
        foreach (var (key, title) in FindingGroups.All)
        {
            var items = rows.Where(r => GroupOf(r) == key).ToList();
            var ordered = key == FindingGroups.Projects
                ? items.OrderByDescending(r => Fact(r, "lastDay")).ThenBy(r => r.Id, StringComparer.Ordinal)
                : items.OrderByDescending(Present).ThenBy(r => ConfidenceRank(r)).ThenBy(r => (string?)r.Body["title"] ?? "", StringComparer.CurrentCultureIgnoreCase);
            groups.Add(new JsonObject
            {
                ["key"] = key,
                ["title"] = title,
                ["count"] = items.Count(r => r.DismissedAt is null && Present(r)),
                ["items"] = new JsonArray(ordered.Select(r => (JsonNode?)Item(r)).ToArray()),
            });
        }
        var last = c.Query("SELECT ts, payload FROM event_log WHERE kind='kesif.bitti' ORDER BY seq DESC LIMIT 1", r => (Ts: r.GetString(0), Payload: r.GetString(1))).FirstOrDefault();
        var active = rows.Where(r => r.DismissedAt is null).ToList();
        return new JsonObject
        {
            ["notice"] = "Yalnız adlara, sürümlere ve yerlere baktım. İçeriklerine bakmadım, hiçbir şey göndermedim.",
            ["total"] = active.Count,
            ["pending"] = active.Count(r => (string?)r.Body["state"] == "suggested"),
            ["lastScan"] = last.Ts is null ? null : new JsonObject { ["at"] = last.Ts, ["result"] = JsonNode.Parse(last.Payload) },
            ["groups"] = groups,
        };
    }

    private static string GroupOf(SuggestionRow r) => (string?)r.Body["group"] ?? r.Kind[KindPrefix.Length..];
    private static bool Present(SuggestionRow r) => IsPresent(r.Body);
    private static bool IsPresent(JsonObject body) => body["present"] is JsonValue v && v.TryGetValue<bool>(out var p) ? p : true;
    private static string Fact(SuggestionRow r, string name) => (string?)(r.Body["facts"] as JsonObject)?[name] ?? "";
    private static int ConfidenceRank(SuggestionRow r) => (string?)r.Body["confidence"] switch { Confidence.Installed => 0, Confidence.Seen => 1, _ => 2 };

    private static string ConfidenceText(string? c) => c switch
    {
        Confidence.Installed => "Dosyasını gördüm, kurulu",
        Confidence.Trace => "İz var, kurulu olduğunu doğrulayamadım",
        Confidence.Seen => "Doğrudan gördüm",
        _ => "Bilinmiyor",
    };

    private static string StateOf(SuggestionRow r) => r.DismissedAt is not null ? "dismissed" : (string?)r.Body["state"] ?? "suggested";

    private static string StateText(string state) => state switch
    {
        "accepted" => "Onayladın",
        "changed" => "Sen düzelttin",
        "dismissed" => "Bir daha gösterilmeyecek",
        _ => "Önerildi, onayını bekliyor",
    };

    private static JsonObject Item(SuggestionRow r)
    {
        var state = StateOf(r);
        return new JsonObject
        {
            ["id"] = r.Id[IdPrefix.Length..],
            ["group"] = GroupOf(r),
            ["title"] = r.Body["title"]?.DeepClone(),
            ["detail"] = r.Body["detail"]?.DeepClone(),
            ["count"] = r.Body["count"]?.DeepClone(),
            ["confidence"] = r.Body["confidence"]?.DeepClone(),
            ["confidenceText"] = ConfidenceText((string?)r.Body["confidence"]),
            ["state"] = state,
            ["stateText"] = StateText(state),
            ["present"] = Present(r),
            ["userValue"] = r.Body["userValue"]?.DeepClone(),
            ["owner"] = "system",
            ["evidenceCount"] = r.Evidence.Count,
            ["firstSeen"] = r.Body["firstSeen"]?.DeepClone(),
            ["facts"] = r.Body["facts"]?.DeepClone(),
        };
    }

    // ---------- Kişi kararları ----------

    private SuggestionRow Require(SqliteConnection c, SqliteTransaction? tx, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new SozException(400, "Hangi madde olduğu (id) gerekli.");
        var row = SuggestionStore.Get(c, IdPrefix + id, tx);
        if (row is null || !row.Kind.StartsWith(KindPrefix, StringComparison.Ordinal)) throw new SozException(404, "Böyle bir madde yok.");
        return row;
    }

    private static void Record(SqliteConnection c, SqliteTransaction tx, string kind, SuggestionRow row)
        => Events.Append(c, kind, "registry_suggestion", null, null, null, UserActor, new JsonObject { ["id"] = row.Id[IdPrefix.Length..], ["group"] = GroupOf(row) }.ToJsonString(), tx);

    private static void RequireNotDismissed(SuggestionRow row)
    {
        if (row.DismissedAt is not null) throw new SozException(409, "Bu madde \"bir daha gösterme\" diye işaretli. Önce geri getir.");
    }

    /// <summary>Kabul: "evet, böyle". Bulunan değer olduğu gibi onaylanır (varsa daha önce yazdığın düzeltme silinir).</summary>
    public JsonObject Accept(string? id)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var row = Require(c, tx, id);
        RequireNotDismissed(row);
        row.Body["state"] = "accepted";
        row.Body["userValue"] = null;
        SuggestionStore.Update(c, tx, row.Id, row.Body, row.Evidence);
        Record(c, tx, "kullanici.onay", row);
        tx.Commit();
        return WithConnector(Item(row), row, open: true);
    }

    /// <summary>Değiştir: kişi kendi değerini yazar (ör. başka tarayıcı adı ya da yol). Değer yalnız burada saklanır; olay kaydına YAZILMAZ.</summary>
    public JsonObject Change(string? id, string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) throw new SozException(400, "Yeni değer boş olamaz.");
        if (v.Length > 260) throw new SozException(400, "Yeni değer en çok 260 karakter olabilir.");
        if (v.Any(char.IsControl)) throw new SozException(400, "Yeni değerde satır sonu ya da denetim karakteri olamaz.");
        if (SecretFilter.ContainsSecret(v)) throw new SozException(400, "Bu değer parola ya da anahtar gibi görünüyor, kaydetmem. Yalnız ad, yol ya da sürüm yaz.");
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var row = Require(c, tx, id);
        RequireNotDismissed(row);
        row.Body["state"] = "changed";
        row.Body["userValue"] = v;
        SuggestionStore.Update(c, tx, row.Id, row.Body, row.Evidence);
        Record(c, tx, "kullanici.duzeltme", row);
        tx.Commit();
        return WithConnector(Item(row), row, open: true);
    }

    /// <summary>Bir daha gösterme: kalıcı. Satır silinmez; keşif tekrar koşsa da geri açılmaz. <see cref="Restore"/> ile geri gelir.</summary>
    public JsonObject Dismiss(string? id)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var row = Require(c, tx, id);
        if (row.DismissedAt is null)
        {
            var at = Time.NowIso();
            SuggestionStore.SetDismissed(c, tx, row.Id, at);
            Record(c, tx, "kullanici.izleme-kapat", row);
            row = row with { DismissedAt = at };
        }
        tx.Commit();
        return WithConnector(Item(row), row, open: false);
    }

    /// <summary>Kapattığın maddeyi geri getirir (kapatmak silmek değildir).</summary>
    public JsonObject Restore(string? id)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var row = Require(c, tx, id);
        if (row.DismissedAt is not null)
        {
            SuggestionStore.SetDismissed(c, tx, row.Id, null);
            Record(c, tx, "kullanici.izleme-ac", row);
            row = row with { DismissedAt = null };
        }
        tx.Commit();
        return Item(row);
    }

    private static string EvidenceLine(JsonObject e)
    {
        var what = (string?)e["what"] ?? "";
        var where = (string?)e["where"];
        var when = (string?)e["when"];
        if (string.IsNullOrEmpty(where) || where == "—") return what;
        return string.IsNullOrEmpty(when) ? $"{what} ({where})" : $"{what} ({where}, {when})";
    }

    /// <summary>"Bunu nereden biliyorsun": maddenin kanıt listesi, sade cümlelerle. İçerik göstermez, çünkü içeriğe bakılmadı.</summary>
    public JsonObject Why(string? id)
    {
        using var c = _db.Open();
        var row = Require(c, null, id);
        var ev = new JsonArray(row.Evidence.Select(e => e?.DeepClone()).ToArray());
        var lines = row.Evidence.OfType<JsonObject>().Select(EvidenceLine).ToList();
        return new JsonObject
        {
            ["id"] = id,
            ["title"] = row.Body["title"]?.DeepClone(),
            ["confidenceText"] = ConfidenceText((string?)row.Body["confidence"]),
            ["answer"] = (lines.Count == 0 ? "Bunu bir yerden görmüştüm ama kanıtı kayıtlı değil." : "Şuralardan öğrendim: " + string.Join(", ", lines) + ".") + " Hiçbir dosyanın içeriğine bakmadım, hiçbir şey göndermedim.",
            ["evidence"] = ev,
        };
    }
}
