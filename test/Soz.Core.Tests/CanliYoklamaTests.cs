using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Attention;
using Soz.Core.Data;
using Soz.Core.Onboarding;
using Soz.Core.Profile;
using Soz.Core.Security;
using Soz.Server;
using Soz.Server.Endpoints;
using Xunit;
using OnboardingScanContext = Soz.Core.Onboarding.ScanContext;

namespace Soz.Core.Tests;

/// <summary>
/// e7: uyarlanır yoklama (kimse bakmıyorken seyrek, bakarken sık; Claude yokken komut hiç; kancayla kapsanmış oturumda komut seyrek; hatada geri çekilme; kanca olayında anlık güncelleme),
/// odaklama dikişi, kabuk tercih köprüsü, ilk tarama ilerlemesi, tek sürüm kaynağı.
/// </summary>
public sealed class CanliYoklamaTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static long Ms(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- LiveCadence (saf kurallar)

    [Fact]
    public void Cadence_KimseBakmiyorkenSeyrek_BakarkenSik_PencereDolunca_SeyreleLur()
    {
        var c = new LiveCadence(() => T0);
        Assert.Equal(LiveRules.LiveIdleInterval, c.Interval(T0));
        c.Watch(T0);
        Assert.Equal(LiveRules.LiveActiveInterval, c.Interval(T0.AddSeconds(5)));
        Assert.Equal(LiveRules.LiveActiveInterval, c.Interval(T0 + LiveRules.WatchWindow));
        Assert.Equal(LiveRules.LiveIdleInterval, c.Interval(T0 + LiveRules.WatchWindow + TimeSpan.FromSeconds(1)));
        Assert.True(LiveRules.LiveIdleInterval >= TimeSpan.FromSeconds(60)); Assert.True(LiveRules.LiveActiveInterval <= TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Cadence_KomutAraligi_KapsanmisSa120Taban_BasarisizlikdaIkiKatiBesDakikayaKadar_BasariylaSifirlanir()
    {
        var c = new LiveCadence(() => T0);
        c.Watch(T0);
        Assert.Equal(LiveRules.LiveCommandActiveInterval, c.CommandInterval(T0, covered: false));
        Assert.Equal(TimeSpan.FromSeconds(120), c.CommandInterval(T0, covered: true));
        Assert.Equal(LiveRules.LiveCommandIdleInterval, c.CommandInterval(T0 + LiveRules.WatchWindow + TimeSpan.FromSeconds(1), false));
        c.CommandFailed(); Assert.Equal(TimeSpan.FromSeconds(120), c.CommandInterval(T0, false));
        c.CommandFailed(); Assert.Equal(TimeSpan.FromSeconds(240), c.CommandInterval(T0, false));
        for (var i = 0; i < 20; i++) c.CommandFailed();
        Assert.Equal(LiveRules.BackoffMax, c.CommandInterval(T0, false));
        Assert.Equal(LiveRules.BackoffMax, c.CommandInterval(T0, true));
        c.CommandSucceeded(); Assert.Equal(0, c.Failures); Assert.Equal(LiveRules.LiveCommandActiveInterval, c.CommandInterval(T0, false));
    }

    [Fact]
    public void Cadence_KancaOlayi_DurumuHemenKaydeder_ListeyeUymayanOturumdaYoklamayiOneCeker_SinirlidirVe_BilinenOturumdaCekmez()
    {
        var c = new LiveCadence(() => T0);
        c.SetKnown(["a"]);
        Assert.False(c.NoteHook("user-prompt-submit", "a", null, T0));          // bilinen oturum: yoklama öne çekilmez
        Assert.Equal(LiveRules.StateWorking, c.NoteOf("a")!.State); Assert.Equal(DateTime.MinValue, c.WakeAt);
        c.NoteHook("stop", "a", null, T0.AddSeconds(1)); Assert.Equal(LiveRules.StateIdle, c.NoteOf("a")!.State);
        c.NoteHook("notification", "a", "permission_prompt", T0.AddSeconds(2));
        Assert.Equal(LiveRules.StateWaiting, c.NoteOf("a")!.State); Assert.True(c.NoteOf("a")!.Waiting);
        c.NoteHook("notification", "a", "idle_prompt", T0.AddSeconds(3)); Assert.False(c.NoteOf("a")!.Waiting);

        Assert.True(c.NoteHook("session-start", "yeni", null, T0.AddSeconds(20)));   // yeni oturum: öne çek
        Assert.Equal(T0.AddSeconds(20), c.WakeAt);
        Assert.False(c.NoteHook("user-prompt-submit", "yeni", null, T0.AddSeconds(21)));  // aynı oturum 10 dk içinde ikinci kez çekmez
        Assert.False(c.NoteHook("session-start", "baska", null, T0.AddSeconds(25)));      // iki çekme arası en az 10 sn
        Assert.True(c.NoteHook("session-start", "baska", null, T0.AddSeconds(31)));
        Assert.True(c.NoteHook("session-start", "yeni", null, T0.AddSeconds(20) + LiveRules.WakePerSession + TimeSpan.FromSeconds(1)));
        Assert.False(c.NoteHook("bilinmeyen-olay", "z", null, T0)); Assert.Null(c.NoteOf("z"));
        Assert.False(c.NoteHook("stop", " ", null, T0)); Assert.False(c.NoteHook("stop", null, null, T0));
    }

    [Fact]
    public void Cadence_Covered_YalnizHepsiKancayla_HaberVermisVe_BeklemeyenSe()
    {
        var c = new LiveCadence(() => T0);
        Assert.False(c.Covered([]));                       // oturum yok: komutu atlatan bir kapsam yok
        Assert.False(c.Covered(["a"]));                    // kancasız oturum
        c.NoteHook("stop", "a", null, T0);
        Assert.True(c.Covered(["a"])); Assert.False(c.Covered(["a", "b"]));
        c.NoteHook("notification", "a", "permission_prompt", T0.AddSeconds(1));
        Assert.False(c.Covered(["a"]));                    // bekleyen oturum komutla da doğrulanır
    }

    [Fact]
    public void Cadence_KancaNotlariSinirlidir()
    {
        var c = new LiveCadence(() => T0);
        c.SetKnown(Enumerable.Range(0, LiveRules.HookNoteLimit + 50).Select(i => "s" + i));
        for (var i = 0; i < LiveRules.HookNoteLimit + 50; i++) c.NoteHook("stop", "s" + i, null, T0.AddSeconds(i));
        Assert.Null(c.NoteOf("s0")); Assert.NotNull(c.NoteOf("s" + (LiveRules.HookNoteLimit + 49)));
    }

    // ---------------------------------------------------------------- ProfileService: komut ve yoklama sayıları

    private sealed class FakeAgents(Func<string?> json, string? failure = null) : IClaudeAgentsCommand
    {
        public int Calls;
        public string? Run(out string? fail) { Interlocked.Increment(ref Calls); fail = failure; return json(); }
    }

    private sealed class Rig
    {
        public required ProfileService Svc; public required OnboardingScanContext Ctx; public required FakeAgents Cmd; public required SozDb Db;
        public DateTime Clock = T0; public required Action<string> SetJson; public required string ClaudeHome;
        public JsonObject Bg(double sec) { Clock = T0.AddSeconds(sec); return Svc.View(null, () => Ctx, background: true); }
        public JsonObject Fg(double sec) { Clock = T0.AddSeconds(sec); return Svc.View(null, () => Ctx, background: false); }
        public string? State(JsonObject view) => view["live"]!.AsArray().SelectMany(g => g!["sessions"]!.AsArray()).FirstOrDefault(s => (string?)s!["id"] == "sess-1")?["state"]?.ToString();
    }

    private static Rig MakeRig(Func<IReadOnlyList<ClaudeRegistryEntry>, bool>? present = null, string? failure = null, bool registryFile = false)
    {
        var root = ProfileMetadataTests.Fixture();
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        var dir = Path.Combine(roots.ClaudeHome, "projects", "E--P"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sess-1.jsonl");
        File.WriteAllText(file, """{"type":"user","sessionId":"sess-1","cwd":"E:\\P","timestamp":"2026-11-12T11:50:00Z"}""" + "\n");
        File.SetLastWriteTimeUtc(file, T0.AddMinutes(-5));
        var started = T0.AddHours(-1);
        if (registryFile)
        {
            var sdir = Path.Combine(roots.ClaudeHome, "sessions"); Directory.CreateDirectory(sdir);
            File.WriteAllText(Path.Combine(sdir, "77.json"), $$"""{"pid":77,"sessionId":"sess-1","cwd":"E:\\P","startedAt":{{Ms(started)}},"status":"busy"}""");
        }
        var json = "";
        void SetJson(string status) => json = $$"""[{"pid":77,"cwd":"C:\\WINDOWS\\system32","kind":"interactive","startedAt":{{Ms(started.AddSeconds(4))}},"sessionId":"sess-1","name":"p-1","status":"{{status}}"}]""";
        SetJson("busy");
        var cmd = new FakeAgents(() => failure is null ? json : null, failure);
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var rig = new Rig { Svc = null!, Ctx = null!, Cmd = cmd, Db = db, SetJson = SetJson, ClaudeHome = roots.ClaudeHome };
        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(),
            Processes = new ProfileP2ExtraTests.FakeProcesses(new ProcessObservation("claude", 77, started, CurrentDirectory: @"E:\P")),
            FileSystem = fs, ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir),
            AgentsCommand = cmd, Clock = () => rig.Clock, RefreshSyntheticRoots = true, ClaudePresent = present,
        };
        rig.Svc = svc;
        rig.Ctx = new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => T0 };
        return rig;
    }

    [Fact]
    public void Service_KimseBakmiyorken120SnKomut_ArkaPlanIstegiBakildigiAnlamina_Gelmez_BakarkenVeBaktiktanSonra60Sn()
    {
        var rig = MakeRig();
        rig.Svc.Scan(rig.Ctx);                       // tam tarama komutu bir kez çalıştırır
        Assert.Equal(1, rig.Cmd.Calls);
        rig.Bg(1); Assert.Equal(2, rig.Cmd.Calls);   // ilk hesap
        rig.Bg(30); rig.Bg(55); rig.Bg(60);          // 60 sn dolmadı: önbellek
        Assert.Equal(2, rig.Cmd.Calls);
        rig.Bg(62); Assert.Equal(2, rig.Cmd.Calls);  // komut kimse bakmıyorken 120 sn: hâlâ önbellek
        rig.Bg(122); Assert.Equal(3, rig.Cmd.Calls); // 120 sn doldu
        rig.Bg(150); Assert.Equal(3, rig.Cmd.Calls);

        rig.Fg(160); Assert.Equal(3, rig.Cmd.Calls); // biri baktı: komut aralığı 60 sn (en son komut 122'de, yaş 38 sn)
        rig.Fg(190); Assert.Equal(4, rig.Cmd.Calls); // yaş 68 sn: yenilendi
        rig.Fg(200); Assert.Equal(4, rig.Cmd.Calls); // 10 sn: aynı
        rig.Fg(251); Assert.Equal(5, rig.Cmd.Calls); // 61 sn: yenilendi
        rig.Bg(255); Assert.Equal(5, rig.Cmd.Calls); // arka plan isteği de önbelleği kullanır (bakan var, yaş 4 sn)
        rig.Bg(251 + 41 + 30); Assert.Equal(5, rig.Cmd.Calls); // pencere kapandı: aralık yeniden 120 sn, yaş 71 → önbellek
        rig.Bg(251 + 135); Assert.Equal(6, rig.Cmd.Calls); // komut yaşı 135 ≥ 120 → hesaplanır
        rig.Bg(251 + 135 + 60); Assert.Equal(6, rig.Cmd.Calls); // ve yine seyrek
    }

    [Fact]
    public void Service_BakanYokkenArkaPlanSeyrekligiKorunur_PencereKapaninca120SneDoner()
    {
        var rig = MakeRig();
        rig.Svc.Scan(rig.Ctx);
        rig.Fg(1); Assert.Equal(2, rig.Cmd.Calls);
        rig.Bg(10); Assert.Equal(2, rig.Cmd.Calls);
        // son bakıştan 41 sn sonra komut aralığı 120 sn: yaş 45 ve 61 sn → hâlâ önbellek
        rig.Bg(46); Assert.Equal(2, rig.Cmd.Calls);
        rig.Bg(62); Assert.Equal(2, rig.Cmd.Calls);
        rig.Bg(122); Assert.Equal(3, rig.Cmd.Calls);
    }

    [Fact]
    public void Service_ClaudeSureciYoksaKomutHicCalismaz()
    {
        var rig = MakeRig(present: _ => false);
        rig.Svc.Scan(rig.Ctx);
        var afterScan = rig.Cmd.Calls;               // tam tarama kuralları atlar
        foreach (var s in new double[] { 1, 20, 70, 200, 1000 }) { var v = rig.Bg(s); Assert.Equal("agents-komutu", (string?)v["liveSource"]); }
        rig.Fg(1500); rig.Fg(1520);
        Assert.Equal(afterScan, rig.Cmd.Calls);
        Assert.Equal(0, rig.Svc.Cadence.Failures);
    }

    [Fact]
    public void Service_HepsiKancaylaKapsanmisSa_KomutSeyrekKalir_KancaDurumuHemenYansir_BeklemeyiKomutDogrular()
    {
        var rig = MakeRig(present: _ => true, registryFile: true);
        rig.Svc.Scan(rig.Ctx); Assert.Equal(1, rig.Cmd.Calls);
        Assert.Equal("calisiyor", rig.State(rig.Fg(1))); Assert.Equal(2, rig.Cmd.Calls);
        // oturum kanca haberi verdi (bilinen oturum: yoklama öne çekilmez), bekleyen değil
        Assert.False(rig.Svc.Cadence.NoteHook("stop", "sess-1", null, T0.AddSeconds(2)));
        // 16 sn sonra canlı bölüm yeniden hesaplanır ama komut çalışmaz (kapsanmışsa 120 sn taban); durum kancadan gelir
        var v = rig.Fg(17);
        Assert.Equal(2, rig.Cmd.Calls); Assert.Equal("bosta", rig.State(v));
        rig.Fg(40); rig.Fg(55); Assert.Equal(2, rig.Cmd.Calls);
        rig.Fg(70); Assert.Equal(2, rig.Cmd.Calls);
        rig.Fg(125); Assert.Equal(3, rig.Cmd.Calls);  // 120 sn sonra komut doğrulaması
        // kanca "seni bekliyor" dedi: komutun doğrulaması gerekir (kapsam biter), aralık bakarken 60 sn
        rig.Svc.Cadence.NoteHook("notification", "sess-1", "permission_prompt", T0.AddSeconds(130));
        rig.Fg(150); Assert.Equal(3, rig.Cmd.Calls);
        rig.Fg(190); Assert.Equal(4, rig.Cmd.Calls);
    }

    [Fact]
    public void Service_KomutBasarisizsaGeriCekilir_UstUsteBinmez()
    {
        var rig = MakeRig(failure: "komut-yok");
        rig.Svc.Scan(rig.Ctx);
        var baseCalls = rig.Cmd.Calls;
        // 10 dakika boyunca her saniye bakan biri: çağrı aralıkları her başarısızlıkta uzar, 5 dakikayı geçmez
        var times = new List<int>(); var last = rig.Cmd.Calls;
        for (var t = 1; t <= 600; t++)
        {
            rig.Fg(t);
            if (rig.Cmd.Calls != last) { Assert.Equal(last + 1, rig.Cmd.Calls); times.Add(t); last = rig.Cmd.Calls; }
        }
        Assert.InRange(times.Count, 3, 6);                                   // sabit 15 sn olsaydı 40 çağrı olurdu
        for (var i = 2; i < times.Count; i++) Assert.True(times[i] - times[i - 1] > times[i - 1] - times[i - 2], "aralıklar uzamalı: " + string.Join(",", times));
        Assert.True(rig.Svc.Cadence.Failures >= 3);
        Assert.True(rig.Svc.Cadence.CommandInterval(T0.AddSeconds(600), false) <= LiveRules.BackoffMax);

        // eşzamanlı istekler tek hesap yapar
        var rig2 = MakeRig();
        rig2.Svc.Scan(rig2.Ctx); rig2.Clock = T0.AddSeconds(5);
        var before = rig2.Cmd.Calls;
        Parallel.For(0, 12, _ => rig2.Svc.View(null, () => rig2.Ctx, background: false));
        Assert.Equal(before + 1, rig2.Cmd.Calls);
    }

    [Fact]
    public void Service_YeniOturumKancasi_YoklamayiOneCeker_ArkaPlandaDa()
    {
        var rig = MakeRig();
        rig.Svc.Scan(rig.Ctx);
        rig.Bg(1); Assert.Equal(2, rig.Cmd.Calls);
        rig.Bg(20); Assert.Equal(2, rig.Cmd.Calls);
        Assert.True(rig.Svc.Cadence.NoteHook("session-start", "tamamen-yeni", null, T0.AddSeconds(21)));
        rig.Bg(22); Assert.Equal(3, rig.Cmd.Calls);   // 60 sn beklemeden
        rig.Bg(30); Assert.Equal(3, rig.Cmd.Calls);   // sonra yine seyrek
    }

    // ---------------------------------------------------------------- Sessions: çift kayıt (D6-25), hata nedeni (D6-17), tarama ilerlemesi (D6-18)

    [Fact]
    public void Scan_IlerlemeSayaci_BittigindeDolu_SonrasindaCalismiyor()
    {
        var rig = MakeRig();
        Assert.False(rig.Svc.Progress().Running);
        rig.Svc.Scan(rig.Ctx);
        var p = rig.Svc.Progress();
        Assert.False(p.Running); Assert.Equal("bitti", p.Phase); Assert.Equal(100, p.Percent); Assert.True(p.Total >= 1); Assert.True(p.Done >= p.Total);
    }

    [Fact]
    public void FailureReason_BozukIleOkunamadiAyrilir()
    {
        Assert.Equal("bozuk", ProfileSources.FailureReason(new System.Text.Json.JsonException("x")));
        Assert.Equal("okunamadi", ProfileSources.FailureReason(new IOException("x")));
        Assert.Equal("kilitli", ProfileSources.FailureReason(new UnauthorizedAccessException()));
    }

    [Fact]
    public void SozInfo_Surumu_TekKaynaktan_0_3_0()
    {
        Assert.Equal("0.3.6", SozInfo.Version);
    }

    // ---------------------------------------------------------------- BackgroundExternalSessions (gerçek saatle, kısa aralıklarla)

    private sealed class CountingSource(Func<int, IReadOnlyList<ExternalSession>> next) : IExternalSessionSource
    {
        public int Calls;
        public IReadOnlyList<ExternalSession> Snapshot() { var n = Interlocked.Increment(ref Calls); return next(n); }
    }

    private static void WaitFor(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(10);
    }

    [Fact]
    public void Background_UyarlanirAralik_Wake_VeHatadaGeriCekilme()
    {
        var interval = TimeSpan.FromHours(1);
        var wake = DateTime.MinValue;
        var src = new CountingSource(_ => Array.Empty<ExternalSession>());
        var bg = new BackgroundExternalSessions(src, interval: () => interval, wakeAt: () => wake);
        bg.Snapshot(); WaitFor(() => bg.Refreshes >= 1);
        Assert.Equal(1, src.Calls);
        bg.Snapshot(); bg.Snapshot(); Thread.Sleep(50); Assert.Equal(1, src.Calls);       // aralık dolmadı
        Thread.Sleep(10); wake = DateTime.UtcNow;                                          // yoklamayı öne çek
        bg.Snapshot(); WaitFor(() => bg.Refreshes >= 2);
        Assert.Equal(2, src.Calls);
        bg.Snapshot(); Thread.Sleep(50); Assert.Equal(2, src.Calls);                       // aynı uyandırma ikinci kez tetiklemez

        var failing = new CountingSource(_ => throw new IOException("sahte"));
        var bg2 = new BackgroundExternalSessions(failing, interval: () => TimeSpan.FromMilliseconds(250));
        bg2.Snapshot(); WaitFor(() => bg2.Failures >= 1);
        Assert.Equal(1, bg2.Failures);
        var calls = failing.Calls;
        Thread.Sleep(300); bg2.Snapshot(); Thread.Sleep(60);                               // taban 250 ms geçti ama hata sonrası aralık 500 ms
        Assert.Equal(calls, failing.Calls);
        Thread.Sleep(300); bg2.Snapshot(); WaitFor(() => failing.Calls > calls);
        Assert.Equal(calls + 1, failing.Calls);
    }

    [Fact]
    public async Task Background_UstUsteBinmez()
    {
        var gate = new ManualResetEventSlim(false); var running = 0; var maxRunning = 0;
        var src = new CountingSource(_ =>
        {
            var r = Interlocked.Increment(ref running); maxRunning = Math.Max(maxRunning, r);
            gate.Wait(500); Interlocked.Decrement(ref running); return Array.Empty<ExternalSession>();
        });
        var bg = new BackgroundExternalSessions(src, firstWait: TimeSpan.FromMilliseconds(20), interval: () => TimeSpan.Zero);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => bg.Snapshot())).ToArray();
        await Task.WhenAll(tasks); gate.Set();
        WaitFor(() => bg.Refreshes >= 1);
        Assert.Equal(1, maxRunning);
    }

    // ---------------------------------------------------------------- Odaklama dikişi

    [Fact]
    public void FocusDirect_ProjeYoluVeBasligiGecer_TabSelectedVeHintDoner_BulunamazsaKlasorAcar_MasaustuYoksa501()
    {
        var oldF = SessionActions.Focuser; var oldD = SessionActions.HasDesktop; var oldO = SessionActions.FolderOpener;
        try
        {
            FocusRequest? got = null;
            SessionActions.HasDesktop = () => true;
            SessionActions.Focuser = q => { got = q; return new FocusResult(true, 42, TabSelected: true, Hint: "ipucu"); };
            var ok = SessionActions.FocusDirect(new FocusRequest(null, 7, @"E:\P", "Başlık"), false);
            Assert.True(ok.Ok); Assert.Equal(200, ok.Status);
            Assert.Equal(7, got!.Pid); Assert.Equal(@"E:\P", got.ProjectPath); Assert.Equal("Başlık", got.SessionTitle);
            var json = System.Text.Json.JsonSerializer.Serialize(ok.Data);
            Assert.Contains("\"tabSelected\":true", json); Assert.Contains("\"hint\":\"ipucu\"", json);

            SessionActions.Focuser = q => new FocusResult(false, null);
            var none = SessionActions.FocusDirect(new FocusRequest(null, 7), false);
            Assert.False(none.Ok); Assert.Equal(404, none.Status);
            var folder = Path.GetTempPath(); string? opened = null;
            SessionActions.FolderOpener = p => { opened = p; return true; };
            var viaFolder = SessionActions.FocusDirect(new FocusRequest(null, null, folder), true);
            Assert.True(viaFolder.Ok); Assert.Equal(folder, opened);

            SessionActions.HasDesktop = () => false;
            Assert.Equal(501, SessionActions.FocusDirect(new FocusRequest(null, 7), false).Status);
        }
        finally { SessionActions.Focuser = oldF; SessionActions.HasDesktop = oldD; SessionActions.FolderOpener = oldO; }
    }

    private static int Port() => 53000 + Random.Shared.Next(0, 900);

    [Fact]
    public async Task Sunucu_LiveFocus_OdaklayiciyaYoluVeBasligiGecirir_TabSelectedDoner_ScanProgressVeShellPrefsUclari()
    {
        using var env = new TestEnv(copySamples: false);
        var oldF = SessionActions.Focuser; var oldD = SessionActions.HasDesktop;
        var oldRead = ShellBridge.Read; var oldWrite = ShellBridge.Write;
        try
        {
            FocusRequest? got = null;
            SessionActions.HasDesktop = () => true;
            SessionActions.Focuser = q => { got = q; return new FocusResult(true, 99, TabSelected: true, Hint: "birden çok sekme"); };
            await using var h = await SozServer.StartAsync(env.DataDir, Port());
            using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
            http.DefaultRequestHeaders.Add("X-Soz-Token", h.Token);

            var resp = await http.PostAsJsonAsync("/api/live/focus", new { pid = 31, projectPath = @"E:\Proje", title = "Oturum adı" });
            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(31, got!.Pid); Assert.Equal(@"E:\Proje", got.ProjectPath); Assert.Equal("Oturum adı", got.SessionTitle);
            Assert.True((bool)body["tabSelected"]!); Assert.Equal("birden çok sekme", (string?)body["hint"]);

            SessionActions.Focuser = q => new FocusResult(false, null);
            Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/live/focus", new { pid = 31 })).StatusCode);

            // ilerleme ucu
            var prog = JsonNode.Parse(await http.GetStringAsync("/api/profile/scan-progress"))!;
            foreach (var k in new[] { "running", "phase", "done", "total", "percent" }) Assert.True(prog.AsObject().ContainsKey(k), k);

            // kabuk tercihleri: kabuk yokken
            ShellBridge.Read = null; ShellBridge.Write = null;
            Assert.False((bool)JsonNode.Parse(await http.GetStringAsync("/api/shell/prefs"))!["available"]!);
            Assert.Equal(HttpStatusCode.NotImplemented, (await http.PutAsJsonAsync("/api/shell/prefs", new { notifications = false })).StatusCode);

            // kabuk bağlıyken
            var state = new ShellPrefs(true, true, false);
            ShellBridge.Read = () => state;
            ShellBridge.Write = (a, b, d) => state = new ShellPrefs(a ?? state.StartWithWindows, b ?? state.Notifications, d ?? state.DeleteDataOnUninstall);
            var get = JsonNode.Parse(await http.GetStringAsync("/api/shell/prefs"))!;
            Assert.True((bool)get["available"]!); Assert.True((bool)get["startWithWindows"]!);
            var put = await http.PutAsJsonAsync("/api/shell/prefs", new { startWithWindows = false });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode); Assert.False(state.StartWithWindows); Assert.True(state.Notifications);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/shell/prefs", new { deleteDataOnUninstall = true })).StatusCode); // onaysız silme açılmaz
            Assert.False(state.DeleteDataOnUninstall);
            Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync("/api/shell/prefs", new { deleteDataOnUninstall = true, confirmed = true })).StatusCode);
            Assert.True(state.DeleteDataOnUninstall);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/shell/prefs", new { notifications = "belki" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/shell/prefs", new { })).StatusCode);
            using var noToken = new HttpClient { BaseAddress = new Uri(h.Url) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.PutAsJsonAsync("/api/shell/prefs", new { notifications = false })).StatusCode);
        }
        finally { SessionActions.Focuser = oldF; SessionActions.HasDesktop = oldD; ShellBridge.Read = oldRead; ShellBridge.Write = oldWrite; }
    }
}
