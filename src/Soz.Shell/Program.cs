using System.Diagnostics;
using System.Windows.Forms;
using Soz.Server;
using Velopack;

namespace Soz.Shell;

internal static class Program
{
    private const string MutexName = @"Local\Soz.SingleInstance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack kurulum/güncelleme/kaldırma kancaları en başta işlenir (kanca çağrısında uygulama burada güvenle sonlanır).
        if (!args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)
                        || a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase)
                        || a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)
                        || a.Equals("--olc-json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                VelopackApp.Build()
                    .OnAfterInstallFastCallback(_ => StartWithWindows.EnableOnFreshInstall(Environment.ProcessPath, TercihKlasorleri()))
                    .OnBeforeUpdateFastCallback(_ => UninstallCleanup.StopRunningInstance())
                    .OnBeforeUninstallFastCallback(_ => UninstallCleanup.RunDefault())
                    .Run();
            }
            catch { /* taşınabilir klasörden çalıştırmada Velopack kancası yoktur */ }
        }

        var opt = Options.Parse(args);
        if (opt.Error != null)
        {
            TryConsole("Soz: " + opt.Error);
            return 2;
        }

        // Başsız kipler: pencere ve mutex gerekmez.
        if (opt.PencereAcYolu != null)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            var s = PencereOnuneGetirici.Getir(opt.PencereHwnd, opt.PencerePid, opt.PencereAcYolu, opt.PencereBaslik);
            if (opt.PencereAcYolu == "tani") { foreach (var w in Soz.Core.Profile.Win32ProcessInspector.SnapshotAllVisibleWindows()) if (PencereOnuneGetirici.SinifAdi(new IntPtr(w.Hwnd)) == "CASCADIA_HOSTING_WINDOW_CLASS" && UiaSekme.Sekmeler(new IntPtr(w.Hwnd)).Any(x => x.Ad.StartsWith("c6"))) TryConsole(UiaSekme.Tani(new IntPtr(w.Hwnd))); return 0; }
            if (UiaSekme.SonHata != null) TryConsole("[uia] " + UiaSekme.SonHata);
            TryConsole(System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return s.Basarili ? 0 : 1;
        }
        if (opt.SelfTest) return SelfTest.Run(opt);
        if (opt.SnapshotPath != null && !opt.SnapshotPanel)
        {
            foreach (var p in Snapshot.Run(opt.SnapshotPath)) TryConsole(p);
            return 0;
        }

        var dataDir = opt.ResolveDataDir();
        if (opt.RequestExit)
        {
            var exitPipe = SingleInstanceChannel.BuildPipeName(opt, dataDir);
            return SingleInstanceChannel.TrySignalExit(exitPipe, 1500) ? 0 : 2;
        }
        // Kaldırıp yeniden kurulduysa: kaldırmada kenara alınan veri geri gelir (Log.Init klasörü yaratmadan önce).
        if (opt.DataDir == null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOZ_DATA")) && KurulumKoku() is { } kok
            && string.Equals(Path.GetFullPath(dataDir).TrimEnd('\\'), Path.Combine(kok, "veri"), StringComparison.OrdinalIgnoreCase))
            VeriKorumasi.GeriYukle(dataDir, kok);
        Log.Init(dataDir);

        // PerMonitorV2 csproj'daki ApplicationHighDpiMode ile gelir, yine de açıkça iste.
        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // deneysel karanlık kip: menüler Windows ya da uygulama temasını izler
        try { Application.SetColorMode(SystemColorMode.System); } catch { /* eski çalışma zamanı */ }
#pragma warning restore WFO5001
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("yakalanmamış (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("[ÇÖKÜŞ] " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("gözlenmeyen görev hatası", e.Exception); e.SetObserved(); };

        if (opt.SnapshotPath != null) return RunPanelSnapshot(opt, dataDir);

        var name = opt.TekOrnekAdi(opt.SmokePath != null ? MutexName + ".smoke" : MutexName, dataDir);
        var pipeName = SingleInstanceChannel.BuildPipeName(opt, dataDir);
        using var mutex = new Mutex(true, name, out var createdNew);
        if (!createdNew)
        {
            if (opt.SmokePath == null && opt.OlcPath == null)
            {
                var cevapVerdi = SingleInstanceChannel.TrySignalShowPanel(pipeName);
                // İlk kopya yanıt vermediyse kişi çift tıklayıp hiçbir şey olmadığını görürdü: sade bir bilgi gösterilir. Windows açılışındaki sessiz başlatmada rahatsız edilmez.
                if (!cevapVerdi && !opt.Background)
                {
                    Log.Write("ikinci kopya: ilk kopya 800 ms içinde yanıt vermedi, kişiye bilgi gösterildi");
                    try { MessageBox.Show(Strings.Get("dlg.second.text"), Strings.Get("app"), MessageBoxButtons.OK, MessageBoxIcon.Information); } catch (Exception ex) { Log.Error("ikinci kopya bilgisi", ex); }
                }
            }
            return 0;
        }

        ShellContext? ctx = null;
        SingleInstanceChannel? activation = null;
        try
        {
            ctx = new ShellContext(opt, dataDir);
            if (opt.SmokePath == null && opt.OlcPath == null)
                activation = SingleInstanceChannel.StartServer(pipeName, () => ctx?.ActivateFromSecondInstance(), () => ctx?.RequestExitFromChannel());

            var started = false;
            Application.Idle += async (_, _) =>
            {
                if (started) return;
                started = true;
                try
                {
                    await ctx.InitAsync(deferCapsule: opt.SmokePath != null).ConfigureAwait(true);
                    if (opt.SmokePath != null) await Smoke.RunAsync(ctx).ConfigureAwait(true);
                    else if (opt.OlcPath != null) await Olcum.RunAsync(ctx).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log.Error("açılış", ex);
                    if (opt.SmokePath == null)
                        MessageBox.Show(Strings.Get("start.failed", Strings.Get("start.failed.reason"), Path.Combine(dataDir, "soz-shell.log")), Strings.Get("app"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ctx.ExitApp();
                }
            };
            Application.Run(ctx);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("çalıştırma", ex);
            try { MessageBox.Show(Strings.Get("start.failed", Strings.Get("start.failed.reason"), Path.Combine(dataDir, "soz-shell.log")), Strings.Get("app"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch (Exception ex2) { Log.Error("açılış hata kutusu", ex2); }
            return 1;
        }
        finally
        {
            try { activation?.Dispose(); } catch { }
        }
    }

    private static void TryConsole(string line) { try { Console.Out.WriteLine(line); } catch { } }

    /// <summary>Velopack kurulumunun kök klasörü (içinde Update.exe olan klasör, <c>current</c> klasörünün üstü); kurulu değilse null.</summary>
    internal static string? KurulumKoku()
    {
        try
        {
            var ust = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)))?.FullName;
            return ust != null && File.Exists(Path.Combine(ust, "Update.exe")) ? ust : null;
        }
        catch { return null; }
    }

    /// <summary>Kurulum kancası için: kullanıcının önceki "açılışta başlama" kararının okunacağı klasörler (kurulumdaki veri ve kaldırmada saklanan veri).</summary>
    private static IEnumerable<string> TercihKlasorleri()
    {
        var kok = KurulumKoku();
        if (kok == null) return [];
        return [Path.Combine(kok, "veri"), VeriKorumasi.KorunanKlasor(kok)];
    }

    /// <summary>--snapshot + --snapshot-panel: kapsül PNG'leri + paneli ekrana göstermeden WebView2 önizleme yakalamasıyla PNG'ye alır.</summary>
    private static int RunPanelSnapshot(Options opt, string dataDir)
    {
        foreach (var p in Snapshot.Run(opt.SnapshotPath!)) TryConsole(p);
        var dir = Path.GetDirectoryName(Path.GetFullPath(opt.SnapshotPath!)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(opt.SnapshotPath!);
        var outPng = Path.Combine(dir, stem + "-panel.png");
        var ctx = new PanelSnapshotContext(opt, dataDir, outPng);
        Application.Run(ctx);
        TryConsole(ctx.Message);
        return ctx.Failed ? 1 : 0;
    }

    private sealed class PanelSnapshotContext : ApplicationContext
    {
        public string Message = "";
        public bool Failed;
        private SozServerHandle? _server;

        public PanelSnapshotContext(Options opt, string dataDir, string outPng)
        {
            var started = false;
            Application.Idle += async (_, _) =>
            {
                if (started) return;
                started = true;
                PanelForm? panel = null;
                try
                {
                    _server = await SozServer.StartAsync(dataDir, opt.Port).ConfigureAwait(true);
                    panel = new PanelForm(_server.Url + "/", Path.Combine(dataDir, "webview2"), Palette.For("system"));
                    var err = await panel.CapturePngAsync(outPng, PanelYerlesim.VarsayilanDp).ConfigureAwait(true);
                    Failed = err != null;
                    Message = err == null ? outPng : "panel PNG alınamadı: " + err;
                }
                catch (Exception ex) { Failed = true; Message = "panel PNG alınamadı: " + ex.Message; }
                finally
                {
                    if (panel != null) { panel.AllowClose = true; try { panel.Dispose(); } catch { } }
                    try { if (_server != null) await _server.DisposeAsync().ConfigureAwait(true); } catch { }
                    ExitThread();
                }
            };
        }
    }
}
