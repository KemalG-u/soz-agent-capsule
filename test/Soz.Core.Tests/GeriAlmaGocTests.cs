using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Şema v12 (geri alma): dondurulmuş v11 örneğinden göç, ileri koruma ve olay türleri.</summary>
public class GeriAlmaGocTests
{
    private static SqliteConnection Ham(string dosya, bool salt = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dosya, Mode = salt ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    [Fact]
    public void V11_ornek_yeniden_uretilebilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "uret");
        V11Ornegi.Uret(dir);
        var yeni = Path.Combine(dir, "soz.db");
        Assert.False(File.Exists(yeni + "-wal"), "wal artığı kalmamalı");

        if (Environment.GetEnvironmentVariable("SOZ_SEMA_URET") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(V11Ornegi.DosyaYolu)!);
            File.Copy(yeni, V11Ornegi.DosyaYolu, overwrite: true);
        }

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
        Assert.Equal(Ozet(V11Ornegi.DosyaYolu), Ozet(yeni)); // taahhütlü dosya = üreticinin çıktısı
    }

    // ---------- göç v11 -> v12 ----------

    private static Dictionary<string, long> Sayilar(SqliteConnection c)
        => c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0))
            .ToDictionary(t => t, t => c.ScalarLong($"SELECT COUNT(*) FROM \"{t}\""));

    [Fact]
    public void V11_ornegi_acilinca_v12_olur_yedek_alinir_veri_korunur_olaylar_yazilir()
    {
        Assert.True(File.Exists(V11Ornegi.DosyaYolu), "test/sema-ornekleri/v11-ornek.db yok (SOZ_SEMA_URET=1 ile üret)");
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g12");
        Directory.CreateDirectory(dir);
        var dosya = Path.Combine(dir, "soz.db");
        File.Copy(V11Ornegi.DosyaYolu, dosya);
        var kopya = Path.Combine(env.Root, "v11-kopya.db");
        File.Copy(V11Ornegi.DosyaYolu, kopya);
        Dictionary<string, long> once;
        using (var c = Ham(kopya, salt: true)) { Assert.Equal(11L, c.ScalarLong("PRAGMA user_version")); once = Sayilar(c); }
        Assert.DoesNotContain("undo", once.Keys);

        var db = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion()); // zincirin sonu (v13 dahil); v12 adımı aşağıda ayrıca denetlenir
        SqliteConnection.ClearAllPools();

        var yedek = Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v11-*.db"));
        using (var y = Ham(yedek, salt: true))
        {
            Assert.Equal(11L, y.ScalarLong("PRAGMA user_version")); // yedek göçten önceki hâl
            Assert.Equal("ok", y.Scalar("PRAGMA quick_check") as string);
        }
        using var c2 = Ham(dosya);
        Assert.Equal((long)SozDb.CurrentVersion, c2.ScalarLong("PRAGMA user_version"));
        Assert.Equal("ok", c2.Scalar("PRAGMA quick_check") as string);
        var sonra = Sayilar(c2);
        foreach (var (t, n) in once)
        {
            if (t is "event_log" or "schema_meta") continue;
            if (t == "event_kind") { Assert.True(sonra.TryGetValue(t, out var k), t); Assert.True(k >= n); continue; } // tohum: sonraki şema adımları kendi olay türlerini ekler, eskisi kaybolmaz
            Assert.True(sonra.TryGetValue(t, out var m), "tablo kayboldu: " + t);
            Assert.Equal(n, m); // hiçbir veri satırı kaybolmadı ya da çoğalmadı
        }
        Assert.Equal(0L, sonra["undo"]);

        // runs: dört yeni sütun, eski satırlarda boş
        var kolonlar = c2.Query("SELECT name FROM pragma_table_info('runs')", r => r.GetString(0));
        foreach (var k in new[] { "before_snapshot_uid", "after_snapshot_uid", "protection", "protection_note" }) Assert.Contains(k, kolonlar);
        Assert.Equal(0L, c2.ScalarLong("SELECT COUNT(*) FROM runs WHERE before_snapshot_uid IS NOT NULL OR protection IS NOT NULL"));
        // undo tablosu
        var undoK = c2.Query("SELECT name FROM pragma_table_info('undo')", r => r.GetString(0));
        foreach (var k in new[] { "uid", "job_id", "run_id", "target_snapshot_uid", "pre_snapshot_uid", "post_snapshot_uid", "mode", "status", "started_at", "finished_at", "files_restored", "files_removed", "files_skipped", "conflicts_json", "error" })
            Assert.Contains(k, undoK);
        Assert.Contains("ix_undo_job", c2.Query("SELECT name FROM sqlite_master WHERE type='index'", r => r.GetString(0)));

        // schema_meta ve ekle-yalnız olaylar
        var meta = c2.Query("SELECT version, backup_file FROM schema_meta ORDER BY version", r => (v: r.GetInt64(0), y: r.Str(1)));
        // v12 adımının satırı göç öncesi yedeği taşır (sonraki adımlar zincirin ortasında yedek yazmaz)
        Assert.Equal(Path.GetFileName(yedek), meta.Single(m => m.v == 12).y);
        Assert.Equal((long)SozDb.CurrentVersion, meta[^1].v);
        var olaylar = c2.Query("SELECT kind, payload FROM event_log ORDER BY seq", r => (k: r.GetString(0), p: r.GetString(1)));
        var son2 = olaylar.TakeLast(2).Select(o => o.k).ToArray();
        Assert.Equal(new[] { "backup.taken", "migration.applied" }, son2);
        var uygulandi = JsonNode.Parse(olaylar[^1].p)!;
        Assert.Equal(11, (int)uygulandi["from"]!);
        Assert.Equal(12, (int)uygulandi["to"]!);
        Assert.Throws<SqliteException>(() => c2.Exec("UPDATE event_log SET kind='x'")); // ekle-yalnız koruması sürüyor
        Assert.Throws<SqliteException>(() => c2.Exec("DELETE FROM event_log"));
    }

    [Fact]
    public void V12_goc_ikinci_kez_acilinca_bir_sey_yapmaz()
    {
        Assert.True(File.Exists(V11Ornegi.DosyaYolu));
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g12b");
        Directory.CreateDirectory(dir);
        File.Copy(V11Ornegi.DosyaYolu, Path.Combine(dir, "soz.db"));
        SozDb.OpenAndMigrate(dir);
        SqliteConnection.ClearAllPools();
        long e1, m1;
        using (var c = Ham(Path.Combine(dir, "soz.db"))) { e1 = c.ScalarLong("SELECT COUNT(*) FROM event_log"); m1 = c.ScalarLong("SELECT COUNT(*) FROM schema_meta"); }
        var db = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion()); // zincirin sonu (v13 dahil); v12 adımı aşağıda ayrıca denetlenir
        SqliteConnection.ClearAllPools();
        using var c3 = Ham(Path.Combine(dir, "soz.db"));
        Assert.Equal(e1, c3.ScalarLong("SELECT COUNT(*) FROM event_log"));
        Assert.Equal(m1, c3.ScalarLong("SELECT COUNT(*) FROM schema_meta"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v11-*.db")); // ikinci açılışta yeni yedek yok
    }

    [Fact]
    public void Goc_ortasinda_hata_olursa_veritabani_v11_kalir_yedek_durur_ve_yeniden_deneme_calisir()
    {
        Assert.True(File.Exists(V11Ornegi.DosyaYolu));
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g12c");
        Directory.CreateDirectory(dir);
        var dosya = Path.Combine(dir, "soz.db");
        File.Copy(V11Ornegi.DosyaYolu, dosya);
        var db = new SozDb(dir) { MigrationFault = adim => { if (adim == "v12.sema") throw new IOException("enjekte hata"); } };
        Assert.Throws<IOException>(() => db.Migrate());
        SqliteConnection.ClearAllPools();
        using (var c = Ham(dosya, salt: true))
        {
            Assert.Equal(11L, c.ScalarLong("PRAGMA user_version")); // işlem geri alındı: yarım şema yok
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name='undo'"));
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name IN ('before_snapshot_uid','protection')"));
        }
        var yedek1 = Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v11-*.db"));
        Assert.True(File.Exists(yedek1));
        var ikinci = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, ikinci.GetVersion());
    }

    [Fact]
    public void Yeni_bos_veritabani_dogrudan_v12_olur_olay_kinleri_ve_undo_var()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g12d");
        var db = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion()); // zincirin sonu (v13 dahil); v12 adımı aşağıda ayrıca denetlenir
        using var c = db.Open();
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='undo'"));
        Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM runs WHERE protection IS NOT NULL"));
        Assert.False(Directory.Exists(Path.Combine(dir, "yedek")) && Directory.GetFiles(Path.Combine(dir, "yedek")).Length > 0); // boş veritabanında yedek yok
    }
}
