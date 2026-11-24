using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Şema v14 (jobs.write_check_first, k1): dondurulmuş v13 örneğinden göç, hata enjeksiyonu, ikinci açılış.</summary>
public class GocV14Tests
{
    private static SqliteConnection Ham(string dosya, bool salt = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dosya, Mode = salt ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static bool SutunVar(SqliteConnection c) => c.Query("PRAGMA table_info(jobs)", r => r.GetString(1)).Contains("write_check_first");

    [Fact]
    public void V13_ornek_yeniden_uretilebilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "uret");
        V13Ornegi.Uret(dir);
        var yeni = Path.Combine(dir, "soz.db");
        Assert.False(File.Exists(yeni + "-wal"), "wal artığı kalmamalı");
        if (Environment.GetEnvironmentVariable("SOZ_SEMA_URET") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(V13Ornegi.DosyaYolu)!);
            File.Copy(yeni, V13Ornegi.DosyaYolu, overwrite: true);
        }
        Assert.True(File.Exists(V13Ornegi.DosyaYolu), "test/sema-ornekleri/v13-ornek.db yok (SOZ_SEMA_URET=1 ile üret)");

        string Ozet(string dosya)
        {
            var kopya = Path.Combine(env.Root, Guid.NewGuid().ToString("N")[..8] + ".db");
            File.Copy(dosya, kopya);
            using var c = Ham(kopya, salt: true);
            var sema = c.Query("SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name", r => r.GetString(0) + "|" + r.GetString(1) + "|" + r.Str(2));
            var sayilar = c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0))
                .Select(t => t + "=" + c.ScalarLong($"SELECT COUNT(*) FROM \"{t}\""));
            return c.ScalarLong("PRAGMA user_version") + "\n" + string.Join("\n", sema) + "\n" + string.Join(",", sayilar);
        }
        Assert.Equal(Ozet(V13Ornegi.DosyaYolu), Ozet(yeni)); // taahhütlü dosya = üreticinin çıktısı
    }

    [Fact]
    public void V13_ornegi_acilinca_v14_olur_yedek_alinir_veri_korunur_sutun_varsayilan_sifir()
    {
        Assert.True(File.Exists(V13Ornegi.DosyaYolu), "test/sema-ornekleri/v13-ornek.db yok (SOZ_SEMA_URET=1 ile üret)");
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g14");
        Directory.CreateDirectory(dir);
        var dosya = Path.Combine(dir, "soz.db");
        File.Copy(V13Ornegi.DosyaYolu, dosya);
        var kopya = Path.Combine(env.Root, "v13-kopya.db");
        File.Copy(V13Ornegi.DosyaYolu, kopya);
        Dictionary<string, long> once;
        using (var c = Ham(kopya, salt: true))
        {
            Assert.Equal(13L, c.ScalarLong("PRAGMA user_version"));
            Assert.False(SutunVar(c));                                   // örnek v14 sütunsuz
            once = c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", r => r.GetString(0)).ToDictionary(t => t, t => c.ScalarLong($"SELECT COUNT(*) FROM \"{t}\""));
            Assert.True(once["jobs"] >= 1);                               // örnekte iş satırları var (ek olarak "Örnek iş"): göç onları korumalı
        }

        var db = new SozDb(dir);
        db.MigrateTo(14); // Bu test dondurulmuş v14 sözleşmesini sınar. Güncel v15 ayrı testlidir.
        Assert.Equal(14, db.GetVersion());
        SqliteConnection.ClearAllPools();

        var yedek = Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v13-*.db"));
        using (var y = Ham(yedek, salt: true))
        {
            Assert.Equal(13L, y.ScalarLong("PRAGMA user_version"));      // yedek göçten önceki hâl
            Assert.False(SutunVar(y));
            Assert.Equal("ok", y.Scalar("PRAGMA quick_check") as string);
        }
        using var c2 = Ham(dosya);
        Assert.Equal(14L, c2.ScalarLong("PRAGMA user_version"));
        Assert.Equal("ok", c2.Scalar("PRAGMA quick_check") as string);
        Assert.True(SutunVar(c2));
        foreach (var (t, n) in once)
        {
            if (t is "event_log" or "schema_meta" or "event_kind") continue;
            Assert.Equal(n, c2.ScalarLong($"SELECT COUNT(*) FROM \"{t}\""));   // hiçbir satır kaybolmadı ya da çoğalmadı
        }
        Assert.Equal(0L, c2.ScalarLong("SELECT COUNT(*) FROM jobs WHERE write_check_first<>0"));  // eski işler: varsayılan 0
        var ornekId = c2.ScalarLong("SELECT id FROM jobs WHERE title='Örnek iş'");
        Assert.True(ornekId > 0);
        var meta = c2.Query("SELECT version, backup_file FROM schema_meta ORDER BY version", r => (v: r.GetInt64(0), b: r.IsDBNull(1) ? null : r.GetString(1))).ToList();
        Assert.Equal(14L, meta[^1].v);
        Assert.Equal(Path.GetFileName(yedek), meta[^1].b);
        var olaylar = c2.Query("SELECT kind, payload FROM event_log ORDER BY seq", r => (k: r.GetString(0), p: r.GetString(1))).ToList();
        var son = olaylar.Last(o => o.k == "migration.applied");
        Assert.Equal(13, (int)JsonNode.Parse(son.p)!["from"]!);
        Assert.Equal(14, (int)JsonNode.Parse(son.p)!["to"]!);
        Assert.Contains(olaylar, o => o.k == "backup.taken" && (string)JsonNode.Parse(o.p)!["file"]! == Path.GetFileName(yedek));
        Assert.Throws<SqliteException>(() => c2.Exec("UPDATE event_log SET kind='x'"));   // ekle-yalnız koruması sürüyor

        // Eski iş yeni kodla okunur: bayrak kapalı.
        using var oku = db.Open();
        var j = JobStore.Get(oku, ornekId)!;
        Assert.False(j.WriteCheckFirst);
        Assert.Equal("Örnek iş", j.Title);
    }

    [Fact]
    public void V14_ikinci_acilista_bir_sey_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g14b");
        Directory.CreateDirectory(dir);
        File.Copy(V13Ornegi.DosyaYolu, Path.Combine(dir, "soz.db"));
        SozDb.OpenAndMigrate(dir);
        SqliteConnection.ClearAllPools();
        long e1, m1; var nullUid = "";
        using (var c = Ham(Path.Combine(dir, "soz.db"))) { e1 = c.ScalarLong("SELECT COUNT(*) FROM event_log"); m1 = c.ScalarLong("SELECT COUNT(*) FROM schema_meta"); foreach (var t in SozDb.UidTables) { var n = c.ScalarLong($"SELECT COUNT(*) FROM {t.Table} WHERE uid IS NULL"); if (n > 0) nullUid += t.Table + "=" + n + " "; } }
        SozDb.OpenAndMigrate(dir);
        SqliteConnection.ClearAllPools();
        using var c3 = Ham(Path.Combine(dir, "soz.db"));
        Assert.True(e1 == c3.ScalarLong("SELECT COUNT(*) FROM event_log"), "ikinci açılışta eklenen olay (ilk açılıştan sonra uid boş: " + nullUid + "): " + string.Join(" | ", c3.Query("SELECT kind || ' ' || payload FROM event_log ORDER BY seq DESC LIMIT 3", r => r.GetString(0))));
        Assert.Equal(m1, c3.ScalarLong("SELECT COUNT(*) FROM schema_meta"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v13-*.db")); // ikinci açılışta yeni yedek yok
    }

    [Fact]
    public void Goc_ortasinda_hata_olursa_v13_kalir_sutun_yok_yedek_durur_ve_yeniden_deneme_calisir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g14c");
        Directory.CreateDirectory(dir);
        var dosya = Path.Combine(dir, "soz.db");
        File.Copy(V13Ornegi.DosyaYolu, dosya);
        var db = new SozDb(dir) { MigrationFault = adim => { if (adim == "v14.sema") throw new IOException("enjekte hata"); } };
        Assert.Throws<IOException>(() => db.Migrate());
        SqliteConnection.ClearAllPools();
        using (var c = Ham(dosya, salt: true))
        {
            Assert.Equal(13L, c.ScalarLong("PRAGMA user_version"));    // işlem geri alındı: yarım şema yok
            Assert.False(SutunVar(c));
        }
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v13-*.db"));
        var ikinci = new SozDb(dir);
        ikinci.MigrateTo(14);
        Assert.Equal(14, ikinci.GetVersion());
        using var c2 = ikinci.Open();
        Assert.True(SutunVar(c2));
    }
}
