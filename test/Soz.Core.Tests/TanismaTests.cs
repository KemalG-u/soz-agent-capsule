using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Onboarding;
using Soz.Core.Registry;
using Soz.Server;
using Xunit;
using static Soz.Core.Tests.RegistryTestKit;

namespace Soz.Core.Tests;

/// <summary>
/// d2e Tanışma. Bütün testler `_gecici\d2e\sahte-ev\` altındaki SENTETİK ağaçla koşar. Kullanıcının gerçek kayıt klasörlerine,
/// tarayıcı profillerine ve kayıt defterine DOKUNMAZ: kökler parametredir, sistem okuması <see cref="FakeProbe"/> arkasındadır.
/// </summary>
internal sealed class SahteEv : IDisposable
{
    /// <summary>Sentetik kayıtlara ve ayar dosyalarına konan işaret: veritabanında ya da API cevabında görünürse keşif içerik okumuş demektir.</summary>
    public const string Marker = "GIZLI-ISARET-7731";
    public const string FakeToken = "sk-ant-SAHTE0123456789ABCDEFGHIJ";
    public static readonly DateTime Stamp = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    public string Root { get; }
    public string Home { get; }
    public string DataDir => Path.Combine(Root, "veri");
    public SozDb Db { get; }
    public OnboardingService Svc { get; }

    public SahteEv(string name, bool build = true)
    {
        Time.UtcNow = () => new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        Root = Path.Combine(TestEnv.RepoRoot, "_gecici", "d2e", "sahte-ev", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Home = Path.Combine(Root, "ev");
        Directory.CreateDirectory(Home);
        Db = SozDb.OpenAndMigrate(DataDir);
        Svc = new OnboardingService(Db);
        if (build) Build(Home);
    }

    public string Npm(string home) => Path.Combine(home, "AppData", "Roaming", "npm");

    public void File(string home, string rel, string content = "")
    {
        var p = Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        System.IO.File.SetLastWriteTimeUtc(p, Stamp);
    }

    public void Dir(string home, string rel) => Directory.CreateDirectory(Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar)));

    public static string EncodedHome(string home) => Regex.Replace(home.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");

    /// <summary>Tam örnek ağaç: 4 komut satırı aracı izi, kayıt klasörleri, iki proje, tarayıcılar ve tarayıcı profili tuzağı.</summary>
    public void Build(string home, bool withCodex = true)
    {
        // Araçlar (npm kısayolları + sürüm künyesi; "description" alanına işaret konur: okunmamalı)
        File(home, "AppData/Roaming/npm/claude.cmd", "@echo off\r\n");
        File(home, "AppData/Roaming/npm/claude.ps1", "# kısayol");
        File(home, "AppData/Roaming/npm/node_modules/@anthropic-ai/claude-code/package.json", $$"""{"name":"@anthropic-ai/claude-code","version":"2.1.322","description":"{{Marker}}"}""");
        if (withCodex)
        {
            File(home, "AppData/Roaming/npm/codex.cmd", "@echo off\r\n");
            File(home, "AppData/Roaming/npm/node_modules/@openai/codex/package.json", $$"""{"name":"@openai/codex","version":"0.130.0","description":"{{Marker}}"}""");
        }
        File(home, ".local/bin/hermes.exe", "MZ sahte " + Marker);

        // Claude kayıtları: iki proje (biri ev klasörü altında), bir boş klasör. Kayıt satırlarına işaret ve sahte anahtar konur.
        File(home, ".claude/projects/C--sahte-proje-bir/a.jsonl", $$"""{"type":"user","message":"{{Marker}}"}""");
        File(home, ".claude/projects/C--sahte-proje-bir/b.jsonl", $$"""{"type":"user","message":"{{Marker}} {{FakeToken}}"}""");
        File(home, ".claude/projects/C--sahte-proje-bir/alt/x.jsonl", Marker);
        File(home, ".claude/projects/" + EncodedHome(home) + "-proje-iki/c.jsonl", Marker);
        Dir(home, ".claude/projects/bos-klasor");
        File(home, ".claude/.credentials.json", $$"""{"accessToken":"{{FakeToken}}"}""");

        // Codex kayıtları: 3 yeni gün dosyası, 1 eski; kimlik dosyası.
        if (withCodex)
        {
            File(home, ".codex/sessions/2026/11/09/rollout-a.jsonl", Marker);
            File(home, ".codex/sessions/2026/11/09/rollout-b.jsonl", Marker);
            File(home, ".codex/sessions/2026/11/10/rollout-c.jsonl", Marker);
            File(home, ".codex/sessions/2026/08/01/rollout-d.jsonl", Marker);
            File(home, ".codex/auth.json", $$"""{"tokens":"{{FakeToken}}"}""");
        }

        // Hermes: veritabanı + canlılık dosyası (içeriği açılmaz). OpenCode: yalnız veritabanı, çalıştırma dosyası YOK (iz).
        File(home, ".hermes/state.db", "SQLite format 3\0" + Marker);
        File(home, ".hermes/runtime/active_sessions.json", $$"""{"x":"{{Marker}}"}""");
        File(home, ".local/share/opencode/opencode.db", "SQLite format 3\0" + Marker);

        // Tarayıcılar (program klasörleri) ve tarayıcı profili tuzağı (AÇILMAMALI).
        File(home, "Program Files/Google/Chrome/Application/chrome.exe", "MZ sahte");
        File(home, "Program Files (x86)/Mozilla Firefox/firefox.exe", "MZ sahte");
        File(home, "AppData/Local/Google/Chrome/User Data/Default/History", Marker);
        File(home, "AppData/Local/Google/Chrome/User Data/Default/Login Data", Marker);
    }

    public FakeProbe Probe(string home) => new()
    {
        Browsers =
        {
            new ProbeBrowser("Google Chrome", Path.Combine(home, "Program Files", "Google", "Chrome", "Application", "chrome.exe"), "kayıt defteri (sahte)"),
            new ProbeBrowser("Opera Stable", Path.Combine(home, "Program Files", "Opera", "opera.exe"), "kayıt defteri (sahte)"), // dosyası yok → iz
        },
        Default = new DefaultBrowserResult(true, new ProbeDefaultBrowser("ChromeHTML", "chrome.exe")),
        Scale = 125,
        Lang = "tr-TR",
    };

    public ScanContext Ctx(string? home = null, ISystemProbe? probe = null, IEnumerable<string>? path = null)
    {
        home ??= Home;
        return new ScanContext { Roots = OnboardingRoots.Under(home, path), Probe = probe ?? Probe(home) };
    }

    public ScanOutcome Scan(ScanContext? ctx = null) => Svc.Scan(ctx ?? Ctx());

    /// <summary>Veritabanındaki bütün metin: öneri satırları, kanıtlar ve olay defteri yükleri. Sızıntı taraması buna bakar.</summary>
    public string DumpDb()
    {
        using var c = Db.Open();
        var sb = new StringBuilder();
        foreach (var r in SuggestionStore.List(c, null, true)) sb.AppendLine($"{r.Id}|{r.Kind}|{r.Body.ToJsonString()}|{r.Evidence.ToJsonString()}|{r.CreatedAt}|{r.DismissedAt}");
        foreach (var p in c.Query("SELECT kind, actor, payload FROM event_log ORDER BY seq", r => $"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}")) sb.AppendLine(p);
        foreach (var p in c.Query("SELECT id, approved_sha256 FROM registry_state", r => r.GetString(0))) sb.AppendLine(p);
        return sb.ToString();
    }

    public long Count(string sql) { using var c = Db.Open(); return c.ScalarLong(sql); }

    public void Dispose()
    {
        Time.UtcNow = () => DateTime.UtcNow;
        SqliteConnection.ClearAllPools();
    }
}

internal sealed class FakeProbe : ISystemProbe
{
    public List<ProbeBrowser> Browsers { get; } = new();
    public DefaultBrowserResult Default { get; set; } = DefaultBrowserResult.Unreadable;
    public int? Scale { get; set; }
    public string? Lang { get; set; }
    IReadOnlyList<ProbeBrowser> ISystemProbe.Browsers() => Browsers;
    public DefaultBrowserResult DefaultBrowser() => Default;
    public int? DisplayScalePercent() => Scale;
    public string? Language() => Lang;
}

public class TanismaTests
{
    private static JsonObject Item(JsonObject view, string id)
        => view["groups"]!.AsArray().SelectMany(g => g!["items"]!.AsArray()).Select(i => i!.AsObject()).Single(i => (string?)i["id"] == id);

    private static bool Has(JsonObject view, string id)
        => view["groups"]!.AsArray().SelectMany(g => g!["items"]!.AsArray()).Any(i => (string?)i!["id"] == id);

    private static List<string> Ids(JsonObject view)
        => view["groups"]!.AsArray().SelectMany(g => g!["items"]!.AsArray()).Select(i => (string)i!["id"]!).ToList();

    // ---------- Beklenen bulgular ----------

    [Fact]
    public void SentetikAgac_BeklenenBulgularCikar()
    {
        using var ev = new SahteEv("bulgu");
        var o = ev.Scan();
        Assert.True(o.Found >= 14, $"bulunan {o.Found}");
        Assert.Equal(o.Found, o.New);
        var v = ev.Svc.View();

        var claude = Item(v, "tool:claude");
        Assert.Equal("installed", (string?)claude["confidence"]);
        Assert.Equal("2.1.322", (string?)claude["facts"]!["version"]);
        Assert.Equal(1, (int)claude["count"]!);
        Assert.Equal("0.130.0", (string?)Item(v, "tool:codex")["facts"]!["version"]);

        var hermes = Item(v, "tool:hermes");                         // .exe var ama sürüm künyesi okunamadı: kurulu, sürüm yok
        Assert.Equal("installed", (string?)hermes["confidence"]);
        Assert.Null(hermes["facts"]!["version"]);
        Assert.Contains("okuyamadım", (string?)hermes["detail"]);

        var opencode = Item(v, "tool:opencode");                     // yalnız veritabanı var: iz, kurulu DEĞİL
        Assert.Equal("trace", (string?)opencode["confidence"]);
        Assert.Contains("doğrulayamadım", (string?)opencode["detail"]);
        Assert.False(Has(v, "tool:gemini"));                         // izi de yok: listelenmez

        Assert.Equal(3, (int)Item(v, "records:claude")["count"]!);   // 2 proje + 1 boş klasör
        var codex = Item(v, "records:codex");
        Assert.Equal(4, (int)codex["facts"]!["sessionFiles"]!);
        Assert.Equal(3, (int)codex["facts"]!["recentSessionFiles"]!);
        Assert.Equal("2026-11-10", (string?)codex["facts"]!["lastDay"]);
        Assert.True((bool)Item(v, "records:hermes")["facts"]!["liveFile"]!);
        Assert.True(Has(v, "records:opencode"));

        // Proje etkinliği: yalnız .jsonl dosya SAYISI (alt klasördekiler ve boş klasör sayılmaz); ev klasörü "~" olur.
        var p1 = Item(v, "project:claude:C--sahte-proje-bir");
        Assert.Equal(2, (int)p1["count"]!);
        Assert.Equal("2026-11-10", (string?)p1["facts"]!["lastDay"]);
        var p2 = Item(v, "project:claude:~-proje-iki");
        Assert.Equal("~-proje-iki", (string?)p2["title"]);
        Assert.Equal(1, (int)p2["count"]!);
        Assert.False(Has(v, "project:claude:bos-klasor"));

        // Tarayıcılar
        Assert.Equal("installed", (string?)Item(v, "browser:chrome")["confidence"]);
        Assert.True((bool)Item(v, "browser:chrome")["facts"]!["isDefault"]!);
        Assert.Equal("installed", (string?)Item(v, "browser:firefox")["confidence"]);     // yalnız program klasöründen bulundu
        Assert.False((bool)Item(v, "browser:firefox")["facts"]!["isDefault"]!);
        Assert.Equal("trace", (string?)Item(v, "browser:opera")["confidence"]);           // kayıtta var, dosyası yok
        Assert.Equal("Varsayılan tarayıcın Google Chrome", (string?)Item(v, "browser:default")["title"]);
        Assert.Equal(125, (int)Item(v, "display:scale")["count"]!);
        Assert.Equal("tr-TR", (string?)Item(v, "language:system")["facts"]!["locale"]);
    }

    [Fact]
    public void BosEv_AltiSabitSatirDurur_HepsiSifir()
    {
        using var ev = new SahteEv("bos", build: false);
        var o = ev.Scan(ev.Ctx(probe: new NullSystemProbe()));
        Assert.Equal(0, o.Found);
        Assert.Equal(new[] { "Varsayılan tarayıcı", "Ekran ölçeği", "Sistem dili" }, o.CouldNotRead);
        var v = ev.Svc.View();
        var groups = v["groups"]!.AsArray();
        Assert.Equal(new[] { "tools", "records", "projects", "browsers", "display", "language" }, groups.Select(g => (string)g!["key"]!).ToArray());
        Assert.All(groups, g => Assert.Equal(0, (int)g!["count"]!));
        Assert.Contains("Yapay zekâ araçların", groups[0]!["title"]!.ToString());
    }

    [Fact]
    public void AyniKurulumIkiKezSayilmaz_IkiAyriKurulumIkiSayilir_PathDisiBilinenKlasorlerdeBulunur()
    {
        using var ev = new SahteEv("kurulum");
        ev.File(ev.Home, ".local/bin/claude.exe", "MZ sahte");                               // ikinci ve ayrı kurulum
        ev.File(ev.Home, "AppData/Roaming/npm/claude.exe", "MZ sahte");                       // aynı klasör, aynı ad: .cmd ile TEK kurulum
        ev.File(ev.Home, ".bun/bin/aider.exe", "MZ sahte");                                   // PATH'te DEĞİL ama bilinen klasör
        ev.Scan();
        var v = ev.Svc.View();
        Assert.Equal(2, (int)Item(v, "tool:claude")["count"]!);
        Assert.Contains("2 ayrı kurulum", (string?)Item(v, "tool:claude")["detail"]);
        Assert.Equal(1, (int)Item(v, "tool:codex")["count"]!);
        var aider = Item(v, "tool:aider");
        Assert.False((bool)aider["facts"]!["installs"]![0]!["inPath"]!);
    }

    // ---------- Onaysız hiçbir şey etkin değil ----------

    [Fact]
    public void HicbirBulguOnaysizEtkinDegil_KabulBileEtkinlestirmez()
    {
        using var ev = new SahteEv("onaysiz");
        ev.Scan();
        using (var c = ev.Db.Open())
        {
            var rows = SuggestionStore.List(c, OnboardingService.KindPrefix, true);
            Assert.NotEmpty(rows);
            Assert.All(rows, r =>
            {
                Assert.Equal("suggested", (string?)r.Body["state"]);
                Assert.Equal("system", (string?)r.Body["owner"]);
                Assert.Null(r.DismissedAt);
                Assert.StartsWith("tanisma.", r.Kind);
                Assert.StartsWith("tanisma:", r.Id);
            });
        }
        Assert.Equal(0, ev.Count("SELECT COUNT(*) FROM registry_state"));
        Assert.Equal(0, ev.Count("SELECT COUNT(*) FROM settings_value"));
        Assert.Equal(0, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind LIKE 'kullanici.%'"));
        Assert.Equal(1, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind='kesif.bitti'"));
        Assert.Equal(ev.Svc.View()["total"]!.GetValue<int>(), ev.Svc.View()["pending"]!.GetValue<int>());

        ev.Svc.Accept("tool:claude");                                                          // kişi onayladı: yalnız durum işareti, hiçbir şey etkinleşmez
        Assert.Equal(0, ev.Count("SELECT COUNT(*) FROM registry_state"));
        Assert.Equal(0, ev.Count("SELECT COUNT(*) FROM settings_value"));
    }

    // ---------- Gizlilik ----------

    [Fact]
    public void IcerikSizintisiYok_VeritabaniVeApiCevabindaIsaretMetniYok_VeDosyaKapisiYalnizKunyeOkur()
    {
        using var ev = new SahteEv("sizinti");
        var ctx = ev.Ctx();
        ev.Svc.Scan(ctx);
        var view = ev.Svc.View(includeDismissed: true);
        var all = new StringBuilder(ev.DumpDb()).Append(view.ToJsonString());
        foreach (var id in Ids(view)) all.Append(ev.Svc.Why(id).ToJsonString());
        ev.Svc.Accept("tool:claude");
        ev.Svc.Change("browser:default", "Firefox");
        ev.Svc.Dismiss("browser:opera");
        all.Append(ev.DumpDb());

        var text = all.ToString();
        Assert.DoesNotContain(SahteEv.Marker, text);
        Assert.DoesNotContain("sk-ant", text);
        Assert.DoesNotContain(SahteEv.FakeToken, text);
        Assert.DoesNotContain(ev.Home, text, StringComparison.OrdinalIgnoreCase);              // ev klasörü yalnız "~" olarak görünür
        Assert.Contains("~", text);

        // Dosya kapısı: içerik okuma yalnız package.json "version" ve .exe künyesi. Kayıt, kimlik, veritabanı ve tarayıcı profili açılmadı.
        var reads = ctx.Fs.Log.Where(l => l.Op.StartsWith("read")).ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, l =>
        {
            if (l.Op == "read-version") Assert.EndsWith("package.json", l.Path);
            else { Assert.Equal("read-exe-version", l.Op); Assert.EndsWith(".exe", l.Path); }
        });
        Assert.All(ctx.Fs.Log, l => Assert.Contains(l.Op, new[] { "exists", "list", "stat", "resolve", "read-version", "read-exe-version" }));
        Assert.DoesNotContain(ctx.Fs.Log, l => l.Path.Contains("User Data", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ctx.Fs.Log, l => l.Path.EndsWith("auth.json") || l.Path.EndsWith(".credentials.json") || l.Path.EndsWith("History"));
        Assert.DoesNotContain(ctx.Fs.Log, l => l.Op.StartsWith("read") && (l.Path.EndsWith(".jsonl") || l.Path.EndsWith(".db")));
    }

    [Fact]
    public void SurumAlani_YalnizSurumKalibinaUyarsaAlinir_BaskaMetinAtilir()
    {
        using var ev = new SahteEv("surum");
        ev.File(ev.Home, "AppData/Roaming/npm/node_modules/@anthropic-ai/claude-code/package.json", $$"""{"version":"{{SahteEv.Marker}} gizli metin"}""");
        ev.Scan();
        var claude = Item(ev.Svc.View(), "tool:claude");
        Assert.Null(claude["facts"]!["version"]);
        Assert.DoesNotContain(SahteEv.Marker, ev.DumpDb());
    }

    [Fact]
    public void SirGorunumluAdliBulgu_YazilmazVeSayilir()
    {
        using var ev = new SahteEv("sir");
        ev.File(ev.Home, ".claude/projects/sk-ABCDEFGHIJKLMNOPQRST/x.jsonl", "a");
        var o = ev.Scan();
        Assert.Equal(1, o.DroppedAsSecret);
        Assert.DoesNotContain("sk-ABCDEF", ev.DumpDb());
    }

    [Fact]
    public void AgYoluHicAcilmaz()
    {
        using var ev = new SahteEv("ag");
        var ctx = ev.Ctx(path: new[] { @"\\sunucu\paylasim\bin", Path.Combine(ev.Home, "AppData", "Roaming", "npm") });
        var o = ev.Svc.Scan(ctx);
        Assert.DoesNotContain(ctx.Fs.Log, l => l.Path.StartsWith(@"\\"));
        Assert.Equal(0, o.Unreadable);
        Assert.True(ev.Svc.View()["total"]!.GetValue<int>() > 0);
    }

    // ---------- Tekrar, kararlılık ----------

    [Fact]
    public void IkinciKesif_Cogalmaz_SatirlarAynenKalir()
    {
        using var ev = new SahteEv("tekrar");
        var first = ev.Scan();
        var before = ev.DumpDb().Split('\n').Where(l => l.StartsWith("tanisma:")).ToList();
        var countBefore = ev.Count("SELECT COUNT(*) FROM registry_suggestion");
        var second = ev.Scan();
        var after = ev.DumpDb().Split('\n').Where(l => l.StartsWith("tanisma:")).ToList();
        Assert.Equal(countBefore, ev.Count("SELECT COUNT(*) FROM registry_suggestion"));
        Assert.Equal(before, after);
        Assert.Equal(0, second.New);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Gone);
        Assert.Equal(first.Found, second.Unchanged);
        Assert.Equal(2, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind='kesif.bitti'"));
    }

    [Fact]
    public void DegisenBulgu_GuncellenirAmaKisininKarariKorunur()
    {
        using var ev = new SahteEv("karar");
        ev.Scan();
        ev.Svc.Accept("tool:claude");
        ev.Svc.Change("browser:default", "Firefox");
        ev.File(ev.Home, "AppData/Roaming/npm/node_modules/@anthropic-ai/claude-code/package.json", """{"version":"2.1.400"}""");   // sürüm güncellendi
        var o = ev.Scan();
        Assert.Equal(1, o.Updated);
        var v = ev.Svc.View();
        var claude = Item(v, "tool:claude");
        Assert.Equal("2.1.400", (string?)claude["facts"]!["version"]);
        Assert.Equal("accepted", (string?)claude["state"]);                                   // onay ezilmedi
        var def = Item(v, "browser:default");
        Assert.Equal("changed", (string?)def["state"]);
        Assert.Equal("Firefox", (string?)def["userValue"]);                                   // düzeltme korundu
        Assert.Equal(1, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind='kullanici.onay'"));
        Assert.Equal(1, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind='kullanici.duzeltme'"));
        Assert.DoesNotContain("Firefox", ev.DumpDb().Split('\n').Single(l => l.StartsWith("kullanici.duzeltme")));   // olay yükünde değer yok

        ev.Svc.Accept("browser:default");                                                     // bulunanı olduğu gibi onayla: düzeltme silinir
        Assert.Null(Item(ev.Svc.View(), "browser:default")["userValue"]);
    }

    [Fact]
    public void BirDahaGosterme_Kalicidir_KesifTekrarKosunca_VeBulguDegisseDe_GeriAcilmaz()
    {
        using var ev = new SahteEv("kapat");
        ev.Scan();
        ev.Svc.Dismiss("browser:firefox");
        Assert.False(Has(ev.Svc.View(), "browser:firefox"));
        Assert.Equal("dismissed", (string?)Item(ev.Svc.View(true), "browser:firefox")["state"]);
        string Row() => ev.DumpDb().Split('\n').Single(l => l.StartsWith("tanisma:browser:firefox"));
        var before = Row();

        ev.File(ev.Home, "AppData/Local/Mozilla Firefox/firefox.exe", "MZ sahte");           // bulgu değişti (ikinci yol)
        var o = ev.Scan();
        Assert.True(o.KeptDismissed >= 1);
        Assert.Equal(before, Row());
        Assert.False(Has(ev.Svc.View(), "browser:firefox"));
        Assert.Throws<SozException>(() => ev.Svc.Accept("browser:firefox"));                  // kapalıyken kabul edilemez
        Assert.Equal(1, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind='kullanici.izleme-kapat'"));

        ev.Svc.Dismiss("browser:firefox");                                                    // ikinci kez kapatmak olay çoğaltmaz
        Assert.Equal(1, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind='kullanici.izleme-kapat'"));

        ev.Svc.Restore("browser:firefox");                                                    // kapatmak silmek değildir: geri gelir
        Assert.True(Has(ev.Svc.View(), "browser:firefox"));
        ev.Scan();                                                                            // geri gelince güncellenir
        Assert.Equal("installed", (string?)Item(ev.Svc.View(), "browser:firefox")["confidence"]);
        Assert.Equal("suggested", (string?)Item(ev.Svc.View(), "browser:firefox")["state"]);
    }

    [Fact]
    public void Kaybolan_PresentFalseOlur_GeriGelinceDuzelir_KararKorunur()
    {
        using var ev = new SahteEv("kaybol");
        ev.Scan();
        ev.Svc.Accept("tool:codex");
        var home2 = Path.Combine(ev.Root, "ev2");                                              // codex'siz ikinci ağaç, aynı veritabanı
        Directory.CreateDirectory(home2);
        ev.Build(home2, withCodex: false);
        var o = ev.Svc.Scan(ev.Ctx(home2));
        Assert.True(o.Gone >= 2);                                                              // tool:codex ve records:codex
        var v = ev.Svc.View();
        Assert.False((bool)Item(v, "tool:codex")["present"]!);
        Assert.Equal("accepted", (string?)Item(v, "tool:codex")["state"]);
        Assert.True((bool)Item(v, "tool:claude")["present"]!);

        ev.Scan();                                                                             // codex geri geldi
        Assert.True((bool)Item(ev.Svc.View(), "tool:codex")["present"]!);
        Assert.Equal("accepted", (string?)Item(ev.Svc.View(), "tool:codex")["state"]);
    }

    [Fact]
    public void Projeler_EnYeniyeGore_SayisiSinirlanmaz()
    {
        using var ev = new SahteEv("cok", build: false);
        for (var i = 0; i < 25; i++)
        {
            ev.File(ev.Home, $".claude/projects/proje-{i:D2}/a.jsonl", "x");
            System.IO.File.SetLastWriteTimeUtc(Path.Combine(ev.Home, ".claude", "projects", $"proje-{i:D2}", "a.jsonl"), SahteEv.Stamp.AddDays(i));
        }
        ev.Scan(ev.Ctx(probe: new NullSystemProbe()));
        var ids = Ids(ev.Svc.View()).Where(i => i.StartsWith("project:")).ToList();
        Assert.Equal(25, ids.Count);
        Assert.Contains("project:claude:proje-24", ids);
        Assert.Contains("project:claude:proje-05", ids);
        Assert.Contains("project:claude:proje-04", ids);
        Assert.Contains("project:claude:proje-00", ids);
        Assert.Equal("project:claude:proje-24", Ids(ev.Svc.View()).First(i => i.StartsWith("project:")));   // en yeni önde
    }

    // ---------- Kişi kararları ve "nereden biliyorsun" ----------

    [Fact]
    public void Degistir_BosAsiriUzunDenetimKarakteriVeSirReddedilir()
    {
        using var ev = new SahteEv("degistir");
        ev.Scan();
        foreach (var bad in new[] { "", "   ", new string('a', 261), "a\nb", "a\u0007b", "parola: abc12345", SahteEv.FakeToken })
        {
            var ex = Assert.Throws<SozException>(() => ev.Svc.Change("browser:default", bad));
            Assert.Equal(400, ex.Status);
        }
        Assert.Equal(404, Assert.Throws<SozException>(() => ev.Svc.Change("yok:yok", "x")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => ev.Svc.Change(null, "x")).Status);
        Assert.Equal(0, ev.Count("SELECT COUNT(*) FROM event_log WHERE kind='kullanici.duzeltme'"));
        Assert.Equal("suggested", (string?)Item(ev.Svc.View(), "browser:default")["state"]);   // hiçbiri kaydedilmedi
    }

    [Fact]
    public void Why_KanitiSadeCumlelerleVerir_YolKisaltilir()
    {
        using var ev = new SahteEv("neden");
        ev.Scan();
        var why = ev.Svc.Why("tool:claude");
        var answer = (string)why["answer"]!;
        Assert.Contains("Şuralardan öğrendim", answer);
        Assert.Contains("~\\AppData\\Roaming\\npm\\claude.cmd", answer);
        Assert.Contains("çalıştırılmadı", answer);
        Assert.Contains("içeriğine bakmadım", answer);
        Assert.DoesNotContain(ev.Home, answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(why["evidence"]!.AsArray().Count >= 3);
        Assert.Equal(404, Assert.Throws<SozException>(() => ev.Svc.Why("tool:yok")).Status);
        var browser = (string)ev.Svc.Why("browser:chrome")["answer"]!;
        Assert.Contains("hiç bakmadım", browser);                                              // profil, geçmiş, çerez, parola
    }

    // ---------- Kayıt defteri ----------

    [Fact]
    public void DbRegistryApprovals_OzeteBagliOnay_DosyaDegisinceDusar()
    {
        using var ev = new SahteEv("onay", build: false);
        var approvals = new DbRegistryApprovals(ev.Db);
        const string connector = """{"schemaVersion":1,"entries":[{"id":"connector:opencode","kind":"connector","owner":"user","title":"OpenCode","body":{"depth":["run"],"detect":{"commands":["opencode"]},"launch":{"protocol":"acp","command":"opencode","args":["acp"]}}}]}""";

        var e1 = Assert.Single(LoadUser(connector, approvals).Entries);
        Assert.Equal(RegistryStates.Suggested, e1.State);                                       // onaysız
        Assert.False(approvals.IsApproved(e1.Id, e1.Sha256));

        approvals.Approve(e1.Id, e1.Sha256);
        Assert.Equal(RegistryStates.Enabled, Assert.Single(LoadUser(connector, approvals).Entries).State);
        approvals.Approve(e1.Id, e1.Sha256);                                                    // ikinci onay çoğaltmaz
        Assert.Equal(1, ev.Count("SELECT COUNT(*) FROM registry_state"));

        var changed = connector.Replace("\"command\":\"opencode\"", "\"command\":\"baska-program\"");
        var e3 = Assert.Single(LoadUser(changed, approvals).Entries);
        Assert.NotEqual(e1.Sha256, e3.Sha256);
        Assert.Equal(RegistryStates.Suggested, e3.State);                                       // dosya değişti: onay düştü

        approvals.Revoke(e1.Id, e1.Sha256);
        Assert.Equal(RegistryStates.Suggested, Assert.Single(LoadUser(connector, approvals).Entries).State);
        Assert.False(approvals.IsApproved("connector:baska", e1.Sha256));
        Assert.False(approvals.IsApproved(e1.Id, "bozuk"));
        Assert.Throws<ArgumentException>(() => approvals.Approve(e1.Id, "bozuk"));
    }

    [Fact]
    public void DbRegistryApprovals_YenidenAcilincaOnayKalir_EnabledSifirsaOnaysiz()
    {
        using var ev = new SahteEv("onay2", build: false);
        var sha = new string('a', 64);
        new DbRegistryApprovals(ev.Db).Approve("connector:x", sha);
        Assert.True(new DbRegistryApprovals(SozDb.OpenAndMigrate(ev.DataDir)).IsApproved("connector:x", sha));
        using (var c = ev.Db.Open()) c.Exec("UPDATE registry_state SET enabled=0 WHERE id='connector:x'");
        Assert.False(new DbRegistryApprovals(ev.Db).IsApproved("connector:x", sha));
    }

    [Fact]
    public void TanismaKaydi_DortCumleliVeKayitDenetimindenGecer()
    {
        var src = RegistryLoader.EmbeddedSources().Single(s => s.Name == "tanisma.soz.json");
        var load = RegistryLoader.Load(new[] { src }, Array.Empty<RegistrySource>());
        Assert.Empty(load.Rejected);
        var e = Assert.Single(load.Entries);
        Assert.Equal("reader:tanisma", e.Id);
        Assert.Equal(RegistryOwners.Fixed, e.Owner);
        Assert.Empty(e.Contract!.Missing());
        var report = RegistryChecker.Check(load);
        Assert.True(report.Ok, report.Format());
    }

    // ---------- Sunucu ----------

    [Fact]
    public async Task Api_TaramaYazarTokenIster_GercekMakineyeDokunmaz_CevapBicimiDogru()
    {
        using var ev = new SahteEv("api", build: true);
        var saved = Soz.Server.Endpoints.OnboardingEndpoints.ContextFactory;
        Soz.Server.Endpoints.OnboardingEndpoints.ContextFactory = () => ev.Ctx();               // GERÇEK makine bağlamına HİÇ dönülmez
        try
        {
            await using var h = await SozServer.StartAsync(Path.Combine(ev.Root, "sunucu-veri"), 0);
            using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
            HttpRequestMessage Post(string path, string body, bool token = true)
            {
                var m = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (token) m.Headers.Add("X-Soz-Token", h.Token);
                return m;
            }

            var empty = JsonNode.Parse(await http.GetStringAsync("/api/onboarding"))!;
            Assert.Equal(0, (int)empty["total"]!);
            Assert.Equal(6, empty["groups"]!.AsArray().Count);                                  // bulgu yokken de altı sabit satır

            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post("/api/onboarding/scan", "{}", token: false))).StatusCode);
            var scan = await http.SendAsync(Post("/api/onboarding/scan", "{}"));
            Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
            var body = JsonNode.Parse(await scan.Content.ReadAsStringAsync())!;
            Assert.True((int)body["outcome"]!["found"]! >= 14);
            Assert.Equal((int)body["outcome"]!["found"]!, (int)body["outcome"]!["new"]!);
            Assert.True((int)body["view"]!["pending"]! > 0);

            var why = JsonNode.Parse(await http.GetStringAsync("/api/onboarding/why?id=" + Uri.EscapeDataString("tool:claude")))!;
            Assert.Contains("Şuralardan öğrendim", (string)why["answer"]!);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/onboarding/why?id=yok")).StatusCode);

            var accept = await http.SendAsync(Post("/api/onboarding/accept", """{"id":"tool:claude"}"""));
            var acceptText = await accept.Content.ReadAsStringAsync();
            Assert.True(accept.StatusCode == HttpStatusCode.OK, $"accept {(int)accept.StatusCode}: {acceptText}");
            Assert.Equal("accepted", (string?)JsonNode.Parse(acceptText)!["state"]);
            var change = await http.SendAsync(Post("/api/onboarding/change", """{"id":"browser:default","value":"Firefox"}"""));
            Assert.Equal("Firefox", (string?)JsonNode.Parse(await change.Content.ReadAsStringAsync())!["userValue"]);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Post("/api/onboarding/change", """{"id":"browser:default","value":""}"""))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("/api/onboarding/dismiss", """{"id":"browser:opera"}"""))).StatusCode);

            var view = JsonNode.Parse(await http.GetStringAsync("/api/onboarding"))!.AsObject();
            Assert.False(Has(view, "browser:opera"));
            Assert.True(Has(JsonNode.Parse(await http.GetStringAsync("/api/onboarding?dismissed=1"))!.AsObject(), "browser:opera"));
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("/api/onboarding/restore", """{"id":"browser:opera"}"""))).StatusCode);
            Assert.True(Has(JsonNode.Parse(await http.GetStringAsync("/api/onboarding"))!.AsObject(), "browser:opera"));

            var raw = view.ToJsonString() + await http.GetStringAsync("/api/onboarding");
            Assert.DoesNotContain(SahteEv.Marker, raw);
            Assert.DoesNotContain(ev.Home, raw, StringComparison.OrdinalIgnoreCase);
        }
        finally { Soz.Server.Endpoints.OnboardingEndpoints.ContextFactory = saved; }
    }
}
