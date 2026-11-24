using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Soz.Core.Profile;

namespace Soz.Shell;

/// <summary>
/// "Pencereyi Aç": çalışan yardımcının penceresini bulup öne getirir.
/// - Windows Terminal: bütün sekmeler tek pencere tutamacını paylaşır. Sekmeler UI Automation ile okunur, proje adını taşıyan TEK sekme varsa o seçilir
///   (başka Terminal pencerelerine de bakılır). Birden çok ya da hiç eşleşme varsa pencere öne getirilir ve kullanıcıya hangi sekmeyi aramalı olduğu söylenir.
///   (<c>wt.exe -w &lt;pencere&gt; focus-tab -t &lt;sıra&gt;</c> komutu pencere numarasını ve sekme sırasını bilmeyi gerektirir, ikisi de dışarıdan okunamaz; bu yüzden kullanılmadı.)
/// - VS Code, Antigravity, Cursor: aynı süreç birden çok pencere taşır; başlığında klasör adı geçen pencere seçilir.
/// - Öteki programlar: pencere öne getirilir.
/// Dönen metinler kod bilmeyene yöneliktir.
/// </summary>
internal static class PencereOnuneGetirici
{
    internal sealed record Sonuc(bool Basarili, string Tur, string Mesaj, string? Ipucu, long? Pencere, IReadOnlyList<string> Sekmeler);

    private const string TerminalSinifi = "CASCADIA_HOSTING_WINDOW_CLASS";
    private static readonly string[] EditorSurecleri = ["Code", "Code - Insiders", "Antigravity", "Cursor", "Windsurf", "VSCodium"];

    // ---------------------------------------------------------------- saf eşleştirme (denenebilir)
    internal static string? KlasorAdi(string? yol)
    {
        if (string.IsNullOrWhiteSpace(yol)) return null;
        var t = yol.Trim().TrimEnd('\\', '/');
        var ad = t.Contains('\\') || t.Contains('/') ? t[(Math.Max(t.LastIndexOf('\\'), t.LastIndexOf('/')) + 1)..] : t;
        return string.IsNullOrWhiteSpace(ad) ? null : ad;
    }

    private static string Norm(string s) => s.ToLower(new CultureInfo("tr-TR")).Replace('—', '-').Replace('–', '-');

    /// <summary>Küçük harfe çevirir, baştaki ve sondaki simgeleri (dönen nokta, yıldız vb.) ve fazla boşlukları atar.</summary>
    internal static string Sade(string s)
    {
        var n = Norm(s).Trim();
        var bas = 0; while (bas < n.Length && !char.IsLetterOrDigit(n[bas])) bas++;
        var son = n.Length; while (son > bas && !char.IsLetterOrDigit(n[son - 1])) son--;
        return string.Join(' ', n[bas..son].Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Başlık ya da sekme adı proje adını (ayrı bir sözcük olarak) taşıyor mu? Üç harften kısa adlar eşleşmez (yanlış eşleşme olmasın).</summary>
    internal static bool Tasiyor(string baslik, string? projeAdi, string? projeYolu, string? oturumBasligi = null)
    {
        if (string.IsNullOrWhiteSpace(baslik)) return false;
        var b = Norm(baslik);
        // Claude Code sekme başlığına proje adını değil oturumun konusunu yazar: oturum başlığı da bir ipucudur (en az 6 harf, yanlış eşleşme olmasın).
        if (!string.IsNullOrWhiteSpace(oturumBasligi))
        {
            var o = Sade(oturumBasligi); var t = Sade(baslik);
            if (o.Length >= 6 && (t.Contains(o) || (t.Length >= 6 && o.Contains(t)))) return true;
        }
        if (!string.IsNullOrWhiteSpace(projeYolu) && b.Contains(Norm(projeYolu.Trim().TrimEnd('\\', '/')))) return true;
        if (string.IsNullOrWhiteSpace(projeAdi) || projeAdi.Trim().Length < 3) return false;
        var ad = Norm(projeAdi.Trim());
        var i = 0;
        while ((i = b.IndexOf(ad, i, StringComparison.Ordinal)) >= 0)
        {
            var once = i == 0 || !char.IsLetterOrDigit(b[i - 1]);
            var sonra = i + ad.Length >= b.Length || !char.IsLetterOrDigit(b[i + ad.Length]);
            if (once && sonra) return true;
            i += ad.Length;
        }
        return false;
    }

    /// <summary>Sekme adlarından proje adını taşıyan TEK sekmenin sırası; sıfır ya da birden çok eşleşmede null.</summary>
    internal static int? TekSekme(IReadOnlyList<string> adlar, string? projeAdi, string? projeYolu, string? oturumBasligi = null)
    {
        int? bulunan = null;
        for (var i = 0; i < adlar.Count; i++)
        {
            if (!Tasiyor(adlar[i], projeAdi, projeYolu, oturumBasligi)) continue;
            if (bulunan != null) return null;
            bulunan = i;
        }
        return bulunan;
    }

    internal static string Ipucu(string? projeAdi, int sekmeSayisi, int eslesen)
    {
        var ad = string.IsNullOrWhiteSpace(projeAdi) ? "ilgili proje" : "\"" + projeAdi + "\"";
        if (sekmeSayisi == 0) return "Pencereyi öne getirdim ama içindeki sekmeleri okuyamadım. " + ad + " için doğru sekmeyi kendin seç.";
        if (eslesen > 1) return $"Pencereyi öne getirdim. {eslesen} sekme aynı projeye benziyor, hangisi olduğunu bilemedim. {ad} adlı sekmeleri kendin kontrol et.";
        return $"Pencereyi öne getirdim ama {sekmeSayisi} sekmeden hangisinin {ad} olduğunu bilemedim. Sekme başlığında {ad} yazanı ara.";
    }

    // ---------------------------------------------------------------- ana akış
    internal static Sonuc Getir(long? hwnd, int? pid, string? projeYolu, string? oturumBasligi = null)
    {
        try
        {
            var proje = KlasorAdi(projeYolu);
            var hedef = HedefPencere(hwnd, pid);
            if (hedef == IntPtr.Zero && !string.IsNullOrWhiteSpace(proje))
            {
                // Tutamaç bilinmiyor: önce bütün Terminal pencerelerinde proje adlı sekme, sonra editör pencerelerinde klasör adlı pencere aranır.
                var tumu = Win32ProcessInspector.SnapshotAllVisibleWindows();
                if (tumu.Any(w => SinifAdi(new IntPtr(w.Hwnd)) == TerminalSinifi))
                {
                    var t = Terminal(IntPtr.Zero, proje, projeYolu, oturumBasligi, pid);
                    if (t.Basarili) return t;
                }
                var ed = tumu.Where(w => SurecAdi(new IntPtr(w.Hwnd)) is { } s && EditorSurecleri.Any(e => e.Equals(s, StringComparison.OrdinalIgnoreCase)) && Tasiyor(w.Title, proje, projeYolu, oturumBasligi)).ToList();
                if (ed.Count >= 1) return Editor(new IntPtr(ed[0].Hwnd), SurecAdi(new IntPtr(ed[0].Hwnd)) ?? "", proje, projeYolu, oturumBasligi);
                return Yok("Açık pencereler arasında bu projenin adını taşıyan bir pencere ya da sekme bulamadım. Yardımcı kapanmış olabilir.");
            }
            if (hedef == IntPtr.Zero)
                return Yok("Açık bir pencere bulamadım. Yardımcı kapanmış olabilir. Yeniden açmak için proje klasörünü açabilirsin.");

            var sinif = SinifAdi(hedef);
            var surec = SurecAdi(hedef);
            if (sinif == TerminalSinifi) return Terminal(hedef, proje, projeYolu, oturumBasligi, pid);
            if (surec != null && EditorSurecleri.Any(e => e.Equals(surec, StringComparison.OrdinalIgnoreCase))) return Editor(hedef, surec, proje, projeYolu, oturumBasligi);

            var one = OneGetir(hedef);
            return new Sonuc(one, "pencere", one ? "Pencere öne getirildi." : "Pencereyi öne getiremedim. Windows başka bir pencereyi korumuş olabilir, görev çubuğundan tıkla.", null, hedef, []);
        }
        catch (Exception ex)
        {
            Log.Write("pencere öne getirme: " + ex.Message);
            return Yok("Pencereyi açamadım. Bir şey ters gitti, tekrar dene.");
        }
    }

    private static Sonuc Yok(string mesaj) => new(false, "yok", mesaj, null, null, []);

    private static IntPtr HedefPencere(long? hwnd, int? pid)
    {
        if (hwnd is > 0 && IsWindow(new IntPtr(hwnd.Value))) return new IntPtr(hwnd.Value);
        if (pid is > 0)
        {
            var r = Win32ProcessInspector.ResolveWindowForProcess(pid.Value, Win32ProcessInspector.SnapshotProcessTree(), Win32ProcessInspector.SnapshotVisibleWindows(), allowTerminalFallback: true);
            if (r is > 0 && IsWindow(new IntPtr(r.Value))) return new IntPtr(r.Value);
        }
        return IntPtr.Zero;
    }

    private static Sonuc Terminal(IntPtr hedef, string? proje, string? yol, string? oturum, int? pid = null)
    {
        // Önce verilen pencere, sonra bütün Terminal pencereleri. Genel olarak tek eşleşme aranır.
        var pencereler = new List<IntPtr>();
        if (hedef != IntPtr.Zero) pencereler.Add(hedef);
        foreach (var w in Win32ProcessInspector.SnapshotAllVisibleWindows())
        {
            var h = new IntPtr(w.Hwnd);
            if (h != hedef && SinifAdi(h) == TerminalSinifi) pencereler.Add(h);
        }
        var eslesenler = new List<(IntPtr Pencere, UiaSekme.Sekme Sekme)>();
        var hedefAdlari = new List<string>();
        var toplamEslesen = 0;
        foreach (var p in pencereler)
        {
            var sekmeler = UiaSekme.Sekmeler(p);
            if (p == hedef) hedefAdlari = sekmeler.Select(s => s.Ad).ToList();
            foreach (var s in sekmeler.Where(s => Tasiyor(s.Ad, proje, yol, oturum))) { eslesenler.Add((p, s)); toplamEslesen++; }
        }

        (IntPtr Pencere, UiaSekme.Sekme Sekme)? secilen = null;
        if (eslesenler.Count == 1) secilen = eslesenler[0];
        else if (eslesenler.Count > 1)
        {
            var hedefte = eslesenler.Where(e => e.Pencere == hedef).ToList();
            if (hedefte.Count == 1) secilen = hedefte[0];
        }

        if (secilen is { } s0)
        {
            var seciliydi = s0.Sekme.Secili;
            var sekmeSecildi = seciliydi || UiaSekme.Sec(s0.Sekme);
            if (sekmeSecildi && !seciliydi)
            {
                // Doğrulama: seçim gerçekten oldu mu? "Doğru sekmeye gidildi" yalnız o zaman söylenir.
                Thread.Sleep(80);
                var yeniden = UiaSekme.Sekmeler(s0.Pencere);
                sekmeSecildi = yeniden.Count > s0.Sekme.Sira && yeniden[s0.Sekme.Sira].Secili && yeniden[s0.Sekme.Sira].Ad == s0.Sekme.Ad;
            }
            if (!sekmeSecildi) Log.Write("sekme seçilemedi: " + UiaSekme.SonHata);
            var one = OneGetir(s0.Pencere);
            if (sekmeSecildi && one)
                return new Sonuc(true, "sekme", $"Doğru sekmeye gidildi: {s0.Sekme.Ad}", null, s0.Pencere, hedefAdlari);
            if (one) return new Sonuc(true, "pencere", "Pencere öne getirildi.", Ipucu(proje, hedefAdlari.Count, 1), s0.Pencere, hedefAdlari);
        }

        if (pid is > 0 && secilen == null)
        {
            // Adla kesin sekme bulunamadı ama sürecin kimliği kesin: sürecin konsol başlığına kısa süre işaret yazılıp sekme o işaretle bulunur, başlık hemen geri konur.
            var bulgu = KonsolIsareti.Bul(pid.Value, pencereler);
            if (bulgu != null)
            {
                Thread.Sleep(60);
                var yeniden = UiaSekme.Sekmeler(bulgu.Pencere);
                var secildi = yeniden.Count > bulgu.Sekme.Sira && yeniden[bulgu.Sekme.Sira].Secili;
                var one2 = OneGetir(bulgu.Pencere);
                var adlar = yeniden.Select(s => s.Ad).ToList();
                if (secildi && one2) return new Sonuc(true, "sekme", "Doğru sekmeye gidildi.", null, bulgu.Pencere, adlar);
                if (one2) return new Sonuc(true, "pencere", "Pencere öne getirildi.", Ipucu(proje, adlar.Count, Math.Max(1, toplamEslesen)), bulgu.Pencere, adlar);
            }
        }

        if (hedef == IntPtr.Zero && eslesenler.Count > 1)
        {
            // Tutamaç bilinmiyordu ve birden çok sekme benziyor: önce seçili olanın penceresi, yoksa ilki öne gelir, kullanıcıya belirsizlik söylenir.
            var secili = eslesenler.FindIndex(e => e.Sekme.Secili);
            hedef = eslesenler[secili >= 0 ? secili : 0].Pencere;
            hedefAdlari = UiaSekme.Sekmeler(hedef).Select(s => s.Ad).ToList();
        }
        if (hedef == IntPtr.Zero)
            return Yok("Açık Windows Terminal pencerelerinde bu projenin adını taşıyan bir sekme bulamadım. Yardımcı kapanmış olabilir.");
        var onde = OneGetir(hedef);
        var ipucu = Ipucu(proje, hedefAdlari.Count, toplamEslesen);
        return new Sonuc(onde, "pencere", onde ? "Pencere öne getirildi." : "Pencereyi öne getiremedim. Windows başka bir pencereyi korumuş olabilir, görev çubuğundan tıkla.", ipucu, hedef, hedefAdlari);
    }

    private static Sonuc Editor(IntPtr hedef, string surec, string? proje, string? yol, string? oturum)
    {
        var ayni = Win32ProcessInspector.SnapshotAllVisibleWindows()
            .Where(w => string.Equals(SurecAdi(new IntPtr(w.Hwnd)), surec, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(w.Title))
            .ToList();
        var eslesen = ayni.Where(w => Tasiyor(w.Title, proje, yol, oturum)).ToList();
        IntPtr git = hedef;
        string tur = "pencere";
        string? ipucu = null;
        if (eslesen.Count == 1) { git = new IntPtr(eslesen[0].Hwnd); tur = "klasor-penceresi"; }
        else if (eslesen.Count > 1)
        {
            var kendi = eslesen.FirstOrDefault(w => w.Hwnd == hedef.ToInt64());
            git = kendi != null ? hedef : new IntPtr(eslesen[0].Hwnd);
            ipucu = $"Pencereyi öne getirdim. Aynı klasör için {eslesen.Count} pencere açık, doğrusunu kendin kontrol et.";
        }
        else ipucu = $"Pencereyi öne getirdim ama hangisinin \"{proje ?? "ilgili klasör"}\" penceresi olduğunu bilemedim. Pencere başlığında klasör adını ara.";
        var one = OneGetir(git);
        return new Sonuc(one, tur, one ? (tur == "klasor-penceresi" ? "Klasörün penceresi öne getirildi." : "Pencere öne getirildi.") : "Pencereyi öne getiremedim. Görev çubuğundan tıkla.", one ? ipucu : null, git, []);
    }

    // ---------------------------------------------------------------- Win32
    /// <summary>Simge durumundaysa açar, sonra önüne alır. Windows'un ön plan korumasını AttachThreadInput ile aşar (kullanıcının az önceki tıklaması yetkidir).</summary>
    internal static bool OneGetir(IntPtr h)
    {
        try
        {
            if (!IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, 9);
            if (GetForegroundWindow() != h)
            {
                var onde = GetForegroundWindow();
                var evOnde = onde == IntPtr.Zero ? 0u : GetWindowThreadProcessId(onde, out _);
                var ben = GetCurrentThreadId();
                var bagli = evOnde != 0 && evOnde != ben && AttachThreadInput(ben, evOnde, true);
                try
                {
                    BringWindowToTop(h);
                    ShowWindow(h, 5);
                    SetForegroundWindow(h);
                }
                finally { if (bagli) AttachThreadInput(ben, evOnde, false); }
            }
            for (var i = 0; i < 10 && GetForegroundWindow() != h; i++) Thread.Sleep(30);
            return GetForegroundWindow() == h;
        }
        catch { return false; }
    }

    internal static string SinifAdi(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    internal static string? SurecAdi(IntPtr h)
    {
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool ekle);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
}
