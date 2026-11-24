using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Soz.Capsule;

/// <summary>
/// Kapsül penceresi ve uygulama döngüsü. Mantık (<see cref="KapsulMantik"/>, <see cref="PointerYonetici"/>) saf ve ayrıdır; burası yalnız
/// Windows'a bağlar: pencere iletileri, zamanlayıcılar, ekran bilgisi, motor, açılır yüzeyler, kısayol, bekçi.
/// Boşta bu süreç hiçbir kare üretmez: tek tek zamanlayıcılar (en çok 100 ms yaklaşma, 1,5 sn bekçi) ve bekleyen ileti döngüsü vardır.
/// </summary>
internal sealed unsafe partial class Kapsul
{
    private const uint WM_FRAME = N.WM_APP + 1, WM_ISLER = N.WM_APP + 2, WM_POSTED = N.WM_APP + 3;
    private const int T_MANTIK = 1, T_YAKLASMA = 2, T_BEKCI = 3, T_CIKIS = 5, T_YOKET = 6, T_DEMO = 7, T_TIK = 8, T_GIZLE = 9, T_OLC = 10, T_KAPAT = 11, T_BLOK = 12;
    private static readonly N.WndProc s_proc = StaticProc;
    private static Kapsul? s_self;

    private readonly Secenekler _o;
    private readonly Ayar _ayar = new();
    private readonly string _durumKlasoru;
    private string? _ayarDosya; private DateTime _ayarMtime;
    private KapsulMantik _m = null!;
    private PointerYonetici _ptr = null!;
    private KonumDurumu _konum = KonumDurumu.Varsayilan();
    private List<EkranBilgi> _ekranlar = new();
    private IntPtr _hwnd;
    private double _olcek = 1;
    private Gorsel _gorsel = null!;
    private YaziCizici _cizici = null!;
    private TemaKip _tema = null!;
    private bool _acikSistem;
    private Yay _ax, _cy;
    private Gorunum? _sonG;
    private bool _kaydet;
    private KapsulUia? _uia;
    private MotorIstemci? _motor;
    private PanelKoprusu _kopru = null!;
    private KabukKanali? _kanal;          // masaüstü programı kapsülü başlattıysa (--kabuk) komut ve olay yolu
    private string _projeAdi = "";        // masaüstü programının söylediği etkin proje (ön bakış ve ekran okuyucu)
    private long _girisHedefPencere;      // yazı satırı açılırken öndeki pencere (metin oraya yapıştırılsın)
    private string _hedefEtiketi = "Proje seç";
    private readonly GirisMetni _giris = new();
    private IntPtr _oncekiOn;
    private Acilir? _onBakis, _menu;
    private bool _tracking;
    private readonly System.Collections.Concurrent.ConcurrentQueue<MotorTeslimi> _teslimler = new();
    private string? _sonIcerikGunlugu;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _kuyruk = new();
    private uint _taskbarCreated;
    private bool _sistemAzalt;
    private long _sonLum, _sonYaziKullanim, _sonUstteMs;
    private const long UstteYenilemeMs = 5000;
    private bool _yaziKullanildi;
    private bool _yaklasmaAktif, _tikAktif;
    private bool _prevSol, _prevSag;
    private double _sonMesafe = -1;
    private bool _dpiBekliyor; private double _bekleyenOlcek;
    private bool _hcAcik;
    private long _kaydetSon;

    // kare saati (yalnız sürükleme ve yerleşme sırasında)
    private readonly ManualResetEventSlim _kapi = new(false);
    private volatile bool _cik;
    private int _kareBekliyor;
    private long _sonKare;
    public long KareSayaci;
    public readonly List<double> KareAraliklari = new();
    public readonly List<double> KareIsMs = new();
    public bool KareKaydi;

    public double IlkKareMs { get; private set; } = -1;
    public KapsulMantik Mantik => _m;
    public Ayar AyarNesnesi => _ayar;
    public IntPtr Hwnd => _hwnd;
    public double Olcek => _olcek;
    public Gorsel GorselNesnesi => _gorsel;

    public Kapsul(Secenekler o)
    {
        _o = o;
        _durumKlasoru = o.DurumKlasoru ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soz", "Kapsul");
        _kaydet = !o.Demo && !o.Olc;
    }

    // ------------------------------------------------------------------ açılış
    public int Calistir()
    {
        s_self = this;
        Gunluk.Ac(_o.GunlukDosyasi);
        AyarYukle(ilk: true);
        _ekranlar = Sistem.Ekranlar();
        if (_kaydet) { try { _konum = KonumDurumu.JsonOku(File.ReadAllText(Path.Combine(_durumKlasoru, "konum.json"))) ?? _konum; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        var ek = KonumHesap.EkranKimlikle(_konum.EkranId, _ekranlar);
        _olcek = ek?.Olcek ?? N.GetDpiForSystem() / 96.0;
        _sistemAzalt = Sistem.SistemHareketAzalt();
        _hcAcik = Sistem.YuksekKarsitlik();
        TemaHesapla();

        _cizici = new YaziCizici();
        _m = new KapsulMantik(_ayar, YaziGenisligiDp);
        _m.ZeminParlaklik = _tema.Koyu ? 0.15 : 0.85;
        _m.EkranGenislikDp = (ek?.Calisma.W ?? 1920) / _olcek;
        _ptr = new PointerYonetici(_m);
        if (_o.Kabuk && !_o.Demo && !_o.Olc) _kanal = new KabukKanali(e => Gonder(() => KabukOlayiIsle(e)));
        _kopru = new PanelKoprusu(_o.PanelKomut, _kanal);
        _kopru.KonumVer = () => KapsulDik();

        var hInst = N.GetModuleHandleW(null);
        var wc = new N.WNDCLASSEXW { cbSize = Marshal.SizeOf<N.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc), hInstance = hInst, hCursor = N.LoadCursorW(IntPtr.Zero, 32512), lpszClassName = "SozKapsul" };
        N.RegisterClassExW(ref wc);
        var tuvalW = (int)Math.Round((_ayar.GirisW + 2 * Gorsel.GolgePay + 8) * _olcek); var tuvalH = (int)Math.Round(Gorsel.TuvalYukseklik * _olcek);
        _hwnd = N.CreateWindowExW(N.WS_EX_NOREDIRECTIONBITMAP | N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW | N.WS_EX_TOPMOST, "SozKapsul", "Söz kapsülü", N.WS_POPUP, 0, 0, tuvalW, tuvalH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { Gunluk.Yaz("pencere oluşturulamadı: " + Marshal.GetLastWin32Error()); return 2; }
        // görev çubuğu önizlemesi / Aero Peek sırasında kapsül gizlenmesin
        Gunluk.Yaz(Sistem.PeekDisiBirak(_hwnd, out var peekHr) ? "peek dışı: tamam" : $"peek dışı bırakılamadı (HRESULT 0x{peekHr:X8})");
        _gorsel = new Gorsel(_hwnd, _olcek, _ayar, _tema, _cizici) { AnimasyonAzalt = _ayar.HareketAzalt(_sistemAzalt) };

        HedefKonumuYenile(ani: true);
        _uia = new KapsulUia(_hwnd, UiaAdi, UiaYardimi, () => Gonder(() => { _m.SolTik(Zaman.Simdi, false); Isle(); }));
        _taskbarCreated = N.RegisterWindowMessageW("TaskbarCreated");
        N.ShowWindow(_hwnd, 4);
        var simdi = Zaman.Simdi;
        _m.BostaSifirla(simdi);
        Guncelle(zorla: true, ilk: true);
        _kopru.KonumBildir();
        N.DwmFlush();
        IlkKareMs = SurecZamani.BasindanMs();
        Gunluk.Yaz($"ilk kare {IlkKareMs:0} ms, ölçek {_olcek:0.##}, tema {_tema.Ad}/{(_tema.Koyu ? "koyu" : "açık")}, yazı: {(_gorsel.YaziHatasi ?? "hazır sonra")}");

        if (_kanal == null) KisayolKaydet(); else Gunluk.Yaz("kısayolu masaüstü programı tutuyor, kapsül kaydetmedi");
        _kanal?.OkumayaBasla();
        N.SetTimer(_hwnd, T_BEKCI, (uint)_ayar.GuardMs, IntPtr.Zero);
        if (_o.CikisSn is { } c) N.SetTimer(_hwnd, T_CIKIS, (uint)(c * 1000), IntPtr.Zero);
        new Thread(SaatDongusu) { IsBackground = true, Name = "vsync" }.Start();
        if (_o.Demo) DemoBaslat();
        else if (!_o.MotorYok && !_o.Olc)
        {
            _motor = new MotorIstemci(MotorIstemci.VeriKlasoruBul(_o.VeriKlasoru), t => { _teslimler.Enqueue(t); N.PostMessageW(_hwnd, WM_ISLER, IntPtr.Zero, IntPtr.Zero); });
            _motor.Baslat();
        }
        if (_o.Olc) OlcBaslat();

        while (N.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0) { N.TranslateMessage(ref msg); N.DispatchMessageW(ref msg); }

        _cik = true; _kapi.Set();
        Kapat();
        return 0;
    }

    private void Kapat()
    {
        _motor?.Dispose();
        if (_kaydet) KonumKaydet();
        N.UnregisterHotKey(_hwnd, 1);
        _onBakis?.Dispose(); _menu?.Dispose();
        _kopru.Dispose();
        _gorsel.Dispose(); _cizici.Dispose();
    }

    // ------------------------------------------------------------------ ayar ve tema
    private void AyarYukle(bool ilk)
    {
        var dosya = _o.AyarDosyasi ?? Path.Combine(_durumKlasoru, "ayar.json");
        var yeni = new Ayar();
        if (File.Exists(dosya))
        {
            try
            {
                var uyar = yeni.JsonYukle(File.ReadAllText(dosya));
                foreach (var u in uyar) Gunluk.Yaz("ayar uyarısı: " + u);
                _ayarMtime = File.GetLastWriteTimeUtc(dosya);
            }
            catch (IOException e) { Gunluk.Yaz("ayar dosyası okunamadı: " + e.Message); return; }
        }
        else if (_o.AyarDosyasi != null) Gunluk.Yaz("ayar dosyası yok, varsayılanlar kullanılıyor: " + dosya);
        _ayarDosya = dosya;
        if (_o.Demo && !_o.DemoGercekSure) { yeni.Idle1 = Math.Min(yeni.Idle1, 3); yeni.Idle2 = Math.Min(yeni.Idle2, 6); yeni.AttentionMs = Math.Min(yeni.AttentionMs, 4500); yeni.WheelShowMs = Math.Min(yeni.WheelShowMs, 2200); }
        if (_o.Olc) { yeni.Idle1 = 1; yeni.Idle2 = 2; }
        if (ilk)
        {
            if (_o.Tema != null) { var t = Ayar.SecimGecerli(Ayar.TemaDegerleri, _o.Tema); if (t != null) yeni.Tema = t; else Gunluk.Yaz("--tema geçersiz: " + _o.Tema); }
            if (_o.Mod != null) { var t = Ayar.SecimGecerli(Ayar.ModDegerleri, _o.Mod); if (t != null) yeni.Mod = t; }
        }
        _ayar.Al(yeni);
    }

    private void AyarKaydet()
    {
        if (!_kaydet || string.IsNullOrEmpty(_ayarDosya)) return;
        try
        {
            var dir = Path.GetDirectoryName(_ayarDosya);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var gecici = _ayarDosya + ".tmp";
            File.WriteAllText(gecici, _ayar.JsonYaz());
            File.Move(gecici, _ayarDosya, true);
            _ayarMtime = File.GetLastWriteTimeUtc(_ayarDosya);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Gunluk.Yaz("ayar dosyası yazılamadı: " + e.Message);
        }
    }

    private void TemaHesapla()
    {
        _acikSistem = Sistem.UygulamaAcik();
        var koyu = _ayar.Mod switch { "koyu" => true, "acik" => false, _ => !_acikSistem };
        _hcAcik = Sistem.YuksekKarsitlik();
        _tema = _hcAcik
            ? Temalar.YuksekKarsitlik(Sistem.SistemRengi(5), Sistem.SistemRengi(8), Sistem.SistemRengi(26), Sistem.SistemRengi(13))
            : Temalar.Al(_ayar.Tema, koyu);
    }

    private void AyarDegistiUygula()
    {
        var eskiTema = _tema;
        TemaHesapla();
        _gorsel.AnimasyonAzalt = _ayar.HareketAzalt(_sistemAzalt);
        _gorsel.Ayar = _ayar;
        var yenidenKur = !ReferenceEquals(eskiTema, _tema) || Math.Abs(_gorsel.TuvalGenisligiDp - (_ayar.GirisW + 2 * Gorsel.GolgePay + 8)) > 0.5;
        if (yenidenKur) GorselYenile();
        HedefKonumuYenile(ani: false);
        Guncelle(zorla: true);
    }

    private void GorselYenile()
    {
        _gorsel.Yeniden(_olcek, _tema);
        N.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _gorsel.TuvalW, _gorsel.TuvalH, N.SWP_NOMOVE | N.SWP_NOZORDER | N.SWP_NOACTIVATE);
        PencereyiKonumla(true);
        _sonG = null;
    }

    public void TemaDegistir(string ad) => TemaVeKipUygula(ad, null);

    public void TemaVeKipUygula(string? aile, string? kip)
    {
        var degisti = false;
        if (!string.IsNullOrWhiteSpace(aile))
        {
            var t = Ayar.SecimGecerli(Ayar.TemaDegerleri, aile);
            if (t != null && t != _ayar.Tema) { _ayar.Tema = t; degisti = true; }
        }
        if (!string.IsNullOrWhiteSpace(kip))
        {
            var m = Ayar.SecimGecerli(Ayar.ModDegerleri, kip);
            if (m != null && m != _ayar.Mod) { _ayar.Mod = m; degisti = true; }
        }
        if (!degisti) return;
        Gunluk.Yaz($"tema: {_ayar.Tema}/{_ayar.Mod}");
        AyarKaydet();
        AyarDegistiUygula();
    }

    // ------------------------------------------------------------------ yer
    private double YaziGenisligiDp(string yazi)
    {
        var d = _gorsel?.YaziGenisligiDp(yazi) ?? yazi.Length * 7.0;
        return d;
    }

    private (double Ax, double Cy) CapaTuvalde => _gorsel.CapaTuvalde(_konum.Taraf);

    private void PencereyiKonumla(bool zorla = false)
    {
        var (cx, cy) = CapaTuvalde;
        var wx = (int)Math.Round(_ax.X - cx); var wy = (int)Math.Round(_cy.X - cy);
        if (!zorla && wx == _sonWx && wy == _sonWy) return;
        _sonWx = wx; _sonWy = wy;
        N.SetWindowPos(_hwnd, IntPtr.Zero, wx, wy, 0, 0, N.SWP_NOSIZE | N.SWP_NOZORDER | N.SWP_NOACTIVATE);
    }
    private int _sonWx = int.MinValue, _sonWy = int.MinValue;

    /// <summary>Saklanan oranlardan çapa hedefini yeniden hesaplar (ekran değişti, sıfırlandı, bırakıldı). ani: yaysız oturt.</summary>
    private void HedefKonumuYenile(bool ani)
    {
        var big = _ayar.Big * _olcek;
        var (ax, cy, ek) = KonumHesap.Cozumle(_konum, _ekranlar, big, _ayar.EdgeGap * _olcek);
        if (ek == null) { ax = 1800; cy = 400; }
        else
        {
            if (Math.Abs(ek.Olcek - _olcek) > 0.001 && !_ptr.Basili) OlcekDegistir(ek.Olcek);
            _m.EkranGenislikDp = ek.Calisma.W / _olcek;
            _ptr.Sinirlar = SanalMasaustu();
            // çalışma alanı içinde kalsın (görev çubuğu, çözünürlük değişimi)
            var capaAx = ax; var capaCy = cy;
            var r = KonumHesap.Dikdortgen(capaAx, capaCy, Math.Max(_ayar.Big * _olcek, 1), big, _konum.Taraf);
            r = KonumHesap.Sikistir(r, ek.Calisma, 0);
            ax = _konum.Taraf == Taraf.Sol ? r.L : r.R; cy = r.MerkezY;
        }
        var hizli = _ptr.Basili;
        _ax = new Yay(ani ? ax : _ax.X, hizli ? _ayar.KFast : _ayar.K, hizli ? _ayar.ZFast : _ayar.Z); _ax.T = ax;
        _cy = new Yay(ani ? cy : _cy.X, hizli ? _ayar.KFast : _ayar.K, hizli ? _ayar.ZFast : _ayar.Z); _cy.T = cy;
        if (ani) { _ax.Oturt(); _cy.Oturt(); }
        PencereyiKonumla(true);
        if (ani) _kopru?.KonumBildir();
        else KareIste();
    }

    private Dik SanalMasaustu()
    {
        if (_ekranlar.Count == 0) return new Dik(0, 0, 1920, 1080);
        return new Dik(_ekranlar.Min(e => e.Tam.L), _ekranlar.Min(e => e.Tam.T), _ekranlar.Max(e => e.Tam.R), _ekranlar.Max(e => e.Tam.B));
    }

    private void OlcekDegistir(double yeni)
    {
        if (Math.Abs(yeni - _olcek) < 0.001) return;
        Gunluk.Yaz($"ölçek {_olcek:0.##} → {yeni:0.##}");
        _olcek = yeni;
        GorselYenile();
        if (_onBakis != null) OnBakisKapat(true);
        if (_menu != null) MenuKapat(true);
    }

    private Dik KapsulDik(Gorunum? g = null)
    {
        g ??= _sonG ?? _m.GorunumHesapla(Zaman.Simdi);
        return KonumHesap.Dikdortgen(_ax.X, _cy.X, g.W * _olcek, g.H * _olcek, _konum.Taraf);
    }

    private Dik TikDik(Gorunum g)
    {
        var r = KonumHesap.Dikdortgen(_ax.X, _cy.X, g.W * _olcek, g.H * _olcek, _konum.Taraf);
        var min = _ayar.MinHit * _olcek;
        var w = Math.Max(r.W, min); var h = Math.Max(r.H, min);
        return new Dik(r.MerkezX - w / 2, r.MerkezY - h / 2, r.MerkezX + w / 2, r.MerkezY + h / 2);
    }

    private void KonumKaydet()
    {
        try { Directory.CreateDirectory(_durumKlasoru); File.WriteAllText(Path.Combine(_durumKlasoru, "konum.json"), _konum.Json()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ kare saati (yalnız hareket varken)
    private void SaatDongusu()
    {
        while (!_cik)
        {
            _kapi.Wait();
            if (_cik) break;
            N.DwmFlush();
            if (!_kapi.IsSet) continue;
            if (Interlocked.Exchange(ref _kareBekliyor, 1) == 0) N.PostMessageW(_hwnd, WM_FRAME, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void KareIste() { _sonKare = 0; _kapi.Set(); }

    private void Kare()
    {
        var t0 = Stopwatch.GetTimestamp();
        Volatile.Write(ref _kareBekliyor, 0);
        var dt = _sonKare == 0 ? 1.0 / 60 : (t0 - _sonKare) / (double)Stopwatch.Frequency;
        if (dt > 0.05) dt = 1.0 / 60;
        if (KareKaydi && _sonKare != 0) KareAraliklari.Add(dt * 1000);
        _sonKare = t0;
        var surukle = _ptr.Basili && _ptr.Tasindi && !_ptr.Uzatiyor;
        if (surukle) { var h = _ptr.SurukleHedefi; _ax.T = h.Ax; _cy.T = h.Cy; }
        else if (_yapay != null) YapayAdim();
        var hizli = surukle || _yapay != null;
        _ax.K = _cy.K = hizli ? _ayar.KFast : _ayar.K; _ax.Z = _cy.Z = hizli ? _ayar.ZFast : _ayar.Z;
        _ax.Adim(dt); _cy.Adim(dt);
        var durgun = !surukle && _yapay == null && _ax.Durgun(0.25, 4) && _cy.Durgun(0.25, 4);
        if (durgun) { _ax.Oturt(); _cy.Oturt(); }
        PencereyiKonumla();
        Interlocked.Increment(ref KareSayaci);
        if (durgun) { _kapi.Reset(); _sonKare = 0; _kopru?.KonumBildir(); if (_kaydet) KonumKaydetGecikmeli(); }
        if (KareKaydi) KareIsMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
    }

    private void KonumKaydetGecikmeli() { _kaydetSon = Zaman.Simdi; }

    // ------------------------------------------------------------------ güncelleme: mantık → çizim
    private string UiaAdi()
    {
        var g = _sonG ?? _m.GorunumHesapla(Zaman.Simdi);
        return _m.EkranOkuyucuAdi(g, _projeAdi);   // iç durum adları değil, sade cümle
    }

    /// <summary>Ekran okuyucu yardım metnindeki kısayol: masaüstü programı tutuyorsa onun söylediği gerçek değer, kapsül kendisi tutuyorsa ayardaki değer.</summary>
    private string UiaYardimi() => Erisim.Yardim(_o.Kabuk ? _o.Kisayol : _ayar.Kisayol);

    private void Guncelle(bool zorla = false, bool ilk = false)
    {
        var now = Zaman.Simdi;
        EylemleriIsle(now);
        var g = _m.GorunumHesapla(now);
        if (zorla || _sonG == null || !g.Equals(_sonG))
        {
            var taraf = _konum.Taraf;
            var eskiGizli = _sonG?.Gizli ?? false;
            _gorsel.DurumUygula(g, taraf, _m.Surukle || _m.Uzatma, ilk || zorla && _sonG == null);
            // durum günlüğü (deneme kanıtı): yalnız görünen durum ya da içerik değişince; yaylanma ve imleç yaklaşması satır üretmez
            var icerikGunlugu = $"{g.Ad} içerik={(g.Icerik is { } ic ? $"'{ic.Yazi}' sınıf={ic.Sinif}" : "yok")} rozet={(g.Rozet ? "var" : "yok")} gizli={(g.Gizli ? "evet" : "hayır")}";
            if (icerikGunlugu != _sonIcerikGunlugu) { _sonIcerikGunlugu = icerikGunlugu; Gunluk.Yaz("durum: " + icerikGunlugu); }
            if (g.Icerik is { Yazi.Length: > 0 }) { _yaziKullanildi = true; _sonYaziKullanim = now; }
            if (g.Giris && _giris != null) GirisCiz(true);
            _sonG = g;
            if (g.Gizli && !eskiGizli) N.SetTimer(_hwnd, T_GIZLE, (uint)(_ayar.ReducedFadeMs + 260), IntPtr.Zero);
            if (!g.Gizli && eskiGizli) { N.KillTimer(_hwnd, T_GIZLE); N.ShowWindow(_hwnd, 4); }
            _uia?.DegistiBildir();
        }
        // kişinin gizlemesi masaüstü programına bildirilir (tepside "Küçük pencereyi göster" satırı). İlk çağrıda da gider: yeniden başlayan kapsül eski durumu sıfırlar.
        var kullaniciGizledi = _m.Gizli == Gizlilik.Kullanici;
        if (kullaniciGizledi != _kabugaGizli) { _kabugaGizli = kullaniciGizledi; _kopru?.GizlilikBildir(kullaniciGizledi); }
        ZamanlayicilariKur(now, g);
    }

    private bool? _kabugaGizli;

    private void Isle()
    {
        // her işaretçi ya da klavye olayından sonra: önce mantık tarihleri, sonra görünüm
        _m.Tik(Zaman.Simdi);
        Guncelle();
    }

    private void ZamanlayicilariKur(long now, Gorunum g)
    {
        var sonraki = _m.SonrakiMs();
        if (sonraki is { } s) N.SetTimer(_hwnd, T_MANTIK, (uint)Math.Max(1, s - now), IntPtr.Zero);
        else N.KillTimer(_hwnd, T_MANTIK);
        // yaklaşma yoklaması: yalnız soluk ya da uykudayken (belirgin zaten tam opak)
        var yak = _m.Sev != Seviye.Belirgin && _ayar.R > 0 && !g.Gizli && !_m.Mesgul || (_m.Sev != Seviye.Belirgin && !g.Gizli && _ayar.R > 0 && !_m.Surukle);
        if (yak != _yaklasmaAktif) { _yaklasmaAktif = yak; if (yak) N.SetTimer(_hwnd, T_YAKLASMA, 100, IntPtr.Zero); else N.KillTimer(_hwnd, T_YAKLASMA); if (!yak) { _sonMesafe = -1; _m.ImlecUzaklik = double.PositiveInfinity; } }
        // dışarı tıklama yoklaması: açılır yüzey, giriş ya da belirginken (uzağa tıkla soluklaş)
        var tik = _onBakis != null && _m.Panel != PanelKipi.OnBakis ? false : (_menu != null || _m.Giris || _m.Panel == PanelKipi.Tam || (_m.Sev == Seviye.Belirgin && !_m.Mesgul && _ayar.FarFade >= 0.5 && !g.Gizli));
        if (_menu != null || _m.Giris) tik = true;
        if (tik != _tikAktif) { _tikAktif = tik; if (tik) { _prevSol = (N.GetAsyncKeyState(N.VK_LBUTTON) & 0x8000) != 0; _prevSag = (N.GetAsyncKeyState(N.VK_RBUTTON) & 0x8000) != 0; N.SetTimer(_hwnd, T_TIK, 50, IntPtr.Zero); } else N.KillTimer(_hwnd, T_TIK); }
    }

    // ------------------------------------------------------------------ eylemler
    private void EylemleriIsle(long now)
    {
        while (_m.Eylemler.TryDequeue(out var e))
        {
            Gunluk.Yaz($"eylem: {e.Tur} {e.Kip} {e.IsId} {(e.Metin != null ? "metin(" + e.Metin.Length + ")" : "")}");
            switch (e.Tur)
            {
                case EylemTuru.PanelAc when e.Kip == PanelKipi.OnBakis: OnBakisAc(); break;
                case EylemTuru.PanelAc: // tam panel: dışarıya olay. Panel açıksa mantık Tam durumunda kalır, değilse hemen kapalı sayılır.
                    if (_onBakis != null) OnBakisKapat(false);   // ön bakış tam panelle üst üste kalmasın (mantık da kapatma eylemi üretir; bu, eylem sırasından bağımsız güvence)
                    if (!_kopru.TamPaneliAc(e.IsId, KapsulDik())) { _m.PanelDisaridanKapandi(now); }
                    break;
                case EylemTuru.PanelKapat: if (e.Kip == PanelKipi.OnBakis) OnBakisKapat(false); else _kopru.TamPaneliKapat(); break;
                case EylemTuru.GirisAc: GirisBasla(); break;
                case EylemTuru.GirisKapat: GirisBitir(); break;
                case EylemTuru.MenuAc: MenuAc(); break;
                case EylemTuru.MenuKapat: MenuKapat(false); break;
                case EylemTuru.MetinGonder: _kopru.MetinGonder(e.Metin ?? "", _hedefEtiketi, _girisHedefPencere); break;
                case EylemTuru.KonumSifirla: _konum = KonumDurumu.Varsayilan(); _konum.EkranId = KonumHesap.EkranKimlikle("", _ekranlar)?.Id ?? ""; HedefKonumuYenile(ani: false); break;
                case EylemTuru.AyarAc: if (!_kopru.AyarAc(_ayarDosya)) _m.Bildir(KabukaUlasilamadi, now); break;
                case EylemTuru.Cikis: _kopru.CikisBildir(); N.SetTimer(_hwnd, T_KAPAT, (uint)(_ayar.ReducedFadeMs + 260), IntPtr.Zero); break;
                case EylemTuru.Uyut: break;
                case EylemTuru.Ses: MessageBeep(0x40); break;
                case EylemTuru.HedefSec:
                    if (!_kopru.HedefSec(e.Metin, KapsulDik()) && _m.Panel == PanelKipi.Tam) _m.PanelDisaridanKapandi(now);
                    break;
                case EylemTuru.TemaDegistir: TemaDegistir(e.Metin ?? ""); break;
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint t);

    // ------------------------------------------------------------------ açılır yüzeyler
    private void OnBakisAc()
    {
        if (_onBakis != null) return;
        var satirlar = OnBakisIcerigi.Kur(_m.Bekleyenler.ToList(), _m.Calisanlar.ToList(), _m.Belirsizler.ToList(), _projeAdi, DateTimeOffset.UtcNow)
            .Select(s => new AcilirSatir(s.Kimlik, s.Metin, s.Sag, s.Glif, s.Sinif, s.Baslik, false, s.Soluk, false, s.Tiklanir)).ToList();
        var ek = KonumHesap.EkranBul(KapsulDik().MerkezX, KapsulDik().MerkezY, _ekranlar);
        try
        {
            _onBakis = new Acilir(_gorsel, _tema, _olcek, satirlar, 420, KapsulDik(), ek?.Calisma ?? SanalMasaustu(), _gorsel.AnimasyonAzalt,
                id => Gonder(() => OnBakisTik(id)), var_ => Gonder(() => { _m.OnBakisUstunde(var_, Zaman.Simdi); Isle(); }));
        }
        catch (Exception e) { Gunluk.Yaz("ön bakış açılamadı: " + e.Message); _onBakis = null; _m.PanelDisaridanKapandi(Zaman.Simdi); }
    }

    private void OnBakisTik(string id)
    {
        if (id == "__sagtik") { _m.SagTik(Zaman.Simdi, false); Isle(); return; }
        OnBakisKapat(false);
        _m.PanelDisaridanKapandi(Zaman.Simdi);
        if (id.StartsWith(OnBakisIcerigi.SayfaOn, StringComparison.Ordinal)) _m.TamPanelSayfaAc(Zaman.Simdi, id[OnBakisIcerigi.SayfaOn.Length..]);
        else _m.TamPanelAc(Zaman.Simdi, id);
        Isle();
    }

    private void OnBakisKapat(bool hemen)
    {
        var a = _onBakis; if (a == null) return; _onBakis = null;
        a.Kapat(_gorsel.AnimasyonAzalt);
        Gecikmeli(hemen ? 0 : 160, () => a.Yok());
    }

    private void MenuAc()
    {
        if (_menu != null) return;
        var satirlar = KabukProtokol.Pano(_kopru.KabukVar, _ayar.Tema, _ayar.HideMs).Select(p => new AcilirSatir(p.Kimlik, p.Metin, Ayirici: p.Ayirici, Isaretli: p.Isaretli, Baslik: p.Baslik, Tiklanir: !p.Baslik)).ToList();
        var ek = KonumHesap.EkranBul(KapsulDik().MerkezX, KapsulDik().MerkezY, _ekranlar);
        try { _menu = new Acilir(_gorsel, _tema, _olcek, satirlar, 260, KapsulDik(), ek?.Calisma ?? SanalMasaustu(), _gorsel.AnimasyonAzalt, id => Gonder(() => MenuTik(id)), _ => { }); }
        catch (Exception e) { Gunluk.Yaz("pano açılamadı: " + e.Message); _menu = null; _m.Menu = false; }
    }

    /// <summary>Masaüstü programına gidecek bir menü işi gitmediyse kişiye söylenir (sessiz kalmaz).</summary>
    internal const string KabukaUlasilamadi = "Söz'ün ana penceresine ulaşılamadı";

    private void MenuTik(string id)
    {
        var now = Zaman.Simdi;
        if (id == "__sagtik") { _m.SagTik(now, false); Isle(); return; }
        _m.MenuKapat(now);
        switch (id)
        {
            case "yeniis": case "dikte": case "paket": case "icealma": if (!_kopru.Komut(KabukProtokol.PanoKomutu(id))) _m.Bildir(KabukaUlasilamadi, now); break;
            case "uyut": _m.Uyut(now); break;
            case "gizle": _m.Gizle(Gizlilik.Kullanici, now); break;
            case "sifirla": _m.KonumSifirla(); break;
            case "ayar": _m.Eylemler.Enqueue(new Eylem(EylemTuru.AyarAc)); break;
            // Kabuk bağlıysa kapatma kararı onundur (çalışan iş varsa sorar, "Hayır" denirse küçük pencere yerinde kalır); kabuk kapanınca kapsülü de kapatır.
            case "cikis": if (_kopru.KabukVar && _kopru.CikisBildir()) break; _m.Gizle(Gizlilik.Cikis, now); break;
            default: if (id.StartsWith("tema:")) TemaDegistir(id[5..]); break;
        }
        Isle();
    }

    /// <summary>Masaüstü programından gelen olay (pencere iş parçacığında çalışır).</summary>
    private void KabukOlayiIsle(KabukOlayi e)
    {
        var now = Zaman.Simdi;
        switch (e.Tur)
        {
            case OlayTuru.Flash: _m.Bildir(e.Metin, now); Isle(); break;
            case OlayTuru.Kisayol: _m.Kisayol(now); Isle(); break;
            case OlayTuru.Goster: _m.Goster(now); Isle(); break;
            case OlayTuru.PanelKapandi:
                _kopru.KabukPaneliKapandi();
                if (_m.Panel == PanelKipi.Tam) { _m.PanelDisaridanKapandi(now); Isle(); }
                break;
            case OlayTuru.Proje:
                _projeAdi = (e.Metin ?? "").Trim();
                _m.ProjeAdi = _projeAdi;
                _hedefEtiketi = _m.HedefEtiketi;
                if (_m.Giris) GirisCiz(true);
                _uia?.DegistiBildir();
                break;
            case OlayTuru.Tema:
                var (aile, kip) = KabukProtokol.TemaCoz(e.Metin);
                TemaVeKipUygula(aile, kip);
                break;
            case OlayTuru.DikteDurum:
                var (faz, ek) = KabukProtokol.DikteCoz(e.Metin);
                if (_m.DikteDurumUygula(faz, ek, now)) Isle();
                break;
            case OlayTuru.Kapat: Gunluk.Yaz("masaüstü programı kapatmamı istedi: " + (e.Metin ?? "")); N.DestroyWindow(_hwnd); break;
        }
    }

    private void MenuKapat(bool hemen)
    {
        var a = _menu; if (a == null) return; _menu = null;
        a.Kapat(_gorsel.AnimasyonAzalt);
        Gecikmeli(hemen ? 0 : 160, () => a.Yok());
    }

    private void Gecikmeli(int ms, Action a)
    {
        if (ms <= 0) { a(); return; }
        var t = new System.Threading.Timer(_ => Gonder(a), null, ms, Timeout.Infinite);
        _zamanlayicilar.Add(t);
    }
    private readonly List<System.Threading.Timer> _zamanlayicilar = new();

    /// <summary>Başka iş parçacığından ana iş parçacığına iş yolla.</summary>
    public void Gonder(Action a) { _kuyruk.Enqueue(a); N.PostMessageW(_hwnd, WM_POSTED, IntPtr.Zero, IntPtr.Zero); }

    // ------------------------------------------------------------------ giriş satırı
    private void GirisBasla()
    {
        _giris.Temizle();
        if (_o.Demo || _o.Olc) { GirisCiz(true); return; } // deneme: odak çalınmaz
        _oncekiOn = N.GetForegroundWindow();
        if (_oncekiOn == _hwnd) _oncekiOn = IntPtr.Zero;
        _girisHedefPencere = (long)_oncekiOn;
        var ex = N.GetWindowLongPtr(_hwnd, N.GWL_EXSTYLE);
        N.SetWindowLongPtr(_hwnd, N.GWL_EXSTYLE, (IntPtr)((long)ex & ~(long)N.WS_EX_NOACTIVATE));
        // odak al: önceki pencerenin iş parçacığına bağlanarak (sentetik girdi yok)
        var onThread = _oncekiOn != IntPtr.Zero ? N.GetWindowThreadProcessId(_oncekiOn, out _) : 0;
        var ben = N.GetCurrentThreadId();
        if (onThread != 0 && onThread != ben) N.AttachThreadInput(ben, onThread, true);
        N.SetForegroundWindow(_hwnd); N.SetFocus(_hwnd);
        if (onThread != 0 && onThread != ben) N.AttachThreadInput(ben, onThread, false);
        GirisCiz(true);
    }

    private void GirisBitir()
    {
        var ex = N.GetWindowLongPtr(_hwnd, N.GWL_EXSTYLE);
        N.SetWindowLongPtr(_hwnd, N.GWL_EXSTYLE, (IntPtr)((long)ex | N.WS_EX_NOACTIVATE));
        if (_oncekiOn != IntPtr.Zero && N.GetForegroundWindow() == _hwnd) N.SetForegroundWindow(_oncekiOn);
        _oncekiOn = IntPtr.Zero;
        _giris.Temizle();
        Gecikmeli(200, () => { if (!_m.Giris) { _gorsel.GirisBirak(); _gorsel.GirisGizle(); } });
    }

    private void GirisCiz(bool zorla = false)
    {
        var w = Math.Min(_ayar.GirisW, _m.EkranGenislikDp - 12);
        _gorsel.GirisGuncelle(_giris.Metin, _giris.Imlec, _hedefEtiketi, "Bir şey yaz ya da söyle", w, zorla);
    }

    private void GirisTusu(uint vk)
    {
        var now = Zaman.Simdi;
        var ctrl = (N.GetKeyState(N.VK_CONTROL) & 0x8000) != 0;
        switch (vk)
        {
            case N.VK_RETURN: _m.MetinGonder(_giris.Metin, now); Isle(); return;
            case N.VK_ESCAPE: _m.Esc(now); Isle(); return;
            case N.VK_BACK: if (ctrl) _giris.KelimeGeriSil(); else _giris.GeriSil(); break;
            case N.VK_DELETE: _giris.IleriSil(); break;
            case N.VK_LEFT: _giris.Sol(ctrl); break;
            case N.VK_RIGHT: _giris.Sag(ctrl); break;
            case N.VK_HOME: _giris.Basa(); break;
            case N.VK_END: _giris.Sona(); break;
            case 'V' when ctrl: _giris.Ekle(Sistem.PanodanMetin(_hwnd)); break;
            default: return;
        }
        GirisCiz();
    }

    // ------------------------------------------------------------------ kısayol
    private void KisayolKaydet()
    {
        uint mods = 0x4000; uint vk = 0; // MOD_NOREPEAT
        foreach (var p in _ayar.Kisayol.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 2; break;
                case "alt": mods |= 1; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default: if (p.Length == 1) vk = char.ToUpperInvariant(p[0]); break;
            }
        }
        if (vk == 0 || (mods & 0xF) == 0) { Gunluk.Yaz("kısayol geçersiz: " + _ayar.Kisayol); return; }
        if (!N.RegisterHotKey(_hwnd, 1, mods, vk)) Gunluk.Yaz($"kısayol {_ayar.Kisayol} kaydedilemedi (başka program kullanıyor olabilir), kapsül yine çalışır");
    }

    // ------------------------------------------------------------------ pencere iletileri
    private static IntPtr StaticProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp) => s_self != null && (h == s_self._hwnd || s_self._hwnd == IntPtr.Zero) ? s_self.Proc(h, msg, wp, lp) : N.DefWindowProcW(h, msg, wp, lp);

    private bool SagDugmeBasili()
    {
        var degis = N.GetSystemMetrics(23) != 0; // SM_SWAPBUTTON
        return (N.GetAsyncKeyState(degis ? N.VK_LBUTTON : N.VK_RBUTTON) & 0x8000) != 0;
    }

    private IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == _taskbarCreated && msg != 0) { Gonder(() => { N.SetWindowPos(_hwnd, N.HWND_TOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE); _ekranlar = Sistem.Ekranlar(); HedefKonumuYenile(false); }); return IntPtr.Zero; }
        var now = Zaman.Simdi;
        switch (msg)
        {
            case N.WM_MOUSEACTIVATE: return _m != null && _m.Giris ? N.MA_ACTIVATE : N.MA_NOACTIVATE;
            case N.WM_NCHITTEST:
            {
                if (_m == null || _sonG == null || _sonG.Gizli) return N.HTTRANSPARENT;
                var sx = N.X(lp); var sy = N.Y(lp);
                if (!TikDik(_sonG).Icinde(sx, sy)) return N.HTTRANSPARENT;
                if (_m.SagTikGecirgen && SagDugmeBasili()) return N.HTTRANSPARENT; // uykuda sağ tık alttaki programa geçer
                return N.HTCLIENT;
            }
            case N.WM_MOUSEMOVE:
            {
                N.GetCursorPos(out var p);
                if (!_tracking) { var te = new N.TRACKMOUSEEVENT { cbSize = sizeof(N.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = h }; N.TrackMouseEvent(ref te); _tracking = true; _m.HoverGirdi(now, p.X / _olcek, p.Y / _olcek); _kopru.Gunluk("üstüne gelindi"); Isle(); }
                else if (!_ptr.Basili) { _m.HoverHareket(now, p.X / _olcek, p.Y / _olcek); }
                if (_ptr.Basili) { _ptr.Hareket(now, p.X, p.Y); if (_ptr.Tasindi) { KareIste(); Isle(); } }
                return IntPtr.Zero;
            }
            case N.WM_MOUSELEAVE:
                _tracking = false;
                if (!_ptr.Basili) { _m.HoverCikti(now); Isle(); }
                return IntPtr.Zero;
            case N.WM_LBUTTONDOWN:
            {
                N.GetCursorPos(out var p);
                if (_m.Giris && ChipTikMi(p)) { _m.HedefCipiTikla(now); Isle(); return IntPtr.Zero; }
                if (_m.Giris) return IntPtr.Zero;
                N.SetCapture(h);
                _ptr.SolBasildi(now, p.X, p.Y, KapsulDik(), _olcek, _konum.Taraf, _m.Sev);
                return IntPtr.Zero;
            }
            case N.WM_LBUTTONUP:
            {
                if (!_ptr.Basili) return IntPtr.Zero;
                N.ReleaseCapture();
                N.GetCursorPos(out var p);
                var sonuc = _ptr.SolBirakildi(now, p.X, p.Y);
                if (sonuc == PointerYonetici.Sonuc.SurukleBitti) SurukleBitti();
                else if (sonuc == PointerYonetici.Sonuc.UzatmaBitti) { }
                Isle();
                return IntPtr.Zero;
            }
            case N.WM_CAPTURECHANGED:
                if (_ptr != null && _ptr.Basili && lp != h) { _ptr.Iptal(now); SurukleBitti(); Isle(); }
                return IntPtr.Zero;
            case N.WM_RBUTTONUP:
                _m.SagTik(now, true); Isle(); return IntPtr.Zero;
            case N.WM_MOUSEWHEEL:
            {
                var delta = (short)(((long)wp >> 16) & 0xFFFF);
                _m.Tekerlek(delta > 0 ? -1 : 1, now); Isle(); return IntPtr.Zero;
            }
            case N.WM_LBUTTONDBLCLK: return IntPtr.Zero;
            case N.WM_KEYDOWN: if (_m != null && _m.Giris) { GirisTusu((uint)wp); return IntPtr.Zero; } break;
            case N.WM_CHAR:
                if (_m != null && _m.Giris && (uint)wp >= 32 && (uint)wp != 127) { _giris.Ekle(((char)(uint)wp).ToString()); GirisCiz(); return IntPtr.Zero; }
                break;
            case N.WM_ACTIVATE:
                // odak başka pencereye geçti ve yazı yoksa giriş kapanır; yazı varsa açık kalır
                if (((long)wp & 0xFFFF) == 0 && _m != null && _m.Giris && _giris.Bos) { _m.GirisKapat(now); Isle(); }
                break;
            case N.WM_HOTKEY: _m.Kisayol(now); Isle(); return IntPtr.Zero;
            case N.WM_TIMER: ZamanlayiciIletisi((int)wp); return IntPtr.Zero;
            case WM_FRAME: Kare(); return IntPtr.Zero;
            case WM_POSTED: while (_kuyruk.TryDequeue(out var a)) a(); return IntPtr.Zero;
            case WM_ISLER:
                while (_teslimler.TryDequeue(out var teslim))
                {
                    var liste = teslim.Isler ?? Array.Empty<Is>();
                    var dikkatOnce = _m.DikkatSayaci;
                    _m.IsleriGuncelle(liste, now, sessiz: teslim.Ilk, kesin: teslim.Kesin);
                    Gunluk.Yaz($"liste uygulandı{(teslim.Isler == null ? " (nötr, motor yok)" : teslim.Kesin ? "" : " (kopuk)")}: bekleyen={liste.Count(j => j.Bekliyor)} çalışan={liste.Count(j => j.Tur == IsTuru.Calisiyor)} belirsiz={liste.Count(j => j.Tur == IsTuru.Belirsiz)} " +
                        $"uyandı={(_m.DikkatSayaci > dikkatOnce ? "evet" : "hayır")} (toplam {_m.DikkatSayaci}, sessiz geçen {_m.SessizSayaci}) türler=[{string.Join(",", liste.Select(j => j.Tur + (j.Bekliyor && !j.Sinyal ? "/sessiz" : "")))}]");
                }
                if (_onBakis != null) { OnBakisKapat(true); if (_m.Panel == PanelKipi.OnBakis) _m.Panel = PanelKipi.Yok; }   // açık tam paneli "kapalı" sayma
                Isle(); return IntPtr.Zero;
            case N.WM_DPICHANGED:
            {
                var yeni = ((long)wp & 0xFFFF) / 96.0;
                if (_ptr != null && _ptr.Basili) { _dpiBekliyor = true; _bekleyenOlcek = yeni; }
                else { OlcekDegistir(yeni); HedefKonumuYenile(true); Guncelle(true); }
                return IntPtr.Zero;
            }
            case N.WM_DISPLAYCHANGE:
                _ekranlar = Sistem.Ekranlar(); Gunluk.Yaz($"ekran değişti: {_ekranlar.Count} ekran"); HedefKonumuYenile(false); Guncelle(true); return IntPtr.Zero;
            case N.WM_SETTINGCHANGE:
            case N.WM_THEMECHANGED:
            case N.WM_DWMCOMPOSITIONCHANGED:
                if (_m != null) Gonder(AyarlarSistemdenYenile);
                return IntPtr.Zero;
            case N.WM_GETOBJECT:
                if ((int)(long)lp == -25 && _uia != null) return N.UiaReturnRawElementProvider(h, wp, lp, _uia);
                break;
            case N.WM_DESTROY: N.PostQuitMessage(0); return IntPtr.Zero;
        }
        return N.DefWindowProcW(h, msg, wp, lp);
    }

    private bool ChipTikMi(N.POINT p)
    {
        var r = KapsulDik(_sonG);
        var (sag, gen) = _gorsel.ChipYerlesimi(_hedefEtiketi);
        var sagPx = r.R - 6 * _olcek; var solPx = sagPx - gen * _olcek;
        return p.X >= solPx && p.X <= sagPx && Math.Abs(p.Y - r.MerkezY) <= 16 * _olcek;
    }

    private void SurukleBitti()
    {
        var g = _sonG ?? _m.GorunumHesapla(Zaman.Simdi);
        var r = KonumHesap.Dikdortgen(_ax.T, _cy.T, g.W * _olcek, g.H * _olcek, _konum.Taraf);
        var ek = KonumHesap.EkranBul(r.MerkezX, r.MerkezY, _ekranlar);
        if (ek == null) return;
        var yeni = KonumHesap.Birak(r, ek, _ayar.Snap * _olcek, _ayar.EdgeGap * _olcek);
        Gunluk.Yaz($"bırakıldı: ekran {yeni.EkranId} fx {yeni.Fx:0.###} fy {yeni.Fy:0.###} yapışma {yeni.SnapX}/{yeni.SnapY} taraf {yeni.Taraf}");
        var tarafDegisti = yeni.Taraf != _konum.Taraf;
        _konum = yeni;
        if (tarafDegisti) { _sonG = null; }
        if (_dpiBekliyor) { _dpiBekliyor = false; OlcekDegistir(_bekleyenOlcek); }
        // hedef: çapa yeni oranlardan, yay yerleştirir
        var big = _ayar.Big * _olcek;
        var (ax, cy, _) = KonumHesap.Cozumle(_konum, _ekranlar, big, _ayar.EdgeGap * _olcek);
        if (tarafDegisti)
        {
            // çapa sağ uçtan sol uca (ya da tersi) geçti: görünen şekil aynı kalsın diye yayın başlangıcını yeni çapaya çevir
            var eskiAx = _ax.X; var eskiW = g.W * _olcek;
            _ax.X = _konum.Taraf == Taraf.Sol ? eskiAx - eskiW : eskiAx + eskiW; _ax.V = 0;
        }
        _ax.T = ax; _cy.T = _konum.SnapY == KenarY.Yok ? r.MerkezY : cy;
        KareIste();
        Guncelle(true);
    }

    private void AyarlarSistemdenYenile()
    {
        var yeniAzalt = Sistem.SistemHareketAzalt(); var yeniAcik = Sistem.UygulamaAcik(); var hc = Sistem.YuksekKarsitlik();
        var degisti = yeniAzalt != _sistemAzalt || yeniAcik != _acikSistem || hc != _hcAcik;
        _sistemAzalt = yeniAzalt;
        _ekranlar = Sistem.Ekranlar();
        if (degisti) AyarDegistiUygula(); else HedefKonumuYenile(false);
    }

    private void ZamanlayiciIletisi(int id)
    {
        var now = Zaman.Simdi;
        _zamanlayiciUyanma++;
        switch (id)
        {
            case T_MANTIK: N.KillTimer(_hwnd, T_MANTIK); _m.Tik(now); Guncelle(); break;
            case T_YAKLASMA: Yaklasma(now); break;
            case T_BEKCI: Bekci(now); break;
            case T_CIKIS: N.KillTimer(_hwnd, T_CIKIS); Gunluk.Yaz("--exit-after doldu, kapanıyor"); N.DestroyWindow(_hwnd); break;
            case T_KAPAT: N.KillTimer(_hwnd, T_KAPAT); N.DestroyWindow(_hwnd); break;
            case T_GIZLE: N.KillTimer(_hwnd, T_GIZLE); if (_sonG?.Gizli == true) N.ShowWindow(_hwnd, 0); break;
            case T_DEMO: DemoAdimi(); break;
            case T_TIK: DisaTikYokla(now); break;
            case T_OLC: OlcAdimi(); break;
            case T_BLOK: BlokYap(); break;
        }
    }

    private void Yaklasma(long now)
    {
        N.GetCursorPos(out var p);
        var r = KapsulDik(_sonG);
        var d = Soz.Capsule.Yaklasma.Uzaklik(p.X, p.Y, r.MerkezX, r.MerkezY) / _olcek;
        if (Math.Abs(d - _sonMesafe) < 0.5 && _sonMesafe >= 0 || (d > _ayar.R * 2 && _sonMesafe > _ayar.R * 2)) { return; }
        _sonMesafe = d;
        _m.ImlecUzaklik = d;
        Guncelle();
    }

    private void DisaTikYokla(long now)
    {
        var sol = (N.GetAsyncKeyState(N.VK_LBUTTON) & 0x8000) != 0; var sag = SagDugmeBasili();
        var solBasti = sol && !_prevSol; var sagBasti = sag && !_prevSag;
        _prevSol = sol; _prevSag = sag;
        if (!solBasti && !sagBasti) return;
        N.GetCursorPos(out var p);
        var g = _sonG ?? _m.GorunumHesapla(now);
        var kapsulda = TikDik(g).Icinde(p.X, p.Y);
        var menudeyiz = _menu != null && _menu.GovdePx.Icinde(p.X, p.Y);
        var onBakista = _onBakis != null && _onBakis.GovdePx.Icinde(p.X, p.Y);
        if (kapsulda || menudeyiz || onBakista) return;
        if (sagBasti && _menu == null && !_m.Giris) return;
        var rr = KapsulDik(g);
        var uzak = Soz.Capsule.Yaklasma.Uzaklik(p.X, p.Y, rr.MerkezX, rr.MerkezY) / _olcek;
        if (_m.Giris && _giris.Bos && solBasti) { _m.GirisKapat(now); Isle(); return; }
        // kabukla birlikteyken panel kabuğun penceresidir: dışarı tık onu kapatırsa panelin içine tıklamak da kapatırdı. Panel odağı kaybedince kabuk haber verir.
        if (_m.Panel == PanelKipi.Tam && solBasti && _kanal == null) { _m.PaneliKapat(now); Isle(); return; }
        _m.DisariTik(now, uzak);
        Isle();
    }

    private void Bekci(long now)
    {
        // tam ekran uygulama: gizle, bitince geri getir
        var tam = Sistem.TamEkranVar(_hwnd, IntPtr.Zero);
        if (tam != _m.TamEkranUygulama) { Gunluk.Yaz("tam ekran: " + tam); _m.TamEkran(tam, now); Isle(); }
        // başlatılan tam panel kapandıysa mantık da kapansın
        if (_m.Panel == PanelKipi.Tam && !_kopru.Calisiyor) { _m.PanelDisaridanKapandi(now); Isle(); }
        // üstte kalma
        var ex = (long)N.GetWindowLongPtr(_hwnd, N.GWL_EXSTYLE);
        var yuzeyAcik = _onBakis != null || _menu != null || _m.Panel != PanelKipi.Yok;
        if ((ex & N.WS_EX_TOPMOST) == 0 || (_sonG is { Gizli: false } && PencereKurallari.UstteKalmaYenilensin(now, _sonUstteMs, UstteYenilemeMs, yuzeyAcik)))
        {
            // bayrak kaybolduysa geri ver; bayrak duruyorsa da görev çubuğu önizlemesi gibi üst pencerelerin altında kalmamak için sırayı periyodik yenile (etkinleştirmeden, taşımadan)
            _sonUstteMs = now;
            N.SetWindowPos(_hwnd, N.HWND_TOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE);
        }
        // zemin parlaklığı (uykudayken 10 sn'de bir)
        if (_m.Sev == Seviye.Uyku && !_m.Mesgul && now - _sonLum >= (long)_ayar.LuminanceMs && _sonG is { Gizli: false })
        {
            _sonLum = now;
            var r = KapsulDik(_sonG);
            var lum = Sistem.ZeminParlakligi(r.MerkezX, r.MerkezY, 20 * _olcek);
            if (lum >= 0 && Math.Abs(lum - _m.ZeminParlaklik) > 0.02) { _m.ZeminParlaklik = lum; Guncelle(true); }
        }
        // ayar dosyası değişti mi
        if (_ayarDosya != null)
        {
            try
            {
                var mt = File.Exists(_ayarDosya) ? File.GetLastWriteTimeUtc(_ayarDosya) : default;
                if (mt != default && mt != _ayarMtime) { Gunluk.Yaz("ayar dosyası değişti, yeniden okunuyor"); AyarYukle(false); AyarDegistiUygula(); }
            }
            catch (IOException) { }
        }
        // yazı aygıtı uzun süre kullanılmadıysa bırak (bellek)
        if (_yaziKullanildi && now - _sonYaziKullanim > 60000 && _sonG?.Icerik is not { Yazi.Length: > 0 } && !_m.Giris && _onBakis == null && _menu == null)
        { _gorsel.YaziAygitiniBirak(); _yaziKullanildi = false; _sonG = null; Guncelle(true); GC.Collect(2, GCCollectionMode.Optimized, false); }
        // sessiz konum kaydı
        if (_kaydet && _kaydetSon != 0 && now - _kaydetSon > 1000) { _kaydetSon = 0; KonumKaydet(); _kopru.KonumBildir(); }
    }
}
