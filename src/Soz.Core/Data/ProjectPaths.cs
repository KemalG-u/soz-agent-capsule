using Microsoft.Data.Sqlite;

namespace Soz.Core.Data;

/// <summary>
/// Proje yolu geçmişi (project_path): klasör taşınınca ya da yeniden görülünce geçmiş kopmasın diye projenin her yolu uid'ye bağlı tutulur.
/// Bir projenin tek bir güncel (is_current=1) yolu vardır.
/// </summary>
public static class ProjectPaths
{
    /// <summary>
    /// Projenin (uid) bu yolda görüldüğünü kaydeder: yol yeniyse eklenir ve güncel olur, bilinen yolsa last_seen yenilenir ve güncel olur;
    /// projenin öbür yolları güncel olmaktan çıkar (silinmez).
    /// </summary>
    public static void Seen(SqliteConnection c, SqliteTransaction? tx, string projectUid, string? path)
    {
        if (string.IsNullOrWhiteSpace(projectUid) || string.IsNullOrWhiteSpace(path)) return;
        var now = Time.NowIso();
        c.Exec("UPDATE project_path SET is_current=0 WHERE project_uid=@u AND path<>@p AND is_current=1", tx, ("@u", projectUid), ("@p", path));
        c.Exec("""
            INSERT INTO project_path(project_uid, path, first_seen, last_seen, is_current) VALUES(@u,@p,@t,@t,1)
            ON CONFLICT(project_uid, path) DO UPDATE SET last_seen=excluded.last_seen, is_current=1
            """, tx, ("@u", projectUid), ("@p", path), ("@t", now));
    }

    /// <summary>Projenin kayıtlı yolları, en yeni görülen önce.</summary>
    public static List<(string Path, string FirstSeen, string LastSeen, bool IsCurrent)> For(SqliteConnection c, string projectUid)
        => c.Query("SELECT path, first_seen, last_seen, is_current FROM project_path WHERE project_uid=@u ORDER BY last_seen DESC, path",
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1), null, ("@u", projectUid));
}
