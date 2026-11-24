using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Soz.Core.Run;
using Soz.Core.Data;

namespace Soz.Core.Hooks;

/// <summary>
/// Kanca jetonu: veri klasöründe <c>kanca-jeton</c> (rastgele 32 bayt, base64url). Claude Code'un http kancası ve soz-kanca rölesi jetonu
/// <c>X-Soz-Hook-Token</c> başlığıyla yollar; sunucu yalnız jetonlu kanca isteğini işler. Sunucunun açılışta yenilenen yazma jetonundan AYRIDIR
/// (kanca ayarı kullanıcının settings.json dosyasında durur, sunucu her açılışta yeni jeton üretirse kancalar bayatlardı).
/// Jeton yalnız 127.0.0.1 üzerinde geçerlidir ve yalnız /api/hooks/* ucunu açar.
/// </summary>
public static class HookToken
{
    public const string FileName = "kanca-jeton";

    public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    public static string? Read(string dataDir)
    {
        try
        {
            var p = PathFor(dataDir);
            if (!File.Exists(p)) return null;
            var t = File.ReadAllText(p).Trim();
            return t.Length >= 16 ? t : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string GetOrCreate(string dataDir)
    {
        var existing = Read(dataDir);
        if (existing is not null) return existing;
        Directory.CreateDirectory(dataDir);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var p = PathFor(dataDir);
        var tmp = p + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, token);
            try { File.Move(tmp, p, overwrite: false); }
            catch (IOException) { /* başka süreç önce yazdı: onunkini oku */ }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        return Read(dataDir) ?? token;
    }

    public static bool Equal(string? given, string expected)
    {
        if (string.IsNullOrEmpty(given)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(given);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>`claude --version` okuması (model çağırmaz). Sonuç kısa süre önbelleğe alınır.</summary>
public static class ClaudeVersion
{
    private static readonly Regex Ver = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);
    private static readonly object Gate = new();
    private static (string key, Version? v, DateTime at)? _cache;

    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Ver.Match(text);
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    public static Version? Detect(string? configuredPath)
    {
        var key = configuredPath ?? "";
        lock (Gate)
            if (_cache is { } c && c.key == key && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(5)) return c.v;
        Version? v = null;
        try
        {
            var cmd = ClaudeLocator.Resolve(configuredPath);
            if (cmd is not null)
            {
                var psi = new ProcessStartInfo(cmd.FileName)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var a in cmd.PrefixArgs) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--version");
                if (cmd.Env is not null) foreach (var (k, val) in cmd.Env) psi.Environment[k] = val;
                using var p = Process.Start(psi);
                if (p is not null)
                {
                    try { p.StandardInput.Close(); } catch (Exception) { }
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    _ = p.StandardError.ReadToEndAsync();
                    if (p.WaitForExit(8000)) { outTask.Wait(2000); v = Parse(outTask.IsCompletedSuccessfully ? outTask.Result : null); }
                    else { try { p.Kill(entireProcessTree: true); } catch (Exception) { } }
                }
            }
        }
        catch (Exception) { v = null; }
        lock (Gate) _cache = (key, v, DateTime.UtcNow);
        return v;
    }
}

/// <summary>
/// Kancayı hangi yolla kuracağını seçer. VARSAYILAN komut yoludur (soz-kanca.exe, sessiz sonek: Söz kapalıyken de Claude penceresinde hata notu çıkmaz).
/// http yolu (süreç açmaz, araç başına yaklaşık 0,5 ms) "hızlı kip"tir ve yalnız şu durumlarda seçilir: kullanıcı Ayarlar'dan açtı (hooksFastMode) ya da
/// SOZ_HOOK_TRANSPORT=http verildi; ayrıca Söz sunucusu şu an çalışıyor (veri klasöründe canlı sunucu.json), adres geri döngü (127.0.0.1) ve Claude Code sürümü
/// okunabiliyor ve <see cref="HookInstaller.HttpHooksMinVersion"/> ya da üstü. Biri tutmazsa null: komut yolu. SOZ_HOOK_TRANSPORT=command her zaman komut yolunu zorlar.
/// </summary>
public static class HookTransportPlanner
{
    public const string EnvName = "SOZ_HOOK_TRANSPORT";

    /// <summary>Hızlı kip isteniyor mu: ortam değişkeni (http/command) ayarın önündedir.</summary>
    public static bool FastModeWanted(bool settingOn) => FastModeWanted(settingOn, Environment.GetEnvironmentVariable(EnvName));

    internal static bool FastModeWanted(bool settingOn, string? env)
    {
        if (string.Equals(env, "command", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(env, "http", StringComparison.OrdinalIgnoreCase)) return true;
        return settingOn;
    }

    /// <param name="fastMode">Ayardaki hooksFastMode değeri (null = kapalı). Ortam değişkeni bunun önündedir.</param>
    public static HookHttpTarget? Plan(string dataDir, string? claudePath, Func<Version?>? versionProbe = null, Func<string, ServerFileInfo?>? serverReader = null, bool? fastMode = null)
    {
        if (!FastModeWanted(fastMode ?? false)) return null;
        var info = (serverReader ?? ServerFile.Read)(dataDir);
        if (info is null || !info.Alive || string.IsNullOrWhiteSpace(info.Url)) return null;
        if (!Uri.TryCreate(info.Url.TrimEnd('/'), UriKind.Absolute, out var u) || u.Scheme != "http") return null;
        if (!(u.Host == "127.0.0.1" || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))) return null;
        var version = versionProbe is not null ? versionProbe() : ClaudeVersion.Detect(claudePath);
        if (version is null || version < HookInstaller.HttpHooksMinVersion) return null;
        // "localhost" yerine 127.0.0.1: localhost önce IPv6'yı dener (Windows'ta kapalıyken ~20 ms yavaş).
        return new HookHttpTarget($"http://127.0.0.1:{u.Port}", HookToken.GetOrCreate(dataDir));
    }
}

/// <summary>Bir geçişin özeti: taranan ayar dosyası, değişen dosyalar, hata cümleleri.</summary>
public sealed record HookRetargetResult(int Scanned, IReadOnlyList<string> Changed, IReadOnlyList<string> Errors);

/// <summary>
/// Hızlı kip açıkken Söz'ün açılışında kancaları http'ye alır, DÜZGÜN kapanışında komut yoluna geri çevirir (Söz kapalıyken http kancası Claude'da hata notu gösterir).
/// Yalnız içinde Söz'ün kendi kancası olan ayar dosyalarına dokunur, yalnız kendi girdilerimizi değiştirir (yorumlar, başka kancalar ve biçim korunur).
/// Her geçişte yeni yedek dosyası bırakılmaz (açılış ve kapanışta birikmesin); kurulum anındaki ilk yedek durur.
/// Çökme ya da zorla kapanmada geri çevirme çalışmaz: kancalar http'de kalır ve Söz yeniden açılana kadar Claude hata notu yazar (iş yine de durmaz).
/// </summary>
public static class HookTransportSwitch
{
    public static List<string> CandidateFiles(Soz.Core.Data.SozDb db, string? userHome)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        using (var c = db.Open())
        {
            var projects = c.Query("SELECT path FROM projects WHERE path IS NOT NULL AND key<>'*' ORDER BY id LIMIT 500", r => r.GetString(0));
            foreach (var root in projects)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in new[] { "settings.json", "settings.local.json" })
                {
                    var path = Path.Combine(root, ".claude", f);
                    if (File.Exists(path) && seen.Add(Path.GetFullPath(path))) list.Add(path);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            var u = Path.Combine(userHome, ".claude", "settings.json");
            if (File.Exists(u) && seen.Add(Path.GetFullPath(u))) list.Add(u);
        }
        return list;
    }

    private sealed record Found(string Arg, bool IsHttp, string? Token, string? Data, string? Url = null);

    private static List<Found>? Inspect(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null; // bağlantı olan dosyaya yazılmaz
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var opts = new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true };
            if (System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: opts) is not System.Text.Json.Nodes.JsonObject root) return null;
            if (root["hooks"] is not System.Text.Json.Nodes.JsonObject events) return null;
            var found = new List<Found>();
            foreach (var (_, groups) in events)
            {
                if (groups is not System.Text.Json.Nodes.JsonArray ga) continue;
                foreach (var g in ga)
                {
                    if (g?["hooks"] is not System.Text.Json.Nodes.JsonArray inner) continue;
                    foreach (var h in inner)
                    {
                        if (!HookInstaller.IsOurs(h)) continue;
                        var arg = HookInstaller.ArgOf(h)!;
                        var type = h?["type"] is System.Text.Json.Nodes.JsonValue tv && tv.TryGetValue<string>(out var ty) ? ty : null;
                        if (type == "http")
                        {
                            string? tok = h?["headers"]?[HookInstaller.TokenHeader] is System.Text.Json.Nodes.JsonValue hv && hv.TryGetValue<string>(out var t) ? t : null;
                            var url = h?["url"] is System.Text.Json.Nodes.JsonValue uv && uv.TryGetValue<string>(out var us) ? us : null;
                            found.Add(new Found(arg, true, tok, null, url));
                        }
                        else if (h?["command"] is System.Text.Json.Nodes.JsonValue cv && cv.TryGetValue<string>(out var cmd))
                            found.Add(new Found(arg, false, null, HookHealth.ParseCommand(cmd).data));
                    }
                }
            }
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Kancaları http'ye (toHttp) ya da komuta geri alır. http'ye alma yalnız hızlı kip koşulları tutuyorsa (Söz çalışıyor, Claude sürümü yeterli) yapılır;
    /// tutmuyorsa hiçbir şeye dokunulmaz. Geri alma koşulsuzdur ama yalnız bizim jetonumuzu taşıyan http girdilerini çevirir. Hata dışarı sızmaz, sonuçta listelenir.
    /// </summary>
    public static HookRetargetResult Retarget(Soz.Core.Data.SozDb db, string kancaExe, bool toHttp, string? userHome, string? claudePath = null,
        Func<Version?>? versionProbe = null, Func<string, ServerFileInfo?>? serverReader = null)
    {
        var dataDir = db.DataDir;
        var changed = new List<string>(); var errors = new List<string>(); var scanned = 0;
        HookHttpTarget? target = null;
        if (toHttp)
        {
            target = HookTransportPlanner.Plan(dataDir, claudePath, versionProbe, serverReader, fastMode: true);
            if (target is null) return new HookRetargetResult(0, changed, errors);
        }
        var ourToken = HookToken.Read(dataDir);
        if (!toHttp && ourToken is null) return new HookRetargetResult(0, changed, errors); // hiç http kurulmamış
        var budget = Soz.Core.Jobs.WorkService.GateBudgetSeconds(SozSettings.Load(db));
        foreach (var path in CandidateFiles(db, userHome))
        {
            var found = Inspect(path);
            if (found is null || found.Count == 0) continue;
            scanned++;
            if (toHttp)
            {
                // yalnız bu Söz'ün veri klasörüne yazan komut girdileri olan dosyalar; başka veri klasörüne ait kuruluma dokunulmaz
                var commands = found.Where(f => !f.IsHttp).ToList();
                if (commands.Any(f => f.Data is null || !HookHealth.SamePath(f.Data, dataDir))) continue;
                var fresh = commands.Any(f => HookInstaller.HttpArgs.Contains(f.Arg));
                // bizim jetonlu http girdisi başka bir kapıyı gösteriyorsa (Söz başka portta açıldı) adres yenilenir
                var stale = found.Any(f => f.IsHttp && string.Equals(f.Token, ourToken, StringComparison.Ordinal) && f.Url is not null && !f.Url.StartsWith(target!.BaseUrl + "/", StringComparison.Ordinal));
                if (!fresh && !stale) continue;
            }
            else if (!found.Any(f => f.IsHttp && string.Equals(f.Token, ourToken, StringComparison.Ordinal))) continue;
            try
            {
                var only = string.Join(",", found.Select(f => f.Arg).Distinct());
                var res = HookInstaller.InstallDetailed(path, kancaExe, dataDir, HookInstaller.DotnetRootFor(kancaExe), only, budget, target, backup: false);
                if (res.Changed) changed.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                errors.Add($"{path}: {e.Message}");
            }
        }
        return new HookRetargetResult(scanned, changed, errors);
    }
}
