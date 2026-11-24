using System.Text.Json.Nodes;
using Soz.Core.Feed;
using Soz.Core.Guard;
using Soz.Core.Jobs;
using Soz.Core.Profile;
using Soz.Core.Queue;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>c2: kullanıcıya giden sunucu cümleleri sade Türkçe olmalı (komut adı, desen, araç adı, noktalı virgül yok).</summary>
public class SadeCumleC2Tests
{
    private const string Root = @"E:\proj";
    private const string Home = @"C:\Users\tester";
    private const string Temp = @"C:\Users\tester\AppData\Local\Temp";

    private static IReadOnlyList<GuardRule> Rules()
    {
        var list = new List<GuardRule>();
        long id = 1;
        foreach (var tid in new[] { "temkinli", "surec-oldurme", "genel-kurulum", "disk", "korunan-yollar" })
            foreach (var tr in GuardTemplates.Find(tid)!.Rules)
                list.Add(new GuardRule(id++, 1, tr.Kind, tr.Label, tr.Spec, tr.Action, true, null, "template", null));
        return list;
    }

    private static GuardDecision Eval(string tool, string key, string value)
        => GuardEngine.Evaluate(Rules(), new GuardInput(tool, new JsonObject { [key] = value }, Root, Root, Home, Temp));

    private sealed class FakeCmd(string? json, string? failure) : IClaudeAgentsCommand
    {
        public string? Run(out string? fail) { fail = failure; return json; }
    }

    // ---------------------------------------------------------------- Y-02
    [Theory]
    [InlineData(null, "komut-yok")]
    [InlineData(null, "zaman-asimi")]
    [InlineData(null, "eski-surum")]
    [InlineData("tanınmayan çıktı", null)]
    public void ClaudeAgents_yedek_yola_dusunce_kullanici_cumlesi_sade_teknik_neden_ayri(string? json, string? failure)
    {
        var r = ClaudeAgents.Read(new FakeCmd(json, failure), null);
        Assert.Equal("Claude'un açık pencerelerinin hepsini şu an göremiyorum.", r.Note);
        Assert.DoesNotContain("claude agents", r.Note);
        Assert.DoesNotContain(";", r.Note);
        Assert.DoesNotContain("süreç", r.Note);
        Assert.DoesNotContain("komut", r.Note);
        Assert.Contains("claude agents", r.Technical); // ham neden yalnız teknik alanda
    }

    [Fact]
    public void ClaudeAgents_komut_calisirsa_not_ve_teknik_alan_bos()
    {
        var r = ClaudeAgents.Read(new FakeCmd("[]", null), null);
        Assert.Null(r.Note);
        Assert.Null(r.Technical);
    }

    // ---------------------------------------------------------------- Y-03
    [Fact]
    public void Bekci_toplu_silme_cumlesi_desen_icermez()
    {
        var d = Eval("Bash", "command", "rm -rf eski-klasor");
        Assert.Equal("ask", d.Verdict);
        Assert.Equal("Bekçi soruyor: «Toplu silme önce sorulur» kuralı. «rm -rf eski-klasor» komutuyla klasörü içindekilerle birlikte silmeye çalıştı. Devam edilsin mi?", d.Reason);
        Assert.DoesNotContain("desen", d.Reason);
        Assert.DoesNotContain("--recursive", d.Reason);
    }

    [Fact]
    public void Bekci_dosya_yazma_cumlesi_arac_adi_icermez()
    {
        var d = Eval("Write", "file_path", @"E:\baska\a.txt");
        Assert.Equal("deny", d.Verdict);
        Assert.Contains("Yardımcı buraya dosya yazmaya çalıştı", d.Reason);
        Assert.DoesNotContain("Write", d.Reason);
        Assert.DoesNotContain("ile yazma", d.Reason);
        var e = Eval("Edit", "file_path", @"E:\baska\a.txt");
        Assert.Contains("Yardımcı burayı değiştirmeye çalıştı", e.Reason);
    }

    [Fact]
    public void Bekci_komut_hedefi_cumleleri_komut_adi_ve_bayrak_icermez()
    {
        var d = Eval("Bash", "command", @"rm -r E:\baska\klasor");
        Assert.Contains("Yardımcı burayı silmeye çalıştı", d.Reason);
        Assert.DoesNotContain("(silme)", d.Reason);
        var c = Eval("Bash", "command", @"curl -o E:\baska\a.zip http://x");
        Assert.Contains("Yardımcı buraya dosya indirmeye çalıştı", c.Reason);
        Assert.DoesNotContain("curl -o", c.Reason);
        var p = Eval("Write", "file_path", @"E:\proj\.env");
        Assert.Contains("korunan bir yol", p.Reason);
        Assert.DoesNotContain("**", p.Reason);
    }

    [Theory]
    [InlineData("git push --force", "değişiklikleri zorla göndermeye")]
    [InlineData("git reset --hard", "kaydedilmemiş değişiklikleri geri alıp silmeye")]
    [InlineData("npm publish", "yayın yapmaya")]
    [InlineData("taskkill /f /im node.exe", "çalışan bir programı zorla kapatmaya")]
    [InlineData("winget install x", "bilgisayara program kurmaya")]
    [InlineData("format d:", "diski silip biçimlendirmeye")]
    public void Bekci_komut_kurali_cumleleri_sade(string cmd, string phrase)
    {
        var d = Eval("Bash", "command", cmd);
        Assert.NotEqual("allow", d.Verdict);
        Assert.Contains(phrase, d.Reason);
        Assert.DoesNotContain("desen", d.Reason);
    }

    // ---------------------------------------------------------------- C-15 (sunucu tarafı: Ne oldu satırı)
    [Fact]
    public void Telefondan_kuyruga_birakilan_mesaj_Ne_oldu_listesinde_gorunur()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NodeSample.Create(env));
        using var c = env.Svc.Db.Open();
        QueueStore.Add(c, pid, null, "Hafta sonu da gönderilsin", "telefon");
        QueueStore.Add(c, pid, null, "Bilgisayardan yazılan", "panel");
        var page = EventFeed.Read(c, 50, null, null);
        Assert.Contains(page.Items, x => x.Sentence == "Telefondan yardımcıya bir mesaj bıraktın: «Hafta sonu da gönderilsin». Yardımcı durunca alacak.");
        Assert.Contains(page.Items, x => x.Sentence == "Yardımcıya iletilmek üzere kuyruğa bir mesaj bırakıldı.");
    }

    // ---------------------------------------------------------------- iş akışı: yardımcı hata cümlesi
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("hermes")]
    [InlineData("opencode")]
    public void Yardimci_hata_cumlesi_sade_ham_metin_icermez(string id)
    {
        var c = Soz.Core.Agents.AgentRegistry.Get(id);
        var s = c.FailureReason(1, c.CreateEventStream(), "boom: Error: connect ECONNREFUSED 127.0.0.1:1 at Object.<anonymous>");
        Assert.Equal("Yardımcı işi yarıda bırakıp durdu.", s);
        var lim = c.FailureReason(1, c.CreateEventStream(), "Usage limit reached, resets 5pm");
        Assert.DoesNotContain("Usage", lim);
        Assert.DoesNotContain("reset", lim);
    }
}
