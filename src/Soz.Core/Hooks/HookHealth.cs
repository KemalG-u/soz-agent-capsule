using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Hooks;

public sealed record HookFileDto(string Scope, long? ProjectId, string? ProjectName, string Path, bool Exists, string? ReadError,
    List<string> Hooks, string? ExePath, bool? ExeExists, string? DataDir, bool? DataMatches);
public sealed record HookStatDto(string Name, int Runs24h, int Errors24h, int Timeouts24h, string? LastRunAt, string? LastResult, string? LastErrorAt, string? LastError, long? MaxMs, string? Label = null);
public sealed record HookHealthDto(string CheckedAt, bool Ok, string? GateProblem, List<string> Warnings, List<HookFileDto> Files, List<HookStatDto> Stats);

/// <summary>
/// Sessiz ölü kanca olmasın (EK B). Yalnız OKUR: bilinen projelerin .claude\settings(.local).json dosyaları ve kullanıcı geneli
/// ~/.claude/settings.json (yalnız Söz komutu var mı; içerik dışarı taşınmaz) ile events'teki "hook_pulse" nabızları.
/// Hiçbir dosyaya yazmaz.
/// </summary>
public static class HookHealth
{
    /// <summary>Kanca adının sade Türkçesi: kişiye gösterilen cümlede ve ekranda "user-prompt-submit" gibi ad değil bu görünür.</summary>
    public static string PlainName(string hook) => hook switch
    {
        "session-start" => "oturum başlangıcı",
        "user-prompt-submit" => "istem kaydı",
        "stop" => "kanıt denetimi",
        "stop-failure" => "hata işareti",
        "notification" => "bekleme işareti",
        "pre-tool-use" => "bekçi",
        _ => hook,
    };

    public const string SilentWarning = "kurulu ama 24 saattir hiç çalışmadı ve bu projede oturum vardı";

    public static HookHealthDto Build(SqliteConnection c, string dataDir, string? userHome, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? Time.UtcNow();
        var day = Time.Iso(now.AddHours(-24));
        var files = new List<HookFileDto>();
        var projects = c.Query("SELECT id, name, path, COALESCE(gate_enabled,0) FROM projects WHERE path IS NOT NULL AND key<>'*' ORDER BY id LIMIT 500",
            r => (id: r.GetInt64(0), name: r.Str(1), path: r.Str(2)!, gate: r.Long(3) == 1));
        foreach (var p in projects)
        {
            if (!Directory.Exists(p.path)) continue;
            foreach (var f in new[] { "settings.json", "settings.local.json" })
            {
                var path = System.IO.Path.Combine(p.path, ".claude", f);
                if (File.Exists(path)) files.Add(Read("proje", p.id, p.name, path, dataDir));
            }
        }
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            var u = System.IO.Path.Combine(userHome, ".claude", "settings.json");
            if (File.Exists(u)) files.Add(Read("kullanıcı geneli", null, null, u, dataDir));
        }

        // nabızlar (son 24 saat)
        var pulses = c.Query("SELECT ts, project_id, detail FROM events WHERE kind=@k AND ts>=@d ORDER BY id", r => (ts: r.Str(0), pid: r.LongN(1), detail: r.Str(2)),
                null, ("@k", HookHandler.PulseKind), ("@d", day))
            .Select(x => (x.ts, x.pid, d: Parse(x.detail))).ToList();
        var stats = new List<HookStatDto>();
        foreach (var name in HookHandler.HookNames)
        {
            var mine = pulses.Where(x => x.d.name == name).ToList();
            var errs = mine.Where(x => x.d.result != "ok").ToList();
            var lastErr = errs.LastOrDefault();
            var last = mine.LastOrDefault();
            stats.Add(new HookStatDto(name, mine.Count, errs.Count(x => x.d.result == "hata"), errs.Count(x => x.d.result == "zaman aşımı"),
                last.ts, mine.Count == 0 ? null : last.d.result, lastErr.ts, lastErr.d.error, mine.Count == 0 ? null : mine.Max(x => x.d.ms), PlainName(name)));
        }

        var warnings = new List<string>();
        var anySessions = c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE last_at>=@d", null, ("@d", day)) > 0;
        foreach (var f in files.Where(f => f.Hooks.Count > 0))
        {
            var who = f.ProjectName ?? f.Scope;
            if (f.ExeExists == false) warnings.Add($"{who}: terminal desteğinin çalıştırdığı program bulunamadı ({f.ExePath}). Terminal desteği hiç çalışmaz.");
            if (f.DataMatches == false)
                warnings.Add(f.DataDir is { } dd && dd.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? $"{who}: terminal desteği Söz'ün şimdiki kapısına değil başka bir kapıya yazıyor ({f.DataDir}). Terminal desteğini yeniden kur."
                    : $"{who}: terminal desteği başka bir veri klasörüne yazıyor ({f.DataDir}). Bu ekrana haber gelmez.");
            var ran = f.ProjectId is { } pid ? pulses.Any(x => x.pid == pid) : pulses.Count > 0;
            var hadSession = f.ProjectId is { } pid2
                ? c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE project_id=@p AND last_at>=@d", null, ("@p", pid2), ("@d", day)) > 0
                : anySessions;
            if (!ran && hadSession && f.ExeExists != false && f.DataMatches != false) warnings.Add($"{who}: {SilentWarning}.");
        }
        foreach (var s in stats.Where(s => s.Errors24h + s.Timeouts24h > 0))
            warnings.Add($"Terminal desteğinin “{PlainName(s.Name)}” parçası son 24 saatte {s.Errors24h + s.Timeouts24h} kez hata verdi{(s.LastError is null ? "" : ". Son hata: " + s.LastError)}.");

        var gate = GateProblem(c, projects.Where(p => p.gate).Select(p => (p.id, p.name)).ToList(), files, stats, pulses.Select(x => (x.pid, x.d.name)).ToList(), day);
        return new HookHealthDto(Time.Iso(now), gate is null && warnings.Count == 0, gate, warnings, files, stats);
    }

    /// <summary>Kanıt kapısı (stop kancası) şu an neden çalışmıyor; sorun yoksa null. Yalnız kapısı açık projelere bakılır.</summary>
    private static string? GateProblem(SqliteConnection c, List<(long id, string? name)> gated, List<HookFileDto> files, List<HookStatDto> stats, List<(long? pid, string? name)> pulses, string day)
    {
        if (gated.Count == 0) return null;
        var userStop = files.FirstOrDefault(f => f.ProjectId is null && f.Hooks.Contains("stop"));
        foreach (var (id, name) in gated)
        {
            var who = name ?? $"proje {id}";
            var stopFile = files.FirstOrDefault(f => f.ProjectId == id && f.Hooks.Contains("stop")) ?? userStop;
            if (stopFile is null) return $"\"{who}\" projesinde kanıt denetimi kurulu değil";
            if (stopFile.ExeExists == false) return $"kanıt denetiminin çalıştırdığı program bulunamadı ({stopFile.ExePath})";
            if (stopFile.DataMatches == false) return stopFile.DataDir?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ? "kanıt denetimi Söz'ün şimdiki kapısına değil başka bir kapıya yazıyor" : "kanıt denetimi başka bir veri klasörüne yazıyor";
            var hadSession = c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE project_id=@p AND last_at>=@d", null, ("@p", id), ("@d", day)) > 0;
            if (hadSession && !pulses.Any(x => x.name == "stop" && x.pid == id)) return $"\"{who}\" projesinde oturum vardı ama kanıt denetimi 24 saattir hiç çalışmadı";
        }
        var stop = stats.First(s => s.Name == "stop");
        if (stop.LastResult is { } lr && lr != "ok") return $"kanıt denetiminin son çalışması {(lr == "zaman aşımı" ? "zaman aşımına uğradı" : "hata verdi")}{(stop.LastError is null ? "" : ": " + stop.LastError)}";
        return null;
    }

    private static (string? name, long ms, string? result, string? error) Parse(string? json)
    {
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject o) return (null, 0, null, null);
            string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var ms = o["ms"] is JsonValue m && m.TryGetValue<long>(out var l) ? l : 0;
            return (S("name"), ms, S("result"), S("error"));
        }
        catch (JsonException) { return (null, 0, null, null); }
    }

    /// <summary>Bir settings dosyasını okur: hangi Söz kancaları var, exe yolu var mı, --data bu veri klasörü mü.</summary>
    public static HookFileDto Read(string scope, long? projectId, string? projectName, string path, string dataDir)
    {
        var hooks = new List<string>();
        string? exe = null, data = null, httpUrl = null;
        try
        {
            var text = File.ReadAllText(path);
            var root = string.IsNullOrWhiteSpace(text) ? null
                : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            if (root?["hooks"] is JsonObject events)
                foreach (var (_, groups) in events)
                {
                    if (groups is not JsonArray ga) continue;
                    foreach (var g in ga)
                    {
                        if (g?["hooks"] is not JsonArray inner) continue;
                        foreach (var h in inner)
                        {
                            if (!HookInstaller.IsOurs(h)) continue;
                            var arg = HookInstaller.ArgOf(h);
                            if (arg is not null && !hooks.Contains(arg)) hooks.Add(arg);
                            if (h is JsonObject ho && ho["type"] is JsonValue tv && tv.TryGetValue<string>(out var ty) && ty == "http")
                            {
                                // http kancası: süreç yok. Hedef adres kayıtlı; Söz'ün şimdiki kapısıyla karşılaştırılır.
                                if (ho["url"] is JsonValue uv && uv.TryGetValue<string>(out var us)) httpUrl ??= us;
                                continue;
                            }
                            var cmd = (string)h!["command"]!;
                            var (e, d) = ParseCommand(cmd);
                            exe ??= e; data ??= d;
                        }
                    }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HookFileDto(scope, projectId, projectName, path, true, "okunamadı: " + e.GetType().Name, hooks, null, null, null, null);
        }
        bool? exeExists = exe is null ? null : ExeExists(exe);
        bool? matches = data is null ? null : SamePath(data, dataDir);
        if (data is null && httpUrl is not null)
        {
            data = httpUrl;
            matches = SameServerPort(httpUrl, dataDir);
        }
        return new HookFileDto(scope, projectId, projectName, path, true, null, hooks, exe, exeExists, data, matches);
    }

    /// <summary>Kanca komutundan exe yolu ve --data değeri. Baştaki ORTAM=değer atamaları atlanır; tırnaklı ya da tırnaksız ilk sözcük exe'dir.</summary>
    public static (string? exe, string? data) ParseCommand(string cmd)
    {
        var tokens = Tokens(cmd);
        var i = 0;
        while (i < tokens.Count && System.Text.RegularExpressions.Regex.IsMatch(tokens[i], @"^[A-Za-z_][A-Za-z0-9_]*=")) i++;
        var exe = i < tokens.Count ? tokens[i] : null;
        string? data = null;
        for (var k = i + 1; k < tokens.Count - 1; k++) if (tokens[k] == "--data") { data = tokens[k + 1]; break; }
        return (exe, data);
    }

    private static List<string> Tokens(string s)
    {
        // bash biçimi: 'tek tırnak' (içinde hiçbir şey açılmaz), "çift tırnak" (eski kayıtlar) ve tırnak dışında \' = düz '.
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        var inS = false; var inD = false; var had = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inS) { if (ch == '\'') inS = false; else cur.Append(ch); continue; }
            if (inD) { if (ch == '"') inD = false; else cur.Append(ch); continue; }
            if (ch == '\'') { inS = true; had = true; continue; }
            if (ch == '"') { inD = true; had = true; continue; }
            if (ch == '\\' && i + 1 < s.Length && s[i + 1] == '\'') { cur.Append('\''); i++; continue; }
            if (char.IsWhiteSpace(ch)) { if (cur.Length > 0 || had) list.Add(cur.ToString()); cur.Clear(); had = false; continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0 || had) list.Add(cur.ToString());
        return list;
    }

    private static bool ExeExists(string exe)
    {
        var p = exe.Replace('/', System.IO.Path.DirectorySeparatorChar);
        // Git Bash biçimi /e/... → E:\...
        if (OperatingSystem.IsWindows() && System.Text.RegularExpressions.Regex.IsMatch(exe, "^/[a-zA-Z]/")) p = exe[1] + ":" + exe[2..].Replace('/', '\\');
        if (System.IO.Path.IsPathRooted(p)) return File.Exists(p) || File.Exists(p + ".exe");
        // yalın ad: PATH'te ara
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = System.IO.Path.Combine(dir.Trim(), p);
                if (File.Exists(f) || File.Exists(f + ".exe")) return true;
            }
            catch (ArgumentException) { }
        }
        return false;
    }

    /// <summary>http kancasının adresi Söz'ün şimdiki kapısı mı? Sunucu şu an kapalıysa (karar verilemez) null.</summary>
    private static bool? SameServerPort(string hookUrl, string dataDir)
    {
        try
        {
            var info = ServerFile.Read(dataDir);
            if (info is null || !info.Alive || string.IsNullOrWhiteSpace(info.Url)) return null;
            return Uri.TryCreate(hookUrl, UriKind.Absolute, out var a) && Uri.TryCreate(info.Url, UriKind.Absolute, out var b) && a.Port == b.Port;
        }
        catch (Exception) { return null; }
    }

    internal static bool SamePath(string a, string b)
    {
        try
        {
            static string N(string x) => System.IO.Path.GetFullPath(x.Replace('/', System.IO.Path.DirectorySeparatorChar)).TrimEnd('\\', '/');
            return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
