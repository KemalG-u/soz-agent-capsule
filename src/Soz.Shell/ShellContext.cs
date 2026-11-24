using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using Soz.Capsule;
using Soz.Server;

namespace Soz.Shell;

/// <summary>Kabuğun ana nesnesi: sunucu, tepsi, kapsül, panel, kısayol, canlı yenileme.</summary>
internal sealed class ShellContext : ApplicationContext
{
    internal readonly Options Opt;
    internal readonly string DataDir;
    internal readonly List<string> Errors = new();

    internal SozServerHandle? Server;
    internal IApiClient Api = new FakeApiClient();
    internal NotifyIcon Tray = new();
    internal CapsuleForm? Capsule;
    /// <summary>Yeni kapsül (ayrı program). Çalışırken <see cref="Capsule"/> (eski kapsül) null'dır, tersi de öyle.</summary>
    internal KapsulYoneticisi? Kapsul;
    internal PanelForm? Panel;
    internal HotkeyManager Hotkey = null!;
    internal HotkeyManager NewJobHotkey = null!;
    internal TextPaster TextPaster = null!;
    internal ILayoutProbe LayoutProbe = new WinLayoutProbe();
    /// <summary>Yeni iş kısayolunun kayıt/düşme sonucu (duman ve günlük için).</summary>
    internal HotkeyResolution? NewJobInfo;
    /// <summary>Paket kısayolunun AltGr ölçümü (yalnız uyarı ve duman).</summary>
    internal HotkeyMeasure? PacketMeasure;
    internal string PacketRequested = "";
    internal ClipboardKeeper Keeper = null!;
    internal PacketPaster Paster = null!;
    internal DictationBridge Dictation = null!;
    internal ActiveProjectResolver Resolver = new();
    internal Dictionary<string, string> Settings = new(StringComparer.OrdinalIgnoreCase);
    internal Palette Pal = Palette.LightTheme;
    internal bool TrayCreated;
    internal string? ServerError;
    /// <summary>Açılışta veri\sunucu.json'dan okunan önceki sunucu notu (bayat dosya yok sayılır).</summary>
    internal string? PriorServerNote;

    private SynchronizationContext _ui;
    private readonly ContextMenuStrip _menu = new();
    private ToolStripMenuItem _hotkeyWarn = null!;
    private ToolStripMenuItem _newJobWarn = null!;
    private ToolStripMenuItem _autoStartItem = null!, _deleteDataItem = null!, _updateItem = null!, _versionItem = null!;
    private ToolStripMenuItem _notifyItem = null!;
    private readonly BildirimYoneticisi _bildirim = new();
    private bool _bildirimAcik = true;
    private bool _bildirimBalonu;
    private string _sonBalonAdresi = "#/suan";
    private GuncellemeDenetimi? _guncelleme;
    private string? _yeniSurum;
    private readonly GuncellemeBalonu _guncellemeBalonu = new();
    private bool _updateChecking;
    /// <summary>Kişi güncelleme denetimini tercih dosyasıyla kapattı (menü satırı bunu söyler).</summary>
    private bool _guncellemeKapali;
    /// <summary>İndirme sürerken ikinci tıklama yeni indirme başlatmaz.</summary>
    private readonly GuncellemeKilidi _indirmeKilidi = new();
    private System.Windows.Forms.Timer? _guncellemeZamanlayici;
    /// <summary>Küçük pencere kişi tarafından gizlendi ("1 saat gizle"). Tepsideki "Küçük pencereyi göster" satırı yalnız o zaman görünür.</summary>
    private bool _kapsulGizli;
    private ToolStripMenuItem _showCapsuleItem = null!;
    /// <summary>Sunucu, veri daha yeni bir Söz sürümüyle yazıldığı için açılmadı (veriye dokunulmadı).</summary>
    internal bool SemaYeni { get; private set; }
    /// <summary>Ölçüm kipi için sayaçlar (hafif): yenileme, etkin proje zamanlayıcısı ve canlı olaylar. Boşta işlemci payının nereden geldiğini gösterir.</summary>
    internal readonly Dictionary<string, (long N, long Ms)> Sayac = new();
    internal void SayacEkle(string ad, long ms) { lock (Sayac) { Sayac.TryGetValue(ad, out var v); Sayac[ad] = (v.N + 1, v.Ms + ms); } }
    private IntPtr _pasteTarget;
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _activeTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _dictationTimer = new() { Interval = 2000 };
    private readonly CancellationTokenSource _cts = new();
    private Icon? _trayIcon;

    private (int Waiting, int Stalled, int FailedJobs)? _counts;
    private IReadOnlyList<ProjectInfo> _projects = [];
    private IReadOnlyList<SessionInfo> _sessions = [];
    private bool _hasData;
    private bool _sseConnected;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _refreshing, _refreshAgain;
    private bool _exiting;
    private bool _initDone;
    private bool _pendingShowPanel;
    private bool _balloonShown;
    private string _sonProje = "";
    private int _dictationPollInFlight;
    private DateTime _lastHostAttachUtc = DateTime.MinValue;
    private DateTime _dictationFastUntilUtc = DateTime.MinValue;
    private string _lastDictationPhase = "kapali";
    // "Yeni iş" odağı dikte sürerken ertelenir (OdakPolitikasi.YeniIsKarari): ilk istek anı, son okunan dikte durumu ve en çok 15 sn sonra zorla odak veren tek atımlık zamanlayıcı.
    private DictationHostStatus _sonDikteDurumu = DictationHostStatus.Unavailable;
    private DateTime? _yeniIsIstegi;
    private readonly System.Windows.Forms.Timer _yeniIsZamanlayici = new() { Interval = (int)OdakPolitikasi.YeniIsEnCokBekleme.TotalMilliseconds + 100 };

    public ShellContext(Options opt, string dataDir)
    {
        Opt = opt;
        DataDir = dataDir;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    }

    public string BaseUrl => Server?.Url ?? "";

    // ------------------------------------------------------------------ açılış
    public async Task InitAsync(bool deferCapsule)
    {
        _ui = SynchronizationContext.Current ?? _ui;
        Directory.CreateDirectory(DataDir);

        // 1) sunucu. Önceki sunucu.json'daki pid ölüyse dosya bayattır (çökme artığı): yok sayılır, kabuk kendi sunucusunu açar ve dosyayı yeniler.
        var prior = Soz.Core.ServerFile.Read(DataDir);
        if (prior != null)
        {
            PriorServerNote = prior.Alive ? $"canlı:{prior.Pid}" : $"bayat:{prior.Pid}";
            Log.Write(prior.Alive
                ? $"başka bir Söz sunucusu canlı (pid {prior.Pid}, {prior.Url}), kabuk kendi sunucusunu açar"
                : $"bayat sunucu.json (pid {prior.Pid} çalışmıyor) yok sayıldı");
        }
        await StartServerAsync().ConfigureAwait(true);

        if (Server != null) { BindFocuser(); BindPrefs(); }

        // 2) API istemcisi
        if (Opt.FakeApi) Api = new FakeApiClient();
        else if (Server != null) Api = new ApiClient(Server.Url, Server.Token);
        else Api = new ApiClient("http://127.0.0.1:1", "");

        // 3) ayarlar
        await LoadSettingsAsync().ConfigureAwait(true);
        Pal = Palette.For(Settings.GetValueOrDefault("theme"), ResolveThemeFamilyFromSettings());
        ApplyProcessColorMode(Settings.GetValueOrDefault("theme"));
        Strings.Language = Settings.GetValueOrDefault("language") == "en" ? "en" : "tr";

        // 4) parçalar
        var clip = new WinClipboardBackend();
        Keeper = new ClipboardKeeper(clip, new UiScheduler());
        Paster = new PacketPaster(Api, clip, Keeper);
        TextPaster = new TextPaster(clip, Keeper);
        Dictation = new DictationBridge(() => Opt.DictationPathOverride ?? Settings.GetValueOrDefault("dictationPath"));

        BuildTray();
        // Sunucu açılamadıysa kişi boş sayfa görmesin: panel sade bir açıklama ve "Yeniden dene" düğmesi gösterir; panel kendiliğinden açılmıyorsa (sessiz başlatma) saat yanında kısa balon çıkar.
        if (Server == null && !Opt.FakeApi) BalonGoster(Strings.Get(SemaYeni ? "msg.schema.new" : "msg.server.fail"), ToolTipIcon.Warning, 8000);
        Hotkey = new HotkeyManager();
        Hotkey.Pressed += () => _ = PastePacketAsync();
        RegisterHotkey();
        NewJobHotkey = new HotkeyManager(0x504C, "SozHotkeyWindowNewJob");
        NewJobHotkey.Pressed += OnNewJobHotkey;
        RegisterNewJobHotkey();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        if (!Opt.NoCapsule && !deferCapsule) ShowCapsule();

        await RefreshAsync().ConfigureAwait(true);
        if (!Opt.IsHeadless && Opt.SmokePath == null) StartLive(); // ölçüm kipinde de çalışır: ölçüm gerçek kullanımı yansıtmalı

        _initDone = true;
        if (!Opt.IsHeadless && Opt.SmokePath == null && Opt.OlcPath == null)
        {
            AnnounceAutoStartOnce();
            _ = StartUpdateWatchAsync();
        }
        var shouldOpenOnLaunch = !Opt.Background && !Opt.IsHeadless && Opt.SmokePath == null && Opt.OlcPath == null;
        if (_pendingShowPanel || shouldOpenOnLaunch)
        {
            // açılış sırasında gelen ikinci örnek isteği kullanıcının isteğidir; program kendiliğinden açıyorsa odak alınmaz
            var neden = _pendingShowPanel ? PanelAcilisi.IkinciOrnek : PanelAcilisi.Acilis;
            _pendingShowPanel = false;
            OpenPanelAndBringToFront(neden);
        }
    }

    /// <summary>Sunucuyu açar. Hata ayrıntısı yalnız günlüğe ve <see cref="ServerError"/> alanına gider, ekrana ham hata çıkmaz (ekranda sade cümle gösterilir).</summary>
    private async Task<bool> StartServerAsync()
    {
        try
        {
            Server = await SozServer.StartAsync(DataDir, Opt.Port).ConfigureAwait(true);
            ServerError = null;
            return true;
        }
        catch (Exception ex)
        {
            ServerError = ex.Message;
            SemaYeni = AcilisHatasi.SemaYeni(ex);   // eski sürüm yeni şemalı veriyi açmaz: kişiye "kapatıp yeniden aç" değil "yeni sürümü kur" denir
            Errors.Add("sunucu: " + ex.Message);
            Log.Error("sunucu başlatma", ex);
            return false;
        }
    }

    private bool _sunucuYenidenDeniyor;

    /// <summary>
    /// Panelin hata sayfasındaki "Yeniden dene" düğmesi: sunucuyu yeniden açmayı dener. Olursa paneli gerçek sayfaya bağlar,
    /// olmazsa sade bir cümleyle bir daha denenebileceğini söyler. Ayrıntı günlüğe yazılır.
    /// </summary>
    internal async Task RetryServerAsync()
    {
        if (_exiting || _sunucuYenidenDeniyor || Server != null) return;
        _sunucuYenidenDeniyor = true;
        try
        {
            Panel?.HataDurumu(Strings.Get("panel.error.busy"), busy: true);
            if (!await StartServerAsync().ConfigureAwait(true))
            {
                Panel?.HataDurumu(Strings.Get("panel.error.again"), busy: false);
                return;
            }
            Log.Write("sunucu yeniden denemede açıldı");
            BindFocuser(); BindPrefs();
            try { (Api as IDisposable)?.Dispose(); } catch { }
            Api = Opt.FakeApi ? new FakeApiClient() : new ApiClient(Server!.Url, Server.Token);
            var clip = new WinClipboardBackend();
            Paster = new PacketPaster(Api, clip, Keeper);
            await LoadSettingsAsync().ConfigureAwait(true);
            Panel?.SunucuyaBaglan(BaseUrl + "/");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("sunucu yeniden deneme", ex);
            Panel?.HataDurumu(Strings.Get("panel.error.again"), busy: false);
        }
        finally { _sunucuYenidenDeniyor = false; }
    }

    private string? ResolveThemeFamilyFromSettings()
        => Settings.GetValueOrDefault("themeFamily")
           ?? Settings.GetValueOrDefault("capsuleTheme");

    /// <summary>İkinci örnek başlatıldığında gelen "show-panel" sinyali: paneli anında açıp kullanıcının önüne getirir.</summary>
    internal void ActivateFromSecondInstance()
    {
        if (_exiting) return;
        _ui.Post(_ =>
        {
            if (_exiting) return;
            if (!_initDone) { _pendingShowPanel = true; return; }
            OpenPanelAndBringToFront(PanelAcilisi.IkinciOrnek);
        }, null);
    }

    /// <summary>Dikte (KarıncaAI) şu an dinliyor ya da çeviriyor mu? Köprü durum vermiyorsa (eski KarıncaAI sürümü) hep false.</summary>
    internal bool DikteSuruyor { get; private set; }

    private void OpenPanelAndBringToFront(PanelAcilisi neden)
    {
        if (_exiting) return;
        var odak = OdakPolitikasi.OdakAlsin(neden, DikteSuruyor);
        if (!OpenPanel(odak)) return;
        if (odak && Panel is { IsDisposed: false } p)
        {
            try
            {
                p.BringToFront();
                if (p.IsHandleCreated) Native.SetForegroundWindow(p.Handle);
                p.Activate();
            }
            catch (Exception ex) { Log.Error("paneli öne getirme", ex); }   // panel açık kalır, yalnız odak alamadı: kişiye söylemeye değmez
        }
    }

    /// <summary>
    /// Kişinin başlattığı bir panel eylemini korur: bir şey ters giderse ayrıntı günlüğe yazılır ve kişiye sade bir cümle gösterilir (sessizce hiçbir şey olmaması yerine).
    /// Dönüş true: eylem hatasız bitti.
    /// </summary>
    private bool KisiEylemi(string ad, Action eylem, string mesajAnahtari = "msg.panel.fail")
    {
        try { eylem(); return true; }
        catch (Exception ex)
        {
            Log.Error(ad, ex);
            if (!_exiting) Soyle(mesajAnahtari);
            return false;
        }
    }

    /// <summary>
    /// Kapsülü gösterir. Ayar "eski" değilse yeni kapsül (ayrı program) denenir. Dosyası yoksa ya da başlamazsa eski kapsüle düşülür ve günlüğe sade bir satır yazılır.
    /// </summary>
    internal void ShowCapsule()
    {
        if (Opt.NoCapsule || Capsule != null || Kapsul != null) return;
        var istenen = Opt.KapsulTuru ?? (Opt.SmokePath != null ? KapsulPolitikasi.EskiDeger : null);
        if (KapsulPolitikasi.Sec(istenen, Settings.GetValueOrDefault(KapsulPolitikasi.AyarAdi)) == KapsulTuru.Yeni)
        {
            var exe = KapsulPolitikasi.ExeBul(AppContext.BaseDirectory);
            if (exe == null) Log.Write("yeni kapsül dosyası bulunamadı (" + KapsulPolitikasi.ExeAdi + "), eski kapsül kullanılıyor");
            else
            {
                var mod = KapsulPolitikasi.ZeminKipi(Settings.GetValueOrDefault("theme"));
                var temaAilesi = Palette.NormalizeFamilyOrNull(ResolveThemeFamilyFromSettings());
                var y = new KapsulYoneticisi(exe, DataDir, mod, Opt.KapsulEk, _ui, OnKapsulKomutu,
                    neden => { if (neden != null) Log.Write(neden + ", eski kapsül kullanılıyor"); FallBackToOldCapsule(); },
                    temaAilesi, NewJobInfo is { Registered: true } ? NewJobInfo.Display : null);
                if (y.Baslat())
                {
                    Kapsul = y;
                    if (_sonProje.Length > 0) y.Proje(_sonProje);
                    if (_lastDictationPhase != "kapali") y.DikteDurum(_lastDictationPhase);
                    return;
                }
                y.Dispose();
                Log.Write("yeni kapsül açılamadı, eski kapsül kullanılıyor");
            }
        }
        ShowOldCapsule();
    }

    private void FallBackToOldCapsule()
    {
        var y = Kapsul; Kapsul = null;
        try { y?.Dispose(); } catch { }
        if (!_exiting) ShowOldCapsule();
    }

    private void ShowOldCapsule()
    {
        if (Opt.NoCapsule || Capsule != null) return;
        Capsule = new CapsuleForm(Pal) { PopupMenu = _menu, ReducedMotion = !Native.ClientAreaAnimationEnabled() };
        Capsule.BodyClicked += TogglePanel;
        Capsule.NewJobClicked += StartNewJob;
        Capsule.NewJobHotkey = NewJobInfo is { Registered: true } ? NewJobInfo.Display : "";
        Capsule.DictateClicked += DictateFromUi;
        Capsule.PacketClicked += () => _ = CopyPacketAsync();
        Capsule.MoveEnded += p => _ = SavePositionAsync(p);
        Capsule.SetModel(CurrentModel());
        Point? saved = null;
        if (int.TryParse(Settings.GetValueOrDefault("capsuleX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(Settings.GetValueOrDefault("capsuleY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            saved = new Point(x, y);
        Capsule.Start(saved);
    }

    // ------------------------------------------------------------------ yeni kapsülden gelen komutlar
    private void OnKapsulKomutu(KapsulKomutu k)
    {
        if (_exiting) return;
        // Küçük pencereden gelen her komut kişinin tıkladığı bir eylemdir: hata olursa günlüğe yazılır ve kişi sade bir cümle görür.
        KisiEylemi("küçük pencere komutu (" + k.Tur + ")", () => KapsulKomutunuIsle(k), "msg.action.fail");
    }

    private void KapsulKomutunuIsle(KapsulKomutu k)
    {
        switch (k.Tur)
        {
            case KomutTuru.Panel: OpenPanelAt(KabukProtokol.PanelAdresi(k.IsId)); break;
            case KomutTuru.PanelKapat: if (Panel is { IsDisposed: false } p) p.Hide(); break;
            case KomutTuru.YeniIs: StartNewJob(); break;
            case KomutTuru.Dikte: DictateFromUi(); break;
            case KomutTuru.Paket: _ = CopyPacketAsync(); break;
            case KomutTuru.IceAl: _ = ImportAsync(); break;
            case KomutTuru.Ayar: OpenPanelAt("#/ayarlar"); break;
            case KomutTuru.Metin: _ = PasteFromCapsuleAsync(k.Metin, k.Hedef); break;
            case KomutTuru.Cikis: _ = CikisIsteginiIsleAsync(); break;
            case KomutTuru.Konum: break;
            case KomutTuru.Gizli: _kapsulGizli = k.Metin == "1"; break;   // menü her açılışta okur (RefreshMenuState)
        }
    }

    private void OpenPanelAt(string? hash)
        => KisiEylemi("panel açma (" + hash + ")", () => { _pasteTarget = IntPtr.Zero; EnsurePanel().OpenNear(PanelAnchor(), hash); });

    /// <summary>Yeni kapsülün yazı satırından gelen metin: satır açılmadan önceki pencereye yapıştırılır, olmazsa panoda kalır.</summary>
    private async Task PasteFromCapsuleAsync(string? text, long target)
    {
        TextPasteOutcome outcome;
        try
        {
            if (!TextPasteRules.Acceptable(text)) outcome = TextPasteOutcome.Rejected;
            else outcome = await TextPaster.PasteAsync(text, TextPaster.IsUsableTarget((IntPtr)target) ? (IntPtr)target : IntPtr.Zero).ConfigureAwait(true);
        }
        catch (Exception ex) { Log.Error("kapsülden yapıştırma", ex); outcome = TextPasteOutcome.Failed; }
        Flash(outcome switch
        {
            TextPasteOutcome.Pasted => "flash.text.pasted",
            TextPasteOutcome.Copied => "flash.text.copied",
            TextPasteOutcome.Rejected => "flash.text.rejected",
            _ => "flash.text.failed",
        });
    }

    /// <summary>"Yeni iş" kısayolu: yeni kapsülde yazı satırını açar, eski kapsülde paneli iş kutusu odaklı açar.</summary>
    private void OnNewJobHotkey()
    {
        if (Kapsul is { Calisiyor: true } k) { if (!_exiting) KisiEylemi("yeni iş kısayolu", k.Kisayol, "msg.action.fail"); }
        else StartNewJob();
    }

    internal void StartLive()
    {
        _pollTimer.Tick += (_, _) =>
        {
            if (!_sseConnected || (DateTime.UtcNow - _lastRefresh).TotalSeconds > 30) _ = RefreshAsync();
        };
        _pollTimer.Start();
        _activeTimer.Tick += (_, _) => UpdateActive(false);
        _activeTimer.Start();
        _dictationTimer.Tick += (_, _) => PollDictationStatus();
        _yeniIsZamanlayici.Tick += (_, _) => YeniIsOdagiVer(); // en çok 15 sn: dikte takılsa da pencere odaksız kalmaz
        _dictationTimer.Start();
        PollDictationStatus();
        _ = LiveLoopAsync(_cts.Token);
    }

    private void PollDictationStatus()
    {
        if (_exiting || Opt.IsHeadless || Opt.SmokePath != null || Opt.OlcPath != null || Opt.DictationPathOverride != null) return;
        if (Interlocked.CompareExchange(ref _dictationPollInFlight, 1, 0) != 0) return;
        var needAttach = !Dictation.IsHostAttached || (DateTime.UtcNow - _lastHostAttachUtc).TotalSeconds >= 5;
        _ = Task.Run(() =>
        {
            try
            {
                DictationHostStatus st;
                if (needAttach)
                {
                    _lastHostAttachUtc = DateTime.UtcNow;
                    st = Dictation.AttachAndQueryStatus(150);
                }
                else
                {
                    st = Dictation.QueryStatus(100);
                }
                if (!_exiting) _ui.Post(_ => ApplyDictationStatus(st), null);
            }
            catch { }
            finally { Interlocked.Exchange(ref _dictationPollInFlight, 0); }
        });
    }

    private void ApplyDictationStatus(DictationHostStatus status)
    {
        if (_exiting) return;
        _sonDikteDurumu = status;
        DikteSuruyor = OdakPolitikasi.DikteSuruyor(status);
        YeniIsBekleyeniDene();
        var fast = DikteSuruyor
                   || DateTime.UtcNow < _dictationFastUntilUtc;
        var targetInterval = fast ? 250 : 2000;
        if (_dictationTimer.Interval != targetInterval) _dictationTimer.Interval = targetInterval;

        var phase = DictationBridge.ToCapsulePhase(status) ?? "kapali";
        if (phase != _lastDictationPhase)
        {
            _lastDictationPhase = phase;
            Kapsul?.DikteDurum(phase);
        }
    }

    internal void RequestExitFromChannel()
    {
        if (_exiting) return;
        _ui.Post(_ => ExitApp(), null);
    }

    private async Task LiveLoopAsync(CancellationToken ct)
    {
        var loggedFail = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Api.StreamEventsAsync(
                    k => { SayacEkle("sse_olay:" + k, 0); _ui.Post(_ => { _ = RefreshAsync(); }, null); },
                    () => { _sseConnected = true; loggedFail = false; _ui.Post(_ => { _ = RefreshAsync(); }, null); },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (!loggedFail) { Log.Write("SSE koptu: " + ex.Message); loggedFail = true; }
            }
            _sseConnected = false;
            try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------------ ayarlar
    private string LocalStatePath => Path.Combine(DataDir, "shell-state.json");

    private async Task LoadSettingsAsync()
    {
        try { Settings = new Dictionary<string, string>(await Api.GetSettingsAsync().ConfigureAwait(true), StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) { Log.Error("ayar okuma", ex); }
        if (File.Exists(LocalStatePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(LocalStatePath));
                foreach (var k in new[] { "capsuleX", "capsuleY", "themeFamily" })
                    if (!Settings.ContainsKey(k) && doc.RootElement.TryGetProperty(k, out var v))
                        Settings[k] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            }
            catch (Exception ex) { Log.Error("yerel durum okuma", ex); }
        }
    }

    private void SaveLocalState()
    {
        try
        {
            var xOk = int.TryParse(Settings.GetValueOrDefault("capsuleX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x);
            var yOk = int.TryParse(Settings.GetValueOrDefault("capsuleY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y);
            var fam = ResolveThemeFamilyFromSettings();
            var d = new Dictionary<string, object?>();
            if (xOk && yOk) { d["capsuleX"] = x; d["capsuleY"] = y; }
            if (!string.IsNullOrWhiteSpace(fam)) d["themeFamily"] = Palette.NormalizeFamily(fam);
            File.WriteAllText(LocalStatePath, JsonSerializer.Serialize(d));
        }
        catch (Exception ex) { Log.Error("yerel durum yazma", ex); }
    }

    private async Task SavePositionAsync(Point p)
    {
        Settings["capsuleX"] = p.X.ToString(CultureInfo.InvariantCulture);
        Settings["capsuleY"] = p.Y.ToString(CultureInfo.InvariantCulture);
        SaveLocalState();
        try
        {
            await Api.PutSettingsAsync(new Dictionary<string, object?> { ["capsuleX"] = p.X, ["capsuleY"] = p.Y }).ConfigureAwait(true);
        }
        catch (Exception ex) { Log.Error("konum kaydı", ex); }
    }

    // ------------------------------------------------------------------ tepsi ve canlı tema
    private void BuildTray()
    {
        _hotkeyWarn = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _menu.Items.Add(_hotkeyWarn);
        _newJobWarn = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _menu.Items.Add(_newJobWarn);
        _menu.Items.Add(Strings.Get("menu.open"), null, (_, _) => OpenPanel());
        _menu.Items.Add(Strings.Get("menu.newjob"), null, (_, _) => StartNewJob());
        _menu.Items.Add(Strings.Get("menu.import"), null, (_, _) => _ = ImportAsync());
        _menu.Items.Add(Strings.Get("menu.packet"), null, (_, _) => _ = CopyPacketAsync());
        _menu.Items.Add(Strings.Get("menu.dictate"), null, (_, _) => DictateFromUi());
        _menu.Items.Add(Strings.Get("menu.panelreset"), null, (_, _) => ResetPanelPlace());
        _showCapsuleItem = new ToolStripMenuItem(Strings.Get("menu.capsule.show")) { Visible = false };
        _showCapsuleItem.Click += (_, _) => KucukPencereyiGoster();
        _menu.Items.Add(_showCapsuleItem);
        _menu.Items.Add(new ToolStripSeparator());
        _autoStartItem = new ToolStripMenuItem(Strings.Get("menu.autostart"));
        _autoStartItem.Click += (_, _) => ToggleAutoStart();
        _menu.Items.Add(_autoStartItem);
        _notifyItem = new ToolStripMenuItem(Strings.Get("menu.notify"));
        _notifyItem.Click += (_, _) => ToggleNotifications();
        _menu.Items.Add(_notifyItem);
        _bildirimAcik = KabukTercihleri.Oku(DataDir).Bildirimler;
        _deleteDataItem = new ToolStripMenuItem(Strings.Get("menu.deletedata"));
        _deleteDataItem.Click += (_, _) => ToggleDeleteDataOnUninstall();
        _menu.Items.Add(_deleteDataItem);
        _menu.Items.Add(Strings.Get("menu.opendata"), null, (_, _) => OpenDataFolder());
        GuncellemeyiHazirla();
        _updateItem = new ToolStripMenuItem(Strings.Get("menu.update.check"));
        _updateItem.Click += (_, _) => _ = UpdateClickedAsync();
        _menu.Items.Add(_updateItem);
        _versionItem = new ToolStripMenuItem(Strings.Get("menu.version", SozVersion())) { Enabled = false };
        _menu.Items.Add(_versionItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Strings.Get("menu.exit"), null, (_, _) => _ = CikisIsteginiIsleAsync());
        _menu.Opening += (_, _) => RefreshMenuState();
        RefreshMenuState();

        ApplyMenuTheme(Pal);
        _trayIcon = TrayIconFactory.Create(Math.Max(16, SystemInformation.SmallIconSize.Width), Pal);
        Tray.Icon = _trayIcon;
        Tray.Text = Strings.Get("app");
        Tray.ContextMenuStrip = _menu;
        Tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        Tray.BalloonTipClicked += (_, _) => { if (_bildirimBalonu && !_exiting) OpenPanelAt(_sonBalonAdresi); };
        Tray.Visible = true;
        TrayCreated = Tray.Visible;
    }

    // ------------------------------------------------------------------ tepsi: açılışta başlama, veri, güncelleme
    private static string SozVersion()
    {
        return Soz.Core.SozInfo.Version; // tek kaynak: Directory.Build.props
    }

    /// <summary>Menü her açılırken işaretleri gerçek durumdan okur (Görev Yöneticisi'nden yapılan değişiklik de görünür).</summary>
    private void RefreshMenuState()
    {
        try
        {
            _autoStartItem.Checked = StartWithWindows.IsEnabled();
            _notifyItem.Checked = _bildirimAcik;
            _deleteDataItem.Checked = KabukTercihleri.Oku(DataDir).KaldirirkenVeriyiSil;
            // güncelleme satırı her zaman görünür: varsayılan besleme var, yalnız kişi tercihle kapattıysa gri "kapalı" yazar
            var (guncellemeMetni, guncellemeEtkin) = GuncellemeMenusu.Satir(_guncelleme == null, _indirmeKilidi.Suruyor, _updateChecking, _yeniSurum);
            _updateItem.Visible = true;
            _updateItem.Text = guncellemeMetni; _updateItem.Enabled = guncellemeEtkin;
            _showCapsuleItem.Visible = _kapsulGizli && Kapsul is { Calisiyor: true };
            foreach (ToolStripItem item in _menu.Items) item.ForeColor = item.Enabled ? Pal.Text : Pal.SubText;
        }
        catch (Exception ex) { Log.Error("menü durumu", ex); }
    }

    private void ToggleAutoStart()
    {
        if (_exiting) return;
        var t = KabukTercihleri.Oku(DataDir);
        var sonuc = AcilisKaydi.Degistir(StartWithWindows.IsEnabled(), () => StartWithWindows.Enable(Environment.ProcessPath), () => StartWithWindows.Disable(), () => StartWithWindows.IsEnabled());
        if (sonuc.Basarili)
        {
            t.AcilistaBasla = sonuc.YeniDeger;
            t.Yaz(DataDir);
        }
        else
        {
            Log.Write("açılışta başlatma kaydı değiştirilemedi");
            Soyle("msg.autostart.fail");
        }
        RefreshMenuState();
    }

    /// <summary>
    /// Sunucu bu süreçte çalıştığı için "O pencereye git" eylemi (oturum kimliğiyle gelen) gerçek pencere işini burada yapar:
    /// Windows Terminal'de doğru sekme, editörde klasör penceresi. Belirsizlik ipucu varsa kişiye balonla söylenir.
    /// </summary>
    private void BindFocuser()
    {
        Soz.Core.Attention.SessionActions.Focuser = OdakBaglayici.Olustur(PencereOnuneGetirici.Getir,
            pid => Soz.Core.Profile.Win32ProcessInspector.ReadPebStrings(pid!.Value).CurrentDirectory,
            ipucu =>
            {
                if (_exiting) return;
                _ui.Post(_ => { try { _bildirimBalonu = false; Tray.ShowBalloonTip(9000, Strings.Get("app"), ipucu, ToolTipIcon.Info); } catch (Exception ex) { Log.Error("pencere ipucu balonu", ex); } }, null);
            });
    }

    private TercihKopru? _kopru;
    private TercihKopru Kopru => _kopru ??= TercihKopru.Gercek(DataDir, () => _bildirimAcik, v => _bildirimAcik = v, () => _ui.Post(_ => { if (!_exiting) RefreshMenuState(); }, null));

    /// <summary>Arayüzdeki Ayarlar'ın tepsi tercihlerini (açılışta başlama, bildirimler, kaldırırken veriyi sil) değiştirebilmesi için sunucuya bağlar.</summary>
    private void BindPrefs() => Kopru.Bagla();

    private void ToggleNotifications()
    {
        if (_exiting) return;
        var t = KabukTercihleri.Oku(DataDir);
        t.Bildirimler = !t.Bildirimler;
        t.Yaz(DataDir);
        _bildirimAcik = t.Bildirimler;
        RefreshMenuState();
    }

    /// <summary>Seni bekleyen yeni oturum varsa tepsi balonu gösterir (kurallar: BildirimYoneticisi). Hata yenilemeyi bozmaz.</summary>
    private async Task NotifyAttentionAsync(IReadOnlyList<AttentionItemInfo>? ogeler)
    {
        if (!_bildirimAcik || _exiting || Opt.IsHeadless || Opt.SmokePath != null || Opt.OlcPath != null) return;
        try
        {
            if (ogeler == null) return;
            var b = _bildirim.Degerlendir(ogeler, Panel is { IsDisposed: false, IsOpen: true });
            if (b == null) return;
            _bildirimBalonu = true;
            _sonBalonAdresi = b.Adres;   // balona tıklayınca o oturumun kartı açılır
            Tray.ShowBalloonTip(10000, b.Baslik, b.Metin, ToolTipIcon.Info);
        }
        catch (Exception ex) { Log.Error("bildirim", ex); }
    }

    private void ToggleDeleteDataOnUninstall()
    {
        if (_exiting) return;
        var t = KabukTercihleri.Oku(DataDir);
        if (!t.KaldirirkenVeriyiSil)
        {
            var cevap = MessageBox.Show(Strings.Get("dlg.deletedata.text"), Strings.Get("dlg.deletedata.title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (cevap != DialogResult.Yes) { RefreshMenuState(); return; }
            t.KaldirirkenVeriyiSil = true;
        }
        else t.KaldirirkenVeriyiSil = false;
        t.Yaz(DataDir);
        RefreshMenuState();
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(DataDir);
            using var p = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log.Error("veri klasörü açma", ex); Soyle("msg.datafolder.fail"); }
    }

    /// <summary>Açılıştan kısa süre sonra sessiz denetim. Adres yoksa, kurulu değilse ya da ağ yoksa hiçbir şey göstermez.</summary>
    /// <summary>Kurulumda sessizce açılan "Windows ile başlat" kaydı, kullanıcıya bir kez söylenir ve karar olarak yazılır (ayar kalıcı).</summary>
    private void AnnounceAutoStartOnce()
    {
        try
        {
            var t = KabukTercihleri.Oku(DataDir);
            if (t.AcilistaBasla != null || !StartWithWindows.IsEnabled()) return;
            t.AcilistaBasla = true;
            t.Yaz(DataDir);
            if (!Opt.Background) { _bildirimBalonu = false; Tray.ShowBalloonTip(8000, Strings.Get("balloon.autostart.title"), Strings.Get("balloon.autostart.text"), ToolTipIcon.Info); }
        }
        catch (Exception ex) { Log.Error("açılış bildirimi", ex); }
    }

    /// <summary>Güncelleme adresini çözer (ortam değişkeni, tercih, varsayılan besleme). Ağa çıkmaz. Kişi kapattıysa denetleyici kurulmaz.</summary>
    private void GuncellemeyiHazirla()
    {
        try
        {
            var (adres, kapali) = GuncellemeDenetimi.AdresBul(DataDir);
            _guncellemeKapali = kapali;
            _guncelleme = adres == null ? null : new GuncellemeDenetimi(adres);
            if (kapali) Log.Write("güncelleme denetimi tercihle kapalı");
        }
        catch (Exception ex) { Log.Error("güncelleme adresi", ex); _guncelleme = null; }
    }

    /// <summary>Açılıştan kısa süre sonra bir kez, sonra 6 saatte bir sessiz denetim. Ağ yoksa günlüğe tek satır yazılır, kişi rahatsız edilmez.</summary>
    private async Task StartUpdateWatchAsync()
    {
        if (_guncelleme == null) return;
        try { await Task.Delay(GuncellemeAdresi.IlkBekleme, _cts.Token).ConfigureAwait(true); } catch (OperationCanceledException) { return; }
        await CheckUpdateAsync(manual: false).ConfigureAwait(true);
        if (_exiting) return;
        _guncellemeZamanlayici = new System.Windows.Forms.Timer { Interval = (int)GuncellemeAdresi.Aralik.TotalMilliseconds };
        _guncellemeZamanlayici.Tick += (_, _) => { if (!_exiting && !_indirmeKilidi.Suruyor) _ = CheckUpdateAsync(manual: false); };
        _guncellemeZamanlayici.Start();
    }

    private async Task CheckUpdateAsync(bool manual)
    {
        if (_guncelleme == null || _updateChecking || _exiting) return;
        _updateChecking = true;
        RefreshMenuState();
        try
        {
            var r = await _guncelleme.DenetleAsync(_cts.Token).ConfigureAwait(true);
            if (_exiting) return;
            switch (r.Durum)
            {
                case GuncellemeDenetimi.Durum.YeniSurum:
                    _yeniSurum = r.YeniSurum;
                    if (_guncellemeBalonu.Gostermeli(r.YeniSurum, manual, Opt.SmokePath != null))
                    {
                        _bildirimBalonu = false;
                        Tray.ShowBalloonTip(8000, Strings.Get("balloon.update.title"), Strings.Get("balloon.update.text", r.YeniSurum ?? ""), ToolTipIcon.Info);
                    }
                    // elle denetleyen sonucu soru olarak görür ("Yeni sürüm var: x. Şimdi kurulsun mu?"), bunu UpdateClickedAsync sorar
                    break;
                case GuncellemeDenetimi.Durum.Guncel: _yeniSurum = null; if (manual) Flash("flash.update.none", SozVersion()); break;
                case GuncellemeDenetimi.Durum.KurulumDegil: if (manual) Flash("flash.update.notinstalled"); break;
                case GuncellemeDenetimi.Durum.Ulasilamadi: Log.Write("güncelleme adresine ulaşılamadı: " + r.Ayrinti); if (manual) Flash("flash.update.unreachable"); break;
                case GuncellemeDenetimi.Durum.AdresGecersiz: Log.Write("güncelleme adresi reddedildi: " + r.Ayrinti); if (manual) Flash("flash.update.unreachable"); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("güncelleme denetimi", ex); }
        finally { _updateChecking = false; if (!_exiting) RefreshMenuState(); }
    }

    private bool _guncellemeSoruluyor;

    /// <summary>
    /// Menüdeki güncelleme satırı. Yeni sürüm bilinmiyorsa önce denetler; varsa sorar: çalışan iş sayısını ve durdurulacağını söyler, kurma her zaman
    /// kişinin onayıyla olur. İndirme sürerken ikinci tıklama yeni indirme başlatmaz. İndirme bitince Söz düzgünce kapanır (işler durdurulur,
    /// kancalar geri çevrilir), güncelleyici kurar ve Söz'ü yeniden açar.
    /// </summary>
    private async Task UpdateClickedAsync()
    {
        if (_exiting || _guncelleme == null || _guncellemeSoruluyor) return;
        if (_indirmeKilidi.Suruyor) { Flash("flash.update.busy"); return; }
        if (_yeniSurum == null)
        {
            await CheckUpdateAsync(manual: true).ConfigureAwait(true);
            if (_yeniSurum == null || _exiting) return;
        }
        _guncellemeSoruluyor = true;
        try
        {
            var surum = _yeniSurum!;
            var calisan = Server == null ? 0 : await CalisanIsSayisiAsync().ConfigureAwait(true);
            if (_exiting) return;
            var cevap = MessageBox.Show(GuncellemeSorusu.Metin(surum, calisan), Strings.Get("dlg.update.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                GuncellemeSorusu.HayirVarsayilan(calisan) ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1);
            if (cevap != DialogResult.Yes || _exiting) return;
            if (!_indirmeKilidi.Basla()) { Flash("flash.update.busy"); return; }
        }
        finally { _guncellemeSoruluyor = false; }

        RefreshMenuState();   // "Güncelleme indiriliyor..."
        var indi = false;
        try { indi = await _guncelleme.IndirAsync(_cts.Token).ConfigureAwait(true); }
        catch (Exception ex) { Log.Error("güncelleme indirme", ex); }
        if (_exiting) return;
        if (!indi || !_guncelleme.UygulamayiBaslat())
        {
            _indirmeKilidi.Bitir();
            RefreshMenuState();
            Flash("flash.update.failed");
            return;
        }
        Log.Write("güncelleme indirildi, uygulamak için Söz kapanıyor");
        ExitApp();
    }

    /// <summary>Çalışan iş sayısı (en çok 3 sn beklenir). Öğrenilemezse null.</summary>
    private async Task<int?> CalisanIsSayisiAsync()
    {
        if (Server == null) return null;
        try
        {
            using var cts = new CancellationTokenSource(3000);
            return await Api.GetRunningJobCountAsync(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException) { Log.Write("çalışan iş sayısı okunamadı: " + ex.GetType().Name); return null; }
    }

    /// <summary>Tepsi: "1 saat gizle" yapılmış küçük pencereyi geri getirir.</summary>
    private void KucukPencereyiGoster()
    {
        if (_exiting) return;
        KisiEylemi("küçük pencereyi göster", () =>
        {
            if (Kapsul is { Calisiyor: true } k) k.Goster();
            _kapsulGizli = false;
            RefreshMenuState();
        }, "msg.action.fail");
    }

    private static void ApplyProcessColorMode(string? modeSetting)
    {
#pragma warning disable WFO5001
        try
        {
            var mode = Palette.NormalizeModeSetting(modeSetting) switch
            {
                "light" => SystemColorMode.Classic,
                "dark" => SystemColorMode.Dark,
                _ => SystemColorMode.System,
            };
            Application.SetColorMode(mode);
        }
        catch { }
#pragma warning restore WFO5001
    }

    private void ApplyMenuTheme(Palette pal)
    {
        var bg = pal.EffectivePanelBg;
        var fg = pal.Text;
        _menu.Renderer = new ToolStripProfessionalRenderer(new TrayMenuColorTable(pal));
        _menu.BackColor = bg;
        _menu.ForeColor = fg;
        foreach (ToolStripItem item in _menu.Items)
        {
            item.BackColor = bg;
            item.ForeColor = item.Enabled ? fg : pal.SubText;
        }
        if (_menu.IsHandleCreated) Native.SetImmersiveDarkMode(_menu.Handle, !pal.Light);
    }

    /// <summary>Arayüzden ya da sistemden gelen tema değişimini uygulamayı yeniden başlatmadan tüm yüzeylere uygular.</summary>
    internal void ApplyTheme(string? family, string? mode)
    {
        if (_exiting) return;
        if (!string.IsNullOrWhiteSpace(family))
        {
            Settings["themeFamily"] = Palette.NormalizeFamily(family);
            SaveLocalState();
        }
        if (!string.IsNullOrWhiteSpace(mode))
        {
            Settings["theme"] = Palette.NormalizeModeSetting(mode);
        }

        var activeMode = Settings.GetValueOrDefault("theme");
        var activeFamily = ResolveThemeFamilyFromSettings();
        Pal = Palette.For(activeMode, activeFamily);
        ApplyProcessColorMode(activeMode);
        ApplyMenuTheme(Pal);

        try
        {
            var yeniIkon = TrayIconFactory.Create(Math.Max(16, SystemInformation.SmallIconSize.Width), Pal);
            var eskiIkon = _trayIcon;
            _trayIcon = yeniIkon;
            Tray.Icon = yeniIkon;
            eskiIkon?.Dispose();
        }
        catch (Exception ex) { Log.Error("tepsi simgesi yenileme", ex); }

        if (Panel is { IsDisposed: false } p) p.ApplyPalette(Pal);
        Capsule?.SetPalette(Pal);
        Kapsul?.Tema(Pal.Family, KapsulPolitikasi.ZeminKipi(activeMode));
    }

    private sealed class TrayMenuColorTable(Palette pal) : ProfessionalColorTable
    {
        private readonly Color _bg = pal.EffectivePanelBg;
        private readonly Color _border = Color.FromArgb(255, pal.Border.A < 255
            ? (pal.Light ? (byte)214 : (byte)58) : pal.Border.R,
            pal.Border.A < 255 ? (pal.Light ? (byte)218 : (byte)62) : pal.Border.G,
            pal.Border.A < 255 ? (pal.Light ? (byte)228 : (byte)74) : pal.Border.B);
        private readonly Color _sel = pal.Light
            ? Color.FromArgb(255, Math.Max(0, pal.EffectivePanelBg.R - 16), Math.Max(0, pal.EffectivePanelBg.G - 14), Math.Max(0, pal.EffectivePanelBg.B - 10))
            : Color.FromArgb(255, Math.Min(255, pal.EffectivePanelBg.R + 22), Math.Min(255, pal.EffectivePanelBg.G + 24), Math.Min(255, pal.EffectivePanelBg.B + 30));

        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _sel;
        public override Color MenuItemSelected => _sel;
        public override Color MenuItemSelectedGradientBegin => _sel;
        public override Color MenuItemSelectedGradientEnd => _sel;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _bg;
    }

    private void RegisterHotkey()
    {
        var spec = Settings.GetValueOrDefault("hotkeyPacket");
        if (string.IsNullOrWhiteSpace(spec)) spec = "Ctrl+Alt+K";
        PacketRequested = spec;
        PacketMeasure = HotkeyPolicy.Measure(spec, LayoutProbe);
        if (PacketMeasure.ConflictReason is { } warn) Log.Write($"UYARI: paket kısayolu {PacketMeasure.Display}: {warn}");
        var ok = Hotkey.Register(spec);
        if (ok) { _hotkeyWarn.Visible = false; return; }
        var shown = string.IsNullOrEmpty(Hotkey.Display) ? spec : Hotkey.Display;
        Log.Write($"kısayol alınamadı: {shown}: {Hotkey.FailureReason}");
        _hotkeyWarn.Text = Strings.Get("tray.hotkeyfail") + " (" + shown + ")";
        _hotkeyWarn.Visible = true;
        if (!_balloonShown && Opt.SmokePath == null)
        {
            _balloonShown = true;
            _bildirimBalonu = false; Tray.ShowBalloonTip(6000, Strings.Get("balloon.hotkey.title"), Strings.Get("balloon.hotkey.text", shown), ToolTipIcon.Warning);
        }
    }

    /// <summary>"Yeni iş" kısayolu: AltGr çakışması ya da kayıt hatasında Ctrl+Shift+Space'e düşer.</summary>
    private void RegisterNewJobHotkey()
    {
        var requested = Settings.GetValueOrDefault("hotkeyNewJob");
        var res = HotkeyPolicy.Resolve(requested, HotkeyPolicy.NewJobDefault, LayoutProbe,
            s => NewJobHotkey.Register(s) ? null : NewJobHotkey.FailureReason ?? "bilinmeyen hata");
        NewJobInfo = res;
        if (res.FellBack) Log.Write($"yeni iş kısayolu {res.Requested} yerine {res.Display}: {res.Reason}" + (res.Registered ? "" : $" (yedek de kaydedilemedi: {res.FailureReason})"));
        else if (!res.Registered) Log.Write($"yeni iş kısayolu alınamadı: {res.Display}: {res.FailureReason}");
        if (res.Registered && !res.FellBack) { _newJobWarn.Visible = false; return; }
        _newJobWarn.Text = res.Registered
            ? Strings.Get("tray.newjob.fallback", res.Requested, res.Display)
            : Strings.Get("tray.newjob.fail") + " (" + res.Display + ")";
        _newJobWarn.Visible = true;
    }

    // ------------------------------------------------------------------ durum
    internal CapsuleModel CurrentModel()
    {
        var name = Resolver.Last?.Name ?? "";
        var c = _counts;
        return new CapsuleModel(name, c?.Waiting ?? 0, c?.Stalled ?? 0, _hasData, FailedJobs: c?.FailedJobs ?? 0);
    }

    internal async Task RefreshAsync()
    {
        if (_exiting) return;
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            do
            {
                _refreshAgain = false;
                // Tepsi ipucu ve eski kapsül, yeni küçük pencerenin okuduğu kaynaktan (api/attention) sayar: sayılar her yerde aynı çıksın.
                // Eski sunucu bu uca sahip değilse önceki yollara (api/now, api/summary) düşülür.
                var ogeler = await Api.GetAttentionAsync().ConfigureAwait(true);
                if (ogeler != null)
                {
                    var (bekleyen, belirsiz) = DikkatSayaci.Say(ogeler);
                    _counts = (bekleyen, belirsiz, 0);
                }
                else
                {
                    var now = await Api.GetNowAsync().ConfigureAwait(true);
                    if (now != null) _counts = (now.Waiting, now.Stalled, now.JobsFailed);
                    else
                    {
                        var summary = await Api.GetSummaryAsync().ConfigureAwait(true);
                        _counts = summary == null ? null : (summary.Waiting, summary.Stalled, 0);
                    }
                }
                _hasData = _counts != null;
                if (_hasData)
                {
                    _projects = await Api.GetProjectsAsync().ConfigureAwait(true);
                    _sessions = await Api.GetSessionsAsync(24).ConfigureAwait(true);
                }
                _lastRefresh = DateTime.UtcNow;
                UpdateActive(true);
                if (_hasData) await NotifyAttentionAsync(ogeler).ConfigureAwait(true);
            } while (_refreshAgain && !_exiting);
        }
        catch (Exception ex) { Log.Error("yenileme", ex); }
        finally { _refreshing = false; SayacEkle("yenileme", sw0.ElapsedMilliseconds); }
    }

    /// <summary>Öndeki pencereye göre etkin projeyi yeniler ve kapsülü günceller.</summary>
    private void UpdateActive(bool force)
    {
        if (_exiting) return;
        var swA = System.Diagnostics.Stopwatch.StartNew();
        try { UpdateActiveCore(force); } finally { SayacEkle(force ? "etkin_proje_zorla" : "etkin_proje_zamanlayici", swA.ElapsedMilliseconds); }
    }

    private void UpdateActiveCore(bool force)
    {
        var before = Resolver.Last;
        Resolver.Resolve(_projects, _sessions, DateTimeOffset.UtcNow);
        var model = CurrentModel();
        if (force || before?.Id != Resolver.Last?.Id || Capsule?.Model.Waiting != model.Waiting || Capsule?.Model.Stalled != model.Stalled
            || Capsule?.Model.FailedJobs != model.FailedJobs || Capsule?.Model.HasData != model.HasData)
            Capsule?.SetModel(model);
        var proje = Resolver.Last?.Name ?? "";
        if (proje != _sonProje) { _sonProje = proje; Kapsul?.Proje(proje); }
        // Sunucu yoksa ya da ulaşılamıyorsa eski sayı canlıymış gibi gösterilmez ve "hiç iş yok" gibi de okunmaz.
        var tip = _hasData ? model.Tooltip() : Strings.Get(Server == null && !Opt.FakeApi ? "tip.offline" : (_lastRefresh == DateTime.MinValue ? "tip.nodata" : "tip.unreachable"));
        var text = Strings.Get("app") + " · " + tip;
        Tray.Text = text.Length > 120 ? text[..120] : text;
    }

    internal ActiveProject? ResolveActiveNow()
    {
        Resolver.Resolve(_projects, _sessions, DateTimeOffset.UtcNow);
        return Resolver.Last;
    }

    // ------------------------------------------------------------------ eylemler
    private Rectangle PanelAnchor()
    {
        if (Kapsul is { SonKonum: { } kr }) return kr;
        if (Capsule is { IsShown: true } c) return c.BodyScreenRect;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Rectangle(wa.Right - 40, wa.Bottom - 40, 24, 24);
    }

    /// <summary>Panel penceresini döner. Herhangi bir nedenle dispose olmuşsa otomatik yeni PanelForm oluşturur (ObjectDisposedException atılmaz).</summary>
    internal PanelForm EnsurePanel()
    {
        if (Panel is { IsDisposed: true }) Panel = null;
        if (Panel == null)
        {
            var url = string.IsNullOrEmpty(BaseUrl) ? "about:blank" : BaseUrl + "/";
            Panel = new PanelForm(url, Path.Combine(DataDir, "webview2"), Pal);
            // Sunucu yoksa boş beyaz sayfa yerine sade bir açıklama ve çalışan "Yeniden dene" düğmesi gösterilir (ham hata ekrana çıkmaz, ayrıntı günlükte).
            if (string.IsNullOrEmpty(BaseUrl) && SemaYeni)
            {
                // Veri daha yeni bir Söz sürümüyle yazılmış: yeniden denemek ya da kapatıp açmak çözmez. Doğru cümle ve güncelleme düğmesi (veriye dokunulmaz).
                Panel.YenidenDeneIstendi += () => _ = UpdateClickedAsync();
                Panel.HataGoster(Strings.Get("panel.schema.title"), Strings.Get("msg.schema.new"), Strings.Get("panel.schema.button"));
            }
            else if (string.IsNullOrEmpty(BaseUrl))
            {
                Panel.YenidenDeneIstendi += () => _ = RetryServerAsync();
                Panel.HataGoster(Strings.Get("panel.error.title"), Strings.Get("panel.error.text"), Strings.Get("panel.error.retry"));
            }
            Panel.DurumDosyasi = Path.Combine(DataDir, PanelDurum.DosyaAdi);
            Panel.ProfilVarsayilanlari = ct => Api.GetProfileDefaultsAsync(ct);
            Panel.MessageReceived += OnPanelMessage;
            Panel.ShownChanged += shown => { if (!shown) Kapsul?.PanelKapandi(); };
        }
        return Panel;
    }

    internal void TogglePanel() { if (!_exiting) KisiEylemi("paneli aç/kapat", () => { _pasteTarget = IntPtr.Zero; EnsurePanel().Toggle(PanelAnchor()); }); }

    /// <summary>Panelin saklı yerini ve boyutunu siler, paneli kapsülün yanında varsayılan boyutla açar.</summary>
    internal void ResetPanelPlace()
    {
        if (_exiting) return;
        KisiEylemi("panel yerini sıfırlama", () =>
        {
            _pasteTarget = IntPtr.Zero;
            var p = EnsurePanel();
            p.YeriSifirla(PanelAnchor());
            if (!p.Visible) p.OpenNear(PanelAnchor());
        });
    }

    /// <summary>Paneli açar. odakAl=false: kullanıcı eylemi olmayan açılış (program açılışı, ölçüm), pencere odak almadan görünür.</summary>
    internal bool OpenPanel(bool odakAl = true)
    {
        if (_exiting) return false;
        return KisiEylemi("panel açma", () =>
        {
            _pasteTarget = IntPtr.Zero;
            var p = EnsurePanel();
            if (!p.Visible) p.OpenNear(PanelAnchor(), odakAl); else p.GorunurkenGetir(odakAl);   // küçültülmüşse geri getirir
        });
    }

    /// <summary>O anki ön pencereyi yapıştırma hedefi olarak hatırlar.</summary>
    internal void RememberPasteTarget()
    {
        var fg = Native.GetForegroundWindow();
        if (TextPaster.IsUsableTarget(fg)) _pasteTarget = fg;
        else if (!Native.IsOwnWindow(fg)) _pasteTarget = IntPtr.Zero;
    }

    internal IntPtr PasteTarget => _pasteTarget;

    /// <summary>"Yeni iş": hedefi hatırla, paneli İşler sayfasında iş kutusu odaklı aç.</summary>
    internal void StartNewJob()
    {
        if (_exiting) return;
        KisiEylemi("yeni iş", StartNewJobCore);
    }

    private void StartNewJobCore()
    {
        RememberPasteTarget();
        var p = EnsurePanel();
        p.RequestNewJob();
        // Dikte sürerken (okunabiliyorsa) panel odak almadan açılır; odak dikte bitince ya da en çok 15 sn sonra verilir. Durum okunamıyorsa bugünkü davranış.
        if (_yeniIsIstegi is null && OdakPolitikasi.YeniIsKarari(_sonDikteDurumu, TimeSpan.Zero) == YeniIsOdak.Ertele)
        {
            _yeniIsIstegi = DateTime.UtcNow;
            _yeniIsZamanlayici.Stop(); _yeniIsZamanlayici.Start();
            if (!p.Visible) p.OpenNear(PanelAnchor(), odakAl: false); else p.GorunurkenGetir(odakAl: false);   // küçültülmüşse odak çalmadan geri getirir
            return;
        }
        if (_yeniIsIstegi is not null) { YeniIsBekleyeniDene(); return; } // zaten bekliyor: ilk istekten sayılan 15 sn sürer
        if (!p.Visible) p.OpenNear(PanelAnchor()); else p.GorunurkenGetir();   // küçültülmüşse geri getirir
        p.FocusWeb();
    }

    /// <summary>Bekleyen "Yeni iş" odağı varsa ve karar artık "hemen" ise odağı verir (dikte bitti, durum okunamaz oldu ya da süre doldu).</summary>
    private void YeniIsBekleyeniDene()
    {
        if (_yeniIsIstegi is not { } t0) return;
        if (!_exiting && OdakPolitikasi.YeniIsKarari(_sonDikteDurumu, DateTime.UtcNow - t0) == YeniIsOdak.Ertele) return;
        YeniIsOdagiVer();
    }

    private void YeniIsOdagiVer()
    {
        _yeniIsIstegi = null;
        _yeniIsZamanlayici.Stop();
        if (_exiting) return;
        try
        {
            if (Panel is { IsDisposed: false } p)
            {
                if (!p.Visible) p.OpenNear(PanelAnchor());
                else p.GorunurkenGetir();   // küçültülmüşse geri getirir
                p.FocusWeb();
            }
        }
        catch (Exception ex) { Log.Error("yeni iş odağı", ex); }
    }

    private void OnPanelMessage(WebMessage m)
    {
        switch (m.Kind)
        {
            case WebMessageKind.Paste: _ = PasteFromPanelAsync(m.Text); break;
            case WebMessageKind.Dictate:
                var r = TriggerDictationAndSync();
                if (Panel is { IsDisposed: false } p) p.PostJson(TextPasteRules.DictateResultJson(r.Started));
                break;
            case WebMessageKind.ThemeChanged:
                ApplyTheme(m.Family, m.Mode);
                break;
            case WebMessageKind.ResetPanelPlace:
                ResetPanelPlace();
                break;
            case WebMessageKind.GetShellPrefs:
            case WebMessageKind.SetShellPref:
                var cevap = Kopru.Isle(m);
                if (Panel is { IsDisposed: false } pp) pp.PostJson(cevap);
                break;
        }
    }

    private async Task PasteFromPanelAsync(string? text)
    {
        TextPasteOutcome outcome;
        try
        {
            if (!TextPasteRules.Acceptable(text)) outcome = TextPasteOutcome.Rejected;
            else
            {
                var target = _pasteTarget;
                if (Panel is { IsDisposed: false } pHide) pHide.Hide();
                outcome = await TextPaster.PasteAsync(text, target).ConfigureAwait(true);
                _pasteTarget = IntPtr.Zero;
            }
        }
        catch (Exception ex) { Log.Error("panelden yapıştırma", ex); outcome = TextPasteOutcome.Failed; }
        if (Panel is { IsDisposed: false } pPost) pPost.PostJson(TextPasteRules.PasteResultJson(outcome));
        Flash(outcome switch
        {
            TextPasteOutcome.Pasted => "flash.text.pasted",
            TextPasteOutcome.Copied => "flash.text.copied",
            TextPasteOutcome.Rejected => "flash.text.rejected",
            _ => "flash.text.failed",
        });
    }

    /// <summary>Kısa sonuç yazısı: küçük pencere varsa onda, yoksa (tepsiden yapılan işlerde) saat yanında kısa balonla. Tepkisiz kalmaz.</summary>
    private void Flash(string key, params object[] args)
    {
        var metin = Strings.Get(key, args);
        if (Kapsul is { Calisiyor: true } k) k.Flash(metin);
        else if (Capsule != null) Capsule.Flash(metin);
        else BalonGoster(metin, ToolTipIcon.Info, 3000);
    }

    /// <summary>Kişinin yaptığı bir işin neden olmadığını söyler (saat yanında kısa balon). Sınama ve başsız kiplerde sessizdir.</summary>
    private void Soyle(string key) => BalonGoster(Strings.Get(key), ToolTipIcon.Warning, 6000);

    private void BalonGoster(string metin, ToolTipIcon simge, int ms)
    {
        if (_exiting || Opt.IsHeadless || Opt.SmokePath != null || Opt.OlcPath != null) return;
        try { _bildirimBalonu = false; Tray.ShowBalloonTip(ms, Strings.Get("app"), metin, simge); } catch (Exception ex) { Log.Error("balon", ex); }
    }

    private async Task ImportAsync()
    {
        var ok = false;
        try { ok = await Api.ImportAsync().ConfigureAwait(true); }
        catch (Exception ex) { Log.Error("kayıtları okuma", ex); }
        Flash(ok ? "flash.imported" : "flash.importfail");
        if (ok) await RefreshAsync().ConfigureAwait(true);
    }

    private async Task CopyPacketAsync()
    {
        var r = PasteOutcome.Failed;
        try { r = await Paster.CopyAsync(ResolveActiveNow()?.Id).ConfigureAwait(true); }
        catch (Exception ex) { Log.Error("kuralları kopyalama", ex); }
        Flash(r switch { PasteOutcome.CopiedOnly => "flash.copied", PasteOutcome.Empty => "flash.empty", _ => "flash.copyfail" });
    }

    private async Task PastePacketAsync()
    {
        var r = PasteOutcome.Failed;
        try { r = await Paster.PasteAsync(ResolveActiveNow()?.Id).ConfigureAwait(true); }
        catch (Exception ex) { Log.Error("kuralları yapıştırma", ex); }
        Flash(r switch
        {
            PasteOutcome.Pasted => "flash.pasted",
            PasteOutcome.CopiedOnly => "flash.copied",
            PasteOutcome.Empty => "flash.empty",
            _ => "flash.pastefail",
        });
    }

    private void DictateFromUi() => _ = TriggerDictationAndSync();

    private DictationResult TriggerDictationAndSync()
    {
        DictationResult r;
        try { r = Dictation.Toggle(); }
        catch (Exception ex) { Log.Error("sesle yazma", ex); Flash("flash.dictation"); return new DictationResult(false, null, "hata", null); }
        if (r.Started)
        {
            _dictationFastUntilUtc = DateTime.UtcNow.AddSeconds(6);
            _lastHostAttachUtc = DateTime.MinValue;
            if (_dictationTimer.Interval != 250) _dictationTimer.Interval = 250;
            PollDictationStatus();
        }
        if (!r.Started || !Dictation.HostProtocolSupported)
            Flash(r.Started ? "flash.dictation.ok" : "flash.dictation");
        return r;
    }

    private void OnUserPreferenceChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        _ui.Post(_ =>
        {
            ApplyTheme(null, null);
            if (Capsule != null) Capsule.ReducedMotion = !Native.ClientAreaAnimationEnabled();
        }, null);
    }

    private void OnDisplaySettingsChanged(object? s, EventArgs e) => _ui.Post(_ => Capsule?.Reclamp(), null);

    // ------------------------------------------------------------------ çıkış
    /// <summary>"Çık" ve oturum kapanışında sunucu tutacağının DisposeAsync ile kapanması için en çok beklenen süre.</summary>
    internal const int SunucuKapatmaBeklemeMs = 10_000;

    /// <summary>
    /// Windows oturumu kapanıyor (oturumu kapat, yeniden başlat, kapat): uygulama çoğu zaman çıkış çağrısı almadan sonlandırılır. Kancaları komut yoluna geri çeviren kısa iş burada,
    /// Windows süreci öldürmeden önce yapılır; sonra normal çıkış çalışır. Yinelenen çağrı zararsızdır (geri çevirme yalnız komut dışı kancalara dokunur).
    /// </summary>
    private void OnSessionEnding(object? s, SessionEndingEventArgs e)
    {
        Log.Write("Windows oturumu kapanıyor: sunucu kapatılıyor");
        try { Server?.RevertHooksForShutdown(); } catch (Exception ex) { Log.Error("oturum kapanışında kanca geri çevirme", ex); }
        if (!_exiting) _ui.Post(_ => ExitApp(), null);
    }

    private bool _cikisSoruluyor;

    /// <summary>
    /// Kişi "Söz'ü kapat" dedi (menü ya da küçük pencere). Çalışan iş yoksa sormadan kapanır; varsa sade bir soru sorar (işler durdurulur, sonra "Aynı işe yeni bir şey ekle" ile sürdürülür).
    /// "Hayır" denirse hiçbir şey kapanmaz (küçük pencere de yerinde kalır). Soru açıkken ikinci istek yok sayılır.
    /// </summary>
    internal async Task CikisIsteginiIsleAsync()
    {
        if (_exiting || _cikisSoruluyor) return;
        _cikisSoruluyor = true;
        try
        {
            var calisan = await CalisanIsSayisiAsync().ConfigureAwait(true);
            var karar = CikisKurali.Ver(Server != null, calisan);
            if (karar != CikisKurali.Karar.Kapat)
            {
                var sonuc = MessageBox.Show(CikisKurali.Soru(karar, calisan ?? 0), Strings.Get("dlg.quit.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (sonuc != DialogResult.Yes) { Log.Write("kapatma kişi tarafından vazgeçildi"); return; }
            }
            ExitApp();
        }
        catch (Exception ex) { Log.Error("kapatma isteği", ex); }
        finally { _cikisSoruluyor = false; }
    }

    internal void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try { _cts.Cancel(); } catch { }
        _pollTimer.Stop(); _activeTimer.Stop(); _dictationTimer.Stop(); _yeniIsZamanlayici.Stop(); _guncellemeZamanlayici?.Stop();
        if (Dictation is { IsHostAttached: true })
        {
            try { _ = Dictation.DetachHost(200); } catch { }
        }
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        try { Hotkey?.Dispose(); } catch { }
        try { NewJobHotkey?.Dispose(); } catch { }
        try { Tray.Visible = false; Tray.Dispose(); } catch { }
        try { Capsule?.Dispose(); } catch { }
        try { Kapsul?.Dispose(); } catch { }
        if (Panel != null) { Panel.AllowClose = true; try { Panel.Dispose(); } catch { } }
        try { _trayIcon?.Dispose(); } catch { }
        try { (Api as IDisposable)?.Dispose(); } catch { }
        if (Server != null)
        {
            // DisposeAsync önce kancaları komuta geri çevirir (hızlı kip), sonra işleri ve sunucuyu kapatır. Geri çevirme bitmeden süreç ölmesin diye bekleme payı geniş tutulur.
            try { if (!Task.Run(async () => await Server.DisposeAsync().ConfigureAwait(false)).Wait(SunucuKapatmaBeklemeMs)) Log.Write("sunucu kapatma süre doldu (" + SunucuKapatmaBeklemeMs + " ms)"); }
            catch (Exception ex) { Log.Error("sunucu kapatma", ex); }
        }
        ExitThread();
    }
}
