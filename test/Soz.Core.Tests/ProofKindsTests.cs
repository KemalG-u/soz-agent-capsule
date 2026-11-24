using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// d2a: altı yeni yerleşik kanıt türü (file-range, doc-heading, diff-summary, local-address, table-recon, human-checklist), güven etiketi,
/// "Bitti'nin tanımı" öneri motoru ve ölçütlerin kayda bağlı olması. Her tür için geçti / kaldı / ölçülemedi ve kandırma denemeleri.
/// </summary>
public class ProofKindsTests
{
    private static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;
    private static CheckToRun Chk(string kind, string spec, long id = 1) => new(id, kind, "deneme", J(spec), 30);
    private static CheckOutcome Run(ProofRunner r, string kind, string spec) => r.Run(Chk(kind, spec), TimeSpan.FromSeconds(10));

    /// <summary>Proje klasörü env.Root\proje; kanıt klasörü proje dışında.</summary>
    private static (ProofRunner r, string dir) Rig(TestEnv env, Func<CheckToRun, IReadOnlySet<int>>? ticks = null)
    {
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(dir);
        return (new ProofRunner(dir, Path.Combine(env.Root, "kanit"), () => null, humanTicks: ticks), dir);
    }

    // ================= dosya var/yok + boyut =================
    [Fact]
    public void FileRange_GectiKaldiOlculemedi()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, dir) = Rig(env);
        File.WriteAllText(Path.Combine(dir, "a.txt"), new string('x', 500));

        Assert.Equal(ResultStatus.Passed, Run(r, "file-range", """{"path":"a.txt","minBytes":100,"maxBytes":1000}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "file-range", """{"path":"a.txt","minBytes":600}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "file-range", """{"path":"a.txt","maxBytes":100}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "file-range", """{"path":"yok.txt"}""").Status);
        Assert.Equal(ResultStatus.Passed, Run(r, "file-range", """{"path":"yok.txt","exists":false}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "file-range", """{"path":"a.txt","exists":false}""").Status);
        Assert.Equal(ResultStatus.Error, Run(r, "file-range", """{"path":""}""").Status);
    }

    [Fact]
    public void FileRange_Kandirma_BosDosya_YolKacisi_DevDosya()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, dir) = Rig(env);
        File.WriteAllBytes(Path.Combine(dir, "bos.txt"), Array.Empty<byte>());
        Assert.Equal(ResultStatus.Failed, Run(r, "file-range", """{"path":"bos.txt"}""").Status); // varsayılan en az 1 bayt: boş dosya "var" sayılmaz

        File.WriteAllText(Path.Combine(env.Root, "disarida.txt"), "gizli");
        var esc = Run(r, "file-range", """{"path":"../disarida.txt"}""");
        Assert.NotEqual(ResultStatus.Passed, esc.Status); // proje dışındaki dosya kanıt sayılmaz
        Assert.NotEqual(ResultStatus.Passed, Run(r, "file-range", J($$"""{"path":{{JsonValue.Create(Path.Combine(env.Root, "disarida.txt")).ToJsonString()}}}""").ToJsonString()).Status);
        Assert.NotEqual(ResultStatus.Passed, Run(r, "file-range", """{"path":"a.txt:gizli"}""").Status); // NTFS akış adı

        // 1 GB'lık (seyrek) dosya: okunmadan boyutu söylenir, takılmaz
        var big = Path.Combine(dir, "dev.bin");
        using (var fs = new FileStream(big, FileMode.Create)) fs.SetLength(1L << 30);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = Run(r, "file-range", """{"path":"dev.bin","maxBytes":1000000}""");
        Assert.Equal(ResultStatus.Failed, o.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "1 GB dosya okunmadan sonuç verilmeli");
        File.Delete(big);
    }

    // ================= belge başlığı =================
    private static string Docx(string path, params (string style, string text)[] paras)
    {
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""");
        foreach (var (style, text) in paras)
            sb.Append("<w:p>").Append(style.Length > 0 ? $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>" : "").Append($"<w:r><w:t>{text}</w:t></w:r></w:p>");
        sb.Append("</w:body></w:document>");
        var e = z.CreateEntry("word/document.xml");
        using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(sb.ToString());
        return path;
    }

    [Fact]
    public void DocHeading_Markdown_Html_Docx()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, dir) = Rig(env);
        File.WriteAllText(Path.Combine(dir, "r.md"), "# Rapor\n\nmetin\n\n## Kaynaklar\n- a\n\nBaşka Başlık\n-----------\n\n```\n# Kodun içindeki başlık değil\n```\n");
        Assert.Equal(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"r.md","heading":"Kaynaklar"}""").Status);
        Assert.Equal(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"r.md","heading":"kaynaklar"}""").Status);   // büyük/küçük harf önemsiz
        Assert.Equal(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"r.md","heading":"Başka Başlık"}""").Status); // altı çizili başlık
        Assert.Equal(ResultStatus.Failed, Run(r, "doc-heading", """{"path":"r.md","heading":"Sonuç"}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "doc-heading", """{"path":"r.md","heading":"Kodun içindeki başlık değil"}""").Status); // kod bloğu başlık değildir

        File.WriteAllText(Path.Combine(dir, "s.html"), "<html><body><h2>Özet</h2><p>Özet</p></body></html>");
        Assert.Equal(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"s.html","heading":"Özet"}""").Status);

        Docx(Path.Combine(dir, "d.docx"), ("Heading1", "Giriş"), ("", "Normal paragraf Sonuç"));
        Assert.Equal(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"d.docx","heading":"Giriş"}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "doc-heading", """{"path":"d.docx","heading":"Sonuç"}""").Status); // başlık stilinde değil
        Assert.Equal(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"d.docx","text":"Normal paragraf"}""").Status);
    }

    [Fact]
    public void DocHeading_Kandirma_YanlisKodlama_Ikili_YolKacisi_Yok()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, dir) = Rig(env);
        // Windows-1254 (eski Türkçe kodlama) dosyada "Çözüm" başlığı: yanlış okunursa bozuk harf çıkar
        var enc = CodePagesEncodingProvider.Instance.GetEncoding(1254)!;
        File.WriteAllBytes(Path.Combine(dir, "eski.md"), enc.GetBytes("# Çözüm\n\nişte\n"));
        Assert.Equal(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"eski.md","heading":"Çözüm"}""").Status);

        File.WriteAllBytes(Path.Combine(dir, "ikili.md"), new byte[] { 0x89, 0x50, 0x4e, 0x47, 0, 0, 0, 0x0d, 0x0a, 0, 0, 0xff, 0xfe });
        Assert.NotEqual(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"ikili.md","heading":"x"}""").Status);

        File.WriteAllBytes(Path.Combine(dir, "bos.md"), Array.Empty<byte>());
        Assert.NotEqual(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"bos.md","heading":"x"}""").Status);

        File.WriteAllText(Path.Combine(env.Root, "disarida.md"), "# Gizli");
        Assert.NotEqual(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"../disarida.md","heading":"Gizli"}""").Status);
        Assert.NotEqual(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"yok.md","heading":"x"}""").Status); // dosya yok: geçmez

        var big = Path.Combine(dir, "dev.md");
        using (var fs = new FileStream(big, FileMode.Create)) fs.SetLength(1L << 30);
        Assert.NotEqual(ResultStatus.Passed, Run(r, "doc-heading", """{"path":"dev.md","heading":"x"}""").Status); // 1 GB belge belleğe alınmaz
        File.Delete(big);
    }

    // ================= tablo mutabakatı =================
    [Fact]
    public void Table_GectiKaldiOlculemedi()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, dir) = Rig(env);
        File.WriteAllText(Path.Combine(dir, "t.csv"), "ad,tutar,not\nali,10,a\nveli,20.5,\n\"ay, se\",5,c\n");
        Assert.Equal(ResultStatus.Passed, Run(r, "table-recon", """{"path":"t.csv","expectRows":3}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "table-recon", """{"path":"t.csv","expectRows":4}""").Status);
        Assert.Equal(ResultStatus.Passed, Run(r, "table-recon", """{"path":"t.csv","sumColumn":"tutar","expectSum":35.5}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "table-recon", """{"path":"t.csv","sumColumn":"tutar","expectSum":36}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "table-recon", """{"path":"t.csv","notEmpty":["not"]}""").Status);
        Assert.Equal(ResultStatus.Passed, Run(r, "table-recon", """{"path":"t.csv","notEmpty":["tutar"]}""").Status);

        // Türkçe yazım: noktalı virgül ayraç, virgül ondalık, nokta binlik
        File.WriteAllText(Path.Combine(dir, "tr.csv"), "ad;tutar\nali;1.234,50\nveli;765,50\n");
        Assert.Equal(ResultStatus.Passed, Run(r, "table-recon", """{"path":"tr.csv","sumColumn":"tutar","expectSum":2000,"decimal":","}""").Status);

        // ölçülecek hiçbir şey yok: "geçti" değil, ölçülemedi
        File.WriteAllText(Path.Combine(dir, "bos.csv"), "ad,tutar\n");
        var o = Run(r, "table-recon", """{"path":"bos.csv","notEmpty":["tutar"]}""");
        Assert.Equal(ResultStatus.Unchecked, o.Status);
        Assert.Equal(ProofStore.Reasons.NothingMeasured, o.ReasonCode);
    }

    [Fact]
    public void Table_Kandirma_BelirsizSayi_SayiOlmayan_BozukSatir_YolKacisi()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, dir) = Rig(env);
        File.WriteAllText(Path.Combine(dir, "b.csv"), "ad,tutar\nali,1.234\nveli,5\n"); // "1.234": bin mi ondalık mı belirsiz
        Assert.Equal(ResultStatus.Error, Run(r, "table-recon", """{"path":"b.csv","sumColumn":"tutar","expectSum":1239}""").Status);

        File.WriteAllText(Path.Combine(dir, "n.csv"), "ad,tutar\nali,on\nveli,5\n");
        Assert.Equal(ResultStatus.Error, Run(r, "table-recon", """{"path":"n.csv","sumColumn":"tutar","expectSum":5}""").Status); // sayı olmayanı sessizce atlayıp 5 demez

        File.WriteAllText(Path.Combine(dir, "k.csv"), "a,b\n1,2\n3\n4,5,6\n");
        Assert.Equal(ResultStatus.Failed, Run(r, "table-recon", """{"path":"k.csv","expectColumns":2}""").Status); // bozuk satırlar

        File.WriteAllBytes(Path.Combine(dir, "z.csv"), Array.Empty<byte>());
        Assert.Equal(ResultStatus.Failed, Run(r, "table-recon", """{"path":"z.csv","expectRows":3}""").Status); // boş dosya 3 satırlı tablo sayılmaz
        File.WriteAllBytes(Path.Combine(dir, "i.csv"), new byte[] { 0, 1, 2, 0, 0xff, 0xfe, 0, 0 });
        Assert.NotEqual(ResultStatus.Passed, Run(r, "table-recon", """{"path":"i.csv","expectRows":1}""").Status);

        File.WriteAllText(Path.Combine(env.Root, "disarida.csv"), "a\n1\n");
        Assert.NotEqual(ResultStatus.Passed, Run(r, "table-recon", """{"path":"../disarida.csv","expectRows":1}""").Status);
    }

    // ================= adres (yalnız kendi bilgisayarı) =================
    [Fact]
    public void LocalAddress_YerelAdres_Gecer_Kalir_DisAdresReddedilir()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, _) = Rig(env);
        using var srv = new ProofFrozenTests.MiniServer("Merhaba Söz");
        Assert.Equal(ResultStatus.Passed, Run(r, "local-address", $$"""{"url":"{{srv.Url}}"}""").Status);
        Assert.Equal(ResultStatus.Passed, Run(r, "local-address", $$"""{"url":"{{srv.Url}}","contains":"SÖZ"}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "local-address", $$"""{"url":"{{srv.Url}}","contains":"yok"}""").Status);
        Assert.Equal(ResultStatus.Failed, Run(r, "local-address", $$"""{"url":"{{srv.Url}}","expectStatus":404}""").Status);

        // dış adres: ağa HİÇ çıkılmaz, Error
        foreach (var bad in new[] { "http://example.com/", "https://8.8.8.8/", "http://192.168.1.5/", "http://" + "user:pw@127.0.0.1/", "ftp://127.0.0.1/", "http://127.0.0.1.evil.com/" })
            Assert.Equal(ResultStatus.Error, Run(r, "local-address", $$"""{"url":"{{bad}}"}""").Status);
        // kişi bilerek yazarsa izin verilir (yalnız o adı)
        Assert.Null(CheckStoreValidate("local-address", """{"url":"http://sunucum.local/","allowedHost":"sunucum.local"}"""));
        Assert.NotNull(CheckStoreValidate("local-address", """{"url":"http://baska.local/","allowedHost":"sunucum.local"}"""));
    }

    private static string? CheckStoreValidate(string kind, string spec) => CheckStore.Validate(kind, J(spec));

    [Fact]
    public void LocalAddress_YonlendirmeDisariGiderse_Kalir()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, _) = Rig(env);
        using var red = new ProofFrozenTests.MiniServer("", 302, "http://example.com/yonlendirildi");
        var o = Run(r, "local-address", $$"""{"url":"{{red.Url}}"}""");
        Assert.Equal(ResultStatus.Failed, o.Status);
        Assert.Contains("yönlendir", o.OutputTail);
        using var ok = new ProofFrozenTests.MiniServer("tamam");
        using var red2 = new ProofFrozenTests.MiniServer("", 302, ok.Url);
        Assert.Equal(ResultStatus.Passed, Run(r, "local-address", $$"""{"url":"{{red2.Url}}"}""").Status); // yerelden yerele yönlendirme izlenir
    }

    // ================= insan onay listesi =================
    [Fact]
    public void HumanChecklist_IsaretYokken_Bekler_Hepsiyse_GectiAmaDEtiketli()
    {
        using var env = new TestEnv(copySamples: false);
        var set = new HashSet<int>();
        var (r, _) = Rig(env, _ => set);
        var spec = """{"items":["Çalıştırıp baktım","Rakamlar doğru"]}""";
        var o = Run(r, "human-checklist", spec);
        Assert.Equal(ResultStatus.AwaitingHuman, o.Status);
        Assert.NotEqual(ResultStatus.Passed, o.Status);
        set.Add(0);
        Assert.Equal(ResultStatus.AwaitingHuman, Run(r, "human-checklist", spec).Status);
        set.Add(1);
        o = Run(r, "human-checklist", spec);
        Assert.Equal(ResultStatus.Passed, o.Status);
        Assert.Contains("Sen onayladın", o.OutputTail);

        Assert.Equal("D", Confidence.Of("human-checklist", ResultStatus.Passed));
        Assert.Equal(Confidence.HumanAccepted, Confidence.SourceOf("human-checklist", ResultStatus.Passed));
        Assert.Equal(Confidence.NoEvidence, Confidence.SourceOf("human-checklist", ResultStatus.AwaitingHuman));

        // hükümde bekleyen onay "geçti" olmaz
        var (verdict, _) = ProofStore.Judge(new[] { o with { Status = ResultStatus.AwaitingHuman } });
        Assert.Equal(Verdicts.Unchecked, verdict);
        // boş ya da çok uzun liste reddedilir
        Assert.NotNull(CheckStoreValidate("human-checklist", """{"items":[]}"""));
    }

    [Fact]
    public void HumanChecklist_IsaretOlayDefterinden_IseBagli_AjanCagrisiKapali()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "Bir işi incele");
        var chk = w.AddCheckFromTemplate(pid, job.Id, "human-list", J("""{"items":"Birinci madde\nİkinci madde"}"""), null, true);
        Assert.Equal("human-checklist", chk.Kind);
        using (var cs = env.Svc.Db.Open()) JobStore.SetState(cs, job.Id, JobStates.Running, pid); // ajan işe başladı: klasör görüntüsü ve ölçüt özeti alınır

        using var c = env.Svc.Db.Open();
        var st = HumanChecklistStore.Get(c, chk.Id, job.Id);
        Assert.Equal(2, st.Waiting); Assert.False(st.AllTicked);
        st = HumanChecklistStore.Tick(c, chk.Id, job.Id, 0, true);
        Assert.Equal(1, st.Waiting);
        st = HumanChecklistStore.Tick(c, chk.Id, job.Id, 1, true);
        Assert.True(st.AllTicked);
        st = HumanChecklistStore.Tick(c, chk.Id, job.Id, 1, false);
        Assert.Equal(1, st.Waiting); // işaret geri alınabilir
        Assert.Throws<SozException>(() => HumanChecklistStore.Tick(c, chk.Id, job.Id, 5, true)); // olmayan madde

        // tam koşu: bekleyen onay "kanıtlanmadı", hepsi işaretlenince "geçti" ve güven D, kaynak insan
        var run = w.RunProof(pid, job.Id);
        Assert.NotEqual(Verdicts.Passed, run.Verdict);
        Assert.Equal(ResultStatus.AwaitingHuman, run.Results!.Single().Status);
        HumanChecklistStore.Tick(c, chk.Id, job.Id, 1, true);
        run = w.RunProof(pid, job.Id);
        Assert.True(run.Verdict == Verdicts.Passed, $"{run.Verdict}: {run.Summary} | {run.ReasonCode} {run.ReasonText}");
        var res = run.Results!.Single();
        Assert.Equal("D", res.Confidence); Assert.Equal(Confidence.HumanAccepted, res.Source);
        Assert.Equal("D", run.Confidence!.Level); Assert.True(run.Confidence.HumanAccepted);

        // ajan bu uçları çağıramaz (kabul yalnız insandan)
        Assert.False(Soz.Server.CallerGate.AgentMayWrite("POST", $"/api/jobs/{job.Id}/checklist/{chk.Id}/tick"));
        Assert.False(Soz.Server.CallerGate.AgentMayWrite("POST", "/api/done/accept"));
    }

    // ================= fark özeti (iş başı görüntüsü) =================
    [Fact]
    public void DiffSummary_IsBasiGoruntusu_BeklenenKumeIcinde_Gecer_Disinda_Kalir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(Path.Combine(dir, "src")); Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "src", "a.cs"), "1"); File.WriteAllText(Path.Combine(dir, "docs", "d.md"), "1");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);

        // iş + ölçüt yokken önceki durum bilinmez: ölçülemedi
        var proje = w.AddCheckFromTemplate(pid, null, "diff-only", J("""{"allowed":"src/**"}"""), null, true);
        var o0 = w.RunProof(pid, null); // işsiz koşu: önceki durum yok
        Assert.NotEqual(Verdicts.Passed, o0.Verdict);
        Assert.Equal(ResultStatus.Unchecked, o0.Results!.Single().Status);
        w.PatchCheck(proje.Id, enabled: false);

        // iş başlarken görüntü alınır, sonra yalnız src değişir: geçer
        var job = w.CreateJob(pid, "bir");
        var chk = w.AddCheckFromTemplate(pid, job.Id, "diff-only", J("""{"allowed":"src/**"}"""), null, true);
        using (var cs = env.Svc.Db.Open()) JobStore.SetState(cs, job.Id, JobStates.Running, pid); // ajan işe başladı: klasör görüntüsü + fark özeti için önceki durum alınır
        File.WriteAllText(Path.Combine(dir, "src", "a.cs"), "değişti-uzun"); File.WriteAllText(Path.Combine(dir, "src", "yeni.cs"), "x");
        var ok = w.RunProof(pid, job.Id);
        Assert.True(ok.Verdict == Verdicts.Passed, $"{ok.Verdict}: {ok.Summary} | {ok.ReasonCode} {ok.ReasonText}");
        Assert.Equal("B", ok.Results!.Single().Confidence); // dosya listesi karşılaştırması: dolaylı

        // beklenmeyen yerde değişiklik: kalır, dosya adı söylenir
        File.WriteAllText(Path.Combine(dir, "docs", "d.md"), "başka şey");
        var bad = w.RunProof(pid, job.Id);
        Assert.Equal(Verdicts.Failed, bad.Verdict);
        Assert.Contains("docs/d.md", bad.Results!.Single().OutputTail);
        Assert.NotNull(chk);
    }

    // ================= kayıt: bilinmeyen tür, yeni araç maliyeti =================
    [Fact]
    public void KayitDefteri_BilinmeyenTur_AslaGecmez_VeHerKayitliTurunOlcucusuVar()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, _) = Rig(env);
        var o = Run(r, "uydurma-tur", """{}""");
        Assert.Equal(ResultStatus.Error, o.Status);
        Assert.Contains("Bilinmeyen kontrol türü", o.OutputTail);
        Assert.NotNull(CheckStoreValidate("uydurma-tur", """{}"""));

        foreach (var k in new[] { "command", "file", "url", "screenshot", "file-range", "doc-heading", "diff-summary", "local-address", "table-recon", "human-checklist" })
            Assert.NotNull(MeasurerRegistry.Find(k));
        Assert.Equal(10, MeasurerRegistry.Kinds.Count);
        Assert.Equal("A", MeasurerRegistry.Find("command")!.Level); // eski dört türün düzeyleri sabit
        Assert.Equal("B", MeasurerRegistry.Find("url")!.Level);
        Assert.Equal("B", MeasurerRegistry.Find("screenshot")!.Level);
    }

    private sealed class FakeMeasurer : MeasurerBase
    {
        public override string Kind => "sahte-uclu";
        public override string Level => Confidence.B;
        public override string? Validate(JsonObject spec) => null;
        public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout) => Passed(c, "sahte ölçüm");
        public override (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root) => ("sahte", new JsonObject());
    }

    [Fact]
    public void YeniArac_Maliyeti_BirSinifBirKayit_KodaDokunmadanCalisir()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, _) = Rig(env);
        MeasurerRegistry.Register(new FakeMeasurer());
        try
        {
            var o = Run(r, "sahte-uclu", "{}");
            Assert.Equal(ResultStatus.Passed, o.Status);
            Assert.Equal("B", Confidence.Of("sahte-uclu", ResultStatus.Passed));
            Assert.Null(CheckStoreValidate("sahte-uclu", "{}"));
        }
        finally { MeasurerRegistry.Unregister("sahte-uclu"); }
        Assert.Equal(ResultStatus.Error, Run(r, "sahte-uclu", "{}").Status);
    }

    // ================= güven etiketi =================
    [Fact]
    public void GuvenEtiketi_OlculenAyriAjanBeyaniAyri()
    {
        Assert.Equal("A", Confidence.Of("command", ResultStatus.Passed));
        Assert.Equal("B", Confidence.Of("url", ResultStatus.Passed));
        Assert.Equal("D", Confidence.Of("command", ResultStatus.Unchecked)); // emin olunamayan ölçüm güven taşımaz
        Assert.Equal("D", Confidence.Of("command", ResultStatus.Error));
        Assert.Equal("ölçüldü", Confidence.Label("A")); Assert.Equal("dolaylı", Confidence.Label("B")); Assert.Equal("yardımcı beyanı", Confidence.Label("C")); Assert.Equal("deneme sonucu yok", Confidence.Label("D"));
        Assert.Equal("D", Confidence.Weakest(new[] { ("command", ResultStatus.Passed), ("human-checklist", ResultStatus.Passed) })); // en zayıf halka belirler
        Assert.Equal("B", Confidence.Weakest(new[] { ("command", ResultStatus.Passed), ("url", ResultStatus.Passed) }));
        Assert.Equal("D", Confidence.Weakest(Array.Empty<(string, string)>()));
    }

    // ================= Bitti'nin tanımı: öneri motoru =================
    [Theory]
    [InlineData("kod")] [InlineData("yazi")] [InlineData("arastirma")] [InlineData("tablo")] [InlineData("tarayici")] [InlineData("dosya-duzeni")]
    public void Oneri_HerIsTurundeBosDonmez_SonMaddeInsanOnayListesi(string kind)
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "bosproje");
        Directory.CreateDirectory(dir); // tamamen boş klasör: en kötü durum
        var res = DoneDefinition.Suggest(dir, kind, null, false);
        Assert.NotEmpty(res.Proposals);
        var last = res.Proposals[^1];
        Assert.Equal("human-list", last.Template);
        Assert.Equal(DoneDefinition.HumanWho, last.Who);
        Assert.Equal("D", last.Level);
        Assert.True(last.Ready);
        Assert.All(res.Proposals, p => { Assert.False(string.IsNullOrWhiteSpace(p.Sentence)); Assert.False(string.IsNullOrWhiteSpace(p.Why)); });
        // klasör yokken ve tür bilinmezken de boş dönmez
        Assert.NotEmpty(DoneDefinition.Suggest(null, null, null, false).Proposals);
        Assert.NotEmpty(DoneDefinition.Suggest(Path.Combine(env.Root, "yok-klasor"), "uydurma", "", false).Proposals);
    }

    [Fact]
    public void Oneri_KlasordeGorulenlerdenOlculebilirMaddeler_VeGeriAlinamazMetin()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "satis.csv"), "a,b\n1,2\n");
        File.WriteAllText(Path.Combine(dir, "rapor.md"), "# x");
        var t = DoneDefinition.Suggest(dir, "tablo", null, false);
        var rows = t.Proposals.First(p => p.Template == "table-rows");
        Assert.Equal("satis.csv", rows.Values["path"]);       // tek tablo varsa yol önceden doldurulur
        Assert.False(rows.Ready);                              // satır sayısı kişiden gelmeli
        Assert.Equal(DoneDefinition.SozWho, rows.Who); Assert.Equal("A", rows.Level);
        Assert.Contains(t.Found, f => f.Contains("tablo"));

        var y = DoneDefinition.Suggest(dir, "yazi", "Müşteriye e-posta gönder ve faturayı kes", false);
                Assert.Contains("Geri alınamayacak", y.Proposals[^1].Values["items"]);
        // zararsız cümlede (satır, para birimi gibi sözcüklere takılmadan) ek madde yok
        var n = DoneDefinition.Suggest(dir, "yazi", "Satırları düzelt, parametre adlarına bak", false);
        Assert.DoesNotContain("Geri alınamayacak", n.Proposals[^1].Values["items"]);
        var e = DoneDefinition.Suggest(dir, "yazi", null, irreversible: true);
        Assert.Contains("Geri alınamayacak", e.Proposals[^1].Values["items"]);
    }

    [Fact]
    public void Oneri_KabulEdilince_OlcutOlurVeHatayaSeffafDuser()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "rapor.md"), "# Rapor\n## Kaynaklar\n");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var s = DoneDefinition.Suggest(dir, "arastirma", null, false);
        var accepted = new List<CheckDto>();
        foreach (var p in s.Proposals.Where(p => p.Ready))
        {
            var vals = new JsonObject(); foreach (var (k, v) in p.Values) vals[k] = v;
            accepted.Add(w.AddCheckFromTemplate(pid, null, p.Template, vals, null, true));
        }
        Assert.NotEmpty(accepted);
        Assert.Contains(accepted, c => c.Kind == "human-checklist");
        // eksik değerle (yol yok) kabul: açık hata, ölçüt eklenmez
        Assert.Throws<SozException>(() => w.AddCheckFromTemplate(pid, null, "table-rows", J("""{"rows":"3"}"""), null, true));
        // proje dışı yol: reddedilir
        Assert.Throws<SozException>(() => w.AddCheckFromTemplate(pid, null, "file-range", J("""{"path":"../disarida.txt"}"""), null, true));
    }

    [Fact]
    public void Sablonlar_EskiYediCumleAyniSirada_YeniOnYediSonra_KayitIleTutarli()
    {
        var ids = CheckTemplates.For(null).Templates.Select(t => t.Id).ToList();
        Assert.Equal(new[] { "tests", "build", "url-opens", "page-text", "file-exists", "file-text", "screenshot" }, ids.Take(7).ToArray());
        foreach (var id in new[] { "file-range", "file-absent", "doc-heading", "doc-text", "diff-only", "local-up", "table-rows", "table-sum", "table-filled", "human-list" })
            Assert.Contains(id, ids);
        // yeni cümlelerin hepsi "kullanılabilir" (komut aramaz)
        Assert.All(CheckTemplates.For(null).Templates.Skip(7), t => Assert.True(t.Available));
    }
}
