using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Soz.Core.Attention;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Registry;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>"Sıra bende" (d2c): öncelik sırası, dikkat bütçesi, "gördüm", kaçırılanı alma, kayıt defteri ayarları ve sunucu ucu.</summary>
public class AttentionTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Saat her çağrıda 1 ms ilerler (olay sıralaması ve "öğe kimliği durum girişine bağlı" denemeleri için).</summary>
    private static void TickingClock(DateTime? start = null) { var t = start ?? T0; var n = 0; Time.UtcNow = () => t.AddMilliseconds(n++); }

    private sealed class Rig : IDisposable
    {
        public readonly TestEnv Env = new(copySamples: false);
        public readonly WorkService Work;
        public readonly long ProjectId;
        public readonly AttentionService Svc;
        public Rig(bool ticking = true)
        {
            if (ticking) TickingClock();
            Work = new WorkService(Env.Svc.Db);
            ProjectId = Work.EnsureProjectByPath(NodeSample.Create(Env));
            Svc = new AttentionService(Env.Svc.Db);
        }
        public JobDto Job(string text, string state)
        {
            var j = Work.CreateJob(ProjectId, text);
            using var c = Env.Svc.Db.Open();
            if (state == JobStates.AwaitingAnswer) JobStore.SetAwaitingAnswer(c, j.Id, "Hangisini seçeyim?", ProjectId);
            else JobStore.SetState(c, j.Id, state, ProjectId);
            return Work.Job(j.Id);
        }
        public void Set(params (string, JsonNode?)[] kv)
        {
            using var c = Env.Svc.Db.Open();
            var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v;
            SozSettings.Save(c, o);
        }
        public void Dispose() => Env.Dispose();
    }

    // ------------------------------------------------------------ sıra ve içerik
    [Fact]
    public void Bos_liste_duz_cumle_soyler_ve_bos_kalir()
    {
        using var r = new Rig();
        var s = r.Svc.Snapshot();
        Assert.Empty(s.Items);
        Assert.Equal("Şu an seni bekleyen bir şey yok.", s.Sentence);
        Assert.Equal(0, s.Counts.Total);
        Assert.Equal(3, s.Budget.DailyLimit);
    }

    [Fact]
    public void Oncelik_sirasi_soru_kabul_engel_bitti_belirsiz_ve_her_ogede_cumle_ve_eylem_var()
    {
        using var r = new Rig();
        var done = r.Job("Bitti işi", JobStates.Passed);
        var failed = r.Job("Kalan iş", JobStates.Failed);
        var decision = r.Job("Karar işi", JobStates.AwaitingDecision);
        var ask = r.Job("Soru işi", JobStates.AwaitingAnswer);
        var run = r.Job("Çalışan iş", JobStates.Running);
        using (var c = r.Env.Svc.Db.Open()) RunStore.Insert(c, run.Id, r.ProjectId, "start"); // canlı koşu (runs.status=running)
        // ajan bitirdi ama kanıt yok: "belirsiz"
        var unproven = r.Job("Kanıtsız iş", JobStates.Given);
        using (var c = r.Env.Svc.Db.Open()) { var rid = RunStore.Insert(c, unproven.Id, r.ProjectId, "start"); RunStore.SetStatus(c, rid, RunStatus.Done, ended: true); }

        var s = r.Svc.Snapshot();
        Assert.Equal(new[] { "ask", "accept", "blocked", "done", "uncertain" }, s.Items.Select(i => i.Kind).ToArray());
        // d2m: tek öncelik tablosu (AttentionKinds.Table): kanıtı kalan iş kabul bekleyenle aynı önemde (2), kabul bekleyen aynı önemde önce gelir.
        Assert.Equal(new[] { 1, 2, 2, 4, 5 }, s.Items.Select(i => i.Priority).ToArray());
        Assert.Equal(1, s.Counts.Ask); Assert.Equal(1, s.Counts.Accept); Assert.Equal(1, s.Counts.Blocked); Assert.Equal(1, s.Counts.Done); Assert.Equal(1, s.Counts.Uncertain);
        Assert.Equal(5, s.Counts.Total);

        Assert.Equal("«Soru işi» işinde yardımcı sana bir soru sordu.", s.Items[0].Sentence);
        Assert.Equal("Hangisini seçeyim?", s.Items[0].Detail);
        Assert.Equal(ask.Uid, s.Items[0].JobUid);
        Assert.Equal(ask.Id, s.Items[0].JobId);
        Assert.Contains(s.Items[0].Actions, a => a.Id == "reply" && a.Method == "POST" && a.Path == $"/api/jobs/{ask.Id}/reply");
        Assert.Contains("kabul edip etmeyeceğine sen karar ver", s.Items[1].Sentence);
        Assert.Contains(s.Items[1].Actions, a => a.Id == "accept" && a.Path == $"/api/jobs/{decision.Id}/accept" && (bool?)a.Body!["accept"] == true);
        Assert.Contains(s.Items[1].Actions, a => a.Id == "reject" && (bool?)a.Body!["accept"] == false);
        Assert.Equal("«Kalan iş» için deneme sonucu geçmedi.", s.Items[2].Sentence);
        Assert.Contains(s.Items[2].Actions, a => a.Id == "continue");
        Assert.Equal("«Bitti işi» deneme sonucuyla bitti.", s.Items[3].Sentence);
        Assert.Equal("firm", s.Items[3].Confidence);
        Assert.Contains("Bitti mi bilmiyorum", s.Items[4].Sentence);
        Assert.Equal("unknown", s.Items[4].Confidence);

        // çalışan iş bekleyen değil, ayrı listede; cümle hepsini söyler
        Assert.Equal(run.Id, Assert.Single(s.Running).JobId);
        Assert.Equal("1 soru, 1 kabul, 1 engel, 1 bitti seni bekliyor. 1 işin durumundan emin değilim, 1 iş çalışıyor.", s.Sentence);
        // hiçbir cümle kod, yol ya da ham durum adı taşımaz
        Assert.All(s.Items, i => { Assert.DoesNotContain("\\", i.Sentence); Assert.DoesNotContain("cevap_bekliyor", i.Sentence); Assert.DoesNotContain("job", i.Sentence, StringComparison.OrdinalIgnoreCase); });
    }

    [Fact]
    public void Calisiyor_gorunen_ama_sureci_kaybolan_kosu_belirsizdir_canli_olan_degil()
    {
        using var r = new Rig();
        var lost = r.Job("Süreci ölmüş", JobStates.Running);
        var live = r.Job("Canlı", JobStates.Running);
        using (var c = r.Env.Svc.Db.Open())
        {
            var a = RunStore.Insert(c, lost.Id, r.ProjectId, "start");
            RunStore.SetProcess(c, a, 2_000_000_000, T0); // böyle bir süreç yok
            var b = RunStore.Insert(c, live.Id, r.ProjectId, "start");
            RunStore.SetProcess(c, b, Environment.ProcessId, System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());
        }
        // başlangıç payı dolmadan yanlış alarm yok
        Assert.DoesNotContain(r.Svc.Snapshot(nowUtc: T0.AddSeconds(5)).Items, i => i.Reason == "run_lost");
        var s = r.Svc.Snapshot(nowUtc: T0.AddSeconds(AttentionBuilder.DeadRunGraceSeconds + 5));
        var it = Assert.Single(s.Items);
        Assert.Equal("uncertain", it.Kind); Assert.Equal("run_lost", it.Reason); Assert.False(it.Signal);
        Assert.Equal(lost.Id, it.JobId);
        Assert.Contains("Durumu belirsiz", it.Sentence);
        Assert.Equal(2, s.Running.Count); // ikisi de "çalışıyor" listesinde; biri için emin değilim
    }

    [Fact]
    public void Onay_bekleyen_is_soru_sayilir_ve_izin_eylemleri_verir()
    {
        using var r = new Rig();
        var j = r.Job("Onay işi", JobStates.Running);
        using (var c = r.Env.Svc.Db.Open())
        {
            var rid = RunStore.Insert(c, j.Id, r.ProjectId, "start");
            RunStore.AddApproval(c, rid, j.Id, 11, "Bash", "npm install çalıştırılsın mı?", "h1");
            JobStore.SetState(c, j.Id, JobStates.AwaitingApproval, r.ProjectId);
        }
        var it = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Equal("ask", it.Kind); Assert.Equal("approval", it.Reason);
        Assert.Contains("izin bekliyor", it.Sentence);
        Assert.Contains("npm install", it.Detail);
        Assert.Equal(new[] { "allow", "deny" }, it.Actions.Select(a => a.Id).ToArray());
        Assert.True((bool)it.Actions[0].Body!["allow"]!); Assert.False((bool)it.Actions[1].Body!["allow"]!);
    }

    [Fact]
    public void Terminal_oturumu_bekliyorsa_listeye_girer_ve_gordum_denemez()
    {
        using var r = new Rig();
        HookHandler.Run("notification", new StringReader(System.Text.Json.JsonSerializer.Serialize(new { session_id = "s-term", cwd = Path.Combine(r.Env.Root, "ornek-node"), notification_type = "idle_prompt" })), new StringWriter(), r.Env.DataDir, _ => null);
        var s = r.Svc.Snapshot();
        var it = Assert.Single(s.Items);
        Assert.Equal("uncertain", it.Kind); Assert.Equal("session_turn_end", it.Reason); // E2: bitti dediğine dair işaret yok
        Assert.NotNull(it.SessionId); Assert.Null(it.JobId);
        Assert.Contains("sırayı sana verdi", it.Sentence);
        Assert.Equal(409, Assert.Throws<SozException>(() => r.Svc.Ack(it.Id)).Status);
    }

    [Fact]
    public void Ayni_is_ikinci_kez_soru_sorarsa_yeni_ogedir_ve_yeni_isaret_dogurur()
    {
        using var r = new Rig();
        var j = r.Job("Sorucu", JobStates.AwaitingAnswer);
        var first = Assert.Single(r.Svc.Snapshot().Items);
        using (var c = r.Env.Svc.Db.Open()) { JobStore.SetState(c, j.Id, JobStates.Running, r.ProjectId); }
        Assert.Empty(r.Svc.Snapshot().Items);
        using (var c = r.Env.Svc.Db.Open()) { JobStore.SetAwaitingAnswer(c, j.Id, "Bir daha sorayım.", r.ProjectId); }
        var second = Assert.Single(r.Svc.Snapshot().Items);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(second.Signal);
        Assert.Equal(2, r.Svc.Snapshot().Budget.UsedToday);
    }

    // ------------------------------------------------------------ gördüm
    [Fact]
    public void Gordum_biten_isi_listeden_dusurur_isi_silmez_ve_olay_yazar()
    {
        using var r = new Rig();
        var j = r.Job("Bitti", JobStates.Passed);
        var s0 = r.Svc.Snapshot();
        var item = Assert.Single(s0.Items);
        Assert.Contains(item.Actions, a => a.Id == "seen" && a.Path == "/api/attention/ack" && (string?)a.Body!["id"] == item.Id);

        r.Svc.Ack(item.Id);
        var s1 = r.Svc.Snapshot();
        Assert.Empty(s1.Items);
        Assert.True(s1.Rev > s0.Rev, "gördüm olay kaydına yazılır, sürüm artar");
        Assert.Equal(JobStates.Passed, r.Work.Job(j.Id).State); // iş durur, yalnız liste sakinleşir
        using var c = r.Env.Svc.Db.Open();
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='attention.acked'"));
        // ikinci kez: öğe artık yok
        Assert.Equal(404, Assert.Throws<SozException>(() => r.Svc.Ack(item.Id)).Status);
    }

    [Fact]
    public void Gordum_cevap_ya_da_karar_bekleyen_ise_ve_belirsiz_calisana_uygulanmaz()
    {
        using var r = new Rig();
        r.Job("Soru", JobStates.AwaitingAnswer); r.Job("Karar", JobStates.AwaitingDecision);
        var items = r.Svc.Snapshot().Items;
        foreach (var it in items) Assert.Equal(409, Assert.Throws<SozException>(() => r.Svc.Ack(it.Id)).Status);
        Assert.Equal(404, Assert.Throws<SozException>(() => r.Svc.Ack("done:job:yok:2026")).Status);
        Assert.Equal(2, r.Svc.Snapshot().Items.Count);
    }

    [Fact]
    public void Gordum_kalan_ve_baslatilamayan_isi_de_dusurur()
    {
        using var r = new Rig();
        var f = r.Job("Kalan", JobStates.Failed);
        var b = r.Job("Başlamayan", JobStates.StartFailed);
        using (var c = r.Env.Svc.Db.Open()) c.Exec("UPDATE jobs SET start_error='Claude bulunamadı.' WHERE id=@i", null, ("@i", b.Id));
        var s = r.Svc.Snapshot();
        Assert.Equal(2, s.Counts.Blocked);
        Assert.Contains(s.Items, i => i.Reason == "start_failed" && i.Detail is null && i.SozNotu == "Claude bulunamadı."); // C9: Söz'ün notu, yardımcının sözü değil
        foreach (var it in s.Items) r.Svc.Ack(it.Id);
        Assert.Empty(r.Svc.Snapshot().Items);
    }

    [Fact]
    public void Kullanicinin_kendi_kabul_ettigi_is_listede_gorunmez()
    {
        using var r = new Rig();
        var j = r.Job("Kabul edilmiş", JobStates.Passed);
        using (var c = r.Env.Svc.Db.Open()) c.Exec("UPDATE jobs SET accepted_by_user=1 WHERE id=@i", null, ("@i", j.Id));
        Assert.Empty(r.Svc.Snapshot().Items);
    }

    [Fact]
    public void Biten_is_ayardaki_sure_sonra_kendiliginden_duser()
    {
        using var r = new Rig();
        r.Job("Eski bitti", JobStates.Passed);
        Assert.Single(r.Svc.Snapshot(nowUtc: T0.AddHours(23)).Items);
        Assert.Empty(r.Svc.Snapshot(nowUtc: T0.AddHours(25)).Items);
        r.Set(("attentionDoneHours", 48));
        Assert.Single(r.Svc.Snapshot(nowUtc: T0.AddHours(25)).Items);
    }

    // ------------------------------------------------------------ dikkat bütçesi
    [Fact]
    public void Dikkat_butcesi_gunde_uc_isaret_fazlasi_sessiz_ama_listede()
    {
        using var r = new Rig();
        for (var i = 1; i <= 5; i++) r.Job($"Bitti {i}", JobStates.Passed);
        var s = r.Svc.Snapshot();
        Assert.Equal(5, s.Items.Count);
        Assert.Equal(3, s.Items.Count(i => i.Signal));
        Assert.Equal(2, s.Items.Count(i => !i.Signal));
        Assert.Equal(3, s.Budget.UsedToday); Assert.Equal(2, s.Budget.SuppressedToday); Assert.Equal(3, s.Budget.DailyLimit);
        // tekrar bakmak hak harcamaz, karar öğeyle birlikte kalır
        var again = r.Svc.Snapshot();
        Assert.Equal(s.Items.Select(i => (i.Id, i.Signal)).ToArray(), again.Items.Select(i => (i.Id, i.Signal)).ToArray());
        Assert.Equal(3, again.Budget.UsedToday);
        // sonradan gelen yeni öğe de hak yok
        var late = r.Job("Altıncı", JobStates.Passed);
        var s3 = r.Svc.Snapshot();
        Assert.False(s3.Items.Single(i => i.JobId == late.Id).Signal);
        Assert.Equal(3, s3.Budget.UsedToday); Assert.Equal(3, s3.Budget.SuppressedToday);
    }

    [Fact]
    public void Dikkat_butcesi_oncelik_sirasiyla_dagitilir_soru_bittiden_once_isaret_alir()
    {
        using var r = new Rig();
        r.Set(("attentionDailySignals", 1));
        r.Job("Bitti", JobStates.Passed);
        r.Job("Soru", JobStates.AwaitingAnswer);
        var s = r.Svc.Snapshot();
        Assert.True(s.Items.Single(i => i.Kind == "ask").Signal);
        Assert.False(s.Items.Single(i => i.Kind == "done").Signal);
    }

    [Fact]
    public void Dikkat_butcesi_sifirsa_hic_isaret_yok_baslatilamadi_engeli_butcesizdir()
    {
        using var r = new Rig();
        r.Set(("attentionDailySignals", 0));
        r.Job("Bitti", JobStates.Passed);
        r.Job("Soru", JobStates.AwaitingAnswer);
        var bad = r.Job("Başlamayan", JobStates.StartFailed);
        var s = r.Svc.Snapshot();
        Assert.False(s.Items.Single(i => i.Kind == "done").Signal);
        Assert.False(s.Items.Single(i => i.Kind == "ask").Signal);
        Assert.True(s.Items.Single(i => i.Reason == "start_failed").Signal); // kişinin o an denediği şeyin engeli yerinde gösterilir
        Assert.Equal(0, s.Budget.UsedToday);
    }

    [Fact]
    public void Belirsiz_oge_hic_isaret_almaz_ve_hak_harcamaz()
    {
        using var r = new Rig();
        var u = r.Job("Kanıtsız", JobStates.Given);
        using (var c = r.Env.Svc.Db.Open()) { var rid = RunStore.Insert(c, u.Id, r.ProjectId, "start"); RunStore.SetStatus(c, rid, RunStatus.Done, ended: true); }
        var s = r.Svc.Snapshot();
        var it = Assert.Single(s.Items);
        Assert.Equal("uncertain", it.Kind); Assert.False(it.Signal);
        Assert.Equal(0, s.Budget.UsedToday); Assert.Equal(0, s.Budget.SuppressedToday);
    }

    [Fact]
    public void Dikkat_butcesi_yeni_gunde_sifirlanir_eski_ogeler_yeniden_isaret_dogurmaz()
    {
        using var r = new Rig();
        r.Set(("attentionDoneHours", 168));
        for (var i = 1; i <= 4; i++) r.Job($"Bitti {i}", JobStates.Passed);
        var s0 = r.Svc.Snapshot(nowUtc: T0);
        Assert.Equal(3, s0.Budget.UsedToday);
        var next = T0.AddHours(30); // en az bir gün sonra (yerel saat dilimi ne olursa olsun)
        var s1 = r.Svc.Snapshot(nowUtc: next);
        Assert.Equal(0, s1.Budget.UsedToday);
        Assert.Equal(s0.Items.Select(i => (i.Id, i.Signal)).ToArray(), s1.Items.Select(i => (i.Id, i.Signal)).ToArray());
        r.Job("Yeni gün işi", JobStates.Passed);
        Assert.True(r.Svc.Snapshot(nowUtc: next).Items.Single(i => i.Sentence.Contains("Yeni gün")).Signal);
    }

    [Fact]
    public void Dikkat_defteri_sunucu_yeniden_baslasa_da_korunur()
    {
        using var r = new Rig();
        for (var i = 1; i <= 4; i++) r.Job($"Bitti {i}", JobStates.Passed);
        var before = r.Svc.Snapshot();
        var fresh = new AttentionService(r.Env.Svc.Db); // yeni süreç
        var after = fresh.Snapshot();
        Assert.Equal(before.Items.Select(i => (i.Id, i.Signal)).ToArray(), after.Items.Select(i => (i.Id, i.Signal)).ToArray());
        Assert.Equal(3, after.Budget.UsedToday);
    }

    [Fact]
    public void Bozuk_dikkat_defteri_uyari_vermeden_bos_baslar()
    {
        using var r = new Rig();
        using (var c = r.Env.Svc.Db.Open()) SozSettings.SetInternal(c, AttentionLedger.SettingKey, "{bozuk json");
        r.Job("Bitti", JobStates.Passed);
        Assert.True(r.Svc.Snapshot().Items.Single().Signal);
    }

    // ------------------------------------------------------------ kaçırılanı alma
    [Fact]
    public void Since_ile_sonradan_gelen_ogeler_isNew_isaretlenir()
    {
        using var r = new Rig();
        r.Job("Eski bitti", JobStates.Passed);
        var s0 = r.Svc.Snapshot(sinceRev: 0);
        Assert.All(s0.Items, i => Assert.True(i.IsNew)); // hiçbir şey bilmeyen istemci için hepsi yeni
        var rev0 = s0.Rev;
        Assert.All(r.Svc.Snapshot(sinceRev: rev0).Items, i => Assert.False(i.IsNew));

        Time.UtcNow = () => T0.AddMinutes(5);
        r.Job("Kopukken biten", JobStates.Passed);
        Time.UtcNow = () => T0.AddMinutes(6);
        var s1 = r.Svc.Snapshot(sinceRev: rev0);
        Assert.Equal(new[] { true }, s1.Items.Where(i => i.Sentence.Contains("Kopukken")).Select(i => i.IsNew).ToArray());
        Assert.False(s1.Items.Single(i => i.Sentence.Contains("Eski bitti")).IsNew);
        Assert.DoesNotContain(r.Svc.Snapshot(sinceRev: s1.Rev).Items, i => i.IsNew);
    }

    [Fact]
    public void Imza_degisiklikte_degisir_ve_degismezse_ayni_kalir()
    {
        using var r = new Rig();
        var a = AttentionService.Signature(r.Svc.Snapshot());
        Assert.Equal(a, AttentionService.Signature(r.Svc.Snapshot()));
        r.Job("Yeni", JobStates.Passed);
        Assert.NotEqual(a, AttentionService.Signature(r.Svc.Snapshot()));
    }

    // ------------------------------------------------------------ kayıt defteri
    [Fact]
    public void Ayarlar_kayit_defterinden_gelir_sinirlari_dogrular_ve_dort_cumle_dolu()
    {
        Assert.Equal(3, SozSettings.Defaults["attentionDailySignals"]!.GetValue<int>());
        Assert.Equal(24, SozSettings.Defaults["attentionDoneHours"]!.GetValue<int>());
        Assert.Null(SozSettings.Validate(new JsonObject { ["attentionDailySignals"] = 0 }));
        Assert.Null(SozSettings.Validate(new JsonObject { ["attentionDailySignals"] = 20 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["attentionDailySignals"] = 21 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["attentionDailySignals"] = -1 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["attentionDoneHours"] = 0 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["attentionDoneHours"] = 169 }));
        foreach (var id in new[] { "setting:attention.daily-signals", "setting:attention.done-hours" })
        {
            var e = FixedCatalog.Get.Get(id)!;
            Assert.Equal("kapsul-bildirim", e.Setting!.Group);
            Assert.Empty(e.Contract!.Missing());
        }
    }

    // ------------------------------------------------------------ sunucu ucu
    private static int RastgeleBaglanti() => 52000 + Random.Shared.Next(0, 800);

    [Fact]
    public async Task Uc_get_json_verir_ack_belirtec_ister_ve_degisim_olayi_yayinlanir()
    {
        using var env = new TestEnv(copySamples: false);
        TickingClock();
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NodeSample.Create(env));
        await using var h = await SozServer.StartAsync(env.DataDir, RastgeleBaglanti());
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };

        var empty = await http.GetFromJsonAsync<JsonObject>("/api/attention");
        Assert.Equal(0, (int)empty!["items"]!.AsArray().Count);
        Assert.Equal("Şu an seni bekleyen bir şey yok.", (string?)empty["sentence"]);
        foreach (var k in new[] { "rev", "now", "sentence", "counts", "budget", "items", "running" }) Assert.True(empty.ContainsKey(k), k);

        // canlı akışa bağlan, sonra bir iş sorsun
        using var sse = new HttpClient { BaseAddress = new Uri(h.Url), Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await sse.GetAsync("/api/events/stream", HttpCompletionOption.ResponseHeadersRead);
        using var rd = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await rd.ReadLineAsync(); // ": bağlandı"
        var j = w.CreateJob(pid, "Akış işi");
        using (var c = env.Svc.Db.Open()) JobStore.SetAwaitingAnswer(c, j.Id, "Hangisi?", pid);

        string? data = null; var gotAttention = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await rd.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (line == "event: attention") gotAttention = true;
                else if (gotAttention && line.StartsWith("data: ")) { data = line[6..]; break; }
            }
        }
        catch (OperationCanceledException) { }
        Assert.True(gotAttention, "SSE attention olayı gelmedi");
        var ev = JsonNode.Parse(data!)!;
        Assert.Equal(1, (int)ev["counts"]!["ask"]!);
        Assert.Single(ev["newIds"]!.AsArray());
        Assert.Single(ev["signalIds"]!.AsArray());
        Assert.Equal("1 soru seni bekliyor.", (string?)ev["sentence"]);

        var got = await http.GetFromJsonAsync<JsonObject>("/api/attention?since=0");
        var item = got!["items"]![0]!;
        Assert.Equal("ask", (string?)item["kind"]);
        Assert.True((bool)item["signal"]!); Assert.True((bool)item["isNew"]!);
        Assert.Equal("reply", (string?)item["actions"]![0]!["id"]);

        // gördüm: jeton ister (yazma ucu), cevap bekleyen işte 409
        var noTok = new HttpRequestMessage(HttpMethod.Post, "/api/attention/ack") { Content = new StringContent("{\"id\":\"x\"}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(noTok)).StatusCode);
        HttpRequestMessage Ack(string id) { var m = new HttpRequestMessage(HttpMethod.Post, "/api/attention/ack") { Content = new StringContent(new JsonObject { ["id"] = id }.ToJsonString(), Encoding.UTF8, "application/json") }; m.Headers.Add("X-Soz-Token", h.Token); return m; }
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Ack((string)item["id"]!))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Ack("done:job:yok:1"))).StatusCode);
        var noId = new HttpRequestMessage(HttpMethod.Post, "/api/attention/ack") { Content = new StringContent("{}", Encoding.UTF8, "application/json") }; noId.Headers.Add("X-Soz-Token", h.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(noId)).StatusCode);

        // /api/now bozulmadı
        var now = await http.GetFromJsonAsync<JsonObject>("/api/now");
        Assert.Equal(1, (int)now!["waitingJobs"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("event: attention\ndata: {}\n\n", true)]
    [InlineData("id: 5\nevent: attention\ndata: {}\n\n", true)]
    [InlineData("event: job.progress\ndata: {}\n\n", true)]
    [InlineData("id: 7\nevent: job\ndata: {}\n\n", false)]
    [InlineData("event: job.state\ndata: {}\n\n", false)]
    [InlineData(": canlı\n\n", false)]
    public void Pompa_kendi_olayini_ve_ilerleme_nabzini_atlar(string msg, bool beklenen)
        => Assert.Equal(beklenen, Soz.Server.Endpoints.AttentionEndpoints.IsOwnOrNoise(msg));

    // d2m: kişinin sırası gelen şeylerin tek öncelik tablosu. Bu test tabloyu sabitler; kapsül ve CLI sunucudaki priority alanına uyar.
    //   1 soru | 2 kabul bekleyen kanıt + kanıtı kalan iş | 3 başka engel | 4 bitti | 5 emin değilim (uyandırmaz)
    [Fact]
    public void Oncelik_tablosu_sabit_ve_kaldi_kabul_bekleyenle_ayni_onemde()
    {
        Assert.Equal(new[] { ("ask", (string?)null, 1), ("accept", null, 2), ("blocked", "proof_failed", 2), ("blocked", null, 3), ("done", null, 4), ("uncertain", null, 5) },
            AttentionKinds.Table.Select(t => (t.Kind, t.Reason, t.Priority)).ToArray());
        Assert.Equal(AttentionKinds.Priority(AttentionKinds.Accept), AttentionKinds.Priority(AttentionKinds.Blocked, AttentionKinds.ProofFailedReason));
        Assert.True(AttentionKinds.Priority(AttentionKinds.Ask) < AttentionKinds.Priority(AttentionKinds.Blocked, AttentionKinds.ProofFailedReason));
        Assert.Equal(3, AttentionKinds.Priority(AttentionKinds.Blocked, "run_lost"));              // başka engel nedeni tür varsayılanını alır
        Assert.Equal(3, AttentionKinds.Priority(AttentionKinds.Blocked, AttentionKinds.StartFailedReason));
        Assert.Equal(AttentionKinds.All.Length, AttentionKinds.All.Distinct().Count());
        Assert.All(AttentionKinds.All, k => Assert.Contains(AttentionKinds.Table, t => t.Kind == k));
    }
}
