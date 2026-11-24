using System.Text.Json.Nodes;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// k6: hızlı kip (http kancası) yalnız Ayarlar'dan açılınca; Söz açılırken kancalar http'ye, düzgün kapanırken komuta döner.
/// Her geçiş yalnız bizim girdilerimize dokunur ve kapanışta dosya bayt bayt komut kurulumuna geri gelir.
/// </summary>
public class KancaHizliKipTests
{
    private const string Exe = @"C:\x\soz-kanca.exe";

    private static readonly string[] HttpOlaylari = { "UserPromptSubmit", "Stop", "StopFailure", "PreToolUse" };

    private static ServerFileInfo? Live(string _) => new("http://127.0.0.1:49321", 1234, "x", true);
    private static Version? V() => new(2, 1, 330);

    private static (string path, string original, string projectRoot) Kur(TestEnv env, string userSettingsText)
    {
        var root = Path.Combine(env.Root, "proje-" + Guid.NewGuid().ToString("N")[..6]);
        var settings = Path.Combine(root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, userSettingsText);
        new WorkService(env.Svc.Db).EnsureProjectByPath(root);
        HookInstaller.Install(settings, Exe, env.DataDir); // komut yolu
        return (settings, File.ReadAllText(settings), root);
    }

    private static JsonNode Parse(string path) => JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;

    private static string TypeOf(JsonNode root, string ev) => (string)root["hooks"]![ev]![0]!["hooks"]![0]!["type"]!;

    [Fact]
    public void Http_ye_alir_komuta_geri_cevirir_bayt_bayt_ayni_dosyayi_verir()
    {
        using var env = new TestEnv(copySamples: false);
        var (path, original, _) = Kur(env, "{\n  // benim notum\n  \"model\": \"opus\", /* blok */\n  \"env\": { \"A\": \"1\" }\n}\n");
        var before = Directory.GetFiles(Path.GetDirectoryName(path)!).OrderBy(x => x).ToArray();

        var up = HookTransportSwitch.Retarget(env.Svc.Db, Exe, toHttp: true, userHome: null, versionProbe: V, serverReader: Live);
        Assert.Equal(1, up.Scanned);
        Assert.Equal(new[] { path }, up.Changed);
        var http = Parse(path);
        foreach (var ev in HttpOlaylari) Assert.Equal("http", TypeOf(http, ev));
        Assert.Equal("command", TypeOf(http, "SessionStart"));
        Assert.Equal("command", TypeOf(http, "Notification"));
        Assert.Contains("// benim notum", File.ReadAllText(path)); // yorumlar korunur
        Assert.Contains("/* blok */", File.ReadAllText(path));

        var down = HookTransportSwitch.Retarget(env.Svc.Db, Exe, toHttp: false, userHome: null);
        Assert.Equal(new[] { path }, down.Changed);
        Assert.Equal(original, File.ReadAllText(path)); // BAYT BAYT önceki komut kurulumu
        Assert.Equal(before, Directory.GetFiles(Path.GetDirectoryName(path)!).OrderBy(x => x).ToArray()); // geçişler yedek dosyası bırakmaz
    }

    [Fact]
    public void Hizli_kip_kosullari_tutmazsa_http_e_hic_dokunmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var (path, original, _) = Kur(env, "{}");
        // Söz kapalı, sürüm okunamıyor ya da eski: komutta kalır
        Assert.Empty(HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, V, _ => null).Changed);
        Assert.Empty(HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, () => null, Live).Changed);
        Assert.Empty(HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, () => new Version(2, 1, 294), Live).Changed);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Baska_veri_klasorune_ait_kurulum_ve_kullanici_kancalari_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "baskasi");
        var settings = Path.Combine(root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{}");
        new WorkService(env.Svc.Db).EnsureProjectByPath(root);
        HookInstaller.Install(settings, Exe, Path.Combine(env.Root, "baska-veri")); // başka Söz veri klasörü
        var original = File.ReadAllText(settings);
        Assert.Empty(HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, V, Live).Changed);
        Assert.Equal(original, File.ReadAllText(settings));

        // kullanıcının kendi kancası olan ama bizimki olmayan dosya taranmaz bile
        var plain = Path.Combine(env.Root, "duz");
        var ps = Path.Combine(plain, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ps)!);
        var mine = "{\n  \"hooks\": { \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"echo benim\" } ] } ] }\n}\n";
        File.WriteAllText(ps, mine);
        new WorkService(env.Svc.Db).EnsureProjectByPath(plain);
        var r = HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, V, Live);
        Assert.Empty(r.Changed);
        Assert.Equal(mine, File.ReadAllText(ps));
    }

    [Fact]
    public void Gecersiz_jetonlu_http_girdisine_dokunulmaz_ve_adres_degisince_yenilenir()
    {
        using var env = new TestEnv(copySamples: false);
        var (path, _, _) = Kur(env, "{}");
        // Söz önce 49300'de açıldı, kancalar http'ye alındı
        HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, V, s => new ServerFileInfo("http://127.0.0.1:49300", 1, "x", true));
        Assert.Contains(":49300/api/hooks/stop", File.ReadAllText(path));
        // sonra başka kapıda açıldı: adres yenilenir
        var moved = HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, V, s => new ServerFileInfo("http://127.0.0.1:49311", 1, "x", true));
        Assert.Equal(new[] { path }, moved.Changed);
        Assert.DoesNotContain(":49300/", File.ReadAllText(path));
        Assert.Contains(":49311/api/hooks/stop", File.ReadAllText(path));
        // aynı adres: değişiklik yok
        Assert.Empty(HookTransportSwitch.Retarget(env.Svc.Db, Exe, true, null, null, V, s => new ServerFileInfo("http://127.0.0.1:49311", 1, "x", true)).Changed);

        // jetonu bizimkine uymayan http girdisi (başka bir Söz) geri çevrilmez
        var other = File.ReadAllText(path).Replace(HookToken.GetOrCreate(env.DataDir), "baska-jeton-baska-jeton-baska");
        File.WriteAllText(path, other);
        Assert.Empty(HookTransportSwitch.Retarget(env.Svc.Db, Exe, false, null).Changed);
        Assert.Equal(other, File.ReadAllText(path));
    }

    [Fact]
    public async Task Sunucu_duzgun_kapanirken_http_kancalar_komuta_doner()
    {
        using var env = new TestEnv(copySamples: false);
        var (path, _, _) = Kur(env, "{\n  \"model\": \"opus\"\n}\n");
        // sunucunun çözdüğü kanca programı: soz.exe'nin yanındaki soz-kanca.exe
        var bin = Path.Combine(env.Root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "soz.exe"), "x");
        var kanca = Path.Combine(bin, "soz-kanca.exe");
        File.WriteAllText(kanca, "x");
        HookInstaller.Install(path, kanca, env.DataDir);
        var original = File.ReadAllText(path);
        HookTransportSwitch.Retarget(env.Svc.Db, kanca, true, null, null, V, Live);
        Assert.Equal("http", TypeOf(Parse(path), "Stop"));

        var h = await SozServer.StartAsync(env.DataDir, 49553, default, autoScan: false, configureRuns: r => r.SozExePath = Path.Combine(bin, "soz.exe"));
        await h.DisposeAsync();
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Kurulum_Ayar_Kapaliyken_Komut_Yolunu_Kurar_Hizli_Kipte_Http()
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "kurulum");
        Directory.CreateDirectory(root);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(root);
        // Ayar kapalı (varsayılan): komut yolu
        var fake = Path.Combine(env.Root, "soz.exe");
        File.WriteAllText(fake, "x");
        var res = w.InstallTerminalHooks(pid, fake);
        Assert.All(res.Transport!.Values, v => Assert.Equal("command", v));
        Assert.False(SozSettings.Load(env.Svc.Db).Bool("hooksFastMode"));
        Assert.Equal(false, SozSettings.Defaults["hooksFastMode"]!.GetValue<bool>());
    }
}
