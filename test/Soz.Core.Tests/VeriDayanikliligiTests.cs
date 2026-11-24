using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Dalga 1-D (veri zemini dayanıklılığı): çok süreçli ilk açılış, onarım, olay kimliği, hash zinciri kapsamı, nabız olayları, canlı pompa, hata iletileri, iş türü.
/// Çok süreçli testler gerçek süreç açar: test\Soz.Core.Tests.Yardimci (Soz.Core.Tests.Yardimci.dll) dotnet ile başlatılır.
/// </summary>
public class VeriDayanikliligiTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public VeriDayanikliligiTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static readonly Regex UuidV7 = new("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.Compiled);

    // ---------- yardımcılar ----------

    private static SqliteConnection Ham(string dosya)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dosya, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static string Sha(string dosya) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dosya)));
    private static long Say(SqliteConnection c, string tablo) => c.ScalarLong($"SELECT COUNT(*) FROM {tablo}");
    private static long UidMs(string uid) => Convert.ToInt64(uid[..8] + uid.Substring(9, 4), 16);

    private static string OrnekKopya(TestEnv env, string ad)
    {
        var dir = Path.Combine(env.Root, ad);
        Directory.CreateDirectory(dir);
        File.Copy(V10Ornegi.DosyaYolu, Path.Combine(dir, "soz.db"));
        return dir;
    }

    /// <summary>Sentetik v10 veri klasörü (gerçek kişisel veri yok): <paramref name="olay"/> events satırı (beşte biri nabız), <paramref name="is"/> iş. Tek parça soz.db bırakır.</summary>
    private static void Uret10(string dir, int olay, int isSayisi)
    {
        var db = new SozDb(dir);
        db.MigrateTo(10);
        using (var c = db.Open())
        {
            using var tx = c.BeginTransaction();
            c.Exec("INSERT OR IGNORE INTO projects(key,name,path,created_at) VALUES('p1','P1','C:\\x\\p1','2026-01-01T00:00:00.000Z')", tx);
            using (var cmd = c.Cmd("INSERT INTO events(ts,kind,project_id,detail) VALUES(@t,@k,1,@d)", tx, ("@t", ""), ("@k", ""), ("@d", "")))
                for (var i = 0; i < olay; i++)
                {
                    cmd.Parameters["@t"].Value = $"2026-02-{1 + i % 27:00}T{i % 24:00}:{i % 60:00}:{i % 60:00}.000Z";
                    cmd.Parameters["@k"].Value = i % 5 == 0 ? "job" : i % 5 == 1 ? "hook_pulse" : "proof";
                    cmd.Parameters["@d"].Value = "{\"jobId\":" + i + ",\"x\":\"" + new string('a', 60) + "\"}";
                    cmd.ExecuteNonQuery();
                }
            using (var cmd = c.Cmd("INSERT INTO jobs(project_id,title,raw_text,prompt_text,prompt_hash,source,state,night,restate,created_at,agent) VALUES(1,@t,@r,'','','yazi','taslak',0,0,@c,'claude')", tx, ("@t", ""), ("@r", ""), ("@c", "")))
                for (var i = 0; i < isSayisi; i++)
                {
                    cmd.Parameters["@t"].Value = "is " + i;
                    cmd.Parameters["@r"].Value = "ham metin " + i;
                    cmd.Parameters["@c"].Value = $"2026-03-{1 + i % 27:00}T10:00:00.000Z";
                    cmd.ExecuteNonQuery();
                }
            tx.Commit();
            c.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            c.Exec("PRAGMA journal_mode=DELETE;"); // tek parça dosya; göç WAL'a kendisi geçer
        }
        SqliteConnection.ClearAllPools();
    }

    private static string DotnetYolu()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var kok = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var exe = Path.Combine(kok, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(exe) ? exe : "dotnet";
    }

    private sealed record SurecSonucu(int Cikis, string Cikti);

    /// <summary>
    /// Her komut için ayrı bir yardımcı süreç başlatır, hepsi "HAZIR" deyince git dosyasını yaratır (bütün süreçler aynı anda koşar), bitmelerini bekler.
    /// Dönen çıktı "HAZIR" satırından sonrasıdır: başarıda tam olarak "TAMAM".
    /// </summary>
    private static List<SurecSonucu> CokSurecKos(string gitDosyasi, IReadOnlyList<string[]> komutlar)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Soz.Core.Tests.Yardimci.dll");
        Assert.True(File.Exists(dll), "Yardımcı süreç derlenmemiş: " + dll);
        var surecler = new List<Process>();
        try
        {
            foreach (var k in komutlar)
            {
                var psi = new ProcessStartInfo(DotnetYolu()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add(dll);
                foreach (var a in k) psi.ArgumentList.Add(a);
                surecler.Add(Process.Start(psi)!);
            }
            foreach (var p in surecler)
            {
                var satir = p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
                Assert.Equal("HAZIR", satir);
            }
            File.WriteAllText(gitDosyasi, "git");
            var sonuc = new List<SurecSonucu>();
            foreach (var p in surecler)
            {
                if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } Assert.Fail("Yardımcı süreç 180 sn içinde bitmedi."); }
                sonuc.Add(new SurecSonucu(p.ExitCode, (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim()));
            }
            return sonuc;
        }
        finally { foreach (var p in surecler) p.Dispose(); }
    }

    // ---------- E1: eşzamanlı ilk açılış (gerçek çok süreçli) ----------

    [Fact]
    public void Es_zamanli_ilk_acilis_6_surec_x_4_tur_ve_12_surec_hicbiri_hata_vermez_goc_ve_yedek_tek()
    {
        using var env = new TestEnv(copySamples: false);
        var ham = Path.Combine(env.Root, "ham");
        Uret10(ham, 30000, 5000);
        var turlar = new[] { 6, 6, 6, 6, 12 };
        for (var t = 0; t < turlar.Length; t++)
        {
            var dir = Path.Combine(env.Root, $"tur{t}");
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(ham, "soz.db"), Path.Combine(dir, "soz.db"));
            var git = Path.Combine(env.Root, $"git{t}");
            var sw = Stopwatch.StartNew();
            var sonuclar = CokSurecKos(git, Enumerable.Range(0, turlar[t]).Select(_ => new[] { "goc", dir, git }).ToList());
            _out.WriteLine($"tur {t}: {turlar[t]} süreç, {sw.ElapsedMilliseconds} ms");
            foreach (var s in sonuclar) Assert.True(s.Cikis == 0 && s.Cikti == "TAMAM", $"tur {t}: çıkış {s.Cikis}: {s.Cikti}");

            var db = new SozDb(dir);
            using var c = db.Open();
            Assert.Equal((long)SozDb.CurrentVersion, c.ScalarLong("PRAGMA user_version"));
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='migration.applied'"));
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='backup.taken'"));
            var yedek = Assert.Single(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-*.db"));
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='backup.taken' AND json_extract(payload,'$.file')=@f", null, ("@f", Path.GetFileName(yedek)))); // olay var olan dosyayı gösterir
            using (var y = Ham(yedek))
            {
                Assert.Equal("ok", y.Scalar("PRAGMA quick_check"));
                Assert.Equal(30000L, Say(y, "events"));
            }
            Assert.Null(Events.VerifyChain(c));
            Assert.Equal(24000L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'legacy.%'")); // nabızlar (6000) event_log dışı
            Assert.Equal(6000L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE log_id=''"));
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE uid IS NULL"));
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Cok_surecli_yazarlar_zinciri_bozmaz_seq_araliksiz_ve_iki_tabloya_yazar()
    {
        using var env = new TestEnv(copySamples: false);
        const int Surec = 6, Adet = 50;
        long once;
        using (var c0 = env.Svc.Db.Open()) once = Say(c0, "event_log");
        var git = Path.Combine(env.Root, "git-yaz");
        var sonuclar = CokSurecKos(git, Enumerable.Range(0, Surec).Select(i => new[] { "yaz", env.DataDir, Adet.ToString(), "s" + i, git }).ToList());
        foreach (var s in sonuclar) Assert.True(s.Cikis == 0 && s.Cikti == "TAMAM", $"çıkış {s.Cikis}: {s.Cikti}");
        using var v = env.Svc.Db.Open();
        Assert.Equal(once + Surec * Adet, Say(v, "event_log"));
        Assert.Equal((long)(Surec * Adet / 2), v.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='yaris.append'"));
        Assert.Equal((long)(Surec * Adet / 2), v.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='legacy.yaris_log'"));
        Assert.Equal((long)(Surec * Adet / 2), v.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='yaris_log'"));
        Assert.Null(Events.VerifyChain(v));
        Assert.Equal(Say(v, "event_log"), v.ScalarLong("SELECT MAX(seq)-MIN(seq)+1 FROM event_log"));
    }

    [Fact]
    public void Goc_kilidi_baskasinda_ise_sade_Turkce_zaman_asimi_ve_veri_dokunulmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "kt");
        var dbDosya = Path.Combine(dir, "soz.db");
        var once = Sha(dbDosya);
        using var tutan = new FileStream(Path.Combine(dir, "goc.kilit"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var db = new SozDb(dir) { MigrationLockTimeout = TimeSpan.FromMilliseconds(400) };
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<SozDataAccessException>(() => db.Migrate());
        Assert.Equal("goc_kilidi", ex.Reason);
        Assert.Contains("başka bir Söz süreci", ex.Message);
        Assert.DoesNotContain("SQLite", ex.Message);
        Assert.IsAssignableFrom<IOException>(ex.InnerException); // ham ayrıntı iç istisnada
        Assert.InRange(sw.ElapsedMilliseconds, 350, 10000);
        SqliteConnection.ClearAllPools();
        Assert.Equal(once, Sha(dbDosya));
        tutan.Dispose(); // kilit bırakılınca göç çalışır
        db.Migrate();
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion());
    }

    [Fact]
    public void Guncel_veride_goc_kilidi_alinmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = env.DataDir; // env.Svc zaten v11'e göç ettirdi
        using var tutan = new FileStream(Path.Combine(dir, "goc.kilit"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var sw = Stopwatch.StartNew();
        var db = SozDb.OpenAndMigrate(dir); // kilit başkasında ama gerek yok: sık yol kilitsiz döner
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion());
    }

    [Fact]
    public void Bozuk_yedekte_yalniz_kendi_dosyasi_kaldirilir_baskasinin_yedegi_durur()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "by");
        var yedekDir = Path.Combine(dir, "yedek");
        Directory.CreateDirectory(yedekDir);
        var baska = Path.Combine(yedekDir, "once-v10-202611101200-deadbeef.db"); // aynı dakika, başka sürecin yedeği
        File.WriteAllText(baska, "baskasinin");
        var db = new SozDb(dir) { AfterBackup = yol => File.WriteAllBytes(yol, new byte[8192]) };
        Assert.Throws<SozBackupFailedException>(() => db.Migrate());
        Assert.Equal("baskasinin", File.ReadAllText(baska));
        Assert.Equal(new[] { baska }, Directory.GetFiles(yedekDir, "once-*.db")); // bozuk yedeğimiz kalkmış
    }

    [Fact]
    public void Dorduncu_gercek_yedek_uretilince_en_eskisi_gider_son_uc_kalir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "d4");
        var dbDosya = Path.Combine(dir, "soz.db");
        var v10 = File.ReadAllBytes(dbDosya);
        var yedekDir = Path.Combine(dir, "yedek");
        var adlar = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            Thread.Sleep(40); // her yedeğin yazma zamanı farklı olsun
            SozDb.OpenAndMigrate(dir);
            SqliteConnection.ClearAllPools();
            var simdi = Directory.GetFiles(yedekDir, "once-v10-*.db").Select(f => Path.GetFileName(f)!).ToList();
            adlar.Add(Assert.Single(simdi.Except(adlar)));
            if (i < 4)
            {
                File.WriteAllBytes(dbDosya, v10); // v10'a geri dön: yeni göç yeni yedek üretsin
                foreach (var ek in new[] { dbDosya + "-wal", dbDosya + "-shm" }) if (File.Exists(ek)) File.Delete(ek);
            }
        }
        var kalan = Directory.GetFiles(yedekDir, "once-v10-*.db").Select(f => Path.GetFileName(f)!).ToHashSet();
        Assert.Equal(3, kalan.Count);
        Assert.DoesNotContain(adlar[0], kalan); // en eskisi gitti
        Assert.All(adlar.Skip(1), a => Assert.Contains(a, kalan));
    }

    // ---------- hata iletileri (madde 7) ----------

    [Fact]
    public void Salt_okunur_veri_dosyasi_sade_Turkce_hata_verir_yedek_ve_veri_dokunulmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "so");
        var dbDosya = Path.Combine(dir, "soz.db");
        var once = Sha(dbDosya);
        File.SetAttributes(dbDosya, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.Throws<SozDataAccessException>(() => new SozDb(dir).Migrate());
            Assert.Equal("salt_okunur", ex.Reason);
            Assert.Contains("salt okunur", ex.Message);
            Assert.Contains("işaretini kaldır", ex.Message); // kişinin ne yapacağı
            Assert.DoesNotContain("SQLite", ex.Message);
            Assert.False(Directory.Exists(Path.Combine(dir, "yedek")) && Directory.GetFiles(Path.Combine(dir, "yedek")).Length > 0); // yedek de bırakılmadı
        }
        finally { File.SetAttributes(dbDosya, FileAttributes.Normal); }
        SqliteConnection.ClearAllPools();
        Assert.Equal(once, Sha(dbDosya));
    }

    [Fact]
    public void Yedek_klasoru_yerine_ayni_adda_dosya_varsa_sade_Turkce_hata_verir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "yd");
        var dbDosya = Path.Combine(dir, "soz.db");
        File.WriteAllText(Path.Combine(dir, "yedek"), "ben bir dosyayım");
        var once = Sha(dbDosya);
        var ex = Assert.Throws<SozBackupFailedException>(() => new SozDb(dir).Migrate());
        Assert.Contains("'yedek' adında bir dosya var", ex.Message);
        Assert.Contains("adını değiştir", ex.Message);
        Assert.DoesNotContain("Access to the path", ex.Message);
        Assert.DoesNotContain("SQLite", ex.Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(once, Sha(dbDosya));
        Assert.Equal("ben bir dosyayım", File.ReadAllText(Path.Combine(dir, "yedek"))); // dosyaya dokunulmadı
    }

    [Fact]
    public void Kilitli_veritabani_sade_Turkce_hata_verir_ve_veri_dokunulmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "kv");
        var dbDosya = Path.Combine(dir, "soz.db");
        var once = Sha(dbDosya);
        using (var kilit = Ham(dbDosya))
        {
            kilit.Exec("BEGIN EXCLUSIVE");
            var ex = Assert.Throws<SozDataAccessException>(() => new SozDb(dir, busyTimeoutMs: 1000).Migrate());
            Assert.Equal("kilitli", ex.Reason);
            Assert.Contains("başka bir program", ex.Message);
            Assert.Contains("yeniden dene", ex.Message);
            Assert.DoesNotContain("SQLite", ex.Message);
            Assert.IsType<SqliteException>(ex.InnerException); // ham ayrıntı iç istisnada
            kilit.Exec("ROLLBACK");
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(once, Sha(dbDosya));
    }

    [Fact]
    public void Disk_dolu_ve_diger_ham_hatalar_sade_Turkce_iletiye_cevrilir()
    {
        string Msg(Exception e) => SozDataAccessException.From(e, "Veri dosyası açılamadı").Message;
        var dolu = SozDataAccessException.From(new SqliteException("database or disk is full", 13), "Veri dosyası açılamadı");
        Assert.Equal("disk_dolu", dolu.Reason);
        Assert.Contains("Diskte yer", dolu.Message);
        Assert.Contains("yer aç", dolu.Message);
        Assert.DoesNotContain("disk is full", dolu.Message);
        Assert.IsType<SqliteException>(dolu.InnerException);
        Assert.Equal("disk_dolu", SozDataAccessException.From(new IOException("Sürücüde yeterli alan yok", unchecked((int)0x80070070))).Reason);
        Assert.Equal("salt_okunur", SozDataAccessException.From(new SqliteException("attempt to write a readonly database", 8)).Reason);
        Assert.Equal("kilitli", SozDataAccessException.From(new SqliteException("database is locked", 5)).Reason);
        Assert.Equal("izin", SozDataAccessException.From(new UnauthorizedAccessException("Access to the path 'x' is denied.")).Reason);
        Assert.Equal("bozuk", SozDataAccessException.From(new SqliteException("database disk image is malformed", 11)).Reason);
        Assert.Equal("bilinmiyor", SozDataAccessException.From(new InvalidOperationException("raw english text")).Reason);
        foreach (var e in new Exception[] { new SqliteException("readonly database", 8), new UnauthorizedAccessException("Access denied"), new InvalidOperationException("raw english text") })
        {
            var m = Msg(e);
            Assert.DoesNotContain("readonly", m, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Access denied", m);
            Assert.DoesNotContain("raw english", m);
            Assert.StartsWith("Veri dosyası açılamadı. ", m);
        }
    }

    [Fact]
    public void Yedek_alirken_disk_dolu_olursa_sade_Turkce_hata_ve_yarim_yedek_kalmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "dd");
        var db = new SozDb(dir) { AfterBackup = _ => throw new SqliteException("database or disk is full", 13) }; // VACUUM INTO sırasında disk dolması
        var ex = Assert.Throws<SozBackupFailedException>(() => db.Migrate());
        Assert.Contains("Diskte yer kalmamış", ex.Message);
        Assert.Contains("veri olduğu gibi duruyor", ex.Message);
        Assert.DoesNotContain("disk is full", ex.Message);
        Assert.IsType<SqliteException>(ex.InnerException);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "yedek"), "once-*.db"));
    }

    // ---------- onarım: eski sürümün bıraktığı izler (madde 2) ----------

    [Fact]
    public void Eski_surum_iz_birakirsa_acilista_uid_atanir_eksik_olaylar_eklenir_ve_repair_applied_yazilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "on");
        var dbDosya = Path.Combine(dir, "soz.db");
        SozDb.OpenAndMigrate(dir);
        SqliteConnection.ClearAllPools();
        long logOnce;
        using (var c = Ham(dbDosya))
        {
            logOnce = Say(c, "event_log");
            // eski exe: göç etmez, uid ve log_id yazmaz
            c.Exec("INSERT INTO events(ts,kind,project_id,detail) VALUES('2026-11-01T10:00:00.000Z','add',2,'9')");
            c.Exec("INSERT INTO events(ts,kind,project_id,detail) VALUES('2026-11-01T10:00:01.000Z','job',2,'{\"id\":9,\"state\":\"taslak\"}')");
            c.Exec("INSERT INTO events(ts,kind,project_id,detail) VALUES('2026-11-01T10:00:02.000Z','hook_pulse',2,'{}')");
            c.Exec("INSERT INTO projects(key,name,path,created_at) VALUES('eski','Eski','D:\\eski','2026-11-01T09:00:00.000Z')");
            c.Exec("INSERT INTO jobs(project_id,title,raw_text,prompt_text,prompt_hash,source,state,night,restate,created_at,agent) VALUES(2,'t','r','','','yazi','taslak',0,0,'2026-11-01T09:30:00.000Z','claude')");
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE uid IS NULL")); // iz gerçekten var
        }
        SqliteConnection.ClearAllPools();

        var db = SozDb.OpenAndMigrate(dir); // onarım
        using (var c = db.Open())
        {
            foreach (var t in SozDb.UidTables) Assert.Equal(0L, c.ScalarLong($"SELECT COUNT(*) FROM {t.Table} WHERE uid IS NULL"));
            var jobUid = (string)c.Scalar("SELECT uid FROM jobs WHERE title='t'")!;
            Assert.Equal(DateTimeOffset.Parse("2026-11-01T09:30:00Z").ToUnixTimeMilliseconds(), UidMs(jobUid)); // uid kaydın zamanından
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE log_id IS NULL"));
            Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='hook_pulse' AND log_id=''")); // örnekteki ve eski exe'nin yazdığı nabız: event_log'a kopyalanmaz
            Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind IN ('legacy.add','legacy.job') AND ts>='2026-11-01'")); // eksik olaylar legacy.* olarak eklendi
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='legacy.hook_pulse'"));
            var rep = c.Query("SELECT payload FROM event_log WHERE kind='repair.applied'", r => JsonNode.Parse(r.GetString(0))!).Single();
            Assert.Equal(2, (int)rep["uids"]!); // proje + iş
            Assert.Equal(2, (int)rep["legacy_events"]!);
            Assert.Null(Events.VerifyChain(c));
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_kind WHERE kind='repair.applied'"));
            logOnce = Say(c, "event_log");
        }
        // ikinci açılış: temiz veri, hiçbir şey yazılmaz
        SqliteConnection.ClearAllPools();
        var db2 = SozDb.OpenAndMigrate(dir);
        using (var c = db2.Open())
        {
            Assert.Equal(logOnce, Say(c, "event_log"));
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='repair.applied'"));
        }
    }

    [Fact]
    public void Temiz_veride_acilis_event_log_a_hicbir_sey_yazmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "tv");
        var db = SozDb.OpenAndMigrate(dir);
        long once;
        using (var c = db.Open()) once = Say(c, "event_log");
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) SozDb.OpenAndMigrate(dir);
        _out.WriteLine($"OPEN_MS_ORT={sw.Elapsed.TotalMilliseconds / 50:F3} (v11 temiz sentetik örnek, onarım kontrolü dahil)");
        using var c2 = db.Open();
        Assert.Equal(once, Say(c2, "event_log"));
        Assert.Equal(0L, c2.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='repair.applied'"));
    }

    [Fact]
    public void Eski_v11_yapisinda_log_id_sutunu_yoksa_acilista_eklenir_ve_ikinci_acilis_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = OrnekKopya(env, "sy");
        var dbDosya = Path.Combine(dir, "soz.db");
        SozDb.OpenAndMigrate(dir);
        SqliteConnection.ClearAllPools();
        using (var c = Ham(dbDosya))
        {
            c.Exec("DROP INDEX ix_events_unlogged");
            c.Exec("ALTER TABLE events DROP COLUMN log_id"); // bu işten önceki v11 yapısı
        }
        SqliteConnection.ClearAllPools();
        var db = SozDb.OpenAndMigrate(dir);
        long once;
        using (var c = db.Open())
        {
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM pragma_table_info('events') WHERE name='log_id'"));
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE log_id IS NULL"));
            Assert.Null(Events.VerifyChain(c));
            once = Say(c, "event_log");
        }
        SqliteConnection.ClearAllPools();
        SozDb.OpenAndMigrate(dir);
        using var c2 = db.Open();
        Assert.Equal(once, Say(c2, "event_log"));
    }

    // ---------- olay kimliği (madde 3) ----------

    [Fact]
    public void Son_events_satiri_silinip_yeniden_eklenince_event_log_kimlik_hatasi_vermez()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        Events.Log(c, "add", null, "a");
        Events.Log(c, "add", null, "b");
        var sonId = c.ScalarLong("SELECT MAX(id) FROM events");
        c.Exec("DELETE FROM events WHERE id=@i", null, ("@i", sonId)); // denetçinin senaryosu: son satır silindi
        Events.Log(c, "add", null, "c"); // eskiden event_log.id UNIQUE hatası
        Assert.Equal(sonId, c.ScalarLong("SELECT MAX(id) FROM events")); // rowid gerçekten yeniden kullanıldı
        var idler = c.Query("SELECT id FROM event_log WHERE kind='legacy.add' ORDER BY seq", r => r.GetString(0));
        Assert.Equal(3, idler.Count);
        Assert.Equal(3, idler.Distinct().Count());
        Assert.All(idler, i => Assert.Matches(UuidV7, i));
        Assert.Null(Events.VerifyChain(c));
    }

    // ---------- hash zinciri kapsamı (madde 4) ----------

    [Theory]
    [InlineData("id")]
    [InlineData("device_uid")]
    [InlineData("ts")]
    [InlineData("kind")]
    [InlineData("kind_v")]
    [InlineData("entity")]
    [InlineData("entity_uid")]
    [InlineData("project_uid")]
    [InlineData("job_uid")]
    [InlineData("actor")]
    [InlineData("payload")]
    [InlineData("prev_hash")]
    [InlineData("hash")]
    public void Hash_zinciri_her_alandaki_oynamayi_yakalar(string alan)
    {
        using var env = new TestEnv(copySamples: false);
        long s2;
        using (var c = env.Svc.Db.Open())
        {
            Events.Append(c, "k.bir", "job", "ju1", "pu1", "ju1", "soz", "{\"n\":1}");
            s2 = Events.Append(c, "k.iki", "run", "ru1", "pu2", "ju2", "claude", "{\"n\":2}").Seq; // bütün alanlar dolu
            Events.Append(c, "k.uc", null, null, null, null, "soz", "{\"n\":3}");
            Assert.Null(Events.VerifyChain(c));
        }
        var kopya = Path.Combine(env.Root, "z.db");
        using (var c = env.Svc.Db.Open()) c.Exec("VACUUM INTO @f", null, ("@f", kopya));
        using var k = Ham(kopya);
        k.Exec("DROP TRIGGER trg_event_no_update");
        object yeni = alan == "kind_v" ? 2L : alan == "payload" ? "{\"n\":99}" : "degisti";
        k.Exec($"UPDATE event_log SET {alan}=@v WHERE seq=@s", null, ("@v", yeni), ("@s", s2));
        Assert.Equal(s2, Events.VerifyChain(k));
    }

    [Fact]
    public void Hash_bos_deger_ile_bos_metni_ve_alanlar_arasi_kaymayi_ayirir()
    {
        string H(string? entity, string? entityUid, string id = "i", string device = "d")
            => Events.ComputeHash(null, id, device, "t", "k", 1, entity, entityUid, null, null, "x", "{}");
        Assert.NotEqual(H("a", "bc"), H("ab", "c")); // alan sınırı kayması
        Assert.NotEqual(H(null, "x"), H("", "x")); // NULL ile boş metin farklı
        Assert.NotEqual(H("a", null), H("a", ""));
        Assert.NotEqual(H(null, null, id: "ab", device: "c"), H(null, null, id: "a", device: "bc"));
        Assert.Equal(H("a", "bc"), H("a", "bc")); // kararlı
        Assert.NotEqual(H("é", "x"), H("e", "x")); // UTF-8 bayt sayısına göre kodlanır
    }

    [Fact]
    public void Hash_null_alani_bos_metne_cevirmek_dogrulamada_yakalanir()
    {
        using var env = new TestEnv(copySamples: false);
        long s;
        using (var c = env.Svc.Db.Open()) s = Events.Append(c, "k.bos", null, null, null, null, "soz", "{}").Seq;
        var kopya = Path.Combine(env.Root, "n.db");
        using (var c = env.Svc.Db.Open()) c.Exec("VACUUM INTO @f", null, ("@f", kopya));
        using var k = Ham(kopya);
        k.Exec("DROP TRIGGER trg_event_no_update");
        k.Exec("UPDATE event_log SET entity='' WHERE seq=@s", null, ("@s", s));
        Assert.Equal(s, Events.VerifyChain(k));
    }

    // ---------- nabız (gürültü) olayları (madde 5) ----------

    [Fact]
    public void Nabiz_olaylari_adli_listede_tek_yerde_ve_yalniz_events_tablosuna_yazilir()
    {
        using var env = new TestEnv(copySamples: false);
        Assert.Equal(new[] { "hook_inject", "hook_pulse", "job_progress" }, Events.NoiseKinds.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        using var c = env.Svc.Db.Open();
        foreach (var k in Events.NoiseKinds)
        {
            long logOnce = Say(c, "event_log"), evOnce = Say(c, "events");
            Events.Log(c, k, null, "{\"x\":1}");
            Assert.Equal(logOnce, Say(c, "event_log")); // event_log'a girmedi
            Assert.Equal(evOnce + 1, Say(c, "events")); // eski tabloya girdi
            Assert.Equal("", c.Scalar("SELECT log_id FROM events ORDER BY id DESC LIMIT 1"));
        }
        long once = Say(c, "event_log");
        Events.Log(c, "approve", null, "1"); // anlamlı olay hâlâ iki yere
        Assert.Equal(once + 1, Say(c, "event_log"));
        Assert.Equal(c.Scalar("SELECT id FROM event_log ORDER BY seq DESC LIMIT 1"), c.Scalar("SELECT log_id FROM events ORDER BY id DESC LIMIT 1"));
        // gerçek yazım yolu: canlı ilerleme olayı da nabızdır
        once = Say(c, "event_log");
        Live.Emit(c, Live.JobProgress, null, new { jobId = 1, text = "satır" });
        Assert.Equal(once, Say(c, "event_log"));
        Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='job_progress'")); // döngüdeki ve Live.Emit'in yazdığı
        // canlı akış kaynağı: nabız türü events'ten, ötekiler event_log'dan
        Assert.Equal(new[] { "job_progress" }, LiveFeed.NoiseKinds);
        Assert.DoesNotContain("legacy.job_progress", LiveFeed.Kinds);
        Assert.Contains("legacy.job", LiveFeed.Kinds);
        Assert.Null(Events.VerifyChain(c));
    }

    // ---------- canlı akış: pompa (madde 5 ve 6) ----------

    [Fact]
    public void Poll_nabiz_ve_anlamli_olaylari_zamana_gore_birlestirir_ve_ikinci_turda_tekrar_vermez()
    {
        using var env = new TestEnv(copySamples: false);
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var n = 0;
        Time.UtcNow = () => t0.AddMilliseconds(n++);
        using var c = env.Svc.Db.Open();
        var cur = LiveFeed.Start(c);
        Live.Emit(c, Live.Job, null, new { id = 1 });
        Live.Emit(c, Live.JobProgress, null, new { jobId = 1, text = "a" });
        Events.Log(c, "hook_pulse", null, "{}"); // akışa girmez
        Live.Emit(c, Live.Proof, null, new { runId = 1 });
        Live.Emit(c, Live.JobProgress, null, new { jobId = 1, text = "b" });
        var alinan = new List<(string Name, long? Seq)>();
        Assert.Equal(4, LiveFeed.Poll(c, cur, (ad, _, seq) => alinan.Add((ad, seq))));
        Assert.Equal(new[] { "job", "job.progress", "proof", "job.progress" }, alinan.Select(x => x.Name).ToArray());
        Assert.Equal(new bool[] { true, false, true, false }, alinan.Select(x => x.Seq is not null).ToArray()); // nabız "id:" taşımaz
        Assert.Equal(0, LiveFeed.Poll(c, cur, (_, _, _) => Assert.Fail("ikinci turda tekrar yayınlandı")));
    }

    [Fact]
    public void Poll_birikmis_olaylari_500luk_turlarla_eksiksiz_ve_tekrarsiz_verir()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var cur = LiveFeed.Start(c);
        using (var tx = c.BeginTransaction())
        {
            for (var i = 0; i < 1300; i++) Events.Log(c, "queue", null, $"{{\"id\":{i}}}", tx);
            for (var i = 0; i < 700; i++) Events.Log(c, "job_progress", null, $"{{\"jobId\":{i}}}", tx);
            tx.Commit();
        }
        var sayilar = new List<int>();
        var alinan = new List<(string Name, long? Seq, int No)>();
        int s;
        do
        {
            s = LiveFeed.Poll(c, cur, (ad, p, seq) => alinan.Add((ad, seq, (int)(p["id"] ?? p["jobId"])!)));
            sayilar.Add(s);
        } while (s > 0);
        Assert.Equal(new[] { 1000, 700, 300, 0 }, sayilar.ToArray()); // iki kaynak da 500'lük turlarla: 500+500, 500+200, 300, 0
        Assert.Equal(1300, alinan.Count(a => a.Name == "queue"));
        Assert.Equal(700, alinan.Count(a => a.Name == "job.progress"));
        Assert.Equal(Enumerable.Range(0, 1300), alinan.Where(a => a.Name == "queue").Select(a => a.No).OrderBy(x => x));
        Assert.Equal(1300, alinan.Where(a => a.Seq is not null).Select(a => a.Seq!.Value).Distinct().Count());
        Assert.Equal(700, alinan.Where(a => a.Name == "job.progress").Select(a => a.No).Distinct().Count());
    }

    [Fact]
    public void ReadSince_ust_sinir_okuma_sirasinda_araya_yazilan_olayi_bu_turdan_disarida_birakir()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var bas = Events.LastSeq(c);
        Events.Append(c, "u.bir", null, null, null, null, "soz", "{}");
        var sinir = Events.LastSeq(c); // pompa max'ı böyle okur
        Events.Append(c, "u.iki", null, null, null, null, "soz", "{}"); // max okunduktan sonra araya yazıldı
        Events.Append(c, "u.uc", null, null, null, null, "soz", "{}");
        var rows = Events.ReadSince(c, bas, 500, null, sinir);
        Assert.Equal(new[] { "u.bir" }, rows.Select(r => r.Kind).ToArray());
        Assert.Equal(new[] { "u.iki", "u.uc" }, Events.ReadSince(c, sinir).Select(r => r.Kind).ToArray()); // sonraki turda bir kez
    }

    [Fact]
    public async Task Hizli_yazimda_ayni_olay_iki_kez_yayinlanmaz_ve_hicbiri_kacmaz()
    {
        using var env = new TestEnv(copySamples: false);
        const int Adet = 1500;
        var alinan = new List<(string Name, long? Seq, int No)>();
        LiveFeed.Cursor cur; using (var c0 = env.Svc.Db.Open()) cur = LiveFeed.Start(c0);
        var yazar = Task.Run(() =>
        {
            using var w = env.Svc.Db.Open();
            for (var i = 0; i < Adet; i++)
            {
                Live.Emit(w, Live.Job, null, new { id = i });
                if (i % 3 == 0) Live.Emit(w, Live.JobProgress, null, new { jobId = i });
            }
        });
        void Tur() { using var r = env.Svc.Db.Open(); LiveFeed.Poll(r, cur, (ad, p, seq) => alinan.Add((ad, seq, (int)(p["id"] ?? p["jobId"])!))); }
        while (!yazar.IsCompleted) Tur();
        await yazar;
        for (var i = 0; i < 5; i++) Tur(); // kalanlar
        var isler = alinan.Where(a => a.Name == "job").ToList();
        Assert.Equal(Adet, isler.Count);
        Assert.Equal(Adet, isler.Select(a => a.Seq!.Value).Distinct().Count()); // aynı seq iki kez gitmedi
        Assert.Equal(isler.Select(a => a.Seq!.Value).OrderBy(x => x), isler.Select(a => a.Seq!.Value)); // sıra bozulmadı
        Assert.Equal(Adet, isler.Select(a => a.No).Distinct().Count());
        var ilerleme = alinan.Where(a => a.Name == "job.progress").ToList();
        Assert.Equal(Adet / 3, ilerleme.Count);
        Assert.Equal(Adet / 3, ilerleme.Select(a => a.No).Distinct().Count());
    }

    // ---------- canlı akış: sunucu (SSE) ----------

    private static async Task<List<(long? Id, string Name)>> AkisOku(SozServerHandle h, string url, int beklenen, Func<Task>? baglaninca = null, Action<HttpRequestMessage>? ayar = null, int zamanAsimiSn = 20)
    {
        using var http = new HttpClient { BaseAddress = new Uri(h.Url), Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ayar?.Invoke(req);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var rd = new StreamReader(await resp.Content.ReadAsStreamAsync());
        if (baglaninca is not null) await baglaninca(); // abonelik, başlıklar gelmeden önce kuruldu: bundan sonra yazılan olay kaçmaz
        var bulunan = new List<(long?, string)>();
        long? id = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(zamanAsimiSn));
        try
        {
            while (bulunan.Count < beklenen)
            {
                var line = await rd.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (line.StartsWith("id: ")) id = long.Parse(line[4..]);
                else if (line.StartsWith("event: ")) { bulunan.Add((id, line[7..])); id = null; }
            }
        }
        catch (OperationCanceledException) { }
        return bulunan;
    }

    private static int RastgeleBaglanti() => 51000 + Random.Shared.Next(0, 900);

    [Fact]
    public async Task Sunucu_akisi_nabiz_olayini_id_olmadan_anlamli_olayi_seq_ile_yayinlar()
    {
        using var env = new TestEnv(copySamples: false);
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var n = 0;
        Time.UtcNow = () => t0.AddMilliseconds(n++);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(NodeSample.Create(env));
        await using var h = await SozServer.StartAsync(env.DataDir, RastgeleBaglanti());
        long queueSeq = 0;
        var a = await AkisOku(h, "/api/events/stream", 2, async () =>
        {
            using var c = env.Svc.Db.Open();
            Live.Emit(c, Live.JobProgress, pid, new { jobId = 1, runId = 1, text = "çalışıyor" });
            Live.Emit(c, Live.Queue, pid, new { id = 3 });
            queueSeq = Events.LastSeq(c);
            await Task.CompletedTask;
        });
        Assert.Equal(new[] { "job.progress", "queue" }, a.Select(x => x.Name).ToArray());
        Assert.Null(a[0].Id); // nabız: kalıcı sırası yok
        Assert.Equal(queueSeq, a[1].Id);
        using var c2 = env.Svc.Db.Open();
        Assert.Equal(0L, c2.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE '%job_progress'")); // event_log gürültüsüz
    }

    [Fact]
    public async Task Since_sinir_degerleri_dogru_yerden_baslar_ve_cop_deger_geri_verme_yapmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(NodeSample.Create(env));
        long s1, s2, s3;
        using (var c = env.Svc.Db.Open())
        {
            Live.Emit(c, Live.Job, pid, new { id = 1 }); s1 = Events.LastSeq(c);
            Live.Emit(c, Live.JobProgress, pid, new { jobId = 1 }); // nabız: since ile geri verilmez
            Live.Emit(c, Live.JobState, pid, new { jobId = 1 }); s2 = Events.LastSeq(c);
            Live.Emit(c, Live.Proof, pid, new { runId = 1 }); s3 = Events.LastSeq(c);
        }
        await using var h = await SozServer.StartAsync(env.DataDir, RastgeleBaglanti()); // pompa imleci sonda: bu üçü yalnız since ile gelir
        static string[] Adlar(List<(long? Id, string Name)> l) => l.Select(x => x.Name).ToArray();

        Assert.Equal(new[] { "job.state", "proof" }, Adlar(await AkisOku(h, $"/api/events/stream?since={s1}", 2))); // since = ilk olayın seq'i: kendisi dahil değil
        Assert.Equal(new[] { "job", "job.state", "proof" }, Adlar(await AkisOku(h, $"/api/events/stream?since={s1 - 1}", 3))); // bir eksiği: ilk olay dahil
        Assert.Equal(new[] { "job", "job.state", "proof" }, Adlar(await AkisOku(h, "/api/events/stream?since=0", 3)));
        Assert.Equal(new[] { "job", "job.state", "proof" }, Adlar(await AkisOku(h, "/api/events/stream?since=-5", 3))); // negatif sıfır sayılır
        var basinda = await AkisOku(h, "/api/events/stream", 2, ayar: r => r.Headers.Add("Last-Event-ID", (s2 - 1).ToString()));
        Assert.Equal(new[] { "job.state", "proof" }, Adlar(basinda)); // başlık since ile aynı işi yapar; nabız (job.progress) geri verilmedi

        // geri verme olmaması gereken durumlar: bağlandıktan sonra yazılan ilk olay (sentinel) ilk gelen olmalı
        async Task GeriVermeYok(string url)
        {
            long sentinel = 0;
            var l = await AkisOku(h, url, 1, async () =>
            {
                using var c = env.Svc.Db.Open();
                Live.Emit(c, Live.Queue, pid, new { id = 99 });
                sentinel = Events.LastSeq(c);
                await Task.CompletedTask;
            });
            var ilk = Assert.Single(l);
            Assert.Equal("queue", ilk.Name);
            Assert.Equal(sentinel, ilk.Id);
            Assert.True(ilk.Id > s3);
        }
        await GeriVermeYok($"/api/events/stream?since={s3}"); // son olayı görmüş istemci: kaçırdığı yok (önce, sentinel yokken)
        await GeriVermeYok("/api/events/stream?since=abc");
        await GeriVermeYok("/api/events/stream?since=");
        await GeriVermeYok("/api/events/stream?since=1.5");
        await GeriVermeYok("/api/events/stream?since=99999999999999999999"); // taşan sayı
        await GeriVermeYok("/api/events/stream?since=9223372036854775807"); // long.MaxValue: ötesinde olay yok
    }

    [Fact]
    public async Task Since_geri_verme_tavani_asilinca_replay_truncated_ile_kesilir()
    {
        using var env = new TestEnv(copySamples: false);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(NodeSample.Create(env));
        using (var c = env.Svc.Db.Open())
        using (var tx = c.BeginTransaction())
        {
            for (var i = 0; i < LiveFeed.ReplayCap + 100; i++) Events.Log(c, "queue", pid, $"{{\"id\":{i}}}", tx);
            tx.Commit();
        }
        await using var h = await SozServer.StartAsync(env.DataDir, RastgeleBaglanti());
        var l = await AkisOku(h, "/api/events/stream?since=0", LiveFeed.ReplayCap + 1, zamanAsimiSn: 60);
        Assert.Equal(LiveFeed.ReplayCap + 1, l.Count);
        Assert.All(l.Take(LiveFeed.ReplayCap), x => Assert.Equal("queue", x.Name));
        Assert.Equal("replay.truncated", l[^1].Name);
        var seqler = l.Take(LiveFeed.ReplayCap).Select(x => x.Id!.Value).ToList();
        Assert.Equal(seqler.OrderBy(x => x), seqler); // sıralı
        Assert.Equal(LiveFeed.ReplayCap, seqler.Distinct().Count()); // tekrarsız
    }

    // ---------- POST /api/jobs: iş türü (madde 8) ----------

    [Fact]
    public async Task Post_jobs_kind_alani_izinli_degerleri_kabul_eder_yoksa_kod_bilinmeyeni_400_ile_reddeder()
    {
        using var env = new TestEnv(copySamples: false);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(NodeSample.Create(env));
        await using var h = await SozServer.StartAsync(env.DataDir, RastgeleBaglanti());
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        async Task<(HttpStatusCode Kod, JsonNode? Govde, string Ham)> Yolla(object govde)
        {
            using var r = new HttpRequestMessage(HttpMethod.Post, "/api/jobs") { Content = JsonContent.Create(govde) };
            r.Headers.Add("X-Soz-Token", h.Token);
            using var resp = await http.SendAsync(r);
            var ham = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, string.IsNullOrWhiteSpace(ham) ? null : JsonNode.Parse(ham), ham);
        }

        var yok = await Yolla(new { projectId = pid, rawText = "kind verilmedi" });
        Assert.Equal(HttpStatusCode.Created, yok.Kod);
        Assert.Equal("kod", (string?)yok.Govde!["kind"]);
        var bos = await Yolla(new Dictionary<string, object?> { ["projectId"] = pid, ["rawText"] = "kind null", ["kind"] = null });
        Assert.Equal("kod", (string?)bos.Govde!["kind"]);
        foreach (var k in new[] { "kod", "yazi", "arastirma", "tablo", "tarayici", "dosya-duzeni" })
        {
            var r = await Yolla(new { projectId = pid, rawText = "tür " + k, kind = k });
            Assert.Equal(HttpStatusCode.Created, r.Kod);
            Assert.Equal(k, (string?)r.Govde!["kind"]);
        }
        Assert.Equal("tablo", (string?)(await Yolla(new { projectId = pid, rawText = "boşluk ve büyük harf", kind = "  Tablo " })).Govde!["kind"]);
        Assert.Equal("yazi", new WorkService(env.Svc.Db).Job((long)(await Yolla(new { projectId = pid, rawText = "kalıcı mı", kind = "yazi" })).Govde!["id"]!).Kind);

        foreach (var kotu in new object[] { "bilinmeyen", "", "kod;drop", 5, true })
        {
            var r = await Yolla(new { projectId = pid, rawText = "kötü tür", kind = kotu });
            Assert.Equal(HttpStatusCode.BadRequest, r.Kod);
            Assert.Contains("Bilinmeyen iş türü", (string?)r.Govde!["error"]);
            Assert.Contains("dosya-duzeni", (string?)r.Govde!["error"]); // izinli liste söylenir
        }
        using var say = env.Svc.Db.Open();
        Assert.Equal(10L, say.ScalarLong("SELECT COUNT(*) FROM jobs")); // reddedilenler iş doğurmadı: 2 varsayılan + 6 tür + boşluklu + kalıcı
    }
}
