using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Dalga 1-A (veri zemini): şema v10 → v11, göç korumaları, yedek, kararlı kimlikler, ekle-yalnız olay kaydı, iş türü.</summary>
public class VeriZeminiTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public VeriZeminiTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static readonly Regex UuidV7 = new("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.Compiled);
    private static readonly string[] UidTablolari = { "projects", "jobs", "runs", "proof_runs", "checks", "guard_rules", "decisions", "run_approvals" };

    // ---------- yardımcılar ----------

    private static SqliteConnection Ham(string dosya, bool salt = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dosya, Mode = salt ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static string Sha(string dosya) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dosya)));

    /// <summary>Taahhütlü v10 örneğini &lt;kök&gt;\&lt;ad&gt;\soz.db olarak kopyalar, veri klasörünü döndürür.</summary>
    private static string OrnekKopya(TestEnv env, string ad)
    {
        Assert.True(File.Exists(V10Ornegi.DosyaYolu), "test\\sema-ornekleri\\v10-ornek.db yok (SOZ_SEMA_URET=1 ile üret)");
        var dir = Path.Combine(env.Root, ad);
        Directory.CreateDirectory(dir);
        File.Copy(V10Ornegi.DosyaYolu, Path.Combine(dir, "soz.db"));
        return dir;
    }

    private static long Say(SqliteConnection c, string tablo) => c.ScalarLong($"SELECT COUNT(*) FROM {tablo}");

    private static long UidMs(string uid) => Convert.ToInt64(uid[..8] + uid.Substring(9, 4), 16);

    // ---------- A0: v10 örneği ----------

    [Fact]
    public void V10_ornek_dosyasi_var_surum_10_ve_veri_dolu()
    {
        using var env = new TestEnv(copySamples: false);
        Assert.True(File.Exists(V10Ornegi.DosyaYolu));
        var kopya = Path.Combine(env.Root, "o.db");
        File.Copy(V10Ornegi.DosyaYolu, kopya);
        using var c = Ham(kopya, salt: true);
        Assert.Equal(10L, c.ScalarLong("PRAGMA user_version"));
        Assert.Equal(0L, c.ScalarLong("PRAGMA application_id"));
        Assert.Equal(4L, Say(c, "projects") ); // Genel + 3
        Assert.Equal(4L, Say(c, "jobs"));
        Assert.Equal(5L, Say(c, "runs"));
        Assert.Equal(5L, Say(c, "proof_runs"));
        Assert.Equal(4L, Say(c, "checks"));
        Assert.Equal(3L, Say(c, "guard_rules"));
        Assert.Equal(4L, Say(c, "decisions"));
        Assert.Equal(2L, Say(c, "run_approvals"));
        Assert.Equal(14L, Say(c, "events"));
        Assert.Equal(3L, Say(c, "settings"));
        Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('event_log','device','schema_meta')"));
    }

    [Fact]
    public void V10_ornek_yeniden_uretilebilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "uret");
        V10Ornegi.Uret(dir);
        var yeni = Path.Combine(dir, "soz.db");
        Assert.False(File.Exists(yeni + "-wal"), "wal artığı kalmamalı");

        if (Environment.GetEnvironmentVariable("SOZ_SEMA_URET") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(V10Ornegi.DosyaYolu)!);
            File.Copy(yeni, V10Ornegi.DosyaYolu, overwrite: true);
        }

        string Ozet(string dosya)
        {
            var kopya = Path.Combine(env.Root, Guid.NewGuid().ToString("N")[..8] + ".db"); // taahhütlü dosyanın yanında -wal/-shm doğmasın
            File.Copy(dosya, kopya);
            using var c = Ham(kopya, salt: true);
            var sema = c.Query("SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name", r => r.GetString(0) + "|" + r.GetString(1) + "|" + r.Str(2));
            var sayilar = c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0))
                .Select(t => t + "=" + c.ScalarLong($"SELECT COUNT(*) FROM \"{t}\""));
            return c.ScalarLong("PRAGMA user_version") + "\n" + string.Join("\n", sema) + "\n" + string.Join(",", sayilar);
        }
        Assert.Equal(Ozet(V10Ornegi.DosyaYolu), Ozet(yeni)); // taahhütlü dosya = üreticinin çıktısı
    }

    // ---------- A1: göç korumaları ----------

    [Fact]
    public void Daha_yeni_surum_istisna_atar_ve_dosya_bayti_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "yeni");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "soz.db");
        using (var c = Ham(CreateFile(db)))
        {
            c.Exec($"CREATE TABLE gelecek(x TEXT); INSERT INTO gelecek VALUES('gelecek sürüm verisi'); PRAGMA user_version={SozDb.CurrentVersion + 1};"); // bu kodun bilmediği, bir sonraki sürüm
        }
        var once = Sha(db);

        var ex = Assert.Throws<SozSchemaTooNewException>(() => SozDb.OpenAndMigrate(dir));
        Assert.Equal(SozDb.CurrentVersion + 1, ex.FoundVersion);
        Assert.Contains("Söz'ü güncelle", ex.Message);
        Assert.Equal(once, Sha(db));
        Assert.False(Directory.Exists(Path.Combine(dir, "yedek")), "yedek klasörü açılmamalı");
        Assert.False(File.Exists(db + "-wal") || File.Exists(db + "-shm"), "wal/shm artığı kalmamalı");
    }

    private static string CreateFile(string path)
    {
        // SQLite dosyayı ReadWrite açılışta oluşturmaz; ReadWriteCreate ile bir kez aç.
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        c.Exec("PRAGMA user_version=0;");
        return path;
    }

    [Fact]
    public void Baska_programin_dosyasi_acilmaz_ve_bayt_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "yabanci");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "soz.db");
        using (var c = Ham(CreateFile(db))) c.Exec("CREATE TABLE baska(x); PRAGMA application_id=1234567; PRAGMA user_version=3;");
        var once = Sha(db);
        var ex = Assert.Throws<SozForeignDatabaseException>(() => SozDb.OpenAndMigrate(dir));
        Assert.Equal(1234567L, ex.ApplicationId);
        Assert.Equal(once, Sha(db));
        Assert.False(Directory.Exists(Path.Combine(dir, "yedek")));

        // SQLite dosyası olmayan bayt yığını da tanınır
        var dir2 = Path.Combine(env.Root, "cop");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, "soz.db"), new string('x', 4096));
        Assert.Throws<SozForeignDatabaseException>(() => SozDb.OpenAndMigrate(dir2));
    }

    [Fact]
    public void V10_ornegi_acilinca_yedek_alinir_quick_check_ok_ve_surum_11_olur()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "g1");
        var db = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion()); // zincirin sonu; ara adımlar aşağıda schema_meta'dan denetlenir
        var yedek = Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v10-*.db"));
        Assert.Matches(@"once-v10-\d{12}-[0-9a-f]{8}\.db$", yedek);
        using (var y = Ham(yedek, salt: true))
        {
            Assert.Equal("ok", y.Scalar("PRAGMA quick_check"));
            Assert.Equal(10L, y.ScalarLong("PRAGMA user_version")); // yedek göçten önceki hâl
            Assert.Equal(4L, Say(y, "projects"));
            Assert.Equal(14L, Say(y, "events"));
        }
        using var c = db.Open();
        Assert.Equal(SozDb.ApplicationId, c.ScalarLong("PRAGMA application_id"));
        Assert.Equal(1397709361L, SozDb.ApplicationId);
        // sürüm geçmişi
        var meta = c.Query("SELECT version, note, backup_file FROM schema_meta ORDER BY version", r => (v: r.GetInt64(0), note: r.Str(1), yedek: r.Str(2)));
        Assert.True(meta.Count >= 3, "v10 devralındı, v11, v12 ve sonraki adımlar");
        Assert.Equal(10L, meta[0].v);
        Assert.Equal("devralındı", meta[0].note);
        Assert.Null(meta[0].yedek);
        Assert.Equal(11L, meta[1].v);
        Assert.Equal(Path.GetFileName(yedek), meta[1].yedek);
        Assert.Equal(12L, meta[2].v); // aynı açılışta v12 adımı da çalışır (geri alma tabloları); yedek v11 adımına yazılıdır
        Assert.Null(meta[2].yedek);
        Assert.Equal((long)SozDb.CurrentVersion, meta[^1].v); // zincirin son adımı (sonraki adımlar yedek yazmaz)
        Assert.Equal(meta.Count, meta.Select(m => m.v).Distinct().Count());
    }

    [Fact]
    public void Yeni_bos_veritabaninda_yedek_alinmaz_ve_application_id_yazilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "bos");
        var db = SozDb.OpenAndMigrate(dir);
        Assert.False(Directory.Exists(Path.Combine(dir, "yedek")) && Directory.GetFiles(Path.Combine(dir, "yedek")).Length > 0);
        using var c = db.Open();
        Assert.Equal((long)SozDb.CurrentVersion, c.ScalarLong("PRAGMA user_version"));
        Assert.Equal(1397709361L, c.ScalarLong("PRAGMA application_id"));
        var kinds = c.Query("SELECT kind FROM event_log ORDER BY seq", r => r.GetString(0));
        Assert.Equal(new[] { "migration.applied" }, kinds); // yedek yok → backup.taken yok
        // yeni boş veritabanı zinciri sıfırdan koşar: 11 ile başlar, her adım bir satır, son satır güncel sürüm (sayı sabit değil)
        var surumler = c.Query("SELECT version FROM schema_meta ORDER BY version", r => r.GetInt64(0)).ToArray();
        Assert.Equal(11L, surumler[0]);
        Assert.Equal((long)SozDb.CurrentVersion, surumler[^1]);
        Assert.Equal(surumler.Length, surumler.Distinct().Count());
    }

    [Fact]
    public void Bozuk_yedek_olursa_goc_baslamaz_ve_veri_dokunulmadan_kalir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "g2");
        var dbDosya = Path.Combine(dir, "soz.db");
        var once = Sha(dbDosya);
        var db = new SozDb(dir) { AfterBackup = yol => File.WriteAllBytes(yol, new byte[8192]) };
        var ex = Assert.Throws<SozBackupFailedException>(() => db.Migrate());
        Assert.Contains("yedek", ex.Message, StringComparison.OrdinalIgnoreCase);
        SqliteConnection.ClearAllPools();
        Assert.Equal(once, Sha(dbDosya));
        using var c = Ham(dbDosya, salt: true);
        Assert.Equal(10L, c.ScalarLong("PRAGMA user_version"));
        Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name='event_log'"));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-*.db")); // bozuk yedek bırakılmaz
    }

    [Fact]
    public void Goc_ortasinda_hata_olursa_veritabani_v10_kalir_yedek_durur_ve_yeniden_deneme_calisir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "g3");
        var dbDosya = Path.Combine(dir, "soz.db");
        var db = new SozDb(dir) { MigrationFault = adim => { if (adim == "v11.eski_olaylar") throw new IOException("enjekte hata"); } };
        Assert.Throws<IOException>(() => db.Migrate());
        SqliteConnection.ClearAllPools();
        using (var c = Ham(dbDosya, salt: true))
        {
            Assert.Equal(10L, c.ScalarLong("PRAGMA user_version"));
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('event_log','device','schema_meta','project_path')"));
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name IN ('uid','kind')"));
            Assert.Equal(0L, c.ScalarLong("PRAGMA application_id"));
            Assert.Equal(4L, Say(c, "projects"));
        }
        var yedek1 = Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v10-*.db"));

        // yeniden deneme: hata yok → göç tamamlanır, ikinci yedek (aynı dakikada, rastgele ekli ayrı ad) alınır
        var ikinci = SozDb.OpenAndMigrate(dir);
        Assert.Equal(SozDb.CurrentVersion, ikinci.GetVersion());
        var yedekler = Directory.GetFiles(Path.Combine(dir, "yedek"), "once-v10-*.db");
        Assert.Equal(2, yedekler.Length);
        Assert.Contains(yedek1, yedekler);
    }

    [Fact]
    public void Son_uc_once_yedegi_tutulur()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "g4");
        var yedekDir = Path.Combine(dir, "yedek");
        Directory.CreateDirectory(yedekDir);
        for (var i = 1; i <= 4; i++)
        {
            var f = Path.Combine(yedekDir, $"once-v{i}-20260101000{i}.db");
            File.WriteAllText(f, "eski");
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }
        SozDb.OpenAndMigrate(dir);
        var kalan = Directory.GetFiles(yedekDir, "once-*.db").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(3, kalan.Length);
        Assert.Contains(kalan, k => k!.StartsWith("once-v10-")); // yeni yedek her zaman kalır
        Assert.DoesNotContain("once-v1-202601010001.db", kalan); // en eskiler gitti
        Assert.DoesNotContain("once-v2-202601010002.db", kalan);
    }

    // ---------- A2: v11 şeması ve doldurma ----------

    [Fact]
    public void V10_ornegi_v11e_cikar_sekiz_tabloda_uid_dolu_tekil_ve_ikinci_acilista_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "u1");
        var db = SozDb.OpenAndMigrate(dir);
        string Anlik(SqliteConnection c)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in UidTablolari) sb.Append(t).Append(':').Append(string.Join(",", c.Query($"SELECT uid FROM {t} ORDER BY id", r => r.GetString(0)))).Append('\n');
            sb.Append("events=").Append(Say(c, "event_log")).Append(" meta=").Append(Say(c, "schema_meta")).Append(" path=").Append(Say(c, "project_path")).Append(" kinds=").Append(Say(c, "event_kind"));
            sb.Append(" dev=").Append(c.Scalar("SELECT device_uid FROM device"));
            return sb.ToString();
        }
        string a;
        using (var c = db.Open())
        {
            var beklenen = new Dictionary<string, long> { ["projects"] = 4, ["jobs"] = 4, ["runs"] = 5, ["proof_runs"] = 5, ["checks"] = 4, ["guard_rules"] = 3, ["decisions"] = 4, ["run_approvals"] = 2 };
            foreach (var t in UidTablolari)
            {
                Assert.Equal(beklenen[t], Say(c, t)); // satır kaybı yok
                Assert.Equal(0L, c.ScalarLong($"SELECT COUNT(*) FROM {t} WHERE uid IS NULL"));
                var uids = c.Query($"SELECT uid FROM {t}", r => r.GetString(0));
                Assert.Equal(uids.Count, uids.Distinct().Count());
                Assert.All(uids, u => Assert.Matches(UuidV7, u));
                Assert.Equal(1L, c.ScalarLong($"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_{t}_uid'"));
            }
            a = Anlik(c);
        }
        // ikinci açılış: hiçbir şey değişmez (göç yeniden çalışmaz)
        var db2 = SozDb.OpenAndMigrate(dir);
        using (var c = db2.Open()) Assert.Equal(a, Anlik(c));
    }

    [Fact]
    public void Tekrar_calistirilabilir_gocu_zorlayinca_uid_ve_olaylar_cogalmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "u2");
        var db = SozDb.OpenAndMigrate(dir);
        string uidler, eskiler;
        long legacy, metaSayisi;
        using (var c = db.Open())
        {
            metaSayisi = Say(c, "schema_meta");
            uidler = string.Join(",", UidTablolari.SelectMany(t => c.Query($"SELECT uid FROM {t} ORDER BY id", r => r.GetString(0))));
            legacy = c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'legacy.%'");
            eskiler = string.Join(",", c.Query("SELECT id FROM event_log WHERE kind LIKE 'legacy.%' ORDER BY seq", r => r.GetString(0)));
            c.Exec("PRAGMA user_version=10;"); // göç adımını zorla yeniden çalıştır
        }
        Assert.Equal(12L, legacy); // 14 eski olaydan 2 nabız (event_log dışı)
        SozDb.OpenAndMigrate(dir);
        using var c2 = db.Open();
        Assert.Equal((long)SozDb.CurrentVersion, c2.ScalarLong("PRAGMA user_version"));
        Assert.Equal(uidler, string.Join(",", UidTablolari.SelectMany(t => c2.Query($"SELECT uid FROM {t} ORDER BY id", r => r.GetString(0)))));
        Assert.Equal(legacy, c2.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'legacy.%'"));
        Assert.Equal(eskiler, string.Join(",", c2.Query("SELECT id FROM event_log WHERE kind LIKE 'legacy.%' ORDER BY seq", r => r.GetString(0))));
        Assert.Equal(metaSayisi, Say(c2, "schema_meta")); // OR IGNORE: satır çoğalmaz
        Assert.Equal(1L, Say(c2, "device"));
        Assert.Null(Events.VerifyChain(c2));
    }

    [Fact]
    public void Eski_satirlarin_uid_zamani_kayit_zamanindan_gelir_zaman_yoksa_simdiki_an()
    {
        using var env = new TestEnv(copySamples: false);
        var db = SozDb.OpenAndMigrate(OrnekKopya(env, "u3"));
        using var c = db.Open();
        var is1 = (string)c.Scalar("SELECT uid FROM jobs WHERE id=1")!; // created_at 2026-10-02T08:00:00Z
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T08:00:00Z").ToUnixTimeMilliseconds(), UidMs(is1));
        var is4 = (string)c.Scalar("SELECT uid FROM jobs WHERE id=4")!;  // created_at NULL
        Assert.Equal(new DateTimeOffset(Time.UtcNow()).ToUnixTimeMilliseconds(), UidMs(is4)); // kodun zaman kaynağı (Time.UtcNow); testte sabit
        var run5 = (string)c.Scalar("SELECT uid FROM runs WHERE id=5")!; // started NULL
        Assert.Matches(UuidV7, run5);
    }

    [Fact]
    public void Proje_yolu_dolar_event_log_ekle_yalnizdir_ve_yeni_tablolar_kurulur()
    {
        using var env = new TestEnv(copySamples: false);
        var db = SozDb.OpenAndMigrate(OrnekKopya(env, "u4"));
        using var c = db.Open();
        // project_path: yolu olan 2 proje, is_current=1; Genel ve yolu olmayan proje yok
        var yollar = c.Query("SELECT p.name, pp.path, pp.is_current FROM project_path pp JOIN projects p ON p.uid=pp.project_uid ORDER BY pp.path", r => r.Str(0) + "|" + r.GetString(1) + "|" + r.GetInt64(2));
        Assert.Equal(new[] { @"defter|D:\ornek\defter|1", @"site|D:\ornek\site|1" }, yollar.ToArray());

        // ekle-yalnız
        var seq = c.ScalarLong("SELECT MIN(seq) FROM event_log");
        var u = Assert.Throws<SqliteException>(() => c.Exec("UPDATE event_log SET payload='{}' WHERE seq=@s", null, ("@s", seq)));
        Assert.Contains("ekle-yalnizdir", u.Message);
        var d = Assert.Throws<SqliteException>(() => c.Exec("DELETE FROM event_log WHERE seq=@s", null, ("@s", seq)));
        Assert.Contains("ekle-yalnizdir", d.Message);
        Assert.Throws<SqliteException>(() => c.Exec("INSERT INTO event_log(id,device_uid,ts,kind,payload) VALUES('x','d','t','k','json degil')")); // json_valid

        // yönetici eklemeleri
        c.Exec("INSERT INTO settings_value(key,value,ts) VALUES('a','1','t')");
        Assert.Equal("global", c.Scalar("SELECT scope FROM settings_value WHERE key='a'"));
        Assert.Throws<SqliteException>(() => c.Exec("INSERT INTO settings_value(key,value,ts) VALUES('a','2','t')")); // birincil anahtar (key,scope,scope_id)
        c.Exec("INSERT INTO registry_state(id) VALUES('mod.a')");
        Assert.Equal(1L, c.ScalarLong("SELECT enabled FROM registry_state WHERE id='mod.a'"));
        c.Exec("INSERT INTO registry_suggestion(id,kind,body,created_at) VALUES('s1','k','{\"a\":1}','t')");
        Assert.Throws<SqliteException>(() => c.Exec("INSERT INTO registry_suggestion(id,kind,body,created_at) VALUES('s2','k','bozuk','t')"));
        Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name='sync_cursor'")); // kullanıcısı yok, eklenmedi
        // eski ayar tablosuna dokunulmadı: v10 örneğindeki üç anahtar aynen duruyor; göçün eklediği her şey "_" önekli iç anahtardır (ör. _eventChainV2From, Events.cs yazar), kullanıcı ayarı değildir.
        var anahtarlar = c.Query("SELECT key FROM settings ORDER BY key", r => r.GetString(0)).ToHashSet();
        Assert.All(new[] { "_lastImportAt", "gateBudgetSeconds", "language" }, k => Assert.Contains(k, anahtarlar));
        Assert.All(anahtarlar.Where(k => k is not ("gateBudgetSeconds" or "language")), k => Assert.StartsWith("_", k));
    }

    [Fact]
    public void Goc_ve_yedek_olaylari_event_kind_tohumu_ve_gecmis_olay_kopyasi()
    {
        using var env = new TestEnv(copySamples: false);
        var db = SozDb.OpenAndMigrate(OrnekKopya(env, "u5"));
        using var c = db.Open();
        // v11 tohumu iki tür; sonraki şema adımları kendi olay türlerini (EventKindCatalog) ekler. Eksiksiz tohum: tohum ⊆ tablo ⊇ katalog.
        var turler = c.Query("SELECT kind FROM event_kind ORDER BY kind", r => r.GetString(0)).ToHashSet();
        Assert.Contains("backup.taken", turler);
        Assert.Contains("migration.applied", turler);
        foreach (var k in EventKindCatalog.All) Assert.Contains(k.Name, turler);
        var son2 = c.Query("SELECT kind, payload FROM event_log WHERE kind NOT LIKE 'legacy.%' ORDER BY seq", r => (r.GetString(0), JsonNode.Parse(r.GetString(1))!));
        Assert.Equal(new[] { "backup.taken", "migration.applied" }, son2.Select(x => x.Item1).ToArray());
        Assert.Equal("ok", (string?)son2[0].Item2["quick_check"]);
        Assert.Equal(10, (int)son2[1].Item2["from"]!);
        Assert.Equal(11, (int)son2[1].Item2["to"]!);
        Assert.Equal(12, (int)son2[1].Item2["legacy_events"]!); // 14 eski olaydan 12'si kopyalanır
        Assert.Equal(2, (int)son2[1].Item2["noise_events"]!); // job_progress ve hook_pulse nabızdır, event_log'a girmez
        var ms = (long)son2[1].Item2["ms"]!;
        _out.WriteLine($"GOC_MS={ms} (v10 sentetik örnek: 4 proje, 14 olay; yedek dahil)");
        Assert.InRange(ms, 0, 5000);
        // geçmiş events satırları legacy.<tür> olarak, yük {"detail": ...}
        Assert.Equal(12L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'legacy.%'"));
        Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind IN ('legacy.hook_pulse','legacy.job_progress')"));
        Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE log_id=''")); // nabız: bilerek kopyalanmadı
        Assert.Equal(12L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE log_id IN (SELECT id FROM event_log)")); // kopyalananlar karşılığına bağlı
        Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE log_id IS NULL"));
        var accepted = JsonNode.Parse((string)c.Scalar("SELECT payload FROM event_log WHERE kind='legacy.job_accepted'")!)!;
        Assert.Contains("\"jobId\":1", (string)accepted["detail"]!);
        Assert.Null(JsonNode.Parse((string)c.Scalar("SELECT payload FROM event_log WHERE kind='legacy.phone_code_renewed'")!)!["detail"]);
        // olayın proje uid'si eski proje kimliğinden bulunur
        var pu = (string)c.Scalar("SELECT project_uid FROM event_log WHERE kind='legacy.approve'")!;
        Assert.Equal(pu, (string)c.Scalar("SELECT uid FROM projects WHERE id=3")!);
        // eski events tablosu aynen duruyor
        Assert.Equal(14L, Say(c, "events"));
        Assert.Null(Events.VerifyChain(c));
    }

    [Fact]
    public void Yeni_satirlar_uid_tasir_is_turu_varsayilan_kod_ve_eski_isler_kod()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var j = w.CreateJob(pid, "Bir şey yap.");
        Assert.Equal("kod", j.Kind);
        Assert.Matches(UuidV7, j.Uid!);
        var y = w.CreateJob(pid, "Rapor yaz.", kind: " Yazi ");
        Assert.Equal("yazi", y.Kind);
        Assert.Equal("yazi", w.Job(y.Id).Kind);
        // API çıktısında alan görünür
        var json = JsonNode.Parse(JsonSerializer.Serialize(w.Job(y.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal("yazi", (string?)json["kind"]);
        Assert.Matches(UuidV7, (string)json["uid"]!);
        Assert.Equal(y.Id, (long)json["id"]!); // sayı kimlik aynen

        using var c = env.Svc.Db.Open();
        var chk = w.AddCheck(pid, null, "command", "Test", new JsonObject { ["cmd"] = "npm test" }, 120, enabled: true);
        Assert.Matches(UuidV7, chk.Uid!);
        var kural = GuardStore.Create(c, pid, "command_block", "Etiket", new JsonObject { ["patterns"] = new JsonArray("rm -rf") }, null);
        Assert.Matches(UuidV7, kural.Uid!);
        var karar = env.Svc.CreateDecision(pid, "rule", "Bir kural.");
        Assert.Matches(UuidV7, karar.Uid!);
        var runId = RunStore.Insert(c, j.Id, pid, "start");
        Assert.Matches(UuidV7, RunStore.Get(c, runId)!.Uid!);
        var proofId = ProofStore.Begin(c, new ProofStore.RunRequest(pid, j.Id, null, "manual", 1));
        Assert.Matches(UuidV7, ProofStore.Get(c, proofId)!.Uid!);
        RunStore.AddApproval(c, runId, j.Id, 1, "Bash", "özet", "h");
        Assert.Matches(UuidV7, RunStore.FindApproval(c, j.Id, "h", "pending")!.Uid!);
        Assert.Matches(UuidV7, env.Svc.Projects().First(p => p.Id == pid).Uid!);

        foreach (var t in UidTablolari)
        {
            Assert.Equal(0L, c.ScalarLong($"SELECT COUNT(*) FROM {t} WHERE uid IS NULL"));
            var uids = c.Query($"SELECT uid FROM {t}", r => r.GetString(0));
            Assert.Equal(uids.Count, uids.Distinct().Count());
        }
        // proje yolu geçmişi: yeni proje yolu güncel, yeniden görülünce last_seen yenilenir, yol değişince eskisi güncel olmaktan çıkar
        var puid = (string)c.Scalar("SELECT uid FROM projects WHERE id=@i", null, ("@i", pid))!;
        Assert.Single(ProjectPaths.For(c, puid), p => p.IsCurrent);
        ProjectPaths.Seen(c, null, puid, @"X:\tasindi\burasi");
        var yollar = ProjectPaths.For(c, puid);
        Assert.Equal(2, yollar.Count);
        Assert.Equal(@"X:\tasindi\burasi", Assert.Single(yollar, p => p.IsCurrent).Path);
        ProjectPaths.Seen(c, null, puid, Path.GetFullPath(dir)); // eski yol geri geldi
        Assert.Equal(Path.GetFullPath(dir), Assert.Single(ProjectPaths.For(c, puid), p => p.IsCurrent).Path);

        // sürüm 10 örneğindeki eski işler "kod" ve uid'li (API'den)
        var w2 = new WorkService(SozDb.OpenAndMigrate(OrnekKopya(env, "u6")));
        var eski = w2.Jobs(limit: 50);
        Assert.Equal(4, eski.Count);
        Assert.All(eski, e => { Assert.Equal("kod", e.Kind); Assert.Matches(UuidV7, e.Uid!); });
        Assert.Equal("codex", eski.First(e => e.Id == 3).Agent); // mevcut alanlar aynen
    }

    // ---------- A3: olay kaydı ----------

    [Fact]
    public void Cift_yazim_eski_events_tablosuna_ve_event_log_a_yazar()
    {
        using var env = new TestEnv(copySamples: false);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(NodeSample.Create(env));
        using var c = env.Svc.Db.Open();
        var puid = (string)c.Scalar("SELECT uid FROM projects WHERE id=@i", null, ("@i", pid))!;
        Events.Log(c, "add", pid, "12");
        Events.Log(c, "import", null, "{\"a\":1}");
        var eski = c.Query("SELECT id, kind, detail, project_id FROM events WHERE kind IN ('add','import') ORDER BY id", r => (r.GetInt64(0), r.GetString(1), r.Str(2), r.LongN(3)));
        Assert.Equal(2, eski.Count);
        Assert.Equal("add", eski[0].Item2);
        Assert.Equal("12", eski[0].Item3);
        Assert.Equal(pid, eski[0].Item4);
        var yeni = c.Query("SELECT id, kind, payload, project_uid, device_uid FROM event_log WHERE kind IN ('legacy.add','legacy.import') ORDER BY seq", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.Str(3), r.GetString(4)));
        Assert.Equal(2, yeni.Count);
        Assert.Equal("12", (string?)JsonNode.Parse(yeni[0].Item3)!["detail"]);
        Assert.Equal(puid, yeni[0].Item4);
        Assert.Null(yeni[1].Item4);
        Assert.Equal(yeni[0].Item1, (string)c.Scalar("SELECT log_id FROM events WHERE id=@i", null, ("@i", eski[0].Item1))!); // eski satır karşılığına log_id ile bağlı
        Assert.Matches(UuidV7, yeni[0].Item1); // event_log kimliği events.id'ye bağlı değil, kendi uid'si
        Assert.Null(Events.VerifyChain(c));
    }

    [Fact]
    public void Append_hash_zincirini_surdurur_ve_dogrulama_temiz_veride_gecer()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var a = Events.Append(c, "job.created", "job", "ju1", "pu1", "ju1", "soz", "{\"n\":1}");
        var b = Events.Append(c, "run.started", "run", "ru1", "pu1", "ju1", "claude", null);
        Events.Log(c, "add", null, "x");
        var d = Events.Append(c, "run.ended", null, null, null, null, "soz", "{}");
        Assert.Equal(a.Hash, b.PrevHash);
        Assert.Equal("{}", b.Payload);
        Assert.Equal(Events.ComputeHash(b), b.Hash);
        Assert.True(d.Seq > b.Seq);
        Assert.Matches(UuidV7, a.Id);
        Assert.Null(Events.VerifyChain(c));
        Assert.Throws<ArgumentException>(() => Events.Append(c, "", null, null, null, null, "soz", "{}"));
        Assert.Throws<SqliteException>(() => Events.Append(c, "x", null, null, null, null, "soz", "json degil"));
        Assert.Null(Events.VerifyChain(c)); // başarısız yazım zinciri bozmaz (işlem geri alındı)
    }

    [Fact]
    public void Elle_bozulmus_kopyada_kirik_ilk_seq_bulunur()
    {
        using var env = new TestEnv(copySamples: false);
        long s2, s4;
        using (var c = env.Svc.Db.Open())
        {
            Events.Append(c, "k.bir", null, null, null, null, "soz", "{\"n\":1}");
            s2 = Events.Append(c, "k.iki", null, null, null, null, "soz", "{\"n\":2}").Seq;
            Events.Append(c, "k.uc", null, null, null, null, "soz", "{\"n\":3}");
            s4 = Events.Append(c, "k.dort", null, null, null, null, "soz", "{\"n\":4}").Seq;
            Events.Append(c, "k.bes", null, null, null, null, "soz", "{\"n\":5}");
            Assert.Null(Events.VerifyChain(c));
        }

        string Kopya(string ad)
        {
            var yol = Path.Combine(env.Root, ad + ".db");
            using var c = env.Svc.Db.Open();
            c.Exec("VACUUM INTO @f", null, ("@f", yol));
            return yol;
        }

        // yük değiştirildi
        var k1 = Kopya("k1");
        using (var c = Ham(k1))
        {
            c.Exec("DROP TRIGGER trg_event_no_update");
            c.Exec("UPDATE event_log SET payload='{\"n\":999}' WHERE seq=@s", null, ("@s", s2));
            Assert.Equal(s2, Events.VerifyChain(c));
        }
        // satır silindi: kırık ilk satır silinenden sonraki
        var k2 = Kopya("k2");
        using (var c = Ham(k2))
        {
            c.Exec("DROP TRIGGER trg_event_no_delete");
            c.Exec("DELETE FROM event_log WHERE seq=@s", null, ("@s", s4));
            var kirik = Events.VerifyChain(c);
            Assert.NotNull(kirik);
            Assert.Equal(c.ScalarLong("SELECT MIN(seq) FROM event_log WHERE seq>@s", null, ("@s", s4)), kirik);
        }
        // hash'in kendisi değiştirildi
        var k3 = Kopya("k3");
        using (var c = Ham(k3))
        {
            c.Exec("DROP TRIGGER trg_event_no_update");
            c.Exec("UPDATE event_log SET hash='00' WHERE seq=@s", null, ("@s", s4));
            Assert.Equal(s4, Events.VerifyChain(c));
        }
    }

    [Fact]
    public async Task Iki_eszamanli_yazar_zinciri_bozmaz()
    {
        using var env = new TestEnv(copySamples: false);
        const int Yazar = 4, Adet = 40;
        long once;
        using (var c0 = env.Svc.Db.Open()) once = Say(c0, "event_log");
        var gorevler = Enumerable.Range(0, Yazar).Select(y => Task.Run(() =>
        {
            using var c = env.Svc.Db.Open();
            for (var i = 0; i < Adet; i++)
            {
                if (i % 2 == 0) Events.Append(c, "es.zamanli", null, null, null, null, "yazar" + y, $"{{\"y\":{y},\"i\":{i}}}");
                else Events.Log(c, "es_eski", null, $"{y}-{i}");
            }
        })).ToArray();
        await Task.WhenAll(gorevler);
        using var v = env.Svc.Db.Open();
        Assert.Equal(once + Yazar * Adet, Say(v, "event_log"));
        Assert.Null(Events.VerifyChain(v));
        Assert.Equal((long)(Yazar * Adet / 2), v.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='es_eski'")); // eski tabloya da yazıldı
        // seq aralıksız
        Assert.Equal(Say(v, "event_log"), v.ScalarLong("SELECT MAX(seq)-MIN(seq)+1 FROM event_log"));
    }

    [Fact]
    public void ReadSince_kacirilan_olaylari_geri_okur()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var son = Events.LastSeq(c);
        var e1 = Events.Append(c, "r.bir", null, null, null, null, "soz", "{}");
        Events.Log(c, "job", null, "{\"id\":1}");
        var e3 = Events.Append(c, "r.uc", null, null, null, null, "soz", "{}");
        var hepsi = Events.ReadSince(c, son);
        Assert.Equal(new[] { "r.bir", "legacy.job", "r.uc" }, hepsi.Select(x => x.Kind).ToArray());
        Assert.Equal(new[] { e1.Seq, e1.Seq + 1, e3.Seq }, hepsi.Select(x => x.Seq).ToArray());
        Assert.Equal(new[] { "legacy.job", "r.uc" }, Events.ReadSince(c, e1.Seq).Select(x => x.Kind).ToArray()); // "şu sıradan sonrası"
        Assert.Single(Events.ReadSince(c, son, limit: 1));
        Assert.Equal(new[] { "legacy.job" }, Events.ReadSince(c, son, kinds: new[] { "legacy.job" }).Select(x => x.Kind).ToArray());
        Assert.Empty(Events.ReadSince(c, e3.Seq));
        Assert.Equal(e3.Seq, Events.LastSeq(c));
    }

    [Fact]
    public async Task Olay_akisi_since_ile_kacirilan_canli_olaylari_geri_verir()
    {
        using var env = new TestEnv(copySamples: false);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(NodeSample.Create(env));
        long son;
        List<long> seqler;
        var port = 50100 + Random.Shared.Next(0, 800);
        await using var h = await SozServer.StartAsync(env.DataDir, port);
        using (var c = env.Svc.Db.Open())
        {
            son = Events.LastSeq(c);
            // istemci yokken üç canlı olay (job, job_state, proof) ve akışa girmeyen bir olay yazılır
            Live.Emit(c, Live.Job, pid, new { id = 1, state = "taslak" });
            Events.Log(c, "hook_pulse", pid, "{}");
            Live.Emit(c, Live.JobState, pid, new { jobId = 1, status = "running" });
            Live.Emit(c, Live.Proof, pid, new { runId = 1, phase = "started" });
            seqler = Events.ReadSince(c, son, kinds: LiveFeedKinds).Select(x => x.Seq).ToList();
        }
        Assert.Equal(3, seqler.Count);

        async Task<List<(long? Id, string Name)>> Oku(string url, Action<HttpRequestMessage>? ayar, int beklenen)
        {
            using var http = new HttpClient { BaseAddress = new Uri(h.Url), Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ayar?.Invoke(req);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            using var rd = new StreamReader(await resp.Content.ReadAsStreamAsync());
            var bulunan = new List<(long?, string)>();
            long? id = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (bulunan.Count < beklenen && !cts.IsCancellationRequested)
            {
                var line = await rd.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (line.StartsWith("id: ")) id = long.Parse(line[4..]);
                else if (line.StartsWith("event: ")) { bulunan.Add((id, line[7..])); id = null; }
            }
            return bulunan;
        }

        // A: since ile bağlan. Üç olay geri verilir; sunucunun yoklaması (300 ms) aynı üçünü canlı da yayınlar ama tekrar gelmez (seq ile elenir),
        // sonra yeni bir olay yazılınca bir sonraki olay o olur.
        var okuA = Oku($"/api/events/stream?since={son}", null, 4);
        await Task.Delay(1200);
        long yeniSeq;
        using (var c = env.Svc.Db.Open()) { Live.Emit(c, Live.Queue, pid, new { id = 7 }); yeniSeq = Events.LastSeq(c); }
        var a = await okuA;
        Assert.Equal(new[] { "job", "job.state", "proof", "queue" }, a.Select(x => x.Name).ToArray()); // yinelenen yok
        Assert.Equal(seqler.Append(yeniSeq).ToList(), a.Select(x => x.Id!.Value).ToList()); // id = event_log seq

        // B: Last-Event-ID başlığı aynı işi yapar; ilk olaydan sonrası gelir (hook_pulse akışa girmez)
        var b = await Oku("/api/events/stream", r => r.Headers.Add("Last-Event-ID", seqler[0].ToString()), 3);
        Assert.Equal(new[] { "job.state", "proof", "queue" }, b.Select(x => x.Name).ToArray());

        // C: since yok → geri verme yok; yalnız bağlandıktan sonra gelen olay okunur
        var okuC = Oku("/api/events/stream", null, 1);
        await Task.Delay(700);
        using (var c = env.Svc.Db.Open()) Live.Emit(c, Live.Queue, pid, new { id = 8 });
        var cc = await okuC;
        Assert.Equal("queue", Assert.Single(cc).Name);
        Assert.True(cc[0].Id > yeniSeq);
    }

    private static readonly string[] LiveFeedKinds = Live.Kinds.Select(k => Events.LegacyPrefix + k).ToArray();
}
