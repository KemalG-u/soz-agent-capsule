using System.Diagnostics;
using System.Text.Json;

namespace Soz.Capsule;

/// <summary>
/// --olc &lt;sonuc.json&gt;: ölçüm kipi. Gerçek fare ya da klavye olayı göndermez; sürükleme mantığa ve yaya sentetik hedef verilerek yapılır
/// (pencere gerçekten hareket eder, DWM gerçekten kare üretir). Sonuç JSON olarak yazılır ve bütçelerle karşılaştırılır.
/// </summary>
internal sealed partial class Kapsul
{
    private sealed class YapaySurukle { public long T0; public double Ax0, Cy0; public double Sure = 4000; }
    private YapaySurukle? _yapay;

    private readonly Dictionary<string, object> _sonuc = new();
    private List<Func<int>> _olcAdimlar = new();
    private int _olcSira; private long _olcBekle;
    private long _zamanlayiciUyanma;
    private double _cpu0, _bosta0Ms; private long _kare0, _uyanma0;
    private readonly Stopwatch _sw = new();

    private void YapayAdim()
    {
        var y = _yapay!;
        var t = Math.Min(1.0, (Zaman.Simdi - y.T0) / y.Sure);
        var a = 250 * _olcek; var b = 120 * _olcek;
        _ax.T = y.Ax0 + a * Math.Sin(2 * Math.PI * 1.5 * t);
        _cy.T = y.Cy0 + b * Math.Sin(2 * Math.PI * 2.0 * t);
    }

    private void OlcBaslat()
    {
        var bostaMs = _o.OlcHizli ? 8000 : 60000;
        var mb = () => N.Bellek();
        _olcAdimlar = new List<Func<int>>
        {
            () => 5000,                                       // uykuya yerleşsin
            () =>
            {
                var b = mb(); _sonuc["bellek_bosta_baslangic_mb"] = Y(b.OzelCalismaMB); _sonuc["taahhut_mb"] = Y(b.TaahhutMB);
                _cpu0 = SurecZamani.IslemciMs(); _bosta0Ms = Zaman.SimdiHassas; _kare0 = KareSayaci; _uyanma0 = _zamanlayiciUyanma;
                return bostaMs;
            },
            () =>
            {
                var cpu = SurecZamani.IslemciMs() - _cpu0; var sure = Zaman.SimdiHassas - _bosta0Ms;
                var b = mb();
                _sonuc["bellek_bosta_mb"] = Y(b.OzelCalismaMB);
                _sonuc["bosta_sure_sn"] = Y(sure / 1000);
                _sonuc["bosta_cpu_ms"] = Y(cpu);
                _sonuc["bosta_cpu_yuzde_tek_cekirdek"] = Y(100 * cpu / sure, 3);
                _sonuc["bosta_cpu_yuzde_toplam"] = Y(100 * cpu / sure / Environment.ProcessorCount, 3);
                _sonuc["bosta_cizim_karesi"] = KareSayaci - _kare0;
                _sonuc["bosta_zamanlayici_uyanmasi"] = _zamanlayiciUyanma - _uyanma0;
                _sonuc["bosta_iplik_sayisi"] = Process.GetCurrentProcess().Threads.Count;
                return 0;
            },
            () =>
            {
                _sw.Restart();
                _m.IsleriGuncelle(new[] { J("j:1", IsTuru.Bitti, "Giriş ekranı", "giriş"), J("j:2", IsTuru.Karar, "Veritabanı", "veritabanı") }, Zaman.Simdi);
                Isle();
                _sonuc["yazi_ilk_cizim_ms"] = Y(_sw.Elapsed.TotalMilliseconds);
                return 2500;
            },
            () =>
            {
                var b = mb(); _sonuc["bellek_yazi_belirgin_mb"] = Y(b.OzelCalismaMB);
                _sonuc["yazi_hatasi"] = _gorsel.YaziHatasi ?? "yok";
                return 4500;
            },
            () => { var b = mb(); _sonuc["bellek_yazi_soluk_mb"] = Y(b.OzelCalismaMB); return 0; },
            () => // 4 sn sentetik sürükleme
            {
                KareAraliklari.Clear(); KareIsMs.Clear(); KareKaydi = true;
                var r = KapsulDik(_sonG);
                _yapay = new YapaySurukle { T0 = Zaman.Simdi, Ax0 = _ax.X, Cy0 = _cy.X };
                _m.SurukleBasla(Zaman.Simdi); Isle(); KareIste();
                return 4300;
            },
            () =>
            {
                _yapay = null; KareKaydi = false; _m.SurukleBitir(Zaman.Simdi, 0, 0); HedefKonumuYenile(false); Isle();
                var l = KareAraliklari.Skip(3).OrderBy(x => x).ToList();
                if (l.Count > 0)
                {
                    _sonuc["surukle_kare_sayisi"] = l.Count;
                    _sonuc["surukle_kare_sn"] = Y(l.Count / (l.Sum() / 1000), 1);
                    _sonuc["surukle_kare_p50_ms"] = Y(Yuzdelik(l, 50)); _sonuc["surukle_kare_p95_ms"] = Y(Yuzdelik(l, 95));
                    _sonuc["surukle_kare_p99_ms"] = Y(Yuzdelik(l, 99)); _sonuc["surukle_kare_en_uzun_ms"] = Y(l[^1]);
                    var k = KareIsMs.Skip(3).OrderBy(x => x).ToList();
                    if (k.Count > 0) { _sonuc["surukle_kare_isi_p95_ms"] = Y(Yuzdelik(k, 95)); }
                }
                _m.IsleriGuncelle(Array.Empty<Is>(), Zaman.Simdi);
                return 800;
            },
            () => { _m.Uyut(Zaman.Simdi); Isle(); return 3500; },   // tıkanma A hazırlık: uyku
            () => TikanmaBasla(0, () => { _m.Uyandir(Zaman.Simdi); _m.BostaSifirla(Zaman.Simdi); Isle(); }),
            () => { _sonuc["tikanma_basta"] = TikanmaBitir(); return 0; },
            () => { _m.Uyut(Zaman.Simdi); Isle(); return 3500; },
            () => TikanmaBasla(120, () => { _m.IsleriGuncelle(new[] { J("j:9", IsTuru.Bitti, "Bitti", "bitti") }, Zaman.Simdi); Isle(); }),
            () => { _sonuc["tikanma_ortada"] = TikanmaBitir(); return 0; },
            () => { Bitir(); return 0; },
        };
        _olcSira = 0; _olcBekle = Zaman.Simdi + 1500;
        N.SetTimer(_hwnd, T_OLC, 100, IntPtr.Zero);
    }

    private void OlcAdimi()
    {
        if (Zaman.Simdi < _olcBekle) return;
        if (_olcSira >= _olcAdimlar.Count) { N.KillTimer(_hwnd, T_OLC); return; }
        int bekle;
        try { bekle = _olcAdimlar[_olcSira++](); }
        catch (Exception e) { Gunluk.Yaz("ölçüm adımı hata: " + e); _sonuc["hata_" + _olcSira] = e.Message; bekle = 0; }
        _olcBekle = Zaman.Simdi + bekle;
    }

    private sealed class TikanmaOturumu
    {
        public readonly List<(double T, ulong H)> Ornek = new();
        public volatile bool Dur;
        public readonly Stopwatch Sw = Stopwatch.StartNew();
        public double T0, TikBas, TikSon; public Thread? Is;
    }
    private TikanmaOturumu? _tik;
    private int _blokGecikme;

    /// <summary>
    /// Arayüz iş parçacığı 200 ms tıkanınca DWM yayı sürüyor mu? Yay başlatılır, iş parçacığı mesaj döngüsüne dönüp Composition'ın değişikliği
    /// DWM'ye göndermesine izin verir (gerçek tıkanma da hareket başladıktan sonra olur), sonra <paramref name="gecikmeMs"/> + 30 ms'de
    /// iş parçacığı 200 ms uyutulur. Bu sırada başka iş parçacığı kapsül çevresindeki ekran pikselini ~30 ms'de bir okur.
    /// Tıkanma içindeki farklı görüntü sayısı ≥ 3 ve son görüntü ilkinden farklıysa yay sürdü. Kapsülün arkasında başka bir şey değişiyorsa
    /// (video gibi) sayı şişebilir; kapsülsüz zemin sabit varsayılır.
    /// </summary>
    private int TikanmaBasla(int gecikmeMs, Action baslat)
    {
        var r = KapsulDik(_sonG);
        var x = (int)(r.L - 24 * _olcek); var y = (int)(r.MerkezY - 40 * _olcek);
        var w = (int)((_ayar.PillMax + 48) * _olcek); var h = (int)(80 * _olcek);
        if (_konum.Taraf == Taraf.Sag) x = (int)(r.R - w + 24 * _olcek);
        var tum = SanalMasaustu();
        x = (int)Math.Clamp(x, tum.L, Math.Max(tum.L, tum.R - w)); y = (int)Math.Clamp(y, tum.T, Math.Max(tum.T, tum.B - h));
        var o = new TikanmaOturumu(); _tik = o;
        o.Is = new Thread(() => { while (!o.Dur) { var t = o.Sw.Elapsed.TotalMilliseconds; var oz = Goruntu.Ozet(x, y, w, h); lock (o.Ornek) o.Ornek.Add((t, oz)); Thread.Sleep(10); } }) { IsBackground = true };
        o.Is.Start();
        Thread.Sleep(120);                                  // taban ölçümü: değişmeyen zemin
        o.T0 = o.Sw.Elapsed.TotalMilliseconds;
        baslat();
        _blokGecikme = gecikmeMs;
        N.SetTimer(_hwnd, T_BLOK, (uint)Math.Max(10, gecikmeMs + 30), IntPtr.Zero);
        return 1800;                                        // blok + yay oturması + son örnekler
    }

    private void BlokYap()
    {
        N.KillTimer(_hwnd, T_BLOK);
        var o = _tik; if (o == null) return;
        o.TikBas = o.Sw.Elapsed.TotalMilliseconds;
        Thread.Sleep(200);                                  // arayüz iş parçacığı tıkalı
        o.TikSon = o.Sw.Elapsed.TotalMilliseconds;
    }

    private Dictionary<string, object> TikanmaBitir()
    {
        var o = _tik!; _tik = null;
        Thread.Sleep(50);
        o.Dur = true; o.Is!.Join(2000);
        List<(double T, ulong H)> l; lock (o.Ornek) l = o.Ornek.ToList();
        var tik = l.Where(e => e.T >= o.TikBas && e.T <= o.TikSon).Select(e => e.H).ToList();
        var oncesi = l.Where(e => e.T < o.T0).Select(e => e.H).Distinct().Count();
        var farkli = tik.Distinct().Count();
        var son = l.Count > 0 ? l[^1].H : 0UL; var ilk = l.Count > 0 ? l[0].H : 0UL;
        var toplamFarkli = l.Select(e => e.H).Distinct().Count();
        return new Dictionary<string, object>
        {
            ["tikanma_ms"] = Y(o.TikSon - o.TikBas), ["tikanma_baslangici_ms"] = Y(o.TikBas - o.T0),
            ["tikanma_icinde_ornek"] = tik.Count, ["tikanma_icinde_farkli_goruntu"] = farkli,
            ["tum_olcumde_farkli_goruntu"] = toplamFarkli, ["baslangictan_once_farkli_goruntu"] = oncesi,
            ["son_goruntu_ilkinden_farkli"] = son != ilk,
            ["yay_suruyor"] = farkli >= 3 && son != ilk,
        };
    }

    private void Bitir()
    {
        _sonuc["ilk_kare_ms"] = Y(IlkKareMs);
        var ts = new List<(string Ad, string Olculen, string Butce, bool Gecti)>();
        double G(string k) => _sonuc.TryGetValue(k, out var v) && v is double d ? d : double.NaN;
        ts.Add(("Boşta özel çalışma kümesi", $"{G("bellek_bosta_mb"):0.0} MB", "< 40 MB", G("bellek_bosta_mb") < 40));
        ts.Add(("Yazılı hapla özel çalışma kümesi (belirgin)", $"{G("bellek_yazi_belirgin_mb"):0.0} MB", "< 40 MB", G("bellek_yazi_belirgin_mb") < 40));
        ts.Add(("Yazılı hapla özel çalışma kümesi (soluk)", $"{G("bellek_yazi_soluk_mb"):0.0} MB", "< 40 MB", G("bellek_yazi_soluk_mb") < 40));
        ts.Add(("İlk kare", $"{G("ilk_kare_ms"):0} ms", "< 150 ms", G("ilk_kare_ms") < 150));
        ts.Add(("Boşta işlemci (tek çekirdek)", $"%{G("bosta_cpu_yuzde_tek_cekirdek"):0.000}", "< %0,5", G("bosta_cpu_yuzde_tek_cekirdek") < 0.5));
        ts.Add(("Boşta çizim karesi", $"{_sonuc.GetValueOrDefault("bosta_cizim_karesi")}", "0", _sonuc.GetValueOrDefault("bosta_cizim_karesi") is long l && l == 0));
        ts.Add(("Sürükleme kare p95", $"{G("surukle_kare_p95_ms"):0.0} ms", "bilgi (60 Hz = 16,7)", G("surukle_kare_p95_ms") < 25));
        ts.Add(("Yazı ilk çizim", $"{G("yazi_ilk_cizim_ms"):0.0} ms", "bilgi", G("yazi_ilk_cizim_ms") < 150));
        bool Yay(string k) => _sonuc.TryGetValue(k, out var v) && v is Dictionary<string, object> d && d.TryGetValue("yay_suruyor", out var s) && s is true;
        ts.Add(("Tıkanınca yay sürüyor (başta)", Yay("tikanma_basta") ? "evet" : "hayır", "evet", Yay("tikanma_basta")));
        ts.Add(("Tıkanınca yay sürüyor (ortada)", Yay("tikanma_ortada") ? "evet" : "hayır", "evet", Yay("tikanma_ortada")));
        _sonuc["ozet"] = ts.Select(t => new Dictionary<string, object> { ["olcut"] = t.Ad, ["olculen"] = t.Olculen, ["butce"] = t.Butce, ["gecti"] = t.Gecti }).ToList();
        _sonuc["olcek"] = _olcek; _sonuc["ekran_sayisi"] = _ekranlar.Count; _sonuc["islemci_cekirdek"] = Environment.ProcessorCount;
        _sonuc["tema"] = _ayar.Tema; _sonuc["hizli_kip"] = _o.OlcHizli;
        var json = JsonSerializer.Serialize(_sonuc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        try { if (_o.OlcSonuc != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_o.OlcSonuc))!); File.WriteAllText(_o.OlcSonuc, json); } }
        catch (IOException e) { Gunluk.Yaz("sonuç yazılamadı: " + e.Message); }
        Gunluk.Yaz("ölçüm bitti");
        N.SetTimer(_hwnd, T_KAPAT, 300, IntPtr.Zero);
    }

    private static double Y(double v, int basamak = 2) => Math.Round(v, basamak);
    private static double Yuzdelik(List<double> sirali, double p) => sirali.Count == 0 ? 0 : sirali[Math.Clamp((int)Math.Ceiling(p / 100 * sirali.Count) - 1, 0, sirali.Count - 1)];
}
