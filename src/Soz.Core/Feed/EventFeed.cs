using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Feed;

/// <summary>"Ne oldu" sayfasının bir satırı. Id: e&lt;sıra&gt; (olay defteri) ya da g&lt;no&gt; (bekçi kaydı). Group: job | guard | setting | system.</summary>
public sealed record FeedLine(string Id, string At, string Group, string GroupLabel, string Kind, string Sentence, long? JobId, string? JobTitle, long? ProjectId, string? ProjectName, string? Folder);

/// <summary>Bir sayfa: satırlar yeniden eskiye. Next doluysa daha eski satır var (bir sonraki isteğin <c>before</c> değeri).</summary>
public sealed record FeedPage(List<FeedLine> Items, bool HasMore, string? Next, int Limit, string? Group);

/// <summary>
/// Olay defterinin okuma yüzü: zaman sırasıyla, kişiye gösterilecek tek cümlelik sade satırlar. YALNIZ OKUR.
/// Kaynak: event_log (iş, ayar, sistem olayları) ile bekçinin durdurduğu ya da sorduğu işlemler (guard_events). Ham yük (payload) dışarı verilmez,
/// cümleler yükten bu sınıfta kurulur ve içindeki metinler sır süzgecinden geçer. Cümlesi tanımlı olmayan olay türü (nabız, ayrıntı, bilinmeyen) satır olmaz.
/// </summary>
public static class EventFeed
{
    public const string GJob = "job", GGuard = "guard", GSetting = "setting", GSystem = "system";
    public static readonly string[] Groups = { GJob, GGuard, GSetting, GSystem };

    public static string GroupLabel(string g) => g switch { GJob => "İş", GGuard => "Koruma", GSetting => "Ayar", _ => "Sistem" };

    public const int DefaultLimit = 50, MaxLimit = 200;

    /// <summary>Türün hangi grupta göründüğü (null: bu tür satır olmaz). legacy.* eski olay adlarıdır.</summary>
    private static readonly Dictionary<string, string> KindGroups = new(StringComparer.Ordinal)
    {
        ["legacy.job"] = GJob, ["legacy.proof"] = GJob, ["legacy.queue"] = GJob, ["legacy.session_needs_user"] = GJob,
        ["legacy.job_accepted"] = GJob, ["legacy.job_rejected"] = GJob, ["attention.acked"] = GJob, ["check.human_ticked"] = GJob,
        ["legacy.job_tests_declined"] = GJob,
        ["legacy.job_check_declined"] = GJob,
        ["legacy.approval_used"] = GGuard, ["legacy.caller_denied"] = GGuard, ["snapshot.taken"] = GGuard, ["snapshot.failed"] = GGuard,
        ["undo.previewed"] = GGuard, ["undo.applied"] = GGuard, ["undo.reverted"] = GGuard,
        ["legacy.check_added"] = GSetting, ["legacy.check_changed"] = GSetting, ["legacy.check_removed"] = GSetting, ["legacy.gate_changed"] = GSetting,
        ["legacy.hooks_installed"] = GSetting, ["legacy.phone_code_renewed"] = GSetting,
        ["legacy.add"] = GSetting, ["legacy.approve"] = GSetting, ["legacy.reject"] = GSetting, ["legacy.archive"] = GSetting, ["legacy.pin"] = GSetting,
        ["registry.approved"] = GSetting, ["registry.revoked"] = GSetting,
        ["kullanici.onay"] = GSetting, ["kullanici.duzeltme"] = GSetting, ["kullanici.izleme-kapat"] = GSetting, ["kullanici.izleme-ac"] = GSetting,
        ["discovery.slot.placed"] = GSetting, ["discovery.slot.cleared"] = GSetting, ["discovery.suggestion.dismissed"] = GSetting, ["discovery.suggestion.restored"] = GSetting,
        ["legacy.import"] = GSystem, ["legacy.export"] = GSystem, ["legacy.packet_copy"] = GSystem, ["legacy.repeat_alert"] = GSystem, ["legacy.judge"] = GSystem,
        ["project.relocated"] = GSetting, ["kesif.bitti"] = GSystem, ["migration.applied"] = GSystem, ["backup.taken"] = GSystem, ["repair.applied"] = GSystem, ["snapshot.expired"] = GSystem,
    };

    /// <summary>Bekçi kayıtları: yalnız durdurulan ve sorulan işlemler satır olur (izin verilenler kalabalık yapar).</summary>
    private const string GuardDeny = "guard.deny", GuardAsk = "guard.ask";

    public static bool IsGroup(string? g) => g is not null && Array.IndexOf(Groups, g) >= 0;

    /// <summary>
    /// Bir sayfa okur. <paramref name="before"/> verilirse yalnız ondan eski satırlar (bir önceki sayfanın <c>Next</c> değeri, ISO zaman). <paramref name="group"/> verilirse yalnız o grup.
    /// Aynı zaman damgalı satırlar sayfa sınırında bölünmez.
    /// </summary>
    public static FeedPage Read(SqliteConnection c, int? limit = null, string? before = null, string? group = null)
    {
        var lim = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        if (group is not null && !IsGroup(group)) throw new SozException(400, "Tür şunlardan biri olmalı: job, guard, setting, system.");
        var beforeIso = string.IsNullOrWhiteSpace(before) ? null : before.Trim();
        if (beforeIso is not null && !DateTime.TryParse(beforeIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out _))
            throw new SozException(400, "before geçerli bir zaman olmalı (örnek: 2026-11-12T08:30:00.000Z).");

        var kinds = KindGroups.Where(k => group is null || k.Value == group).Select(k => k.Key).ToList();
        var includeGuard = group is null or GGuard;
        var lines = new List<FeedLine>();
        var ctx = new Ctx(c);
        var offset = 0;
        const int chunk = 300;
        var exhausted = false;
        // Satırı olmayan alt olaylar (ör. kanıt denemesinin ara adımı) atlanır; yeterli satır çıkana kadar dilim dilim okunur.
        while (lines.Count <= lim && !exhausted)
        {
            var rows = Fetch(c, kinds, includeGuard, beforeIso, chunk, offset);
            offset += rows.Count;
            if (rows.Count < chunk) exhausted = true;
            foreach (var r in rows)
                if (Narrate(ctx, r) is { } line) lines.Add(line);
        }

        if (lines.Count > lim)
        {
            // Sınırdaki aynı zaman damgalı satırlar birlikte gider.
            var cut = lines[lim - 1].At;
            var keep = lines.Take(lim).ToList();
            keep.AddRange(lines.Skip(lim).TakeWhile(l => l.At == cut));
            lines = keep;
        }
        var more = lines.Count > 0 && HasOlder(c, kinds, includeGuard, lines[^1].At, ctx);
        return new FeedPage(lines, more, more ? lines[^1].At : null, lim, group);
    }

    /// <summary>Verilen zamandan daha eski, satırı olan bir kayıt var mı (en çok bir dilim bakılır).</summary>
    private static bool HasOlder(SqliteConnection c, List<string> kinds, bool includeGuard, string at, Ctx ctx)
    {
        var offset = 0;
        const int chunk = 100;
        while (true)
        {
            var rows = Fetch(c, kinds, includeGuard, at, chunk, offset);
            foreach (var r in rows) if (Narrate(ctx, r) is not null) return true;
            offset += rows.Count;
            if (rows.Count < chunk) return false;
        }
    }

    private sealed record Row(string Src, long Key, string At, string Kind, string Payload, string? ProjectUid, long? ProjectId, string? JobUid, string? Extra);

    private static List<Row> Fetch(SqliteConnection c, List<string> kinds, bool includeGuard, string? before, int limit, int offset)
    {
        var ps = new List<(string, object?)> { ("@b", before), ("@l", limit), ("@o", offset) };
        var names = new List<string>();
        for (var i = 0; i < kinds.Count; i++) { var n = "@k" + i; names.Add(n); ps.Add((n, kinds[i])); }
        var parts = new List<string>();
        if (kinds.Count > 0)
            parts.Add($"SELECT 'e' AS src, seq AS k, ts, kind, payload, project_uid, NULL AS pid, job_uid, NULL AS extra FROM event_log WHERE kind IN ({string.Join(",", names)}) AND (@b IS NULL OR ts<@b)");
        if (includeGuard)
            parts.Add("""
                SELECT 'g', e.id, e.at, 'guard.' || e.action,
                       json_object('tool', e.tool, 'target', e.target_summary, 'rule', r.label), NULL, e.project_id, NULL, NULL
                FROM guard_events e LEFT JOIN guard_rules r ON r.id=e.rule_id
                WHERE e.action IN ('deny','ask') AND (@b IS NULL OR e.at<@b)
                """);
        if (parts.Count == 0) return new();
        try
        {
            return c.Query($"SELECT * FROM ({string.Join(" UNION ALL ", parts)}) ORDER BY 3 DESC, 1 DESC, 2 DESC LIMIT @l OFFSET @o",
                r => new Row(r.GetString(0), r.GetInt64(1), r.Str(2) ?? "", r.Str(3) ?? "", r.Str(4) ?? "{}", r.Str(5), r.LongN(6), r.Str(7), r.Str(8)), null, ps.ToArray());
        }
        catch (SqliteException) when (includeGuard && !GuardTableExists(c))
        {
            // bekçi tablosu henüz kurulmamış eski veri: yalnız olay defteri
            return Fetch(c, kinds, false, before, limit, offset);
        }
    }

    private static bool GuardTableExists(SqliteConnection c)
        => c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('guard_events','guard_rules')") == 2;

    // ---------- cümle kurma ----------

    private sealed class Ctx(SqliteConnection c)
    {
        public SqliteConnection C { get; } = c;
        public Dictionary<long, JobBits?> JobsById { get; } = new();
        public Dictionary<string, JobBits?> JobsByUid { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ProjBits?> ProjByUid { get; } = new(StringComparer.Ordinal);
        public Dictionary<long, ProjBits?> ProjById { get; } = new();
    }

    private sealed record JobBits(long Id, string Title, long ProjectId, bool AcceptedByUser, string? PassedReason = null);
    private sealed record ProjBits(long Id, string Name, string? Path);

    private static JobBits? JobById(Ctx x, long id)
    {
        if (x.JobsById.TryGetValue(id, out var hit)) return hit;
        var j = x.C.Query("SELECT id, title, project_id, COALESCE(accepted_by_user,0), (SELECT reason_code FROM proof_runs WHERE job_id=jobs.id AND verdict='passed' ORDER BY id DESC LIMIT 1) FROM jobs WHERE id=@i",
            r => new JobBits(r.GetInt64(0), r.Str(1) ?? "", r.Long(2), r.Long(3) == 1, r.Str(4)), null, ("@i", id)).FirstOrDefault();
        return x.JobsById[id] = j;
    }

    private static JobBits? JobByUid(Ctx x, string uid)
    {
        if (x.JobsByUid.TryGetValue(uid, out var hit)) return hit;
        var j = x.C.Query("SELECT id, title, project_id, COALESCE(accepted_by_user,0), (SELECT reason_code FROM proof_runs WHERE job_id=jobs.id AND verdict='passed' ORDER BY id DESC LIMIT 1) FROM jobs WHERE uid=@u",
            r => new JobBits(r.GetInt64(0), r.Str(1) ?? "", r.Long(2), r.Long(3) == 1, r.Str(4)), null, ("@u", uid)).FirstOrDefault();
        return x.JobsByUid[uid] = j;
    }

    private static ProjBits? ProjByUid(Ctx x, string uid)
    {
        if (x.ProjByUid.TryGetValue(uid, out var hit)) return hit;
        var p = x.C.Query("SELECT id, COALESCE(name, key), path FROM projects WHERE uid=@u", r => new ProjBits(r.GetInt64(0), r.Str(1) ?? "", r.Str(2)), null, ("@u", uid)).FirstOrDefault();
        return x.ProjByUid[uid] = p;
    }

    private static ProjBits? ProjById(Ctx x, long id)
    {
        if (x.ProjById.TryGetValue(id, out var hit)) return hit;
        var p = x.C.Query("SELECT id, COALESCE(name, key), path FROM projects WHERE id=@i", r => new ProjBits(r.GetInt64(0), r.Str(1) ?? "", r.Str(2)), null, ("@i", id)).FirstOrDefault();
        return x.ProjById[id] = p;
    }

    private static JsonObject Obj(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string? S(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long? N(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : (o?[k] is JsonValue v2 && v2.TryGetValue<int>(out var i) ? i : null);
    private static bool? B(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static string Clip(string? s, int max) => SecretFilter.MaskSecrets(TextNormalizer.Truncate((s ?? "").ReplaceLineEndings(" ").Trim(), max));

    private static string T(JobBits? j) => j is null ? "Bir iş" : $"«{Clip(j.Title, 60)}» işi";

    /// <summary>Tek satır kurar. Cümlesi olmayan olay null döner.</summary>
    private static FeedLine? Narrate(Ctx x, Row r)
    {
        var p = Obj(r.Payload);
        // Eski çağrılarla yazılan olaylarda asıl yük "detail" içinde JSON metni olarak durur.
        var d = r.Kind.StartsWith(Events.LegacyPrefix, StringComparison.Ordinal) && S(p, "detail") is { Length: > 0 } det ? Obj(det) : p;

        // iş: uid, yük içindeki iş no ya da yük içindeki iş
        JobBits? job = null;
        if (r.JobUid is { Length: > 0 } ju) job = JobByUid(x, ju);
        if (job is null && r.Kind is "legacy.job" or "legacy.proof" or "legacy.job_accepted" or "legacy.job_rejected" or "legacy.approval_used" or "legacy.job_tests_declined" or "legacy.job_check_declined" && (N(d, "jobId") ?? N(d, "id")) is { } jid)
            job = JobById(x, jid);

        ProjBits? proj = r.ProjectUid is { Length: > 0 } pu ? ProjByUid(x, pu) : r.ProjectId is { } pid ? ProjById(x, pid) : null;
        if (proj is null && job is not null) proj = ProjById(x, job.ProjectId);

        string? sentence;
        if (r.Src == "g") sentence = GuardSentence(r.Kind, p, proj);
        else sentence = Sentence(r.Kind, d, p, job);
        if (string.IsNullOrWhiteSpace(sentence)) return null;

        var group = r.Src == "g" ? GGuard : KindGroups.GetValueOrDefault(r.Kind, GSystem);
        var id = (r.Src == "g" ? "g" : "e") + r.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var kind = r.Src == "g" ? r.Kind : (r.Kind.StartsWith(Events.LegacyPrefix, StringComparison.Ordinal) ? r.Kind[Events.LegacyPrefix.Length..] : r.Kind);
        return new FeedLine(id, r.At, group, GroupLabel(group), kind, sentence!, job?.Id, job is null ? null : Clip(job.Title, 80), proj?.Id, proj?.Name is { } n ? Clip(n, 80) : null, proj?.Path);
    }

    private static string? GuardSentence(string kind, JsonObject p, ProjBits? proj)
    {
        var target = Clip(S(p, "target"), 120);
        var rule = S(p, "rule") is { Length: > 0 } rl ? $" («{Clip(rl, 60)}» kuralı)" : "";
        var what = target.Length > 0 ? $": {target}" : "";
        return kind switch
        {
            GuardDeny => $"Bekçi bir işlemi durdurdu{rule}{what}.",
            GuardAsk => $"Bekçi bir işlem için sana sordu{rule}{what}.",
            _ => null,
        };
    }

    private static string? Sentence(string kind, JsonObject d, JsonObject p, JobBits? job)
    {
        switch (kind)
        {
            case "legacy.job":
            {
                if (S(d, "state") is not { } st) return null; // ajanın son sözü gibi alt olaylar satır olmaz
                var t = T(job);
                return st switch
                {
                    "taslak" => $"{t} taslak olarak yazıldı.",
                    "verildi" => $"{t} yardımcıya verildi.",
                    "sirada" => $"{t} sıraya alındı.",
                    "calisiyor" => $"{t} çalışmaya başladı.",
                    "cevap_bekliyor" => $"{t}: yardımcı sana bir soru sordu.",
                    "onay_bekliyor" => $"{t} senin onayını bekliyor.",
                    "karar_bekliyor" => $"{t} senin kararını bekliyor.",
                    "gecti" => job is { AcceptedByUser: true } ? $"{t} senin onayınla bitti."
                        : job is { PassedReason: "no_change" } ? $"{t} bitti, ama yardımcı hiçbir dosyayı değiştirmedi."
                        : job is { PassedReason: "ignored_only" } ? $"{t} bitti, ama yardımcı yalnız izlenmeyen klasörlere yazdı."
                        : $"{t} deneme sonucuyla bitti.",
                    "kaldi" => $"{t} olmadı: deneme sonucu tutmadı.",
                    "durduruldu" => $"{t} durduruldu.",
                    "baslatilamadi" => $"{t} başlatılamadı.",
                    "iptal" => $"{t} iptal edildi.",
                    _ => null,
                };
            }
            case "legacy.proof":
            {
                var phase = S(d, "phase");
                var t = job is null ? "Bir iş" : T(job);
                if (phase == "started") return $"{t} için deneme başladı.";
                if (phase != "finished") return null;
                return S(d, "verdict") switch
                {
                    "passed" => $"{t} için deneme geçti.",
                    "failed" => $"{t} için denemede bir kontrol tutmadı.",
                    "unchecked" => $"{t} denenmeden bitti: deneme sonucu sayılamadı.",
                    "error" => $"{t} için deneme yarıda kaldı.",
                    _ => null,
                };
            }
            case "legacy.queue":
                if (B(d, "delivered") != true && S(d, "origin") == "telefon")
                    return S(d, "preview") is { Length: > 0 } pv
                        ? $"Telefondan yardımcıya bir mesaj bıraktın: «{Clip(pv, 80)}». Yardımcı durunca alacak."
                        : "Telefondan yardımcıya bir mesaj bıraktın. Yardımcı durunca alacak.";
                return B(d, "delivered") == true ? "Kuyruğa bırakılan bir mesaj yardımcıya iletildi." : "Yardımcıya iletilmek üzere kuyruğa bir mesaj bırakıldı.";
            case "legacy.session_needs_user":
                return S(d, "reason") switch
                {
                    "permission_prompt" => "Bir yardımcı bir işlem için senden izin bekliyor.",
                    "elicitation_dialog" or "agent_needs_input" => "Bir yardımcı senden bir bilgi bekliyor.",
                    { } r when r.StartsWith("failure:", StringComparison.Ordinal) => "Bir yardımcı hata yüzünden durdu.",
                    _ => "Terminalde çalışan bir yardımcı senden cevap bekliyor.",
                };
            case "legacy.job_accepted": return $"{T(job)} için sonucu sen kabul ettin.";
            case "legacy.job_rejected": return $"{T(job)} için sonucu kabul etmedin.";
            case "legacy.job_check_declined": return $"{T(job)} için yardımcının yazdığı denemeye Hayır dedin. Deneme açılmadı.";
            case "legacy.job_tests_declined": return S(d, "scope") == "edited"
                ? $"{T(job)} için yardımcının test değişikliklerini kabul etmedin. Onayladığın testler korundu."
                : S(d, "scope") == "added" ? $"{T(job)} için yardımcının eklediği testleri onaylı testlere katmadın."
                : $"{T(job)} için yardımcının test teklifine Hayır dedin. Onayladığın testler korundu.";
            case "attention.acked": return job is null ? "Bir bildirimi \"gördüm\" diyerek kapattın." : $"{T(job)} için \"gördüm\" dedin.";
            case "check.human_ticked":
            {
                var text = Clip(S(p, "text"), 80);
                var mark = B(p, "ticked") == false ? "işaretini kaldırdın" : "işaretledin";
                return text.Length > 0 ? $"«{text}» maddesini {mark}." : $"Bir onay maddesini {mark}.";
            }
            case "legacy.approval_used": return "Sorulan bir komut için verdiğin onay kullanıldı.";
            case "legacy.caller_denied": return "Bir program Söz'ün kabul ya da ayar düğmesine dokunmak istedi. İzin verilmedi.";
            case "snapshot.taken":
                return N(p, "files") is { } f ? $"İşin başında klasörün geri alma noktası alındı ({f} dosya)." : "İşin başında klasörün geri alma noktası alındı.";
            case "snapshot.failed": return "Geri alma noktası alınamadı. İş korumasız çalıştı.";
            case "undo.previewed": return "Bir işin geri alınması önizlendi. Hiçbir dosyaya dokunulmadı.";
            case "undo.applied":
            {
                var rs = N(p, "restored") ?? 0;
                var rm = N(p, "removed") ?? 0;
                if (rs > 0 && rm > 0) return $"Bir iş geri alındı: {rs} dosya eski haline döndü, {rm} dosya kaldırıldı.";
                if (rs > 0) return $"Bir iş geri alındı: {rs} dosya eski haline döndü.";
                if (rm > 0) return $"Bir iş geri alındı: yardımcının eklediği {rm} dosya kaldırıldı.";
                return "Bir iş geri alındı.";
            }
            case "undo.reverted": return "Yaptığın geri alma geri alındı.";
            case "legacy.check_added": return Chk(d, "eklendi");
            case "legacy.check_changed": return Chk(d, "değişti");
            case "legacy.check_removed": return Chk(d, "kaldırıldı", old: true);
            case "legacy.gate_changed": return "Bitti denetiminin ayarı değişti.";
            case "legacy.hooks_installed": return "Bir klasöre terminal desteği kuruldu.";
            case "legacy.phone_code_renewed": return "Telefon bağlantı kodu yenilendi.";
            case "legacy.add": return "Deftere yeni bir karar önerisi eklendi.";
            case "legacy.approve": return "Defterdeki bir kararı onayladın.";
            case "legacy.reject": return "Defterdeki bir kararı reddettin.";
            case "legacy.archive": return "Defterdeki bir kararı arşive aldın.";
            case "legacy.pin": return "Defterdeki bir kararı sabitledin.";
            case "registry.approved": return $"{Who(p)} kaydını onayladın.";
            case "registry.revoked": return $"{Who(p)} kaydının onayını geri aldın.";
            case "kullanici.onay": return "Tanışma sayfasındaki bir maddeyi onayladın.";
            case "kullanici.duzeltme": return "Tanışma sayfasındaki bir maddeyi kendi değerinle düzelttin.";
            case "kullanici.izleme-kapat": return "Tanışma sayfasındaki bir maddeye \"bir daha gösterme\" dedin.";
            case "kullanici.izleme-ac": return "Kapattığın bir Tanışma maddesini geri getirdin.";
            case "discovery.slot.placed": return "Bir yuvaya bir öğe yerleştirdin.";
            case "discovery.slot.cleared": return "Bir yuvayı boşalttın.";
            case "discovery.suggestion.dismissed": return "Söz'ün bir önerisini susturdun.";
            case "discovery.suggestion.restored": return "Susturduğun bir öneriyi geri getirdin.";
            case "legacy.import": return "Eski yardımcı kayıtları içe alındı.";
            case "legacy.export": return "Bağlam paketi dosyalara yazıldı.";
            case "legacy.packet_copy": return "Bağlam paketi panoya kopyalandı.";
            case "legacy.repeat_alert": return "Aynı şeyi yeniden söyledin, Söz seni uyardı.";
            case "legacy.judge": return "Yardımcının son sözü için ikinci görüş istendi.";
            case "kesif.bitti": return N(p, "found") is { } n ? $"Söz bilgisayarındaki araçlara baktı ve {n} madde buldu." : "Söz bilgisayarındaki araçlara baktı.";
            case "migration.applied": return N(p, "from") is { } a && N(p, "to") is { } b ? $"Veri dosyası yeni sürüme güncellendi ({a}. sürümden {b}. sürüme)." : "Veri dosyası yeni sürüme güncellendi.";
            case "backup.taken": return "Güncellemeden önce veri dosyasının yedeği alındı.";
            case "repair.applied": return "Eski sürümden kalan kayıtlar onarıldı.";
            case "project.relocated": return "Bir projenin klasör yeri güncellendi.";
            case "snapshot.expired": return "Süresi dolan geri alma noktaları temizlendi.";
            default: return null;
        }
    }

    private static string Chk(JsonObject d, string verb, bool old = false)
    {
        var node = (old ? d["old"] : d["new"]) as JsonObject ?? (d["new"] ?? d["old"]) as JsonObject;
        var label = Clip(S(node, "label"), 80);
        var jobBound = N(node, "jobId") is not null;
        var scope = jobBound ? "Bir işe özel bitti kontrolü" : "Klasörün bitti kontrolü";
        return label.Length > 0 ? $"{scope} {verb}: «{label}»." : $"{scope} {verb}.";
    }

    private static string Who(JsonObject p)
    {
        var id = S(p, "id") ?? "";
        var name = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;
        if (string.IsNullOrEmpty(name)) return "Bir";
        if (string.Equals(name, "hermes", StringComparison.OrdinalIgnoreCase)) return "Hermes Agent";
        if (Soz.Core.Assistants.AssistantCatalog.Get(name) is { } d) return d.Title;
        return $"«{Clip(name, 50)}»";
    }
}
