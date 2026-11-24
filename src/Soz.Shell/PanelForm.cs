using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Soz.Shell;

/// <summary>
/// Arayüzü gösteren panel (WebView2). Yalnız kullanıcı kapsüle ya da tepsiye tıklayınca açılır, X, tepsi ya da kapsül tıklamasıyla gizlenir
/// (kapanmaz). Esc'i yalnız arayüz yönetir. Odak kaybında kendiliğinden gizlenmez (<see cref="PanelOdak"/>). Kendiliğinden öne gelmez.
/// Normal pencere gibi küçültülür ve büyütülür, görev çubuğunda düğmesi vardır. Küçültülmüş panel "görünür" sayılır ama tepsi tıklaması, kısayol ya da
/// ikinci açılış onu GERİ GETİRİR (<see cref="PanelPencereKurali"/>). Büyütülmüş durum saklanır, küçültülmüş durum saklanmaz.
/// </summary>
internal sealed class PanelForm : Form
{
    private string _url;
    private readonly string _userDataDir;
    private Palette _palette;
    private WebView2 _web = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new();
    private bool _picking;

    /// <summary>Kabuk kapanırken gerçek pencere kapatmasına izin verir. Bunun dışındaki tüm kapatmalar (WM_CLOSE, Alt+F4) gizlemeye çevrilir.</summary>
    internal bool AllowClose { get; set; }

    /// <summary>
    /// Panel bu kadar süre gizli kalırsa WebView2 (tarayıcı süreçleri, yüzlerce MB) bırakılır, sonraki açılışta yeniden kurulur (soğuk açılış).
    /// Varsayılan 3 dk. Ayar: ortam değişkeni <c>SOZ_PANEL_BOSTA_DK</c> ya da veri\panel-durum.json içinde <c>bostaDk</c> (1-1440 dk).
    /// </summary>
    internal static TimeSpan IdleReleaseAfter => PanelBosta.Varsayilan;
    private DateTime _shownAt = DateTime.MinValue;

    /// <summary>Klasör seçici için sunucunun profil varsayılanları (kabuk bağlar). Boş ya da yavaşsa seçici beklemeden sıradaki adaya geçer.</summary>
    internal Func<CancellationToken, Task<ProfilVarsayilan?>>? ProfilVarsayilanlari { get; set; }
    /// <summary>Panelin yer/boyut ve son klasör durumunun yazıldığı dosya (veri\panel-durum.json). Boşsa hiçbir şey saklanmaz ve okunmaz (duman, ekran görüntüsü).</summary>
    internal string? DurumDosyasi { get; set; }
    private PanelDurum _durum = PanelDurum.Bos;
    private bool _durumYuklendi;
    private Rectangle _sonUygulanan;
    private Rectangle _sonAnchor;
    private System.Diagnostics.Stopwatch _t0 = System.Diagnostics.Stopwatch.StartNew();
    private int _acilisNo;
    private Task? _init;
    private bool _runtimeMissingHandled;
    private bool _pageLoaded;
    private bool _newJobPending;
    private string? _pendingHash;

    /// <summary>Şu An ekranı, iş kutusu odaklı (arayüz bu bağlantıyı okuyup açılışta iş kutusuna odaklanır).</summary>
    public const string NewJobHash = "#/suan?yeni=1";

    /// <summary>Panel gösterildi (true) ya da gizlendi (false). Yeni kapsül panelin kapandığını buradan öğrenir.</summary>
    public event Action<bool>? ShownChanged;

    public bool RuntimeMissing { get; private set; }
    public string? InitError { get; private set; }

    /// <summary>Arayüzden gelen nesne iletileri (paste, dictate, bilinmeyen). Eski "soz-esc" iletisi yok sayılır.</summary>
    public event Action<WebMessage>? MessageReceived;

    public PanelForm(string url, string userDataDir, Palette palette)
    {
        _url = url;
        _userDataDir = userDataDir;
        _palette = palette;
        Text = "Söz";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;   // küçültülen pencere görev çubuğundan bulunur; gizliyken (X) düğme de kalkar
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        MinimumSize = PanelYerlesim.EnKucukDp;
        MaximizeBox = true;
        MinimizeBox = true;
        BackColor = palette.EffectivePanelBg;
        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        try { Icon = TrayIconFactory.Create(32, palette); } catch { }
        _idleTimer.Interval = (int)IdleReleaseAfter.TotalMilliseconds;
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); if (!Visible && !_picking) ReleaseWebView(); };
        YenidenDeneIstendi += CokmedenSonraYenidenDene;   // yalnız çöküş mesajındayken iş yapar (sunucu hatasındaki düğmeyi kabuk bağlar)
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetImmersiveDarkMode(Handle, !_palette.Light);
    }

    /// <summary>Aktif temanın zemin rengini, WebView2 varsayılan zeminini, karanlık başlık çubuğunu ve ikonu yeniden başlatmadan günceller.</summary>
    public void ApplyPalette(Palette palette)
    {
        _palette = palette;
        var bg = palette.EffectivePanelBg;
        BackColor = bg;
        if (!_web.IsDisposed) _web.DefaultBackgroundColor = bg;
        if (IsHandleCreated) Native.SetImmersiveDarkMode(Handle, !palette.Light);
        try { var old = Icon; Icon = TrayIconFactory.Create(32, palette); old?.Dispose(); } catch { }
        HataRenkleri();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _idleTimer.Stop();
        if (!Visible) { _idleTimer.Interval = (int)BostaSure().TotalMilliseconds; _idleTimer.Start(); }
        ShownChanged?.Invoke(Visible);
    }

    private TimeSpan BostaSure() => PanelBosta.Coz(Environment.GetEnvironmentVariable(PanelBosta.OrtamAdi), Durum().BostaDk);

    private PanelDurum Durum()
    {
        if (!_durumYuklendi) { _durum = PanelDurum.Yukle(DurumDosyasi); _durumYuklendi = DurumDosyasi != null; }
        return _durum;
    }

    private void DurumuYaz(PanelDurum d)
    {
        _durum = d;
        if (DurumDosyasi != null && !PanelDurum.Kaydet(DurumDosyasi, d)) Log.Write("panel durumu yazılamadı");
    }

    /// <summary>Gizliyken WebView2'yi bırakır (RAM). Sonraki OpenNear yeniden başlatır; bekleyen "yeni iş" isteği korunur.</summary>
    private void ReleaseWebView()
    {
        if (_init is { IsCompleted: false } || _web.CoreWebView2 == null) return; // henüz kurulmamış ya da kuruluyor
        Log.Write($"panel {BostaSure().TotalMinutes:0} dk gizli kaldı: WebView2 bırakıldı");
        var old = _web;
        Controls.Remove(old);
        try { old.Dispose(); } catch (Exception ex) { Log.Error("WebView2 bırakma", ex); }
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);
        _init = null;
        _pageLoaded = false;
    }

    /// <summary>Panel kişinin önünde mi: görünür ve küçültülmemiş (küçültülmüş pencere WinForms'ta Visible kalır ama kimse görmez).</summary>
    public bool IsOpen => Visible && WindowState != FormWindowState.Minimized;

    private PencereDurumu Pencere() => WindowState switch
    {
        FormWindowState.Minimized => PencereDurumu.Kucultulmus,
        FormWindowState.Maximized => PencereDurumu.Buyutulmus,
        _ => PencereDurumu.Normal,
    };

    // Kişinin son durumu (büyütülmüş mü). Küçültme bunu değiştirmez; açılışta saklı değerden gelir.
    private bool _buyutulmus;
    private bool _buyutulmusYuklendi;
    private bool _durumKuruluyor;   // kodun kendi yaptığı durum ve boyut değişikliği "kişi büyüttü" diye saklanmasın

    /// <summary>Kullanıcı eylemiyle: kapsülün yanında aç/kapat. Küçültülmüşse kapatmaz, geri getirir.</summary>
    public void Toggle(Rectangle anchorScreen)
    {
        // Görünürken: etkinse gizle, başka pencerenin arkasında kaldıysa öne getir, küçültülmüşse geri getir.
        var etkin = ReferenceEquals(Form.ActiveForm, this);
        var kayip = (DateTime.UtcNow - _sonEtkinKaybi).TotalMilliseconds;
        switch (PanelPencereKurali.TepsiTiklandi(Visible, Pencere(), PanelOdak.AcKapa(etkin, kayip)))
        {
            case PanelEylemi.Goster: OpenNear(anchorScreen); break;
            case PanelEylemi.Gizle: Hide(); break;
            case PanelEylemi.GeriGetir: GeriGetir(); break;
            default:
                try { BringToFront(); if (IsHandleCreated) Native.SetForegroundWindow(Handle); Activate(); }
                catch (Exception ex) { Log.Error("paneli öne getirme", ex); }
                break;
        }
    }

    /// <summary>
    /// Panel zaten görünürken gelen "aç" isteği (kısayol, menü, ikinci açılış, bildirim, yeni iş): küçültülmüşse geri getirir, değilse öne alır.
    /// Gizliyse hiçbir şey yapmaz (çağıran <see cref="OpenNear(Rectangle, bool)"/> ile açar). odakAl=false: odak çalınmaz.
    /// </summary>
    internal void GorunurkenGetir(bool odakAl = true)
    {
        switch (PanelPencereKurali.AcIstendi(Visible, Pencere()))
        {
            case PanelEylemi.GeriGetir: GeriGetir(odakAl); break;
            case PanelEylemi.OneGetir: if (odakAl) Activate(); break;
        }
    }

    /// <summary>Küçültülmüş paneli küçültmeden önceki duruma (normal ya da büyütülmüş) döndürüp öne alır. Küçültülmüş değilse yalnız öne alır.</summary>
    internal void GeriGetir(bool odakAl = true)
    {
        try
        {
            if (!IsHandleCreated) return;
            var hedef = PanelPencereKurali.HedefDurum(_buyutulmus) == PencereDurumu.Buyutulmus ? FormWindowState.Maximized : FormWindowState.Normal;
            if (WindowState == FormWindowState.Minimized)
            {
                Log.Write($"panel küçültülmüştü, geri getiriliyor ({hedef}, odak={odakAl})");
                if (odakAl) WindowState = hedef;
                else
                {
                    Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);   // odak çalmadan geri getir (dikte sürerken)
                    if (WindowState == FormWindowState.Minimized) WindowState = hedef;
                }
            }
            if (odakAl) { BringToFront(); Native.SetForegroundWindow(Handle); Activate(); }
            else OdaksizOneAl();
        }
        catch (Exception ex) { Log.Error("paneli geri getirme", ex); }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_durumKuruluyor || !_buyutulmusYuklendi) return;
        // Kişi büyüttü ya da eski boyuta döndürdü: bir sonraki açılış için saklanır. Küçültme bu değeri değiştirmez, hiç saklanmaz.
        var yeni = PanelPencereKurali.BuyutulmusMu(Pencere(), _buyutulmus);
        if (yeni == _buyutulmus) return;
        _buyutulmus = yeni;
        DurumuYaz(Durum() with { Buyutulmus = yeni ? true : null });
        Log.Write(yeni ? "panel büyütüldü (saklandı)" : "panel eski boyuta döndü (saklandı)");
    }

    private DateTime _sonEtkinKaybi = DateTime.MinValue;
    private bool OdakKaybindaGizleAyari() => PanelOdak.Coz(Environment.GetEnvironmentVariable(PanelOdak.OrtamAdi), Durum().OdakKaybindaGizle);

    /// <summary>
    /// Paneli açar ve arayüzün verilen sayfasına götürür (ör. "#/is/7", "#/ayarlar"). Yalnız izinli adresler kabul edilir (<see cref="Soz.Capsule.KabukProtokol.AdresIzinli"/>).
    /// Sayfa yüklüyse adres çubuğu değişir, yüklenmediyse ilk gezinme bu adresle başlar.
    /// </summary>
    public void OpenNear(Rectangle anchorScreen, string? hash, bool odakAl = true)
    {
        if (Soz.Capsule.KabukProtokol.AdresIzinli(hash))
        {
            if (_pageLoaded && _web.CoreWebView2 != null)
            {
                try { _ = _web.CoreWebView2.ExecuteScriptAsync(Soz.Capsule.KabukProtokol.GezinmeBetigi(hash)!); } catch (Exception ex) { Log.Error("panel adresi", ex); }
            }
            else _pendingHash = hash;
        }
        if (!Visible) OpenNear(anchorScreen, odakAl); else GorunurkenGetir(odakAl);
    }

    private bool _odaksizAcilis;
    /// <summary>Kullanıcı eylemi olmadan açılırken pencere odak almadan gösterilir (SW_SHOWNOACTIVATE).</summary>
    protected override bool ShowWithoutActivation => _odaksizAcilis;

    /// <summary>Gösterilirken öne alır ama odak almaz (Win32 SWP_NOACTIVATE).</summary>
    internal void OdaksizOneAl()
    {
        try { if (IsHandleCreated) Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); } catch { }
    }

    /// <summary>
    /// Paneli açar. Yer: kişi taşıdıysa ya da boyutlandırdıysa saklı yer (hâlâ görünüyorsa), yoksa kapsülün GÜNCEL konumuna göre
    /// (<see cref="PanelYerlesim.Hesapla"/>). Her açılışın aşamaları zaman damgalı günlüğe yazılır (gözlem 4 ve 6 ölçümü).
    /// </summary>
    public void OpenNear(Rectangle anchorScreen, bool odakAl = true)
    {
        _t0 = System.Diagnostics.Stopwatch.StartNew();
        var no = ++_acilisNo;
        var sicak = _pageLoaded && _web.CoreWebView2 != null;
        if (!IsHandleCreated) _ = Handle;
        var durum = Durum();
        _sonAnchor = anchorScreen;
        Log.Write($"panel açılış #{no}: tık alındı ({(sicak ? "sıcak: sayfa yüklü" : "soğuk: WebView2 kurulacak")}), kapsül={R(anchorScreen)}, saklı yer {(durum.Sakli != null ? "var" : "yok")}, dpi={DeviceDpi}");
        var dpi0 = DeviceDpi;
        Rectangle hedef;
        _durumKuruluyor = true;   // açılışta kodun kurduğu durum ve boyut kişinin tercihi sayılıp saklanmasın
        try
        {
            if (!_buyutulmusYuklendi) { _buyutulmus = durum.Buyutulmus == true; _buyutulmusYuklendi = true; }
            // Gizliyken küçültülmüş kalmış olabilir (küçültüp sonra gizleme): yer önce normal durumda hesaplanır, sonra büyütülmüşse büyütülür. Küçültülmüş açılış yok.
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            hedef = Yerlestir(anchorScreen);
            if (DeviceDpi != dpi0) { Log.Write($"panel açılış #{no}: yerleştirirken dpi değişti ({dpi0} -> {DeviceDpi}), yer yeniden hesaplandı"); hedef = Yerlestir(anchorScreen); }
            var istenenDurum = PanelPencereKurali.HedefDurum(_buyutulmus) == PencereDurumu.Buyutulmus ? FormWindowState.Maximized : FormWindowState.Normal;
            if (istenenDurum == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
            _shownAt = DateTime.UtcNow;
            _odaksizAcilis = !odakAl;
            try { Show(); } finally { _odaksizAcilis = false; }
            if (WindowState != istenenDurum) WindowState = istenenDurum;   // yeniden gösterimde durum bazen ilk atamada tutmaz: gösterdikten sonra da doğrula
            Log.Write($"panel açılış #{no}: pencere gösterildi +{_t0.ElapsedMilliseconds} ms, durum={WindowState}, Bounds={R(Bounds)}{(WindowState != FormWindowState.Normal ? " yerBoyutu=" + R(RestoreBounds) : "")} hedef={R(hedef)} ekran={Screen.FromRectangle(Bounds).DeviceName} çalışma={R(Screen.FromRectangle(Bounds).WorkingArea)} dpi={DeviceDpi}");
            if (odakAl) Activate(); // kullanıcı tıkladı; panelin odak almasına izin var
            else OdaksizOneAl();    // kendiliğinden açılış: görünür ama odak alınmaz
            // Büyütülmüşken Bounds ekran boyudur; yer ve boyut RestoreBounds'tadır. Yalnız normal durumda Bounds hedefle karşılaştırılır.
            var gercek = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (gercek != hedef)
            {
                // WinForms başka ölçekli ekrana geçişte pencereyi yeniden ölçekleyip kaydırabilir (gözlem 6 şüphesi): bir kez düzelt
                Log.Write($"panel açılış #{no}: yer hedefe uymadı, düzeltiliyor ({R(gercek)} -> {R(hedef)})");
                hedef = Yerlestir(anchorScreen);
                Log.Write($"panel açılış #{no}: düzeltme sonrası Bounds={R(Bounds)}");
            }
        }
        finally { _durumKuruluyor = false; }
        if (sicak) Log.Write($"panel açılış #{no}: sıcak açılış tamam +{_t0.ElapsedMilliseconds} ms (sayfa zaten yüklü)");
        if (!_hataModu) _init ??= InitWebViewAsync(_t0, no);
    }

    // ------------------------------------------------------------------ sunucu yokken sade hata görünümü
    // WebView2 ve sunucu gerekmez: boş beyaz sayfa yerine panel, ne olduğunu ve ne yapılacağını yazan sade bir görünüm ve "Yeniden dene" düğmesi gösterir.
    private bool _hataModu;
    private Panel? _hata;
    private Label? _hataBaslik, _hataMetin;
    private Button? _hataDugme;
    private string _hataDugmeYazisi = "";

    /// <summary>"Yeniden dene" düğmesine basıldı (kabuk sunucuyu yeniden açmayı dener).</summary>
    public event Action? YenidenDeneIstendi;

    /// <summary>Hata görünümü şu an gösteriliyor mu.</summary>
    internal bool HataGorunuyor => _hataModu;
    internal string? HataMetniSimdi => _hataMetin?.Text;
    internal bool HataDugmesiAcik => _hataDugme is { Enabled: true };

    public void HataGoster(string baslik, string metin, string dugme)
    {
        _hataDugmeYazisi = dugme;
        if (_hata == null)
        {
            _hata = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28), BackColor = BackColor, AccessibleName = baslik };
            _hataBaslik = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 15f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) };
            _hataMetin = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 10.5f), Margin = new Padding(0, 0, 0, 18) };
            _hataDugme = new Button { AutoSize = true, Padding = new Padding(14, 6, 14, 6), FlatStyle = FlatStyle.System, Margin = new Padding(0) };
            _hataDugme.Click += (_, _) => YenidenDeneIstendi?.Invoke();
            var duzen = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            duzen.Controls.Add(_hataBaslik); duzen.Controls.Add(_hataMetin); duzen.Controls.Add(_hataDugme);
            _hata.Controls.Add(duzen);
            void Sigdir() { if (duzen.ClientSize.Width > 60) _hataMetin.MaximumSize = new Size(duzen.ClientSize.Width - 8, 0); }
            duzen.SizeChanged += (_, _) => Sigdir();
            Controls.Add(_hata);
        }
        _hataModu = true;
        _hataBaslik!.Text = baslik; _hataMetin!.Text = metin;
        _hataDugme!.Text = dugme; _hataDugme.Enabled = true;
        HataRenkleri();
        _hata.Visible = true; _hata.BringToFront();
    }

    /// <summary>Hata görünümünün metnini değiştirir (ör. yeniden deneme sürerken "Açılıyor...", olmazsa "Yine açılamadı").</summary>
    public void HataDurumu(string metin, bool busy)
    {
        if (_hataMetin == null || _hataDugme == null) return;
        _hataMetin.Text = metin;
        _hataDugme.Enabled = !busy;
        _hataDugme.Text = _hataDugmeYazisi;
    }

    private void HataRenkleri()
    {
        if (_hata == null) return;
        _hata.BackColor = _palette.EffectivePanelBg;
        if (_hataBaslik != null) _hataBaslik.ForeColor = _palette.Text;
        if (_hataMetin != null) _hataMetin.ForeColor = _palette.SubText.A == 255 ? _palette.SubText : _palette.Text;
        foreach (Control c in _hata.Controls) { c.BackColor = _palette.EffectivePanelBg; foreach (Control k in c.Controls) if (k is Label) k.BackColor = _palette.EffectivePanelBg; }
    }

    /// <summary>Sunucu yeniden açıldı: hata görünümü kalkar, panel gerçek sayfaya bağlanır (görünürse hemen yüklenir).</summary>
    public void SunucuyaBaglan(string url)
    {
        _url = url;
        _hataModu = false;
        if (_hata != null) _hata.Visible = false;
        _pageLoaded = false;
        _init = null;
        if (Visible) _init = InitWebViewAsync(System.Diagnostics.Stopwatch.StartNew(), _acilisNo);
    }

    // ------------------------------------------------------------------ ekran süreci (WebView2) ölürse
    // Panel beyaz kalmaz: ilk çöküşte bir kez kendiliğinden toparlanır, kısa süre içinde yine çökerse sade mesaj ve "Yeniden dene" gösterilir.
    private readonly WebViewKurtarma _kurtarma = new();
    private bool _cokmeModu;

    /// <summary>Çöküş mesajı şu an gösteriliyor mu (sınama ve günlük için).</summary>
    internal bool CokmeMesajiGorunuyor => _cokmeModu && _hataModu;

    private void EkranSureciOldu(CoreWebView2ProcessFailedEventArgs e)
    {
        var tur = e.ProcessFailedKind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited => WebViewKurtarma.Tur.TarayiciKapandi,
            CoreWebView2ProcessFailedKind.RenderProcessExited => WebViewKurtarma.Tur.SayfaKapandi,
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => WebViewKurtarma.Tur.SayfaYanitVermiyor,
            _ => WebViewKurtarma.Tur.Diger,
        };
        var karar = _kurtarma.Ver(tur, DateTime.UtcNow);
        string ayrinti;
        try { ayrinti = $"{e.ProcessFailedKind}, neden {e.Reason}, çıkış kodu {e.ExitCode}"; } catch { ayrinti = e.ProcessFailedKind.ToString(); }
        Log.Write($"panel ekran süreci düştü ({ayrinti}): {karar}");
        if (karar == WebViewKurtarma.Karar.Yoksay) return;
        // Denetim kendi olayının içinde bırakılmaz: iş bir sonraki döngüye ertelenir.
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => KurtarmayiUygula(karar)); }
        catch (Exception ex) { Log.Error("panel kurtarma", ex); }
    }

    private void KurtarmayiUygula(WebViewKurtarma.Karar karar)
    {
        if (IsDisposed) return;
        try
        {
            switch (karar)
            {
                case WebViewKurtarma.Karar.Yenile:
                    try { _web.CoreWebView2?.Reload(); }
                    catch (Exception ex) { Log.Error("panel yeniden yükleme", ex); EkraniYenidenKur(); }
                    break;
                case WebViewKurtarma.Karar.YenidenKur:
                    EkraniYenidenKur();
                    break;
                case WebViewKurtarma.Karar.MesajGoster:
                    EkraniBirak();
                    _cokmeModu = true;
                    HataGoster(Strings.Get("panel.crash.title"), Strings.Get("panel.crash.text"), Strings.Get("panel.error.retry"));
                    break;
            }
        }
        catch (Exception ex) { Log.Error("panel kurtarma", ex); }
    }

    /// <summary>Çöküş mesajındaki "Yeniden dene": mesaj kalkar, ekran baştan kurulur, sayaç sıfırlanır.</summary>
    private void CokmedenSonraYenidenDene()
    {
        if (!_cokmeModu) return;
        _cokmeModu = false;
        _kurtarma.Sifirla();
        _hataModu = false;
        if (_hata != null) _hata.Visible = false;
        Log.Write("panel: çöküş sonrası kişi yeniden denedi");
        EkraniYenidenKur();
    }

    /// <summary>Ölü ekranı bırakıp yenisini kurar. Panel görünüyorsa hemen yükler, gizliyse sonraki açılışta yüklenir.</summary>
    private void EkraniYenidenKur()
    {
        EkraniBirak();
        if (Visible && !_hataModu) _init = InitWebViewAsync(System.Diagnostics.Stopwatch.StartNew(), ++_acilisNo);
    }

    private void EkraniBirak()
    {
        var old = _web;
        Controls.Remove(old);
        try { old.Dispose(); } catch (Exception ex) { Log.Error("WebView2 bırakma", ex); }
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);
        if (_hata is { Visible: true }) _hata.BringToFront();
        _init = null;
        _pageLoaded = false;
    }

    /// <summary>Sağ tık menüsünü süzer: yalnız metin işleri kalır. Bir hata olursa Edge'in tam menüsü yerine hiç menü çıkmaz.</summary>
    private static void BaglamMenusunuSuz(CoreWebView2ContextMenuRequestedEventArgs e)
    {
        try
        {
            var ogeler = e.MenuItems;
            var liste = ogeler.Select(o => ((string?)o.Name, o.Kind == CoreWebView2ContextMenuItemKind.Separator)).ToList();
            var kalan = BaglamMenusu.Suz(liste, e.ContextMenuTarget.IsEditable).Select(i => ogeler[i]).ToList();
            ogeler.Clear();
            foreach (var o in kalan) ogeler.Add(o);
            if (kalan.Count == 0) e.Handled = true;   // gösterilecek bir şey yok: menü açılmaz
        }
        catch (Exception ex)
        {
            Log.Error("sağ tık menüsü", ex);
            try { e.Handled = true; } catch { }
        }
    }

    private static string R(Rectangle r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

    /// <summary>Ekranların çalışma alanları, birincil ekran başta (kapsül konumu bilinmiyorsa onun sağ altı kullanılır).</summary>
    private static List<Rectangle> CalismaAlanlari()
        => Screen.AllScreens.OrderByDescending(s => s.Primary).Select(s => s.WorkingArea).ToList();

    /// <summary>Yeri ve boyut sınırlarını hesaplayıp uygular; uygulanan dikdörtgeni döner. Pencere görünürken de çalışır (sıfırlama).</summary>
    private Rectangle Yerlestir(Rectangle anchorScreen)
    {
        var alanlar = CalismaAlanlari();
        var olcek = DeviceDpi / 96.0;
        var hedef = PanelYerlesim.Hesapla(Durum().Sakli, alanlar, anchorScreen, olcek);
        var min = new Size((int)Math.Round(PanelYerlesim.EnKucukDp.Width * olcek), (int)Math.Round(PanelYerlesim.EnKucukDp.Height * olcek));
        MinimumSize = new Size(Math.Min(min.Width, hedef.Width), Math.Min(min.Height, hedef.Height));
        MaximumSize = PanelYerlesim.EnBuyuk(alanlar);
        Bounds = hedef;
        _sonUygulanan = hedef;
        return hedef;
    }

    /// <summary>Kişi paneli taşıdı ya da boyutlandırdı: yer ve boyut saklanır, sonraki açılışta aynen gelir.</summary>
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        // Büyütülmüş ya da küçültülmüşken Bounds ekran boyu ya da ekran dışıdır: saklanmaz (normal yer en son normal durumda saklanmıştır, büyütülmüş durum ayrı bayrakla saklanır).
        if (!Visible || !PanelPencereKurali.YerSaklanabilir(Pencere()) || Bounds == _sonUygulanan) return; // yer değişmediyse (yalnız başlığa tıklandı) saklama
        var olcek = DeviceDpi / 96.0;
        var s = new PanelSakli(Bounds.X, Bounds.Y, (int)Math.Round(Bounds.Width / olcek), (int)Math.Round(Bounds.Height / olcek));
        DurumuYaz(Durum() with { Sakli = s });
        _sonUygulanan = Bounds;
        Log.Write($"panel yeri saklandı: Bounds={R(Bounds)} ({s.GenDp}x{s.YukDp} dp)");
    }

    /// <summary>Saklı yer ve boyutu siler; panel görünüyorsa kapsülün yanına, varsayılan boyutla geri döner. Sonraki açılışlar da varsayılandır.</summary>
    public void YeriSifirla(Rectangle anchorScreen)
    {
        DurumuYaz(Durum() with { Sakli = null, Buyutulmus = null });
        _buyutulmus = false;
        Log.Write("panel yeri ve boyutu varsayılana döndürüldü");
        if (Visible)
        {
            if (!IsHandleCreated) _ = Handle;
            _durumKuruluyor = true;
            try { if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal; Yerlestir(anchorScreen); }   // büyütülmüş ya da küçültülmüşse normale döner
            finally { _durumKuruluyor = false; }
        }
    }

    /// <summary>İleti kaynağı panel adresinin kökeniyle (şema, ana bilgisayar, port) birebir aynı mı.</summary>
    internal static bool SameOrigin(string? source, string baseUrl)
        => Uri.TryCreate(source, UriKind.Absolute, out var s) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
           && s.Scheme == b.Scheme && string.Equals(s.Host, b.Host, StringComparison.OrdinalIgnoreCase) && s.Port == b.Port;

    private bool IsOwnOrigin(string? source) => SameOrigin(source, _url);

    private async Task InitWebViewAsync(System.Diagnostics.Stopwatch sw, int no)
    {
        try
        {
            Directory.CreateDirectory(_userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataDir).ConfigureAwait(true);
            Log.Write($"panel açılış #{no}: WebView2 ortamı hazır +{sw.ElapsedMilliseconds} ms");
            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            Log.Write($"panel açılış #{no}: WebView2 çekirdeği hazır +{sw.ElapsedMilliseconds} ms");
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            // Sağ tık: Edge'in tam menüsü yerine yalnız metin işleri (Kopyala, Kes, Yapıştır, Tümünü seç), yazı kutusunda yazım önerileri de kalır.
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.ContextMenuRequested += (_, e) => BaglamMenusunuSuz(e);
            // Geliştirici araçları (F12, İncele) kapalı, SOZ_DEVTOOLS=1 ile açılır. Yakınlaştırma (Ctrl+tekerlek) ve F5 ile yenileme açık kalır.
            core.Settings.AreDevToolsEnabled = GelistiriciAraclari.Acik(Environment.GetEnvironmentVariable(GelistiriciAraclari.OrtamAdi));
            core.ProcessFailed += (_, e) => EkranSureciOldu(e);
            core.NewWindowRequested += (_, e) => { e.Handled = true; TryOpenExternal(e.Uri); };
            var ilkGezinme = true; var ilkBitis = true; var ilkIcerik = true; // her başlatmada yalnız ilk olay günlüğe yazılır
            core.DOMContentLoaded += (_, _) =>
            {
                if (!ilkIcerik) return; ilkIcerik = false;
                Log.Write($"panel açılış #{no}: ilk içerik (DOMContentLoaded) +{sw.ElapsedMilliseconds} ms");
            };
            core.NavigationStarting += (_, e) =>
            {
                if (ilkGezinme) { ilkGezinme = false; Log.Write($"panel açılış #{no}: gezinme başladı +{sw.ElapsedMilliseconds} ms"); }
                if (!e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                { e.Cancel = true; TryOpenExternal(e.Uri); }
            };
            core.WebMessageReceived += (_, e) =>
            {
                // Yapıştırma tuş gönderir: yalnız kendi arayüzümüzün kökeninden gelen ileti kabul edilir.
                if (!IsOwnOrigin(e.Source)) { Log.Write("web iletisi yok sayıldı: yabancı köken"); return; }
                try { HandleWebMessage(e.WebMessageAsJson); } catch (Exception ex) { Log.Error("web iletisi", ex); }
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (ilkBitis) { ilkBitis = false; Log.Write($"panel açılış #{no}: gezinme bitti +{sw.ElapsedMilliseconds} ms (başarılı={e.IsSuccess})"); }
                if (!e.IsSuccess) return;
                _pageLoaded = true;
                // sayfa yüklenirken gelen "yeni iş" isteği, yükleme bitince iletiyle iletilir
                if (_newJobPending) { _newJobPending = false; PostString(WebMessages.NewJobToPage); }
                // yükleme sürerken gelen sayfa isteği (ilk gezinmeye yetişmediyse) yükleme bitince uygulanır
                if (_pendingHash is { } ph) { _pendingHash = null; try { _ = core.ExecuteScriptAsync("location.hash=" + System.Text.Json.JsonSerializer.Serialize(ph)); } catch (Exception ex) { Log.Error("panel adresi", ex); } }
            };
            // Esc'i yalnız arayüz yönetir (açık pencereyi kapatır, alt sayfadan geri döner). Kabuk Esc ile paneli gizlemez: eskiden alt sayfada Esc hem paneli gizliyor hem sayfayı değiştiriyordu.
            // henüz gezilmediyse ve "yeni iş" bekliyorsa doğrudan İşler adresine git
            var cold = _newJobPending;
            _newJobPending = false;
            var start = cold ? NewJobHash : _pendingHash ?? "";
            _pendingHash = null;
            _web.Source = new Uri(_url + start);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            RuntimeMissing = true;
            HandleRuntimeMissing();
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            Log.Error("WebView2 başlatma", ex);
            if (ex is System.Runtime.InteropServices.COMException || ex.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
                HandleRuntimeMissing();
        }
    }

    private void HandleWebMessage(string json)
    {
        var m = WebMessages.Parse(json);
        switch (m.Kind)
        {
            case WebMessageKind.Esc: break;   // eski "soz-esc" iletisi: Esc'i arayüz yönetir, kabuk paneli gizlemez
            case WebMessageKind.Paste:
            case WebMessageKind.Dictate:
            case WebMessageKind.ThemeChanged:
            case WebMessageKind.ResetPanelPlace:
            case WebMessageKind.GetShellPrefs:
            case WebMessageKind.SetShellPref:
                MessageReceived?.Invoke(m);
                break;
            case WebMessageKind.PickFolder: PickFolder(); break;
            case WebMessageKind.Unknown: Log.Write("bilinmeyen web iletisi yok sayıldı (tür: " + WebMessages.SafeType(m.Type) + ")"); break;
            default: Log.Write("bozuk web iletisi yok sayıldı"); break;
        }
    }

    /// <summary>Yerel klasör seçme penceresi; seçilen yol sayfaya {"type":"folder-picked"} olarak döner. Pencere açıkken panel odak kaybıyla gizlenmez.</summary>
    private async void PickFolder()
    {
        if (_picking) return;
        _picking = true; // profil beklenirken de panel odak kaybıyla gizlenmesin
        string? chosen = null;
        try
        {
            var start = await BaslangicKlasoruAsync().ConfigureAwait(true);
            using var dlg = new FolderBrowserDialog { Description = "Söz'ün çalışacağı proje klasörünü seç", UseDescriptionForTitle = true, ShowNewFolderButton = false };
            // InitialDirectory yalnız varsayılan klasör sayılır ve Windows'un "son kullanılan" kaydı onu ezer; SelectedPath (sondaki ayırıcıyla) o klasörün İÇİNDE açtırır.
            if (!string.IsNullOrEmpty(start))
            {
                dlg.InitialDirectory = start;
                dlg.SelectedPath = start.TrimEnd('\\', '/') + "\\";
            }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                chosen = dlg.SelectedPath;
                if (!string.IsNullOrWhiteSpace(chosen)) DurumuYaz(Durum() with { SonKlasor = chosen });
            }
        }
        catch (Exception ex) { Log.Error("klasör seçici", ex); }
        finally { _picking = false; }
        _shownAt = DateTime.UtcNow;
        if (Visible) { Activate(); FocusWeb(); }
        PostJson(WebMessages.FolderPickedJson(chosen));
    }

    /// <summary>Seçicinin açılacağı klasör (sıra: <see cref="KlasorBaslangic"/>). Profil çağrısı 300 ms içinde dönmezse beklenmeden sıradaki adaya geçilir: seçici asla takılmaz.</summary>
    private async Task<string> BaslangicKlasoruAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ProfilVarsayilan? profil = null;
        var durum = "profil yok";
        if (ProfilVarsayilanlari is { } getir)
        {
            try
            {
                using var cts = new CancellationTokenSource();
                var gorev = getir(cts.Token);
                var bitti = await Task.WhenAny(gorev, Task.Delay(300)).ConfigureAwait(true);
                if (bitti == gorev) { profil = await gorev.ConfigureAwait(true); durum = profil == null ? "profil boş" : "profil geldi"; }
                else { cts.Cancel(); durum = "profil 300 ms içinde dönmedi"; _ = gorev.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default); }
            }
            catch (Exception) { durum = "profil okunamadı"; }
        }
        string belgeler;
        try { belgeler = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { belgeler = ""; }
        var (yol, kaynak) = KlasorBaslangic.Karar(Durum().SonKlasor, profil?.UstKlasor, profil?.Surucu, belgeler, Directory.Exists);
        Log.Write($"klasör seçici başlangıcı: kaynak={kaynak} ({durum}, {sw.ElapsedMilliseconds} ms)"); // yol günlüğe yazılmaz
        return yol;
    }

    /// <summary>
    /// "Yeni iş": sayfa yüklüyse "soz:new-job" iletisi gider; değilse ilk gezinme (ya da yükleme sonu) bunu taşır.
    /// Paneli göstermez; gösterme çağıranın işidir.
    /// </summary>
    public void RequestNewJob()
    {
        if (_pageLoaded && _web.CoreWebView2 != null) PostString(WebMessages.NewJobToPage);
        else _newJobPending = true;
    }

    /// <summary>Panel görünürken web içeriğine klavye odağı ver (kullanıcı eylemi sonrası).</summary>
    public void FocusWeb() { try { _web.Focus(); } catch { } }

    private void PostString(string s) { try { _web.CoreWebView2?.PostWebMessageAsString(s); } catch (Exception ex) { Log.Error("web iletisi gönderme", ex); } }

    /// <summary>Sayfaya JSON iletisi gönderir (panel gizli olsa da gider).</summary>
    public void PostJson(string json) { try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Error("web iletisi gönderme", ex); } }

    private void HandleRuntimeMissing()
    {
        if (_runtimeMissingHandled) return;
        _runtimeMissingHandled = true;
        Hide();
        var r = MessageBox.Show(Strings.Get("webview.missing.text"), Strings.Get("webview.missing.title"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) TryOpenExternal(_url);
        _init = null; _runtimeMissingHandled = false;
    }

    private static void TryOpenExternal(string uri)
    {
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("dış bağlantı", ex); }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _sonEtkinKaybi = DateTime.UtcNow;
        // Varsayılan: panel ana pencere gibi kalır (terminalden kopyalayıp dönmek isteyen kaybetmesin). Eski davranış ayarla açılır; gösterildikten hemen sonraki sahte odak oynamaları yine yok sayılır.
        if (PanelOdak.OdakKaybindaGizle(OdakKaybindaGizleAyari(), Visible, _picking, (DateTime.UtcNow - _shownAt).TotalMilliseconds)) Hide();
    }

    /// <summary>Kapatma isteği gizlemeye çevrilmeli mi: kabuk açıkça izin vermedikçe ve sistem/uygulama çıkışı olmadıkça evet (CloseReason.None dahil).</summary>
    internal static bool ShouldCancelClose(CloseReason reason, bool allowClose)
        => !allowClose && reason is not (CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (ShouldCancelClose(e.CloseReason, AllowClose)) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    /// <summary>Duman sınaması: paneli GÖSTERMEDEN WebView2'yi başlatıp adrese gider, sonucu bildirir.</summary>
    public async Task<(bool Ok, string? Error, string? Title)> ProbeAsync(TimeSpan timeout)
    {
        try
        {
            _ = Handle; _ = _web.Handle;
            var navDone = new TaskCompletionSource<bool>();
            Directory.CreateDirectory(_userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataDir).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            _web.CoreWebView2.NavigationCompleted += (_, e) => navDone.TrySetResult(e.IsSuccess);
            _web.Source = new Uri(_url);
            var done = await Task.WhenAny(navDone.Task, Task.Delay(timeout)).ConfigureAwait(true);
            if (done != navDone.Task) return (false, "gezinme zaman aşımı", null);
            return (await navDone.Task, null, _web.CoreWebView2.DocumentTitle);
        }
        catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message, null); }
    }

    /// <summary>
    /// Duman sınaması: paneli GÖSTERMEDEN WebView2'yi başlatıp "yeni iş" adresine (soğuk başlangıç) gider, 1,5 sn sonra
    /// <c>document.activeElement.id</c> değerini okur. Arayüz hazır değilse "" gelebilir; bu hata değildir.
    /// </summary>
    public async Task<(bool Ok, string? Error, string Focus)> ProbeNewJobFocusAsync(TimeSpan timeout)
    {
        try
        {
            _ = Handle; _ = _web.Handle;
            var navDone = new TaskCompletionSource<bool>();
            Directory.CreateDirectory(_userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataDir).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            _web.CoreWebView2.NavigationCompleted += (_, e) => navDone.TrySetResult(e.IsSuccess);
            _web.Source = new Uri(_url + NewJobHash);
            var done = await Task.WhenAny(navDone.Task, Task.Delay(timeout)).ConfigureAwait(true);
            if (done != navDone.Task) return (false, "gezinme zaman aşımı", "");
            if (!await navDone.Task) return (false, "gezinme başarısız", "");
            await Task.Delay(1500).ConfigureAwait(true);
            var raw = await _web.CoreWebView2.ExecuteScriptAsync("document.activeElement && document.activeElement.id").ConfigureAwait(true);
            string focus;
            try { focus = System.Text.Json.JsonSerializer.Deserialize<string?>(raw) ?? ""; } catch { focus = raw ?? ""; }
            return (true, null, focus);
        }
        catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message, ""); }
    }

    /// <summary>Panel ekrana gösterilmeden PNG'ye alınır (ekran dışı ya da gizli pencerede önizleme yakalama).</summary>
    public async Task<string?> CapturePngAsync(string path, Size size)
    {
        try
        {
            _ = Handle; _ = _web.Handle;
            Size = size;
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataDir).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            var done = new TaskCompletionSource<bool>();
            _web.CoreWebView2.NavigationCompleted += (_, e) => done.TrySetResult(e.IsSuccess);
            _web.Source = new Uri(_url);
            if (await Task.WhenAny(done.Task, Task.Delay(8000)).ConfigureAwait(true) != done.Task) return "gezinme zaman aşımı";
            await Task.Delay(800).ConfigureAwait(true); // arayüz çizsin
            using var fs = File.Create(path);
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs).ConfigureAwait(true);
            return null;
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _idleTimer.Dispose(); _web.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
