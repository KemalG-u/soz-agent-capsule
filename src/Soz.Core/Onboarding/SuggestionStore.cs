using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Onboarding;

/// <summary>registry_suggestion satırı. Body: önerinin kendisi (JSON nesne); Evidence: "nerede gördüm" listesi (JSON dizi); DismissedAt: "bir daha gösterme".</summary>
public sealed record SuggestionRow(string Id, string Kind, JsonObject Body, JsonArray Evidence, string CreatedAt, string? DismissedAt);

/// <summary>
/// `registry_suggestion` tablosunun okuma/yazma deposu (şema v11'de hazır). Sistem önerileri buraya yazılır; hiçbiri etkin değildir, kişi onaylayana kadar öneri kalır.
/// Kimlik (Id) çağıranın verdiği kararlı metindir; aynı kimlik ikinci kez eklenmez. <paramref name="kindPrefix"/> ile yalnız bir üreticinin satırları okunur.
/// </summary>
public static class SuggestionStore
{
    private const string Cols = "id, kind, body, evidence, created_at, dismissed_at";

    private static SuggestionRow Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1),
        JsonNode.Parse(r.GetString(2)) as JsonObject ?? new JsonObject(),
        (r.IsDBNull(3) ? null : JsonNode.Parse(r.GetString(3))) as JsonArray ?? new JsonArray(),
        r.GetString(4), r.Str(5));

    public static List<SuggestionRow> List(SqliteConnection c, string? kindPrefix = null, bool includeDismissed = false, SqliteTransaction? tx = null)
    {
        var sql = $"SELECT {Cols} FROM registry_suggestion WHERE (@p IS NULL OR substr(kind,1,length(@p))=@p)" + (includeDismissed ? "" : " AND dismissed_at IS NULL") + " ORDER BY id";
        return c.Query(sql, Map, tx, ("@p", kindPrefix));
    }

    public static SuggestionRow? Get(SqliteConnection c, string id, SqliteTransaction? tx = null)
        => c.Query($"SELECT {Cols} FROM registry_suggestion WHERE id=@i", Map, tx, ("@i", id)).FirstOrDefault();

    /// <summary>Yeni öneri ekler. Aynı kimlik varsa HİÇBİR şey yapmaz ve false döner (kişinin kararı ezilmez).</summary>
    public static bool Insert(SqliteConnection c, SqliteTransaction? tx, string id, string kind, JsonObject body, JsonArray evidence, string createdAt)
        => c.Exec("INSERT OR IGNORE INTO registry_suggestion(id, kind, body, evidence, created_at, dismissed_at) VALUES(@i,@k,@b,@e,@c,NULL)", tx,
            ("@i", id), ("@k", kind), ("@b", body.ToJsonString()), ("@e", evidence.ToJsonString()), ("@c", createdAt)) > 0;

    /// <summary>Var olan önerinin gövdesini ve kanıtını günceller. dismissed_at ve created_at'e dokunmaz.</summary>
    public static void Update(SqliteConnection c, SqliteTransaction? tx, string id, JsonObject body, JsonArray evidence)
        => c.Exec("UPDATE registry_suggestion SET body=@b, evidence=@e WHERE id=@i", tx, ("@b", body.ToJsonString()), ("@e", evidence.ToJsonString()), ("@i", id));

    /// <summary>"Bir daha gösterme" (at verilirse) ya da geri getirme (null). Satır silinmez.</summary>
    public static bool SetDismissed(SqliteConnection c, SqliteTransaction? tx, string id, string? at)
        => c.Exec("UPDATE registry_suggestion SET dismissed_at=@d WHERE id=@i", tx, ("@d", at), ("@i", id)) > 0;
}

/// <summary>
/// <see cref="Registry.IRegistryApprovals"/>'ın veritabanı uygulaması (registry_state.approved_sha256). Dosyadan gelen çalıştırılabilir kayıt, özeti burada
/// onaylanmadıkça etkin olmaz; dosya değişirse özet değişir ve onay kendiliğinden düşer. Onaylamak kişi eylemidir: bu sınıfı yalnız onay yolu çağırır.
/// </summary>
public sealed class DbRegistryApprovals : Registry.IRegistryApprovals
{
    private readonly SozDb _db;
    public DbRegistryApprovals(SozDb db) => _db = db;

    private static bool ValidSha(string? s) => s is { Length: 64 } && s.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public bool IsApproved(string id, string sha256)
    {
        if (string.IsNullOrWhiteSpace(id) || !ValidSha(sha256)) return false;
        using var c = _db.Open();
        var row = c.Query("SELECT approved_sha256, enabled FROM registry_state WHERE id=@i", r => (Sha: r.Str(0), Enabled: r.GetInt64(1)), null, ("@i", id)).FirstOrDefault();
        return row.Sha is not null && row.Enabled == 1 && string.Equals(row.Sha, sha256, StringComparison.Ordinal);
    }

    public void Approve(string id, string sha256)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Kimlik boş olamaz.", nameof(id));
        if (!ValidSha(sha256)) throw new ArgumentException("Özet 64 karakterlik küçük harfli onaltılık metin olmalı.", nameof(sha256));
        using var c = _db.Open();
        c.Exec("""
            INSERT INTO registry_state(id, enabled, approved_sha256, last_seen) VALUES(@i,1,@s,@t)
            ON CONFLICT(id) DO UPDATE SET approved_sha256=@s, enabled=1, last_seen=@t
            """, null, ("@i", id), ("@s", sha256), ("@t", Time.NowIso()));
    }

    public bool IsDisabled(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        using var c = _db.Open();
        var rows = c.Query("SELECT enabled FROM registry_state WHERE id=@i", r => r.GetInt64(0), null, ("@i", id));
        return rows.Count > 0 && rows[0] == 0;
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Kimlik boş olamaz.", nameof(id));
        using var c = _db.Open();
        c.Exec("""
            INSERT INTO registry_state(id, enabled, approved_sha256, last_seen) VALUES(@i,@en,NULL,@t)
            ON CONFLICT(id) DO UPDATE SET enabled=@en, last_seen=@t
            """, null, ("@i", id), ("@en", enabled ? 1 : 0), ("@t", Time.NowIso()));
    }

    /// <summary>Yalnız o özetin onayını geri alır (başka özetin onayı kalır).</summary>
    public void Revoke(string id, string sha256)
    {
        using var c = _db.Open();
        c.Exec("UPDATE registry_state SET approved_sha256=NULL WHERE id=@i AND approved_sha256=@s", null, ("@i", id), ("@s", sha256));
    }
}
