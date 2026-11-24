using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Tests;

/// <summary>
/// Sentetik v13 veritabanı üreticisi (gerçek kişisel veri YOK). Dosya: test\sema-ornekleri\v13-ornek.db.
/// Kaynak: dondurulmuş v12 örneği kopyalanır ve SozDb.MigrateTo(13) ile v13'e göç ettirilir; yani jobs.write_check_first (v14) adımı gelmeden önceki hâli.
/// Yeniden üretmek için SOZ_SEMA_URET=1 ile GocV14Tests.V13_ornek_yeniden_uretilebilir koşulur.
/// </summary>
internal static class V13Ornegi
{
    public static string DosyaYolu => Path.Combine(TestEnv.RepoRoot, "test", "sema-ornekleri", "v13-ornek.db");

    /// <summary><paramref name="dataDir"/>\soz.db olarak sürüm 13 örneğini üretir.</summary>
    public static void Uret(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.Copy(V12Ornegi.DosyaYolu, Path.Combine(dataDir, "soz.db"), overwrite: true);
        var onceki = Time.UtcNow;
        Time.UtcNow = () => new DateTime(2026, 11, 11, 11, 0, 0, DateTimeKind.Utc);
        try
        {
            var db = new SozDb(dataDir);
            db.MigrateTo(13);
            using (var c = db.Open())
            {
                // Göçün veriyi koruduğunu göstermek için sentetik bir proje, iş ve ölçüt (v14 sütunu henüz yok: ham SQL).
                c.Exec("INSERT INTO projects(key, name, path, created_at, uid) VALUES('v13-ornek','Örnek proje','E:/ornek-proje','2026-11-11T11:00:00.000Z','018f0000-0000-7000-8000-000000000000')");
                c.Exec("INSERT INTO jobs(project_id, title, raw_text, prompt_text, prompt_hash, state, created_at, uid, kind) VALUES((SELECT id FROM projects WHERE key='v13-ornek'),'Örnek iş','Örnek iş metni','Örnek iş metni','h1','taslak','2026-11-11T11:00:00.000Z','018f0000-0000-7000-8000-000000000001','kod')");
                c.Exec("INSERT INTO checks(project_id, kind, label, spec_json, enabled, origin, created_at, uid) VALUES((SELECT id FROM projects WHERE key='v13-ornek'),'command','Testler geçsin','{\"cmd\":\"npm test\",\"expectExit\":0}',1,'user','2026-11-11T11:00:00.000Z','018f0000-0000-7000-8000-000000000002')");
                c.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
                c.Exec("PRAGMA journal_mode=DELETE;"); // taahhütlü dosya tek parça kalsın
            }
        }
        finally
        {
            Time.UtcNow = onceki;
            SqliteConnection.ClearAllPools();
        }
    }
}
