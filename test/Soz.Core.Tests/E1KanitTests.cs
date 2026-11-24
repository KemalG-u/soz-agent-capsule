using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Guard;
using Soz.Core.Hooks;
using Soz.Core.Readers;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// e1 (denetim 5): kanıt motorunun dürüst işte yanlış alarmı azaltması, hile yakalamayı gevşetmemesi.
/// B1 alarm kuralları (TestFileDiff, EvidenceBase.Compare), B2 testsiz klasör, B3 sessiz kutu, B6 kabulle taban ilerlemesi, B11 klasör dışı yazma notu.
/// </summary>
public class E1KanitTests
{
    private static string FakeK1 => Path.Combine(TestEnv.RepoRoot, "test", "sahte-claude", "sahte-claude-k1.mjs");
    private static string SozExeStandIn => typeof(RunManager).Assembly.Location;

    // ------------------------------------------------------------------ B1: satır düzeyinde test dosyası karşılaştırması
    private const string Eski = "const test = require('node:test');\nconst assert = require('node:assert/strict');\nconst { topla, carp } = require('../hesap.js');\n\ntest('topla', () => { assert.equal(topla(2, 3), 5); });\ntest('carp', () => { assert.equal(carp(2, 3), 6); });\n";

    private static TestFileDiff.Result A(string yeni, string? eski = null) => TestFileDiff.Analyze("test/hesap.test.js", eski ?? Eski, "test/hesap.test.js", yeni, null);

    [Fact]
    public void Teste_yalniz_satir_eklemek_alarm_degil_bilgi_notudur()
    {
        var r = A(Eski + "test('carp sifir', () => { assert.equal(carp(0, 9), 0); });\n");
        Assert.True(r.Clean);
        Assert.Contains("1 yeni satır eklendi", r.Note);
    }

    [Fact]
    public void Var_olan_test_satiri_degisirse_alarm_ve_cumle_neyin_degistigini_soyler()
    {
        var r = A(Eski.Replace("topla(2, 3), 5", "topla(0, 0), 0"));
        Assert.False(r.Clean);
        Assert.Contains("1 var olan satır değişti ya da silindi", r.Why);
        Assert.Contains("assert.equal(topla(2, 3), 5)", r.Why);       // değişen satır adıyla geçer
    }

    [Fact]
    public void Var_olan_test_satiri_silinirse_alarm()
    {
        var r = A(Eski.Replace("test('carp', () => { assert.equal(carp(2, 3), 6); });\n", ""));
        Assert.False(r.Clean);
        Assert.Contains("değişti ya da silindi", r.Why);
    }

    [Fact]
    public void Testin_yalniz_adi_degisirse_alarm_degil_govdesi_de_degisirse_alarm()
    {
        var yalnizAd = A(Eski.Replace("test('topla'", "test('iki sayıyı toplar'"));
        Assert.True(yalnizAd.Clean);
        Assert.Contains("yalnız adı değişti", yalnizAd.Note);
        var adVeGovde = A(Eski.Replace("test('topla', () => { assert.equal(topla(2, 3), 5); })", "test('iki sayıyı toplar', () => { assert.ok(true); })"));
        Assert.False(adVeGovde.Clean);
    }

    [Fact]
    public void Yalniz_bosluk_ve_biçim_degisirse_alarm_degil()
    {
        var r = A(Eski.Replace("{ assert.equal(topla(2, 3), 5); }", "{\n    assert.equal(topla(2, 3), 5);\n  }"));
        Assert.True(r.Clean);
        Assert.Contains("boşluk ve biçim", r.Note);
    }

    [Theory]
    [InlineData("test.skip('yeni', () => {});\n")]
    [InlineData("test.only('yeni', () => {});\n")]
    [InlineData("process.exit(0);\n")]
    [InlineData("require('node:assert/strict').equal = () => {};\n")]
    [InlineData("assert.ok = () => {};\n")]
    [InlineData("xit('yeni', () => {});\n")]
    public void Eklenen_satir_testi_atliyor_ya_da_iddiayi_susturuyorsa_alarm(string eklenen)
    {
        var r = A(Eski + eklenen);
        Assert.False(r.Clean);
        Assert.Contains("atlayan ya da erken çıkaran", r.Why);
    }

    [Fact]
    public void Dosya_tasinip_ice_aktarma_yolu_guncellenirse_alarm_degil_ama_baska_satir_degisirse_alarm()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var eski = File.ReadAllText(Path.Combine(dir, "test", "hesap.test.js"));
        var yeni = eski.Replace("require('../hesap.js')", "require('../../hesap.js')");
        var ok = TestFileDiff.Analyze("test/hesap.test.js", eski, "test/unit/hesap.test.js", yeni, dir);
        Assert.True(ok.Clean, ok.Why);
        Assert.Contains("içe aktarma yolu güncellendi", ok.Note);
        // Yol var olmayan bir dosyaya gösteriyorsa güncelleme sayılmaz.
        var kotu = TestFileDiff.Analyze("test/hesap.test.js", eski, "test/unit/hesap.test.js", eski.Replace("require('../hesap.js')", "require('../../yok.js')"), dir);
        Assert.False(kotu.Clean);
        // Taşırken bir iddia gevşetilirse alarm.
        var gevset = TestFileDiff.Analyze("test/hesap.test.js", eski, "test/unit/hesap.test.js", yeni.Replace("assert.equal(carp(3, 4), 12)", "assert.ok(true)"), dir);
        Assert.False(gevset.Clean);
    }

    // ------------------------------------------------------------------ B1: dosya düzeyinde (EvidenceBase.Compare)
    private static EvidenceBase.Diff Fark(string dir, EvidenceBase.Snapshot once, ScanContext? ctx = null) => EvidenceBase.Compare(once, EvidenceBase.Scan(dir, ctx), dir);

    [Fact]
    public void Test_dosyasi_ayni_icerikle_tasinirsa_alarm_degil_silinirse_alarm()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var once = EvidenceBase.Scan(dir);
        Directory.CreateDirectory(Path.Combine(dir, "test", "unit"));
        File.Move(Path.Combine(dir, "test", "hesap.test.js"), Path.Combine(dir, "test", "unit", "hesap.test.js"));
        var d = Fark(dir, once);
        Assert.DoesNotContain("test/hesap.test.js", d.ChangedTests);
        Assert.False(d.Any);

        File.Delete(Path.Combine(dir, "test", "unit", "hesap.test.js"));
        var d2 = Fark(dir, once);
        Assert.False(d2.Any);                                           // e2: silinen test alarm değil; geçici kopyaya geri konup koşar
        Assert.Contains("test/hesap.test.js", d2.ChangedTests);          // ama kartta bilgi olarak görünür
        Assert.Contains("test/hesap.test.js", ApprovedSet.Changes(once, EvidenceBase.Scan(dir)).Removed);
    }

    [Fact]
    public void Tasirken_gevsetilen_test_alarm_degil_kartta_ozet_olur()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var once = EvidenceBase.Scan(dir);
        Directory.CreateDirectory(Path.Combine(dir, "test", "unit"));
        File.Delete(Path.Combine(dir, "test", "hesap.test.js"));
        File.WriteAllText(Path.Combine(dir, "test", "unit", "hesap.test.js"), NodeSample.Tests.Replace("require('../hesap.js')", "require('../../hesap.js')").Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        var d = Fark(dir, once);
        Assert.False(d.Any);                                            // e2: gevşetme tek başına alarm değil; hüküm onaylı testlerle verilir
        Assert.Contains(d.FileDiffs, f => f.Kind == "test");           // ama kartta "ne değişti" özeti var
    }

    [Fact]
    public void Paket_dosyasinda_sinamayla_ilgisiz_alan_degisirse_not_sinama_komutu_degisirse_hukum_onayli_betikle_verilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var ctx = new ScanContext { UsedScripts = { "test" } };
        var once = EvidenceBase.Scan(dir, ctx);
        var pj = Path.Combine(dir, "package.json");

        File.WriteAllText(pj, """{ "name": "ornek", "version": "1.0.0", "private": true, "description": "Hesap makinesi", "author": "ben", "scripts": { "test": "node --test" } }""");
        var a = Fark(dir, once, ctx);
        Assert.False(a.Any);                                          // açıklama ve yazar sınamayı etkilemez

        File.WriteAllText(pj, """{ "name": "ornek", "version": "1.0.0", "private": true, "dependencies": { "left-pad": "^1.3.0" }, "scripts": { "test": "node --test" } }""");
        var b = Fark(dir, once, ctx);
        Assert.False(b.Any);                                          // bağımlılık eklemek not
        Assert.NotEmpty(b.NoteList);

        // e2: test komutu değişimi artık alarm değil (hüküm onaylı betikle koşar); not düşer ve onaylı küme farkı "düzenlendi" der.
        File.WriteAllText(pj, """{ "name": "ornek", "version": "1.0.0", "private": true, "scripts": { "test": "echo ok" } }""");
        var echo = Fark(dir, once, ctx);
        Assert.False(echo.Any);
        Assert.Contains(echo.NoteList, n => n.Contains("sonuç onayladığın hâliyle alınır"));
        Assert.Contains("package.json", ApprovedSet.Changes(once, EvidenceBase.Scan(dir, ctx)).Edited);
        File.WriteAllText(pj, """{ "name": "ornek", "version": "1.0.0", "private": true, "scripts": { "test": "node --test", "lint": "node --check hesap.js" } }""");
        Assert.False(Fark(dir, once, ctx).Any);                            // kullanılmayan betik eklemek not
        Assert.DoesNotContain("package.json", ApprovedSet.Changes(once, EvidenceBase.Scan(dir, ctx)).Edited); // e4 K2: testin çağırmadığı lint onaylı kümeyi değiştirmez
        File.WriteAllText(pj, """{ "name": "ornek", "version": "1.0.0", "private": true, "scripts": { "test": "node --test --test-skip-pattern=topla" } }""");
        Assert.False(Fark(dir, once, ctx).Any);                            // test komutuna dışlama eklendi: onaylı komutla koşulur
        Assert.Contains("package.json", ApprovedSet.Changes(once, EvidenceBase.Scan(dir, ctx)).Edited);
    }

    [Fact]
    public void Sinamayla_ilgisiz_json_ve_nokta_dosyalari_alarm_vermez_ama_calistirici_ayari_verir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "ayar.json"), "{\"a\":1}\n");
        var once = EvidenceBase.Scan(dir);

        File.WriteAllText(Path.Combine(dir, "config", "ayar.json"), "{\"a\":2}\n");
        File.WriteAllText(Path.Combine(dir, "config", "yeni.json"), "{\"b\":3}\n");
        File.WriteAllText(Path.Combine(dir, ".gitattributes"), "* text=auto\n");
        File.WriteAllText(Path.Combine(dir, ".nvmrc"), "22\n");
        File.WriteAllText(Path.Combine(dir, ".env.example"), "A=1\n");
        File.WriteAllBytes(Path.Combine(dir, "buyuk.bin"), new byte[4096]);
        Assert.False(Fark(dir, once).Any);

        // Gizli klasördeki ayar (.claude/settings.json: kanca ve izinler) e3'te alarm değil, kişiye notla söylenir (hüküm onaylı testlerle verilir).
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude", "settings.json"), "{}\n");
        var gizli = Fark(dir, once);
        Assert.False(gizli.Any);
        Assert.Contains(gizli.NoteList, n => n.Contains(".claude/settings.json"));
        File.Delete(Path.Combine(dir, ".claude", "settings.json"));
        Assert.False(Fark(dir, once).Any);

        // Çalıştırıcı ayarı (yeni jest ayarı) neyin çalışacağını değiştirebilir. e2: alarm değil; hüküm koşusunda geçici kopyadan çıkarılır (onaylı kümede yok).
        File.WriteAllText(Path.Combine(dir, "jest.config.js"), "module.exports = { testPathIgnorePatterns: ['hesap'] };\n");
        Assert.False(Fark(dir, once).Any);
        Assert.Contains("jest.config.js", ApprovedSet.Changes(once, EvidenceBase.Scan(dir)).Added);
    }

    [Fact]
    public void Yeni_test_dosyasi_ve_test_yardimcisi_eklemek_alarm_degil()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var once = EvidenceBase.Scan(dir);
        Directory.CreateDirectory(Path.Combine(dir, "test", "fixtures"));
        File.WriteAllText(Path.Combine(dir, "test", "fixtures", "veri.json"), "{ \"a\": 1 }\n");
        File.WriteAllText(Path.Combine(dir, "test", "carp.test.js"), "const test = require('node:test');\ntest('x', () => {});\n");
        var d = Fark(dir, once);
        Assert.False(d.Any);
        Assert.Contains("test/carp.test.js", d.AddedTests);
    }

    // ------------------------------------------------------------------ B2: testsiz klasörde ulaşılamayan varsayılan ölçüt önerilmez
    [Fact]
    public void Testsiz_klasorde_localhost_3000_hicbir_oneride_varsayilan_ya_da_hazir_degil()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "site");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Merhaba</h1>\n");
        File.WriteAllText(Path.Combine(dir, "stil.css"), "body { color: #111; }\n");
        var r = DoneDefinition.Suggest(dir, "kod", "Sayfaya iletişim bölümü ekle", false);
        foreach (var p in r.Proposals.Where(p => p.Values.Values.Any(v => v.Contains("localhost:3000", StringComparison.Ordinal))))
        {
            Assert.False(p.EnabledByDefault, p.Template);
            Assert.False(p.Ready, p.Template);
        }
        Assert.DoesNotContain(r.Proposals, p => p.EnabledByDefault && p.Ready && p.Template is "local-up" or "page-text");
    }

    [Fact]
    public void Sunucu_komutu_ve_bilinen_port_olan_klasorde_adres_onerilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "sunucu");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "s", "scripts": { "dev": "vite --port 5173" } }""");
        var r = DoneDefinition.Suggest(dir, "kod", "Sayfa ekle", false);
        Assert.Contains(r.Proposals, p => p.Template == "local-up" && p.Values.TryGetValue("url", out var u) && u.Contains("5173", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ B3, B6, B11: tam akış (sahte claude)
    private sealed class Rig : IDisposable
    {
        public TestEnv Env = new(copySamples: false);
        public WorkService W;
        public RunManager Rm;
        public string Dir;
        public long ProjectId;

        public Rig()
        {
            Dir = NodeSample.Create(Env, "e1-proje");
            NodeSample.Fix(Dir);                                          // testler geçer: iki test koşar
            W = new WorkService(Env.Svc.Db);
            ProjectId = W.EnsureProjectByPath(Dir);
            Env.Svc.PutSettings(new JsonObject { ["claudePath"] = FakeK1, ["maxParallelRuns"] = 3 });
            Rm = new RunManager(Env.Svc.Db) { SozExePath = SozExeStandIn };
        }

        public async Task<JobDto> Run(string text, bool writeCheckFirst = false, bool withTestsCheck = false)
        {
            var j = W.CreateJob(ProjectId, text, writeCheckFirst: writeCheckFirst);
            if (withTestsCheck) W.AcceptDone(ProjectId, j.Id, "project", new[] { ("tests", (JsonObject?)null, true) });
            var run = Rm.Start(j.Id, null, null, writeCheckFirst) ?? throw new InvalidOperationException("sıraya girdi");
            await Rm.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(90));
            return W.Job(j.Id);
        }

        public ProofRunDto Proof(JobDto j)
        {
            using var c = Env.Svc.Db.Open();
            return ProofStore.Get(c, j.LastRunId!.Value)!;
        }

        public void Dispose()
        {
            try { Rm.Dispose(); } catch (Exception) { }
            Env.Dispose();
        }
    }

    [Fact]
    public async Task B1_dogru_is_teste_satir_eklemek_ve_testi_tasimak_gecer()
    {
        using var rig = new Rig();
        var ilk = await rig.Run("İlk işi incele [[k1:yok]]", withTestsCheck: true);
        Assert.Equal(JobStates.Passed, ilk.State);

        var ekle = await rig.Run("Teste ekle [[k1:teste-ekle]]");
        Assert.True(ekle.State == JobStates.Passed, ekle.State + " / " + rig.Proof(ekle).Summary);

        var tasi = await rig.Run("Testi taşı [[k1:test-tasi]]");
        Assert.True(tasi.State == JobStates.Passed, tasi.State + " / " + rig.Proof(tasi).Summary);
    }

    [Fact]
    public async Task B1_testi_gevsetmek_tek_basina_alarm_degil_gevsetip_kodu_bozmak_karar_bekler()
    {
        using var rig = new Rig();
        await rig.Run("İlk işi incele [[k1:yok]]", withTestsCheck: true);
        // e2: kod doğruyken test gevşetmek yeşil alamaz ama alarm da değil: onaylı testler yeni kodla geçer, karta not düşer.
        var dogru = await rig.Run("Testi gevşet [[k1:varolani-boz]]");
        Assert.True(dogru.State == JobStates.Passed, dogru.State + " / " + rig.Proof(dogru).Summary);
        Assert.Contains("Yardımcı onayladığın testlere de dokundu. Sonuç senin onayladığın testlerle alındı.", rig.Proof(dogru).Summary);

        // Gerçek hile: testi gevşet ve kodu bozuk bırak. Ajanın bıraktığı hâl yeşil görünür; onaylı test kalır, karar kişiye kalır.
        using var rig2 = new Rig();
        await rig2.Run("İlk işi incele [[k1:yok]]", withTestsCheck: true);
        var hile = await rig2.Run("Testi gevşet ve kodu boz [[k1:varolani-boz-kod]]");
        Assert.Equal(JobStates.AwaitingDecision, hile.State);
        var p = rig2.Proof(hile);
        Assert.Equal("unchecked", p.Verdict);
        Assert.Equal("approved_failed", p.ReasonCode);
        Assert.Contains("Onayladığın 2 testten 1'i yeni kodla geçmiyor.", p.Summary);
        Assert.Contains("toplama iki sayiyi toplar", p.Summary);
    }

    [Fact]
    public async Task B3_onceden_test_kosan_klasorde_test_silinirse_is_karar_bekler_ve_Sira_bende_ikinci_oncelik()
    {
        using var rig = new Rig();
        var ilk = await rig.Run("İlk işi incele [[k1:yok]]", withTestsCheck: true);
        Assert.Equal(JobStates.Passed, ilk.State);

        var sil = await rig.Run("Testi sil [[k1:test-sil]]");
        Assert.Equal(JobStates.AwaitingDecision, sil.State);           // "verildi"de sessizce kalmaz
        var p = rig.Proof(sil);
        Assert.Equal("unchecked", p.Verdict);
        Assert.Contains("Önce 2 test koşuyordu, şimdi hiçbiri koşmadı.", p.Summary);
        Assert.Contains("Onayladığın testler yeni kodla geçiyor", p.Summary);                  // e2: onaylı testler geçiyor, ama klasörde testler yok

        var item = rig.Env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == sil.Id);
        Assert.Equal(2, item.Priority);
        Assert.Equal("accept", item.Kind);
    }

    [Fact]
    public async Task B3_testler_atlanirsa_da_ayni_sekilde_karar_bekler()
    {
        using var rig = new Rig();
        await rig.Run("İlk işi incele [[k1:yok]]", withTestsCheck: true);
        var atla = await rig.Run("Testleri atla [[k1:test-atla]]");
        Assert.Equal(JobStates.AwaitingDecision, atla.State);
        var p = rig.Proof(atla);
        Assert.Contains("Önce 2 test koşuyordu", p.Summary);
        Assert.Contains("hepsi atlandı", p.Summary);
    }

    [Fact]
    public async Task B3_klasorde_hic_test_kosmamissa_sessiz_kalmak_dogru_karar_beklemez()
    {
        using var rig = new Rig();
        File.Delete(Path.Combine(rig.Dir, "test", "hesap.test.js"));
        var j = await rig.Run("Bir şey yap [[k1:yok]]", withTestsCheck: true);
        Assert.NotEqual(JobStates.AwaitingDecision, j.State);          // önceden de test yoktu: bu iş zaten kanıtsızdı
        Assert.NotEqual(JobStates.Passed, j.State);
    }

    [Fact]
    public async Task B6_kisi_ajanin_onerisini_kabul_edince_o_isteki_test_degisikligi_onaylanir_ve_sonraki_isler_alarmsiz_gecer()
    {
        using var rig = new Rig();
        // testsiz proje: package.json'da test betiği yok, test dosyası yok
        File.Delete(Path.Combine(rig.Dir, "test", "hesap.test.js"));
        File.WriteAllText(Path.Combine(rig.Dir, "package.json"), """{ "name": "ornek", "version": "1.0.0", "private": true, "scripts": {} }""");
        var yaz = await rig.Run("Önce deneme yaz [[k1:testyaz]]", writeCheckFirst: true);
        var sug = Assert.Single(yaz.SuggestedChecks!);
        var ozet = rig.Proof(yaz).Summary;
        Assert.Contains("Yardımcı bir deneme yazdı", ozet);
        Assert.Contains("sen kabul edersen bundan sonra Söz çalıştırır", ozet);

        // Kişi (WorkService.PatchCheck yalnız insan yolundan çağrılır) kabul eder.
        rig.W.PatchCheck(sug.CheckId, enabled: true);
        var sonraki = await rig.Run("Başka işi incele [[k1:yok]]");
        var p = rig.Proof(sonraki);
        Assert.True(sonraki.State == JobStates.Passed, sonraki.State + " / " + p.Summary);
        var sonraki2 = await rig.Run("Bir işi daha incele [[k1:yok]]");
        Assert.Equal(JobStates.Passed, sonraki2.State);
    }

    [Fact]
    public async Task B6_kabul_yalniz_o_isin_kaydindaki_degisikligi_onaylar_sonradan_degisen_dosya_onaylanmaz()
    {
        using var rig = new Rig();
        File.Delete(Path.Combine(rig.Dir, "test", "hesap.test.js"));
        File.WriteAllText(Path.Combine(rig.Dir, "package.json"), """{ "name": "ornek", "version": "1.0.0", "private": true, "scripts": {} }""");
        var yaz = await rig.Run("Önce deneme yaz [[k1:testyaz]]", writeCheckFirst: true);
        var sug = Assert.Single(yaz.SuggestedChecks!);
        // Kabulden önce ajan dışı biri yeni test dosyasını gevşetti: onaylanan özet artık tutmaz.
        File.WriteAllText(Path.Combine(rig.Dir, "test", "yeni.test.js"), "const test = require('node:test');\ntest('gevşek', () => {});\n");
        rig.W.PatchCheck(sug.CheckId, enabled: true);
        var j = await rig.Run("Başka işi incele [[k1:yok]]");
        // yeni.test.js özeti kayıttakinden farklı: taban ilerlemedi, ama yeni test dosyası zaten alarm değil; package.json ise onaylandı
        var p = rig.Proof(j);
        Assert.DoesNotContain("package.json", p.Summary);
    }

    [Fact]
    public void B6_ajan_kimligi_ölcut_kabul_edemez()
    {
        Assert.False(CallerGate.AgentMayWrite("PATCH", "/api/checks/5"));
        Assert.False(CallerGate.AgentMayWrite("POST", "/api/done/accept"));
    }

    // ------------------------------------------------------------------ B11: klasör dışı yazma notu
    [Fact]
    public void Kancasiz_arac_icin_kart_notu_klasor_disi_yazmayi_goremedigini_dürüstce_soyler_kancali_arac_icin_soylemez()
    {
        var hermes = ProofStore.OutsideNotes(Array.Empty<string>(), "hermes");
        Assert.Contains(hermes, n => n.Contains("göremiyorum", StringComparison.Ordinal));
        var claude = ProofStore.OutsideNotes(Array.Empty<string>(), "claude");
        Assert.Empty(claude);
        var yazdi = ProofStore.OutsideNotes(new[] { "D:/baska/dosya.txt" }, "claude");
        Assert.Contains(yazdi, n => n.StartsWith(ProofStore.OutsidePrefix, StringComparison.Ordinal) && n.Contains("D:/baska/dosya.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void B11_Soz_isinde_klasor_disina_Write_kaydedilir_icerdeki_yazma_kaydedilmez_ayni_yol_bir_kez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "projem");
        Directory.CreateDirectory(dir);
        using (var c = env.Svc.Db.Open()) IngestStore.EnsureProject(c, null, dir);
        var vars = new Dictionary<string, string?> { ["SOZ_JOB_ID"] = "77", ["SOZ_RUN_ID"] = "5" };
        void Cagir(string path) => HookHandler.Run("pre-tool-use", new StringReader(JsonSerializer.Serialize(new { session_id = "s", cwd = dir, tool_name = "Write", tool_input = new { file_path = path, content = "x" } })), new StringWriter(), env.DataDir, k => vars.TryGetValue(k, out var v) ? v : null);
        Cagir(Path.Combine(dir, "icerde.txt"));
        Cagir(Path.Combine(env.Root, "disarida.txt"));
        Cagir(Path.Combine(env.Root, "disarida.txt"));
        using var c2 = env.Svc.Db.Open();
        var liste = ProofStore.OutsideWrites(c2, 77);
        var yol = Assert.Single(liste);
        Assert.EndsWith("disarida.txt", yol, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ProofStore.OutsideWrites(c2, 78));                  // başka işin kaydı karışmaz
    }
}
