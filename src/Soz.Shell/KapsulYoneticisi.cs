using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Soz.Capsule;

namespace Soz.Shell;

/// <summary>
/// Yeni kapsülü (ayrı küçük program <c>Soz-Kapsul.exe</c>) alt süreç olarak başlatır, kapanışta kapatır, çökerse bir kez yeniden başlatır.
/// Haberleşme: kapsülün standart çıktısından komut, standart girdisine olay (satır başına JSON, bkz. <see cref="KabukProtokol"/>).
/// Kapsül verisini (Sıra bende listesi) sunucudan kendisi okur, buradan geçmez.
/// </summary>
internal sealed class KapsulYoneticisi : IDisposable
{
    public const string ExeAdi = KapsulPolitikasi.ExeAdi;

    private readonly string _exe, _veriKlasoru, _durumKlasoru, _ekArgumanlar;
    private string _mod;
    private string? _tema;
    private readonly string _kisayol;
    private readonly SynchronizationContext _ui;
    private readonly Action<KapsulKomutu> _komut;
    private readonly Action<string?> _eskiyeDus;
    private readonly object _kilit = new();
    private Process? _surec;
    private StreamWriter? _girdi;
    private volatile bool _durdu;

    public KapsulPolitikasi Politika { get; } = new();
    /// <summary>Kapsülün son bildirdiği ekran konumu (panel onun yanında açılır). Her komut ve yerine oturma bildirimi (<see cref="KomutTuru.Konum"/>) günceller.</summary>
    public System.Drawing.Rectangle? SonKonum { get; private set; }
    public int? Pid { get { try { return _surec is { HasExited: false } ? _surec.Id : null; } catch { return null; } } }
    public bool Calisiyor => !_durdu && Pid != null;
    /// <summary>Alt süreç (ölçüm kipi bellek okusun diye).</summary>
    internal Process? Surec => _surec;

    /// <param name="mod">Kapsülün zemin kipi: sistem, koyu ya da acik (kabuğun tema ayarından).</param>
    /// <param name="ekArgumanlar">Yalnız deneme için: kapsüle eklenecek ham argümanlar (ör. kısa ömürlü çalıştırma).</param>
    public KapsulYoneticisi(string exe, string veriKlasoru, string mod, string? ekArgumanlar, SynchronizationContext ui, Action<KapsulKomutu> komut, Action<string?> eskiyeDus, string? tema = null, string? kisayol = null)
    {
        _kisayol = string.IsNullOrWhiteSpace(kisayol) ? "yok" : kisayol.Trim();   // "yok": kısayol alınamadı (kapsülde Erisim.KisayolYok ile aynı)
        _exe = exe; _veriKlasoru = veriKlasoru; _mod = mod; _tema = string.IsNullOrWhiteSpace(tema) ? null : tema; _ekArgumanlar = ekArgumanlar ?? "";
        _durumKlasoru = Path.Combine(veriKlasoru, "kapsul");
        _ui = ui; _komut = komut; _eskiyeDus = eskiyeDus;
    }

    /// <summary>Süreci başlatır. Dönüş false: başlamadı (eski kapsül açılmalı).</summary>
    public bool Baslat()
    {
        try
        {
            Directory.CreateDirectory(_durumKlasoru);
            var psi = new ProcessStartInfo(_exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(_exe) ?? "",
            };
            foreach (var a in new[] { "--kabuk", "--data", _veriKlasoru, "--durum", _durumKlasoru, "--mod", _mod }) psi.ArgumentList.Add(a);
            if (!string.IsNullOrWhiteSpace(_tema)) { psi.ArgumentList.Add("--tema"); psi.ArgumentList.Add(_tema); }
            psi.ArgumentList.Add("--kisayol"); psi.ArgumentList.Add(_kisayol);   // kapsül ekran okuyucu yardımında gerçek kısayolu söyler
            foreach (var a in _ekArgumanlar.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p == null) { Log.Write("yeni kapsül başlatılamadı (süreç açılmadı)"); return false; }
            lock (_kilit) { _surec = p; _girdi = p.StandardInput; _girdi.AutoFlush = true; }
            Log.Write($"yeni kapsül başlatıldı (pid {p.Id})");
            new Thread(() => Dinle(p)) { IsBackground = true, Name = "kapsul-cikti" }.Start();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Write("yeni kapsül başlatılamadı: " + ex.Message);
            return false;
        }
    }

    private void Dinle(Process p)
    {
        try
        {
            string? satir;
            while ((satir = p.StandardOutput.ReadLine()) != null)
            {
                var k = KabukProtokol.KomutOku(satir);
                if (k is { } komut) _ui.Post(_ => { if (!_durdu) Isle(komut); }, null);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        var kod = -1;
        try { p.WaitForExit(3000); kod = p.HasExited ? p.ExitCode : -1; } catch { }
        _ui.Post(_ => Bitti(p, kod), null);
    }

    private void Isle(KapsulKomutu k)
    {
        if (k.Dik is { } d) SonKonum = System.Drawing.Rectangle.FromLTRB((int)d.L, (int)d.T, (int)d.R, (int)d.B);
        _komut(k);
    }

    private void Bitti(Process p, int kod)
    {
        lock (_kilit) { if (!ReferenceEquals(_surec, p)) return; _surec = null; _girdi = null; }
        if (_durdu) return;
        var karar = Politika.CikistaKarar();
        Log.Write(KapsulPolitikasi.Cumle(karar, kod));
        if (karar == KapsulKarari.Yeniden) { if (!Baslat()) _eskiyeDus("yeni kapsül yeniden başlamadı"); }
        else if (karar == KapsulKarari.EskiyeDus) _eskiyeDus(null);
    }

    // ------------------------------------------------------------ kapsüle olay
    private void Yolla(KabukOlayi o)
    {
        try { lock (_kilit) _girdi?.WriteLine(KabukProtokol.OlayYaz(o)); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Flash(string metin) => Yolla(new KabukOlayi(OlayTuru.Flash, metin));
    public void PanelKapandi() => Yolla(new KabukOlayi(OlayTuru.PanelKapandi));
    public void Proje(string ad) => Yolla(new KabukOlayi(OlayTuru.Proje, ad));
    public void Tema(string aile, string kip)
    {
        _tema = aile;
        _mod = kip;
        Yolla(KabukProtokol.TemaOlayi(aile, kip));
    }
    public void DikteDurum(string faz, string? metin = null) => Yolla(KabukProtokol.DikteOlayi(faz, metin));
    /// <summary>Tepsiden "Küçük pencereyi göster": kişi gizlediyse küçük pencere geri gelir.</summary>
    public void Goster() => Yolla(new KabukOlayi(OlayTuru.Goster));

    /// <summary>Genel kısayola basıldı: yazı satırı açılsın. Kısayolu tutan bu program olduğu için ön plan hakkı kapsüle devredilir (yoksa satır odak alamaz).</summary>
    public void Kisayol()
    {
        if (Pid is { } pid) { try { AllowSetForegroundWindow((uint)pid); } catch { } }
        Yolla(new KabukOlayi(OlayTuru.Kisayol));
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);

    /// <summary>Kapanış: kapsüle söyler, kısa bekler, kapanmadıysa kendi başlattığımız süreci sonlandırır.</summary>
    public void Dispose()
    {
        if (_durdu) return;
        _durdu = true;
        Process? p; lock (_kilit) { p = _surec; }
        if (p == null) return;
        Politika.CikisIstendi();   // kapanış kapsülün çökmesi sayılmaz (kişi kapatmayı onayladıktan sonra buraya gelinir)
        Yolla(new KabukOlayi(OlayTuru.Kapat, "masaüstü programı kapanıyor"));
        try { if (!p.WaitForExit(1500)) p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { _girdi?.Dispose(); } catch { }
        try { p.Dispose(); } catch { }
    }
}
