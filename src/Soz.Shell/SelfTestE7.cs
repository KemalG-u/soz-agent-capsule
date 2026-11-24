using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Soz.Core.Attention;
using Soz.Core.Security;
using Soz.Server;
using Soz.Server.Endpoints;

namespace Soz.Shell;

/// <summary>--selftest e7 bölümü: Core ile kabuk arasındaki dikişler (odaklama zinciri, tercih köprüsü, arka plan başlığı, tek sürüm kaynağı). Gerçek kullanıcı verisine ve gerçek kayıt defterine dokunmaz.</summary>
internal static partial class SelfTest
{
    private static int BosBaglanti()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return port;
    }

    private static void E7Testleri(Action<string, Func<string?>> T)
    {
        T("sürüm: kabuk ve Core aynı tek kaynaktan (0.3.6)", () =>
        {
            var kabuk = typeof(SelfTest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
            return C(Soz.Core.SozInfo.Version == "0.3.6" && kabuk == Soz.Core.SozInfo.Version, $"core={Soz.Core.SozInfo.Version} kabuk={kabuk}");
        });

        T("arka plan başlığı: kabuk istemcisi ile Core aynı adı kullanır", () =>
            C(ApiClient.ArkaBaslik == Soz.Core.Profile.LiveCadence.BackgroundHeader, $"kabuk={ApiClient.ArkaBaslik} core={Soz.Core.Profile.LiveCadence.BackgroundHeader}"));

        T("kök değişkenleri: USERPROFILE, ANTIGRAVITY_HOME ve ANTIGRAVITY_CLI_HOME gerçek profile sızmadan izlenir", () =>
        {
            string?[] adlar = ["USERPROFILE", "ANTIGRAVITY_HOME", "ANTIGRAVITY_CLI_HOME"];
            var eski = adlar.Select(a => Environment.GetEnvironmentVariable(a!)).ToArray();
            try
            {
                Environment.SetEnvironmentVariable("USERPROFILE", @"C:\sentetik\ev");
                Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", @"C:\sentetik\ag");
                Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", @"C:\sentetik\agcli");
                var r = Soz.Core.Onboarding.OnboardingRoots.FromEnvironment();
                var a = r.Home == @"C:\sentetik\ev" && r.AntigravityHome == @"C:\sentetik\ag" && r.AntigravityCliHome == @"C:\sentetik\agcli";
                Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", null);
                Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", null);
                var r2 = Soz.Core.Onboarding.OnboardingRoots.FromEnvironment();
                var b = r2.AntigravityHome == Path.Combine(@"C:\sentetik\ev", ".gemini", "antigravity");   // değişken yoksa ev klasörünün altındaki varsayılan
                return C(a && b, $"home={r.Home} ag={r.AntigravityHome} cli={r.AntigravityCliHome} varsayilan={r2.AntigravityHome}");
            }
            finally { for (var i = 0; i < adlar.Length; i++) Environment.SetEnvironmentVariable(adlar[i]!, eski[i]); }
        });

        T("web köprüsü: get-shell-prefs / set-shell-pref ayrıştırılır, bozuk değer işlenmez", () =>
        {
            var g = WebMessages.Parse("{\"type\":\"get-shell-prefs\"}");
            var s = WebMessages.Parse("{\"type\":\"set-shell-pref\",\"key\":\"bildirimler\",\"value\":false}");
            var sc = WebMessages.Parse("{\"type\":\"set-shell-pref\",\"key\":\"kaldirirkenVeriyiSil\",\"value\":true,\"confirmed\":true}");
            var bozukDeger = WebMessages.Parse("{\"type\":\"set-shell-pref\",\"key\":\"bildirimler\",\"value\":\"evet\"}");
            var anahtarsiz = WebMessages.Parse("{\"type\":\"set-shell-pref\",\"value\":true}");
            using var doc = System.Text.Json.JsonDocument.Parse(WebMessages.ShellPrefsJson(true, false, true));
            var r = doc.RootElement;
            return C(g.Kind == WebMessageKind.GetShellPrefs
                && s is { Kind: WebMessageKind.SetShellPref, Key: "bildirimler", Value: false, Confirmed: false }
                && sc is { Kind: WebMessageKind.SetShellPref, Key: "kaldirirkenVeriyiSil", Value: true, Confirmed: true }
                && bozukDeger.Kind == WebMessageKind.Invalid && anahtarsiz.Kind == WebMessageKind.Invalid
                && r.GetProperty("type").GetString() == "shell-prefs" && r.GetProperty("bildirimler").GetBoolean() && !r.GetProperty("kaldirirkenVeriyiSil").GetBoolean() && r.GetProperty("acilistaBasla").GetBoolean(),
                $"g={g.Kind} s={s} sc={sc} bozuk={bozukDeger.Kind} anahtarsiz={anahtarsiz.Kind}");
        });

        T("web köprüsü: tercih iletileri kabuk tercihlerini yazar; veriyi silme onaysız açılmaz", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "soz-e7w-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(dir);
                var acilis = true; var bildirim = true;
                var kopru = new TercihKopru(dir, () => acilis, _ => { acilis = true; return true; }, () => acilis = false, () => bildirim, b => bildirim = b);
                string Cevap(string json) => kopru.Isle(WebMessages.Parse(json));
                bool Bool(string cevap, string ad) { using var d = System.Text.Json.JsonDocument.Parse(cevap); return d.RootElement.GetProperty(ad).GetBoolean(); }

                var c0 = Cevap("{\"type\":\"get-shell-prefs\"}");
                var okGet = Bool(c0, "bildirimler") && Bool(c0, "acilistaBasla") && !Bool(c0, "kaldirirkenVeriyiSil");
                var c1 = Cevap("{\"type\":\"set-shell-pref\",\"key\":\"bildirimler\",\"value\":false}");
                var ok1 = !Bool(c1, "bildirimler") && !bildirim;
                var c2 = Cevap("{\"type\":\"set-shell-pref\",\"key\":\"acilistaBasla\",\"value\":false}");
                var ok2 = !Bool(c2, "acilistaBasla") && !acilis;
                var c3 = Cevap("{\"type\":\"set-shell-pref\",\"key\":\"kaldirirkenVeriyiSil\",\"value\":true}");
                var ok3 = !Bool(c3, "kaldirirkenVeriyiSil") && !KabukTercihleri.Oku(dir).KaldirirkenVeriyiSil;     // onaysız: değişmez
                var c4 = Cevap("{\"type\":\"set-shell-pref\",\"key\":\"kaldirirkenVeriyiSil\",\"value\":true,\"confirmed\":true}");
                var ok4 = Bool(c4, "kaldirirkenVeriyiSil") && KabukTercihleri.Oku(dir).KaldirirkenVeriyiSil;
                var c5 = Cevap("{\"type\":\"set-shell-pref\",\"key\":\"kaldirirkenVeriyiSil\",\"value\":false}");
                var ok5 = !Bool(c5, "kaldirirkenVeriyiSil");                                                      // kapatmak onay istemez
                var c6 = Cevap("{\"type\":\"set-shell-pref\",\"key\":\"bilinmeyen\",\"value\":true}");
                var ok6 = !Bool(c6, "bildirimler") && !Bool(c6, "acilistaBasla");
                return C(okGet && ok1 && ok2 && ok3 && ok4 && ok5 && ok6, $"get={okGet} 1={ok1} 2={ok2} 3={ok3} 4={ok4} 5={ok5} 6={ok6}");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        T("odaklama zinciri: /api/live/focus -> Focuser -> kabuk eşleştirici (proje yolu, başlık, tabSelected, ipucu)", () => Task.Run(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "soz-e7f-" + Guid.NewGuid().ToString("N")[..8]);
            var eskiF = SessionActions.Focuser; var eskiD = SessionActions.HasDesktop; var eskiA = CallerIdentity.AncestorCheck;
            try
            {
                Directory.CreateDirectory(dir);
                CallerIdentity.AncestorCheck = false;
                SessionActions.HasDesktop = () => true;
                (long? Hwnd, int? Pid, string? Klasor, string? Baslik) gelen = default; string? balon = null;
                var tur = "sekme";
                SessionActions.Focuser = OdakBaglayici.Olustur(
                    (hwnd, pid, klasor, baslik) => { gelen = (hwnd, pid, klasor, baslik); return new PencereOnuneGetirici.Sonuc(tur != "yok", tur, "m", tur == "sekme" ? "2 sekme benziyor" : null, tur == "yok" ? null : 777, []); },
                    null, ip => balon = ip);
                await using var h = await SozServer.StartAsync(dir, BosBaglanti(), autoScan: false);
                using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
                http.DefaultRequestHeaders.Add(HookInstallerTokenBaslik, h.Token);

                var r1 = await http.PostAsJsonAsync("/api/live/focus", new { pid = 4242, projectPath = @"C:\Proje X", title = "Oturum başlığı" });
                var b1 = JsonNode.Parse(await r1.Content.ReadAsStringAsync())!;
                var ok1 = r1.StatusCode == HttpStatusCode.OK && gelen.Pid == 4242 && gelen.Klasor == @"C:\Proje X" && gelen.Baslik == "Oturum başlığı"
                    && (bool)b1["tabSelected"]! && (string?)b1["hint"] == "2 sekme benziyor" && balon == "2 sekme benziyor";

                tur = "pencere"; balon = null;
                var r2 = await http.PostAsJsonAsync("/api/live/focus", new { pid = 4242 });
                var b2 = JsonNode.Parse(await r2.Content.ReadAsStringAsync())!;
                var ok2 = r2.StatusCode == HttpStatusCode.OK && !(bool)b2["tabSelected"]! && balon == null;

                tur = "yok";
                var r3 = await http.PostAsJsonAsync("/api/live/focus", new { pid = 4242 });
                var ok3 = r3.StatusCode == HttpStatusCode.NotFound;
                return C(ok1 && ok2 && ok3, $"ok1={ok1} ok2={ok2} ok3={ok3} gelen={gelen} b1={b1.ToJsonString()}");
            }
            finally
            {
                SessionActions.Focuser = eskiF; SessionActions.HasDesktop = eskiD; CallerIdentity.AncestorCheck = eskiA;
                try { Directory.Delete(dir, true); } catch { }
            }
        }).GetAwaiter().GetResult());

        T("tercih köprüsü: /api/shell/prefs kabuğun tercihlerini okur ve yazar (yalıtılmış, onaysız silme açılmaz)", () => Task.Run(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "soz-e7p-" + Guid.NewGuid().ToString("N")[..8]);
            var eskiA = CallerIdentity.AncestorCheck; var eskiR = ShellBridge.Read; var eskiW = ShellBridge.Write;
            try
            {
                Directory.CreateDirectory(dir);
                CallerIdentity.AncestorCheck = false;
                var acilis = true; var bildirim = true; var degisti = 0;
                var kopru = new TercihKopru(dir, () => acilis, _ => { acilis = true; return true; }, () => acilis = false, () => bildirim, b => bildirim = b, () => degisti++);
                kopru.Bagla();
                await using var h = await SozServer.StartAsync(dir, BosBaglanti(), autoScan: false);
                using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
                http.DefaultRequestHeaders.Add(HookInstallerTokenBaslik, h.Token);

                var g = JsonNode.Parse(await http.GetStringAsync("/api/shell/prefs"))!;
                var okG = (bool)g["available"]! && (bool)g["startWithWindows"]! && (bool)g["notifications"]! && !(bool)g["deleteDataOnUninstall"]!;
                var p1 = await http.PutAsJsonAsync("/api/shell/prefs", new { startWithWindows = false, notifications = false });
                var okP1 = p1.StatusCode == HttpStatusCode.OK && !acilis && !bildirim && degisti == 1;
                var p2 = await http.PutAsJsonAsync("/api/shell/prefs", new { deleteDataOnUninstall = true });
                var okP2 = p2.StatusCode == HttpStatusCode.BadRequest && !KabukTercihleri.Oku(dir).KaldirirkenVeriyiSil;
                var p3 = await http.PutAsJsonAsync("/api/shell/prefs", new { deleteDataOnUninstall = true, confirmed = true });
                var okP3 = p3.StatusCode == HttpStatusCode.OK && KabukTercihleri.Oku(dir).KaldirirkenVeriyiSil;
                return C(okG && okP1 && okP2 && okP3, $"get={okG} put1={okP1} put2={okP2} put3={okP3}");
            }
            finally
            {
                CallerIdentity.AncestorCheck = eskiA; ShellBridge.Read = eskiR; ShellBridge.Write = eskiW;
                try { Directory.Delete(dir, true); } catch { }
            }
        }).GetAwaiter().GetResult());
    }

    private const string HookInstallerTokenBaslik = "X-Soz-Token";
}
