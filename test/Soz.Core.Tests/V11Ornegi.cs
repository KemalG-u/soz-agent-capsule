using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Tests;

/// <summary>
/// Sentetik v11 veritabanı üreticisi (gerçek kişisel veri YOK). Dosya: test\sema-ornekleri\v11-ornek.db.
/// Kaynak: dondurulmuş v10 örneği (V10Ornegi) kopyalanır ve SozDb.MigrateTo(11) ile v11'e göç ettirilir; yani v11 şemasının
/// v12 koduyla DEĞİŞMEDEN göç ettiği yerde üretilir. Yeniden üretmek için SOZ_SEMA_URET=1 ile GeriAlmaGocTests.V11_ornek_yeniden_uretilebilir koşulur.
/// </summary>
internal static class V11Ornegi
{
    public static string DosyaYolu => Path.Combine(TestEnv.RepoRoot, "test", "sema-ornekleri", "v11-ornek.db");

    /// <summary><paramref name="dataDir"/>\soz.db olarak sürüm 11 örneği üretir.</summary>
    public static void Uret(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.Copy(V10Ornegi.DosyaYolu, Path.Combine(dataDir, "soz.db"), overwrite: true);
        var onceki = Time.UtcNow;
        Time.UtcNow = () => new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        try
        {
            var db = new SozDb(dataDir);
            db.MigrateTo(11);
            using (var c = db.Open())
            {
                c.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
                c.Exec("PRAGMA journal_mode=DELETE;"); // taahhütlü dosya tek parça kalsın; sonraki göç WAL'a kendisi geçer
            }
        }
        finally
        {
            Time.UtcNow = onceki;
            SqliteConnection.ClearAllPools();
        }
    }
}
