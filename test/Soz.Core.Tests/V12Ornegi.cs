using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Tests;

/// <summary>
/// Sentetik v12 veritabanı üreticisi (gerçek kişisel veri YOK). Dosya: test\sema-ornekleri\v12-ornek.db.
/// Kaynak: dondurulmuş v11 örneği kopyalanır ve SozDb.MigrateTo(12) ile v12'ye göç ettirilir; yani keşif (v13) adımı gelmeden önceki hâli.
/// Yeniden üretmek için SOZ_SEMA_URET=1 ile KesifGocTests.V12_ornek_yeniden_uretilebilir koşulur.
/// </summary>
internal static class V12Ornegi
{
    public static string DosyaYolu => Path.Combine(TestEnv.RepoRoot, "test", "sema-ornekleri", "v12-ornek.db");

    /// <summary><paramref name="dataDir"/>\soz.db olarak sürüm 12 örneği üretir.</summary>
    public static void Uret(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.Copy(V11Ornegi.DosyaYolu, Path.Combine(dataDir, "soz.db"), overwrite: true);
        var onceki = Time.UtcNow;
        Time.UtcNow = () => new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        try
        {
            var db = new SozDb(dataDir);
            db.MigrateTo(12);
            using (var c = db.Open())
            {
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
