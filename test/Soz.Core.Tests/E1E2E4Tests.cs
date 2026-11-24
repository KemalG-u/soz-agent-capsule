using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Attention;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// e1 (vizyon E1, E2, E4): (E2) durmuş ama bitirdiğine dair işareti olmayan oturum "bitti" görünmez; (E1) Söz'ün kendi notu yardımcının sözüne karışmaz ve
/// çalışmayacak "bu işi sına" düğmesi baştan söylenir; (E4) yardımcının "n dosya değiştirdim" beyanı ile Söz'ün klasörde bulduğu tek cümlede karşılaştırılır.
/// Hepsi sentetik kayıt ve sahte kanca girdisiyle.
/// </summary>
public class E1E2E4Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Rig : IDisposable
    {
        public readonly TestEnv Env = new(copySamples: false);
        public readonly AttentionService Svc;
        public readonly string Dir;
        public readonly WorkService Work;
        public readonly long ProjectId;
        public Rig()
        {
            Dir = NodeSample.Create(Env, "e1-proje");
            Work = new WorkService(Env.Svc.Db);
            ProjectId = Work.EnsureProjectByPath(Dir);
            Svc = new AttentionService(Env.Svc.Db);
        }
        public void At(int minutes) => Time.UtcNow = () => T0.AddMinutes(minutes);
        private void Hook(string name, object input) => HookHandler.Run(name, new StringReader(JsonSerializer.Serialize(input)), new StringWriter(), Env.DataDir, _ => null);
        /// <summary>Oturum durur (idle_prompt), yardımcının son sözü <paramref name="said"/> (null: kayıtta söz yok).</summary>
        public long Stopped(string ext, string? said, int minutes = 0)
        {
            At(minutes);
            Hook("user-prompt-submit", new { session_id = ext, cwd = Dir, prompt = "merhaba" });
            Hook("notification", new { session_id = ext, cwd = Dir, notification_type = "idle_prompt" });
            using var c = Env.Svc.Db.Open();
            var sid = c.ScalarLong("SELECT id FROM sessions WHERE external_id=@e", null, ("@e", ext));
            if (said is not null)
                c.Exec("UPDATE sessions SET last_assistant_text=@t, last_assistant_ts=@a, turn_ended=1 WHERE id=@id", null, ("@t", said), ("@a", Time.Iso(T0.AddMinutes(minutes + 1))), ("@id", sid));
            return sid;
        }
        public AttentionItem Item(long sid) { At(10); return Svc.Snapshot().Items.Single(i => i.SessionId == sid); }
        public void Dispose() => Env.Dispose();
    }

    // ------------------------------------------------------------ E2: durmak bitmek değildir
    [Fact]
    public void E2_plan_sunup_onay_bekleyen_oturum_bitti_gorunmez()
    {
        using var r = new Rig();
        var sid = r.Stopped("plan-1", "Planı hazırladım: önce hesap.js sonra testler. Onayını bekliyorum.");
        var it = r.Item(sid);
        Assert.Equal(AttentionKinds.Uncertain, it.Kind); Assert.Equal("session_turn_end", it.Reason);
        Assert.NotEqual(AttentionKinds.Done, it.Kind); Assert.NotEqual("session_done", it.Reason);
        Assert.Equal("«" + it.Title + "» durdu ve sırayı sana verdi. Bitirdi mi, senden bir şey mi bekliyor, bilmiyorum.", it.Sentence);
        Assert.False(it.Signal);          // belirsiz öğe kapsülü uyandırmaz
        Assert.Equal(5, it.Priority);
        Assert.Equal(new[] { "go" }, it.Actions.Select(a => a.Id).ToArray()); // "bu işi sına" yok: bitti denmedi
        Assert.Null(it.CanProve);
    }

    [Fact]
    public void E2_bitti_diyen_oturum_bitti_kalir_ve_sinanabilir()
    {
        using var r = new Rig();
        var sid = r.Stopped("bitti-2", "Hesap işini bitirdim. Tüm testler geçti.");
        var it = r.Item(sid);
        Assert.Equal(AttentionKinds.Done, it.Kind); Assert.Equal("session_done", it.Reason);
        Assert.Contains("işini bitirdi", it.Sentence);
    }

    [Theory]
    [InlineData("3 dosyayı değiştirdim, kalan 2 dosya için devam ediyorum.")]            // ara rapor
    [InlineData("Silmeden önce senin onayını bekliyorum.")]                              // soru işaretsiz onay isteği
    [InlineData("Birinci aşama tamamlandı. İkinci aşamaya geçiyorum.")]                  // bitiş sözü var ama iş sürüyor
    [InlineData("Hazır. Şimdilik bu kadar.")]                                           // gevşek söz, net bitiş yok
    public void E2_ara_rapor_ve_onay_istegi_bitti_sayilmaz(string said)
    {
        using var r = new Rig();
        var it = r.Item(r.Stopped("ara-" + Math.Abs(said.GetHashCode()), said));
        Assert.Equal("session_turn_end", it.Reason);
    }

    [Fact]
    public void E2_kayitta_soz_yoksa_ya_da_kullanici_son_soyleyendiyse_bitti_denmez()
    {
        using var r = new Rig();
        Assert.Equal("session_turn_end", r.Item(r.Stopped("yok-1", null)).Reason);
        // yardımcı "bitti" demiş ama kullanıcı ondan sonra yazmış: eski mesaj, işaret sayılmaz
        var sid = r.Stopped("eski-1", "İş tamamlandı.", 0);
        using (var c = r.Env.Svc.Db.Open()) c.Exec("UPDATE sessions SET last_user_ts=@u WHERE id=@id", null, ("@u", Time.Iso(T0.AddMinutes(5))), ("@id", sid));
        Assert.Equal("session_turn_end", r.Item(sid).Reason);
    }

    [Fact]
    public void E2_ayrim_tek_fonksiyonda_bool_ile_beslenebilir()
    {
        Assert.Equal((AttentionKinds.Done, "session_done"), TurnEnd.Classify(true));
        Assert.Equal((AttentionKinds.Uncertain, "session_turn_end"), TurnEnd.Classify(false));
        Assert.Equal((AttentionKinds.Uncertain, "session_turn_end"), TurnEnd.Classify(null));
        Assert.Null(TurnEnd.ClaimsDone(null)); Assert.Null(TurnEnd.ClaimsDone("  "));
        Assert.True(TurnEnd.ClaimsDone("Yaptım.\n\nİş bitti, testler geçti."));
        Assert.False(TurnEnd.ClaimsDone("Bitti mi? Bilmiyorum."));
    }

    [Fact]
    public void E2_durmus_oturumlardan_yalniz_en_yeni_birkaci_kutuya_girer()
    {
        using var r = new Rig();
        for (var i = 0; i < AttentionBuilder.MaxDoneSessionItems + 4; i++) r.Stopped("t-" + i, null, i);
        r.At(100);
        var turn = r.Svc.Snapshot().Items.Where(x => x.Reason == "session_turn_end").ToList();
        Assert.Equal(AttentionBuilder.MaxDoneSessionItems, turn.Count);
        Assert.DoesNotContain(turn, d => d.ExternalSessionId == "t-0");
    }

    // ------------------------------------------------------------ E1: Söz'ün notu yardımcının sözü değildir
    [Fact]
    public void E1_kontrolsuz_projede_not_ayri_alanda_ve_sina_dugmesi_yok()
    {
        using var r = new Rig();
        var sid = r.Stopped("e1-a", "İş bitti, testler geçti.");
        var it = r.Item(sid);
        Assert.Equal("session_done", it.Reason);
        Assert.Equal("Bu projede neyle sınayacağımı bilmiyorum.", it.SozNotu);
        Assert.Null(it.Detail);                                   // yardımcının sözü alanına ürünün cümlesi konmaz
        Assert.False(it.CanProve); Assert.Equal("no_checks", it.CanProveReason);
        Assert.DoesNotContain(it.Actions, a => a.Id == "prove");
        Assert.Contains(it.Actions, a => a.Id == "go");
    }

    [Fact]
    public void E1_kontrol_eklenince_not_kalkar_ve_sinanabilir_olur()
    {
        using var r = new Rig();
        var sid = r.Stopped("e1-b", "İş bitti, testler geçti.");
        r.Work.AddCheck(r.ProjectId, null, "command", "Testler geçsin", new JsonObject { ["cmd"] = "node -e 0" }, 60, enabled: true);
        var it = r.Item(sid);
        Assert.Null(it.SozNotu); Assert.Null(it.Detail); Assert.Null(it.CanProveReason);
        Assert.True(it.CanProve);
        Assert.Contains(it.Actions, a => a.Id == "prove" && a.Path == $"/api/sessions/{sid}/prove");
    }

    [Fact]
    public void E1_kontrolsuz_projede_sina_gercekten_409_doner_yani_gizlemek_dogru()
    {
        using var r = new Rig();
        var sid = r.Stopped("e1-c", "İş bitti, testler geçti.");
        var res = new SessionActions(r.Env.Svc.Db, r.Svc).Prove(sid, null, r.Work);
        Assert.False(res.Ok); Assert.Equal(409, res.Status);
        Assert.False(r.Item(sid).CanProve);
    }

    [Fact]
    public void E1_proje_bilinmeyen_oturumda_sina_yok_ve_neden_proje_yok()
    {
        using var r = new Rig();
        var sid = r.Stopped("e1-d", "İş bitti, testler geçti.");
        using (var c = r.Env.Svc.Db.Open()) c.Exec("UPDATE sessions SET project_id=NULL WHERE id=@id", null, ("@id", sid));
        var it = r.Item(sid);
        Assert.Equal("session_done", it.Reason);
        Assert.False(it.CanProve); Assert.Equal("no_project", it.CanProveReason);
        Assert.DoesNotContain(it.Actions, a => a.Id == "prove");
    }

    // ------------------------------------------------------------ E4: söyleyen ile kontrol eden yan yana
    [Fact]
    public void E4_cumle_uc_hali_ve_okunamayan_beyan()
    {
        Assert.Equal("Yardımcı 2 dosya değiştirdiğini söyledi. Ben klasörde değişiklik bulamadım.", ChangeClaim.Sentence(2, false));
        Assert.Equal("Yardımcı 2 dosya değiştirdiğini söyledi. Ben de klasörde değişiklik buldum.", ChangeClaim.Sentence(2, true));
        Assert.Equal("Yardımcı dosya değiştirdiğini söylemedi. Ben klasörde değişiklik buldum.", ChangeClaim.Sentence(0, true));
        Assert.Equal("Yardımcı 1 dosya değiştirdiğini söyledi. Ben izlenen dosyalarda değişiklik bulamadım.", ChangeClaim.Sentence(1, false, ignoredOnly: true));
        Assert.Null(ChangeClaim.Sentence(null, true));    // beyan okunamıyor: cümle kurulmaz
        Assert.Null(ChangeClaim.Sentence(null, false));
        Assert.Null(ChangeClaim.Sentence(2, null));       // bulgu bilinmiyor
        Assert.Null(ChangeClaim.Sentence(0, false));      // ikisi de "değişiklik yok": çelişki yok
    }

    private static void Line(Rig r, long jobId, string text, string? detail = null)
    {
        using var c = r.Env.Svc.Db.Open();
        c.Exec("INSERT INTO run_lines(run_id, job_id, at, kind, text, detail) VALUES(NULL,@j,@a,'progress',@t,@d)", null, ("@j", jobId), ("@a", Time.NowIso()), ("@t", text), ("@d", detail));
    }

    [Fact]
    public void E4_beyan_akis_satirlarindan_okunur_ayni_dosya_bir_sayilir_akis_yoksa_null()
    {
        using var r = new Rig();
        var j = r.Work.CreateJob(r.ProjectId, "Beyan");
        using (var c = r.Env.Svc.Db.Open()) Assert.Null(ChangeClaim.Read(c, j.Id));      // akış hiç yok
        Line(r, j.Id, "düşünüyor");
        using (var c = r.Env.Svc.Db.Open()) Assert.Equal(0, ChangeClaim.Read(c, j.Id));  // akış var, dosya değiştirme yok
        Line(r, j.Id, "2 dosyayı değiştirdi", "değişen: a.js, b.js");
        Line(r, j.Id, "a.js dosyasını değiştirdi");                                      // aynı dosya yine anıldı
        Line(r, j.Id, "c.js dosyasını değiştirdi");
        using (var c = r.Env.Svc.Db.Open()) Assert.Equal(3, ChangeClaim.Read(c, j.Id));
    }

    // Gerçek hüküm yolu: iş başlar, ajan klasörde bir şey yapar ya da yapmaz, Stop kancası kanıt kapısını koşar.
    private static string StopInput(string session, string cwd)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["session_id"] = session, ["transcript_path"] = "t.jsonl", ["cwd"] = cwd, ["hook_event_name"] = "Stop", ["stop_hook_active"] = false, ["last_assistant_message"] = "Bitti." });

    private (string verdict, string reason, string summary) Judge(Rig r, string tag, int? claimedFiles, Action? change)
    {
        NodeSample.Fix(r.Dir);
        r.Work.AddCheck(r.ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, enabled: true);
        var job = r.Work.CreateJob(r.ProjectId, "Hatayı düzelt: " + tag);
        r.Work.PatchJob(job.Id, JobStates.Given);
        HookHandler.Run("user-prompt-submit", new StringReader(JsonSerializer.Serialize(new { session_id = "e4-" + tag, cwd = r.Dir, prompt = job.PromptText })), new StringWriter(), r.Env.DataDir, _ => null);
        if (claimedFiles is { } n)
            Line(r, job.Id, n == 0 ? "düşünüyor" : n == 1 ? "hesap.js dosyasını değiştirdi" : $"{n} dosyayı değiştirdi", n >= 2 ? "değişen: hesap.js, notlar.txt" : null);
        change?.Invoke();
        HookHandler.Run("stop", new StringReader(StopInput("e4-" + tag, r.Dir)), new StringWriter(), r.Env.DataDir, _ => null);
        using var c = r.Env.Svc.Db.Open();
        return c.Query("SELECT verdict, COALESCE(reason_code,''), summary FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", x => (x.GetString(0), x.GetString(1), x.GetString(2)), null, ("@j", job.Id)).First();
    }

    [Fact]
    public void E4_yardimci_2_dosya_dedi_klasorde_fark_yok_cumle_hukumde_gorunur()
    {
        using var r = new Rig();
        var (verdict, reason, summary) = Judge(r, "a", 2, null);
        // C7: değişiklik isteyen iş, klasör aynı: artık "geçti" değil, "iş yapılmamış görünüyor" (kanıtsız).
        Assert.Equal("unchecked", verdict); Assert.Equal(ProofStore.Reasons.NoWork, reason);
        Assert.StartsWith("Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok. Bu iş yapılmamış görünüyor.", summary);
    }

    [Fact]
    public void E4_yardimci_2_dosya_dedi_klasorde_de_fark_var_cumle_ayni_fikirde()
    {
        using var r = new Rig();
        var (verdict, _, summary) = Judge(r, "b", 2, () => File.WriteAllText(Path.Combine(r.Dir, "hesap.js"), NodeSample.FixedLib + "// değişiklik\n"));
        Assert.Equal("passed", verdict);
        Assert.Contains("Yardımcı 2 dosya değiştirdiğini söyledi. Ben de klasörde değişiklik buldum.", summary);
        Assert.DoesNotContain("hiçbir dosyayı değiştirmedi", summary);
    }

    [Fact]
    public void E4_yardimci_bildirmedi_ama_klasor_degisti_ters_yon_ve_beyan_yoksa_cumle_yok()
    {
        using var r = new Rig();
        var (_, _, summary) = Judge(r, "c", 0, () => File.WriteAllText(Path.Combine(r.Dir, "hesap.js"), NodeSample.FixedLib + "// değişiklik\n"));
        Assert.Contains("Yardımcı dosya değiştirdiğini söylemedi. Ben klasörde değişiklik buldum.", summary);

        using var r2 = new Rig();
        var (_, _, plain) = Judge(r2, "d", null, () => File.WriteAllText(Path.Combine(r2.Dir, "hesap.js"), NodeSample.FixedLib + "// değişiklik\n"));
        Assert.DoesNotContain("Yardımcı", plain);   // akış hiç yok (terminal oturumu): beyan okunamaz, cümle kurulmaz
    }
}
