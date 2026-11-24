namespace Soz.Capsule;

/// <summary>
/// P2 girdi sözleşmesinin işaretçi (fare, dokunma) kısmı: basma, 4 dp eşik, sürükleme ya da uzatma, tık, çift tık.
/// Saf mantık: gerçek fare olayı gerekmez, testte doğrudan çağrılır. Koordinatlar fiziksel piksel, ölçek px/dp.
/// </summary>
internal sealed class PointerYonetici
{
    private readonly KapsulMantik _m;
    public PointerYonetici(KapsulMantik m) { _m = m; }

    public bool Basili { get; private set; }
    public bool Tasindi { get; private set; }
    public bool Uzatiyor { get; private set; }
    public bool Suruklenir => Tasindi && !Uzatiyor;

    /// <summary>Sürükleme sırasında kapsül çapasının hedefi (px). Pencere katmanı yayla buna doğru gider.</summary>
    public (double Ax, double Cy) SurukleHedefi { get; private set; }

    /// <summary>Sürüklemeyi sınırlayan alan (px): sanal masaüstü. Boşsa sınırsız.</summary>
    public Dik? Sinirlar { get; set; }

    public bool UykuluTik { get; private set; }

    private double _x0, _y0, _grabDx, _grabRx, _cy0, _w0Dp, _hDp;
    private Dik _rect0;
    private Taraf _taraf;
    private double _olcek = 1;
    private bool _stretchAdayi;
    private long _sonTikMs = long.MinValue; private double _sonTikX, _sonTikY;

    /// <summary>Sol düğme basıldı. kapsul: şu anki görünen dikdörtgen (px). taraf: büyüme yönü.</summary>
    public void SolBasildi(long now, double px, double py, Dik kapsul, double olcek, Taraf taraf, Seviye lvl)
    {
        _olcek = olcek; _taraf = taraf; _rect0 = kapsul;
        Basili = true; Tasindi = false; Uzatiyor = false;
        _x0 = px; _y0 = py;
        _hDp = kapsul.H / olcek;
        var serbestUc = taraf == Taraf.Sol ? kapsul.R : kapsul.L;
        _stretchAdayi = !_m.Giris && lvl != Seviye.Uyku && kapsul.W >= _m.Ayar.Big * .9 * olcek && Math.Abs(px - serbestUc) <= _m.Ayar.StretchGrip * olcek;
        UykuluTik = _m.TikUykulu(now);
    }

    /// <summary>İşaretçi hareket etti (düğme basılıyken). Eşik aşılınca sürükleme ya da uzatma başlar.</summary>
    public void Hareket(long now, double px, double py)
    {
        if (!Basili) return;
        var dist = Math.Sqrt((px - _x0) * (px - _x0) + (py - _y0) * (py - _y0)) / _olcek;
        if (!Tasindi && dist > _m.Ayar.DragThreshold)
        {
            Tasindi = true;
            if (_stretchAdayi) { Uzatiyor = true; _w0Dp = _rect0.W / _olcek; _m.UzatmaBasla(); }
            else
            {
                _grabDx = _x0 - _rect0.L; _grabRx = _rect0.R - _x0; _cy0 = _rect0.MerkezY - _y0;
                _m.SurukleBasla(now);
            }
        }
        if (!Tasindi) return;
        if (Uzatiyor)
        {
            var dxDp = (px - _x0) / _olcek;
            var istek = _w0Dp + (_taraf == Taraf.Sol ? dxDp : -dxDp);
            var en = Math.Min(_m.Ayar.GirisW, _m.EkranGenislikDp - 12);
            _m.UzatmaGuncelle(Math.Clamp(istek, _hDp, Math.Max(_hDp, en)));
        }
        else
        {
            var ax = _taraf == Taraf.Sol ? px - _grabDx : px + _grabRx;
            var cy = py + _cy0;
            if (Sinirlar is { } s)
            {
                var h = _rect0.H; var w = _rect0.W;
                var r = KonumHesap.Dikdortgen(ax, cy, w, h, _taraf);
                r = KonumHesap.Sikistir(r, s, 2);
                ax = _taraf == Taraf.Sol ? r.L : r.R; cy = r.MerkezY;
            }
            SurukleHedefi = (ax, cy);
        }
    }

    public enum Sonuc { Yok, Tik, CiftTik, SurukleBitti, UzatmaBitti }

    /// <summary>Sol düğme bırakıldı. Tık, çift tık ya da sürükleme sonu. Sürükleme sonunda çağıran <c>KonumHesap.Birak</c> ile yapıştırır.</summary>
    public Sonuc SolBirakildi(long now, double px, double py)
    {
        if (!Basili) return Sonuc.Yok;
        Basili = false;
        if (!Tasindi)
        {
            var yakin = Math.Sqrt((px - _sonTikX) * (px - _sonTikX) + (py - _sonTikY) * (py - _sonTikY)) / _olcek <= _m.Ayar.DwellTol * 2;
            if (now - _sonTikMs <= (long)_m.Ayar.DoubleClickMs && yakin)
            {
                _sonTikMs = long.MinValue;
                _m.CiftTik(now);
                return Sonuc.CiftTik;
            }
            _sonTikMs = now; _sonTikX = px; _sonTikY = py;
            _m.SolTik(now, UykuluTik);
            return Sonuc.Tik;
        }
        Tasindi = false;
        if (Uzatiyor) { Uzatiyor = false; _m.UzatmaBitir(now, _hDp); return Sonuc.UzatmaBitti; }
        _m.SurukleBitir(now, px / _olcek, py / _olcek);
        return Sonuc.SurukleBitti;
    }

    /// <summary>Basış iptal oldu (yakalama kaybı).</summary>
    public void Iptal(long now)
    {
        if (!Basili) return;
        var s = Suruklenir; var u = Uzatiyor;
        Basili = false; Tasindi = false; Uzatiyor = false;
        if (u) _m.UzatmaBitir(now, _hDp); else if (s) _m.SurukleBitir(now, 0, 0);
    }
}
