using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;

namespace Soz.Core;

/// <summary>event_log satırı (ekle-yalnız olay kaydı). Seq yerel sıradır; Id cihazlar arası tekil kimliktir.</summary>
public sealed record EventRow(long Seq, string Id, string DeviceUid, string Ts, string Kind, int KindV, string? Entity, string? EntityUid, string? ProjectUid, string? JobUid,
    string Actor, string Payload, string? PrevHash, string? Hash);

/// <summary>Eski events tablosundan bir satır (canlı akışın nabız olayları için; bunlar event_log'a yazılmaz).</summary>
public sealed record LegacyEventRow(long Id, string Ts, string Kind, long? ProjectId, string? Detail);

/// <summary>Zincir doğrulama raporu (bkz. <see cref="Events.VerifyChainDetailed"/>): toplam, doğrulanan, eski biçimli (doğrulanmadı), kırık ilk satırın seq değeri.</summary>
public sealed record ChainReport(long Total, long Verified, long Legacy, long? BrokenSeq)
{
    /// <summary>`soz doctor` satırı: "olay defteri: N kayıt, zincir sağlam" ya da "K. kayıttan itibaren bozuk".</summary>
    public string Line()
    {
        var old = Legacy > 0 ? $" ({Legacy} eski biçimli kayıt doğrulanmadı)" : "";
        return BrokenSeq is { } b ? $"olay defteri: {Total} kayıt, zincir {b}. kayıttan itibaren bozuk{old}" : $"olay defteri: {Total} kayıt, zincir sağlam{old}";
    }
}

/// <summary>
/// Olay yazımı. İki yol vardır:
/// (1) <see cref="Append"/>: yeni, türü belli olay; yalnız event_log'a yazar, hash zinciri tutar.
/// (2) <see cref="Log"/>: eski çağrılar kırılmasın diye eski events tablosuna yazar ve (nabız olayları hariç) aynı işlemde event_log'a "legacy.&lt;tür&gt;" olarak da yazar.
/// event_log anlamlı kayıt içindir: yüksek sıklıklı nabız olayları (<see cref="NoiseKinds"/>) yalnız events tablosuna gider.
/// events.log_id satırın event_log'daki karşılığının kimliğidir: NULL = henüz kopyalanmadı (eski sürüm yazmış), '' = bilerek kopyalanmaz (nabız).
/// Hash zinciri: hash = SHA256(prev_hash ve satırın bütün anlamlı alanları, uzunluk önekli), BEGIN IMMEDIATE içinde son satırın hash'i okunup yeni satır yazılır;
/// böylece birden çok süreç (sunucu, kancalar, komut satırı) aynı anda yazsa da zincir bozulmaz.
/// Zincir yalnız kaza ve kurcalama farkındalığı içindir, kötü niyete karşı koruma değildir (aynı kullanıcı hesabı).
/// Özet biçimi etiketlidir (<see cref="HashTag"/>): biçim değişirse etiket artar. Eski etiketli (v1) satırlar kırık sayılmaz, "eski biçim, doğrulanmadı" diye ayrı sayılır (bkz. <see cref="VerifyChainDetailed"/>).
/// </summary>
public static class Events
{
    public const string LegacyPrefix = "legacy.";

    /// <summary>
    /// Nabız (gürültü) olayları: yüksek sıklıklı, tek tek anlamı olmayan olay türleri. Yalnız eski events tablosuna yazılır, event_log'a girmez.
    /// Canlı akışa bunlardan YALNIZ job_progress girer (Live.Kinds listesinde olan tür; LiveFeed events tablosundan "id:" olmadan yayınlar, `since` ile geri verilmez).
    /// hook_pulse ve hook_inject canlı akışa girmez: yalnız events tablosunda durur (kanca sağlığı ve sayaçlar buradan okur).
    /// hook_pulse: her kanca çalışması; job_progress: ajanın her ilerleme satırı; hook_inject: her bağlam enjeksiyonu (kanca başına bir).
    /// Yeni nabız türü eklemek için tek yer burasıdır; akışa girmesi için ayrıca Live.Kinds'e eklenmesi gerekir.
    /// </summary>
    public static readonly IReadOnlySet<string> NoiseKinds = new HashSet<string>(StringComparer.Ordinal) { HookHandler.PulseKind, Live.JobProgress, "hook_inject" };

    public static bool IsNoise(string kind) => NoiseKinds.Contains(kind);

    // ---------- yazma ----------

    /// <summary>
    /// Yeni olay. <paramref name="payloadJson"/> geçerli JSON olmalıdır (boşsa "{}"). tx verilirse çağıranın işleminde yazılır, verilmezse kendi BEGIN IMMEDIATE işlemini açar.
    /// </summary>
    public static EventRow Append(SqliteConnection c, string kind, string? entity, string? entityUid, string? projectUid, string? jobUid, string actor, string? payloadJson, SqliteTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Olay türü boş olamaz.", nameof(kind));
        return InWriteTx(c, tx, () => AppendCore(c, tx, kind, entity, entityUid, projectUid, jobUid, string.IsNullOrWhiteSpace(actor) ? "soz" : actor, payloadJson, id: null, ts: null));
    }

    /// <summary>
    /// Eski çağrı: events tablosuna yazar; nabız değilse aynı olayı event_log'a "legacy.&lt;tür&gt;" olarak da yazar (aynı işlemde, kendi Uid'siyle).
    /// Nabız olayı tek bir INSERT'tür (kanca yolunun maliyeti eskisiyle aynı).
    /// </summary>
    public static void Log(SqliteConnection c, string kind, long? projectId, string? detail, SqliteTransaction? tx = null)
    {
        if (IsNoise(kind))
        {
            c.Exec("INSERT INTO events(ts, kind, project_id, detail, log_id) VALUES(@t,@k,@p,@d,'')", tx, ("@t", Time.NowIso()), ("@k", kind), ("@p", projectId), ("@d", detail));
            return;
        }
        InWriteTx(c, tx, () =>
        {
            var ts = Time.NowIso();
            var id = Uid.New();
            c.Exec("INSERT INTO events(ts, kind, project_id, detail, log_id) VALUES(@t,@k,@p,@d,@l)", tx, ("@t", ts), ("@k", kind), ("@p", projectId), ("@d", detail), ("@l", id));
            var projectUid = projectId is null ? null : c.Scalar("SELECT uid FROM projects WHERE id=@p", tx, ("@p", projectId)) as string;
            var payload = new JsonObject { ["detail"] = detail }.ToJsonString();
            return AppendCore(c, tx, LegacyPrefix + kind, null, null, projectUid, null, "soz", payload, id, ts);
        });
    }

    private static T InWriteTx<T>(SqliteConnection c, SqliteTransaction? tx, Func<T> body)
    {
        if (tx is not null) return body();
        c.Exec("BEGIN IMMEDIATE");
        try
        {
            var r = body();
            c.Exec("COMMIT");
            return r;
        }
        catch
        {
            try { c.Exec("ROLLBACK"); } catch (SqliteException) { /* işlem zaten kapanmış olabilir */ }
            throw;
        }
    }

    internal static string DeviceUid(SqliteConnection c, SqliteTransaction? tx)
        => c.Scalar("SELECT device_uid FROM device WHERE id=1", tx) as string
           ?? throw new InvalidOperationException("Cihaz kimliği yok: veritabanı v11'e göç etmemiş.");

    private static EventRow AppendCore(SqliteConnection c, SqliteTransaction? tx, string kind, string? entity, string? entityUid, string? projectUid, string? jobUid, string actor, string? payloadJson, string? id, string? ts)
    {
        var payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        var device = DeviceUid(c, tx);
        var prev = c.Scalar("SELECT hash FROM event_log ORDER BY seq DESC LIMIT 1", tx) as string;
        id ??= Uid.New();
        ts ??= Time.NowIso();
        EnsureChainMarker(c, tx);
        var hash = ComputeHash(prev, id, device, ts, kind, 1, entity, entityUid, projectUid, jobUid, actor, payload);
        c.Exec("""
            INSERT INTO event_log(id, device_uid, ts, kind, kind_v, entity, entity_uid, project_uid, job_uid, actor, payload, prev_hash, hash)
            VALUES(@id,@d,@ts,@k,1,@e,@eu,@pu,@ju,@a,@pl,@ph,@h)
            """, tx, ("@id", id), ("@d", device), ("@ts", ts), ("@k", kind), ("@e", entity), ("@eu", entityUid), ("@pu", projectUid), ("@ju", jobUid),
            ("@a", actor), ("@pl", payload), ("@ph", prev), ("@h", hash));
        var seq = c.ScalarLong("SELECT last_insert_rowid()", tx);
        return new EventRow(seq, id, device, ts, kind, 1, entity, entityUid, projectUid, jobUid, actor, payload, prev, hash);
    }

    /// <summary>Özet biçiminin etiketi (özetin ilk baytları). Biçim değişirse artar: v1 = ilk Dalga 1 biçimi (`soz-event-v1;`), v2 = bütün alanlar uzunluk önekli.</summary>
    public const string HashTag = "soz-event-v2";

    /// <summary>settings içindeki iç anahtar: v2 biçimiyle yazılan İLK satırın seq değeri. Bundan önceki satırlar eski biçimlidir (özetleri v2 ile doğrulanamaz).</summary>
    internal const string ChainMarkerKey = "_eventChainV2From";

    /// <summary>İlk v2 satırı yazılmadan önce (aynı işlemde) o satırın seq değerini kaydeder; sonra bir daha dokunmaz. Eski biçimli satırları yeni satırlardan ayırır.</summary>
    private static void EnsureChainMarker(SqliteConnection c, SqliteTransaction? tx)
    {
        if (c.Scalar("SELECT 1 FROM settings WHERE key=@k", tx, ("@k", ChainMarkerKey)) is not null) return;
        c.Exec("INSERT OR IGNORE INTO settings(key,value) SELECT @k, CAST(COALESCE(MAX(seq),0)+1 AS TEXT) FROM event_log", tx, ("@k", ChainMarkerKey));
    }

    /// <summary>
    /// Zincir özeti: SHA256 üzerinde, sırayla prev_hash, id, device_uid, ts, kind, kind_v, entity, entity_uid, project_uid, job_uid, actor, payload.
    /// Her alan belirsizliksiz kodlanır: boş değer "~;", dolu değer "&lt;UTF-8 bayt sayısı&gt;:&lt;değer&gt;;" (uzunluk önekli, bu yüzden alanlar arası kayma ve birleştirme yanıltması olmaz).
    /// </summary>
    public static string ComputeHash(string? prevHash, string id, string deviceUid, string ts, string kind, int kindV, string? entity, string? entityUid, string? projectUid, string? jobUid, string actor, string payload)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.ASCII.GetBytes(HashTag + ";"));
        foreach (var f in new[] { prevHash, id, deviceUid, ts, kind, kindV.ToString(System.Globalization.CultureInfo.InvariantCulture), entity, entityUid, projectUid, jobUid, actor, payload })
        {
            if (f is null) { h.AppendData("~;"u8); continue; }
            var bytes = Encoding.UTF8.GetBytes(f);
            h.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            h.AppendData(bytes);
            h.AppendData(";"u8);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeHash(EventRow r)
        => ComputeHash(r.PrevHash, r.Id, r.DeviceUid, r.Ts, r.Kind, r.KindV, r.Entity, r.EntityUid, r.ProjectUid, r.JobUid, r.Actor, r.Payload);

    // ---------- okuma ----------

    private const string RowCols = "seq, id, device_uid, ts, kind, kind_v, entity, entity_uid, project_uid, job_uid, actor, payload, prev_hash, hash";

    private static EventRow MapRow(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), (int)r.GetInt64(5),
        r.Str(6), r.Str(7), r.Str(8), r.Str(9), r.GetString(10), r.GetString(11), r.Str(12), r.Str(13));

    /// <summary>
    /// "şu sıradan sonrasını ver": seq &gt; <paramref name="seq"/> olan olaylar, seq sırasıyla. kinds verilirse yalnız o türler.
    /// <paramref name="upToSeq"/> verilirse seq &lt;= upToSeq ile sınırlanır (okuyucu önce LastSeq'i okuyup üst sınır koyarsa aynı olay iki turda görünmez).
    /// </summary>
    public static List<EventRow> ReadSince(SqliteConnection c, long seq, int limit = 500, IReadOnlyCollection<string>? kinds = null, long? upToSeq = null)
    {
        limit = Math.Clamp(limit, 1, 10000);
        var ps = new List<(string, object?)> { ("@s", seq), ("@l", limit), ("@u", upToSeq ?? long.MaxValue) };
        if (kinds is null || kinds.Count == 0)
            return c.Query($"SELECT {RowCols} FROM event_log WHERE seq>@s AND seq<=@u ORDER BY seq LIMIT @l", MapRow, null, ps.ToArray());
        var names = new List<string>();
        var i = 0;
        foreach (var k in kinds) { var n = "@k" + i++; names.Add(n); ps.Add((n, k)); }
        return c.Query($"SELECT {RowCols} FROM event_log WHERE seq>@s AND seq<=@u AND kind IN ({string.Join(",", names)}) ORDER BY seq LIMIT @l", MapRow, null, ps.ToArray());
    }

    public static long LastSeq(SqliteConnection c) => c.ScalarLong("SELECT COALESCE(MAX(seq),0) FROM event_log");

    /// <summary>Eski events tablosunun son kimliği (canlı akışın nabız imleci).</summary>
    public static long LastLegacyId(SqliteConnection c) => c.ScalarLong("SELECT COALESCE(MAX(id),0) FROM events");

    /// <summary>
    /// events tablosundan <paramref name="afterId"/> &lt; id &lt;= <paramref name="upToId"/> aralığındaki, verilen türlerden satırlar (id sırasıyla).
    /// Canlı akış nabız olaylarını (event_log'a girmeyenleri) buradan alır.
    /// </summary>
    public static List<LegacyEventRow> ReadLegacySince(SqliteConnection c, long afterId, long upToId, IReadOnlyCollection<string> kinds, int limit = 500)
    {
        if (kinds.Count == 0) return new();
        var ps = new List<(string, object?)> { ("@a", afterId), ("@u", upToId), ("@l", Math.Clamp(limit, 1, 10000)) };
        var names = new List<string>();
        var i = 0;
        foreach (var k in kinds) { var n = "@k" + i++; names.Add(n); ps.Add((n, k)); }
        return c.Query($"SELECT id, ts, kind, project_id, detail FROM events WHERE id>@a AND id<=@u AND kind IN ({string.Join(",", names)}) ORDER BY id LIMIT @l",
            r => new LegacyEventRow(r.GetInt64(0), r.Str(1) ?? "", r.GetString(2), r.LongN(3), r.Str(4)), null, ps.ToArray());
    }

    /// <summary>
    /// Zinciri baştan sona doğrular (salt okunur). Temizse null; değilse kırık ilk satırın seq değeri:
    /// bir satırın herhangi bir alanı değişmişse (hash tutmaz), bir satır silinmişse ya da araya girmişse (prev_hash önceki hash ile tutmaz).
    /// Eski biçimli (v1) satırlar kırık sayılmaz (bkz. <see cref="VerifyChainDetailed"/>).
    /// </summary>
    public static long? VerifyChain(SqliteConnection c) => VerifyChainDetailed(c).BrokenSeq;

    /// <summary>
    /// Zincir raporu: Total kayıt sayısı, Verified özeti bugünkü biçimle (v2) tutan satırlar, Legacy eski biçimli (v1) olduğu için özeti doğrulanamayan satırlar
    /// ("eski biçim, doğrulanmadı"; bağlantıları yine denetlenir: prev_hash önceki satırın hash'iyle tutmalı), BrokenSeq kırık ilk satır (yoksa null).
    /// Eski biçimli sayılan satır: v2 özeti tutmayan VE ilk v2 satırından (<see cref="ChainMarkerKey"/>) önce yazılmış olan; ondan sonra özeti tutmayan satır kırıktır.
    /// İşaret hiç yoksa (v2 ile henüz hiç satır yazılmamış) mevcut bütün satırlar eski biçimli sayılır.
    /// </summary>
    public static ChainReport VerifyChainDetailed(SqliteConnection c)
    {
        long? marker = long.TryParse(SozSettings.GetInternal(c, ChainMarkerKey), out var m) ? m : null;
        string? expectedPrev = null;
        long total = 0, verified = 0, legacy = 0;
        using var cmd = c.Cmd($"SELECT {RowCols} FROM event_log ORDER BY seq", null);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = MapRow(r);
            total++;
            if (row.PrevHash != expectedPrev) return new ChainReport(total, verified, legacy, row.Seq);
            if (row.Hash is not null && row.Hash == ComputeHash(row)) verified++;
            else if (marker is null || row.Seq < marker.Value) legacy++;
            else return new ChainReport(total, verified, legacy, row.Seq);
            expectedPrev = row.Hash;
        }
        return new ChainReport(total, verified, legacy, null);
    }

    /// <summary>
    /// Toplu kopya (göç ve onarım): events satırlarından henüz kopyalanmamış olanları (log_id NULL) "legacy.&lt;tür&gt;" olarak event_log'a ekler ve zinciri sürdürür.
    /// Nabız türleri kopyalanmaz, yalnız log_id='' ile işaretlenir. Kimlik, olayın kendi zamanından üretilen yeni Uid'dir; events.log_id ona bağlanır,
    /// bu yüzden tekrar çalışınca çoğalmaz. Çağıranın işlemi içinde çalışır. (kopyalanan, nabız diye işaretlenen) döndürür.
    /// </summary>
    internal static (int Copied, int Noise) CopyLegacy(SqliteConnection c, SqliteTransaction tx, string deviceUid)
    {
        var noiseNames = new List<string>();
        var noisePs = new List<(string, object?)>();
        foreach (var k in NoiseKinds) { var n = "@n" + noiseNames.Count; noiseNames.Add(n); noisePs.Add((n, k)); }
        var noise = c.Exec($"UPDATE events SET log_id='' WHERE log_id IS NULL AND kind IN ({string.Join(",", noiseNames)})", tx, noisePs.ToArray());

        var rows = c.Query("""
            SELECT e.id, e.ts, e.kind, e.detail, p.uid FROM events e LEFT JOIN projects p ON p.id=e.project_id
            WHERE e.log_id IS NULL
            ORDER BY e.id
            """, r => (id: r.GetInt64(0), ts: r.Str(1), kind: r.Str(2) ?? "", detail: r.Str(3), pu: r.Str(4)), tx);
        if (rows.Count == 0) return (0, noise);
        EnsureChainMarker(c, tx);
        var prev = c.Scalar("SELECT hash FROM event_log ORDER BY seq DESC LIMIT 1", tx) as string;
        var now = Time.NowIso();
        using var ins = c.Cmd("""
            INSERT INTO event_log(id, device_uid, ts, kind, kind_v, entity, entity_uid, project_uid, job_uid, actor, payload, prev_hash, hash)
            VALUES(@id,@d,@ts,@k,1,NULL,NULL,@pu,NULL,'soz',@pl,@ph,@h)
            """, tx, ("@id", ""), ("@d", deviceUid), ("@ts", ""), ("@k", ""), ("@pu", null), ("@pl", ""), ("@ph", null), ("@h", ""));
        using var mark = c.Cmd("UPDATE events SET log_id=@l WHERE id=@i", tx, ("@l", ""), ("@i", 0L));
        foreach (var e in rows)
        {
            var ts = string.IsNullOrEmpty(e.ts) ? now : e.ts;
            var id = Uid.At(ts);
            var kind = LegacyPrefix + e.kind;
            var payload = new JsonObject { ["detail"] = e.detail }.ToJsonString();
            var hash = ComputeHash(prev, id, deviceUid, ts, kind, 1, null, null, e.pu, null, "soz", payload);
            ins.Parameters["@id"].Value = id;
            ins.Parameters["@ts"].Value = ts;
            ins.Parameters["@k"].Value = kind;
            ins.Parameters["@pu"].Value = (object?)e.pu ?? DBNull.Value;
            ins.Parameters["@pl"].Value = payload;
            ins.Parameters["@ph"].Value = (object?)prev ?? DBNull.Value;
            ins.Parameters["@h"].Value = hash;
            ins.ExecuteNonQuery();
            mark.Parameters["@l"].Value = id;
            mark.Parameters["@i"].Value = e.id;
            mark.ExecuteNonQuery();
            prev = hash;
        }
        return (rows.Count, noise);
    }
}
