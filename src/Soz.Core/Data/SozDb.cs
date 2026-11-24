using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Soz.Core.Data;

/// <summary>SQLite bağlantısı ve şema göçleri. Şema sürümü PRAGMA user_version ile tutulur.</summary>
public sealed class SozDb
{
    public const int CurrentVersion = 16;

    /// <summary>PRAGMA application_id: "SOZ1" (0x534F5A31). Başka program ya da bozuk dosya açılışta tanınır.</summary>
    public const long ApplicationId = 1397709361;

    /// <summary>Göç öncesi yedeklerden (once-*.db) tutulan son sayı.</summary>
    public const int KeepPreMigrationBackups = 3;

    /// <summary>Test dikişi: göç işleminin içinde her adımdan sonra adın adıyla çağrılır; istisna atarsa göç geri alınır, veritabanı eski sürümde kalır.</summary>
    public Action<string>? MigrationFault { get; set; }

    /// <summary>Test dikişi: göç öncesi yedek yazıldıktan sonra, doğrulamadan önce yedeğin yoluyla çağrılır (bozuk yedek denemesi için).</summary>
    public Action<string>? AfterBackup { get; set; }

    public string DataDir { get; }
    public string DbPath { get; }
    private readonly string _connString;

    public SozDb(string dataDir, int busyTimeoutMs = 5000)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
        DbPath = Path.Combine(DataDir, "soz.db");
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = Math.Max(1, busyTimeoutMs / 1000),
        }.ToString();
        BusyTimeoutMs = busyTimeoutMs;
    }

    public int BusyTimeoutMs { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA foreign_keys=OFF;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Veritabanını açar, gerekirse oluşturur ve son sürüme göç ettirir.</summary>
    public static SozDb OpenAndMigrate(string dataDir, int busyTimeoutMs = 5000)
    {
        var db = new SozDb(dataDir, busyTimeoutMs);
        db.Migrate();
        return db;
    }

    /// <summary>
    /// Kanca hızlı yolu (&lt;25 ms): soz.db zaten mevcut ve PRAGMA user_version == CurrentVersion ise
    /// ağır ReadHeader + 9 sorgulu NeedsRepairSql + göç kilitlerini tamamen atlar ve açık bağlantıyı döndürür.
    /// </summary>
    public static (SozDb Db, SqliteConnection Conn, bool IsCurrentSchema) OpenFastForHook(string dataDir, int busyTimeoutMs = 1500)
    {
        var fullDir = Path.GetFullPath(dataDir);
        var dbFile = Path.Combine(fullDir, "soz.db");
        if (File.Exists(dbFile))
        {
            try
            {
                var fi = new FileInfo(dbFile);
                if (fi.Length > 0)
                {
                    var fastDb = new SozDb(fullDir, busyTimeoutMs);
                    var conn = fastDb.Open();
                    try
                    {
                        var ver = Convert.ToInt32(conn.Scalar("PRAGMA user_version"));
                        if (ver == CurrentVersion)
                            return (fastDb, conn, true);
                    }
                    catch (Exception)
                    {
                        conn.Dispose();
                        throw;
                    }
                    conn.Dispose();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var migrated = OpenAndMigrate(fullDir, busyTimeoutMs);
        return (migrated, migrated.Open(), true);
    }

    public int GetVersion()
    {
        using var c = Open();
        return Convert.ToInt32(c.Scalar("PRAGMA user_version"));
    }

    /// <summary>Yedek klasörü: &lt;veri&gt;\yedek.</summary>
    public string BackupDir => Path.Combine(DataDir, "yedek");

    /// <summary>Başka bir süreç göç yaparken bekleme süresi. Aşılırsa sade Türkçe <see cref="SozDataAccessException"/> (neden goc_kilidi) atılır.</summary>
    public TimeSpan MigrationLockTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private sealed record Header(int Version, long AppId, bool Existed, bool NeedsRepair);

    /// <summary>
    /// Açılış koruması: veritabanına yazmadan (salt okunur bağlantı) sürümü ve application_id değerini okur.
    /// Dosya yoksa yeni veritabanıdır (sürüm 0). Sürüm 11 ve üstündeyse aynı bağlantıda ucuz bir onarım kontrolü de yapılır
    /// (bkz. <see cref="NeedsRepairSql"/>): eski bir Söz sürümünün bıraktığı izler var mı.
    /// </summary>
    private Header ReadHeader()
    {
        if (!File.Exists(DbPath) || new FileInfo(DbPath).Length == 0) return new Header(0, 0, false, false);
        var cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = Math.Max(1, BusyTimeoutMs / 1000) }.ToString();
        try
        {
            using var r = new SqliteConnection(cs);
            r.Open();
            var v = Convert.ToInt32(r.Scalar("PRAGMA user_version"));
            var app = Convert.ToInt64(r.Scalar("PRAGMA application_id"));
            var repair = false;
            if (v >= 11 && app == ApplicationId)
            {
                try { repair = Convert.ToInt64(r.Scalar(NeedsRepairSql)) != 0; }
                catch (SqliteException e) when (e.SqliteErrorCode == 1)
                {
                    // Sorgu çalışmadı: eski v11 yapısında events.log_id sütunu yok olabilir (onarım gerekir). Tablo hiç yoksa ya da başka bir şey eksikse
                    // bu kontrol onarım değildir; açılış engellenmez (eski davranış: olay günlüğü olmasa da Söz açılır).
                    var cols = new List<string>();
                    using (var cmd = r.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA table_info(events)";
                        using var rd = cmd.ExecuteReader();
                        while (rd.Read()) cols.Add(rd.GetString(1));
                    }
                    repair = cols.Count > 0 && !cols.Contains("log_id", StringComparer.OrdinalIgnoreCase);
                }
            }
            return new Header(v, app, true, repair);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 26)
        {
            throw new SozForeignDatabaseException(0, "Bu dosya bir Söz veritabanı değil (SQLite dosyası olarak okunamadı). Dosyaya dokunulmadı.");
        }
    }

    private static void CheckHeader(Header h)
    {
        // Koruma 1: daha yeni sürümle yazılmış veri sessizce geçilmez; veritabanına dokunmadan çıkılır.
        if (h.Version > CurrentVersion) throw new SozSchemaTooNewException(h.Version, CurrentVersion);
        // Koruma 2: başka programın dosyası (application_id sıfır değil ve bizimki değil) açılmaz.
        if (h.AppId != 0 && h.AppId != ApplicationId)
            throw new SozForeignDatabaseException(h.AppId, "Bu dosya Söz'ün veritabanı değil (başka bir program yazmış). Dosyaya dokunulmadı.");
    }

    public void Migrate() => MigrateTo(CurrentVersion);

    /// <summary>
    /// <paramref name="target"/> sürümüne kadar göç eder (üretimde hep <see cref="CurrentVersion"/>; eski sürümlü sentetik örnek dosyaları üretmek için testler daha küçük hedef verir).
    /// Ham SQLite ve dosya sistemi hataları sade Türkçe <see cref="SozDataAccessException"/> olarak çıkar (ham ayrıntı iç istisnada).
    /// </summary>
    public void MigrateTo(int target)
    {
        try { MigrateCore(target); }
        catch (SqliteException e) { throw SozDataAccessException.From(e, "Veri dosyası açılamadı"); }
        catch (UnauthorizedAccessException e) { throw SozDataAccessException.From(e, "Veri dosyası açılamadı"); }
    }

    private void MigrateCore(int target)
    {
        // Sık yol (kanca, CLI, sunucu): sürüm güncel ve onarım gerekmiyorsa kilit alınmaz.
        // Başka süreç o sırada göç yapıyorsa okuma meşgul çıkabilir; o durumda kilit yoluna düşülür.
        try
        {
            var first = ReadHeader();
            CheckHeader(first);
            if (first.Version >= target && !first.NeedsRepair) return;
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { /* göç sürüyor olabilir: kilidi bekle */ }

        // Süreçler arası göç kilidi: aynı anda ilk kez açan süreçlerden yalnız biri göç eder, ötekiler bitmesini bekler.
        using var gate = AcquireMigrationLock();
        var h = ReadHeader(); // kilit İÇİNDE yeniden oku: başkası göçü bitirmişse sessizce çık
        CheckHeader(h);
        var v = h.Version;
        if (v >= target)
        {
            if (h.NeedsRepair) RepairV11();
            return;
        }

        if (h.Existed && (File.GetAttributes(DbPath) & FileAttributes.ReadOnly) != 0)
            throw new SozDataAccessException("salt_okunur", "Veri dosyası salt okunur işaretli, Söz göç edemiyor. Dosyanın özelliklerinden 'Salt okunur' işaretini kaldır ve Söz'ü yeniden aç. Veriye dokunulmadı.");

        using var c = Open();
        var from = v;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Koruma 3: göçten ÖNCE, işlem dışında tutarlı yedek (VACUUM INTO işlem içinde çalışmaz). Yeni (boş) veritabanında yedek alınmaz.
        BackupInfo? backup = h.Existed && v >= 1 ? TakePreMigrationBackup(c, v) : null;
        c.Exec("PRAGMA journal_mode=WAL;");
        using var tx = c.BeginTransaction();
        if (v < 1 && target >= 1) { c.Exec(SchemaV1, tx); v = 1; }
        if (v < 2 && target >= 2) { c.Exec(SchemaV2, tx); v = 2; }
        if (v < 3 && target >= 3) { MigrateV3(c, tx); v = 3; }
        if (v < 4 && target >= 4) { MigrateV4(c, tx); v = 4; }
        if (v < 5 && target >= 5) { MigrateV5(c, tx); v = 5; }
        if (v < 6 && target >= 6) { MigrateV6(c, tx); v = 6; }
        if (v < 7 && target >= 7) { MigrateV7(c, tx); v = 7; }
        if (v < 8 && target >= 8) { MigrateV8(c, tx); v = 8; }
        if (v < 9 && target >= 9) { MigrateV9(c, tx); v = 9; }
        if (v < 10 && target >= 10) { MigrateV10(c, tx); v = 10; }
        if (v < 11 && target >= 11) { MigrateV11(c, tx, from, backup, sw); v = 11; }
        if (v < 12 && target >= 12) { MigrateV12(c, tx, from, backup, sw); v = 12; }
        if (v < 13 && target >= 13) { MigrateV13(c, tx, from, backup, sw); v = 13; }
        if (v < 14 && target >= 14) { MigrateV14(c, tx, from, backup, sw); v = 14; }
        if (v < 15 && target >= 15) { MigrateV15(c, tx, from, backup, sw); v = 15; }
        if (v < 16 && target >= 16)
        {
            Soz.Core.Profile.ProfileSchema.ApplyV16(c, tx);
            MigrationFault?.Invoke("v16.sema");
            c.Exec("INSERT OR IGNORE INTO schema_meta(version,applied_at,app_version,backup_file) VALUES(16,@t,@a,@b)",tx,
                ("@t",Time.NowIso()),("@a",SozInfo.Version),("@b",backup is null?null:Path.GetFileName(backup.Path)));
            v = 16;
        }
        c.Exec($"PRAGMA user_version={v};", tx);
        if (v >= 11) c.Exec($"PRAGMA application_id={ApplicationId};", tx);
        tx.Commit();
        EnsureGeneralProject(c, withUid: v >= 11);
    }

    // ---------- süreçler arası göç kilidi ----------

    /// <summary>
    /// &lt;veri&gt;\goc.kilit dosyasını başka süreç açamayacak biçimde (FileShare.None) açar; süreç ölürse işletim sistemi kilidi kendisi bırakır.
    /// Başkası tutuyorsa <see cref="MigrationLockTimeout"/> boyunca bekler; aşılırsa sade Türkçe hata. Dosya silinmez (silmek yarış doğurur), boş kalır.
    /// </summary>
    private FileStream AcquireMigrationLock()
    {
        var path = Path.Combine(DataDir, "goc.kilit");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        var delay = 15;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException e)
            {
                var (reason, _) = SozDataAccessException.Explain(e);
                if (reason is "disk_dolu") throw SozDataAccessException.From(e, "Veri dosyası açılamadı");
                if (wait.Elapsed >= MigrationLockTimeout)
                    throw new SozDataAccessException("goc_kilidi", $"Veri başka bir Söz süreci tarafından güncelleniyor ve {(int)Math.Max(1, MigrationLockTimeout.TotalSeconds)} saniyede bitmedi. Birkaç dakika bekleyip Söz'ü yeniden aç. Sürerse açık Söz pencerelerini kapat. Veriye dokunulmadı.", e);
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 200);
            }
        }
    }

    // ---------- eski sürümün bıraktığı izlerin onarımı ----------

    /// <summary>
    /// Sürüm 11 olan veride eski bir Söz sürümü (göç etmeyen, yalnız events tablosuna yazan) iz bırakmış olabilir:
    /// uid'si boş satırlar ve event_log'da karşılığı olmayan events satırları. Burada uid atanır, eksik olaylar "legacy.*" olarak eklenir ve
    /// bir <c>repair.applied</c> olayı yazılır. Kilit içinde, tek işlemde çalışır; tekrar çalıştırılabilir. Temiz veride hiçbir şey yazmaz.
    /// </summary>
    private void RepairV11()
    {
        using var c = Open();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var tx = c.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: yazma kilidi baştan
        var hadColumn = c.Query("PRAGMA table_info(events)", r => r.GetString(1), tx).Contains("log_id", StringComparer.OrdinalIgnoreCase);
        var device = Events.DeviceUid(c, tx);
        if (!hadColumn)
        {
            AddColumnIfMissing(c, tx, "events", "log_id", "TEXT");
            // Sütunsuz yapıda çift yazılmış olayların event_log kimliği "legacy-<cihaz>-<events.id>" idi: bunlar yeniden kopyalanmasın.
            c.Exec("UPDATE events SET log_id='legacy-'||@d||'-'||id WHERE log_id IS NULL AND EXISTS(SELECT 1 FROM event_log l WHERE l.id='legacy-'||@d||'-'||events.id)", tx, ("@d", device));
        }
        c.Exec("CREATE INDEX IF NOT EXISTS ix_events_unlogged ON events(id) WHERE log_id IS NULL", tx);
        var uids = 0;
        foreach (var t in UidTables)
        {
            c.Exec($"CREATE INDEX IF NOT EXISTS ix_{t.Table}_uid_null ON {t.Table}(id) WHERE uid IS NULL", tx);
            uids += BackfillUids(c, tx, t.Table, t.TimeColumn);
        }
        var (copied, noise) = Events.CopyLegacy(c, tx, device);
        if (uids + copied > 0)
        {
            c.Exec("INSERT OR IGNORE INTO event_kind(kind, owner, schema_v, since, description) VALUES('repair.applied','soz.core',1,@t,'Eski sürümün bıraktığı izler onarıldı (uids, legacy_events, noise_marked, ms).')", tx, ("@t", Time.NowIso()));
            Events.Append(c, "repair.applied", null, null, null, null, "soz",
                new JsonObject { ["uids"] = uids, ["legacy_events"] = copied, ["noise_marked"] = noise, ["ms"] = sw.ElapsedMilliseconds, ["app_version"] = SozInfo.Version }.ToJsonString(), tx);
        }
        tx.Commit();
    }

    private static void EnsureGeneralProject(SqliteConnection c, bool withUid)
    {
        if (withUid) c.Exec("INSERT OR IGNORE INTO projects(key,name,path,created_at,uid) VALUES('*','Genel',NULL,@t,@u)", null, ("@t", Time.NowIso()), ("@u", Uid.New()));
        else c.Exec("INSERT OR IGNORE INTO projects(key,name,path,created_at) VALUES('*','Genel',NULL,@t)", null, ("@t", Time.NowIso()));
    }

    // ---------- göç öncesi yedek ----------

    private sealed record BackupInfo(string Path, long Bytes);

    /// <summary>
    /// VACUUM INTO ile &lt;veri&gt;\yedek\once-v&lt;n&gt;-&lt;yyyyMMddHHmm&gt;-&lt;8 hane rastgele&gt;.db yazar, yedekte PRAGMA quick_check = ok değilse göçü durdurur.
    /// Rastgele ek yüzünden iki yedek aynı adı almaz; hata yolunda yalnız bu çağrının oluşturduğu dosya kaldırılır.
    /// Başarılı olunca son <see cref="KeepPreMigrationBackups"/> once- dosyası tutulur, eskileri ürünün kendi veri klasöründen kaldırılır.
    /// </summary>
    private BackupInfo TakePreMigrationBackup(SqliteConnection c, int version)
    {
        string? path = null;
        var created = false; // dosya bu çağrıda doğdu mu (yalnız onu kaldırırız)
        try
        {
            if (File.Exists(BackupDir))
                throw new SozBackupFailedException("Yedek klasörü açılamadı: veri klasöründe 'yedek' adında bir dosya var. O dosyayı başka yere taşı ya da adını değiştir, sonra Söz'ü yeniden aç. Göç başlamadı, veri olduğu gibi duruyor.");
            Directory.CreateDirectory(BackupDir);
            var stamp = Time.UtcNow().ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
            do path = Path.Combine(BackupDir, $"once-v{version}-{stamp}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.db");
            while (File.Exists(path));
            created = true; // VACUUM INTO hata verse bile dosya yarım doğmuş olabilir; ad bize ait (rastgele, yeni)
            c.Exec("VACUUM INTO @f", null, ("@f", path));
            AfterBackup?.Invoke(path);
            var check = QuickCheck(path);
            if (check != "ok") throw new SozBackupFailedException($"Göç öncesi yedek doğrulanamadı (quick_check: {check}). Göç başlamadı, veri olduğu gibi duruyor.");
        }
        catch (SozBackupFailedException)
        {
            if (created) RemoveQuietly(path); // bozuk yedek işe yaramaz; asıl veri dokunulmamış
            throw;
        }
        catch (Exception e)
        {
            if (created) RemoveQuietly(path);
            var (_, text) = SozDataAccessException.Explain(e);
            throw new SozBackupFailedException("Göç öncesi yedek alınamadı. " + text.Replace(" Veriye dokunulmadı.", "") + " Göç başlamadı, veri olduğu gibi duruyor.", e);
        }
        PruneBackups("once-*.db", KeepPreMigrationBackups, path);
        return new BackupInfo(path, new FileInfo(path).Length);
    }

    private static string QuickCheck(string file)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        try
        {
            using var r = new SqliteConnection(cs);
            r.Open();
            return Convert.ToString(r.Scalar("PRAGMA quick_check")) ?? "boş sonuç";
        }
        catch (SqliteException e) { return "okunamadı: " + e.Message; }
    }

    private static void RemoveQuietly(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>En yeni <paramref name="keep"/> dosyayı tutar (her zaman <paramref name="current"/> dahil), kalanını yedek klasöründen kaldırır.</summary>
    private void PruneBackups(string pattern, int keep, string current)
    {
        var files = new DirectoryInfo(BackupDir).GetFiles(pattern)
            .OrderByDescending(f => string.Equals(f.FullName, current, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var f in files.Skip(keep)) RemoveQuietly(f.FullName);
    }

    /// <summary>
    /// v11 (veri zemini): sürüm geçmişi, cihaz kimliği, kararlı kimlikler (uid), proje yolu geçmişi, ekle-yalnız olay kaydı, ayar/kayıt defteri tabloları, iş türü.
    /// Yalnız eklemelidir (sütun silinmez, yeniden adlandırılmaz), tekrar çalıştırılabilir (IF NOT EXISTS, eksik sütun, uid IS NULL, kopyalanmamış olay).
    /// </summary>
    private void MigrateV11(SqliteConnection c, SqliteTransaction tx, int fromVersion, BackupInfo? backup, System.Diagnostics.Stopwatch sw)
    {
        var now = Time.NowIso();
        c.Exec(SchemaV11, tx);
        foreach (var t in UidTables) AddColumnIfMissing(c, tx, t.Table, "uid", "TEXT");
        AddColumnIfMissing(c, tx, "jobs", "kind", "TEXT NOT NULL DEFAULT 'kod'");
        AddColumnIfMissing(c, tx, "events", "log_id", "TEXT"); // event_log karşılığının kimliği: NULL = kopyalanmadı, '' = bilerek kopyalanmaz (nabız)
        c.Exec("CREATE INDEX IF NOT EXISTS ix_events_unlogged ON events(id) WHERE log_id IS NULL", tx);
        MigrationFault?.Invoke("v11.sema");

        // cihaz kimliği (tek satır)
        c.Exec("INSERT OR IGNORE INTO device(id, device_uid, created_at) VALUES(1,@u,@t)", tx, ("@u", Uid.New()), ("@t", now));
        var device = Events.DeviceUid(c, tx);

        // eski satırlara uid: kaydın zaman alanından (yoksa şimdiki an), sonra kısmi tekil dizin
        foreach (var t in UidTables) BackfillUids(c, tx, t.Table, t.TimeColumn);
        foreach (var t in UidTables) c.Exec($"CREATE UNIQUE INDEX IF NOT EXISTS ux_{t.Table}_uid ON {t.Table}(uid) WHERE uid IS NOT NULL", tx);
        foreach (var t in UidTables) c.Exec($"CREATE INDEX IF NOT EXISTS ix_{t.Table}_uid_null ON {t.Table}(id) WHERE uid IS NULL", tx); // açılışta ucuz onarım kontrolü için
        MigrationFault?.Invoke("v11.uid");

        // proje yolu geçmişi: mevcut yol is_current=1
        c.Exec("""
            INSERT OR IGNORE INTO project_path(project_uid, path, first_seen, last_seen, is_current)
            SELECT uid, path, COALESCE(created_at, @t), @t, 1 FROM projects WHERE path IS NOT NULL AND path<>'' AND uid IS NOT NULL
            """, tx, ("@t", now));
        MigrationFault?.Invoke("v11.proje_yolu");

        // olay türü kaydı (yalnız bugün yazılanlar) ve geçmiş events satırlarının kopyası
        c.Exec("""
            INSERT OR IGNORE INTO event_kind(kind, owner, schema_v, since, description) VALUES
              ('migration.applied','soz.core',1,@t,'Şema göçü tamamlandı (from, to, backup_file, ms).'),
              ('backup.taken','soz.core',1,@t,'Göç öncesi yedek alındı ve doğrulandı (file, bytes, from_version).')
            """, tx, ("@t", now));
        var (copied, noiseSkipped) = Events.CopyLegacy(c, tx, device);
        MigrationFault?.Invoke("v11.eski_olaylar");

        // sürüm geçmişi
        var app = SozInfo.Version;
        if (fromVersion >= 1)
            c.Exec("INSERT OR IGNORE INTO schema_meta(version, applied_at, app_version, note) VALUES(@v,@t,@a,'devralındı')", tx, ("@v", fromVersion), ("@t", now), ("@a", app));
        c.Exec("INSERT OR IGNORE INTO schema_meta(version, applied_at, app_version, backup_file) VALUES(11,@t,@a,@b)", tx, ("@t", now), ("@a", app), ("@b", backup is null ? null : Path.GetFileName(backup.Path)));

        // göç ve yedek olayları (gerçekten yazılır)
        if (backup is not null)
            Events.Append(c, "backup.taken", null, null, null, null, "soz", new JsonObject { ["file"] = Path.GetFileName(backup.Path), ["bytes"] = backup.Bytes, ["from_version"] = fromVersion, ["quick_check"] = "ok" }.ToJsonString(), tx);
        Events.Append(c, "migration.applied", null, null, null, null, "soz",
            new JsonObject { ["from"] = fromVersion, ["to"] = 11, ["backup_file"] = backup is null ? null : Path.GetFileName(backup.Path), ["legacy_events"] = copied, ["noise_events"] = noiseSkipped, ["ms"] = sw.ElapsedMilliseconds, ["app_version"] = app }.ToJsonString(), tx);
        MigrationFault?.Invoke("v11.bitis");
    }

    /// <summary>
    /// v12 (geri alma): koşuda önce/sonra görüntü kimlikleri ve koruma düzeyi (tam | sinirli | yok, nedeni protection_note), undo tablosu, geri alma olay türleri.
    /// Anlık görüntülerin kendisi ayrı dosyada durur (&lt;veri&gt;\geri\geri.db, türetilmiş ve atılabilir); burada yalnız uid ile yumuşak bağ vardır.
    /// Yalnız eklemelidir, tekrar çalıştırılabilir.
    /// </summary>
    private void MigrateV12(SqliteConnection c, SqliteTransaction tx, int fromVersion, BackupInfo? backup, System.Diagnostics.Stopwatch sw)
    {
        var now = Time.NowIso();
        c.Exec(SchemaV12, tx);
        foreach (var (col, type) in new[] { ("before_snapshot_uid", "TEXT NULL"), ("after_snapshot_uid", "TEXT NULL"), ("protection", "TEXT NULL"), ("protection_note", "TEXT NULL") })
            AddColumnIfMissing(c, tx, "runs", col, type);
        MigrationFault?.Invoke("v12.sema");
        var app = SozInfo.Version;
        c.Exec("INSERT OR IGNORE INTO schema_meta(version, applied_at, app_version, backup_file) VALUES(12,@t,@a,@b)", tx, ("@t", now), ("@a", app), ("@b", fromVersion >= 11 && backup is not null ? Path.GetFileName(backup.Path) : null));
        // Gerçek bir v11 → v12 yükseltmesinde olay yazılır. Eski veri tek geçişte v10'dan geliyorsa v11 adımının olayı ("migration.applied") zaten yazıldı; v12 adımı yalnız tablo ekler.
        if (fromVersion >= 11 && backup is not null)
            Events.Append(c, "backup.taken", null, null, null, null, "soz", new JsonObject { ["file"] = Path.GetFileName(backup.Path), ["bytes"] = backup.Bytes, ["from_version"] = fromVersion, ["quick_check"] = "ok" }.ToJsonString(), tx);
        if (fromVersion >= 11)
            Events.Append(c, "migration.applied", null, null, null, null, "soz",
                new JsonObject { ["from"] = 11, ["to"] = 12, ["backup_file"] = backup is null ? null : Path.GetFileName(backup.Path), ["ms"] = sw.ElapsedMilliseconds, ["app_version"] = app }.ToJsonString(), tx);
        MigrationFault?.Invoke("v12.bitis");
    }

    /// <summary>
    /// v13 (Dalga 2 birleştirmesi): keşif tabloları (DiscoveryDiscoverySchema) ve bilinen bütün olay türlerinin kaydı (EventKindCatalog).
    /// v12 (geri alma) yayınlanmış sayılır ve değişmez; yeni tablolar ayrı bir sürümle gelir. Yalnız eklemelidir, tekrar çalıştırılabilir.
    /// Sonraki şeritlerin tabloları için hazır yer: <see cref="ApplyLaneSchemas"/>.
    /// </summary>
    private void MigrateV13(SqliteConnection c, SqliteTransaction tx, int fromVersion, BackupInfo? backup, System.Diagnostics.Stopwatch sw)
    {
        var now = Time.NowIso();
        ApplyLaneSchemas(c, tx);
        EventKindCatalog.Ensure(c, tx);
        MigrationFault?.Invoke("v13.sema");
        var app = SozInfo.Version;
        c.Exec("INSERT OR IGNORE INTO schema_meta(version, applied_at, app_version, backup_file) VALUES(13,@t,@a,@b)", tx, ("@t", now), ("@a", app), ("@b", fromVersion >= 12 && backup is not null ? Path.GetFileName(backup.Path) : null));
        // Gerçek bir v12 → v13 yükseltmesinde olay yazılır; daha eski veri tek geçişte geliyorsa ilk göçün olayı zaten yazıldı.
        if (fromVersion >= 12 && backup is not null)
            Events.Append(c, "backup.taken", null, null, null, null, "soz", new JsonObject { ["file"] = Path.GetFileName(backup.Path), ["bytes"] = backup.Bytes, ["from_version"] = fromVersion, ["quick_check"] = "ok" }.ToJsonString(), tx);
        if (fromVersion >= 12)
            Events.Append(c, "migration.applied", null, null, null, null, "soz",
                new JsonObject { ["from"] = 12, ["to"] = 13, ["backup_file"] = backup is null ? null : Path.GetFileName(backup.Path), ["ms"] = sw.ElapsedMilliseconds, ["app_version"] = app }.ToJsonString(), tx);
        MigrationFault?.Invoke("v13.bitis");
    }

    /// <summary>
    /// v14 (k1, ilk gün boşlukları): jobs.write_check_first = iş "önce küçük bir deneme yaz" isteğiyle başlatıldı mı (0/1). Yalnız bir sütun, mevcut veri korunur
    /// (eski işlerde 0). v13 yayınlanmış sayılır ve değişmez. Yalnız eklemelidir, tekrar çalıştırılabilir.
    /// </summary>
    private void MigrateV14(SqliteConnection c, SqliteTransaction tx, int fromVersion, BackupInfo? backup, System.Diagnostics.Stopwatch sw)
    {
        var now = Time.NowIso();
        AddColumnIfMissing(c, tx, "jobs", "write_check_first", "INTEGER NOT NULL DEFAULT 0");
        MigrationFault?.Invoke("v14.sema");
        var app = SozInfo.Version;
        c.Exec("INSERT OR IGNORE INTO schema_meta(version, applied_at, app_version, backup_file) VALUES(14,@t,@a,@b)", tx, ("@t", now), ("@a", app), ("@b", fromVersion >= 13 && backup is not null ? Path.GetFileName(backup.Path) : null));
        // Gerçek bir v13 → v14 yükseltmesinde olay yazılır; daha eski veri tek geçişte geliyorsa ilk göçün olayı zaten yazıldı.
        if (fromVersion >= 13 && backup is not null)
            Events.Append(c, "backup.taken", null, null, null, null, "soz", new JsonObject { ["file"] = Path.GetFileName(backup.Path), ["bytes"] = backup.Bytes, ["from_version"] = fromVersion, ["quick_check"] = "ok" }.ToJsonString(), tx);
        if (fromVersion >= 13)
            Events.Append(c, "migration.applied", null, null, null, null, "soz",
                new JsonObject { ["from"] = 13, ["to"] = 14, ["backup_file"] = backup is null ? null : Path.GetFileName(backup.Path), ["ms"] = sw.ElapsedMilliseconds, ["app_version"] = app }.ToJsonString(), tx);
        MigrationFault?.Invoke("v14.bitis");
    }

    /// <summary>
    /// Şeritlerin tabloları (her şerit kendi klasöründe <c>Apply(SqliteConnection, SqliteTransaction)</c> yazar; şema yalnız v13 göçünde kurulur).
    /// Çağrılar tekrar çalıştırılabilir (IF NOT EXISTS) olmalıdır.
    /// </summary>
    private void MigrateV15(SqliteConnection c, SqliteTransaction tx, int fromVersion, BackupInfo? backup, System.Diagnostics.Stopwatch sw)
    {
        Soz.Core.Profile.ProfileSchema.Apply(c, tx);
        MigrationFault?.Invoke("v15.sema");
        c.Exec("INSERT OR IGNORE INTO schema_meta(version,applied_at,app_version,backup_file) VALUES(15,@t,@a,@b)", tx,
            ("@t", Time.NowIso()), ("@a", SozInfo.Version), ("@b", backup is null ? null : Path.GetFileName(backup.Path)));
        if (fromVersion >= 14)
            Events.Append(c, "migration.applied", null, null, null, null, "soz",
                new JsonObject { ["from"] = 14, ["to"] = 15, ["ms"] = sw.ElapsedMilliseconds }.ToJsonString(), tx);
        MigrationFault?.Invoke("v15.bitis");
    }

    private static void ApplyLaneSchemas(SqliteConnection c, SqliteTransaction tx)
    {
        Soz.Core.Discovery.DiscoverySchema.Apply(c, tx);
    }

    /// <summary>v12 tabloları: geri alma kayıtları (soz.db). Anlık görüntü manifestleri ayrı geri.db dosyasındadır.</summary>
    public const string SchemaV12 = """
        CREATE TABLE IF NOT EXISTS undo(
          id INTEGER PRIMARY KEY, uid TEXT NOT NULL UNIQUE, job_id INTEGER NOT NULL, run_id INTEGER NULL,
          target_snapshot_uid TEXT NOT NULL, pre_snapshot_uid TEXT NULL, post_snapshot_uid TEXT NULL,
          mode TEXT NOT NULL, status TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT NULL,
          files_restored INTEGER NULL, files_removed INTEGER NULL, files_skipped INTEGER NULL, conflicts_json TEXT NULL, error TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_undo_job ON undo(job_id, id);
        """;

    /// <summary>uid verilen sekiz tablo ve eski satırlar için uid'nin zamanını belirleyen alan. utterances, candidates, clusters, run_lines, proof_results ve usage_daily kimliksiz kalır (türetilmiş ya da üst varlığa bağlı).</summary>
    public static readonly (string Table, string TimeColumn)[] UidTables =
    {
        ("projects", "created_at"), ("jobs", "created_at"), ("runs", "started"), ("proof_runs", "started_at"),
        ("checks", "created_at"), ("guard_rules", "created_at"), ("decisions", "created_at"), ("run_approvals", "created_at"),
    };

    /// <summary>Onarım kontrolü (bkz. ReadHeader): tek sorguda dokuz kısmi dizin. UidTables'tan sonra tanımlanmalı (statik başlatma sırası).</summary>
    private static readonly string NeedsRepairSql = "SELECT " + string.Join(" OR ", new[] { "EXISTS(SELECT 1 FROM events WHERE log_id IS NULL)" }
        .Concat(UidTables.Select(t => $"EXISTS(SELECT 1 FROM {t.Table} WHERE uid IS NULL)")));

    private static int BackfillUids(SqliteConnection c, SqliteTransaction tx, string table, string timeColumn)
    {
        var rows = c.Query($"SELECT id, {timeColumn} FROM {table} WHERE uid IS NULL ORDER BY id", r => (id: r.GetInt64(0), t: r.Str(1)), tx);
        if (rows.Count == 0) return 0;
        using var cmd = c.Cmd($"UPDATE {table} SET uid=@u WHERE id=@i AND uid IS NULL", tx, ("@u", ""), ("@i", 0L));
        foreach (var (id, t) in rows)
        {
            cmd.Parameters["@u"].Value = Uid.At(t);
            cmd.Parameters["@i"].Value = id;
            cmd.ExecuteNonQuery();
        }
        return rows.Count;
    }

    /// <summary>v11 tabloları. Hepsi yalnız burada kurulur (GuardStore ve UsageIngest eski DDL'leri ayrıdır, bkz. teslim notu).</summary>
    public const string SchemaV11 = """
        CREATE TABLE IF NOT EXISTS schema_meta(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL, app_version TEXT NOT NULL, backup_file TEXT NULL, note TEXT NULL);
        CREATE TABLE IF NOT EXISTS device(id INTEGER PRIMARY KEY CHECK(id=1), device_uid TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS project_path(project_uid TEXT NOT NULL, path TEXT NOT NULL, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, is_current INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(project_uid, path)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS event_log(
          seq INTEGER PRIMARY KEY AUTOINCREMENT,
          id TEXT NOT NULL UNIQUE,
          device_uid TEXT NOT NULL,
          ts TEXT NOT NULL,
          kind TEXT NOT NULL,
          kind_v INTEGER NOT NULL DEFAULT 1,
          entity TEXT NULL,
          entity_uid TEXT NULL,
          project_uid TEXT NULL,
          job_uid TEXT NULL,
          actor TEXT NOT NULL DEFAULT 'soz',
          payload TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(payload)),
          prev_hash TEXT NULL,
          hash TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_event_entity ON event_log(entity_uid, seq);
        CREATE INDEX IF NOT EXISTS ix_event_job ON event_log(job_uid, seq);
        CREATE INDEX IF NOT EXISTS ix_event_kind ON event_log(kind, seq);
        CREATE TRIGGER IF NOT EXISTS trg_event_no_update BEFORE UPDATE ON event_log BEGIN SELECT RAISE(ABORT, 'event_log ekle-yalnizdir'); END;
        CREATE TRIGGER IF NOT EXISTS trg_event_no_delete BEFORE DELETE ON event_log BEGIN SELECT RAISE(ABORT, 'event_log ekle-yalnizdir'); END;
        CREATE TABLE IF NOT EXISTS event_kind(kind TEXT PRIMARY KEY, owner TEXT NOT NULL, schema_v INTEGER NOT NULL DEFAULT 1, since TEXT NOT NULL, description TEXT NULL);
        CREATE TABLE IF NOT EXISTS settings_value(key TEXT NOT NULL, scope TEXT NOT NULL DEFAULT 'global', scope_id TEXT NOT NULL DEFAULT '', value TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'user', ts TEXT NOT NULL, pinned INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(key, scope, scope_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS registry_state(id TEXT PRIMARY KEY, enabled INTEGER NOT NULL DEFAULT 1, approved_sha256 TEXT NULL, last_seen TEXT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS registry_suggestion(id TEXT PRIMARY KEY, kind TEXT NOT NULL, body TEXT NOT NULL CHECK(json_valid(body)), evidence TEXT NULL, created_at TEXT NOT NULL, dismissed_at TEXT NULL) WITHOUT ROWID;
        """;

    /// <summary>Sözleşme bölüm 2'deki temel şema (v1).</summary>
    public const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS projects(id INTEGER PRIMARY KEY, key TEXT UNIQUE NOT NULL, name TEXT, path TEXT, created_at TEXT);
        CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY, tool TEXT NOT NULL, external_id TEXT NOT NULL, project_id INTEGER, file_path TEXT, started_at TEXT, last_at TEXT, UNIQUE(tool, external_id));
        CREATE TABLE IF NOT EXISTS utterances(id INTEGER PRIMARY KEY, session_id INTEGER, project_id INTEGER, ts TEXT, text TEXT, source_line INTEGER, hash TEXT, UNIQUE(session_id, hash, ts));
        CREATE TABLE IF NOT EXISTS candidates(id INTEGER PRIMARY KEY, utterance_id INTEGER, project_id INTEGER, type TEXT, text TEXT, score REAL, cluster_id INTEGER NULL);
        CREATE TABLE IF NOT EXISTS clusters(id INTEGER PRIMARY KEY, project_id INTEGER, canonical TEXT, count INTEGER, session_count INTEGER, first_ts TEXT, last_ts TEXT);
        CREATE TABLE IF NOT EXISTS decisions(id INTEGER PRIMARY KEY, project_id INTEGER, type TEXT, text TEXT, status TEXT, pinned INTEGER DEFAULT 0, origin_candidate_id INTEGER NULL, cluster_id INTEGER NULL, repeat_count INTEGER DEFAULT 1, created_at TEXT, updated_at TEXT);
        CREATE TABLE IF NOT EXISTS file_state(path TEXT PRIMARY KEY, tool TEXT, offset INTEGER, mtime TEXT, size INTEGER);
        CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, ts TEXT, kind TEXT, project_id INTEGER NULL, detail TEXT);
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
        """;

    /// <summary>v3 (Söz 2): iş kartı, kanıt kapısı, bekçi, kuyruk, harcama. Tablo adları ve alanlar FABLE-VIZYON-2 bölüm 3 ile birebir.</summary>
    private static void MigrateV3(SqliteConnection c, SqliteTransaction tx)
    {
        c.Exec(SchemaV3, tx);
        AddColumnIfMissing(c, tx, "sessions", "needs_user_since", "TEXT NULL");
        AddColumnIfMissing(c, tx, "sessions", "needs_user_kind", "TEXT NULL");
        // Sözleşme ekinde yok: proje başına kapı anahtarı (PATCH /api/projects/{id} {gateEnabled}) için.
        AddColumnIfMissing(c, tx, "projects", "gate_enabled", "INTEGER DEFAULT 0");
        // Sözleşme ekinde yok: iddia tablosu ("bitti dedi / kanıt: …") için kanca anındaki ajan işaretleri.
        AddColumnIfMissing(c, tx, "proof_runs", "claims_done", "INTEGER NULL");
        AddColumnIfMissing(c, tx, "proof_runs", "shows_evidence", "INTEGER NULL");
    }

    /// <summary>v4: usage_daily tekilliği. Aynı (gün, araç, model, alt ajan) için birden çok satır varsa toplanıp tek satıra iner, sonra UNIQUE dizin.</summary>
    private static void MigrateV4(SqliteConnection c, SqliteTransaction tx)
    {
        c.Exec("""
            CREATE TABLE IF NOT EXISTS usage_daily(day TEXT, tool TEXT, model TEXT, is_subagent INTEGER, messages INTEGER DEFAULT 0, input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0, cache_read_tokens INTEGER DEFAULT 0, cache_write_tokens INTEGER DEFAULT 0);
            UPDATE usage_daily SET model=COALESCE(model,''), tool=COALESCE(tool,''), is_subagent=COALESCE(is_subagent,0);
            CREATE TEMP TABLE _ud AS SELECT day, tool, model, is_subagent, SUM(messages) m, SUM(input_tokens) i, SUM(output_tokens) o, SUM(cache_read_tokens) cr, SUM(cache_write_tokens) cw
              FROM usage_daily GROUP BY day, tool, model, is_subagent;
            DELETE FROM usage_daily;
            INSERT INTO usage_daily(day, tool, model, is_subagent, messages, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens) SELECT day, tool, model, is_subagent, m, i, o, cr, cw FROM _ud;
            DROP TABLE _ud;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_usage_daily ON usage_daily(day, tool, model, is_subagent);
            """, tx);
    }

    /// <summary>v5: iş verildiği anda uygulanacak ölçüt tanımlarının özeti (jobs.checks_hash). Mevcut veri korunur; eski işlerde null kalır (karşılaştırma yapılmaz).</summary>
    private static void MigrateV5(SqliteConnection c, SqliteTransaction tx)
        => AddColumnIfMissing(c, tx, "jobs", "checks_hash", "TEXT NULL");

    /// <summary>
    /// v6 (terminal yok turu): Söz'ün kendi başlattığı ajan süreçleri (runs), ilerleme satırları (run_lines), bekçi onay istekleri (run_approvals);
    /// projects.added (elle eklenen proje listede görünsün), jobs.agent_state (Hakem: ajanın son sözünün sınıfı). Mevcut veri korunur.
    /// </summary>
    private static void MigrateV6(SqliteConnection c, SqliteTransaction tx)
    {
        c.Exec(SchemaV6, tx);
        AddColumnIfMissing(c, tx, "projects", "added", "INTEGER DEFAULT 0");
        AddColumnIfMissing(c, tx, "jobs", "agent_state", "TEXT NULL");
    }

    /// <summary>
    /// v7 (denetim 2 düzeltmesi): kanıt tabanı (job_base, projects.defs_json), iş alanları (kabul, başlatma hatası, soru, sıra, görüldü),
    /// kanıt nedeni/sayıları (proof_runs, proof_results), onay ayrıntısı. Mevcut veri korunur; eski işlerde taban yoktur (karşılaştırma yapılmaz).
    /// </summary>
    private static void MigrateV7(SqliteConnection c, SqliteTransaction tx)
    {
        c.Exec(SchemaV7, tx);
        foreach (var (col, type) in new[] { ("accepted_by_user", "INTEGER DEFAULT 0"), ("start_error", "TEXT NULL"), ("question", "TEXT NULL"), ("base_note", "TEXT NULL"), ("queued_json", "TEXT NULL"), ("queued_at", "TEXT NULL"), ("seen_at", "TEXT NULL") })
            AddColumnIfMissing(c, tx, "jobs", col, type);
        AddColumnIfMissing(c, tx, "projects", "defs_json", "TEXT NULL");
        AddColumnIfMissing(c, tx, "projects", "defs_at", "TEXT NULL");
        foreach (var (col, type) in new[] { ("reason_code", "TEXT NULL"), ("reason_text", "TEXT NULL"), ("changed_files", "TEXT NULL"), ("count_unknown", "INTEGER DEFAULT 0"), ("added_tests", "TEXT NULL"), ("accepted", "INTEGER DEFAULT 0") })
            AddColumnIfMissing(c, tx, "proof_runs", col, type);
        foreach (var (col, type) in new[] { ("tests_total", "INTEGER NULL"), ("tests_passed", "INTEGER NULL"), ("tests_skipped", "INTEGER NULL"), ("cmd_key", "TEXT NULL") })
            AddColumnIfMissing(c, tx, "proof_results", col, type);
        AddColumnIfMissing(c, tx, "run_approvals", "detail", "TEXT NULL");
        c.Exec("CREATE INDEX IF NOT EXISTS ix_results_cmdkey ON proof_results(cmd_key)", tx);
    }

    /// <summary>v8 (denetim 3): başlangıç ölçümü (jobs.baseline_json), kanıt satırında başlangıç/şimdi karşılaştırma cümlesi. Mevcut veri korunur.</summary>
    private static void MigrateV8(SqliteConnection c, SqliteTransaction tx)
    {
        AddColumnIfMissing(c, tx, "jobs", "baseline_json", "TEXT NULL");
        AddColumnIfMissing(c, tx, "proof_runs", "baseline_text", "TEXT NULL");
    }

    /// <summary>v9 (denetim 4): kanıt satırında anlık görüntü kimliği, dosya farkı ve bilgi notları. Mevcut veri korunur.</summary>
    private static void MigrateV9(SqliteConnection c, SqliteTransaction tx)
    {
        foreach (var (col, type) in new[] { ("snapshot_id", "TEXT NULL"), ("diff_json", "TEXT NULL"), ("notes_json", "TEXT NULL") })
            AddColumnIfMissing(c, tx, "proof_runs", col, type);
    }

    /// <summary>
    /// v10 (çok ajan, bağlayıcılar): işte ve koşuda ajan kimliği (claude | codex; eski kayıtlar claude), koşuda harcama alanları (token sayıları, aracın bildirdiği dolar).
    /// Yalnız sütun eklenir, mevcut veri korunur.
    /// </summary>
    private static void MigrateV10(SqliteConnection c, SqliteTransaction tx)
    {
        AddColumnIfMissing(c, tx, "jobs", "agent", "TEXT DEFAULT 'claude'");
        AddColumnIfMissing(c, tx, "runs", "agent", "TEXT DEFAULT 'claude'");
        foreach (var (col, type) in new[] { ("input_tokens", "INTEGER NULL"), ("cached_tokens", "INTEGER NULL"), ("output_tokens", "INTEGER NULL"), ("reasoning_tokens", "INTEGER NULL"), ("cost_usd", "REAL NULL") })
            AddColumnIfMissing(c, tx, "runs", col, type);
    }

    public const string SchemaV7 = """
        CREATE TABLE IF NOT EXISTS job_base(job_id INTEGER PRIMARY KEY, taken_at TEXT, json TEXT, truncated INTEGER DEFAULT 0);
        """;

    public const string SchemaV6 = """
        CREATE TABLE IF NOT EXISTS runs(id INTEGER PRIMARY KEY, job_id INTEGER, session_id TEXT NULL, pid INTEGER NULL, status TEXT, started TEXT, ended TEXT NULL, exit_code INTEGER NULL, last_line TEXT NULL, project_id INTEGER NULL, kind TEXT DEFAULT 'start', fail_reason TEXT NULL, proc_start TEXT NULL);
        CREATE TABLE IF NOT EXISTS run_lines(id INTEGER PRIMARY KEY, run_id INTEGER, job_id INTEGER, at TEXT, kind TEXT, text TEXT, detail TEXT NULL);
        CREATE TABLE IF NOT EXISTS run_approvals(id INTEGER PRIMARY KEY, run_id INTEGER, job_id INTEGER, guard_event_id INTEGER NULL, tool TEXT, summary TEXT, input_hash TEXT, status TEXT DEFAULT 'pending', created_at TEXT, decided_at TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_runs_job_id ON runs(job_id, id);
        CREATE INDEX IF NOT EXISTS ix_runs_status ON runs(status);
        CREATE INDEX IF NOT EXISTS ix_run_lines_job ON run_lines(job_id, id);
        CREATE INDEX IF NOT EXISTS ix_run_approvals_job ON run_approvals(job_id, input_hash);
        """;

    public static void AddColumnIfMissing(SqliteConnection c, SqliteTransaction? tx, string table, string column, string type)
    {
        var cols = c.Query($"PRAGMA table_info({table})", r => r.GetString(1), tx);
        if (!cols.Contains(column, StringComparer.OrdinalIgnoreCase)) c.Exec($"ALTER TABLE {table} ADD COLUMN {column} {type}", tx);
    }

    /// <summary>v3 tabloları (B ajanının guard_*, usage_daily tabloları dahil; IF NOT EXISTS, aynı DDL).</summary>
    public const string SchemaV3 = """
        CREATE TABLE IF NOT EXISTS jobs(id INTEGER PRIMARY KEY, project_id INTEGER, session_id INTEGER NULL, title TEXT, raw_text TEXT, prompt_text TEXT, prompt_hash TEXT, source TEXT DEFAULT 'yazi', state TEXT DEFAULT 'taslak', night INTEGER DEFAULT 0, restate INTEGER DEFAULT 0, created_at TEXT, given_at TEXT NULL, finished_at TEXT NULL);
        CREATE TABLE IF NOT EXISTS checks(id INTEGER PRIMARY KEY, project_id INTEGER, job_id INTEGER NULL, kind TEXT, label TEXT, spec_json TEXT, timeout_s INTEGER DEFAULT 120, enabled INTEGER DEFAULT 0, origin TEXT DEFAULT 'user', created_at TEXT);
        CREATE TABLE IF NOT EXISTS proof_runs(id INTEGER PRIMARY KEY, project_id INTEGER, job_id INTEGER NULL, session_id INTEGER NULL, trigger TEXT, attempt INTEGER DEFAULT 1, started_at TEXT, finished_at TEXT NULL, verdict TEXT NULL, summary TEXT NULL);
        CREATE TABLE IF NOT EXISTS proof_results(id INTEGER PRIMARY KEY, run_id INTEGER, check_id INTEGER, label TEXT, status TEXT, exit_code INTEGER NULL, output_tail TEXT NULL, artifact_path TEXT NULL, duration_ms INTEGER DEFAULT 0);
        CREATE TABLE IF NOT EXISTS guard_rules(id INTEGER PRIMARY KEY, project_id INTEGER NULL, kind TEXT, label TEXT, spec_json TEXT, action TEXT, enabled INTEGER DEFAULT 1, decision_id INTEGER NULL, origin TEXT DEFAULT 'user', created_at TEXT);
        CREATE TABLE IF NOT EXISTS guard_events(id INTEGER PRIMARY KEY, at TEXT, project_id INTEGER, tool_session_id TEXT, tool TEXT, target_summary TEXT, rule_id INTEGER, action TEXT, reason TEXT);
        CREATE TABLE IF NOT EXISTS queue(id INTEGER PRIMARY KEY, project_id INTEGER, session_id INTEGER NULL, text TEXT, created_at TEXT, delivered_at TEXT NULL, origin TEXT DEFAULT 'panel');
        CREATE TABLE IF NOT EXISTS usage_daily(day TEXT, tool TEXT, model TEXT, is_subagent INTEGER, messages INTEGER DEFAULT 0, input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0, cache_read_tokens INTEGER DEFAULT 0, cache_write_tokens INTEGER DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_jobs_project ON jobs(project_id, state);
        CREATE INDEX IF NOT EXISTS ix_jobs_hash ON jobs(prompt_hash);
        CREATE INDEX IF NOT EXISTS ix_checks_project ON checks(project_id, job_id);
        CREATE INDEX IF NOT EXISTS ix_runs_project ON proof_runs(project_id, started_at);
        CREATE INDEX IF NOT EXISTS ix_runs_job ON proof_runs(job_id);
        CREATE INDEX IF NOT EXISTS ix_results_run ON proof_results(run_id);
        CREATE INDEX IF NOT EXISTS ix_guard_events_at ON guard_events(at);
        CREATE INDEX IF NOT EXISTS ix_queue_pending ON queue(project_id, delivered_at);
        """;

    /// <summary>Ek 10/11/12: oturum durumu, kaynak, üst küme, sıkıştırma sayımı.</summary>
    public const string SchemaV2 = """
        ALTER TABLE sessions ADD COLUMN kind TEXT DEFAULT 'human';
        ALTER TABLE sessions ADD COLUMN title TEXT;
        ALTER TABLE sessions ADD COLUMN last_user_text TEXT;
        ALTER TABLE sessions ADD COLUMN last_user_ts TEXT;
        ALTER TABLE sessions ADD COLUMN last_assistant_text TEXT;
        ALTER TABLE sessions ADD COLUMN last_assistant_ts TEXT;
        ALTER TABLE sessions ADD COLUMN last_event_ts TEXT;
        ALTER TABLE sessions ADD COLUMN last_event_kind TEXT;
        ALTER TABLE sessions ADD COLUMN turn_ended INTEGER DEFAULT 0;
        ALTER TABLE sessions ADD COLUMN parent_external_id TEXT;
        ALTER TABLE utterances ADD COLUMN source TEXT DEFAULT 'transcript';
        ALTER TABLE utterances ADD COLUMN extracted INTEGER DEFAULT 0;
        ALTER TABLE utterances ADD COLUMN folded TEXT;
        ALTER TABLE clusters ADD COLUMN parent_id INTEGER NULL;
        ALTER TABLE clusters ADD COLUMN max_score REAL DEFAULT 0;
        ALTER TABLE file_state ADD COLUMN line_no INTEGER DEFAULT 0;
        CREATE TABLE IF NOT EXISTS compactions(id INTEGER PRIMARY KEY, session_id INTEGER, ts TEXT, UNIQUE(session_id, ts));
        CREATE INDEX IF NOT EXISTS ix_utt_session ON utterances(session_id, ts);
        CREATE INDEX IF NOT EXISTS ix_utt_project ON utterances(project_id, ts);
        CREATE INDEX IF NOT EXISTS ix_utt_extracted ON utterances(extracted);
        CREATE INDEX IF NOT EXISTS ix_cand_cluster ON candidates(cluster_id);
        CREATE INDEX IF NOT EXISTS ix_cand_utt ON candidates(utterance_id);
        CREATE INDEX IF NOT EXISTS ix_clusters_project ON clusters(project_id);
        CREATE INDEX IF NOT EXISTS ix_dec_project ON decisions(project_id, status);
        CREATE INDEX IF NOT EXISTS ix_dec_cluster ON decisions(cluster_id);
        CREATE INDEX IF NOT EXISTS ix_sessions_file ON sessions(file_path);
        CREATE INDEX IF NOT EXISTS ix_sessions_parent ON sessions(parent_external_id);
        CREATE INDEX IF NOT EXISTS ix_events_kind ON events(kind, ts);
        """;
}

/// <summary>Küçük ADO.NET yardımcıları.</summary>
public static class SqliteExt
{
    public static SqliteCommand Cmd(this SqliteConnection c, string sql, SqliteTransaction? tx, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = c.Cmd(sql, tx, ps);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = c.Cmd(sql, tx, ps);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public static long ScalarLong(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
        => Convert.ToInt64(c.Scalar(sql, tx, ps) ?? 0L);

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = c.Cmd(sql, tx, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? LongN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static long Long(this SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    public static double Dbl(this SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);
}

public static class Time
{
    public const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    public static string NowIso() => UtcNow().ToString(IsoFormat, System.Globalization.CultureInfo.InvariantCulture);
    public static string Iso(DateTime utc) => utc.ToUniversalTime().ToString(IsoFormat, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Kayıtlardaki zaman damgasını ISO UTC biçimine çevirir; okunamazsa null.</summary>
    public static string? NormalizeIso(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
            return Iso(d.UtcDateTime);
        return null;
    }

    public static DateTime? Parse(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
            return d.UtcDateTime;
        return null;
    }
}
