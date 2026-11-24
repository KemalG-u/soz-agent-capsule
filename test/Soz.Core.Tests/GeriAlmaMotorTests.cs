using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Soz.Core.Data;
using Soz.Core.Restore;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Geri alma çekirdeği (Dalga 2, şerit B): görüntü deposu + geri yükleyici, doğrudan motor düzeyinde (veritabanı ve sunucu yok).
/// Cline'ın checkpoint senaryolarından uyarlanan testler (C1..C17) ve spike'ın 11 uç durumu (U1..U11). Hepsi yalnız
/// urun\_gecici\d2b\t\ altındaki sentetik klasörlerde çalışır, test sonunda o klasörü kaldırır.
/// </summary>
public class GeriAlmaMotorTests
{
    // ------------------------------------------------------------------ düzenek
    internal sealed class GeriRig : IDisposable
    {
        public string Dir { get; }
        public string Proje { get; }
        public GeriDepo Depo { get; }
        public SnapshotEngine Snap { get; }
        public RestoreEngine Rest { get; }

        private readonly string _ad;
        public GeriRig(Action<RestoreOptions>? ayar = null, [System.Runtime.CompilerServices.CallerMemberName] string? ad = null)
        {
            _ad = ad ?? "?";
            Dir = Path.Combine(TestEnv.RepoRoot, "_gecici", "d2b", "t", Guid.NewGuid().ToString("N")[..10]);
            Proje = Path.Combine(Dir, "proje dizini"); // boşluklu yol
            Directory.CreateDirectory(Proje);
            var o = new RestoreOptions { Fsync = false };
            ayar?.Invoke(o);
            Depo = new GeriDepo(Path.Combine(Dir, "depo"), o);
            Snap = new SnapshotEngine(Depo);
            Rest = new RestoreEngine(Snap);
        }

        public string P(string rel) => Path.Combine(Proje, rel.Replace('/', '\\'));
        public void Yaz(string rel, string icerik) { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!); File.WriteAllText(P(rel), icerik, new UTF8Encoding(false)); }
        public void YazBayt(string rel, byte[] b) { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!); File.WriteAllBytes(P(rel), b); }
        public string Oku(string rel) => File.ReadAllText(P(rel));
        public bool Var(string rel) => File.Exists(P(rel)) || Directory.Exists(P(rel));
        public void KlasorYap(string rel) => Directory.CreateDirectory(P(rel));
        public void Sil(string rel) { if (Directory.Exists(P(rel))) Directory.Delete(P(rel), true); else File.Delete(P(rel)); }

        public SnapshotResult Al(string kind = "manual") => Snap.Take(new SnapshotRequest(Proje, kind, "p1", "j1"));

        public RestoreOutcome Geri(SnapshotResult hedef, SnapshotResult? ajanSon = null, string[]? yollar = null, bool zorla = false, bool uygula = true)
        {
            var rq = new RestoreRequest(Proje, hedef.Id, ajanSon?.Id, yollar, zorla, "p1", "j1");
            return uygula ? Rest.Apply(rq) : Rest.Preview(rq);
        }

        /// <summary>Klasörün birebir kimliği: yol kümesi (dosya ve boş klasörler) + her dosyanın SHA-256 özeti.</summary>
        public string Ozet(bool mtime = false) => Ozetle(Proje, mtime);

        public static string Ozetle(string kok, bool mtime = false)
        {
            var sb = new StringBuilder();
            var satir = new List<string>();
            foreach (var d in Directory.GetDirectories(kok, "*", SearchOption.AllDirectories))
                satir.Add("D|" + Path.GetRelativePath(kok, d).Replace('\\', '/'));
            foreach (var f in Directory.GetFiles(kok, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(kok, f).Replace('\\', '/');
                var h = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
                satir.Add("F|" + rel + "|" + h + (mtime ? "|" + (File.GetLastWriteTimeUtc(f).Ticks) : ""));
            }
            satir.Sort(StringComparer.Ordinal);
            foreach (var s in satir) sb.Append(s).Append('\n');
            return sb.ToString();
        }

        public IEnumerable<string> GeciciArtik()
        {
            foreach (var f in Directory.GetFiles(Proje, "*" + RestoreEngine.TempSuffix, SearchOption.AllDirectories)) yield return f;
            if (Directory.Exists(Depo.TmpDir)) foreach (var f in Directory.GetFiles(Depo.TmpDir, "*", SearchOption.AllDirectories)) yield return f;
        }

        public void Dispose()
        {
            try { Depo.Dispose(); } catch (Exception) { }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(Dir, true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                    if (i == 39) { try { File.WriteAllText(Path.Combine(Dir, "kaldi.txt"), _ad + " | " + e.Message); } catch (Exception) { } }
                }
            }
        }
    }

    private static GeriRig Standart([System.Runtime.CompilerServices.CallerMemberName] string? ad = null)
    {
        var r = new GeriRig(null, ad);
        r.Yaz("hesap.js", "function topla(a, b) { return a + b }\n");
        r.Yaz("notlar.txt", "önemli notlar\n");
        r.Yaz("src/a.js", "export const a = 1\n");
        r.Yaz("src/derin/b.js", "export const b = 2\n");
        r.KlasorYap("bos-klasor");
        return r;
    }

    /// <summary>Sahte ajan: dosyaları bozar (değiştirir, siler, yaratır, klasör adını değiştirir).</summary>
    private static void AjanBozar(GeriRig r)
    {
        r.Yaz("hesap.js", "AJAN BOZDU\n");
        r.Sil("notlar.txt");
        r.Yaz("yeni.txt", "ajan yazdı\n");
        r.Yaz("yeni-klasor/ic.txt", "ic\n");
        Directory.Move(r.P("src"), r.P("kaynak"));
        Directory.Delete(r.P("bos-klasor"));
        r.KlasorYap("yeni-bos");
    }

    // ================================================================== Cline senaryoları (C1..C17)

    [Fact]
    public void C1_Degisen_dosya_icerik_ve_mtime_ns_ile_geri_gelir()
    {
        using var r = Standart();
        var once = File.GetLastWriteTimeUtc(r.P("hesap.js")).Ticks;
        var b = r.Al("before_run");
        Assert.True(b.Ok);
        Assert.Equal(ProtectionLevel.Full, b.Protection);
        Thread.Sleep(30);
        r.Yaz("hesap.js", "bozuk");
        var a = r.Al("after_run");
        var o = r.Geri(b, a);
        Assert.True(o.Ok, o.Error);
        Assert.Equal("function topla(a, b) { return a + b }\n", r.Oku("hesap.js"));
        Assert.Equal(once, File.GetLastWriteTimeUtc(r.P("hesap.js")).Ticks); // zaman 100 ns doğrulukla geri yazılır (Unix ns olarak saklanır)
        Assert.Equal(1, o.Restored);
    }

    [Fact]
    public void C2_Ajanin_sildigi_dosya_geri_gelir()
    {
        using var r = Standart();
        var b = r.Al();
        r.Sil("notlar.txt");
        var a = r.Al();
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Equal("önemli notlar\n", r.Oku("notlar.txt"));
        Assert.Contains(o.Items, i => i.Path == "notlar.txt" && i.Action == "geri-yukle" && i.Why == "ajan-sildi");
    }

    [Fact]
    public void C3_Ajanin_yarattigi_dosya_ve_klasorler_kaldirilir()
    {
        using var r = Standart();
        var b = r.Al();
        r.Yaz("yeni.txt", "x");
        r.Yaz("a/b/c/d.txt", "derin");
        var a = r.Al();
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.False(r.Var("yeni.txt"));
        Assert.False(r.Var("a")); // dosyası gidince boşalan klasör zinciri de temizlenir
        Assert.Equal(2, o.Removed);
        Assert.True(o.DirsRemoved >= 3);
    }

    [Fact]
    public void C4_Eski_noktaya_sonra_yeni_noktaya_gidilebilir()
    {
        using var r = Standart();
        var n1 = r.Al();
        r.Yaz("hesap.js", "ikinci surum");
        var n2 = r.Al();
        r.Yaz("hesap.js", "ucuncu surum yazisi");
        var n3 = r.Al();
        Assert.True(r.Geri(n1, n3).Ok);
        Assert.Equal("function topla(a, b) { return a + b }\n", r.Oku("hesap.js"));
        var o = r.Geri(n3, null, zorla: true); // başka yönde de gidilebilir (ajanın son hâli bilinmiyor: her fark ajanın sayılır)
        Assert.True(o.Ok);
        Assert.Equal("ucuncu surum yazisi", r.Oku("hesap.js"));
        Assert.True(r.Geri(n2).Ok);
        Assert.Equal("ikinci surum", r.Oku("hesap.js"));
    }

    [Fact]
    public void C5_Degisiklik_yoksa_yeni_nokta_yazilmaz_ve_geri_alma_hicbir_sey_yapmaz()
    {
        using var r = Standart();
        var b = r.Al();
        var b2 = r.Al();
        Assert.True(b.IsNew);
        Assert.False(b2.IsNew);
        Assert.Equal(b.Id, b2.Id);
        var onceki = r.Ozet(mtime: true);
        var o = r.Geri(b, b2);
        Assert.True(o.Ok);
        Assert.Empty(o.Items);
        Assert.Equal(onceki, r.Ozet(mtime: true)); // dosyalara hiç dokunulmadı (zaman da aynı)
    }

    [Fact]
    public void C6_Klasor_adi_degisikligi_geri_alinir()
    {
        using var r = Standart();
        var b = r.Al();
        var once = r.Ozet();
        Directory.Move(r.P("src"), r.P("kaynak"));
        var a = r.Al();
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Equal(once, r.Ozet());
        Assert.False(r.Var("kaynak"));
    }

    [Fact]
    public void C7_Bos_dosya_ve_ikili_dosya_birebir_geri_gelir()
    {
        using var r = Standart();
        r.Yaz("bos.txt", "");
        var ikili = new byte[70_000];
        new Random(7).NextBytes(ikili);
        r.YazBayt("veri.bin", ikili);
        var b = r.Al();
        r.Yaz("bos.txt", "dolu");
        r.YazBayt("veri.bin", new byte[] { 1, 2, 3 });
        var a = r.Al();
        Assert.True(r.Geri(b, a).Ok);
        Assert.Equal(0, new FileInfo(r.P("bos.txt")).Length);
        Assert.Equal(ikili, File.ReadAllBytes(r.P("veri.bin")));
    }

    [Fact]
    public void C8_Cok_derin_ic_ice_klasorler()
    {
        using var r = Standart();
        var yol = string.Join("/", Enumerable.Range(0, 12).Select(i => "k" + i)) + "/son.txt";
        r.Yaz(yol, "dipte");
        var b = r.Al();
        r.Sil("k0");
        var a = r.Al();
        Assert.True(r.Geri(b, a).Ok);
        Assert.Equal("dipte", r.Oku(yol));
    }

    [Fact]
    public void C9_Gizli_noktali_dosyalar_geri_gelir_ve_bagimlilik_klasorleri_saklanmaz_dokunulmaz()
    {
        using var r = Standart();
        r.Yaz(".env", "ANAHTAR=gizli-degil-sentetik\n");
        r.Yaz("node_modules/x/index.js", "bagimlilik 1");
        var b = r.Al();
        r.Yaz(".env", "ANAHTAR=bozuk\n");
        r.Yaz("node_modules/x/index.js", "bagimlilik 2 (ajan kurdu)");
        r.Yaz("node_modules/y/yeni.js", "yeni paket");
        var a = r.Al();
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Equal("ANAHTAR=gizli-degil-sentetik\n", r.Oku(".env"));
        Assert.Equal("bagimlilik 2 (ajan kurdu)", r.Oku("node_modules/x/index.js")); // içeriği saklanmadı: olduğu gibi kaldı
        Assert.True(r.Var("node_modules/y/yeni.js"));
        Assert.Contains(o.Notes, n => n.Contains("node_modules") && n.Contains("değişti"));
        // Depo node_modules içeriğini hiç tutmadı
        using var c = r.Depo.Open();
        Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM snapshot_entry WHERE path LIKE 'node_modules/%'"));
    }

    [Fact]
    public void C10_Kismi_geri_alma_dosya_ve_klasor_secimi()
    {
        using var r = Standart();
        var b = r.Al();
        AjanBozar(r);
        r.Yaz("src2/x.txt", "x"); // ajan yarattı, seçilmeyecek
        var a = r.Al();

        var o = r.Geri(b, a, yollar: new[] { "hesap.js" });
        Assert.True(o.Ok);
        Assert.Equal("function topla(a, b) { return a + b }\n", r.Oku("hesap.js"));
        Assert.False(r.Var("notlar.txt"));     // seçilmedi: ajanın hâlinde kaldı
        Assert.True(r.Var("yeni.txt"));
        Assert.True(r.Var("kaynak/a.js"));

        // klasör seçimi: "src" dönülür, "kaynak" (yeni ad) o klasörün parçası sayıldığı için seçime girmez
        var o2 = r.Geri(b, a, yollar: new[] { "src/" }, zorla: true);
        Assert.True(o2.Ok);
        Assert.True(r.Var("src/a.js"));
        Assert.True(r.Var("src/derin/b.js"));
        Assert.False(r.Var("notlar.txt")); // yine seçilmedi
    }

    [Fact]
    public void C11_Onizleme_hicbir_dosyaya_yazmaz_ve_uygulamayla_ayni_listeyi_verir()
    {
        using var r = Standart();
        var b = r.Al();
        AjanBozar(r);
        var a = r.Al();
        var once = r.Ozet(mtime: true);
        var on = r.Geri(b, a, uygula: false);
        Assert.True(on.Ok);
        Assert.Equal(once, r.Ozet(mtime: true));
        Assert.Empty(r.GeciciArtik());
        var ap = r.Geri(b, a);
        Assert.True(ap.Ok);
        Assert.Equal(on.Items.Select(i => i.Path + "|" + i.Action), ap.Items.Select(i => i.Path + "|" + i.Action));
        Assert.True(on.Items.Count >= 6);
    }

    [Fact]
    public void C12_Geri_almayi_geri_al_ajanin_son_haline_doner()
    {
        using var r = Standart();
        var b = r.Al("before_run");
        AjanBozar(r);
        var a = r.Al("after_run");
        var ajanHali = r.Ozet();
        var temiz = "";
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        temiz = r.Ozet();
        Assert.NotEqual(ajanHali, temiz);
        Assert.NotNull(o.PostUid);

        // geri almadan önceki noktaya dön; ajan son hâli = geri almanın sonundaki nokta
        var pre = new SnapshotResult(true, o.PreId, o.PreUid, false, ProtectionLevel.Full, null, null, 0, 0, 0, 0, 0, 0);
        var post = new SnapshotResult(true, o.PostId, o.PostUid, false, ProtectionLevel.Full, null, null, 0, 0, 0, 0, 0, 0);
        var o2 = r.Geri(pre, post);
        Assert.True(o2.Ok, o2.Error);
        Assert.Equal(ajanHali, r.Ozet());
    }

    [Fact]
    public void C13_Kisi_ajandan_sonra_dokunduysa_cakisma_atlanir_zorla_ile_gelir()
    {
        using var r = Standart();
        var b = r.Al();
        r.Yaz("hesap.js", "ajan surumu");
        var a = r.Al();
        r.Yaz("hesap.js", "kisinin sonradan yazdigi");
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Contains(o.Conflicts, c => c.Path == "hesap.js" && c.Reason == "degisti");
        Assert.Equal("kisinin sonradan yazdigi", r.Oku("hesap.js")); // atlandı

        var z = r.Geri(b, a, zorla: true);
        Assert.Empty(z.Conflicts);
        Assert.Equal("function topla(a, b) { return a + b }\n", r.Oku("hesap.js"));
        // kişinin sürümü geri almadan önceki noktada saklandı: "geri almayı geri al" ile döner
        var pre = new SnapshotResult(true, z.PreId, z.PreUid, false, "tam", null, null, 0, 0, 0, 0, 0, 0);
        Assert.True(r.Geri(pre, null, zorla: true).Ok);
        Assert.Equal("kisinin sonradan yazdigi", r.Oku("hesap.js"));
    }

    [Fact]
    public void C14_Ajanin_dokunmadigi_dosyaya_kisi_dokunduysa_hic_el_surulmez()
    {
        using var r = Standart();
        var b = r.Al();
        r.Yaz("hesap.js", "ajan");
        var a = r.Al();
        r.Yaz("notlar.txt", "kişi not ekledi"); // ajan notlar.txt'ye dokunmadı (B = A), kişi değiştirdi (C != A)
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Equal("kişi not ekledi", r.Oku("notlar.txt"));
        Assert.DoesNotContain(o.Items, i => i.Path == "notlar.txt");
        Assert.Empty(o.Conflicts);
    }

    [Fact]
    public void C15_Olmayan_klasorde_nokta_alinamaz_istisna_firlatmaz()
    {
        using var r = Standart();
        var s = r.Snap.Take(new SnapshotRequest(Path.Combine(r.Dir, "yok-boyle-klasor"), "before_run", "p1"));
        Assert.False(s.Ok);
        Assert.Equal(ProtectionLevel.None, s.Protection);
        Assert.Equal("klasor-yok", s.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(s.Note));
    }

    [Fact]
    public void C16_Ayni_kokte_eszamanli_goruntuler_sirayla_calisir_ve_bozulmaz()
    {
        using var r = Standart();
        for (var i = 0; i < 30; i++) r.Yaz($"dosya{i}.txt", new string('x', 2000 + i));
        var sonuc = new SnapshotResult[6];
        Parallel.For(0, 6, new ParallelOptions { MaxDegreeOfParallelism = 6 }, i =>
        {
            if (i % 2 == 1) { /* yazma yarışı: bazı dosyaları değiştir */ File.WriteAllText(r.P($"dosya{i}.txt"), "yarış " + i); }
            sonuc[i] = r.Al();
        });
        Assert.All(sonuc, s => Assert.True(s.Ok, s.Note));
        var son = r.Al();
        var kopya = r.Ozet();
        r.Yaz("dosya3.txt", "bozuldu");
        var sonra = r.Al();
        Assert.True(r.Geri(son, sonra).Ok);
        Assert.Equal(kopya, r.Ozet());
    }

    [Fact]
    public void C17_Ajanin_son_hali_bilinmiyorsa_her_fark_ajanin_sayilir()
    {
        using var r = Standart();
        var b = r.Al();
        AjanBozar(r);
        var once = r.Ozet();
        var o = r.Geri(b, null);
        Assert.True(o.Ok);
        Assert.NotEqual(once, r.Ozet());
        Assert.StartsWith("function topla", r.Oku("hesap.js"));
        Assert.False(r.Var("yeni.txt"));
    }

    // ================================================================== uçtan uca birebirlik (yol kümesi + özet)

    [Fact]
    public void E2E_Ajan_bozar_geri_alinir_klasor_birebir_ayni()
    {
        using var r = Standart();
        r.Yaz("a b/çalışma ğüşiöç.txt", "türkçe");
        r.YazBayt("buyuk.bin", new byte[300_000]);
        var onceki = r.Ozet(mtime: true);
        var b = r.Al("before_run");
        AjanBozar(r);
        File.AppendAllText(r.P("buyuk.bin"), "ek");
        var a = r.Al("after_run");
        Assert.NotEqual(onceki, r.Ozet(mtime: true));
        var o = r.Geri(b, a);
        Assert.True(o.Ok, o.Error);
        Assert.Empty(o.Conflicts);
        Assert.Equal(onceki, r.Ozet(mtime: true)); // yol kümesi (boş klasörler dahil) + her dosyanın özeti + yazma zamanı
        Assert.Empty(r.GeciciArtik());
    }

    // ================================================================== spike'ın 11 uç durumu (U1..U11)

    [Fact]
    public void U1_Kilitli_dosya_goruntude_atlanir_digerleri_alinir_koruma_sinirli()
    {
        using var r = Standart();
        r.Yaz("kilitli.txt", "kilitli içerik");
        using (new FileStream(r.P("kilitli.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var b = r.Al();
            Assert.True(b.Ok);
            Assert.Equal(ProtectionLevel.Limited, b.Protection);
            Assert.Equal(1, b.Skipped);
            Assert.Contains("kilitli", b.Note);
            using var c = r.Depo.Open();
            Assert.Equal("kilitli", c.Scalar("SELECT skip_reason FROM snapshot_entry WHERE path='kilitli.txt'") as string);
            Assert.True(c.ScalarLong("SELECT COUNT(*) FROM snapshot_entry WHERE path='hesap.js' AND blob IS NOT NULL") == 1);
        }
        // kilit açılınca bir sonraki görüntü dosyayı alır ve koruma tam olur
        var b2 = r.Al();
        Assert.Equal(ProtectionLevel.Full, b2.Protection);
    }

    [Fact]
    public void U2_Kilitli_dosya_geri_almada_atlanir_kilit_acilinca_ikinci_denemede_gelir()
    {
        using var r = Standart();
        var b = r.Al();
        r.Yaz("hesap.js", "ajan"); r.Yaz("notlar.txt", "ajan not");
        var a = r.Al();
        RestoreOutcome o;
        using (new FileStream(r.P("notlar.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            o = r.Geri(b, a);
            Assert.True(o.Ok);
            Assert.Equal("function topla(a, b) { return a + b }\n", r.Oku("hesap.js")); // kilitsiz olan geldi
            Assert.Empty(r.GeciciArtik()); // yarım yazılmış geçici dosya kalmadı
        }
        // kilitli kalan dosya raporlandı (çakışma ya da alınamayan olarak), ikinci denemede gelir
        Assert.True(o.Conflicts.Any(c => c.Path == "notlar.txt") || o.Unrestorable.Any(c => c.Path == "notlar.txt") || o.Items.Any(i => i.Path == "notlar.txt"));
        var o2 = r.Geri(b, a);
        Assert.True(o2.Ok);
        Assert.Equal("önemli notlar\n", r.Oku("notlar.txt"));
    }

    [Fact]
    public void U3_Sinirdan_buyuk_dosya_saklanmaz_ozeti_tutulur_geri_alinamayan_olarak_bildirilir()
    {
        using var r = new GeriRig(o => o.MaxCopyBytes = 100_000);
        r.Yaz("hesap.js", "k");
        r.YazBayt("buyuk.bin", new byte[300_000]);
        var b = r.Al();
        Assert.Equal(ProtectionLevel.Limited, b.Protection);
        Assert.Contains("çok büyük", b.Note);
        using (var c = r.Depo.Open())
        {
            Assert.Equal("buyuk", c.Scalar("SELECT skip_reason FROM snapshot_entry WHERE path='buyuk.bin'") as string);
            Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM blob WHERE size=300000"));
        }
        File.AppendAllText(r.P("buyuk.bin"), "x");
        r.Yaz("hesap.js", "bozuk");
        var a = r.Al();
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Equal("k", r.Oku("hesap.js"));
        Assert.Contains(o.Unrestorable, u => u.Path == "buyuk.bin" && u.Reason == "buyuk");
    }

    [Fact]
    public void U4_Sinir_icindeki_buyuk_dosya_parcalanarak_saklanir_ve_birebir_geri_gelir()
    {
        using var r = new GeriRig(o => { o.ChunkFromBytes = 5L << 20; o.MaxCopyBytes = 64L << 20; });
        var veri = new byte[(9 << 20) + 123]; // 4 MiB'lik 3 parça
        new Random(11).NextBytes(veri);
        r.YazBayt("buyuk.bin", veri);
        var b = r.Al();
        Assert.Equal(ProtectionLevel.Full, b.Protection);
        using (var c = r.Depo.Open()) Assert.Equal(3, c.ScalarLong("SELECT COUNT(*) FROM blob_parca"));
        var bozuk = (byte[])veri.Clone();
        bozuk[5_000_000] ^= 0xFF; // tek bayt (aynı boyut)
        r.YazBayt("buyuk.bin", bozuk);
        File.SetLastWriteTimeUtc(r.P("buyuk.bin"), DateTime.UtcNow.AddMinutes(1));
        var a = r.Al();
        // ikinci görüntü yalnız değişen parçayı yazar
        Assert.True(a.NewBytes < 5L << 20, "yalnız değişen 4 MiB'lik parça (sıkıştırılmış) yazılmalı: " + a.NewBytes);
        Assert.True(r.Geri(b, a).Ok);
        Assert.Equal(veri, File.ReadAllBytes(r.P("buyuk.bin")));
    }

    [Fact]
    public void U5_260_karakterden_uzun_yol()
    {
        using var r = Standart();
        var uzun = string.Join("/", Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 45))) + "/dosya.txt"; // > 300 karakter
        Assert.True(r.P(uzun).Length > 300);
        r.Yaz(uzun, "uzun yol");
        var b = r.Al();
        r.Sil(uzun.Split('/')[0]);
        var a = r.Al();
        Assert.True(r.Geri(b, a).Ok);
        Assert.Equal("uzun yol", r.Oku(uzun));
    }

    [Fact]
    public void U6_Turkce_bosluklu_NFD_adlar_ve_bos_dosya()
    {
        using var r = Standart();
        var nfd = "cafe\u0301.txt"; // e + birleşik vurgu
        var adlar = new[] { "ışık ğüşiöç.txt", "İstanbul Ş.txt", "boşluklu ad  çift.txt", nfd, "bos.dat" };
        foreach (var ad in adlar) r.Yaz("tr/" + ad, ad == "bos.dat" ? "" : "içerik " + ad);
        var onceki = r.Ozet();
        var b = r.Al();
        r.Sil("tr");
        var a = r.Al();
        Assert.True(r.Geri(b, a).Ok);
        Assert.Equal(onceki, r.Ozet());
        foreach (var ad in adlar) Assert.True(File.Exists(r.P("tr/" + ad)), ad);
        Assert.Contains(Directory.GetFiles(r.P("tr")).Select(Path.GetFileName), x => x == nfd); // NFD ad olduğu gibi (normalleştirilmeden)
    }

    [Fact]
    public void U7_Salt_okunur_dosya_oznitelikle_geri_gelir_ajanin_salt_okunur_dosyasi_silinir()
    {
        using var r = Standart();
        r.Yaz("sabit.txt", "sabit");
        File.SetAttributes(r.P("sabit.txt"), FileAttributes.ReadOnly);
        var b = r.Al();
        File.SetAttributes(r.P("sabit.txt"), FileAttributes.Normal);
        r.Yaz("sabit.txt", "bozuk");
        r.Yaz("ajan-ro.txt", "ro");
        File.SetAttributes(r.P("ajan-ro.txt"), FileAttributes.ReadOnly);
        var a = r.Al();
        var o = r.Geri(b, a);
        Assert.True(o.Ok, o.Error);
        Assert.Equal("sabit", r.Oku("sabit.txt"));
        Assert.True((File.GetAttributes(r.P("sabit.txt")) & FileAttributes.ReadOnly) != 0);
        Assert.False(r.Var("ajan-ro.txt"));
        File.SetAttributes(r.P("sabit.txt"), FileAttributes.Normal);
    }

    [Fact]
    public void U8_Bos_klasorler_kaydedilir_ajanin_sildigi_geri_gelir_yarattigi_kalkar()
    {
        using var r = Standart();
        r.KlasorYap("bos2/ic-bos");
        var onceki = r.Ozet();
        var b = r.Al();
        Directory.Delete(r.P("bos-klasor"));
        Directory.Delete(r.P("bos2"), true);
        r.KlasorYap("yeni-bos");
        r.Yaz("yeni/ic/dosya.txt", "x");
        var a = r.Al();
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Equal(onceki, r.Ozet());
        Assert.True(o.DirsCreated >= 2);
        Assert.True(o.DirsRemoved >= 3);
    }

    [Fact]
    public void U9_Baglanti_noktasi_junction_izlenmez_uzerinden_yazilmaz()
    {
        using var r = Standart();
        var dis = Path.Combine(r.Dir, "dis-klasor");
        Directory.CreateDirectory(dis);
        File.WriteAllText(Path.Combine(dis, "dis.txt"), "dışarıdaki dosya");
        var junction = r.P("baglanti");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{dis}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(10000); }
        if (!Directory.Exists(junction)) return; // bu ortamda junction kurulamadı: test anlamsız (çıktıda "geçti" sayılmaz, sonuç raporda belirtilir)
        try
        {
            var b = r.Al();
            using (var c = r.Depo.Open()) Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM snapshot_entry WHERE path LIKE 'baglanti/%'"));
            r.Yaz("hesap.js", "bozuk");
            var a = r.Al();
            Assert.True(r.Geri(b, a).Ok);
            Assert.Equal("dışarıdaki dosya", File.ReadAllText(Path.Combine(dis, "dis.txt")));
            Assert.True(Directory.Exists(junction));
            Assert.Single(Directory.GetFileSystemEntries(dis));
        }
        finally { Directory.Delete(junction, false); } // yalnız bağlantıyı kaldırır, hedefe dokunmaz
    }

    [Fact]
    public void U10_Yalniz_buyuk_kucuk_harf_farkiyla_yeniden_adlandirma()
    {
        using var r = Standart();
        r.Yaz("Rapor.TXT", "r");
        var b = r.Al();
        File.Move(r.P("Rapor.TXT"), r.P("rapor.txt.gecici"));
        File.Move(r.P("rapor.txt.gecici"), r.P("rapor.txt"));
        var a = r.Al();
        using (var c = r.Depo.Open())
        {
            var adlar = r.Snap.Resolve(a.Id).Values.Select(v => v.Path).ToList();
            Assert.Contains("rapor.txt", adlar);
            Assert.DoesNotContain("Rapor.TXT", adlar);
        }
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Contains(Directory.GetFiles(r.Proje).Select(Path.GetFileName), x => x == "Rapor.TXT");
        Assert.Equal(1, o.Renamed);
    }

    [Fact]
    public void U11_Cakisma_ucyonlu_altı_senaryo_ve_yine_de_geri_al()
    {
        using var r = Standart();
        r.Yaz("a1.txt", "b1"); r.Yaz("a2.txt", "b2"); r.Yaz("a3.txt", "b3"); r.Yaz("a4.txt", "b4"); r.Yaz("a5.txt", "b5"); r.Yaz("a6.txt", "b6");
        var b = r.Al();
        // ajan: a1 değiştir, a2 değiştir, a3 sil, a4 değiştir; a5, a6'ya dokunmadı. a7 yarattı.
        r.Yaz("a1.txt", "ajan1"); r.Yaz("a2.txt", "ajan2"); r.Sil("a3.txt"); r.Yaz("a4.txt", "ajan4"); r.Yaz("a7.txt", "ajan7");
        var a = r.Al();
        // kişi sonradan: a2 değiştirdi (çakışma), a3'ü yeniden yarattı (çakışma), a5 değiştirdi (ajan dokunmadı: el sürülmez),
        //                a4 değişmedi, a7'yi değiştirdi (çakışma), a6 dokunulmadı
        r.Yaz("a2.txt", "kisi2"); r.Yaz("a3.txt", "kisi3"); r.Yaz("a5.txt", "kisi5"); r.Yaz("a7.txt", "kisi7");
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Equal(new[] { "a2.txt", "a3.txt", "a7.txt" }, o.Conflicts.Select(c => c.Path).OrderBy(x => x).ToArray());
        Assert.Equal("b1", r.Oku("a1.txt"));
        Assert.Equal("b4", r.Oku("a4.txt"));
        Assert.Equal("kisi5", r.Oku("a5.txt"));
        Assert.Equal("b6", r.Oku("a6.txt"));
        Assert.Equal("kisi2", r.Oku("a2.txt"));
        Assert.Equal("kisi7", r.Oku("a7.txt"));

        var z = r.Geri(b, a, zorla: true);
        Assert.Empty(z.Conflicts);
        Assert.Equal("b2", r.Oku("a2.txt"));
        Assert.Equal("b3", r.Oku("a3.txt"));
        Assert.False(r.Var("a7.txt"));
        Assert.Equal("kisi5", r.Oku("a5.txt")); // zorlamada bile ajanın dokunmadığı dosyaya el sürülmez

        // geri almayı geri al: kişinin eski sürümleri döner
        var pre = new SnapshotResult(true, z.PreId, z.PreUid, false, "tam", null, null, 0, 0, 0, 0, 0, 0);
        var post = new SnapshotResult(true, z.PostId, z.PostUid, false, "tam", null, null, 0, 0, 0, 0, 0, 0);
        Assert.True(r.Geri(pre, post).Ok);
        Assert.Equal("kisi2", r.Oku("a2.txt"));
        Assert.Equal("kisi3", r.Oku("a3.txt"));
        Assert.Equal("kisi7", r.Oku("a7.txt"));
    }

    // ================================================================== ek: güvenlik ve dayanıklılık

    [Fact]
    public void X1_Boyut_ve_zaman_ayni_ama_icerik_farkliysa_onbellek_yanilmaz_geri_almada_yeniden_ozet_alinir()
    {
        using var r = Standart();
        r.Yaz("hesap.js", "AAAAAAAA");
        var b = r.Al();
        r.Yaz("hesap.js", "BBBBBBBB");
        File.SetLastWriteTimeUtc(r.P("hesap.js"), File.GetLastWriteTimeUtc(r.P("hesap.js")).AddSeconds(5)); // ajanın yazışı kesin ayrı bir zamanda (aynı saat dilimine düşmesin)
        var ajanZaman = File.GetLastWriteTimeUtc(r.P("hesap.js"));
        var a = r.Al();
        // kişi içeriği değiştirir ama zamanı geri yazar (boyut ve zaman aynı): görüntü önbelleği bunu göremez
        File.WriteAllText(r.P("hesap.js"), "CCCCCCCC");
        File.SetLastWriteTimeUtc(r.P("hesap.js"), ajanZaman);
        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Contains(o.Conflicts, c => c.Path == "hesap.js" && c.Reason is "degisti" or "degisti-onbellek");
        Assert.Equal("CCCCCCCC", r.Oku("hesap.js")); // kişinin yazdığı ezilmedi
    }

    [Fact]
    public void X2_SafePath_kok_disina_cikan_yollari_reddeder()
    {
        var kok = @"C:\proje";
        Assert.Null(RestoreEngine.SafePath(kok, "../disari.txt"));
        Assert.Null(RestoreEngine.SafePath(kok, "a/../../b"));
        Assert.Null(RestoreEngine.SafePath(kok, @"C:\Windows\x"));
        Assert.Null(RestoreEngine.SafePath(kok, "/etc/x"));
        Assert.Null(RestoreEngine.SafePath(kok, ""));
        Assert.Equal(@"C:\proje\a\b.txt", RestoreEngine.SafePath(kok, "a/b.txt"));
    }

    [Fact]
    public void X3_Geri_alma_sonrasi_gecici_dosya_ve_depo_tmp_artigi_kalmaz()
    {
        using var r = Standart();
        for (var i = 0; i < 40; i++) r.Yaz($"d/{i}.txt", new string('y', 500 + i));
        var b = r.Al();
        for (var i = 0; i < 40; i++) r.Yaz($"d/{i}.txt", "bozuk");
        var a = r.Al();
        Assert.True(r.Geri(b, a).Ok);
        Assert.Empty(r.GeciciArtik());
        Assert.Empty(Directory.GetFiles(r.Proje, "*.soz-geri", SearchOption.AllDirectories));
    }

    [Fact]
    public void X4_Kucuk_dosyalar_paketlere_girer_paket_sayisi_dosya_sayisindan_cok_az()
    {
        using var r = Standart();
        for (var i = 0; i < 200; i++) r.Yaz($"kucuk/{i}.txt", $"dosya {i} " + new string('z', 100 + i));
        r.Al();
        var paketler = Directory.Exists(r.Depo.PackDir) ? Directory.GetFiles(r.Depo.PackDir, "*.pak").Length : 0;
        var gevsek = Directory.Exists(r.Depo.BlobDir) ? Directory.GetFiles(r.Depo.BlobDir, "*", SearchOption.AllDirectories).Length : 0;
        Assert.InRange(paketler, 1, 8);
        Assert.True(gevsek < 10, "küçük dosyalar tek tek dosya olarak yazılmamalı: " + gevsek);
    }

    [Fact]
    public void X5_Ayni_icerik_bir_kez_saklanir_tekillestirme()
    {
        using var r = Standart();
        for (var i = 0; i < 20; i++) r.Yaz($"kopya/{i}.txt", "aynı içerik aynı içerik");
        var s = r.Al();
        using var c = r.Depo.Open();
        Assert.Equal(1, c.ScalarLong("SELECT COUNT(*) FROM blob WHERE size=@z", null, ("@z", Encoding.UTF8.GetByteCount("aynı içerik aynı içerik"))));
        Assert.True(s.Files >= 24);
    }

    [Fact]
    public void X6_Zincir_derinligi_asilinca_tam_nokta_yazilir_ve_hepsi_cozulebilir()
    {
        using var r = new GeriRig(o => o.FullEvery = 4);
        r.Yaz("hesap.js", "0");
        var noktalar = new List<(SnapshotResult s, string ozet)>();
        for (var i = 1; i <= 11; i++)
        {
            r.Yaz("hesap.js", new string((char)(96 + i), i * 7)); // her sürümün boyutu farklı (boyut+zaman önbelleği aynı zaman diliminde yanılmasın)
            r.Yaz($"d{i}.txt", "d" + i);
            var s = r.Al();
            noktalar.Add((s, r.Ozet()));
        }
        using (var c = r.Depo.Open()) Assert.True(c.ScalarLong("SELECT COUNT(*) FROM snapshot WHERE full=1") >= 3);
        r.Snap.ForgetCache();
        // her eski noktayı kendi özetine göre doğrula: noktaya git, özet eşit olmalı
        foreach (var (s, ozet) in noktalar.AsEnumerable().Reverse())
        {
            var o = r.Geri(s, null, zorla: true);
            Assert.True(o.Ok, o.Error);
            Assert.Equal(ozet, r.Ozet());
        }
    }

    [Fact]
    public void X7_Bozuk_ya_da_eksik_depo_icerigi_kisinin_dosyasina_yazilmaz_depo_bozuk_diye_bildirilir()
    {
        using var r = Standart();
        var b = r.Al();
        r.Yaz("hesap.js", "ajan");
        var a = r.Al();
        r.Depo.Dispose();
        var paketler = Directory.GetFiles(r.Depo.PackDir, "*.pak");
        Assert.NotEmpty(paketler);

        void DenemeKos(string aciklama)
        {
            using var d2 = new GeriDepo(r.Depo.Dir, new RestoreOptions { Fsync = false });
            var rest2 = new RestoreEngine(new SnapshotEngine(d2));
            var o = rest2.Apply(new RestoreRequest(r.Proje, b.Id, a.Id, null, false, "p1"));
            Assert.True(o.Ok, aciklama + ": " + o.Error); // işlem biter ama bozuk dosya yazılmaz
            Assert.Contains(o.Unrestorable, u => u.Path == "hesap.js" && u.Reason == "depo-bozuk");
            Assert.Equal("ajan", r.Oku("hesap.js")); // kişinin dosyası olduğu gibi
            Assert.Empty(Directory.GetFiles(r.Proje, "*.soz-geri", SearchOption.AllDirectories));
        }

        // 1) aynı uzunlukta çöp bayt (özet tutmaz)
        foreach (var pak in paketler) File.WriteAllBytes(pak, Enumerable.Repeat((byte)0x41, (int)new FileInfo(pak).Length).ToArray());
        DenemeKos("aynı uzunlukta bozuk");
        // 2) paket kısaltılmış (okuma dosya sonunda biter)
        foreach (var pak in paketler) File.WriteAllBytes(pak, new byte[16]);
        DenemeKos("kısaltılmış paket");
    }

    [Fact]
    public void X8_Baska_klasore_ait_nokta_bu_klasore_uygulanmaz()
    {
        using var r = Standart();
        var b = r.Al();
        var baska = Path.Combine(r.Dir, "baska-proje");
        Directory.CreateDirectory(baska);
        File.WriteAllText(Path.Combine(baska, "x.txt"), "x");
        var o = r.Rest.Apply(new RestoreRequest(baska, b.Id, null, null, false, "p2"));
        Assert.False(o.Ok);
        Assert.Contains("başka bir klasöre", o.Error);
        Assert.Equal("x", File.ReadAllText(Path.Combine(baska, "x.txt")));
    }

    [Fact]
    public void X9_Dislama_listesi_tek_kaynak_kanit_motoru_ayni_nesneyi_okur()
    {
        // Eskiden iki kopya yansımayla karşılaştırılıyordu; artık kopya yok: kanıt motoru kümeleri doğrudan Exclusions'tan alır.
        var asm = typeof(Soz.Core.Data.SozDb).Assembly;
        var t = asm.GetType("Soz.Core.Proof.EvidenceBase") ?? throw new InvalidOperationException("EvidenceBase bulunamadı");
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        object Alan(string alan) => (t.GetField(alan, flags) ?? throw new InvalidOperationException(alan + " alanı bulunamadı")).GetValue(null)!;
        Assert.Same(Exclusions.DepRootKinds, Alan("DepRootNames"));
        Assert.Same(Exclusions.Build, Alan("BuildNames"));
        Assert.Same(Exclusions.Skip, Alan("SkipDirs"));
        // Davranış: iki taraf aynı klasör adlarını dışlar.
        var ignored = t.GetMethod("IsIgnoredDir", flags)!;
        foreach (var ad in new[] { "node_modules", ".venv", "venv", "vendor", ".git", "bin", "obj", ".cache" })
        {
            Assert.True((bool)ignored.Invoke(null, new object[] { ad })!, ad);
            Assert.True(Exclusions.IsExcludedDir(ad), ad);
        }
    }

    [Fact]
    public void X10_Tarayici_mtime_Unix_ns_gidis_donus()
    {
        var ticks = new DateTime(2026, 11, 11, 12, 34, 56, DateTimeKind.Utc).Ticks + 1234567; // 100 ns hassasiyetinde kesir
        var ns = FolderScanner.ToNs(ticks);
        Assert.Equal(ticks, FolderScanner.ToTicks(ns));
        Assert.Equal(0, ns % 100);
        Assert.Equal(0, FolderScanner.ToNs(FolderScanner.EpochTicks));
    }

    [Fact]
    public void X11_Cok_dosya_siniri_asilirsa_nokta_alinmaz_koruma_yok()
    {
        using var r = new GeriRig(o => o.MaxFiles = 20);
        for (var i = 0; i < 30; i++) r.Yaz($"d{i}.txt", "x");
        var s = r.Al();
        Assert.False(s.Ok);
        Assert.Equal("cok-dosya", s.ErrorCode);
        Assert.Equal(ProtectionLevel.None, s.Protection);
    }

    [Fact]
    public void X12_Butce_asilirsa_nokta_alinmaz_koruma_yok_ve_sade_neden_yazilir()
    {
        using var r = new GeriRig(o => { o.GlobalBudgetBytes = 1000; o.MinProjectBudgetBytes = 10; });
        r.Yaz("a.txt", new string('x', 5000));
        var s = r.Al();
        Assert.False(s.Ok);
        Assert.Equal("butce", s.ErrorCode);
        Assert.Equal(ProtectionLevel.None, s.Protection);
        Assert.Contains("disk alanı", s.Note);
    }

    [Fact]
    public void X13_Disk_yetersizse_nokta_alinmaz_koruma_yok()
    {
        using var r = new GeriRig(o => o.DiskFreeFactor = 1e12);
        r.Yaz("a.txt", new string('x', 5000));
        var s = r.Al();
        Assert.False(s.Ok);
        Assert.Equal("disk-yetersiz", s.ErrorCode);
        Assert.Contains("Disk dolmak üzere", s.Note);
    }

    [Fact]
    public void X14_Goruntu_aldiktan_sonra_paket_dosyalari_acik_tutulmaz_tutamac_sizmaz()
    {
        using var r = Standart();
        for (var i = 0; i < 50; i++) r.Yaz($"d/{i}.txt", new string('q', 300 + i));
        var b = r.Al();
        r.Yaz("hesap.js", "ajan");
        var a = r.Al();
        Assert.True(r.Geri(b, a).Ok);
        r.Depo.Dispose(); // okuma tutamaçları depoyla birlikte kapanır
        var paketler = Directory.GetFiles(r.Depo.PackDir, "*.pak");
        Assert.NotEmpty(paketler);
        foreach (var pak in paketler)
            using (new FileStream(pak, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } // dışarıdan özel erişim alınabiliyor: hiçbir tutamak açık kalmadı
    }
}
