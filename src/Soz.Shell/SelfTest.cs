using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Soz.Core.Data;

namespace Soz.Shell;

/// <summary>--selftest: ayrı test projesi olmadan, kabuğun saf mantığını (pano koruma, kısayol ayrıştırma, etkin proje, yerleşim...) sınar.</summary>
internal static partial class SelfTest
{
    private sealed class FakeClock : IScheduler
    {
        private readonly List<(TimeSpan At, Action Run, Handle H)> _jobs = new();
        public TimeSpan Now { get; private set; }

        private sealed class Handle : IDisposable { public bool Cancelled; public void Dispose() => Cancelled = true; }

        public IDisposable After(TimeSpan delay, Action action)
        {
            var h = new Handle();
            _jobs.Add((Now + delay, action, h));
            return h;
        }

        public void Advance(TimeSpan by)
        {
            var target = Now + by;
            while (true)
            {
                var due = _jobs.Where(x => !x.H.Cancelled && x.At <= target).OrderBy(x => x.At).ToList();
                if (due.Count == 0) break;
                var j = due[0];
                _jobs.Remove(j);
                Now = j.At;
                j.H.Cancelled = true;
                j.Run();
            }
            Now = target;
        }
    }

    private sealed class FakeClipboard : IClipboardBackend
    {
        public string? Text;
        public long Seq;
        public int Restores;
        public long Sequence => Seq;
        public object? Capture() => Text;
        public void SetText(string text) { Text = text; Seq++; }
        public string? GetText() => Text;
        public void Restore(object? snapshot) { Text = snapshot as string; Seq++; Restores++; }
        public void UserCopies(string t) { Text = t; Seq++; }
    }

    /// <summary>Sahte klavye düzenleri: ABD düzeni hiçbir Ctrl+Alt birleşiminde karakter üretmez; "TR Q benzeri" düzende Q için '@' ayarlanabilir.</summary>
    private sealed class FakeProbe : ILayoutProbe
    {
        private readonly Dictionary<(string Layout, uint Vk), int> _map = new();
        public IReadOnlyList<LayoutDescriptor> Layouts { get; } =
        [
            new(IntPtr.Zero, "0x04090409", "00000409", "ABD", "en-US"),
            new(IntPtr.Zero, "0x041F041F", "0000041F", "TR Q benzeri", "tr-TR"),
        ];
        public FakeProbe Set(string klid, uint vk, int cp) { _map[(klid, vk)] = cp; return this; }
        public int CtrlAltChar(LayoutDescriptor layout, uint vk, bool shift) => _map.TryGetValue((layout.Klid, vk), out var cp) ? cp : 0;
    }

    private sealed record Result(string Name, bool Ok, string Detail);

    public static int Run(Options opt)
    {
        var results = new List<Result>();
        void T(string name, Func<string?> body)
        {
            try { var e = body(); results.Add(new Result(name, e == null, e ?? "tamam")); }
            catch (Exception ex) { results.Add(new Result(name, false, "istisna: " + ex.GetType().Name + ": " + ex.Message)); }
        }
        static string? Check(bool cond, string msg) => cond ? null : msg;

        // ---------- pano koruma ----------
        T("pano: 1500 ms sonra eski içerik geri konur (1499 ms'de değil)", () =>
        {
            var clock = new FakeClock(); var clip = new FakeClipboard { Text = "ESKI" };
            var k = new ClipboardKeeper(clip, clock);
            k.Place("PAKET"); k.ScheduleRestore();
            clock.Advance(TimeSpan.FromMilliseconds(1499));
            if (clip.Text != "PAKET") return "1499 ms'de erken geri konmuş: " + clip.Text;
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return Check(clip.Text == "ESKI" && k.LastRestoreDone, "1500 ms'de geri konmadı: " + clip.Text);
        });
        T("pano: kullanıcı araya başka metin kopyaladıysa geri KONMAZ", () =>
        {
            var clock = new FakeClock(); var clip = new FakeClipboard { Text = "ESKI" };
            var k = new ClipboardKeeper(clip, clock);
            k.Place("PAKET"); k.ScheduleRestore();
            clock.Advance(TimeSpan.FromMilliseconds(500));
            clip.UserCopies("KULLANICININ YENI METNI");
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            return Check(clip.Text == "KULLANICININ YENI METNI" && clip.Restores == 0, "kullanıcının metni ezildi: " + clip.Text);
        });
        T("pano: kullanıcı AYNI paket metnini yeniden kopyaladıysa (sıra no değişti) geri konmaz", () =>
        {
            var clock = new FakeClock(); var clip = new FakeClipboard { Text = "ESKI" };
            var k = new ClipboardKeeper(clip, clock);
            k.Place("PAKET"); k.ScheduleRestore();
            clip.UserCopies("PAKET");
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            return Check(clip.Text == "PAKET" && clip.Restores == 0, "geri konmamalıydı: " + clip.Text);
        });
        T("pano: art arda iki yapıştırmada asıl eski içerik korunur (kendi paketimiz 'eski' sayılmaz)", () =>
        {
            var clock = new FakeClock(); var clip = new FakeClipboard { Text = "ESKI" };
            var k = new ClipboardKeeper(clip, clock);
            k.Place("PAKET1"); k.ScheduleRestore();
            clock.Advance(TimeSpan.FromMilliseconds(800));
            k.Place("PAKET2"); k.ScheduleRestore();
            clock.Advance(TimeSpan.FromMilliseconds(1499));
            if (clip.Text != "PAKET2") return "ilk zamanlayıcı iptal olmadı / erken geri koydu: " + clip.Text;
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return Check(clip.Text == "ESKI" && clip.Restores == 1, "asıl eski içerik geri gelmedi: " + clip.Text + " restores=" + clip.Restores);
        });
        T("pano: eski pano boşsa geri koyma panoyu boşaltır", () =>
        {
            var clock = new FakeClock(); var clip = new FakeClipboard { Text = null };
            var k = new ClipboardKeeper(clip, clock);
            k.Place("PAKET"); k.ScheduleRestore();
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            return Check(clip.Text == null, "pano boşalmadı: " + clip.Text);
        });
        T("pano: Abandon (kendi pencerem öndeyse) eski içeriği geri koymaz, paket kalır", () =>
        {
            var clock = new FakeClock(); var clip = new FakeClipboard { Text = "ESKI" };
            var k = new ClipboardKeeper(clip, clock);
            k.Place("PAKET"); k.Abandon();
            clock.Advance(TimeSpan.FromSeconds(5));
            return Check(clip.Text == "PAKET" && clip.Restores == 0, "paket kalmadı: " + clip.Text);
        });

        T("web iletisi: yalnız panelin kendi kökeni kabul edilir", () =>
        {
            const string b = "http://127.0.0.1:49300";
            foreach (var bad in new[] { "http://127.0.0.1:493001/", "http://127.0.0.1:49301/", "https://127.0.0.1:49300/", "http://evil.example/", "http://127.0.0.1.evil.example:49300/", "about:blank", "", null })
                if (PanelForm.SameOrigin(bad, b)) return $"'{bad}' kabul edildi";
            return Check(PanelForm.SameOrigin("http://127.0.0.1:49300/#/isler?yeni=1", b) && PanelForm.SameOrigin("http://127.0.0.1:49300/", b + "/"), "kendi köken reddedildi");
        });

        // ---------- kısayol ----------
        T("kısayol: Ctrl+Alt+K ayrıştırılır", () =>
        {
            if (!HotkeyManager.TryParse("Ctrl+Alt+K", out var s, out var e)) return "ayrıştırılamadı: " + e;
            return Check(s.Modifiers == (Native.MOD_CONTROL | Native.MOD_ALT) && s.VirtualKey == 0x4B && s.Display == "Ctrl+Alt+K", $"mods={s.Modifiers} vk={s.VirtualKey} {s.Display}");
        });
        T("kısayol: tek tuş, Caps Lock, değiştiricisiz ve çift ana tuş reddedilir", () =>
        {
            foreach (var bad in new[] { "K", "CapsLock", "Ctrl+CapsLock", "Ctrl+Alt", "", "Ctrl+K+L", "Ctrl+Bilinmez" })
                if (HotkeyManager.TryParse(bad, out _, out _)) return $"'{bad}' kabul edildi";
            return null;
        });
        T("kısayol: F-tuşu ve rakam kabul edilir", () =>
            Check(HotkeyManager.TryParse("Ctrl+Shift+F9", out var a, out _) && a.VirtualKey == (uint)Keys.F9
                  && HotkeyManager.TryParse("Alt+1", out var b, out _) && b.VirtualKey == (uint)Keys.D1, "F9/1 ayrıştırılamadı"));

        // ---------- etkin proje ----------
        var projects = new List<ProjectInfo>
        {
            new(1, "Söz", @"E:\Sample Work\urun", DateTimeOffset.UtcNow.AddHours(-2)),
            new(2, "KarincaAI", @"D:\projelerim\karinca", DateTimeOffset.UtcNow.AddMinutes(-30)),
            new(3, "a", @"C:\x", DateTimeOffset.UtcNow),
        };
        var now = DateTimeOffset.UtcNow;
        T("etkin proje: başlıktaki proje adı eşleşir", () =>
        {
            var r = ActiveProjectResolver.Pick("Claude Code - KarincaAI - Windows Terminal", false, projects, [], now, null);
            return Check(r is { Id: 2, Source: "title" }, "beklenen id=2 title; gelen " + r);
        });
        T("etkin proje: başlıktaki yol eşleşir (eğik çizgi/harf büyüklüğü fark etmez)", () =>
        {
            var r = ActiveProjectResolver.Pick("Code - e:/sample work/urun/src/x.cs", false, projects, [], now, null);
            return Check(r is { Id: 1 }, "gelen " + r);
        });
        T("etkin proje: çok kısa ad (1 harf) başlıkla eşleşmez", () =>
        {
            var r = ActiveProjectResolver.Pick("a b c", false, projects, [], now, null);
            return Check(r is not { Id: 3, Source: "title" }, "tek harfli ad başlıkla eşleşti");
        });
        T("etkin proje: başlık eşleşmezse son 10 dk'daki insan oturumu", () =>
        {
            var sessions = new List<SessionInfo>
            {
                new("s1", "claude", 1, "Söz", "human", "waiting", now.AddMinutes(-3), now.AddMinutes(-2), now),
                new("s2", "claude", 2, "KarincaAI", "subagent", "working", now.AddMinutes(-1), now, now),
                new("s3", "codex", 2, "KarincaAI", "human", "idle", now.AddMinutes(-40), now.AddMinutes(-39), now),
            };
            var r = ActiveProjectResolver.Pick("Gezgin", false, projects, sessions, now, null);
            return Check(r is { Id: 1, Source: "session" }, "gelen " + r);
        });
        T("etkin proje: hiçbiri yoksa en son proje", () =>
        {
            var r = ActiveProjectResolver.Pick("Gezgin", false, projects, [], now, null);
            return Check(r is { Id: 3, Source: "latest" }, "gelen " + r);
        });
        T("etkin proje: kabuğun kendi penceresi öndeyse son bilinen korunur", () =>
        {
            var last = new ActiveProject(2, "KarincaAI", "title");
            var r = ActiveProjectResolver.Pick("Söz", true, projects, [], now, last);
            return Check(r == last, "son bilinen korunmadı: " + r);
        });

        T("etkin proje: yolsuz Genel (*) projesi başlıkla ve 'en son' ile seçilmez", () =>
        {
            var withGeneral = new List<ProjectInfo> { new(9, "Genel", "", DateTimeOffset.UtcNow), new(1, "Söz", @"E:\Sample Work\urun", DateTimeOffset.UtcNow.AddHours(-2)) };
            var r1 = ActiveProjectResolver.Pick("Genel Ayarlar - Pencere", false, withGeneral, [], now, null);
            return Check(r1 is { Id: 1, Source: "latest" }, "gelen " + r1);
        });

        // ---------- ekran / yerleşim ----------
        T("ekran: dışarı taşan kapsül en çok kesiştiği çalışma alanına çekilir (çoklu ekran)", () =>
        {
            var was = new List<Rectangle> { new(0, 0, 1920, 1040), new(1920, 0, 1920, 1040) };
            var r1 = ScreenMath.ClampToWorkAreas(new Rectangle(1900, 1000, 200, 56), was);
            var r2 = ScreenMath.ClampToWorkAreas(new Rectangle(-5000, -5000, 200, 56), was);
            var r3 = ScreenMath.ClampToWorkAreas(new Rectangle(1800, 100, 200, 56), was);
            return Check(r1 == new Rectangle(1920, 984, 200, 56) && r2 == new Rectangle(0, 0, 200, 56) && r3 == new Rectangle(1720, 100, 200, 56),
                $"r1={r1} r2={r2} r3={r3}");
        });
        T("kapsül: yerleşim, rozet yoksa gizli; en çok MaxWidth; tıklama bölgeleri", () =>
        {
            using var rend = new CapsuleRenderer(1f);
            var none = rend.Layout(new CapsuleModel("Söz", 0, 0, true));
            var some = rend.Layout(new CapsuleModel("Söz", 2, 1, true));
            var huge = rend.Layout(new CapsuleModel(new string('W', 80), 3, 1, true));
            if (none.BadgeRect != null) return "rozet 0'da görünüyor";
            if (some.BadgeRect == null) return "rozet 3'te görünmüyor";
            if (huge.WinW > (CapsuleLayout.MaxWidth + 2 * CapsuleLayout.Margin) + 1) return "azami genişlik aşıldı: " + huge.WinW;
            if (none.WinW < (CapsuleLayout.MinWidth + 2 * CapsuleLayout.Margin) - 1) return "asgari genişlik altında: " + none.WinW;
            var d = Point.Round(new PointF(some.Dictate.X + 2, some.Dictate.Y + 2));
            var p = Point.Round(new PointF(some.Packet.X + 2, some.Packet.Y + 2));
            var b = Point.Round(new PointF(some.Body.X + 20, some.Body.Y + 20));
            var nj = Point.Round(new PointF(some.NewJob.X + 2, some.NewJob.Y + 2));
            if (some.HitTest(d) != HoverPart.Dictate || some.HitTest(p) != HoverPart.Packet || some.HitTest(b) != HoverPart.Body) return "tıklama bölgeleri yanlış";
            if (some.HitTest(nj) != HoverPart.NewJob) return "Yeni iş bölgesi yanlış: " + some.HitTest(nj);
            if (some.NewJob.IntersectsWith(some.Dictate) || some.Dictate.IntersectsWith(some.Packet) || some.NewJob.IntersectsWith(some.Packet)) return "düğme bölgeleri çakışıyor";
            if (!(some.NewJob.Right <= some.Dictate.Left && some.Dictate.Right <= some.Packet.Left)) return "düğme sırası bozuk (Yeni iş, Dikte, Paket beklenir)";
            if (some.BadgeRect is { } br0 && br0.Right > some.NewJob.Left) return "rozet Yeni iş düğmesine biniyor";
            if (some.NameRect.Right > some.NewJob.Left) return "ad alanı Yeni iş düğmesine biniyor";
            return Check(some.HitTest(new Point(1, 1)) == HoverPart.None, "gölgede tıklama gövde sayıldı");
        });
        T("kapsül: ipucu metni biçimi", () =>
        {
            var a = new CapsuleModel("Söz", 2, 1, true, FailedJobs: 1).Tooltip();
            var b = new CapsuleModel("Söz", 0, 0, true).Tooltip();
            var c = new CapsuleModel("", 0, 0, false).Tooltip();
            var d = new CapsuleModel("Söz", 0, 0, true, FailedJobs: 3).Tooltip();
            var e = new CapsuleModel("Söz", 2, 1, true).Tooltip();
            var noProj = new CapsuleModel("", 0, 0, true).Tooltip();
            var badge = new CapsuleModel("Söz", 2, 1, true, FailedJobs: 1).Badge;
            if (!Strings.Get("webview.missing.text").StartsWith("Ekran bileşeni (Microsoft Edge WebView2) bu bilgisayarda eksik görünüyor.", StringComparison.Ordinal))
                return "webview eksik metni güncellenmedi: " + Strings.Get("webview.missing.text");
            return Check(a == "2 bekliyor, 1 durumu belli değil, 1 iş kaldı · Proje: Söz" && b == "Seni bekleyen yok · Proje: Söz" && c == "veri yok"
                && d == "3 iş kaldı · Proje: Söz" && e == "2 bekliyor, 1 durumu belli değil · Proje: Söz"
                && noProj == "Seni bekleyen yok · Henüz seçili proje yok" && badge == 4,
                $"a='{a}' b='{b}' c='{c}' d='{d}' e='{e}' noProj='{noProj}' badge={badge}");
        });
        T("kapsül: HiDPI ölçekte yerleşim orantılı büyür (150%)", () =>
        {
            var (w1, h1) = Snapshot.MeasureOne(new CapsuleModel("Söz", 2, 0, true), 1f);
            var (w2, h2) = Snapshot.MeasureOne(new CapsuleModel("Söz", 2, 0, true), 1.5f);
            return Check(Math.Abs(w2 - w1 * 1.5) <= 6 && Math.Abs(h2 - h1 * 1.5) <= 2, $"100%={w1}x{h1} 150%={w2}x{h2}");
        });

        // ---------- tek örnek kilidi ----------
        T("tek örnek kilidi: açık veri klasörü kilidi klasöre özel yapar, varsayılanda eski ad kalır (b2)", () =>
        {
            const string Taban = @"Local\Soz.SingleInstance.v1";
            var varsayilan = Options.Parse(Array.Empty<string>());
            var a = Options.Parse(new[] { "--data", @"C:\deneme\veri" });
            var aYine = Options.Parse(new[] { "--data", @"C:\DENEME\veri\" });
            var b = Options.Parse(new[] { "--data", @"C:\baska\veri" });
            var eskiEnv = Environment.GetEnvironmentVariable("SOZ_DATA");
            try
            {
                Environment.SetEnvironmentVariable("SOZ_DATA", null);
                if (varsayilan.TekOrnekAdi(Taban, @"C:\deneme\veri") != Taban) return "varsayılan klasörde eski ad değişti: " + varsayilan.TekOrnekAdi(Taban, @"C:\deneme\veri");
                var ad = a.TekOrnekAdi(Taban, @"C:\deneme\veri");
                if (ad == Taban || !ad.StartsWith(Taban + ".", StringComparison.Ordinal)) return "açık veri klasörü kilide yansımadı: " + ad;
                if (aYine.TekOrnekAdi(Taban, @"C:\DENEME\veri\") != ad) return "aynı klasör (büyük harf, sondaki ters bölü) farklı kilit verdi";
                if (b.TekOrnekAdi(Taban, @"C:\baska\veri") == ad) return "farklı klasörler aynı kilidi paylaşıyor";
                Environment.SetEnvironmentVariable("SOZ_DATA", @"C:\deneme\veri");
                if (varsayilan.TekOrnekAdi(Taban, @"C:\deneme\veri") != ad) return "SOZ_DATA ile verilen klasör --data ile aynı kilidi vermedi";
                return null;
            }
            finally { Environment.SetEnvironmentVariable("SOZ_DATA", eskiEnv); }
        });

        // ---------- dikte köprüsü ----------
        T("dikte: yol sırası (ayar > current\\ > kök) ve eksikse null", () =>
        {
            var root = Path.Combine(opt.SelfTestPath != null ? Path.GetDirectoryName(Path.GetFullPath(opt.SelfTestPath))! : AppContext.BaseDirectory, "selftest-tmp-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var cur = Path.Combine(root, "KarincaAI", "current"); Directory.CreateDirectory(cur);
                var curExe = Path.Combine(cur, "KarincaAI.exe"); var rootExe = Path.Combine(root, "KarincaAI", "KarincaAI.exe");
                if (new DictationBridge(() => null, root).ResolvePath() != null) return "dosya yokken yol döndü";
                File.WriteAllText(rootExe, "x");
                if (new DictationBridge(() => null, root).ResolvePath() != rootExe) return "kök yol bulunamadı";
                File.WriteAllText(curExe, "x");
                if (new DictationBridge(() => null, root).ResolvePath() != curExe) return "current\\ öncelik almadı";
                var custom = Path.Combine(root, "ozel.cmd"); File.WriteAllText(custom, "x");
                if (new DictationBridge(() => custom, root).ResolvePath() != custom) return "ayar yolu öncelik almadı";
                if (new DictationBridge(() => Path.Combine(root, "yok.exe"), root).ResolvePath() != null) return "ayarlı ama eksik yol varsayılana düştü";
                return null;
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        // ---------- API ayrıştırma ----------
        T("API: ayarlar gerçek (düz nesne) biçimde ayrıştırılır; özet eksik alanda çökmez", () =>
        {
            using var d1 = JsonDocument.Parse("{\"hotkeyPacket\":\"Ctrl+Alt+K\",\"capsuleX\":120,\"x\":null,\"explainEnabled\":false,\"packetBudget\":2000}");
            using var d2 = JsonDocument.Parse("{\"capsuleY\":40,\"dictationPath\":null}");
            using var d3 = JsonDocument.Parse("{\"waiting\":2,\"stalled\":\"1\"}");
            using var d4 = JsonDocument.Parse("[1,2]");
            var s1 = Json.ParseSettings(d1.RootElement); var s2 = Json.ParseSettings(d2.RootElement);
            var sum = Json.ParseSummary(d3.RootElement);
            if (s1["hotkeyPacket"] != "Ctrl+Alt+K" || s1["capsuleX"] != "120" || s1["x"] != "" || s1["explainEnabled"] != "false" || s1["packetBudget"] != "2000") return "nesne biçimi yanlış";
            if (s2["capsuleY"] != "40" || s2["dictationPath"] != "") return "null/sayı biçimi yanlış";
            if (sum is not { Waiting: 2, Stalled: 1, Working: 0 }) return "özet yanlış: " + sum;
            return Check(Json.ParseSummary(d4.RootElement) == null && Json.ParseProjects(d3.RootElement).Count == 0, "yanlış biçimde çökmedi ama boş dönmedi");
        });

        // ---------- yeni iş kısayolu: AltGr kararı ----------
        T("kısayol: Ctrl+Shift+Space ayrıştırılır (Keys.Space)", () =>
        {
            if (!HotkeyManager.TryParse("Ctrl+Shift+Space", out var s, out var e)) return "ayrıştırılamadı: " + e;
            return Check(s.Modifiers == (Native.MOD_CONTROL | Native.MOD_SHIFT) && s.VirtualKey == (uint)Keys.Space && s.Display == "Ctrl+Shift+Space", $"mods={s.Modifiers} vk={s.VirtualKey} {s.Display}");
        });
        T("AltGr: Ctrl+Alt+Q bir düzende karakter üretiyorsa kısayol düşer (Q kaydı denenmez)", () =>
        {
            var probe = new FakeProbe().Set("0000041F", 0x51, 0x40); // TR Q benzeri: AltGr+Q = '@'
            var tried = new List<string>();
            var r = HotkeyPolicy.Resolve("Ctrl+Alt+Q", "Ctrl+Alt+J", probe, s => { tried.Add(s); return null; });
            if (!(r.FellBack && r.Registered && r.Display == "Ctrl+Shift+Space")) return "düşmedi: " + r;
            if (tried.Count != 1 || tried[0] != "Ctrl+Shift+Space") return "kayıt denemeleri: " + string.Join(",", tried);
            return Check(r.Reason != null && r.Reason.Contains("AltGr") && r.Reason.Contains("U+0040") && !r.Reason.Contains("@"), "neden: " + r.Reason);
        });
        T("AltGr: Ctrl+Alt+J hiçbir düzende karakter üretmiyorsa kalır", () =>
        {
            var probe = new FakeProbe().Set("0000041F", 0x51, 0x40);
            var r = HotkeyPolicy.Resolve("Ctrl+Alt+J", "Ctrl+Alt+J", probe, _ => null);
            return Check(!r.FellBack && r.Registered && r.Display == "Ctrl+Alt+J" && r.Reason == null, "geldi: " + r);
        });
        T("AltGr: ayar boşsa varsayılan Ctrl+Alt+J kullanılır", () =>
        {
            var r = HotkeyPolicy.Resolve("", HotkeyPolicy.NewJobDefault, new FakeProbe(), _ => null);
            return Check(!r.FellBack && r.Display == "Ctrl+Alt+J", "geldi: " + r);
        });
        T("AltGr: RegisterHotKey başarısızsa Ctrl+Shift+Space'e düşer, neden kayıtlı", () =>
        {
            var r = HotkeyPolicy.Resolve("Ctrl+Alt+J", "Ctrl+Alt+J", new FakeProbe(), s => s == "Ctrl+Alt+J" ? "başka program" : null);
            return Check(r.FellBack && r.Registered && r.Display == "Ctrl+Shift+Space" && r.Reason!.Contains("başka program"), "geldi: " + r);
        });
        T("AltGr: yedek de kaydedilemezse registered=false ve neden dolu", () =>
        {
            var r = HotkeyPolicy.Resolve("Ctrl+Alt+J", "Ctrl+Alt+J", new FakeProbe(), _ => "hep hata");
            return Check(r.FellBack && !r.Registered && r.FailureReason == "hep hata", "geldi: " + r);
        });
        T("AltGr: Ctrl+Alt içermeyen birleşim ölçülmez; denetim karakteri çakışma sayılmaz, ölü tuş sayılır", () =>
        {
            var probe = new FakeProbe().Set("0000041F", 0x4A, 0x0A).Set("00000409", 0x4B, -1); // J: Ctrl+J denetim kodu; K: ölü tuş
            var shift = HotkeyPolicy.Measure("Ctrl+Shift+Space", probe);
            var j = HotkeyPolicy.Measure("Ctrl+Alt+J", probe);
            var k = HotkeyPolicy.Measure("Ctrl+Alt+K", probe);
            if (shift.Hits.Count != 0 || shift.Conflict) return "Ctrl+Shift+Space ölçüldü";
            if (j.Conflict) return "denetim karakteri çakışma sayıldı";
            return Check(k.Conflict && k.ConflictReason!.Contains("ölü tuş"), "ölü tuş çakışma sayılmadı: " + k.ConflictReason);
        });
        T("AltGr: geçersiz ayar metni yedeğe düşer", () =>
        {
            var r = HotkeyPolicy.Resolve("Bilinmez", "Ctrl+Alt+J", new FakeProbe(), _ => null);
            return Check(r.FellBack && r.Display == "Ctrl+Shift+Space" && r.Reason!.Contains("geçersiz"), "geldi: " + r);
        });

        // ---------- yapıştırma kararı (gerçek tuş/pano yok) ----------
        var hTarget = new IntPtr(1234); var hOwn = new IntPtr(99);
        bool Usable(IntPtr h) => h == hTarget; // hOwn (kabuğun kendi penceresi) ve var olmayan pencereler kullanılamaz
        T("yapıştırma kararı: hedef yok → copied; kendi pencerem → copied; ön pencere olmadı → copied; oldu → pasted", () =>
        {
            var a = TextPasteRules.Decide("x", IntPtr.Zero, Usable, true);
            var b = TextPasteRules.Decide("x", hOwn, Usable, true);
            var c = TextPasteRules.Decide("x", hTarget, Usable, false);
            var d = TextPasteRules.Decide("x", hTarget, Usable, true);
            var e2 = TextPasteRules.Decide("x", new IntPtr(555), Usable, true);
            return Check(a == TextPasteOutcome.Copied && b == TextPasteOutcome.Copied && c == TextPasteOutcome.Copied && d == TextPasteOutcome.Pasted && e2 == TextPasteOutcome.Copied,
                $"a={a} b={b} c={c} d={d} e={e2}");
        });
        T("yapıştırma kararı: boş / yalnız boşluk / 20.000'den uzun metin → rejected; tam 20.000 kabul", () =>
        {
            var a = TextPasteRules.Decide("", hTarget, Usable, true);
            var b = TextPasteRules.Decide(null, hTarget, Usable, true);
            var c = TextPasteRules.Decide("   ", hTarget, Usable, true);
            var d = TextPasteRules.Decide(new string('a', 20001), hTarget, Usable, true);
            var e2 = TextPasteRules.Decide(new string('a', 20000), hTarget, Usable, true);
            return Check(a == TextPasteOutcome.Rejected && b == TextPasteOutcome.Rejected && c == TextPasteOutcome.Rejected && d == TextPasteOutcome.Rejected && e2 == TextPasteOutcome.Pasted,
                $"a={a} b={b} c={c} d={d} e={e2}");
        });
        T("yapıştırma: sayfaya giden sonuç iletileri", () =>
        {
            var j1 = TextPasteRules.PasteResultJson(TextPasteOutcome.Pasted);
            var j2 = TextPasteRules.PasteResultJson(TextPasteOutcome.Rejected);
            var j3 = TextPasteRules.DictateResultJson(true);
            return Check(j1 == "{\"type\":\"paste-result\",\"outcome\":\"pasted\"}" && j2.Contains("\"rejected\"") && TextPasteRules.PasteResultJson(TextPasteOutcome.Copied).Contains("\"copied\"")
                && TextPasteRules.PasteResultJson(TextPasteOutcome.Failed).Contains("\"failed\"") && j3 == "{\"type\":\"dictate-result\",\"started\":true}", $"{j1} {j2} {j3}");
        });

        // ---------- arayüz iletileri ----------
        T("web iletisi: paste, dictate, bozuk JSON, dize soz-esc, bilinmeyen tür", () =>
        {
            var p = WebMessages.Parse("{\"type\":\"paste\",\"text\":\"x\"}");
            var p2 = WebMessages.Parse("{\"type\":\"paste\"}");
            var p3 = WebMessages.Parse("{\"type\":\"paste\",\"text\":5}");
            var d = WebMessages.Parse("{\"type\":\"dictate\"}");
            var bad = WebMessages.Parse("{\"type\":");
            var empty = WebMessages.Parse("");
            var esc = WebMessages.Parse("\"soz-esc\"");
            var other = WebMessages.Parse("\"baska\"");
            var unk = WebMessages.Parse("{\"type\":\"zzz\"}");
            var notype = WebMessages.Parse("{\"a\":1}");
            var arr = WebMessages.Parse("[1]");
            var pf = WebMessages.Parse("{\"type\":\"pick-folder\"}");
            var tc = WebMessages.Parse("{\"type\":\"theme-changed\",\"family\":\"terminal\",\"mode\":\"koyu\"}");
            var rp = WebMessages.Parse("{\"type\":\"reset-panel-place\"}");
            if (pf.Kind != WebMessageKind.PickFolder) return "pick-folder: " + pf;
            if (tc is not { Kind: WebMessageKind.ThemeChanged, Family: "terminal", Mode: "koyu" }) return "theme-changed: " + tc;
            if (rp.Kind != WebMessageKind.ResetPanelPlace) return "reset-panel-place: " + rp;
            using (var fp = JsonDocument.Parse(WebMessages.FolderPickedJson("E:\\Proje Klasörü")))
            using (var fn = JsonDocument.Parse(WebMessages.FolderPickedJson(null)))
            {
                if (fp.RootElement.GetProperty("type").GetString() != "folder-picked" || fp.RootElement.GetProperty("path").GetString() != "E:\\Proje Klasörü") return "folder-picked yolu";
                if (fn.RootElement.GetProperty("path").ValueKind != JsonValueKind.Null) return "folder-picked vazgeçme";
            }
            if (PanelForm.IdleReleaseAfter != TimeSpan.FromMinutes(3)) return "boşta bırakma süresi 3 dk olmalı: " + PanelForm.IdleReleaseAfter;
            if (PanelYerlesim.VarsayilanDp != new Size(500, 720) || PanelYerlesim.EnKucukDp != new Size(380, 480)) return "panel dp boyutları yanlış";
            if (p is not { Kind: WebMessageKind.Paste, Text: "x" }) return "paste: " + p;
            if (p2.Kind != WebMessageKind.Paste || p2.Text != null || p3.Kind != WebMessageKind.Paste || p3.Text != null) return "metinsiz paste: " + p2 + " / " + p3;
            if (d.Kind != WebMessageKind.Dictate) return "dictate: " + d;
            if (bad.Kind != WebMessageKind.Invalid || empty.Kind != WebMessageKind.Invalid) return "bozuk JSON: " + bad;
            if (esc.Kind != WebMessageKind.Esc) return "soz-esc: " + esc;
            if (other.Kind != WebMessageKind.Unknown || unk is not { Kind: WebMessageKind.Unknown, Type: "zzz" } || notype.Kind != WebMessageKind.Unknown || arr.Kind != WebMessageKind.Unknown)
                return "bilinmeyen: " + other + " / " + unk + " / " + notype + " / " + arr;
            return null;
        });

        // ---------- /api/now ----------
        T("API: /api/now counts ayrıştırılır; counts yoksa null (özete düşülür)", () =>
        {
            using var d1 = JsonDocument.Parse("{\"sentence\":\"x\",\"needsYou\":[],\"runningJobs\":[],\"recentVerdicts\":[],\"counts\":{\"jobsPassed\":0,\"jobsFailed\":1,\"jobsRunning\":1,\"waiting\":2,\"working\":0,\"stalled\":1,\"queuePending\":0,\"guardBlocked\":0}}");
            using var d2 = JsonDocument.Parse("{\"waiting\":2}");
            using var d3 = JsonDocument.Parse("{\"counts\":{\"waiting\":\"3\"}}");
            using var d4 = JsonDocument.Parse("[]");
            var n = Json.ParseNow(d1.RootElement);
            var n3 = Json.ParseNow(d3.RootElement);
            return Check(n is { Waiting: 2, Stalled: 1, JobsFailed: 1 } && Json.ParseNow(d2.RootElement) == null && n3 is { Waiting: 3, Stalled: 0, JobsFailed: 0 } && Json.ParseNow(d4.RootElement) == null,
                $"n={n} n3={n3}");
        });

        // ---------- PanelForm WM_CLOSE / IsDisposed savunması + 6 tema + tek örnek ve dikte kanalı ----------
        T("panel kapatma savunması: CloseReason.None ve UserClosing gizlenir, uygulama çıkışı ve AllowClose kapatır", () =>
        {
            if (!PanelForm.ShouldCancelClose(CloseReason.None, false)) return "CloseReason.None engellenmedi";
            if (!PanelForm.ShouldCancelClose(CloseReason.UserClosing, false)) return "UserClosing engellenmedi";
            if (!PanelForm.ShouldCancelClose(CloseReason.FormOwnerClosing, false)) return "FormOwnerClosing engellenmedi";
            if (PanelForm.ShouldCancelClose(CloseReason.ApplicationExitCall, false)) return "ApplicationExitCall engellendi";
            if (PanelForm.ShouldCancelClose(CloseReason.WindowsShutDown, false)) return "WindowsShutDown engellendi";
            if (PanelForm.ShouldCancelClose(CloseReason.TaskManagerClosing, false)) return "TaskManagerClosing engellendi";
            if (PanelForm.ShouldCancelClose(CloseReason.None, true)) return "AllowClose=true iken engellendi";
            return null;
        });
        T("panel IsDisposed kurtarması ve zemin rengi eşlemesi", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "soz-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var ctx = new ShellContext(Options.Parse(new[] { "--no-capsule" }), tmp);
                ctx.Pal = Palette.ForExplicit("terminal", false, "dark");
                var p1 = ctx.EnsurePanel();
                if (p1.BackColor != ctx.Pal.EffectivePanelBg) return "ilk zemin rengi eşleşmedi: " + p1.BackColor;
                p1.AllowClose = true;
                p1.Dispose();
                if (!p1.IsDisposed) return "p1 dispose olmadı";
                var p2 = ctx.EnsurePanel();
                if (p2.IsDisposed || ReferenceEquals(p1, p2)) return "dispose sonrası yeni PanelForm üretilmedi";
                ctx.ApplyTheme("sicak", "acik");
                if (p2.BackColor != Palette.ForExplicit("sicak", true, "light").EffectivePanelBg) return "canlı tema zemin rengi güncellenmedi";
                p2.AllowClose = true;
                p2.Dispose();
                return null;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        });
        T("tek örnek kanalı: show-panel sinyali çalışan örneği uyandırır", () =>
        {
            var pipe = "Soz.Shell.TestActivate." + Guid.NewGuid().ToString("N");
            var signaled = new ManualResetEventSlim(false);
            using var srv = SingleInstanceChannel.StartServer(pipe, () => signaled.Set());
            Thread.Sleep(40);
            var ok = SingleInstanceChannel.TrySignalShowPanel(pipe, 1000);
            return Check(ok && signaled.Wait(1000), $"ok={ok} signaled={signaled.IsSet}");
        });
        T("dikte köprüsü: sahte boru varken 1 bayt 4 gönderir ve 1 onay okur, boru yokken yola düşer", () =>
        {
            var pipe = "Soz.Shell.TestDictate." + Guid.NewGuid().ToString("N");
            byte received = 0;
            var serverTask = Task.Run(async () =>
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                var buf = new byte[1];
                var n = await server.ReadAsync(buf);
                if (n == 1) received = buf[0];
                await server.WriteAsync(new byte[] { DictationBridge.PipeAcceptedReply });
                await server.FlushAsync();
                try { _ = await server.ReadAsync(buf); } catch { }
            });
            Thread.Sleep(40);
            var fallbackCalled = false;
            var bridge = new DictationBridge(() => null, Path.GetTempPath(), () => pipe, _ => { fallbackCalled = true; return null; });
            var res = bridge.Toggle();
            serverTask.Wait(1000);
            if (!res.Started || received != DictationBridge.DictatePipeCommand || fallbackCalled)
                return $"boru tetiklenmedi: started={res.Started} cmd={received} fallback={fallbackCalled}";

            var root = Path.Combine(Path.GetTempPath(), "soz-dic-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(root);
                var fakeExe = Path.Combine(root, "sahte.exe");
                File.WriteAllText(fakeExe, "x");
                string? startedPath = null;
                var bridge2 = new DictationBridge(() => fakeExe, root, () => pipe + ".yok", p => { startedPath = p; return System.Diagnostics.Process.GetCurrentProcess(); });
                var res2 = bridge2.Toggle();
                return Check(res2.Started && startedPath == fakeExe, $"fallback çalışmadı: {res2}");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });
        T("dikte ev sahibi: komut 7/6/8 ile canlı durum okunur ve çıkışta ayrılır", () =>
        {
            var pipe = "Soz.Shell.TestHost." + Guid.NewGuid().ToString("N");
            var seen = new List<byte>();
            var ready = new ManualResetEventSlim(false);
            var serverTask = Task.Run(async () =>
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                ready.Set();
                for (var step = 0; step < 3; step++)
                {
                    await server.WaitForConnectionAsync();
                    var buf = new byte[1];
                    var n = await server.ReadAsync(buf);
                    if (n == 1) seen.Add(buf[0]);
                    var reply = buf[0] switch
                    {
                        DictationBridge.HostAttachPipeCommand => (byte)DictationHostStatus.Recording,
                        DictationBridge.StatusQueryPipeCommand => (byte)DictationHostStatus.Delivered,
                        DictationBridge.HostDetachPipeCommand => DictationBridge.PipeAcceptedReply,
                        _ => (byte)0,
                    };
                    await server.WriteAsync(new byte[] { reply });
                    await server.FlushAsync();
                    try { _ = await server.ReadAsync(buf); } catch { }
                    server.Disconnect();
                }
            });
            ready.Wait(1000);
            var bridge = new DictationBridge(() => null, Path.GetTempPath(), () => pipe);
            var s1 = bridge.AttachAndQueryStatus(500);
            var s2 = bridge.QueryStatus(500);
            var detached = bridge.DetachHost(500);
            serverTask.Wait(1500);
            return Check(
                s1 == DictationHostStatus.Recording
                && DictationBridge.ToCapsulePhase(s1) == "dinliyor"
                && bridge.HostProtocolSupported
                && s2 == DictationHostStatus.Delivered
                && DictationBridge.ToCapsulePhase(s2) == "yazdi"
                && detached
                && !bridge.IsHostAttached
                && seen.SequenceEqual(new byte[] { 7, 6, 8 }),
                $"s1={s1} s2={s2} detached={detached} seen={string.Join(",", seen)}");
        });
        T("tek örnek kanalı: exit sinyali çalışan örneği temizce kapatır", () =>
        {
            var pipe = "Soz.Shell.TestExit." + Guid.NewGuid().ToString("N");
            var exited = new ManualResetEventSlim(false);
            using var srv = SingleInstanceChannel.StartServer(pipe, () => { }, () => exited.Set());
            Thread.Sleep(40);
            var ok = SingleInstanceChannel.TrySignalExit(pipe, 1000);
            return Check(ok && exited.Wait(1000), $"ok={ok} exited={exited.IsSet}");
        });
        C6Testleri(T);
        E7Testleri(T);

        var ok = results.Count(r => r.Ok); var fail = results.Count - ok;
        var json = JsonSerializer.Serialize(new { passed = ok, failed = fail, total = results.Count, tests = results },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        try
        {
            if (opt.SelfTestPath != null)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(opt.SelfTestPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(opt.SelfTestPath, json);
            }
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Out.WriteLine(json);
        }
        catch { /* konsol yoksa dosya yeter */ }
        return fail == 0 ? 0 : 1;
    }
}
