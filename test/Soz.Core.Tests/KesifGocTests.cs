using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Discovery;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Şema v13 (keşif tabloları, d2m): dondurulmuş v12 örneğinden göç, hata enjeksiyonu ve olay türü tutarlılığı.</summary>
public class KesifGocTests
{
    private static SqliteConnection Ham(string dosya, bool salt = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dosya, Mode = salt ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static Dictionary<string, long> Sayilar(SqliteConnection c)
        => c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0))
            .ToDictionary(t => t, t => c.ScalarLong($"SELECT COUNT(*) FROM \"{t}\""));

    [Fact]
    public void V12_ornek_yeniden_uretilebilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "uret");
        V12Ornegi.Uret(dir);
        var yeni = Path.Combine(dir, "soz.db");
        Assert.False(File.Exists(yeni + "-wal"), "wal artığı kalmamalı");
        if (Environment.GetEnvironmentVariable("SOZ_SEMA_URET") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(V12Ornegi.DosyaYolu)!);
            File.Copy(yeni, V12Ornegi.DosyaYolu, overwrite: true);
        }
        Assert.True(File.Exists(V12Ornegi.DosyaYolu), "test/sema-ornekleri/v12-ornek.db yok (SOZ_SEMA_URET=1 ile üret)");

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
        Assert.Equal(Ozet(V12Ornegi.DosyaYolu), Ozet(yeni)); // taahhütlü dosya = üreticinin çıktısı
    }

    [Fact]
    public void V12_ornegi_acilinca_v13_olur_yedek_alinir_veri_korunur_kesif_ve_olay_turleri_kurulur()
    {
        Assert.True(File.Exists(V12Ornegi.DosyaYolu), "test/sema-ornekleri/v12-ornek.db yok (SOZ_SEMA_URET=1 ile üret)");
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g13");
        Directory.CreateDirectory(dir);
        var dosya = Path.Combine(dir, "soz.db");
        File.Copy(V12Ornegi.DosyaYolu, dosya);
        var kopya = Path.Combine(env.Root, "v12-kopya.db");
        File.Copy(V12Ornegi.DosyaYolu, kopya);
        Dictionary<string, long> once;
        using (var c = Ham(kopya, salt: true))
        {
            Assert.Equal(12L, c.ScalarLong("PRAGMA user_version"));
            once = Sayilar(c);
            Assert.False(DiscoverySchema.IsInstalled(c));              // örnek keşifsiz
        }

        var db = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion()); // zincirin sonu (v14 dahil); v13 adımı aşağıda ayrıca denetlenir
        SqliteConnection.ClearAllPools();

        var yedek = Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v12-*.db"));
        using (var y = Ham(yedek, salt: true))
        {
            Assert.Equal(12L, y.ScalarLong("PRAGMA user_version"));    // yedek göçten önceki hâl
            Assert.Equal("ok", y.Scalar("PRAGMA quick_check") as string);
        }
        using var c2 = Ham(dosya);
        Assert.Equal((long)SozDb.CurrentVersion, c2.ScalarLong("PRAGMA user_version"));
        Assert.Equal("ok", c2.Scalar("PRAGMA quick_check") as string);
        Assert.True(DiscoverySchema.IsInstalled(c2));                  // yedi keşif tablosu kuruldu
        var sonra = Sayilar(c2);
        foreach (var (t, n) in once)
        {
            if (t is "event_log" or "schema_meta" or "event_kind") continue;
            Assert.True(sonra.TryGetValue(t, out var m), "tablo kayboldu: " + t);
            Assert.Equal(n, m);                                        // hiçbir veri satırı kaybolmadı ya da çoğalmadı
        }
        // olay türleri: tohum eksiksiz ve eski türler kaybolmadı
        var turler = c2.Query("SELECT kind FROM event_kind", r => r.GetString(0)).ToHashSet();
        foreach (var k in EventKindCatalog.All) Assert.Contains(k.Name, turler);
        Assert.True(sonra["event_kind"] >= once["event_kind"]);
        foreach (var (kind, _) in DiscoverySchema.EventKinds) Assert.Contains(kind, turler);
        // göç kaydı ve olay
        var meta = c2.Query("SELECT version, backup_file FROM schema_meta ORDER BY version", r => (v: r.GetInt64(0), b: r.IsDBNull(1) ? null : r.GetString(1))).ToList();
        var m13 = meta.Single(x => x.v == 13L);
        Assert.Equal(Path.GetFileName(yedek), m13.b);
        var olaylar = c2.Query("SELECT kind, payload FROM event_log ORDER BY seq", r => (k: r.GetString(0), p: r.GetString(1))).ToList();
        var son = olaylar.Last(o => o.k == "migration.applied" && (int)JsonNode.Parse(o.p)!["to"]! == 13);
        Assert.Equal(12, (int)JsonNode.Parse(son.p)!["from"]!);
        Assert.Contains(olaylar, o => o.k == "backup.taken" && (string)JsonNode.Parse(o.p)!["file"]! == Path.GetFileName(yedek));
        Assert.Throws<SqliteException>(() => c2.Exec("UPDATE event_log SET kind='x'"));   // ekle-yalnız koruması sürüyor
    }

    [Fact]
    public void V13_ikinci_acilista_bir_sey_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g13b");
        Directory.CreateDirectory(dir);
        File.Copy(V12Ornegi.DosyaYolu, Path.Combine(dir, "soz.db"));
        SozDb.OpenAndMigrate(dir);
        SqliteConnection.ClearAllPools();
        long e1, m1, k1;
        using (var c = Ham(Path.Combine(dir, "soz.db"))) { e1 = c.ScalarLong("SELECT COUNT(*) FROM event_log"); m1 = c.ScalarLong("SELECT COUNT(*) FROM schema_meta"); k1 = c.ScalarLong("SELECT COUNT(*) FROM event_kind"); }
        SozDb.OpenAndMigrate(dir);
        SqliteConnection.ClearAllPools();
        using var c3 = Ham(Path.Combine(dir, "soz.db"));
        Assert.Equal(e1, c3.ScalarLong("SELECT COUNT(*) FROM event_log"));
        Assert.Equal(m1, c3.ScalarLong("SELECT COUNT(*) FROM schema_meta"));
        Assert.Equal(k1, c3.ScalarLong("SELECT COUNT(*) FROM event_kind"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v12-*.db")); // ikinci açılışta yeni yedek yok
    }

    [Fact]
    public void Goc_ortasinda_hata_olursa_veritabani_v12_kalir_kesif_tablosu_yok_yedek_durur_ve_yeniden_deneme_calisir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g13c");
        Directory.CreateDirectory(dir);
        var dosya = Path.Combine(dir, "soz.db");
        File.Copy(V12Ornegi.DosyaYolu, dosya);
        var db = new SozDb(dir) { MigrationFault = adim => { if (adim == "v13.sema") throw new IOException("enjekte hata"); } };
        Assert.Throws<IOException>(() => db.Migrate());
        SqliteConnection.ClearAllPools();
        using (var c = Ham(dosya, salt: true))
        {
            Assert.Equal(12L, c.ScalarLong("PRAGMA user_version"));   // işlem geri alındı: yarım şema yok
            Assert.False(DiscoverySchema.IsInstalled(c));
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'discovery_%'"));
        }
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v12-*.db"));
        var ikinci = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, ikinci.GetVersion());
        using var c2 = ikinci.Open();
        Assert.True(DiscoverySchema.IsInstalled(c2));
    }

    [Fact]
    public void Daha_yeni_surum_reddedilir_ve_dosya_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "g13d");
        Directory.CreateDirectory(dir);
        var dosya = Path.Combine(dir, "soz.db");
        File.Copy(V12Ornegi.DosyaYolu, dosya);
        using (var c = Ham(dosya)) c.Exec($"PRAGMA user_version={SozDb.CurrentVersion + 1};");
        SqliteConnection.ClearAllPools();
        var once = File.ReadAllBytes(dosya);
        Assert.ThrowsAny<Exception>(() => SozDb.OpenAndMigrate(dir));
        SqliteConnection.ClearAllPools();
        Assert.Equal(once, File.ReadAllBytes(dosya));
    }

    // d2m: şeritlerin "ilk kullanımda kaydettiği" olay türleri artık tek katalogda. Kodda yazılan her sabit tür adı katalogda olmalı (yeni tür eklemek: katalog satırı).
    [Fact]
    public void Koddaki_her_sabit_olay_turu_event_kind_katalogunda_var()
    {
        var kok = Path.Combine(TestEnv.RepoRoot, "src");
        var katalog = EventKindCatalog.All.Select(k => k.Name).ToHashSet();
        var desen = new Regex("""Events\.Append\(\s*[a-zA-Z_]+\s*,\s*"([a-z][a-z0-9_.\-]*)"\s*,""", RegexOptions.CultureInvariant);
        var bulunan = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dosya in Directory.EnumerateFiles(kok, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            foreach (Match m in desen.Matches(File.ReadAllText(dosya))) bulunan.Add(m.Groups[1].Value);
        Assert.NotEmpty(bulunan);
        var eksik = bulunan.Where(k => !katalog.Contains(k)).ToList();
        Assert.True(eksik.Count == 0, "event_kind kataloğunda olmayan olay türleri: " + string.Join(", ", eksik));
        // katalogdaki adlar kuralına uyar ve tekil
        Assert.Equal(EventKindCatalog.All.Count, katalog.Count);
        Assert.All(katalog, k => Assert.Matches(@"^[a-z][a-z0-9_\-]*(\.[a-z][a-z0-9_\-]*)+$", k));
    }
}
