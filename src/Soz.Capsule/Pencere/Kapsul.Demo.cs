namespace Soz.Capsule;

/// <summary>
/// --demo: motor olmadan bütün durumlar sırayla (uyku, soluk, belirgin, dikkat, 2-3 bekliyor, tekerlek, ön bakış, çalışıyor, dikte,
/// giriş, sağ tık panosu) ve üç tema. Süreler demo için kısaltılır (idle 3/6 sn); --demo-gercek-sure ile ayar süreleri kullanılır.
/// Odak çalınmaz, fare ya da klavye gönderilmez: mantık doğrudan sürülür.
/// </summary>
internal sealed partial class Kapsul
{
    private sealed record DemoAdim(int Ms, string Ad, Action Is, bool Goruntu = false);
    private List<DemoAdim> _demo = new();
    private int _demoSira;
    private long _demoT0;
    private readonly List<(long Ms, string Ad)> _goruntuBekleyen = new();
    private int _goruntuNo;

    private static Is J(string id, IsTuru t, string baslik, string kisa) => new(id, t, baslik, kisa);

    private void DemoBaslat()
    {
        var kaldi = J("j:1", IsTuru.Kaldi, "Ödeme sayfası testleri", "ödeme testi");
        var bitti = J("j:2", IsTuru.Bitti, "Giriş ekranı düzeltmesi", "giriş düzeltmesi");
        var karar = J("j:3", IsTuru.Karar, "Veritabanı: eskisi mi yenisi mi?", "veritabanı kararı");
        var soru = J("j:4", IsTuru.Soru, "Hangi sürüm numarası?", "sürüm sorusu");
        var c1 = J("j:5", IsTuru.Calisiyor, "Arayüz derlemesi", "derleme");
        var c2 = J("j:6", IsTuru.Calisiyor, "Rapor yazımı", "rapor");
        var c3 = J("j:7", IsTuru.Calisiyor, "Test koşusu", "test");
        void Isler(params Is[] l) { _m.IsleriGuncelle(l, Zaman.Simdi); }

        _demo = new List<DemoAdim>
        {
            new(0, "belirgin", () => { }, true),
            new(3600, "soluk (otomatik)", () => { }, true),
            new(9800, "uyku (otomatik)", () => { }, true),
            new(11000, "dikkat: 1 bitti", () => Isler(bitti), true),
            new(16500, "soluk durumlu (otomatik)", () => { }, true),
            new(17000, "2 bekliyor", () => Isler(bitti, karar), true),
            new(21000, "tekerlek", () => _m.Tekerlek(1, Zaman.Simdi), true),
            new(23000, "tema mürekkep, 3 bekliyor", () => { TemaDegistir("murekkep"); Isler(kaldi, karar, soru); }, true),
            new(25000, "ön bakış (üstünde durma)", () => { _m.HoverGirdi(Zaman.Simdi, 100, 100); }, true),
            new(27500, "ön bakış kapanır", () => { _m.OnBakisUstunde(false, Zaman.Simdi); _m.HoverCikti(Zaman.Simdi); }),
            new(28500, "tema kâğıt, 3 çalışıyor", () => { TemaDegistir("kagit"); Isler(c1, c2, c3); }, true),
            new(30000, "dikte dinliyor", () => _m.DikteBaslat(Zaman.Simdi), true),
            new(31500, "dikte çeviriyor", () => _m.DikteFaz(DikteFazi.Ceviriyor, Zaman.Simdi)),
            new(32300, "dikte yazdı", () => _m.DikteFaz(DikteFazi.Yazdi, Zaman.Simdi), true),
            new(33200, "dikte bitti, giriş satırı", () => { _m.DikteBitir(Zaman.Simdi); _m.GirisAc(Zaman.Simdi); Isle(); _giris.Ekle("testleri yeniden koş"); GirisCiz(true); }, true),
            new(35200, "giriş kapanır", () => { _giris.Temizle(); _m.GirisKapat(Zaman.Simdi); }),
            new(36300, "sağ tık panosu", () => _m.MenuAc(Zaman.Simdi), true),
            new(38000, "pano kapanır, tema cam", () => { _m.MenuKapat(Zaman.Simdi); TemaDegistir("cam"); Isler(Array.Empty<Is>()); }),
        };
        _demoSira = 0; _demoT0 = Zaman.Simdi;
        _m.Isler = Array.Empty<Is>();
        N.SetTimer(_hwnd, T_DEMO, 100, IntPtr.Zero);
        Gunluk.Yaz("demo başladı");
    }

    private void DemoAdimi()
    {
        var gecen = Zaman.Simdi - _demoT0;
        while (_demoSira < _demo.Count && _demo[_demoSira].Ms <= gecen)
        {
            var a = _demo[_demoSira++];
            Gunluk.Yaz($"demo {gecen / 1000.0:0.0} sn: {a.Ad}");
            try { a.Is(); } catch (Exception e) { Gunluk.Yaz("demo adımı hata: " + e); }
            Isle();
            if (a.Goruntu && _o.GoruntuKlasoru != null) _goruntuBekleyen.Add((Zaman.Simdi + 1100, a.Ad));
        }
        for (var i = _goruntuBekleyen.Count - 1; i >= 0; i--)
        {
            if (_goruntuBekleyen[i].Ms > Zaman.Simdi) continue;
            var (_, ad) = _goruntuBekleyen[i]; _goruntuBekleyen.RemoveAt(i);
            GoruntuAl(ad);
        }
        if (_demoSira >= _demo.Count && _goruntuBekleyen.Count == 0) N.KillTimer(_hwnd, T_DEMO);
    }

    private void GoruntuAl(string ad)
    {
        if (_o.GoruntuKlasoru == null || _sonG == null) return;
        var r = KapsulDik(_sonG); var tum = SanalMasaustu();
        var w = (int)(560 * _olcek); var h = (int)(420 * _olcek);
        var x = (int)(_konum.Taraf == Taraf.Sag ? r.R - w + 40 * _olcek : r.L - 40 * _olcek);
        var y = (int)(r.MerkezY - h / 2.0);
        x = (int)Math.Clamp(x, tum.L, Math.Max(tum.L, tum.R - w)); y = (int)Math.Clamp(y, tum.T, Math.Max(tum.T, tum.B - h));
        var dosya = Path.Combine(_o.GoruntuKlasoru, $"{++_goruntuNo:00}-{Ascii(ad)}.png");
        var tamam = Goruntu.Kaydet(dosya, x, y, w, h);
        Gunluk.Yaz($"görüntü {(tamam ? "yazıldı" : "yazılamadı")}: {dosya}");
    }

    private static string Ascii(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.ToLowerInvariant())
            sb.Append(c switch { 'ı' => 'i', 'ş' => 's', 'ğ' => 'g', 'ü' => 'u', 'ö' => 'o', 'ç' => 'c', 'â' => 'a', ' ' or '(' or ')' or ',' or ':' => '-', _ => c });
        return sb.ToString().Trim('-');
    }
}
