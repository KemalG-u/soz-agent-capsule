using System.Diagnostics;
using System.Text;
using Soz.Core.Restore;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Ölçüm (spike kabul ölçütü): 5 GB, 63 bin dosyalık sentetik küme (R2'nin hazır kopyası urun\_gecici\spike-geri\calisma). Ağır olduğu için yalnız
/// SOZ_GERI_OLCUM=1 ortam değişkeniyle koşar; değişken yoksa test hemen döner (CI'da "geçti" sayılmaz, DURUM.md'ye yalnız koşulmuş sonuç yazılır).
/// Küme geri almayla BİREBİR eski hâline döner (yol kümesi + boyut + yazma zamanı ns), kaynak küme (calisma\repo-inceleme\...\proje) hiç kullanılmaz.
/// Kabul: ilk görüntü ≤ 40 sn, sonraki ≤ 3 sn, geri alma ≤ 3 sn, depo ≤ 3,6 GB.
/// </summary>
public class GeriAlmaOlcumTests
{
    private static readonly string Kok = Environment.GetEnvironmentVariable("SOZ_GERI_OLCUM_KOK")
        ?? Path.Combine(TestEnv.RepoRoot, "_gecici", "spike-geri", "calisma");

    private static string Imza(string root)
    {
        var sc = FolderScanner.Scan(root, 1_000_000);
        var satir = sc.Items.Select(i => (i.IsDir ? "D|" : "F|") + i.Path + "|" + (i.IsDir ? 0 : i.Size) + "|" + (i.IsDir ? 0 : i.MtimeNs)).ToList();
        satir.Sort(StringComparer.Ordinal);
        return satir.Count + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", satir))));
    }

    [Fact]
    public void Olcum_ilk_goruntu_sonraki_goruntu_ve_geri_alma_sureleri()
    {
        if (Environment.GetEnvironmentVariable("SOZ_GERI_OLCUM") != "1") return;
        Assert.True(Directory.Exists(Kok), "ölçüm kümesi yok: " + Kok);
        var cikti = Path.Combine(TestEnv.RepoRoot, "_gecici", "d2b", "olcum");
        Directory.CreateDirectory(cikti);
        var depoDir = Path.Combine(cikti, "depo-" + Guid.NewGuid().ToString("N")[..6]);
        var log = new StringBuilder();
        void Yaz(string s) { log.AppendLine(s); File.WriteAllText(Path.Combine(cikti, "sonuc.txt"), log.ToString()); }

        using var depo = new GeriDepo(depoDir, new RestoreOptions()); // varsayılan: fsync açık, paralellik 4, 1024 MB sınırı
        using var snap = new SnapshotEngine(depo);
        var rest = new RestoreEngine(snap);

        var imza0 = Imza(Kok);
        Yaz("küme imzası (dosya+klasör sayısı|özet): " + imza0[..Math.Min(40, imza0.Length)]);

        // 1) ilk görüntü
        var sw = Stopwatch.StartNew();
        var b = snap.Take(new SnapshotRequest(Kok, "before_run", "olcum", "j1"));
        var ilk = sw.Elapsed.TotalSeconds;
        Assert.True(b.Ok, b.Note);
        Yaz($"ilk görüntü: {ilk:F1} sn | dosya {b.Files} | toplam {b.TotalBytes / 1048576} MB | yeni saklanan {b.NewBytes / 1048576} MB | tarama {b.ScanMs} ms | depolama {b.StoreMs} ms | koruma {b.Protection} atlanan {b.Skipped}");

        // 2) hiç değişiklik yokken sonraki görüntü
        sw.Restart();
        var b2 = snap.Take(new SnapshotRequest(Kok, "before_run", "olcum", "j1"));
        var degismeyen = sw.Elapsed.TotalSeconds;
        Assert.True(b2.Ok);
        Assert.False(b2.IsNew);
        Yaz($"değişiklik yok, sonraki görüntü: {degismeyen:F2} sn");

        // 3) ajan bozar: 200 dosyayı değiştir, 60'ını sil, 100 yenisini yarat, bir klasör adını değiştir
        var sc = FolderScanner.Scan(Kok, 1_000_000);
        var dosyalar = sc.Items.Where(i => !i.IsDir && i.Size > 0 && i.Size < 200_000 && !i.Path.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase)).ToList();
        var degisecek = dosyalar.Where((_, i) => i % Math.Max(1, dosyalar.Count / 260) == 0).Take(260).ToList();
        for (var i = 0; i < 200; i++) File.AppendAllText(Path.Combine(Kok, degisecek[i].Path.Replace('/', '\\')), "\n// ajan ekledi " + i + "\n");
        for (var i = 200; i < 260; i++) File.Delete(Path.Combine(Kok, degisecek[i].Path.Replace('/', '\\')));
        Directory.CreateDirectory(Path.Combine(Kok, "ajan-yeni", "ic"));
        for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(Kok, "ajan-yeni", "ic", $"y{i}.txt"), "yeni " + i);
        Yaz("ajan bozdu: 200 dosya değişti, 60 silindi, 100 yeni (ajan-yeni\\ic)");

        sw.Restart();
        var a = snap.Take(new SnapshotRequest(Kok, "after_run", "olcum", "j1"));
        var sonraki = sw.Elapsed.TotalSeconds;
        Assert.True(a.Ok);
        Assert.True(a.IsNew);
        Yaz($"sonraki görüntü (360 değişiklik): {sonraki:F2} sn | tarama {a.ScanMs} ms | depolama {a.StoreMs} ms | yeni saklanan {a.NewBytes} bayt");

        // 4) geri al
        sw.Restart();
        var o = rest.Apply(new RestoreRequest(Kok, b.Id, a.Id, null, false, "olcum", "j1"));
        var geri = sw.Elapsed.TotalSeconds;
        Assert.True(o.Ok, o.Error);
        Yaz($"geri alma: {geri:F2} sn (önce nokta {o.PreMs} ms; toplam {o.TotalMs} ms) | geri yazılan {o.Restored} | silinen {o.Removed} | klasör silinen {o.DirsRemoved} | çakışma {o.Conflicts.Count} | alınamayan {o.Unrestorable.Count}");

        var imza1 = Imza(Kok);
        Yaz("geri almadan sonra küme imzası eşit: " + (imza0 == imza1));
        var diskBayt = Directory.EnumerateFiles(depoDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Yaz($"depo boyutu: {diskBayt / 1048576} MB (hedef ≤ 3686 MB)");
        Assert.Empty(Directory.GetFiles(Kok, "*.soz-geri", SearchOption.AllDirectories));

        Assert.Equal(imza0, imza1);
        Assert.True(ilk <= 40, $"ilk görüntü {ilk:F1} sn > 40 sn");
        Assert.True(sonraki <= 3, $"sonraki görüntü {sonraki:F2} sn > 3 sn");
        Assert.True(degismeyen <= 3, $"değişmeyen görüntü {degismeyen:F2} sn > 3 sn");
        Assert.True(geri <= 3, $"geri alma {geri:F2} sn > 3 sn");
        Assert.True(diskBayt <= 3686L << 20, "depo 3,6 GB üstü");

        // kendi ürettiğimiz depoyu temizle (yalnız bu testin olcum alt klasöründeki depo-* klasörü; ölçülen küme silinmez)
        depo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var eski in Directory.GetDirectories(cikti, "depo-*")) Directory.Delete(eski, recursive: true); // bu testin önceki koşularından kalanlar da (hepsi cikti = _gecici/d2b/olcum altında)
        Yaz("ölçüm deposu silindi: " + !Directory.Exists(depoDir));
    }
}
