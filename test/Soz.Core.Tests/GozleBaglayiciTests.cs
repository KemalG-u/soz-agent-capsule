using System.Text.Json.Nodes;
using Soz.Core.Agents;
using Soz.Core.Data;
using Soz.Core.Readers;
using Soz.Core.Registry;
using Soz.Core.Sessions;
using Xunit;
using static Soz.Core.Tests.SahteAracVeritabani;

namespace Soz.Core.Tests;

/// <summary>
/// Dalga 2 şerit F: gözle bağlayıcıları (Hermes, OpenCode), bağlayıcının kayda bağlanması, durum kararı (kaynak, tazelik, "belirsiz").
/// Hiçbir gerçek ajan verisi açılmaz: veritabanları urun\_gecici\d2f altında kaynak koddaki şemadan üretilir (GERCEK KAYIT DEGIL). Gerçek hermes/opencode/claude/codex çalıştırılmaz.
/// </summary>
public class GozleBaglayiciTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly StateThresholds Th = new();

    private static SessionCard Kart(TestEnv env, string tool, string ext)
    {
        using var c = env.Svc.Db.Open();
        return SessionQueries.Cards(c, 48, true, Th, Now, humanOnly: false).Single(k => k.Tool == tool && k.ExternalId == ext);
    }

    private static long Sayi(TestEnv env, string sql)
    {
        using var c = env.Svc.Db.Open();
        return c.ScalarLong(sql);
    }

    // =========================================================== kayıt defterinden beslenme

    [Fact]
    public void KayitDefteri_DortBaglayici_HatasizYuklenir_AjanListesiYalnizCalistirilanlari()
    {
        var load = RegistryLoader.LoadEmbedded();
        Assert.Empty(load.Rejected);
        Assert.True(RegistryChecker.Check(load).Ok);
        Assert.Equal(new[] { "claude", "codex", "hermes", "opencode", "antigravity" }, AgentRegistry.Specs.Select(s => s.Id).ToArray());
        Assert.Equal(new[] { "claude", "codex", "hermes", "opencode", "antigravity" }, AgentRegistry.Ids.ToArray());
        Assert.Equal("claude", AgentRegistry.Default);
        Assert.Equal("claude", AgentRegistry.HookSessionTool);
        Assert.NotNull(AgentRegistry.Find("hermes"));
        var h = AgentRegistry.Specs.Single(s => s.Id == "hermes");
        Assert.True(h.CanObserve); Assert.True(h.CanRun); Assert.NotNull(h.Capabilities); Assert.True(h.IsActive);
        Assert.Equal("hermes-state", h.Reader);
        foreach (var id in new[] { "claude", "codex", "hermes", "opencode", "antigravity" })
            Assert.Empty(load.Get("connector:" + id)!.Contract!.Missing());
    }

    [Fact]
    public void Kimlik_Ve_Yetenek_KayittanGelir_BilinenVarsayilanlaAyni()
    {
        var claude = AgentRegistry.Get("claude"); var codex = AgentRegistry.Get("codex");
        Assert.Equal(new ClaudeConnector().Capabilities, claude.Capabilities);
        Assert.Equal(new CodexConnector().Capabilities, codex.Capabilities);
        Assert.Equal("Claude Code", claude.DisplayName); Assert.Equal("Codex", codex.DisplayName);
        Assert.Equal("codex", codex.SessionTool);
    }

    [Fact]
    public void SourceLocator_KayittanCozer_Oncelik_ArgumanAyarOrtamVarsayilan()
    {
        using var env = new TestEnv(copySamples: false);
        var klasor = YeniKlasor("kok");
        var eski = Environment.GetEnvironmentVariable("HERMES_HOME");
        try
        {
            Assert.Equal("argüman", SourceLocator.Hermes(klasor).Origin);
            Assert.Equal("ayar", SourceLocator.Hermes(null, klasor).Origin);
            Environment.SetEnvironmentVariable("HERMES_HOME", klasor);
            var r = SourceLocator.Hermes(null);
            Assert.Equal("HERMES_HOME", r.Origin); Assert.Equal(Path.GetFullPath(klasor), r.Root); Assert.True(r.Exists);
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("HERMES_HOME", EnvironmentVariableTarget.User) is null)
                Assert.Equal("%USERPROFILE%\\.hermes", SourceLocator.Hermes(null).Origin);
        }
        finally { Environment.SetEnvironmentVariable("HERMES_HOME", eski); }
        // Claude ve Codex'in alt klasör kuralı kayıttan: üst klasör verilince projects / sessions'a inilir.
        var kok = YeniKlasor("claude-ust"); Directory.CreateDirectory(Path.Combine(kok, "projects"));
        Assert.EndsWith("projects", SourceLocator.Claude(kok, null).Root);
        var kok2 = YeniKlasor("codex-ust"); Directory.CreateDirectory(Path.Combine(kok2, "sessions"));
        Assert.EndsWith("sessions", SourceLocator.Codex(kok2, null).Root);
        Assert.False(SourceLocator.For("yok-boyle-arac", null, null).Exists);
    }

    [Fact]
    public void TaniVeKaynakListesi_YalnizDosyaTabanliOkuyucular_VeritabaniAraclarinaKendiligindenBakmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var fs = SourceLocator.FileSources(env.Svc.Settings());
        Assert.Equal(new[] { "claude", "codex" }, fs.Select(x => x.Root.Tool).ToArray());
        Assert.Equal("rollout-*.jsonl", fs[1].Reader.FilePattern);
        Assert.True(fs[0].Reader.SubagentFolders);
    }

    // =========================================================== Hermes

    private static (string klasor, string db) HermesHazirla(Action<Microsoft.Data.Sqlite.SqliteConnection> doldur, int surum = 33, bool wal = true)
    {
        var k = YeniKlasor("hermes");
        var db = Path.Combine(k, "state.db");
        using (var c = Ac(db, wal)) { HermesKur(c, surum); doldur(c); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return (k, db);
    }

    private static void HermesVeri(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        // h-bekliyor: araç çağırdı, sonra son sözü söyledi, sırayı kişiye verdi
        HermesOturum(c, "h-bekliyor", "2026-11-10T11:38:00Z", cwd: "E:\\sahte\\proje-a", baslik: "Fatura düzeni", sonEtkinlik: "2026-11-10T11:42:00Z", mesaj: 4);
        HermesMesaj(c, "h-bekliyor", "user", "Faturaları klasöre göre ayır", "2026-11-10T11:40:00Z");
        HermesMesaj(c, "h-bekliyor", "assistant", null, "2026-11-10T11:41:00Z", aracCagrisi: true, bitisNedeni: "tool_calls");
        HermesMesaj(c, "h-bekliyor", "tool", "{\"gizli\":\"ARAC-CIKTISI-SIZINTI-ISARETI\"}", "2026-11-10T11:41:30Z");
        HermesMesaj(c, "h-bekliyor", "assistant", "Hazır. Hangisini istersin, tarihe göre mi firmaya göre mi?", "2026-11-10T11:42:00Z", bitisNedeni: "stop");
        // h-calisiyor: 30 sn önce araç çağırdı
        HermesOturum(c, "h-calisiyor", "2026-11-10T11:57:00Z", cwd: "E:\\sahte\\proje-b", sonEtkinlik: "2026-11-10T11:59:30Z", mesaj: 2);
        HermesMesaj(c, "h-calisiyor", "user", "Tabloyu topla", "2026-11-10T11:58:00Z");
        HermesMesaj(c, "h-calisiyor", "assistant", null, "2026-11-10T11:59:30Z", aracCagrisi: true, bitisNedeni: "tool_calls");
        // h-belirsiz: 3 dk önce araç çağrısı sonuçsuz
        HermesOturum(c, "h-belirsiz", "2026-11-10T11:50:00Z", cwd: "E:\\sahte\\proje-c", sonEtkinlik: "2026-11-10T11:57:00Z", mesaj: 2);
        HermesMesaj(c, "h-belirsiz", "user", "Raporu yaz", "2026-11-10T11:56:00Z");
        HermesMesaj(c, "h-belirsiz", "assistant", null, "2026-11-10T11:57:00Z", aracCagrisi: true, bitisNedeni: "tool_calls");
        // h-olu: açık tur ama süreç gitmiş
        HermesOturum(c, "h-olu", "2026-11-10T11:30:00Z", cwd: "E:\\sahte\\proje-d", sonEtkinlik: "2026-11-10T11:58:00Z", mesaj: 2);
        HermesMesaj(c, "h-olu", "user", "Dosyayı temizle", "2026-11-10T11:57:00Z");
        HermesMesaj(c, "h-olu", "assistant", null, "2026-11-10T11:58:00Z", aracCagrisi: true, bitisNedeni: "tool_calls");
        // h-alt: h-bekliyor'un alt işi, şu an çalışıyor
        HermesOturum(c, "h-alt", "2026-11-10T11:59:00Z", ust: "h-bekliyor", cwd: "E:\\sahte\\proje-a", sonEtkinlik: "2026-11-10T11:59:40Z", mesaj: 2);
        HermesMesaj(c, "h-alt", "user", "ALT-ISIN-GOREVI", "2026-11-10T11:59:10Z");
        HermesMesaj(c, "h-alt", "assistant", null, "2026-11-10T11:59:40Z", aracCagrisi: true, bitisNedeni: "tool_calls");
        // sıkıştırma: ana oturum bitti, çocuk aynı konuşmanın devamı
        HermesOturum(c, "h-sik-ana", "2026-11-10T09:00:00Z", bitis: "2026-11-10T10:00:00Z", neden: "compression", cwd: "E:\\sahte\\proje-e", mesaj: 2);
        HermesMesaj(c, "h-sik-ana", "user", "Uzun iş", "2026-11-10T09:01:00Z");
        HermesMesaj(c, "h-sik-ana", "assistant", "Sürüyor.", "2026-11-10T09:59:00Z", bitisNedeni: "stop");
        HermesOturum(c, "h-sik-cocuk", "2026-11-10T10:00:00Z", ust: "h-sik-ana", cwd: "E:\\sahte\\proje-e", sonEtkinlik: "2026-11-10T11:00:00Z", mesaj: 2);
        HermesMesaj(c, "h-sik-cocuk", "user", "Devam et", "2026-11-10T10:30:00Z");
        HermesMesaj(c, "h-sik-cocuk", "assistant", "Devam ediyorum.", "2026-11-10T11:00:00Z", bitisNedeni: "stop");
        // cron: otomasyon
        HermesOturum(c, "h-cron", "2026-11-10T08:00:00Z", bitis: "2026-11-10T08:05:00Z", neden: "completed", kaynak: "cron", cwd: "E:\\sahte\\proje-f", mesaj: 2);
        HermesMesaj(c, "h-cron", "user", "OTOMASYON-GIRDISI", "2026-11-10T08:00:30Z");
        HermesMesaj(c, "h-cron", "assistant", "Bitti.", "2026-11-10T08:04:00Z", bitisNedeni: "stop");
    }

    private static HermesStateReader HermesOkuyucu(params string[] yasayanOturumlar)
    {
        var sahte = new Dictionary<int, DateTime>();
        return new HermesStateReader { ProcessStart = pid => sahte.TryGetValue(pid, out var t) ? t : (pid == 4242 ? new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc) : null) };
    }

    private static void CanlilikYaz(string klasor, params string[] oturumlar)
    {
        Directory.CreateDirectory(Path.Combine(klasor, "runtime"));
        var entries = new JsonArray();
        foreach (var s in oturumlar)
            entries.Add(new JsonObject { ["lease_id"] = "l-" + s, ["session_id"] = s, ["surface"] = "cli", ["pid"] = 4242, ["process_start_time"] = Epoch("2026-11-10T06:00:00Z"), ["started_at"] = 1.0, ["updated_at"] = 2.0 });
        File.WriteAllText(Path.Combine(klasor, "runtime", "active_sessions.json"), new JsonObject { ["entries"] = entries }.ToJsonString());
    }

    [Fact]
    public void Hermes_SaltOkunur_BaytDegismez_YanDosyaOlusmaz_VeDurumlarDogru()
    {
        using var env = new TestEnv(copySamples: false);
        var (k, db) = HermesHazirla(HermesVeri, wal: true);
        CanlilikYaz(k, "h-bekliyor", "h-calisiyor", "h-belirsiz", "h-alt", "h-sik-cocuk");
        Assert.Equal("GERCEK-KAYIT-DEGIL.txt|state.db", Dizin(k)); // kapalı WAL veritabanı: -wal/-shm yok
        var once = Ozet(db); var dizinOnce = Dizin(k); var zamanOnce = File.GetLastWriteTimeUtc(db);

        var rep = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());

        Assert.True(rep.Found); Assert.Null(rep.Note);
        Assert.Equal(8, rep.FilesRead); Assert.Equal(0, rep.FileErrors); Assert.Equal(0, rep.Unrecognized);
        Assert.Equal(once, Ozet(db));                       // dosya özeti aynı
        Assert.Equal(dizinOnce, Dizin(k));                  // -wal / -shm / -journal oluşmadı
        Assert.Equal(zamanOnce, File.GetLastWriteTimeUtc(db));

        Assert.Equal("waiting", Kart(env, "hermes", "h-bekliyor").State);
        Assert.Equal("working", Kart(env, "hermes", "h-calisiyor").State);
        var belirsiz = Kart(env, "hermes", "h-belirsiz");
        Assert.Equal("uncertain", belirsiz.State); Assert.Equal("tool_call_pending", belirsiz.EndClass); Assert.Equal("log", belirsiz.StateSource); Assert.Equal("unknown", belirsiz.StateConfidence);
        Assert.Equal("idle", Kart(env, "hermes", "h-olu").State);   // canlılık dosyasında yok: süreç gitmiş, "çalışıyor" denmez
        Assert.Equal("human", Kart(env, "hermes", "h-bekliyor").Kind);
        Assert.Equal("Fatura düzeni", Kart(env, "hermes", "h-bekliyor").Title);
        Assert.Equal("subagent", Kart(env, "hermes", "h-alt").Kind);
        Assert.Equal("human", Kart(env, "hermes", "h-sik-cocuk").Kind);   // sıkıştırma devamı alt iş değil
        Assert.Equal("automation", Kart(env, "hermes", "h-cron").Kind);
        // alt oturum ana oturumun yerine geçmez: ana kart olduğu gibi bekliyor, alt iş ayrı kartta çalışıyor; insan listesinde yalnız ana görünür
        using (var c = env.Svc.Db.Open())
        {
            var insan = SessionQueries.Cards(c, 48, true, Th, Now, humanOnly: true).Where(x => x.Tool == "hermes").Select(x => x.ExternalId).ToHashSet();
            Assert.Contains("h-bekliyor", insan); Assert.DoesNotContain("h-alt", insan); Assert.DoesNotContain("h-cron", insan);
            Assert.Equal(1, SessionQueries.Cards(c, 48, true, Th, Now, humanOnly: true).Single(x => x.ExternalId == "h-bekliyor").SubCount);
        }
        // insan sözü: yalnız insan oturumlarının kullanıcı mesajları; alt iş, otomasyon girdisi ve araç çıktısı veritabanına GİRMEZ
        Assert.Equal(0, Sayi(env, "SELECT COUNT(*) FROM utterances WHERE text LIKE '%ALT-ISIN-GOREVI%' OR text LIKE '%OTOMASYON-GIRDISI%'"));
        Assert.Equal(0, Sayi(env, "SELECT COUNT(*) FROM sessions WHERE last_assistant_text LIKE '%SIZINTI%' OR last_user_text LIKE '%SIZINTI%'"));
        Assert.Equal(0, Sayi(env, "SELECT COUNT(*) FROM utterances WHERE text LIKE '%SIZINTI%'"));
        Assert.Equal(1, Sayi(env, "SELECT COUNT(*) FROM utterances WHERE text LIKE 'Faturaları klasöre%'"));

        // ikinci okuma: değişen yok, çoğalma yok
        var utt = Sayi(env, "SELECT COUNT(*) FROM utterances"); var ses = Sayi(env, "SELECT COUNT(*) FROM sessions");
        var rep2 = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());
        Assert.Equal(8, rep2.FilesSkippedUnchanged); Assert.Equal(0, rep2.FilesRead);
        Assert.Equal(utt, Sayi(env, "SELECT COUNT(*) FROM utterances")); Assert.Equal(ses, Sayi(env, "SELECT COUNT(*) FROM sessions"));
        Assert.Equal(once, Ozet(db));
    }

    [Fact]
    public void Hermes_CanlilikDosyasiYoksa_Bilinmez_VarsayimYapilmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var (k, _) = HermesHazirla(HermesVeri);
        HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());
        // canlılık bilgisi yok: açık tur "durmuş" diye varsayılmaz; kayıt ne diyorsa o (açık araç çağrısı, 2 dk: belirsiz)
        Assert.Equal("uncertain", Kart(env, "hermes", "h-olu").State);
    }

    [Fact]
    public void Hermes_YazilmaktaOlanWal_OkumaBaytlariBozmaz_VeKilitlenmez()
    {
        using var env = new TestEnv(copySamples: false);
        var k = YeniKlasor("hermes-canli"); var db = Path.Combine(k, "state.db");
        using var yazar = Ac(db, wal: true);
        Exec(yazar, "PRAGMA wal_autocheckpoint=0");
        HermesKur(yazar); HermesVeri(yazar);
        // yazıcı açık işlemde: taahhüt edilmemiş bir satır var
        Exec(yazar, "BEGIN");
        HermesOturum(yazar, "h-yarim", "2026-11-10T11:59:00Z", cwd: "E:\\sahte\\yarim", mesaj: 1);
        Assert.True(File.Exists(db + "-wal") && File.Exists(db + "-shm"));
        var dbOnce = Ozet(db); var walOnce = Ozet(db + "-wal"); var dizinOnce = Dizin(k);

        var rep = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());

        Assert.Equal(0, rep.FileErrors); Assert.Null(rep.Note);
        Assert.Equal(8, rep.FilesRead);                      // taahhüt edilmemiş satır görünmez, kilit beklenmez
        Assert.Equal(dbOnce, Ozet(db)); Assert.Equal(walOnce, Ozet(db + "-wal"));
        Assert.Equal(dizinOnce, Dizin(k));
        Exec(yazar, "ROLLBACK");
    }

    [Fact]
    public void Hermes_KilitliVeritabani_OkuyamadimDer_Cokmez()
    {
        using var env = new TestEnv(copySamples: false);
        var (k, db) = HermesHazirla(HermesVeri, wal: false);
        using var yazar = Ac(db);
        Exec(yazar, "BEGIN EXCLUSIVE");
        HermesOturum(yazar, "h-yeni", "2026-11-10T11:59:00Z", mesaj: 0); // journal oluşur, kilit tutulur
        var rep = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());
        Assert.Equal(1, rep.FileErrors); Assert.Contains("kilitli", rep.Note);
        Assert.Equal(0, Sayi(env, "SELECT COUNT(*) FROM sessions WHERE tool='hermes'"));
        Exec(yazar, "ROLLBACK");
    }

    [Theory]
    [InlineData(34)]   // bilmediğim yeni sürüm
    [InlineData(100)]
    public void Hermes_YeniSurum_TanimadigimDer_HicbirSatirOkunmaz(int surum)
    {
        using var env = new TestEnv(copySamples: false);
        var (k, db) = HermesHazirla(HermesVeri, surum);
        var once = Ozet(db);
        var rep = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());
        Assert.Equal(1, rep.Unrecognized); Assert.Contains("tanımadığım sürüm", rep.Note);
        Assert.Equal(0, rep.FilesRead);
        Assert.Equal(0, Sayi(env, "SELECT COUNT(*) FROM sessions WHERE tool='hermes'"));
        Assert.Equal(once, Ozet(db));
    }

    [Fact]
    public void Hermes_EksikSutun_BozukDosya_VeOlmayanDosya_Cokmez()
    {
        using var env = new TestEnv(copySamples: false);
        // eksik sütun: sessions tablosu last_activity_at olmadan
        var k1 = YeniKlasor("hermes-eksik");
        using (var c = Ac(Path.Combine(k1, "state.db")))
        {
            Exec(c, "CREATE TABLE schema_version(version INTEGER NOT NULL)"); Exec(c, "INSERT INTO schema_version VALUES(33)");
            Exec(c, "CREATE TABLE sessions(id TEXT PRIMARY KEY, source TEXT NOT NULL, started_at REAL NOT NULL)");
            Exec(c, "CREATE TABLE messages(id INTEGER PRIMARY KEY, session_id TEXT, role TEXT)");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var r1 = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k1, new ReadOptions());
        Assert.Equal(1, r1.Unrecognized); Assert.Contains("tanımadığım sürüm", r1.Note);
        // bozuk dosya
        var k2 = YeniKlasor("hermes-bozuk");
        File.WriteAllText(Path.Combine(k2, "state.db"), "bu bir sqlite dosyası değil, yarım yazılmış olabilir");
        var r2 = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), k2, new ReadOptions());
        Assert.Equal(1, r2.FileErrors); Assert.Contains("okunamadı", r2.Note);
        // dosya yok
        var r3 = HermesOkuyucu().Read(new IngestStore(env.Svc.Db), YeniKlasor("hermes-bos"), new ReadOptions());
        Assert.False(r3.Found); Assert.Equal(0, r3.FileErrors);
    }

    [Fact]
    public void Hermes_Canlilik_PidYeniden_Kullanilmis_SurecSayilmaz()
    {
        var k = YeniKlasor("canlilik");
        CanlilikYaz(k, "a");
        var ayniSurec = HermesLiveness.Read(k, pid => new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc));
        Assert.Contains("a", ayniSurec!);
        var baskaSurec = HermesLiveness.Read(k, pid => new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc));   // pid başka bir sürece geçmiş
        Assert.DoesNotContain("a", baskaSurec!);
        var yokSurec = HermesLiveness.Read(k, pid => null);
        Assert.Empty(yokSurec!);
        Assert.Null(HermesLiveness.Read(YeniKlasor("canlilik-yok"), pid => null));
        File.WriteAllText(Path.Combine(k, "runtime", "active_sessions.json"), "{bozuk json");
        Assert.Null(HermesLiveness.Read(k, pid => null));
    }

    // =========================================================== OpenCode

    [Fact]
    public void OpenCode_V2_V1_VeTanimayanDosya_SaltOkunur_DurumlarDogru()
    {
        using var env = new TestEnv(copySamples: false);
        var k = YeniKlasor("opencode");
        var v2 = Path.Combine(k, "opencode.db"); var v1 = Path.Combine(k, "opencode-eski.db"); var yabanci = Path.Combine(k, "opencode-yeni.db");
        using (var c = Ac(v2, wal: true))
        {
            OpenCodeKur(c, 2);
            // oc-mesgul: yürütme başladı (busy), bitmedi
            OpenCode2Oturum(c, "oc-mesgul", "E:\\sahte\\oc-a", "2026-11-10T11:00:00Z", "2026-11-10T11:30:00Z", "Mesgul", mesgul: "2026-11-10T11:59:40Z");
            OpenCode2Kullanici(c, "oc-mesgul", "Testleri çalıştır", "2026-11-10T11:59:30Z");
            // oc-bosta: bitti, son söz asistanın
            OpenCode2Oturum(c, "oc-bosta", "E:\\sahte\\oc-b", "2026-11-10T10:00:00Z", "2026-11-10T11:10:00Z", "Bosta", mesgul: "2026-11-10T11:05:00Z", bosta: "2026-11-10T11:10:00Z");
            OpenCode2Kullanici(c, "oc-bosta", "Özet yaz", "2026-11-10T11:04:00Z");
            OpenCode2AsistanMetni(c, "oc-bosta", "Özet hazır, bakar mısın?", "2026-11-10T11:09:00Z");
            // oc-alt: alt iş
            OpenCode2Oturum(c, "oc-alt", "E:\\sahte\\oc-a", "2026-11-10T11:59:00Z", "2026-11-10T11:59:10Z", "Alt", ust: "oc-mesgul", mesgul: "2026-11-10T11:59:10Z");
            OpenCode2Kullanici(c, "oc-alt", "OC-ALT-GOREVI", "2026-11-10T11:59:05Z");
            // oc-arsiv
            OpenCode2Oturum(c, "oc-arsiv", "E:\\sahte\\oc-c", "2026-11-10T08:00:00Z", "2026-11-10T08:10:00Z", "Arsiv", arsiv: "2026-11-10T08:20:00Z");
        }
        using (var c = Ac(v1))
        {
            OpenCodeKur(c, 1);
            OpenCode1Oturum(c, "oc1-bitti", "E:\\sahte\\oc-d", "2026-11-10T11:00:00Z", "2026-11-10T11:20:00Z", "Eski bitti");
            OpenCode1Mesaj(c, "oc1-bitti", "user", "Eski soru", "2026-11-10T11:10:00Z", false);
            OpenCode1Mesaj(c, "oc1-bitti", "assistant", "Eski cevap", "2026-11-10T11:20:00Z", true);
            OpenCode1Oturum(c, "oc1-surer", "E:\\sahte\\oc-e", "2026-11-10T11:50:00Z", "2026-11-10T11:59:50Z", "Eski sürüyor");
            OpenCode1Mesaj(c, "oc1-surer", "user", "Yap şunu", "2026-11-10T11:55:00Z", false);
            OpenCode1Mesaj(c, "oc1-surer", "assistant", "Yapıyorum", "2026-11-10T11:59:50Z", false);
        }
        using (var c = Ac(yabanci)) { Exec(c, "CREATE TABLE baska_bir_sey(x INTEGER)"); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var ozetler = new[] { v2, v1, yabanci }.Select(Ozet).ToArray(); var dizinOnce = Dizin(k);

        var rep = new OpenCodeDbReader().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());

        Assert.Equal(3, rep.Files); Assert.Equal(6, rep.FilesRead); Assert.Equal(1, rep.Unrecognized); Assert.Contains("opencode-yeni.db", rep.Note); Assert.Contains("tanımadığım sürüm", rep.Note);
        Assert.Equal(ozetler, new[] { v2, v1, yabanci }.Select(Ozet).ToArray());
        Assert.Equal(dizinOnce, Dizin(k));

        Assert.Equal("working", Kart(env, "opencode", "oc-mesgul").State);
        Assert.Equal("waiting", Kart(env, "opencode", "oc-bosta").State);
        Assert.Equal("subagent", Kart(env, "opencode", "oc-alt").Kind);
        Assert.Equal("idle", Kart(env, "opencode", "oc-arsiv").State);
        Assert.Equal("waiting", Kart(env, "opencode", "oc1-bitti").State);
        Assert.Equal("working", Kart(env, "opencode", "oc1-surer").State);
        Assert.Equal("Mesgul", Kart(env, "opencode", "oc-mesgul").Title);
        Assert.Equal("Özet hazır, bakar mısın?", Kart(env, "opencode", "oc-bosta").LastAssistantText);
        Assert.Equal(0, Sayi(env, "SELECT COUNT(*) FROM utterances WHERE text LIKE '%OC-ALT-GOREVI%'"));
        Assert.True(Sayi(env, "SELECT COUNT(*) FROM utterances WHERE text LIKE 'Testleri çalıştır%'") == 1);

        var rep2 = new OpenCodeDbReader().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());
        Assert.Equal(6, rep2.FilesSkippedUnchanged); Assert.Equal(0, rep2.FilesRead);
    }

    [Fact]
    public void OpenCode_BusyIdleOlaylariTimeUpdatedDegistirmez_YineDegisiklikAlgilanir()
    {
        using var env = new TestEnv(copySamples: false);
        var k = YeniKlasor("opencode-busy"); var db = Path.Combine(k, "opencode.db");
        using (var c = Ac(db)) { OpenCodeKur(c, 2); OpenCode2Oturum(c, "oc-x", "E:\\sahte\\x", "2026-11-10T11:00:00Z", "2026-11-10T11:00:00Z", "X"); OpenCode2Kullanici(c, "oc-x", "başla", "2026-11-10T11:00:00Z"); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        new OpenCodeDbReader().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());
        Assert.Equal("stalled", Kart(env, "opencode", "oc-x").State); // yürütme hiç başlamamış, kullanıcı sözünden 60 dk geçmiş
        // yürütme başladı: yalnız time_busy değişir (kaynakta time_updated sabit kalır)
        using (var c = Ac(db)) Exec(c, "UPDATE session_v2 SET time_busy=@b WHERE id='oc-x'", ("@b", EpochMs("2026-11-10T11:59:50Z")));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var rep = new OpenCodeDbReader().Read(new IngestStore(env.Svc.Db), k, new ReadOptions());
        Assert.Equal(1, rep.FilesRead);
        Assert.Equal("working", Kart(env, "opencode", "oc-x").State);
    }

    // =========================================================== Durum kararı (tek yerde)

    private static SessionStateResolver.Input Girdi(string? kind, bool ended, double olayDkOnce, string? source = null, string? hint = null, double? gozlemDkOnce = null, string? user = null, string? asst = null)
        => new(kind, ended, Time.Iso(Now.AddMinutes(-olayDkOnce)), null, user, asst, Time.Iso(Now.AddMinutes(-olayDkOnce)), source, gozlemDkOnce is null ? null : Now.AddMinutes(-gozlemDkOnce.Value), hint);

    [Fact]
    public void Durum_KaynaksizGirdi_EskiDavranisAynen_BelirsizUretilmez()
    {
        // Source verilmezse (eski çağrıcılar): 3 dk önce açık araç çağrısı hâlâ "working" (SessionStateTests ile aynı sözleşme).
        Assert.Equal("working", SessionStateResolver.Resolve(Girdi("tool", false, 3), Now, Th).state);
        Assert.Equal("stalled", SessionStateResolver.Resolve(Girdi("tool", false, 12), Now, Th).state);
    }

    [Fact]
    public void Durum_KayitKaynakliAcikAracCagrisi_90SnSonraBelirsiz_5DkSonraTakildi()
    {
        Assert.Equal("working", SessionStateResolver.Resolve(Girdi("tool", false, 1, StateSources.Log), Now, Th).state);        // 60 sn
        var d = SessionStateResolver.Decide(Girdi("tool", false, 3, StateSources.Log), Now, Th);                                  // 3 dk
        Assert.Equal("uncertain", d.State); Assert.Equal("log", d.Source); Assert.Equal("unknown", d.Confidence); Assert.Equal("tool_call_pending", d.EndClass);
        Assert.Equal("stalled", SessionStateResolver.Resolve(Girdi("tool", false, 6, StateSources.Log), Now, Th).state);          // 6 dk
        // araç çağrısı olmayan açık tur belirsiz sayılmaz
        Assert.Equal("working", SessionStateResolver.Resolve(Girdi("assistant_text", false, 3, StateSources.Log), Now, Th).state);
    }

    [Fact]
    public void Durum_GucluKaynakTazeyseKazanir_BayatlarsaKayitKarar()
    {
        // kanca 60 sn önce "waiting" dedi: tazelik 120 sn, kazanır (kayıt "çalışıyor" gibi görünse de)
        var taze = SessionStateResolver.Decide(Girdi("tool", false, 1, StateSources.Hook, "waiting", 1), Now, Th);
        Assert.Equal(("waiting", "hook", "firm"), (taze.State, taze.Source, taze.Confidence));
        // 3 dk önce: kanca bayat, yerini kayda bırakır (log, belirsiz: kayıt açık araç çağrısında bitiyor)
        var bayat = SessionStateResolver.Decide(Girdi("tool", false, 3, StateSources.Hook, "waiting", 3), Now, Th);
        Assert.Equal(("uncertain", "log"), (bayat.State, bayat.Source));
        // protokol "çalışıyor" 20 sn, "bekliyor" 5 sn tazelik
        Assert.Equal("working", SessionStateResolver.Decide(Girdi("tool", false, 1, StateSources.Protocol, "working", 0.3), Now, Th).State);
        Assert.Equal("log", SessionStateResolver.Decide(Girdi("tool", false, 1, StateSources.Protocol, "waiting", 0.5), Now, Th).Source);   // 30 sn > 5 sn
        // hint yoksa güçlü kaynak sayılmaz
        Assert.Equal("log", SessionStateResolver.Decide(Girdi("tool", false, 1, StateSources.Hook, null, 0.1), Now, Th).Source);
    }

    [Fact]
    public void Durum_KayitSonuSinifi_UcSinif()
    {
        Assert.Equal("tool_call_pending", SessionStateResolver.ClassifyEnd(Girdi("tool", false, 1)));
        Assert.Equal("awaiting_user", SessionStateResolver.ClassifyEnd(Girdi("assistant_text", true, 1)));
        Assert.Equal("truncated", SessionStateResolver.ClassifyEnd(Girdi("assistant_text", false, 1)));
        Assert.Equal("truncated", SessionStateResolver.ClassifyEnd(Girdi("other", false, 1)));
        Assert.Null(SessionStateResolver.ClassifyEnd(Girdi("user", false, 1)));
        Assert.Null(SessionStateResolver.ClassifyEnd(Girdi("other", true, 1)));
    }

    // =========================================================== Yeni araç maliyeti (sahte üçüncü araç)

    /// <summary>"Sahte araç": düz metin kayıt. Bu sınıf, üçüncü aracı eklemenin ÇEKİRDEK DIŞI tek kodudur (1 okuyucu).</summary>
    private sealed class SahteAracOkuyucu : ITranscriptReader
    {
        public string Tool => "sahte-arac";
        public ReadReport Read(IngestStore store, string root, ReadOptions options)
        {
            var rep = new ReadReport { Tool = Tool, Root = root, Found = Directory.Exists(root) };
            var f = Path.Combine(root, "oturumlar.txt");
            if (!File.Exists(f)) return rep;
            foreach (var line in File.ReadAllLines(f))
            {
                var p = line.Split('|'); // kimlik|klasör|metin
                var s = new SessionUpdate { Tool = Tool, ExternalId = p[0], Cwd = p[1], LastAssistantText = p[2], LastAssistantTs = "2026-11-10T11:00:00.000Z" };
                s.Event(EventKinds.AssistantText, "2026-11-10T11:00:00.000Z", true);
                store.Apply(new[] { new FileBatch { Path = f + "#" + p[0], Tool = Tool, Session = s, Size = 1, Mtime = "2026-11-10T11:00:00.000Z" } }, null);
                rep.FilesRead++;
            }
            return rep;
        }
    }

    private const string UcuncuAracKaydi = """
        {"schemaVersion":1,"entries":[{"id":"connector:sahte-arac","kind":"connector","owner":"user","title":"Sahte Araç",
         "body":{"depth":["observe"],"detect":{"commands":["sahte-arac"]},"history":{"reader":"sahte-okuyucu","defaultRoot":"%USERPROFILE%\\.sahte-arac"}}}]}
        """;

    [Fact]
    public void YeniArac_BirKayitDosyasi_ArtiBirOkuyucu_CekirdekteSatirDegismez()
    {
        using var env = new TestEnv(copySamples: false);
        ReaderCatalog.Register(new ReaderDef("sahte-okuyucu", () => new SahteAracOkuyucu()));
        var kaynak = RegistrySource.FromText("sahte-arac.soz.json", "file:sahte-arac.soz.json", UcuncuAracKaydi);
        // 1) onaysız: dosyadan gelen bağlayıcı "öneri" kalır, hiçbir şey okunmaz
        var onaysiz = RegistryLoader.Load(RegistryLoader.EmbeddedSources(), new[] { kaynak });
        Assert.Equal("suggested", onaysiz.Get("connector:sahte-arac")!.State);
        Assert.DoesNotContain(AgentRegistry.SpecsFrom(onaysiz), s => s.Id == "sahte-arac");
        // 2) onaylı özetle etkinleşir
        var onay = new MemoryRegistryApprovals(); onay.Approve("connector:sahte-arac", kaynak.Sha256);
        var load = RegistryLoader.Load(RegistryLoader.EmbeddedSources(), new[] { kaynak }, onay);
        Assert.Empty(load.Rejected);
        var specs = AgentRegistry.SpecsFrom(load);
        Assert.Contains(specs, s => s.Id == "sahte-arac" && s.CanObserve && !s.CanRun);
        // 3) kök kayıttaki kuraldan çözülür ve okunur
        var kok = YeniKlasor("sahte-arac");
        File.WriteAllText(Path.Combine(kok, "oturumlar.txt"), "sa-1|E:\\sahte\\x|Bitti, bakar mısın?\nsa-2|E:\\sahte\\y|Hazır.");
        var sonuc = ObserveRunner.Run(env.Svc.Db, new Dictionary<string, string?> { ["sahte-arac"] = kok }, null, specs);
        var r = Assert.Single(sonuc);
        Assert.Null(r.Problem); Assert.Equal(2, r.Report!.FilesRead);
        Assert.Equal("waiting", Kart(env, "sahte-arac", "sa-1").State);
        Assert.Equal(2, Sayi(env, "SELECT COUNT(*) FROM sessions WHERE tool='sahte-arac'"));
        // ajan listesi (çalıştırılanlar) ve ana kod değişmedi
        Assert.Equal(new[] { "claude", "codex", "hermes", "opencode", "antigravity" }, AgentRegistry.Ids.ToArray());
        // 4) kayıtta olmayan araç adı: sade sorun cümlesi, çökme yok
        var yok = ObserveRunner.Run(env.Svc.Db, new Dictionary<string, string?> { ["yok-boyle-arac"] = null }, null, specs);
        Assert.Contains(yok, x => x.Tool == "yok-boyle-arac" && x.Problem!.Contains("kayıt defterinde yok"));
    }

    [Fact]
    public void ObserveRunner_YalnizAdiVerilenAraciOkur_VarsayilanKokleriKendiligindenAcmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var bos = ObserveRunner.Run(env.Svc.Db, new Dictionary<string, string?>());
        Assert.Empty(bos);
        // Hermes adı verilip kök yoksa: bulunamadı (hata değil)
        var r = ObserveRunner.Run(env.Svc.Db, new Dictionary<string, string?> { ["hermes"] = Path.Combine(YeniKlasor("yok"), "olmayan") });
        Assert.False(Assert.Single(r).Report!.Found);
        // Hermes gerçek okuyucu üzerinden
        var (k, _) = HermesHazirla(HermesVeri);
        var ok = ObserveRunner.Run(env.Svc.Db, new Dictionary<string, string?> { ["hermes"] = k });
        Assert.Equal("hermes-state", ok[0].ReaderId); Assert.Equal(8, ok[0].Report!.FilesRead);
    }
}
