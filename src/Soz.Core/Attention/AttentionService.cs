using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;

namespace Soz.Core.Attention;

/// <summary>
/// Dikkat bütçesi defteri (ilke 6): günde en çok N istem dışı işaret. Sayaç SUNUCUDA durur (kapsül ya da panel sayamaz), ayarı kayıt defterindendir
/// (<c>attentionDailySignals</c>). Defter iç ayar anahtarında (<c>_attentionLedger</c>) tutulur: sunucu yeniden başlasa da aynı öğe için ikinci işaret verilmez.
/// Her öğe ilk görüldüğü anda bir kez değerlendirilir; karar öğe kimliğiyle saklanır, sonraki bakışlarda değişmez.
/// </summary>
public sealed class AttentionLedger
{
    public const string SettingKey = "_attentionLedger";
    /// <summary>Listede olmayan eski öğelerden bu kadarının kararı saklanır (defter şişmesin).</summary>
    public const int KeepOthers = 200;

    public string Day { get; private set; } = "";
    public int Used { get; private set; }
    public int Suppressed { get; private set; }
    private readonly List<string> _order = new();
    private readonly Dictionary<string, bool> _signals = new(StringComparer.Ordinal);

    public static AttentionLedger Load(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var l = new AttentionLedger();
        try
        {
            if (SozSettings.GetInternal(c, SettingKey) is { Length: > 0 } raw && JsonNode.Parse(raw) is JsonObject o)
            {
                l.Day = (string?)o["day"] ?? "";
                l.Used = (int?)o["used"] ?? 0;
                l.Suppressed = (int?)o["suppressed"] ?? 0;
                if (o["seen"] is JsonArray a)
                    foreach (var e in a)
                        if (e is JsonArray p && p.Count == 2 && (string?)p[0] is { } id) { l._order.Add(id); l._signals[id] = ((int?)p[1] ?? 0) == 1; }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { /* bozuk defter: boş başlar, uyarı yok */ }
        return l;
    }

    public void Save(Microsoft.Data.Sqlite.SqliteConnection c, IReadOnlyCollection<string> liveIds)
    {
        var live = new HashSet<string>(liveIds, StringComparer.Ordinal);
        var others = _order.Where(i => !live.Contains(i)).ToList();
        foreach (var drop in others.Take(Math.Max(0, others.Count - KeepOthers))) { _signals.Remove(drop); _order.Remove(drop); }
        var seen = new JsonArray();
        foreach (var id in _order) seen.Add(new JsonArray(id, _signals[id] ? 1 : 0));
        var o = new JsonObject { ["day"] = Day, ["used"] = Used, ["suppressed"] = Suppressed, ["seen"] = seen };
        SozSettings.SetInternal(c, SettingKey, o.ToJsonString());
    }

    /// <summary>Gün değiştiyse sayaç sıfırlanır; öğelerin eski kararı kalır (gece yarısı eski öğeler yeniden işaret doğurmaz).</summary>
    public bool Roll(string day)
    {
        if (Day == day) return false;
        Day = day; Used = 0; Suppressed = 0;
        return true;
    }

    /// <summary>
    /// Öğeleri öncelik sırasıyla değerlendirir ve Signal alanını doldurur. Belirsiz öğe asla işaret almaz. Kişinin bir şey denerken karşılaştığı engel
    /// (başlatılamadı) bütçesizdir. Öteki yeni öğeler günlük hak bitene kadar işaret alır. Dönüş: defter değişti mi.
    /// </summary>
    public bool Observe(List<AttentionItem> items, int dailyLimit)
    {
        var changed = false;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (_signals.TryGetValue(it.Id, out var known)) { items[i] = it with { Signal = known }; continue; }
            bool signal;
            if (it.Kind == AttentionKinds.Uncertain) signal = false;
            else if (it.Reason == AttentionKinds.StartFailedReason) signal = true;
            else if (Used < dailyLimit) { signal = true; Used++; }
            else { signal = false; Suppressed++; }
            _signals[it.Id] = signal; _order.Add(it.Id); changed = true;
            items[i] = it with { Signal = signal };
        }
        return changed;
    }
}

/// <summary>
/// "Sıra bende" tek kaynağı: kapsül, ön bakış, panel ve telefon aynı listeyi buradan okur. Okuma yan etkisizdir, yalnız dikkat defterine ilk görülen öğeyi işler.
/// </summary>
public sealed class AttentionService
{
    private readonly SozDb _db;
    private readonly object _lock = new();

    public AttentionService(SozDb db) => _db = db;

    /// <summary>Dışarıdaki açık oturumların canlı durumu (profil motoru bağlar). Boşsa kutu yalnız kanca ve kayıttan beslenir.</summary>
    public IExternalSessionSource? External { get; set; }

    // "Seni bekliyor" dedikleri andan beri durumun başladığı an (dış kaynak başlangıç vermezse ilk görüldüğü an). Durum değişince silinir.
    private readonly Dictionary<string, DateTime> _extWaitingSince = new(StringComparer.Ordinal);

    private (IReadOnlyList<ExternalSession> rows, Func<ExternalSession, DateTime> since) ReadExternal(DateTime now)
    {
        IReadOnlyList<ExternalSession> rows = Array.Empty<ExternalSession>();
        try { rows = External?.Snapshot() ?? rows; }
        catch (Exception e) when (e is not OperationCanceledException) { /* kaynak patlarsa kutu kanca ve kayıtla devam eder */ }
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => r.State == ExternalStates.WaitingForYou))
        {
            var k = r.Tool.ToLowerInvariant() + "|" + r.SessionId;
            live.Add(k);
            if (!_extWaitingSince.ContainsKey(k)) _extWaitingSince[k] = r.SinceUtc ?? now;
        }
        foreach (var k in _extWaitingSince.Keys.Where(k => !live.Contains(k)).ToList()) _extWaitingSince.Remove(k);
        return (rows, r => _extWaitingSince.TryGetValue(r.Tool.ToLowerInvariant() + "|" + r.SessionId, out var t) ? t : (r.SinceUtc ?? now));
    }

    /// <summary>Yerel takvim günü (sayaç yerel gece yarısında sıfırlanır).</summary>
    public static string LocalDay(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <param name="sinceRev">İstemcinin son bildiği sürüm (rev). Verilirse ondan sonra ortaya çıkan öğeler isNew işaretlenir (kopukken kaçırılanlar).</param>
    public AttentionSnapshot Snapshot(long? sinceRev = null, DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            var now = nowUtc ?? Time.UtcNow();
            using var c = _db.Open();
            var st = SozSettings.Load(c);
            var (ext, extSince) = ReadExternal(now);
            var col = AttentionBuilder.Collect(c, now, st, ext, extSince);
            var rev = Events.LastSeq(c);
            var ledger = AttentionLedger.Load(c);
            var limit = st.ClampedInt("attentionDailySignals");
            var rolled = ledger.Roll(LocalDay(now));
            var items = col.Items;
            // C9: başlatılamayan işin cümlesi Söz'ün kendi notudur (yardımcı hiç başlamadı, "yardımcının sözü" değil): Detail'den SozNotu'ya taşınır.
            for (var i = 0; i < items.Count; i++)
                if (items[i].Reason == AttentionKinds.StartFailedReason && !string.IsNullOrWhiteSpace(items[i].Detail))
                    items[i] = items[i] with { Detail = null, SozNotu = items[i].Detail };
            // C10: iş cümleleri (Attention yapıcısının kendi sözcükleri) kullanıcıya sade sözcüklerle gider; cümle ile ayrıntının tekrarı yeni sözcüklerle yeniden ayıklanır.
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Source != "job") continue;
                var sentence = Soz.Core.Text.UserWords.Simplify(it.Sentence);
                var ownDetail = it.Reason is "passed" or "no_change" or "proof_failed" or "unproven"; // yalnız Söz'ün kendi özeti; yardımcının sözü (soru, hata) olduğu gibi kalır
                var detail = it.Detail is null ? null : ownDetail ? Soz.Core.Text.UserWords.Simplify(it.Detail) : it.Detail;
                if (detail is not null)
                {
                    var prefix = $"«{it.Title}» için ";
                    var body = sentence.StartsWith(prefix, StringComparison.Ordinal) ? sentence[prefix.Length..] : sentence;
                    if (detail.StartsWith(body, StringComparison.OrdinalIgnoreCase)) detail = detail[body.Length..].Trim();
                    if (detail.Length == 0) detail = null;
                }
                if (sentence != it.Sentence || detail != it.Detail) items[i] = it with { Sentence = sentence, Detail = detail };
            }
            var changed = ledger.Observe(items, limit);
            if (changed || rolled) ledger.Save(c, items.Select(i => i.Id).ToList());

            if (sinceRev is { } since)
            {
                string? sinceTs = since >= rev ? null : c.Scalar("SELECT ts FROM event_log WHERE seq<=@s ORDER BY seq DESC LIMIT 1", null, ("@s", since)) as string;
                for (var i = 0; i < items.Count; i++)
                    items[i] = items[i] with { IsNew = since < rev && (sinceTs is null || string.CompareOrdinal(items[i].At, sinceTs) > 0) };
            }

            var counts = new AttentionCounts(
                items.Count(i => i.Kind == AttentionKinds.Ask), items.Count(i => i.Kind == AttentionKinds.Accept), items.Count(i => i.Kind == AttentionKinds.Blocked),
                items.Count(i => i.Kind == AttentionKinds.Done), items.Count(i => i.Kind == AttentionKinds.Uncertain), items.Count, col.Running.Count);
            return new AttentionSnapshot(rev, Time.Iso(now), Sentence(counts), counts, new AttentionBudget(limit, ledger.Used, ledger.Suppressed, ledger.Day), items, col.Running);
        }
    }

    /// <summary>Düz Türkçe özet cümlesi (kişi kod okumaz).</summary>
    public static string Sentence(AttentionCounts n)
    {
        var parts = new List<string>();
        if (n.Ask > 0) parts.Add($"{n.Ask} soru");
        if (n.Accept > 0) parts.Add($"{n.Accept} kabul");
        if (n.Blocked > 0) parts.Add($"{n.Blocked} engel");
        if (n.Done > 0) parts.Add($"{n.Done} bitti");
        var waiting = parts.Count == 0 ? "Şu an seni bekleyen bir şey yok." : string.Join(", ", parts) + " seni bekliyor.";
        var tail = new List<string>();
        if (n.Uncertain > 0) tail.Add($"{n.Uncertain} işin durumundan emin değilim");
        if (n.Running > 0) tail.Add($"{n.Running} iş çalışıyor");
        if (tail.Count == 0) return waiting;
        var t = string.Join(", ", tail);
        return waiting + " " + char.ToUpper(t[0], Soz.Core.Text.TextNormalizer.Tr) + t[1..] + ".";
    }

    /// <summary>
    /// "Gördüm": biten, kanıtsız kalan, kalan ya da başlatılamayan işi listeden düşürür (iş silinmez, yalnız seen_at yazılır). Cevap ya da karar bekleyen iş için
    /// kullanılamaz: önce cevap verilmeli. Oturum bildirimi ajanın açık olduğu pencerede kapanır.
    /// </summary>
    public AttentionItem Ack(string id, string actor = "api")
    {
        var snap = Snapshot();
        var item = snap.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal))
                   ?? throw new SozException(404, "Bu öğe artık listede değil. Liste kendiliğinden güncellenmiş olabilir.");
        if (item.JobId is null) throw new SozException(409, "Bu bildirim yardımcının açık olduğu pencerede kapanır. Buradan \"gördüm\" denemez.");
        if (item.Reason is not ("passed" or "no_change" or "proof_failed" or "start_failed" or "unproven"))
            throw new SozException(409, item.Kind is AttentionKinds.Ask or AttentionKinds.Accept ? "Bu iş senden bir cevap ya da karar bekliyor. Önce onu ver." : "Bu öğe \"gördüm\" ile kapanmaz. Durum değişince kendiliğinden düşer.");
        lock (_lock)
        {
            using var c = _db.Open();
            c.Exec("UPDATE jobs SET seen_at=@n WHERE id=@id AND seen_at IS NULL", null, ("@n", Time.NowIso()), ("@id", item.JobId));
            Events.Append(c, "attention.acked", "job", item.JobUid, null, item.JobUid, actor, new JsonObject { ["itemId"] = item.Id, ["kind"] = item.Kind, ["reason"] = item.Reason }.ToJsonString());
        }
        return item;
    }

    /// <summary>Değişim imzası: kimlikler, türler, işaret kararları ve çalışanlar. İmza değişince SSE "attention" olayı yayınlanır.</summary>
    public static string Signature(AttentionSnapshot s)
        => string.Join("|", s.Items.Select(i => $"{i.Id}={(i.Signal ? 1 : 0)}")) + "#" + string.Join(",", s.Running.Select(r => r.JobId)) + "#" + s.Budget.UsedToday;
}
