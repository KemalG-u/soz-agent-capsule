using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Discovery;
using Soz.Core.Registry;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>d2d: keşif mekaniği. Sentetik veritabanı <c>_gecici\d2d\testler\</c> altında; gerçek veri açılmaz.</summary>
public sealed class DiscoveryDb : IDisposable
{
    public string Dir { get; }
    public SozDb Db { get; }
    public SqliteConnection C { get; }

    public DiscoveryDb(bool install = true)
    {
        Dir = Path.Combine(TestEnv.RepoRoot, "_gecici", "d2d", "testler", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Dir);
        // d2m: keşif tabloları v13 göçüyle kurulur. install:false, v12'ye kadar göç edilmiş (keşifsiz) veritabanı verir.
        if (install) Db = SozDb.OpenAndMigrate(Dir);
        else { Db = new SozDb(Dir); Db.MigrateTo(12); }
        C = Db.Open();
    }

    /// <summary>v13 adımının keşif şemasını elle uygular; tekrar çağrısı zararsız olmalı.</summary>
    public void Install()
    {
        using var tx = C.BeginTransaction(deferred: false);
        DiscoverySchema.Apply(C, tx);
        tx.Commit();
    }

    public static Moment M(int day, int hour = 10) { var l = new DateTime(2026, 3, 2, hour, 0, 0, DateTimeKind.Unspecified).AddDays(day); return new Moment(DateTime.SpecifyKind(l, DateTimeKind.Utc), l); }
    public static DiscoveryParams P => DiscoveryParams.FromDefaults(FixedCatalog.Get);

    public void Dispose()
    {
        C.Dispose();
        SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(Dir, recursive: true); return; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}

public class DiscoveryFormulaTests
{
    private static readonly double[] Tol = { 1e-6 };
    private static IEnumerable<double> Range(int n, double step, double start = 0) => Enumerable.Range(0, n).Select(i => start + i * step);

    // repo-4 frec.mjs: "D3 F | F^" sütunları (node çıktısı, 6 hane). F^ = F/(F+3).
    [Theory]
    [InlineData("A", 2.087025, 0.410264)]
    [InlineData("B", 13.255018, 0.815442)]
    [InlineData("C", 1.810660, 0.376385)]
    [InlineData("D", 0.951695, 0.240832)]
    [InlineData("E", 0.051271, 0.016803)]
    [InlineData("F", 4.600135, 0.605270)]
    [InlineData("G", 2.852480, 0.487397)]
    [InlineData("H", 68.298621, 0.957923)]
    public void FrecMjs_SekizSenaryo_AyniSayilariVerir(string s, double f, double hat)
    {
        var ages = s switch
        {
            "A" => new[] { 21.0, 20, 14, 3 },
            "B" => Range(40, 1.5).ToArray(),
            "C" => new[] { 7.0, 14, 21, 28 },
            "D" => new[] { 1.0 },
            "E" => new[] { 60.0 },
            "F" => Range(20, 1, 21).ToArray(),
            "G" => new[] { 0.1, 1, 2 },
            _ => Range(200, 0.2, 5).ToArray(),
        };
        var got = Frecency.FromAges(ages, 14);
        Assert.True(Math.Abs(f - got) < 2e-6, $"{f} != {got}");
        Assert.True(Math.Abs(hat - Frecency.D3Hat(got, 3)) < 2e-6);
    }

    // repo-4 oneri.mjs: düzeltilmiş formül, K=12, ağırlık 4/4, eşik 0,30. Sayılar kayıt defterinden gelir.
    [Theory]
    [InlineData("fatura (bu projede 4 kullanım)", 0.465, true)]
    [InlineData("kod-gözden-geçir (sabit yuvada)", 0.086, false)]
    [InlineData("haftalık rapor (pazartesi 10)", 0.430, true)]
    [InlineData("web-gezinti (başka projelerde)", 0.111, false)]
    [InlineData("tek kullanım dün, bu proje", 0.284, false)]
    [InlineData("playwright 3 kez yok sayıldı", 0.053, false)]
    public void OneriMjs_AltiOrnek_AyniPuanVeKarar(string name, double expected, bool suggest)
    {
        var p = DiscoveryDb.P;
        double F(params double[] a) => Frecency.FromAges(a, p.HalfLifeDays);
        var score = name switch
        {
            var n when n.StartsWith("fatura") => Frecency.Score(F(21, 20, 14, 3), F(21, 20, 14, 3), 0, 1, 0, p),
            var n when n.StartsWith("kod") => Frecency.Score(9.0, 0, 0, p.InSlotFactor, 0, p),
            var n when n.StartsWith("haftalık") => Frecency.Score(F(7, 14, 21, 28), 0, F(7, 14, 21, 28), 1, 0, p),
            var n when n.StartsWith("web") => Frecency.Score(1.5, 0, 0, 1, 0, p),
            var n when n.StartsWith("tek") => Frecency.Score(F(1), F(1), 0, 1, 0, p),
            _ => Frecency.Score(F(10, 9, 8, 6), F(10, 9, 8, 6), 0, 1, 0.9, p),
        };
        Assert.Equal(expected, score, 3);
        Assert.Equal(suggest, score >= p.Threshold);
    }

    [Fact]
    public void ArtimliSaklama_OlayListesiyleAyniF_Ve_AktifGunTatiliEskitmez()
    {
        var p = DiscoveryDb.P;
        // yaşlar 21,20,14,3 → olay anları (şimdiden geriye); artımlı: eskiden yeniye (F, tSon)
        double f = 0, t = 0; var first = true;
        foreach (var age in new[] { 21.0, 20, 14, 3 })
        {
            var at = 100 - age;
            f = first ? 1 : Frecency.Accumulate(f, t, at, p.HalfLifeDays);
            first = false; t = at;
        }
        Assert.Equal(Frecency.FromAges(new[] { 21.0, 20, 14, 3 }, p.HalfLifeDays), Frecency.Current(f, t, 100, p.HalfLifeDays), 9);
        // tatil: takvimde 17..21 gün önce, aktif gün sayacında 3..7 (14 gün tatil sayılmaz): 1,957 ve 3,913
        Assert.Equal(1.957, Frecency.FromAges(new[] { 17.0, 18, 19, 20, 21 }, 14), 3);
        Assert.Equal(3.913, Frecency.FromAges(new[] { 3.0, 4, 5, 6, 7 }, 14), 3);
        Assert.Equal(0, Frecency.Saturate(0, 12));
        Assert.Equal(1.0, Frecency.Decay(-5, 14)); // gelecek tarihli kullanım şişirilmez
    }

    [Fact]
    public void Sayilar_YalnizKayitDefterinden_VeEksikAyarSessizDegilAcikHata()
    {
        var p = DiscoveryDb.P;
        Assert.Equal((14.0, 4.0, 4.0, 12.0, 0.30, 0.2), (p.HalfLifeDays, p.ProjectWeight, p.TimeWeight, p.Saturation, p.Threshold, p.InSlotFactor));
        Assert.True(p.ShadowMode);
        Assert.Equal((3, 30.0, 0.3, 0.9, 30.0, 3), (p.MaxSuggestions, p.HitWindowMinutes, p.DismissStep, p.DismissMax, p.DismissHalfLifeDays, p.TimeBlockHours));
        var cat = FixedCatalog.Get;
        foreach (var k in DiscoveryParams.Keys) Assert.NotNull(cat.Get(k));
        Assert.All(DiscoveryParams.Keys.Select(k => cat.Get(k)!), e => { Assert.Equal(RegistryOwners.Fixed, e.Owner); Assert.Empty(e.Contract!.Missing()); });
        Assert.True(RegistryChecker.Check(cat).Ok);
        // Kayıt kümesi ayarı taşımıyorsa uydurma varsayılan yok: açık hata
        var empty = RegistryLoader.Load(Array.Empty<RegistrySource>(), Array.Empty<RegistrySource>());
        Assert.Throws<InvalidOperationException>(() => DiscoveryParams.FromDefaults(empty));
        // kayıt sınırı: kötü değer ayar doğrulamasında reddedilir
        Assert.NotNull(SozSettings.Validate(new JsonObject { [DiscoveryParams.KThreshold] = 1.5 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { [DiscoveryParams.KTimeBlock] = 5 }));
        Assert.Null(SozSettings.Validate(new JsonObject { [DiscoveryParams.KTimeBlock] = 6, [DiscoveryParams.KShadow] = false }));
    }

    [Fact]
    public void KayitDosyasi_KodDaSihirliSayiTutmaz_AyarKapsami()
    {
        // Puan hesabı sınıfları sayıları yalnız DiscoveryParams üzerinden alır: kaynakta 0,5^ dışında kayan nokta sabiti yok.
        var dir = Path.Combine(TestEnv.RepoRoot, "src", "Soz.Core", "Discovery");
        var src = File.ReadAllText(Path.Combine(dir, "Frecency.cs"));
        Assert.DoesNotMatch(@"[^\w.](12|14|30|0\.3|0\.9|0\.2)(\.0)?\b", Regex.Replace(src, @"//.*|///.*", ""));
    }
}

public class DiscoveryLayoutTests
{
    [Fact]
    public void TablolarKurulmadan_Kurulu_Degil_KurulunceVar_VeTekrarKurmakZararsiz()
    {
        using var t = new DiscoveryDb(install: false);
        Assert.False(DiscoverySchema.IsInstalled(t.C));
        var svc = new DiscoveryService(t.Db);
        Assert.False(svc.Installed());
        var ex = Assert.Throws<SozException>(() => svc.Layout());
        Assert.Equal(503, ex.Status);
        Assert.Equal("Keşif henüz kurulmadı.", ex.Message);
        Assert.Equal(503, Assert.Throws<SozException>(() => svc.RecordUsage("x", null, null)).Status);
        Assert.Equal(503, Assert.Throws<SozException>(() => svc.Suggestions(null, null)).Status);
        // arama tablo olmadan da çöker değil
        var s = svc.Search("tema", null, 5);
        Assert.False((bool)s["installed"]!);
        Assert.NotEmpty(s["items"]!.AsArray());
        t.Install(); t.Install();
        Assert.True(DiscoverySchema.IsInstalled(t.C));
        Assert.True(svc.Installed());
    }

    [Fact]
    public void YuvaYerlestir_Cakisma_Degistir_Bosalt_KomsuKaymaz()
    {
        using var t = new DiscoveryDb();
        var a = new SlotAddress(2, 3);
        var r = LayoutStore.Place(t.C, a, "skill:fatura");
        Assert.True(r.Ok);
        LayoutStore.Place(t.C, new SlotAddress(2, 4), "skill:rapor");
        var conflict = LayoutStore.Place(t.C, a, "skill:baska");
        Assert.False(conflict.Ok);
        Assert.Equal("skill:fatura", conflict.Occupant!.ObjectUid);
        Assert.Equal("skill:fatura", LayoutStore.Get(t.C, a)!.ObjectUid); // dolu yuva sessizce ezilmedi
        var rep = LayoutStore.Place(t.C, a, "skill:baska", replace: true);
        Assert.True(rep.Ok); Assert.Equal("skill:fatura", rep.Occupant!.ObjectUid);
        Assert.True(LayoutStore.Clear(t.C, a));
        Assert.False(LayoutStore.Clear(t.C, a));
        Assert.Equal("skill:rapor", LayoutStore.Get(t.C, new SlotAddress(2, 4))!.ObjectUid); // komşu yerinde
        Assert.Null(LayoutStore.Get(t.C, a));                                                   // boşluk kapatılmadı
        // aynı nesne iki yuvada olabilir
        LayoutStore.Place(t.C, new SlotAddress(0, 0), "skill:rapor");
        Assert.Equal(2, LayoutStore.All(t.C).Count(s => s.ObjectUid == "skill:rapor"));
        // geçersiz adres ve kimlik
        foreach (var bad in new[] { new SlotAddress(-1, 0), new SlotAddress(0, 8), new SlotAddress(8, 8), new SlotAddress(0, -3) })
            Assert.False(LayoutStore.Place(t.C, bad, "x").Ok);
        Assert.False(LayoutStore.Place(t.C, a, "").Ok);
        Assert.False(LayoutStore.Place(t.C, a, new string('x', 201)).Ok);
        Assert.False(LayoutStore.Clear(t.C, new SlotAddress(9, 9)));
        Assert.Null(Events.VerifyChain(t.C));
        Assert.True(t.C.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'discovery.slot.%'") >= 4);
    }

    [Fact]
    public void YuvaKapisi_DogrudanSqlYazmasiReddedilir_Ve_KapiHerZamanKapaliKalir()
    {
        using var t = new DiscoveryDb();
        LayoutStore.Place(t.C, new SlotAddress(1, 1), "a");
        var ex = Assert.Throws<SqliteException>(() => t.C.Exec("INSERT INTO discovery_slot(kind_dir,slot_dir,object_uid,source,placed_at) VALUES(3,3,'z','user','x')"));
        Assert.Contains("yuva yalniz kisi eyleminde degisir", ex.Message);
        Assert.Throws<SqliteException>(() => t.C.Exec("UPDATE discovery_slot SET object_uid='z'"));
        Assert.Throws<SqliteException>(() => t.C.Exec("DELETE FROM discovery_slot"));
        Assert.Equal(0, t.C.ScalarLong("SELECT open FROM discovery_guard"));
        // hata yarıda keserse kapı açık kalmaz (işlem geri alınır)
        t.C.Exec("CREATE TRIGGER trg_test_fail BEFORE INSERT ON event_log WHEN NEW.kind='discovery.slot.placed' BEGIN SELECT RAISE(ABORT,'deneme hatasi'); END");
        Assert.Throws<SqliteException>(() => LayoutStore.Place(t.C, new SlotAddress(5, 5), "b"));
        Assert.Equal(0, t.C.ScalarLong("SELECT open FROM discovery_guard"));
        Assert.Null(LayoutStore.Get(t.C, new SlotAddress(5, 5)));
    }

    [Fact]
    public void YuzYirmiDortUc_64YuvaDolu_VeBosUclar()
    {
        using var t = new DiscoveryDb();
        var svc = new DiscoveryService(t.Db);
        var empty = svc.Layout();
        Assert.Equal(0, (int)empty["filled"]!);
        Assert.Equal(8, empty["directions"]!.AsArray().Count);
        Assert.All(empty["directions"]!.AsArray(), d => { Assert.Equal(8, d!["slots"]!.AsArray().Count); Assert.All(d["slots"]!.AsArray(), s => Assert.Null(s!["objectUid"])); });
        for (var k = 0; k < 8; k++) for (var s = 0; s < 8; s++) Assert.True(LayoutStore.Place(t.C, new SlotAddress(k, s), $"obj:{k}-{s}").Ok);
        Assert.Equal(64, LayoutStore.All(t.C).Count);
        var full = svc.Layout();
        Assert.Equal(64, (int)full["filled"]!);
        Assert.Equal("obj:7-7", (string?)full["directions"]![7]!["slots"]![7]!["objectUid"]);
        Assert.Equal("obj:0-0", (string?)full["directions"]![0]!["slots"]![0]!["objectUid"]);
        // 65. yerleştirme yok: dolu yuvaya replace'siz yazılamaz, "hangisinin yerine" kişiye sorulur
        var ex = Assert.Throws<SozException>(() => svc.Place(3, 3, "obj:yeni", replace: false));
        Assert.Equal(409, ex.Status);
        Assert.Equal("obj:3-3", LayoutStore.Get(t.C, new SlotAddress(3, 3))!.ObjectUid);
        // uçlar: ilk ve son yuva boşaltılabilir, komşusu kalır
        Assert.True(LayoutStore.Clear(t.C, new SlotAddress(0, 0)));
        Assert.True(LayoutStore.Clear(t.C, new SlotAddress(7, 7)));
        Assert.Equal(62, LayoutStore.All(t.C).Count);
        Assert.Equal("obj:0-1", LayoutStore.Get(t.C, new SlotAddress(0, 1))!.ObjectUid);
    }

    [Fact]
    public void Yerlesim_HicbirKodYolundaKendiligindenDegismez_OzellikTesti()
    {
        using var t = new DiscoveryDb();
        var rnd = new Random(20261111);
        var objs = Enumerable.Range(0, 14).Select(i => $"obj:{i}").ToArray();
        var projects = new[] { "p-a", "p-b", "p-c", null };
        // 20 yuva doldur (kişi eylemi), sonra yerleşime dokunmayan her şeyi rastgele sırayla çalıştır
        for (var i = 0; i < 20; i++) LayoutStore.Place(t.C, new SlotAddress(rnd.Next(8), rnd.Next(8)), objs[rnd.Next(objs.Length)], replace: true);
        var before = LayoutStore.All(t.C);
        var eventsBefore = t.C.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'discovery.slot.%'");
        var items = objs.Select(o => DiscoveryItem.Of(o, rnd.Next(8), "Nesne " + o)).ToList();
        var p = DiscoveryDb.P;
        var day = 0;
        for (var step = 0; step < 400; step++)
        {
            day += rnd.Next(0, 3);
            var at = DiscoveryDb.M(day, rnd.Next(24));
            var pr = projects[rnd.Next(projects.Length)];
            var obj = objs[rnd.Next(objs.Length)];
            switch (rnd.Next(8))
            {
                case 0: case 1: case 2: UsageStore.Record(t.C, obj, pr, at, p); break;
                case 3: SuggestionService.Suggest(t.C, pr, at, rnd.Next(2) == 0 ? p : p with { ShadowMode = false, Threshold = 0.05 }); break;
                case 4: { var m = (DismissMode)rnd.Next(3); if (m == DismissMode.NeverHere && pr is null) m = DismissMode.Ignored; DismissStore.Dismiss(t.C, obj, pr, m, at, p); break; }
                case 5: DismissStore.Restore(t.C, obj, pr, (DismissMode)rnd.Next(3), at); break;
                case 6: SearchRanker.Rank("nes", items, _ => rnd.NextDouble()); ShadowLog.Stats(t.C, 30, at); break;
                default: UsageStore.Weights(t.C, pr, at, p with { HalfLifeDays = 1 + rnd.Next(100), TimeBlockHours = new[] { 1, 2, 3, 6, 12 }[rnd.Next(5)] }); break;
            }
        }
        Assert.Equal(before, LayoutStore.All(t.C));                      // her yuva birebir aynı (nesne, kaynak, damga)
        Assert.Equal(eventsBefore, t.C.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'discovery.slot.%'"));
        Assert.Equal(0, t.C.ScalarLong("SELECT open FROM discovery_guard"));
        Assert.Null(Events.VerifyChain(t.C));
    }

    [Fact]
    public void YuvaTablosunaYazanTekYer_LayoutStore()
    {
        var dir = Path.Combine(TestEnv.RepoRoot, "src", "Soz.Core", "Discovery");
        var write = new Regex(@"(INSERT\s+(OR\s+\w+\s+)?INTO|UPDATE|DELETE\s+FROM)\s+discovery_slot", RegexOptions.IgnoreCase);
        var offenders = Directory.GetFiles(dir, "*.cs").Where(f => Path.GetFileName(f) is not ("LayoutStore.cs" or "DiscoverySchema.cs")).Where(f => write.IsMatch(File.ReadAllText(f))).ToList();
        Assert.Empty(offenders);
        // öneri, sayaç, arama ve ölçüm sınıfları LayoutStore'un yazma yöntemlerini çağırmaz
        foreach (var f in new[] { "SuggestionService.cs", "UsageStore.cs", "DismissStore.cs", "SearchRanker.cs", "ShadowLog.cs", "Frecency.cs", "DiscoveryItems.cs" })
            Assert.DoesNotMatch(@"LayoutStore\.(Place|Clear)\(", File.ReadAllText(Path.Combine(dir, f)));
    }
}

public class DiscoveryUsageSuggestTests
{
    [Fact]
    public void Kullanim_UcKapsam_ProjesizdeProjeYazilmaz_AktifGunTatiliEskitmez()
    {
        using var t = new DiscoveryDb();
        var p = DiscoveryDb.P;
        UsageStore.Record(t.C, "o1", "pA", DiscoveryDb.M(0, 10), p);          // pazartesi 10-13 dilimi
        UsageStore.Record(t.C, "o1", null, DiscoveryDb.M(1, 10), p);
        Assert.Equal(4, t.C.ScalarLong("SELECT COUNT(*) FROM discovery_usage WHERE object_uid='o1' AND n>=1 AND (scope='global' OR (scope='project' AND scope_key='pA') OR (scope='time'))"));
        Assert.Equal(1, t.C.ScalarLong("SELECT COUNT(*) FROM discovery_usage WHERE scope='project'"));
        Assert.Equal(2, t.C.ScalarLong("SELECT n FROM discovery_usage WHERE scope='global'"));
        Assert.Equal("mon-09-12", ActiveDays.TimeKey(DiscoveryDb.M(0, 10).Local, 3));
        Assert.Equal("tue-09-12", ActiveDays.TimeKey(DiscoveryDb.M(1, 10).Local, 3));
        Assert.Equal("mon-23-24", ActiveDays.TimeKey(DiscoveryDb.M(0, 23).Local, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ActiveDays.TimeKey(DiscoveryDb.M(0).Local, 5));
        // 60 gün tatil: aktif gün sayacı aynı, F aynı
        var w0 = UsageStore.Weights(t.C, "pA", DiscoveryDb.M(1, 10), p)["o1"];
        var w60 = UsageStore.Weights(t.C, "pA", DiscoveryDb.M(61, 10), p)["o1"];
        Assert.Equal(w0.Fg, w60.Fg, 12);
        // dönünce ilk kullanım yalnız 1 aktif gün sayılır
        UsageStore.Record(t.C, "o1", "pA", DiscoveryDb.M(61, 10), p);
        Assert.Equal(3, t.C.ScalarLong("SELECT COUNT(*) FROM discovery_active_day"));
        var f = t.C.Query("SELECT f FROM discovery_usage WHERE scope='global'", r => r.GetDouble(0)).Single();
        Assert.Equal(Frecency.FromAges(new[] { 2.0, 1, 0 }, 14), f, 9);
        Assert.Throws<ArgumentException>(() => UsageStore.Record(t.C, " ", null, DiscoveryDb.M(0), p));
    }

    // fatura: 4 ardışık aktif gün, bu projede. kod-gözden-geçir: çok kullanılmış ama sabit yuvada.
    private static void Seed(DiscoveryDb t, DiscoveryParams p)
    {
        for (var d = 0; d < 4; d++) UsageStore.Record(t.C, "skill:fatura", "pA", DiscoveryDb.M(d, 15), p);
        for (var d = 0; d < 4; d++) for (var i = 0; i < 9; i++) UsageStore.Record(t.C, "skill:kod", null, DiscoveryDb.M(d, 15), p);
        UsageStore.Record(t.C, "skill:web", "pB", DiscoveryDb.M(3, 15), p);
        LayoutStore.Place(t.C, new SlotAddress(2, 0), "skill:kod");
    }

    [Fact]
    public void GolgeKip_Varsayilan_BosDonerAmaGunluğeYazar_IsabetOlculur()
    {
        using var t = new DiscoveryDb();
        var p = DiscoveryDb.P;
        Seed(t, p);
        var at = DiscoveryDb.M(3, 16);
        var slotsBefore = LayoutStore.All(t.C);
        var r = SuggestionService.Suggest(t.C, "pA", at, p);
        Assert.True(r.Shadow);
        Assert.Empty(r.Items);                                        // kişiye gösterilmez
        Assert.Contains(r.Computed, s => s.ObjectUid == "skill:fatura");
        Assert.DoesNotContain(r.Computed, s => s.ObjectUid == "skill:kod"); // yuvada: çarpan 0,2, eşik altı
        Assert.Equal(1, t.C.ScalarLong("SELECT COUNT(*) FROM discovery_batch WHERE shown=0"));
        Assert.Equal(r.Computed.Count, (int)t.C.ScalarLong("SELECT COUNT(*) FROM discovery_impression"));
        // kişi 10 dk sonra tam önerilecek şeyi seçti: isabet
        UsageStore.Record(t.C, "skill:fatura", "pA", DiscoveryDb.M(3, 16).Plus(10), p);
        var s = ShadowLog.Stats(t.C, 30, DiscoveryDb.M(3, 17));
        Assert.Equal((1, 1, 1, 1), (s.Batches, s.Resolved, s.Hits, s.HitsAtRank1));
        Assert.Equal(1.0, s.HitRate);
        // ikinci grup: kişi başka şey seçti → çözüldü ama isabet yok; hit oranı 0,5
        SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(3, 17).Plus(1), p);
        UsageStore.Record(t.C, "skill:web", "pA", DiscoveryDb.M(3, 17).Plus(5), p);
        s = ShadowLog.Stats(t.C, 30, DiscoveryDb.M(3, 18));
        Assert.Equal(2, s.ResolvedWithSuggestion);
        Assert.Equal(0.5, s.HitRate);
        // ölçüm süresi dışı: çözülmez
        SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(3, 18), p);
        UsageStore.Record(t.C, "skill:fatura", "pA", DiscoveryDb.M(3, 20), p);
        Assert.Equal(2, ShadowLog.Stats(t.C, 30, DiscoveryDb.M(3, 21)).Resolved);
        // başka projedeki kullanım pA'nın açık grubunu çözmez
        SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(3, 21), p);
        UsageStore.Record(t.C, "skill:fatura", "pB", DiscoveryDb.M(3, 21).Plus(1), p);
        Assert.Equal(2, ShadowLog.Stats(t.C, 30, DiscoveryDb.M(3, 22)).Resolved);
        Assert.Equal(slotsBefore, LayoutStore.All(t.C));
    }

    [Fact]
    public void GorunenKip_Sirali_EsikliVeSinirli_YuvadakiBastirilir()
    {
        using var t = new DiscoveryDb();
        var p = DiscoveryDb.P with { ShadowMode = false };
        Seed(t, p);
        var r = SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(3, 16), p);
        Assert.False(r.Shadow);
        Assert.Equal("skill:fatura", r.Items[0].ObjectUid);
        Assert.Equal("project", r.Items[0].ReasonCode);
        Assert.Equal("Bu projede 4 kez kullandın.", r.Items[0].Reason);
        Assert.Equal(1, t.C.ScalarLong("SELECT shown FROM discovery_batch"));
        // yuvadan çıkarınca (kişi eylemi) kod-gözden-geçir de önerilir; sınır uygulanır
        LayoutStore.Clear(t.C, new SlotAddress(2, 0));
        var r2 = SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(3, 16), p with { Threshold = 0.1 }, limit: 1);
        Assert.Single(r2.Items);
        var r3 = SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(3, 16), p with { Threshold = 0.1, MaxSuggestions = 2 }, limit: 3);
        Assert.Equal(2, r3.Items.Count);
        Assert.True(r3.Items[0].Score >= r3.Items[1].Score);
        // eşik 1: eşik yalnız dayanaksız (2 kullanımdan az) nesneyi eler; 2 gerçek kullanımı olan, yuvada olmayan nesne eşikten bağımsız önerilir (s1, B8)
        var esik1 = SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(3, 16), p with { Threshold = 1 }).Items;
        Assert.DoesNotContain(esik1, s => s.ObjectUid == "skill:web");
        Assert.Contains(esik1, s => s.ObjectUid == "skill:fatura");
        // proje başka: web başka projede kullanıldı, pA bağlamında fatura yine önde ama pB bağlamında farklı
        UsageStore.Record(t.C, "skill:web", "pB", DiscoveryDb.M(2, 15), p); // tek kullanım dayanak sayılmaz (b2): ikinci kullanım
        var rb = SuggestionService.Suggest(t.C, "pB", DiscoveryDb.M(3, 16), p with { Threshold = 0.05 });
        Assert.Contains(rb.Items, s => s.ObjectUid == "skill:web");
    }

    [Fact]
    public void BirDahaGosterme_YokSayCezasiTavanliVeGecer_KalicilarProjeyeGore_GeriAlinir()
    {
        using var t = new DiscoveryDb();
        var p = DiscoveryDb.P with { ShadowMode = false, Threshold = 0.1 };
        Seed(t, p);
        var at = DiscoveryDb.M(3, 16);
        var baseScore = SuggestionService.Suggest(t.C, "pA", at, p).Items.Single(s => s.ObjectUid == "skill:fatura").Score;
        for (var i = 0; i < 5; i++) DismissStore.Dismiss(t.C, "skill:fatura", "pA", DismissMode.Ignored, at, p);
        var seq = ActiveDays.Current(t.C, at.Local);
        Assert.Equal(0.9, DismissStore.Penalty(t.C, null, "skill:fatura", "pA", seq, p), 9);          // tavan 0,9
        var damped = SuggestionService.Suggest(t.C, "pA", at, p).Items.Where(s => s.ObjectUid == "skill:fatura").Select(s => s.Score).DefaultIfEmpty(0).Single();
        Assert.True(damped < baseScore * 0.11 + 1e-9);                                                   // x(1-0,9)
        // 30 aktif gün sonra ceza yarıya iner
        Assert.Equal(0.45, DismissStore.Penalty(t.C, null, "skill:fatura", "pA", seq + 30, p), 9);
        // başka projede ceza yok
        Assert.Equal(0, DismissStore.Penalty(t.C, null, "skill:fatura", "pB", seq, p));
        // kalıcı: burada önerme (yalnız pA), hiç önerme (hepsi)
        DismissStore.Restore(t.C, "skill:fatura", "pA", DismissMode.Ignored, at);
        DismissStore.Dismiss(t.C, "skill:fatura", "pA", DismissMode.NeverHere, at, p);
        Assert.DoesNotContain(SuggestionService.Suggest(t.C, "pA", at, p).Items, s => s.ObjectUid == "skill:fatura");
        Assert.Contains(SuggestionService.Suggest(t.C, "pB", at, p with { Threshold = 0.0001 }).Items, s => s.ObjectUid == "skill:fatura");
        DismissStore.Dismiss(t.C, "skill:fatura", null, DismissMode.Never, at, p);
        Assert.DoesNotContain(SuggestionService.Suggest(t.C, "pB", at, p with { Threshold = 0.0001 }).Items, s => s.ObjectUid == "skill:fatura");
        // kalıcılık: yeniden açılan bağlantıda da duruyor
        using (var c2 = t.Db.Open()) Assert.DoesNotContain(SuggestionService.Suggest(c2, "pA", at, p).Items, s => s.ObjectUid == "skill:fatura");
        // geri alma
        Assert.Equal(1, DismissStore.Restore(t.C, "skill:fatura", null, DismissMode.Never, at));
        Assert.Equal(0, DismissStore.Restore(t.C, "skill:fatura", null, DismissMode.Never, at));
        Assert.Equal(1, DismissStore.Restore(t.C, "skill:fatura", "pA", DismissMode.NeverHere, at));
        Assert.Contains(SuggestionService.Suggest(t.C, "pA", at, p).Items, s => s.ObjectUid == "skill:fatura");
        Assert.Throws<ArgumentException>(() => DismissStore.Dismiss(t.C, "x", null, DismissMode.NeverHere, at, p));
        Assert.Null(Events.VerifyChain(t.C));
    }
}

internal static class MomentExt
{
    public static Moment Plus(this Moment m, int minutes) => new(m.Utc.AddMinutes(minutes), m.Local.AddMinutes(minutes));
}

public class DiscoverySearchTests
{
    private static DiscoveryItem I(string uid, string title, string? desc = null, string[]? alias = null, string[]? kw = null) => DiscoveryItem.Of(uid, null, title, desc, alias, kw);
    private static string[] Titles(IEnumerable<SearchHit> h) => h.Select(x => x.Item.Title).ToArray();

    [Theory]
    [InlineData("isparta")]
    [InlineData("ISPARTA")]
    [InlineData("ısparta")]
    [InlineData("Isparta")]
    [InlineData("İSPARTA")]
    public void TurkceTuzaklari_BuyukKucukNoktaliNoktasizAyni(string q)
    {
        var items = new[] { I("1", "Isparta"), I("2", "İstanbul"), I("3", "Diyarbakır") };
        Assert.Equal(new[] { "Isparta" }, Titles(SearchRanker.Rank(q, items, _ => 0)));
    }

    [Theory]
    [InlineData("istanbul", "İstanbul")]
    [InlineData("ISTANBUL", "İstanbul")]
    [InlineData("İSTANBUL", "İstanbul")]
    [InlineData("isik", "Işık Raporu")]
    [InlineData("ışık", "Işık Raporu")]
    [InlineData("IŞIK", "Işık Raporu")]
    [InlineData("calisma", "Çalışma Planı")]
    [InlineData("ÇALIŞMA", "Çalışma Planı")]
    [InlineData("gunluk", "Günlük")]
    [InlineData("öğle", "Ogle yemegi")]
    public void TurkceKatlama_SoruVeHedefAyniFonksiyondan(string q, string expected)
    {
        var items = new[] { I("1", "İstanbul"), I("2", "Işık Raporu"), I("3", "Çalışma Planı"), I("4", "Günlük"), I("5", "Ogle yemegi"), I("6", "Başka") };
        Assert.Contains(expected, Titles(SearchRanker.Rank(q, items, _ => 0)));
    }

    [Fact]
    public void Basamaklar_TakmaAdTamAdTamOnekSozcukBasHarfIcindeAciklamaHarfDizisi()
    {
        var items = new[]
        {
            I("sub", "Kod Gozden Geciriciler", null, null, null),                 // harf dizisi "kgg"? (baş harf: kgg → acronym)
            I("alias", "Başka şey", null, new[] { "Rapor" }),                     // takma ad tam
            I("exact", "rapor"),                                                  // ad tam
            I("prefix", "Raporlama aracı"),                                       // ad öneki
            I("word", "Haftalık rapor"),                                          // sözcük öneki
            I("acr", "Rapor Oluştur Hızlı"),                                      // baş harfler "roh" için aşağıda ayrı sorgu
            I("contains", "Önrapor"),                                             // ad içinde
            I("desc", "Bambaşka", "haftalık rapor üretir"),                       // açıklama
            I("kw", "Diğer", null, null, new[] { "rapor" }),                      // anahtar sözcük
            I("none", "Hiç ilgisiz"),
        };
        var hits = SearchRanker.Rank("rapor", items, _ => 0);
        Assert.Equal(new[] { "alias", "exact", "prefix", "acr", "word", "contains", "kw", "desc" }, hits.Select(h => h.Item.Uid).ToArray());
        Assert.Equal(new[] { MatchTier.AliasExact, MatchTier.TitleExact, MatchTier.Prefix, MatchTier.Prefix }, hits.Take(4).Select(h => h.Tier));
        Assert.Equal(MatchTier.WordPrefix, hits[4].Tier);
        Assert.Equal(MatchTier.Contains, hits[5].Tier);
        Assert.Equal(MatchTier.Text, hits[6].Tier);
        Assert.Equal(MatchTier.Text, hits[7].Tier);
        Assert.Equal(MatchTier.Acronym, SearchRanker.Rank("kgg", items, _ => 0).Single(h => h.Item.Uid == "sub").Tier);
        Assert.Equal(MatchTier.Subsequence, SearchRanker.Rank("kzd", new[] { I("x", "Kod Gozden") }, _ => 0).Single().Tier);
        Assert.Empty(SearchRanker.Rank("kz", new[] { I("x", "Kod Gozden") }, _ => 0)); // 3 harften kısa sorguda harf dizisi yok
    }

    [Fact]
    public void AyniBasamakta_KullanimAgirligi_SonraKisaAd_SonraKimlik_BosSorguHicBirSeyiDusurmez()
    {
        var items = new[] { I("c", "Rapor C"), I("a", "Rapor A"), I("b", "Rapor B"), I("long", "Rapor uzun bir ad") };
        var w = new Dictionary<string, double> { ["b"] = 5, ["c"] = 2 };
        Assert.Equal(new[] { "b", "c", "a", "long" }, SearchRanker.Rank("rapor", items, u => w.GetValueOrDefault(u)).Select(h => h.Item.Uid).ToArray());
        var all = SearchRanker.Rank("", items, u => w.GetValueOrDefault(u));
        Assert.Equal(4, all.Count);
        Assert.All(all, h => Assert.Equal(MatchTier.Empty, h.Tier));
        Assert.Equal("b", all[0].Item.Uid);
        Assert.Equal(4, SearchRanker.Rank(null, items, _ => 0).Count);
        Assert.Equal(4, SearchRanker.Rank("   ", items, _ => 0).Count);
        Assert.Equal(2, SearchRanker.Rank("", items, _ => 0, limit: 2).Count);
        Assert.Empty(SearchRanker.Rank("", items, _ => 0, limit: 0));
        // saçma girdiler çökmez
        Assert.Empty(SearchRanker.Rank(new string('x', 5000), items, _ => 0));
        Assert.Empty(SearchRanker.Rank("\ud800", items, _ => 0));
        Assert.Empty(SearchRanker.Rank("rapor", Array.Empty<DiscoveryItem>(), _ => 0));
    }

    [Fact]
    public void ServisAramasi_KayitDefteriVeProjeleriArar_KullanimOneGecirir()
    {
        using var t = new DiscoveryDb();
        t.C.Exec("INSERT INTO projects(key,name,path,created_at,uid) VALUES('p1','Fatura Sistemi',NULL,'2026-03-01',@u)", null, ("@u", "11111111-1111-7111-8111-111111111111"));
        var svc = new DiscoveryService(t.Db, clock: () => DiscoveryDb.M(0));
        var s = svc.Search("fatura", null, 10);
        Assert.Equal("Fatura Sistemi", (string?)s["items"]![0]!["title"]);
        Assert.Equal("project", Directions.Kind((int)s["items"]![0]!["kindDir"]!).Code);
        var th = svc.Search("tema", null, 10)["items"]!.AsArray();
        Assert.Contains(th, i => (string?)i!["objectUid"] == "setting:ui.theme");
        // kullanım sırayı değiştirir: aynı basamaktaki iki ayardan çok kullanılan öne geçer
        var before = svc.Search("dil", null, 50)["items"]!.AsArray().Select(i => (string)i!["objectUid"]!).ToList();
        var last = before[^1];
        for (var i = 0; i < 3; i++) svc.RecordUsage(last, null, "test");
        var after = svc.Search("dil", null, 50)["items"]!.AsArray().Select(i => (string)i!["objectUid"]!).ToList();
        Assert.True(after.IndexOf(last) <= before.IndexOf(last));
    }
}

public class DiscoveryGeometryTests
{
    private static readonly RingOptions O = new();
    private static readonly Pt C0 = new(500, 500);

    [Theory]
    [InlineData(0, -100, 0)]
    [InlineData(70, -70, 1)]
    [InlineData(100, 0, 2)]
    [InlineData(70, 70, 3)]
    [InlineData(0, 100, 4)]
    [InlineData(-70, 70, 5)]
    [InlineData(-100, 0, 6)]
    [InlineData(-70, -70, 7)]
    public void SekizYon_YukariSifirSaatYonunde(double dx, double dy, int dir)
        => Assert.Equal(dir, RingGeometry.Direction(C0, new Pt(C0.X + dx, C0.Y + dy), O));

    [Fact]
    public void OluBolge_SektorSiniri_Histerezis()
    {
        Assert.Null(RingGeometry.Direction(C0, new Pt(500, 500), O));
        Assert.Null(RingGeometry.Direction(C0, new Pt(500, 461), O));      // 39 < 40
        Assert.Equal(0, RingGeometry.Direction(C0, new Pt(500, 460), O));  // tam 40
        Pt At(double deg) { var r = deg * Math.PI / 180; return new Pt(500 + 100 * Math.Sin(r), 500 - 100 * Math.Cos(r)); }
        Assert.Equal(0, RingGeometry.Direction(C0, At(22.4), O));
        Assert.Equal(1, RingGeometry.Direction(C0, At(22.6), O));
        Assert.Equal(7, RingGeometry.Direction(C0, At(337.4), O));         // 360 sarması
        Assert.Equal(0, RingGeometry.Direction(C0, At(359.9), O));
        // histerezis: 0'dayken 27 derecede hâlâ 0, 28'de 1; 1'deyken 18 derecede hâlâ 1 (sınır 22,5 - 5 = 17,5)
        Assert.Equal(0, RingGeometry.Direction(C0, At(27), O, current: 0));
        Assert.Equal(1, RingGeometry.Direction(C0, At(28), O, current: 0));
        Assert.Equal(1, RingGeometry.Direction(C0, At(18), O, current: 1));
        Assert.Equal(0, RingGeometry.Direction(C0, At(17), O, current: 1));
        Assert.Equal(7, RingGeometry.Direction(C0, At(333), O, current: 7));
        Assert.Equal(0, RingGeometry.Direction(C0, At(342), O, current: 0));              // 0'dan -18 derece: hâlâ 0
        Assert.Equal(0, RingGeometry.Direction(C0, new Pt(500, 400), O, current: 99));    // geçersiz mevcut yok sayılır
    }

    [Fact]
    public void IkiVurus_AyniYondeIkiKez_SabitUzunlukla_TurSonraYuva()
    {
        // 1. vuruş sağa (yön 2), seçimden sonra başlangıç ideal noktaya taşınır; 2. vuruş yine sağa: köşe gerekmeden seçilir
        var st = StrokeState.Idle;
        StrokeSelection? sel = null; var first = new List<StrokeSelection>(); var x = -10.0;
        for (var i = 0; i < 40 && first.Count < 2; i++)
        {
            x += 10;
            (st, sel) = StrokeDetector.Step(st, new Pt(x, 0), i * 16, O, fixedLength: true);
            if (sel is { } s) first.Add(s);
        }
        Assert.Equal(2, first.Count);
        Assert.Equal(150, first[0].At.X, 6);                       // ölü bölge 40 + 110: ideal nokta tam 150
        Assert.Equal(300, first[1].At.X, 6);                       // yeni başlangıç 150 → 300
        Assert.Equal(2, RingGeometry.Direction(new Pt(0, 0), first[0].At, O));
        Assert.Equal(2, RingGeometry.Direction(first[0].At, first[1].At, O));
        Assert.Equal(new SlotAddress(2, 2), RingGeometry.Address(2, 2));
        Assert.Null(RingGeometry.Address(null, 2));
        Assert.Null(RingGeometry.Address(8, 2));
    }

    [Fact]
    public void SabitUzunluk_SinirDegerleri()
    {
        Assert.False(RingGeometry.FixedLength(new Pt(0, 0), new Pt(150, 0), O).Selected);   // tam 150: aşılmadı
        var (sel, ideal) = RingGeometry.FixedLength(new Pt(0, 0), new Pt(151, 0), O);
        Assert.True(sel); Assert.Equal(150, ideal.X, 9);
        var (_, d) = RingGeometry.FixedLength(new Pt(10, 10), new Pt(10 + 300, 10 + 400), O); // 500 uzunluk: yönü korur
        Assert.Equal(10 + 90, d.X, 6); Assert.Equal(10 + 120, d.Y, 6);                        // 150 * (0,6 ; 0,8)
    }

    [Fact]
    public void IsaretlemeKipi_Kose_Duraklama_Titreme()
    {
        // 200 px sağa, sonra 90 derece aşağı: köşe, seçim köşe noktasında
        var st = StrokeDetector.Step(StrokeState.Idle, new Pt(0, 0), 0, O, false).State;
        st = StrokeDetector.Step(st, new Pt(100, 0), 16, O, false).State;
        st = StrokeDetector.Step(st, new Pt(200, 0), 32, O, false).State;
        var (st2, sel) = StrokeDetector.Step(st, new Pt(200, 30), 48, O, false);
        Assert.NotNull(sel); Assert.Equal("corner", sel!.Value.Reason); Assert.Equal(new Pt(200, 0), sel.Value.At);
        Assert.Equal(new Pt(200, 0), st2.Start);
        // 15 derecelik hafif dönüş köşe değil
        var (_, none) = StrokeDetector.Step(st, new Pt(200 + 30 * Math.Cos(15 * Math.PI / 180), 30 * Math.Sin(15 * Math.PI / 180)), 48, O, false);
        Assert.Null(none);
        // titreme (10 px altı) duraklama sayacını bozmaz; süre dolunca duraklama seçimi son noktada
        var (p1, s1) = StrokeDetector.Step(st, new Pt(203, 0), 40, O, false);
        Assert.Null(s1);
        var (p2, _) = StrokeDetector.Step(p1, new Pt(205, 2), 80, O, false);
        Assert.Null(StrokeDetector.Tick(p2, 120, O).Selection);          // 80 ms: henüz değil
        var (_, pause) = StrokeDetector.Step(p2, new Pt(205, 2), 150, O, false);
        Assert.Null(pause);
        var tick = StrokeDetector.Tick(p2, 140, O).Selection;            // sayaç 40'ta kuruldu, 100 ms doldu
        Assert.NotNull(tick); Assert.Equal("pause", tick!.Value.Reason);
        // kısa vuruş (150 px altı) ne köşe ne duraklama üretir
        var s = StrokeDetector.Step(StrokeDetector.Step(StrokeState.Idle, new Pt(0, 0), 0, O, false).State, new Pt(100, 0), 10, O, false).State;
        Assert.Null(StrokeDetector.Tick(s, 5000, O).Selection);
    }

    [Fact]
    public void EkranKenari_SikistirmaIsinlamaFarki_AbsoluteYonKorunur()
    {
        var area = new WorkArea(0, 0, 1920, 1040);
        var inside = RingGeometry.ClampCenter(new Pt(960, 500), area, O.EdgeMargin);
        Assert.False(inside.Clamped); Assert.Equal(new Pt(960, 500), inside.Center);
        var corner = RingGeometry.ClampCenter(new Pt(5, 5), area, O.EdgeMargin);
        Assert.True(corner.Clamped); Assert.Equal(new Pt(160, 160), corner.Center); Assert.Equal(new Pt(155, 155), corner.CursorWarp);
        var br = RingGeometry.ClampCenter(new Pt(1919.7, 1039.2), area, O.EdgeMargin);
        Assert.Equal(new Pt(1760, 880), br.Center);
        var frac = RingGeometry.ClampCenter(new Pt(300.9, 400.9), area, O.EdgeMargin);
        Assert.Equal(new Pt(300, 400), frac.Center);                                   // aşağı yuvarlama (Kando: floor)
        // ikinci ekran (sol üst köşesi negatif): koordinatlar çalışma alanına göre
        var left = RingGeometry.ClampCenter(new Pt(-1900, 100), new WorkArea(-1920, 0, 0, 1080), O.EdgeMargin);
        Assert.Equal(new Pt(-1760, 160), left.Center);
        // çalışma alanı iki kenar boşluğundan küçük: ortaya konur, çökmez
        Assert.Equal(new Pt(150, 100), RingGeometry.ClampCenter(new Pt(0, 0), new WorkArea(0, 0, 300, 200), 160).Center);
        // sıkıştırınca onay yarıçapı çalışma alanı içinde kalır: boşluk > onay yarıçapı
        Assert.True(O.EdgeMargin > O.ConfirmRadius);
        foreach (var p in new[] { new Pt(0, 0), new Pt(1920, 1040), new Pt(0, 1040), new Pt(1920, 0) })
        {
            var c = RingGeometry.ClampCenter(p, area, O.EdgeMargin).Center;
            Assert.InRange(c.X - O.ConfirmRadius, area.Left, area.Right); Assert.InRange(c.X + O.ConfirmRadius, area.Left, area.Right);
            Assert.InRange(c.Y - O.ConfirmRadius, area.Top, area.Bottom); Assert.InRange(c.Y + O.ConfirmRadius, area.Top, area.Bottom);
        }
    }

    [Fact]
    public void Ayarlar_KenarBosluguOnayYaricapindanBuyuk_DokunusSurukleme()
    {
        Assert.Null(O.Problem());
        Assert.Equal(150, O.ConfirmRadius);
        Assert.NotNull((O with { EdgeMargin = 150 }).Problem());
        Assert.NotNull((O with { EdgeMargin = 100 }).Problem());
        Assert.NotNull((O with { HysteresisDegrees = 22.5 }).Problem());
        Assert.NotNull((O with { DeadZone = -1 }).Problem());
        Assert.True(RingGeometry.IsTap(0.15, 3, O));
        Assert.False(RingGeometry.IsTap(0.25, 3, O));
        Assert.False(RingGeometry.IsTap(0.1, 20, O));
    }

    [Fact]
    public void KandoAtfi_DosyaBasindaVar()
    {
        var head = string.Join("\n", File.ReadLines(Path.Combine(TestEnv.RepoRoot, "src", "Soz.Core", "Discovery", "RingGeometry.cs")).Take(8));
        Assert.Contains("Kando", head); Assert.Contains("MIT", head); Assert.Contains("gesture-detector.ts", head);
    }
}

public class DiscoveryHttpTests
{
    private static StringContent J(string s) => new(s, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Uclar_GocleKurulu_IlkIstektenCalisir_TokenGerekir()
    {
        using var env = new TestEnv();
        await using var h = await SozServer.StartAsync(env.DataDir, 49790);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        // d2m: tablolar v13 göçüyle açılışta kurulur, uçlar 503 vermez. 503 yolu servis testinde (v12 db) sınanır.
        var r = await http.GetAsync("/api/discovery/layout");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0, (int)JsonNode.Parse(await r.Content.ReadAsStringAsync())!["filled"]!);
        Assert.True((bool)JsonNode.Parse(await http.GetStringAsync("/api/discovery/search?q=tema"))!["installed"]!);

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/discovery/layout/slot") { Content = J("""{"kindDir":2,"slotDir":1,"objectUid":"setting:ui.theme"}""") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(put)).StatusCode);            // jeton yok
        HttpRequestMessage Req(HttpMethod m, string url, string? body = null) { var q = new HttpRequestMessage(m, url); q.Headers.Add("X-Soz-Token", h.Token); if (body is not null) q.Content = J(body); return q; }
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Put, "/api/discovery/layout/slot", """{"kindDir":2,"slotDir":1,"objectUid":"setting:ui.theme"}"""))).StatusCode);
        var dup = await http.SendAsync(Req(HttpMethod.Put, "/api/discovery/layout/slot", """{"kindDir":2,"slotDir":1,"objectUid":"setting:ui.language"}"""));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Put, "/api/discovery/layout/slot", """{"kindDir":2,"slotDir":1,"objectUid":"setting:ui.language","replace":true}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Put, "/api/discovery/layout/slot", """{"kindDir":9,"slotDir":1,"objectUid":"x"}"""))).StatusCode);
        var lay = JsonNode.Parse(await http.GetStringAsync("/api/discovery/layout"))!;
        Assert.Equal(1, (int)lay["filled"]!);
        Assert.Equal("setting:ui.language", (string?)lay["directions"]![2]!["slots"]![1]!["objectUid"]);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Post, "/api/discovery/usage", """{"objectUid":"setting:ui.theme","source":"test"}"""))).StatusCode);
        var sug = JsonNode.Parse(await (await http.SendAsync(Req(HttpMethod.Post, "/api/discovery/suggestions", "{}"))).Content.ReadAsStringAsync())!;
        Assert.True((bool)sug["shadow"]!);
        Assert.Empty(sug["items"]!.AsArray());                                                       // gölge kip: boş
        Assert.NotNull((string?)sug["batchId"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Post, "/api/discovery/dismiss", """{"objectUid":"x","mode":"saçma"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Post, "/api/discovery/dismiss", """{"objectUid":"x","mode":"never"}"""))).StatusCode);
        var shadow = JsonNode.Parse(await http.GetStringAsync("/api/discovery/shadow?days=7"))!;
        Assert.Equal(1, (int)shadow["batches"]!);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Delete, "/api/discovery/layout/slot?kindDir=2&slotDir=1"))).StatusCode);
        Assert.Equal(0, (int)JsonNode.Parse(await http.GetStringAsync("/api/discovery/layout"))!["filled"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Delete, "/api/discovery/layout/slot"))).StatusCode);
    }
}
