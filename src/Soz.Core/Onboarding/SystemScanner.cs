using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Registry;

namespace Soz.Core.Onboarding;

/// <summary>
/// Tarayıcılar, ekran ölçeği ve dil. Tarayıcı için yalnız "kurulu mu" ve "varsayılan mı" sorulur:
/// profil, geçmiş, çerez, parola deposu, yer imi klasörlerine (ör. "User Data") HİÇ bakılmaz, listelenmez, varlığı bile sorulmaz.
/// </summary>
internal static class SystemScanner
{
    private sealed record KnownBrowser(string Key, string Title, string[] NameHints, string[] ExeNames, string[] ProgIdPrefixes, string[] Relative);

    // Göreli yollar program klasörü köklerinden (Program Files, Program Files (x86), %LOCALAPPDATA%) aranır. Hepsi kurulum klasörüdür, profil değil.
    private static readonly KnownBrowser[] Known =
    {
        new("chrome", "Google Chrome", new[] { "chrome" }, new[] { "chrome.exe" }, new[] { "ChromeHTML" }, new[] { @"Google\Chrome\Application\chrome.exe" }),
        new("msedge", "Microsoft Edge", new[] { "edge" }, new[] { "msedge.exe" }, new[] { "MSEdgeHTM" }, new[] { @"Microsoft\Edge\Application\msedge.exe" }),
        new("firefox", "Mozilla Firefox", new[] { "firefox" }, new[] { "firefox.exe" }, new[] { "FirefoxURL", "FirefoxHTML" }, new[] { @"Mozilla Firefox\firefox.exe" }),
        new("brave", "Brave", new[] { "brave" }, new[] { "brave.exe" }, new[] { "BraveHTML", "BraveFile" }, new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" }),
        new("opera", "Opera", new[] { "opera" }, new[] { "opera.exe" }, new[] { "OperaStable", "Opera" }, new[] { @"Opera\opera.exe", @"Programs\Opera\opera.exe" }),
        new("vivaldi", "Vivaldi", new[] { "vivaldi" }, new[] { "vivaldi.exe" }, new[] { "VivaldiHTM" }, new[] { @"Vivaldi\Application\vivaldi.exe" }),
        new("yandex", "Yandex Browser", new[] { "yandex" }, new[] { "yandexbrowser.exe" }, new[] { "YandexHTML" }, new[] { @"Yandex\YandexBrowser\Application\browser.exe" }),
    };

    private sealed class Acc
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RegistrySources { get; } = new();
    }

    public static void Scan(ScanContext ctx, ScanResult res)
    {
        ScanBrowsers(ctx, res);
        ScanDisplay(ctx, res);
        ScanLanguage(ctx, res);
    }

    private static void ScanBrowsers(ScanContext ctx, ScanResult res)
    {
        var roots = ctx.Roots;
        var fs = ctx.Fs;
        var acc = new Dictionary<string, Acc>(StringComparer.Ordinal);

        Acc Get(string key, string title) => acc.TryGetValue(key, out var a) ? a : acc[key] = new Acc { Key = key, Title = title };

        // 1) Sistemin kayıtlı tarayıcı listesi (Başlat menüsü kaydı).
        foreach (var b in ctx.Probe.Browsers())
        {
            var k = KeyOf(b.Name, b.ExePath);
            var a = Get(k.Key, k.Title ?? b.Name);
            a.RegistrySources.Add(b.Source);
            if (!string.IsNullOrWhiteSpace(b.ExePath)) a.Paths.Add(b.ExePath!);
        }
        // 2) Bilinen kurulum klasörleri (yalnız dosya var mı).
        foreach (var kb in Known)
            foreach (var rel in kb.Relative)
                foreach (var baseDir in new[] { roots.ProgramFiles, roots.ProgramFilesX86, roots.LocalAppData })
                {
                    var p = Path.Combine(baseDir, rel);
                    if (fs.FileExists(p)) Get(kb.Key, kb.Title).Paths.Add(p);
                }

        // 3) Varsayılan tarayıcı.
        string? defaultKey = null;
        var def = ctx.Probe.DefaultBrowser();
        if (!def.Readable) res.CouldNotRead.Add("Varsayılan tarayıcı");
        else if (def.Value is { } dv && (dv.ProgId is not null || dv.ExeName is not null))
        {
            var prog = dv.ProgId;
            var exeName = dv.ExeName;
            var key = Known.FirstOrDefault(k => prog is not null && k.ProgIdPrefixes.Any(pp => prog.StartsWith(pp, StringComparison.OrdinalIgnoreCase)))?.Key
                      ?? Known.FirstOrDefault(k => exeName is not null && k.ExeNames.Contains(exeName, StringComparer.OrdinalIgnoreCase))?.Key
                      ?? Slug("other-" + (dv.ProgId ?? dv.ExeName));
            defaultKey = key;
            var kb = Known.FirstOrDefault(k => k.Key == key);
            var installed = acc.TryGetValue(key, out var da) && da.Paths.Any(fs.FileExists);
            var title = kb?.Title ?? (dv.ProgId ?? dv.ExeName)!;
            var f = new Finding
            {
                Id = "browser:default",
                Group = FindingGroups.Browsers,
                Title = $"Varsayılan tarayıcın {title}",
                Detail = installed || kb is null ? null : "Sistem böyle diyor ama program dosyasını göremedim",
                Confidence = Confidence.Seen,
                Facts = new JsonObject { ["key"] = key, ["name"] = title, ["progId"] = dv.ProgId, ["exeName"] = dv.ExeName },
            };
            f.Evidence.Add(new Evidence("Sistemin varsayılan web tarayıcısı kaydını okudum (yalnız program kimliği)", "Windows varsayılan uygulama ayarı"));
            res.Findings.Add(f);
        }

        // 4) Tarayıcı başına bulgu: dosyası diskte görüldüyse "kurulu", yalnız kayıt varsa "iz".
        foreach (var a in acc.Values.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var present = a.Paths.Where(fs.FileExists).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var isDefault = a.Key == defaultKey;
            var installed = present.Count > 0;
            var f = new Finding
            {
                Id = "browser:" + a.Key,
                Group = FindingGroups.Browsers,
                Title = installed ? $"{a.Title} kurulu" : $"{a.Title} için kayıt var",
                Detail = installed
                    ? (isDefault ? "Varsayılan tarayıcı" : null)
                    : "Sistem kayıt defterinde görünüyor ama program dosyasını görmedim: kurulu olduğunu doğrulayamadım" + (isDefault ? ". Varsayılan olarak işaretli" : ""),
                Confidence = installed ? Confidence.Installed : Confidence.Trace,
                Facts = new JsonObject { ["key"] = a.Key, ["name"] = a.Title, ["isDefault"] = isDefault },
            };
            foreach (var p in present) f.Evidence.Add(new Evidence("Program dosyası var", roots.Abbrev(p), fs.LastWrite(p) is { } w ? ScanContext.Day(w) : null));
            foreach (var s in a.RegistrySources.Distinct()) f.Evidence.Add(new Evidence("Sistem bunu tarayıcı olarak kaydetmiş", s));
            f.Evidence.Add(new Evidence("Profiline, geçmişine, çerezlerine ve parolalarına hiç bakmadım", "—"));
            res.Findings.Add(f);
        }
    }

    private static (string Key, string? Title) KeyOf(string name, string? exePath)
    {
        var fold = Fold.Text(name);
        var exe = exePath is null ? null : Path.GetFileName(exePath);
        // "Chromium" Chrome sayılmaz.
        if (fold.Contains("chromium")) return ("chromium", "Chromium");
        foreach (var k in Known)
        {
            if (k.NameHints.Any(h => fold.Contains(h))) return (k.Key, k.Title);
            if (exe is not null && k.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase)) return (k.Key, k.Title);
        }
        return (Slug("other-" + fold), null);
    }

    private static string Slug(string s) => Regex.Replace(Fold.Text(s), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } t ? (t.Length > 60 ? t[..60] : t) : "other";

    private static void ScanDisplay(ScanContext ctx, ScanResult res)
    {
        var pct = ctx.Probe.DisplayScalePercent();
        if (pct is null or < 50 or > 500) { res.CouldNotRead.Add("Ekran ölçeği"); return; }
        var f = new Finding
        {
            Id = "display:scale", Group = FindingGroups.Display, Title = $"Ekran ölçeğin %{pct}", Confidence = Confidence.Seen, Count = pct,
            Facts = new JsonObject { ["scalePercent"] = pct },
        };
        f.Evidence.Add(new Evidence("Sistemin ekran ölçek ayarını okudum", "Windows ekran ayarları"));
        res.Findings.Add(f);
    }

    private static readonly Regex LocaleShape = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$", RegexOptions.CultureInvariant);

    private static void ScanLanguage(ScanContext ctx, ScanResult res)
    {
        var loc = ctx.Probe.Language();
        if (loc is null || !LocaleShape.IsMatch(loc)) { res.CouldNotRead.Add("Sistem dili"); return; }
        var lang = loc.Split('-')[0].ToLowerInvariant();
        var name = lang switch { "tr" => "Türkçe", "en" => "İngilizce", "de" => "Almanca", "fr" => "Fransızca", "es" => "İspanyolca", "ar" => "Arapça", "ru" => "Rusça", _ => loc };
        var f = new Finding
        {
            Id = "language:system", Group = FindingGroups.Language, Title = $"Sistem dilin {name} ({loc})", Confidence = Confidence.Seen,
            Facts = new JsonObject { ["locale"] = loc, ["language"] = lang },
        };
        f.Evidence.Add(new Evidence("Windows bölge ve dil ayarını okudum", "Windows bölge ayarları"));
        res.Findings.Add(f);
    }
}
