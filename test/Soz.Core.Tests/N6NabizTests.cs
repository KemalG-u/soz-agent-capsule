using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Attention;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Queue;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// n6 (NABIZ): "Benden Beklenen" kutusu. İzin / bilgi / soru ayrı söylenir, hata verip duran ayrı, dışarıdaki canlı oturum tek öğede birleşir,
/// oturumlar kendiliğinden içe alınır, "bitti" öğesinde "pencereye git" ve "bu işi sına" eylemleri, kuyruk teslim dürüstlüğü.
/// Hepsi sentetik kayıt ve sahte kanca girdisiyle; gerçek yardımcı çalışmaz.
/// </summary>
public class N6NabizTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig : IDisposable
    {
        public readonly TestEnv Env = new(copySamples: false);
        public readonly AttentionService Svc;
        public readonly string Dir;
        public readonly WorkService Work;
        public readonly long ProjectId;
        public Rig()
        {
            Dir = NodeSample.Create(Env, "n6-proje");
            Work = new WorkService(Env.Svc.Db);
            ProjectId = Work.EnsureProjectByPath(Dir);
            Svc = new AttentionService(Env.Svc.Db);
        }
        public void At(int minutes) => Time.UtcNow = () => T0.AddMinutes(minutes);
        public void Hook(string name, object input)
            => HookHandler.Run(name, new StringReader(JsonSerializer.Serialize(input)), new StringWriter(), Env.DataDir, _ => null);
        public long Notify(string sessionId, string? type, int minutes = 0, string? prompt = "merhaba")
        {
            At(minutes);
            if (prompt is not null) Hook("user-prompt-submit", new { session_id = sessionId, cwd = Dir, prompt });
            Hook("notification", new { session_id = sessionId, cwd = Dir, notification_type = type });
            using var c = Env.Svc.Db.Open();
            return c.ScalarLong("SELECT id FROM sessions WHERE external_id=@e", null, ("@e", sessionId));
        }
        /// <summary>Yardımcının son sözünü (sentetik) oturum kaydına yazar: tur kapanmış, son söz yardımcıda (E2: "bitti" ayrımı bu kayda bakar).</summary>
        public void AssistantSaid(long sid, string text, int minutes)
        {
            using var c = Env.Svc.Db.Open();
            c.Exec("UPDATE sessions SET last_assistant_text=@t, last_assistant_ts=@a, turn_ended=1 WHERE id=@id", null, ("@t", text), ("@a", Time.Iso(T0.AddMinutes(minutes))), ("@id", sid));
        }
        /// <summary>k6'nın StopFailure kancası gelene dek: kancanın yazacağı satırı elle yazar (needs_user_kind = failure:&lt;ad&gt;).</summary>
        public void FlagFailure(long sid, string code, int minutes)
        {
            using var c = Env.Svc.Db.Open();
            c.Exec("UPDATE sessions SET needs_user_since=@n, needs_user_kind=@k WHERE id=@id", null, ("@n", Time.Iso(T0.AddMinutes(minutes))), ("@k", FailureProbe.HookKindPrefix + code), ("@id", sid));
        }
        public string ClaudeFile(string id, params string[] lines)
        {
            var dir = Path.Combine(Env.ClaudeRoot, "n6-proje");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, id + ".jsonl");
            File.WriteAllText(file, string.Join("\n", lines) + "\n");
            File.SetLastWriteTimeUtc(file, T0.AddMinutes(-1));
            return file;
        }
        public string UserLine(string id, int minutes) => JsonSerializer.Serialize(new { type = "user", sessionId = id, cwd = Dir, timestamp = Time.Iso(T0.AddMinutes(minutes)), message = new { role = "user", content = "SENTETIK istem" } });
        public string ErrLine(string id, int minutes, string? code, string text) => JsonSerializer.Serialize(new
        {
            type = "assistant", sessionId = id, cwd = Dir, timestamp = Time.Iso(T0.AddMinutes(minutes)), isApiErrorMessage = true, error = code,
            message = new { role = "assistant", content = new object[] { new { type = "text", text } } },
        });
        public string AsstLine(string id, int minutes, string text) => JsonSerializer.Serialize(new
        {
            type = "assistant", sessionId = id, cwd = Dir, timestamp = Time.Iso(T0.AddMinutes(minutes)),
            message = new { role = "assistant", stop_reason = "end_turn", content = new object[] { new { type = "text", text } } },
        });
        public void Dispose() => Env.Dispose();
    }

    private static ExternalSession Ext(string tool, string id, string state, string? waitingFor = null, string? path = null, int? pid = 4242, DateTime? observed = null, DateTime? since = null)
        => new(tool, id, path, null, pid, null, state, waitingFor, "surec", observed ?? T0, since);

    // ------------------------------------------------------------ 1. izin / girdi / soru ayrı söylenir
    [Fact]
    public void Izin_bilgi_ve_bos_bekleyis_kancadan_ayri_ayri_ve_sade_cumleyle_kutuya_girer()
    {
        using var r = new Rig();
        r.Notify("s-izin", "permission_prompt", 0);
        r.Notify("s-bilgi", "elicitation_dialog", 1);
        r.Notify("s-girdi", "agent_needs_input", 2);
        r.Notify("s-bos", "idle_prompt", 3);
        r.At(10);
        var items = r.Svc.Snapshot().Items;

        var izin = Assert.Single(items, i => i.Reason == "session_permission" && i.ExternalSessionId == "s-izin");
        Assert.Equal(AttentionKinds.Ask, izin.Kind); Assert.Equal(1, izin.Priority);
        Assert.Contains("izin bekliyor", izin.Sentence);
        Assert.DoesNotContain("işini bitirdi", izin.Sentence);
        Assert.Equal("hook", izin.Source);
        Assert.Equal("Claude Code", izin.Helper);
        Assert.Equal("n6-proje", izin.ProjectName);
        Assert.True(izin.AgeSeconds >= 600);

        var bilgi = Assert.Single(items, i => i.ExternalSessionId == "s-bilgi");
        Assert.Equal("session_input", bilgi.Reason); Assert.Contains("bir bilgi bekliyor", bilgi.Sentence);
        Assert.Equal("session_input", Assert.Single(items, i => i.ExternalSessionId == "s-girdi").Reason);

        var bos = Assert.Single(items, i => i.ExternalSessionId == "s-bos");
        // E2: yardımcının bitirdiğine dair işaret yok: "bitti" denmez, "sırayı sana verdi" denir.
        Assert.Equal(AttentionKinds.Uncertain, bos.Kind); Assert.Equal("session_turn_end", bos.Reason);
        Assert.Contains("sırayı sana verdi", bos.Sentence); Assert.DoesNotContain("işini bitirdi", bos.Sentence);

        // sıra: önce bekleyenler (en eski üstte), bitenler sonda
        Assert.Equal(new[] { "s-izin", "s-bilgi", "s-girdi", "s-bos" }, items.Select(i => i.ExternalSessionId).ToArray());
        // izin öğesinin eylemi pencereye gitmek
        Assert.Contains(izin.Actions, a => a.Id == "go" && a.Method == "POST" && a.Path == $"/api/sessions/{izin.SessionId}/focus");
        Assert.DoesNotContain(izin.Actions, a => a.Id == "prove");
        // "gördüm" ile kapanmaz
        Assert.Equal(409, Assert.Throws<SozException>(() => r.Svc.Ack(izin.Id)).Status);
        // hiçbir cümlede teknik söz yok
        Assert.All(items, i => { Assert.DoesNotContain("permission", i.Sentence); Assert.DoesNotContain(";", i.Sentence); Assert.DoesNotContain("—", i.Sentence); });
    }

    [Fact]
    public void Bilinmeyen_bildirim_turu_izin_denmez_ve_soru_metni_varsa_soru_olur()
    {
        using var r = new Rig();
        r.Notify("s-x", "baska_bir_sey", 0);
        var it = Assert.Single(r.Svc.Snapshot().Items);
        Assert.NotEqual("session_permission", it.Reason);
        Assert.DoesNotContain("izin", it.Sentence);
    }

    // ------------------------------------------------------------ 4. hata verip durma
    [Fact]
    public void Kancadan_gelen_hata_durmasi_durdu_nedeni_su_diye_engel_olarak_girer()
    {
        using var r = new Rig();
        var sid = r.Notify("s-limit", "idle_prompt", 0);
        r.FlagFailure(sid, "rate_limit", 1);
        r.At(5);
        var it = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Equal(AttentionKinds.Blocked, it.Kind); Assert.Equal("session_failed", it.Reason); Assert.Equal(3, it.Priority);
        Assert.Equal("«merhaba» yardımcısı hata yüzünden durdu: kullanım sınırı doldu.", it.Sentence);
        Assert.Equal("hook", it.Source);
        Assert.Contains(it.Actions, a => a.Id == "go");

        r.FlagFailure(sid, "authentication_failed", 1);
        Assert.Contains("giriş geçersiz", Assert.Single(r.Svc.Snapshot().Items).Sentence);
        r.FlagFailure(sid, "billing_error", 1);
        Assert.Contains("ödeme ya da bakiye", Assert.Single(r.Svc.Snapshot().Items).Sentence);
        r.FlagFailure(sid, "icat_edilmis_ad", 1);
        Assert.Contains("nedenini ayırt edemedim", Assert.Single(r.Svc.Snapshot().Items).Sentence);
    }

    [Theory]
    [InlineData("rate_limit", "You've hit your limit", "kullanım sınırı doldu")]
    [InlineData(null, "API Error: usage limit reached", "kullanım sınırı doldu")]
    [InlineData("authentication_failed", "Invalid API key · Please run /login", "giriş geçersiz")]
    [InlineData(null, "OAuth token has expired. Please run /login", "giriş geçersiz")]
    [InlineData("server_error", "Overloaded", "hizmet tarafında bir sorun")]
    [InlineData(null, "API Error: 529 overloaded_error", "hizmet tarafında bir sorun")]
    [InlineData(null, "Credit balance is too low", "ödeme ya da bakiye")]
    [InlineData(null, "tamamen anlaşılmaz bir ileti", "nedenini ayırt edemedim")]
    public void Kayittaki_hata_satiri_sinifina_gore_nedenle_engel_olur(string? code, string text, string expectedCause)
    {
        using var r = new Rig();
        var id = "c-hata";
        r.ClaudeFile(id, r.UserLine(id, -6), r.ErrLine(id, -5, code, text));
        r.Env.Import();
        r.At(0);
        var it = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Equal("session_failed", it.Reason); Assert.Equal(AttentionKinds.Blocked, it.Kind);
        Assert.Contains(expectedCause, it.Sentence);
        Assert.Equal("log", it.Source);
        Assert.Equal("Claude Code", it.Helper);
    }

    [Fact]
    public void Hatadan_sonra_ajan_devam_ettiyse_ya_da_hata_hic_yoksa_engel_cikmaz()
    {
        using var r = new Rig();
        r.ClaudeFile("c-devam", r.UserLine("c-devam", -8), r.ErrLine("c-devam", -7, "rate_limit", "limit"), r.UserLine("c-devam", -6), r.AsstLine("c-devam", -5, "Tamam, devam ediyorum."));
        r.ClaudeFile("c-sakin", r.UserLine("c-sakin", -8), r.AsstLine("c-sakin", -7, "Yaptım."));
        r.Env.Import();
        r.At(0);
        Assert.DoesNotContain(r.Svc.Snapshot().Items, i => i.Reason == "session_failed");
        // hata satırından sonra yalnız sistem satırları gelirse hâlâ durmuştur
        r.ClaudeFile("c-sistem", r.UserLine("c-sistem", -8), r.ErrLine("c-sistem", -7, "rate_limit", "limit"), JsonSerializer.Serialize(new { type = "system", subtype = "turn_duration", timestamp = Time.Iso(T0.AddMinutes(-6)) }));
        r.Env.Import();
        Assert.Contains(r.Svc.Snapshot().Items, i => i.Reason == "session_failed");
    }

    [Fact]
    public void Codex_kaydinin_sonundaki_hata_olayi_da_durdu_olarak_girer_tamamlanmis_tur_girmez()
    {
        using var r = new Rig();
        var dir = Path.Combine(r.Env.CodexRoot, "2026", "11", "10"); Directory.CreateDirectory(dir);
        string Line(string type, object payload, int m) => JsonSerializer.Serialize(new { timestamp = Time.Iso(T0.AddMinutes(m)), type, payload });
        void Write(string id, params string[] extra)
        {
            var lines = new List<string>
            {
                Line("session_meta", new { id, cwd = r.Dir, timestamp = Time.Iso(T0.AddMinutes(-9)), originator = "codex_cli_rs" }, -9),
                Line("event_msg", new { type = "task_started" }, -9),
                Line("event_msg", new { type = "item_completed", item = new { type = "UserMessage", content = new object[] { new { type = "input_text", text = "SENTETIK istem" } } } }, -9),
            };
            lines.AddRange(extra);
            var f = Path.Combine(dir, $"rollout-2026-11-10T11-50-00-{id}.jsonl");
            File.WriteAllText(f, string.Join("\n", lines) + "\n"); File.SetLastWriteTimeUtc(f, T0.AddMinutes(-1));
        }
        Write("x-hata", Line("event_msg", new { type = "error", message = "You exceeded your current quota", codex_error_info = "usage_limit_exceeded" }, -5));
        Write("x-bitti", Line("event_msg", new { type = "error", message = "limit" }, -6), Line("event_msg", new { type = "task_complete", last_agent_message = "Tamam." }, -5));
        r.Env.Import();
        r.At(0);
        var failed = Assert.Single(r.Svc.Snapshot().Items, i => i.Reason == "session_failed");
        Assert.Contains("kullanım sınırı doldu", failed.Sentence);
        Assert.Equal("Codex", failed.Helper);
    }

    // ------------------------------------------------------------ 2. dış oturum kaynağı
    [Fact]
    public void Kancasiz_dis_oturum_seni_bekliyor_derse_kutuya_girer_proje_yardimci_ne_bekledigi_ile()
    {
        using var r = new Rig();
        r.At(0);
        r.Svc.External = new FixedExternalSessions(new[]
        {
            Ext("claude", "dis-1", ExternalStates.WaitingForYou, "approve Bash: npm test", r.Dir),
            Ext("claude", "dis-2", ExternalStates.Working, null, r.Dir),
            Ext("codex", "dis-3", ExternalStates.Idle, null, r.Dir),
            Ext("claude", "dis-4", ExternalStates.Unknown, null, r.Dir),
        });
        var s = r.Svc.Snapshot();
        var it = Assert.Single(s.Items);
        Assert.Equal(AttentionKinds.Ask, it.Kind); Assert.Equal("session_permission", it.Reason);
        Assert.Equal("n6-proje", it.ProjectName); Assert.Equal("Claude Code", it.Helper); Assert.Equal("dis-1", it.ExternalSessionId);
        Assert.Equal("agents", it.Source); Assert.Equal(Time.Iso(T0), it.ObservedAt);
        Assert.Contains("approve Bash", it.Detail);
        Assert.StartsWith("ask:ext:claude:dis-1:", it.Id);
        var go = Assert.Single(it.Actions);
        Assert.Equal("/api/attention/focus", go.Path); Assert.Equal(it.Id, (string?)go.Body!["id"]);
        Assert.Equal(1, s.Counts.Ask);
    }

    [Fact]
    public void Ayni_oturum_hem_kancadan_hem_dis_kaynaktan_gelirse_tek_ogedir_ve_ikisini_de_soyler()
    {
        using var r = new Rig();
        r.Notify("ikili", "permission_prompt", 0);
        r.At(1);
        r.Svc.External = new FixedExternalSessions(new[] { Ext("claude", "ikili", ExternalStates.WaitingForYou, "approve Edit: hesap.js", r.Dir, observed: T0.AddMinutes(1)) });
        var it = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Equal("session_permission", it.Reason);
        Assert.Equal("hook+agents", it.Source);
        Assert.Equal(Time.Iso(T0.AddMinutes(1)), it.ObservedAt);
        Assert.Contains("approve Edit", it.Detail);
        Assert.Equal(Time.Iso(T0), it.At); // durumun başladığı an kancadan
    }

    [Fact]
    public void Dis_kaynak_calisiyor_diyorsa_eski_izin_ve_takildi_isareti_gecersizdir()
    {
        using var r = new Rig();
        r.Notify("eski", "permission_prompt", 0);
        r.At(2);
        r.Svc.External = new FixedExternalSessions(new[] { Ext("claude", "eski", ExternalStates.Working, null, r.Dir, observed: T0.AddMinutes(2)) });
        Assert.Empty(r.Svc.Snapshot().Items);
        // çalışıyor bilgisi işaretten ESKİyse işaret geçerli kalır
        ((FixedExternalSessions)r.Svc.External).Rows[0] = Ext("claude", "eski", ExternalStates.Working, null, r.Dir, observed: T0.AddSeconds(-30));
        r.At(1);
        Assert.Single(r.Svc.Snapshot().Items);
    }

    [Fact]
    public void Bayat_dis_bilgi_kutuya_girmez_ve_kaynak_patlarsa_kutu_calisir()
    {
        using var r = new Rig();
        r.At(10);
        r.Svc.External = new FixedExternalSessions(new[] { Ext("claude", "bayat", ExternalStates.WaitingForYou, "x", r.Dir, observed: T0) }); // 10 dk eski
        Assert.Empty(r.Svc.Snapshot().Items);
        r.Svc.External = new ThrowingSource();
        Assert.Empty(r.Svc.Snapshot().Items);
    }

    private sealed class ThrowingSource : IExternalSessionSource { public IReadOnlyList<ExternalSession> Snapshot() => throw new InvalidOperationException("kaynak yok"); }

    [Fact]
    public void Dis_kaynak_bitirdi_ya_da_belirsiz_oturumu_kesin_seni_bekliyor_diye_yukseltir()
    {
        using var r = new Rig();
        r.Notify("yuks", "idle_prompt", 0);
        r.At(1);
        r.Svc.External = new FixedExternalSessions(new[] { Ext("claude", "yuks", ExternalStates.WaitingForYou, null, r.Dir, observed: T0.AddMinutes(1)) });
        var it = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Equal(AttentionKinds.Ask, it.Kind); Assert.Equal("session_waiting", it.Reason);
        Assert.NotEmpty(it.Actions);
    }

    [Fact]
    public void Dis_oturum_bekleme_baslangici_tur_boyu_sabit_kalir_ve_id_degismez()
    {
        using var r = new Rig();
        r.At(0);
        r.Svc.External = new FixedExternalSessions(new[] { Ext("claude", "sabit", ExternalStates.WaitingForYou, "x", r.Dir, observed: T0) });
        var first = Assert.Single(r.Svc.Snapshot().Items);
        r.At(1);
        ((FixedExternalSessions)r.Svc.External).Rows[0] = Ext("claude", "sabit", ExternalStates.WaitingForYou, "x", r.Dir, observed: T0.AddMinutes(1));
        var second = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(Time.Iso(T0.AddMinutes(1)), second.ObservedAt);
        // durum değişip yeniden bekleyince yeni öğedir
        ((FixedExternalSessions)r.Svc.External).Rows[0] = Ext("claude", "sabit", ExternalStates.Working, null, r.Dir, observed: T0.AddMinutes(2));
        r.At(2); Assert.Empty(r.Svc.Snapshot().Items);
        ((FixedExternalSessions)r.Svc.External).Rows[0] = Ext("claude", "sabit", ExternalStates.WaitingForYou, "y", r.Dir, observed: T0.AddMinutes(3));
        r.At(3);
        Assert.NotEqual(first.Id, Assert.Single(r.Svc.Snapshot().Items).Id);
    }

    [Fact]
    public void Profil_gorunumu_baglayicisi_state_ve_waitingFor_alanlarini_okur_bayat_gorunumu_yok_sayar()
    {
        JsonObject View(DateTime scanned) => new()
        {
            ["scannedAt"] = Time.Iso(scanned),
            ["live"] = new JsonArray(new JsonObject
            {
                ["projectPath"] = @"E:\proje\a",
                ["sessions"] = new JsonArray(
                    new JsonObject { ["tool"] = "claude", ["id"] = "p1", ["pid"] = 77, ["hwnd"] = 555, ["state"] = "seni-bekliyor", ["waitingFor"] = "izin", ["evidence"] = "surec" },
                    new JsonObject { ["tool"] = "codex", ["id"] = "p2" }),
            }),
        };
        var src = new ProfileExternalSessions(() => View(T0), TimeSpan.FromMinutes(3), () => T0.AddMinutes(1));
        var rows = src.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal(ExternalStates.WaitingForYou, rows[0].State); Assert.Equal("izin", rows[0].WaitingFor); Assert.Equal(77, rows[0].Pid); Assert.Equal(555L, rows[0].Hwnd);
        Assert.Equal(@"E:\proje\a", rows[0].ProjectPath);
        Assert.Equal(ExternalStates.Unknown, rows[1].State); // a6 henüz state yazmadıysa kutuya girmez
        Assert.Empty(new ProfileExternalSessions(() => View(T0), TimeSpan.FromMinutes(3), () => T0.AddMinutes(10)).Snapshot());
        Assert.Empty(new ProfileExternalSessions(() => throw new IOException("x")).Snapshot());
    }

    // ------------------------------------------------------------ 5. bitti iddiası: eylemler
    [Fact]
    public void Bitirdi_ogesinde_pencereye_git_ve_bu_isi_sina_eylemleri_vardir_olcut_yoksa_bunu_soyler()
    {
        using var r = new Rig();
        var sid = r.Notify("bitti-1", "idle_prompt", 0);
        r.AssistantSaid(sid, "Hesap işini bitirdim, testler geçti.", 1);
        r.At(2);
        var it = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Equal("session_done", it.Reason);
        // E1: ölçüt yok: düğme listede yok, Söz'ün notu ayrı alanda, yardımcının sözü (Detail) boş kalır.
        Assert.Equal(new[] { "go" }, it.Actions.Select(a => a.Id).ToArray());
        Assert.Equal("Bu projede neyle sınayacağımı bilmiyorum.", it.SozNotu);
        Assert.Null(it.Detail);
        Assert.False(it.CanProve); Assert.Equal("no_checks", it.CanProveReason);

        r.Work.AddCheck(r.ProjectId, null, "command", "Testler geçsin", new JsonObject { ["cmd"] = "node -e 0" }, 60, enabled: true);
        var withCheck = Assert.Single(r.Svc.Snapshot().Items);
        Assert.Null(withCheck.SozNotu); Assert.Null(withCheck.Detail);
        Assert.True(withCheck.CanProve); Assert.Null(withCheck.CanProveReason);
        Assert.Equal(new[] { "go", "prove" }, withCheck.Actions.Select(a => a.Id).ToArray());
        Assert.Equal($"/api/sessions/{sid}/prove", withCheck.Actions[1].Path);
    }

    [Fact]
    public void Sina_eylemi_olcut_yoksa_baslatmaz_varsa_kendi_kanit_motorunu_o_klasorde_calistirir()
    {
        using var r = new Rig();
        var sid = r.Notify("sinanacak", "idle_prompt", 0);
        var acts = new SessionActions(r.Env.Svc.Db, r.Svc);

        var none = acts.Prove(sid, null, r.Work);
        Assert.False(none.Ok); Assert.Equal(409, none.Status); Assert.Contains("neyle sınayacağımı bilmiyorum", none.Message);
        Assert.Empty(r.Work.Runs(r.ProjectId, null));
        Assert.Equal(404, acts.Prove(99999, null, r.Work).Status);

        r.Work.AddCheck(r.ProjectId, null, "command", "Küçük deneme", new JsonObject { ["cmd"] = "node -e 0" }, 60, enabled: true);
        var ok = acts.Prove(sid, null, r.Work);
        Assert.True(ok.Ok); Assert.Equal(202, ok.Status); Assert.Contains("1 kontrolle sınama başladı", ok.Message);
        var runId = (long)ok.Data!.GetType().GetProperty("runId")!.GetValue(ok.Data)!;
        // kayıt oturuma bağlı, tetik "elle"
        using (var c = r.Env.Svc.Db.Open())
        {
            Assert.Equal(sid, c.ScalarLong("SELECT session_id FROM proof_runs WHERE id=@i", null, ("@i", runId)));
            Assert.Equal("manual", c.Scalar("SELECT trigger FROM proof_runs WHERE id=@i", null, ("@i", runId)) as string);
        }
        // sürerken ikincisi reddedilir ya da bitmiştir; bitene kadar bekle
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var c = r.Env.Svc.Db.Open();
            if (c.Scalar("SELECT verdict FROM proof_runs WHERE id=@i", null, ("@i", runId)) is string) break;
            Thread.Sleep(200);
        }
        using var c2 = r.Env.Svc.Db.Open();
        Assert.NotNull(c2.Scalar("SELECT verdict FROM proof_runs WHERE id=@i", null, ("@i", runId)) as string);
    }

    [Fact]
    public void Sina_eylemi_projesi_bilinmeyen_oturumda_acik_hata_verir()
    {
        using var r = new Rig();
        long sid;
        using (var c = r.Env.Svc.Db.Open())
        {
            c.Exec("INSERT INTO sessions(tool, external_id, kind, turn_ended) VALUES('claude','projesiz','human',1)");
            sid = c.ScalarLong("SELECT id FROM sessions WHERE external_id='projesiz'");
        }
        var o = new SessionActions(r.Env.Svc.Db, r.Svc).Prove(sid, null, r.Work);
        Assert.Equal(409, o.Status); Assert.Contains("hangi projede olduğunu bilmiyorum", o.Message);
    }

    [Fact]
    public void Pencereye_git_oturum_kimligiyle_pencereyi_bulur_bulamazsa_ya_da_masaustu_yoksa_anlasilir_hata_doner()
    {
        using var r = new Rig();
        var sid = r.Notify("pencere", "permission_prompt", 0);
        var acts = new SessionActions(r.Env.Svc.Db, r.Svc);
        var oldFocus = SessionActions.Focuser; var oldDesk = SessionActions.HasDesktop;
        try
        {
            long? gotHwnd = null; int? gotPid = null; string? gotPath = null;
            SessionActions.HasDesktop = () => true;
            SessionActions.Focuser = q => { gotHwnd = q.Hwnd; gotPid = q.Pid; gotPath = q.ProjectPath; return new FocusResult(true, 9001, TabSelected: true, Hint: "ipucu"); };
            r.At(1);
            // canlı kayıt yok: bulamadım
            var none = acts.Focus(sid, null);
            Assert.False(none.Ok); Assert.Equal(404, none.Status); Assert.Contains("açık penceresini bulamadım", none.Message);
            // canlı kayıt var: pid ile odaklanır
            r.Svc.External = new FixedExternalSessions(new[] { Ext("claude", "pencere", ExternalStates.WaitingForYou, null, r.Dir, pid: 31337, observed: T0.AddMinutes(1)) });
            var ok = acts.Focus(sid, null);
            Assert.True(ok.Ok); Assert.Equal(200, ok.Status); Assert.Equal(31337, gotPid);
            Assert.Equal(r.Dir, gotPath); // proje klasörü eşleştiriciye geçer
            Assert.Contains("\"tabSelected\":true", System.Text.Json.JsonSerializer.Serialize(ok.Data)); // sekme seçimi ve ipucu yanıta taşınır
            Assert.Contains("\"hint\":\"ipucu\"", System.Text.Json.JsonSerializer.Serialize(ok.Data));
            // öğe kimliğiyle de aynı
            var item = Assert.Single(r.Svc.Snapshot().Items);
            gotPid = null;
            Assert.True(acts.Focus(null, item.Id).Ok); Assert.Equal(31337, gotPid);
            // pencere öne alınamadı
            SessionActions.Focuser = q => new FocusResult(false, null);
            Assert.Equal(404, acts.Focus(sid, null).Status);
            // klasörü aç yedeği
            var opened = "";
            SessionActions.FolderOpener = p => { opened = p; return true; };
            ((FixedExternalSessions)r.Svc.External).Rows.Clear();
            var folder = acts.Focus(sid, null, openFolder: true);
            Assert.True(folder.Ok); Assert.Equal(r.Dir, opened);
            // masaüstü yok
            SessionActions.HasDesktop = () => false;
            var nodesk = acts.Focus(sid, null);
            Assert.Equal(501, nodesk.Status); Assert.Contains("Windows masaüstünde", nodesk.Message);
            // olmayan oturum
            Assert.Equal(404, acts.Focus(123456, null).Status);
        }
        finally { SessionActions.Focuser = oldFocus; SessionActions.HasDesktop = oldDesk; SessionActions.FolderOpener = p => Soz.Core.Profile.Win32ProcessInspector.OpenFolderInExplorer(p); }
    }

    // ------------------------------------------------------------ 3. kendiliğinden içe alma
    [Fact]
    public void Kayitlar_diskteyken_kendiliginden_icer_alinir_degisen_yoksa_hat_atlanir_sureler_olculur()
    {
        using var r = new Rig();
        r.ClaudeFile("oto-1", r.UserLine("oto-1", -6), r.ErrLine("oto-1", -5, "rate_limit", "limit"));
        r.At(0);
        Assert.Empty(r.Svc.Snapshot().Items); // içe alınmadan kutu boş (eski dert)
        using var auto = new AutoImporter(r.Env.Svc.Db, () => new ImportOptions { ClaudeRoot = r.Env.ClaudeRoot, CodexRoot = r.Env.CodexRoot });

        var first = auto.RunOnce();
        Assert.NotNull(first); Assert.True(first!.Changed); Assert.False(first.Idle);
        Assert.Contains(r.Svc.Snapshot().Items, i => i.Reason == "session_failed");

        var second = auto.RunOnce();
        Assert.True(second!.Idle); Assert.False(second.Changed); Assert.Equal(0, second.NewUtterances);
        Assert.Equal(2, auto.Runs); Assert.Equal(1, auto.ChangedRuns);
        Assert.True(auto.LastMs < 5000, $"boş tur {auto.LastMs} ms");

        // yeni dosya: yalnız o okunur
        r.ClaudeFile("oto-2", r.UserLine("oto-2", -4), r.AsstLine("oto-2", -3, "Bitti."));
        var third = auto.RunOnce();
        Assert.True(third!.Changed);
        Assert.Equal(1, third.Sources.Sum(s => s.FilesRead));
        Assert.True(third.Sources.Sum(s => s.FilesSkippedUnchanged) >= 1);
    }

    [Fact]
    public async Task Otomatik_icer_alma_dongusu_baslar_ve_durur_manuel_icer_alma_surerken_tur_atlanir()
    {
        using var r = new Rig();
        r.ClaudeFile("dongu-1", r.UserLine("dongu-1", -6), r.ErrLine("dongu-1", -5, "rate_limit", "limit"));
        var changed = new TaskCompletionSource<bool>();
        using var auto = new AutoImporter(r.Env.Svc.Db, () => new ImportOptions { ClaudeRoot = r.Env.ClaudeRoot, CodexRoot = r.Env.CodexRoot }, TimeSpan.FromMilliseconds(100), _ => changed.TrySetResult(true));
        auto.Start(TimeSpan.FromMilliseconds(10));
        Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(30_000)));
        await Task.Delay(400);
        Assert.True(auto.Runs >= 2, $"tur sayısı {auto.Runs}");
        Assert.Equal(1, auto.ChangedRuns);
        Assert.Null(auto.LastError);
    }

    // ------------------------------------------------------------ 6. kuyruk dürüstlüğü
    [Fact]
    public void Kuyruk_teslim_yolu_olmayan_yardimciya_mesaj_almaz_nedenini_soyler()
    {
        using var r = new Rig();
        long codexSid, claudeSid;
        using (var c = r.Env.Svc.Db.Open())
        {
            c.Exec("INSERT INTO sessions(tool, external_id, project_id, kind, turn_ended) VALUES('codex','cx-1',@p,'human',1)", null, ("@p", r.ProjectId));
            codexSid = c.ScalarLong("SELECT id FROM sessions WHERE external_id='cx-1'");
        }
        var ex = Assert.Throws<SozException>(() => r.Work.Enqueue(null, codexSid, "şunu da yap"));
        Assert.Equal(409, ex.Status);
        Assert.Contains("Codex", ex.Message); Assert.Contains("yalnız Claude Code", ex.Message); Assert.Contains("penceresine yaz", ex.Message);
        Assert.DoesNotContain("kanca", ex.Message);
        Assert.Empty(r.Work.Queue(null, null, true)); // kuyruğa hiçbir şey alınmadı

        claudeSid = r.Notify("cl-1", "idle_prompt", 0);
        var q = r.Work.Enqueue(null, claudeSid, "şunu da yap");
        Assert.Equal(claudeSid, q.SessionId);
    }

    [Fact]
    public void Kuyruk_terminal_destegi_kurulu_degilse_mesaj_alinir_ama_ajan_gormeyecegini_soyler_kuruluysa_uyari_yok()
    {
        using var r = new Rig();
        var homeOld = QueueStore.UserHome;
        try
        {
            QueueStore.UserHome = () => Path.Combine(r.Env.Root, "ev"); // gerçek kullanıcı ayarına bakılmasın
            var q = r.Work.Enqueue(r.ProjectId, null, "merhaba");
            Assert.Contains("yardımcı onu görmeyecek", q.DeliveryNote);
            Assert.Contains("terminal desteği", q.DeliveryNote);
            Assert.DoesNotContain("hook", q.DeliveryNote, StringComparison.OrdinalIgnoreCase);

            HookInstaller.Install(Path.Combine(r.Dir, ".claude", "settings.json"), Path.Combine(r.Env.Root, "soz.exe"), r.Env.DataDir);
            Assert.Null(r.Work.Enqueue(r.ProjectId, null, "ikinci").DeliveryNote);
        }
        finally { QueueStore.UserHome = homeOld; }
    }

    // ------------------------------------------------------------ 7. metin sadeliği
    [Theory]
    [InlineData("Codex bu bilgisayarda komut çalıştıramadı (Windows kum havuzu kurulumu eksik).", "güvenli çalışma alanı")]
    [InlineData("sandbox kurulumu eksik", "güvenli çalışma alanı")]
    public void Kutudaki_metinlerde_teknik_soz_sadelesir(string raw, string expected)
    {
        var plain = AttentionBuilder.Plain(raw);
        Assert.Contains(expected, plain);
        Assert.DoesNotContain("kum havuzu", plain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sandbox", plain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eski_bitirdi_ogelerinden_yalniz_en_yeni_birkacı_kutuya_girer()
    {
        using var r = new Rig();
        for (var i = 0; i < AttentionBuilder.MaxDoneSessionItems + 5; i++) { var sid = r.Notify("d-" + i, "idle_prompt", i); r.AssistantSaid(sid, "İş tamamlandı.", i); }
        r.At(100);
        var done = r.Svc.Snapshot().Items.Where(x => x.Reason == "session_done").ToList();
        Assert.Equal(AttentionBuilder.MaxDoneSessionItems, done.Count);
        Assert.Contains(done, d => d.ExternalSessionId == "d-" + (AttentionBuilder.MaxDoneSessionItems + 4)); // en yeni var
        Assert.DoesNotContain(done, d => d.ExternalSessionId == "d-0"); // en eski düştü
    }

    // ------------------------------------------------------------ 8. yavaş kaynak kutuyu bekletmez
    private sealed class SlowSource(int ms) : IExternalSessionSource
    {
        public int Calls;
        public IReadOnlyList<ExternalSession> Snapshot()
        {
            Interlocked.Increment(ref Calls);
            Thread.Sleep(ms);
            return new[] { new ExternalSession("claude", "yavas", "C:/p", "t", null, null, ExternalStates.WaitingForYou, null, null, DateTime.UtcNow) };
        }
    }

    [Fact]
    public void Yavas_dis_kaynak_kutuyu_bekletmez_son_bilineni_verir()
    {
        var slow = new SlowSource(1200);
        var bg = new BackgroundExternalSessions(slow, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = bg.Snapshot();
        Assert.True(sw.ElapsedMilliseconds < 600, "ilk çağrı yavaş kaynağı beklememeli");
        Assert.Empty(first); // henüz yenilenmedi
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (bg.Refreshes == 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        sw.Restart();
        var second = bg.Snapshot();
        Assert.True(sw.ElapsedMilliseconds < 600);
        Assert.Single(second);
        Assert.Equal("yavas", second[0].SessionId);
    }
}
