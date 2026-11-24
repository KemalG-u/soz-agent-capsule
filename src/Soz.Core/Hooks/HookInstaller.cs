using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Core.Hooks;

/// <summary>Claude Code'un http kancası için hedef: Söz sunucusunun geri döngü adresi ve kanca jetonu.</summary>
public sealed record HookHttpTarget(string BaseUrl, string Token);

public sealed record HookInstallResult(string SettingsPath, string? BackupPath, bool Changed, IReadOnlyDictionary<string, string> Transport);

/// <summary>
/// Claude Code settings.json içine Söz kancalarını ekler/kaldırır.
/// Kullanıcının dosyası METİN olarak düzenlenir (SettingsText): yorumlar, sıra, girinti, satır sonu ve BOM korunur; yalnız Söz'ün kayıtları eklenir/çıkar.
/// Her yazmadan önce zaman damgalı yedek alınır (hiçbir yedeğin üstüne yazılmaz). Kaldırınca dosya kurulumdan önceki bayt dizisine döner
/// (kurulum öncesi yedekle aynı içerikteyse yedeğin kendisi geri konur; kurulumda dosya yoktu ve geriye boş nesne kaldıysa dosya silinir).
/// Kayıt biçimi: hooks.&lt;Olay&gt;[ { matcher?, hooks: [ { type: "command"|"http", … } ] } ].
///  command: kabuk dizesi (Windows'ta Git Bash); yol TEK tırnaklı ve düz eğik çizgili; sonunda "2>/dev/null || true" (program yoksa Claude'da hata satırı çıkmaz).
///  http (Claude Code 2.1.295+, yalnız SessionStart dışındaki kancalar): süreç açılmaz, olay doğrudan Söz sunucusuna POST edilir (127.0.0.1, jetonlu).
/// </summary>
public static class HookInstaller
{
    /// <summary>Altı kanca. Zaman aşımı birimi saniyedir (belge: "Seconds before canceling"; command varsayılanı 600).</summary>
    public static readonly (string Event, string Arg)[] Hooks =
    {
        ("SessionStart", "session-start"), ("UserPromptSubmit", "user-prompt-submit"), ("Stop", "stop"), ("StopFailure", "stop-failure"), ("Notification", "notification"), ("PreToolUse", "pre-tool-use"),
    };

    /// <summary>http türüyle kurulabilen kancalar. SessionStart http desteklemez (belge: yalnız command ve mcp_tool); Notification seyrek olduğundan komutta kalır.</summary>
    public static readonly string[] HttpArgs = { "user-prompt-submit", "stop", "stop-failure", "pre-tool-use" };

    /// <summary>Claude Code'un http kancasını belgelediği ilk sürüm (belge: onFailure ve http alanları "v2.1.295 ve sonrası"). Altında komut yoluna düşülür.</summary>
    public static readonly Version HttpHooksMinVersion = new(2, 1, 295);

    /// <summary>Bekçi yalnız bu araçlara bakar (GuardEngine.IsFileTool / IsShellTool); eşleştirici diğer araçlarda (Read, Grep, MCP…) kancayı hiç çalıştırmaz.</summary>
    public const string PreToolUseMatcher = "Bash|PowerShell|Shell|Write|Edit|MultiEdit|NotebookEdit";

    public const string TokenHeader = "X-Soz-Hook-Token";
    public const string QuietSuffix = " 2>/dev/null || true";

    /// <summary>Stop kancası zaman aşımı = kapı bütçesi + 30 sn (sözleşme 4). Gözlem ve bekçi kancaları kısa: sunucu takılırsa iş beklemesin.</summary>
    public static int TimeoutFor(string arg, int gateBudgetSeconds) => arg switch
    {
        "stop" => gateBudgetSeconds + 30,
        _ => 10,
    };

    public static int HttpTimeoutFor(string arg, int gateBudgetSeconds) => arg switch
    {
        "stop" => gateBudgetSeconds + 30,
        _ => 5,
    };

    /// <summary>"--only stop,notification" gibi seçim; boşsa altısı da. Bilinmeyen ad → ArgumentException.</summary>
    public static IReadOnlyList<(string Event, string Arg)> Select(string? only)
    {
        if (string.IsNullOrWhiteSpace(only)) return Hooks;
        var want = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bad = want.Where(w => Hooks.All(h => !h.Arg.Equals(w, StringComparison.OrdinalIgnoreCase) && !h.Event.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (bad.Count > 0) throw new ArgumentException("Bilinmeyen terminal desteği parçası: " + string.Join(", ", bad) + ". Geçerli: " + string.Join(", ", Hooks.Select(h => h.Arg)));
        return Hooks.Where(h => want.Any(w => h.Arg.Equals(w, StringComparison.OrdinalIgnoreCase) || h.Event.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static string BuildCommand(string exePath, string arg, string? dataDir, string? dotnetRoot, bool quiet = true)
    {
        // K-7: tek tırnak. Çift tırnak içinde $( ), ters tırnak ve $ kabukta çalışırdı; tek tırnak içinde hiçbiri çalışmaz. ' işareti '\'' ile kaçar.
        static string Q(string p) => "'" + p.Replace('\\', '/').Replace("'", "'\\''") + "'";
        var cmd = $"{Q(exePath)} hook {arg}";
        if (!string.IsNullOrWhiteSpace(dataDir)) cmd += $" --data {Q(dataDir)}";
        if (!string.IsNullOrWhiteSpace(dotnetRoot)) cmd = $"DOTNET_ROOT={Q(dotnetRoot)} " + cmd;
        // Program silinmişse ya da çalışmazsa kabuk "bulunamadı" (çıkış 127) der ve Claude her çağrıda hata satırı gösterir; sessiz ve başarılı bitsin.
        return quiet ? cmd + QuietSuffix : cmd;
    }

    public static string HttpUrl(HookHttpTarget t, string arg) => t.BaseUrl.TrimEnd('/') + "/api/hooks/" + arg;

    /// <summary>Kanca girdisinin hangi kanca olduğu (session-start …); Söz'e ait değilse null.</summary>
    public static string? ArgOf(JsonNode? hook)
    {
        if (hook is not JsonObject o) return null;
        var type = Str(o, "type");
        if (type == "http")
        {
            if (Str(o, "url") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            if (u.Scheme != "http" || !(u.Host == "127.0.0.1" || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))) return null;
            const string prefix = "/api/hooks/";
            if (!u.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var tail = u.AbsolutePath[prefix.Length..].Trim('/');
            return Hooks.Any(h => h.Arg == tail) ? tail : null;
        }
        if (Str(o, "command") is not { } cmd) return null;
        var lower = cmd.ToLowerInvariant();
        if (!lower.Contains("soz")) return null;
        return Hooks.Select(h => h.Arg).FirstOrDefault(a => HasHookArg(lower, a));
    }

    /// <summary>" hook stop" ifadesi "hook stop-failure" içinde de geçer: addan sonra ad karakteri gelmemeli.</summary>
    private static bool HasHookArg(string lowerCmd, string arg)
    {
        var needle = " hook " + arg;
        for (var i = lowerCmd.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = lowerCmd.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            var end = i + needle.Length;
            if (end >= lowerCmd.Length || !(char.IsLetterOrDigit(lowerCmd[end]) || lowerCmd[end] == '-' || lowerCmd[end] == '_')) return true;
        }
        return false;
    }

    /// <summary>Söz'e ait kanca girdisi mi? (komutta " hook <ad>" ve soz; http'de 127.0.0.1 üzerinde /api/hooks/<ad>)</summary>
    public static bool IsOurs(JsonNode? hook) => ArgOf(hook) is not null;

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---------------- kurulum ----------------
    public static string Install(string settingsPath, string exePath, string? dataDir, string? dotnetRoot = null, string? only = null, int gateBudgetSeconds = 300, HookHttpTarget? http = null, bool backup = true)
        => InstallDetailed(settingsPath, exePath, dataDir, dotnetRoot, only, gateBudgetSeconds, http, backup).SettingsPath;

    /// <summary>Kurar. Seçilmeyen kancalarımız da önce kaldırılır (kurulum = tam istenen küme). http verilirse uygun kancalar http türünde kurulur.</summary>
    public static HookInstallResult InstallDetailed(string settingsPath, string exePath, string? dataDir, string? dotnetRoot = null, string? only = null, int gateBudgetSeconds = 300, HookHttpTarget? http = null, bool backup = true)
    {
        var selected = Select(only);
        var file = SettingsFile.Read(settingsPath);
        var text = file.Text;
        if (string.IsNullOrWhiteSpace(StripComments(text))) text = "{}";
        var root = SettingsText.Parse(text);
        if (!root.IsObject) throw new InvalidDataException("settings.json bir JSON nesnesi değil. Dokunulmadı.");

        var (cleaned, _) = RemoveAllOurs(text, SettingsFile.Provenance(settingsPath));
        var transport = new Dictionary<string, string>();
        foreach (var (ev, arg) in selected)
        {
            var useHttp = http is not null && HttpArgs.Contains(arg);
            transport[arg] = useHttp ? "http" : "command";
            var hook = useHttp
                ? new JsonObject { ["type"] = "http", ["url"] = HttpUrl(http!, arg), ["timeout"] = HttpTimeoutFor(arg, gateBudgetSeconds), ["headers"] = new JsonObject { [TokenHeader] = http!.Token } }
                : new JsonObject { ["type"] = "command", ["command"] = BuildCommand(exePath, arg, dataDir, dotnetRoot), ["timeout"] = TimeoutFor(arg, gateBudgetSeconds) };
            var group = new JsonObject();
            if (arg == "pre-tool-use") group["matcher"] = PreToolUseMatcher;
            group["hooks"] = new JsonArray(hook);
            cleaned = AddGroup(cleaned, ev, group);
        }
        if (!file.Existed && !cleaned.EndsWith('\n')) cleaned += SettingsText.NewLine(cleaned);
        Validate(cleaned);
        if (file.Existed && cleaned == file.Text) return new HookInstallResult(settingsPath, null, false, transport);
        var backupPath = SettingsFile.WriteWithBackup(settingsPath, file, cleaned, "kurulum", backup);
        return new HookInstallResult(settingsPath, backupPath, true, transport);
    }

    private static string AddGroup(string text, string eventName, JsonNode group)
    {
        var root = SettingsText.Parse(text);
        var hooksEntry = SettingsText.Member(root, "hooks");
        if (hooksEntry is null) return SettingsText.InsertEntry(text, root, "hooks", new JsonObject { [eventName] = new JsonArray(group) });
        if (!hooksEntry.Value.IsObject) throw new InvalidDataException("settings.json içindeki \"hooks\" bir nesne değil. Dokunulmadı.");
        var evEntry = SettingsText.Member(hooksEntry.Value, eventName);
        if (evEntry is null) return SettingsText.InsertEntry(text, hooksEntry.Value, eventName, new JsonArray(group));
        if (!evEntry.Value.IsArray) throw new InvalidDataException($"settings.json içindeki hooks.{eventName} bir dizi değil. Dokunulmadı.");
        return SettingsText.InsertEntry(text, evEntry.Value, null, group);
    }

    /// <summary>Sonuç metni yorum ve sondaki virgülü bilen okuyucuyla JSON nesnesi olarak okunabilmeli; olmazsa dosyaya yazılmaz.</summary>
    private static void Validate(string text)
    {
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is not JsonObject) throw new InvalidDataException("sonuç bir JSON nesnesi değil");
        }
        catch (JsonException e) { throw new InvalidDataException("Ayar dosyası düzenlenemedi (" + e.Message + "). Dokunulmadı."); }
    }

    private static string StripComments(string t)
    {
        try { return SettingsText.Normalize(t); } catch { return t; }
    }

    // ---------------- kaldırma ----------------
    public static bool Uninstall(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return false;
        var file = SettingsFile.Read(settingsPath);
        if (string.IsNullOrWhiteSpace(file.Text)) return false;
        var (cleaned, removed) = RemoveAllOurs(file.Text, SettingsFile.Provenance(settingsPath));
        if (removed == 0) return false;
        Validate(cleaned);
        SettingsFile.RestoreOrWrite(settingsPath, file, cleaned);
        return true;
    }

    /// <summary>Söz'ün bütün kayıtlarını kaldırır; kullanıcının her şeyi yerinde kalır. Boşalan grup/olay/hooks yalnız kurulum öncesinde yoktuysa silinir.</summary>
    internal static (string Text, int Removed) RemoveAllOurs(string text, SettingsFile.OriginalShape original)
    {
        var removed = 0;
        for (var guard = 0; guard < 200; guard++)
        {
            var root = SettingsText.Parse(text);
            if (!root.IsObject) break;
            var hooksEntry = SettingsText.Member(root, "hooks");
            if (hooksEntry is null || !hooksEntry.Value.IsObject) break;
            var found = false;
            for (var ei = 0; ei < hooksEntry.Value.Entries.Count && !found; ei++)
            {
                var ev = hooksEntry.Value.Entries[ei];
                if (!ev.Value.IsArray) continue;
                for (var gi = 0; gi < ev.Value.Entries.Count && !found; gi++)
                {
                    var group = ev.Value.Entries[gi];
                    if (!group.Value.IsObject) continue;
                    var inner = SettingsText.Member(group.Value, "hooks");
                    if (inner is null || !inner.Value.IsArray) continue;
                    for (var hi = 0; hi < inner.Value.Entries.Count; hi++)
                    {
                        var h = inner.Value.Entries[hi];
                        JsonNode? node;
                        try { node = JsonNode.Parse(text.Substring(h.Start, h.End - h.Start), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
                        catch (JsonException) { continue; }
                        if (!IsOurs(node)) continue;
                        found = true;
                        removed++;
                        // En büyük boş atayı seç; kurulum öncesinde zaten var olan kapları boş bırak.
                        var onlyOursInGroup = inner.Value.Entries.Count == 1 && group.Value.Entries.All(m => m.Name is "hooks" or "matcher");
                        if (!onlyOursInGroup) { text = SettingsText.RemoveEntry(text, inner.Value, hi); break; }
                        var groupIsOnlyInEvent = ev.Value.Entries.Count == 1;
                        if (!groupIsOnlyInEvent || original.HadEvent(ev.Name!)) { text = SettingsText.RemoveEntry(text, ev.Value, gi); break; }
                        var eventIsOnlyInHooks = hooksEntry.Value.Entries.Count == 1;
                        if (!eventIsOnlyInHooks || original.HadHooks) { text = SettingsText.RemoveEntry(text, hooksEntry.Value, ei); break; }
                        text = SettingsText.RemoveEntry(text, root, root.Entries.IndexOf(hooksEntry));
                        break;
                    }
                }
            }
            if (!found) break;
        }
        return (text, removed);
    }

    /// <summary>exe kendi çalışma zamanını taşımıyorsa (Debug derlemesi) ve DOTNET_ROOT tanımlıysa onu döndürür.</summary>
    public static string? DotnetRootFor(string exePath)
    {
        if (Path.GetFileNameWithoutExtension(exePath).Equals("soz-kanca", StringComparison.OrdinalIgnoreCase)) return null;
        var dir = Path.GetDirectoryName(exePath) ?? "";
        if (File.Exists(Path.Combine(dir, "coreclr.dll"))) return null;
        return Environment.GetEnvironmentVariable("DOTNET_ROOT");
    }
}

/// <summary>Ayar dosyası okuma/yazma: bayt bayt koruma (BOM), zaman damgalı yedek, kurulum öncesi şekil ve geri yükleme.</summary>
internal static class SettingsFile
{
    internal sealed record Loaded(string Text, bool Bom, bool Existed, byte[] Bytes);

    internal readonly record struct OriginalShape(bool HadHooks, IReadOnlySet<string> Events)
    {
        public bool HadEvent(string name) => Events.Contains(name);
        public static OriginalShape None => new(false, new HashSet<string>());
    }

    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    public static Loaded Read(string path)
    {
        if (!File.Exists(path)) return new Loaded("", false, false, Array.Empty<byte>());
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try { return new Loaded(Strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom, true, bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("settings.json UTF-8 değil. Dokunulmadı."); }
    }

    private static byte[] Encode(string text, bool bom)
    {
        var body = Strict.GetBytes(text);
        if (!bom) return body;
        var all = new byte[body.Length + 3];
        all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
        Buffer.BlockCopy(body, 0, all, 3, body.Length);
        return all;
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Yeni, hiçbir şeyin üstüne yazmayan yedek yolu: &lt;dosya&gt;.soz-&lt;tür&gt;-&lt;zaman&gt;[-n].&lt;uzantı&gt;</summary>
    private static string BackupPath(string path, string kind, string ext)
    {
        var stamp = Stamp();
        for (var n = 0; ; n++)
        {
            var p = $"{path}.soz-{kind}-{stamp}{(n == 0 ? "" : "-" + n)}.{ext}";
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>Kurulum öncesi yedekler, eskiden yeniye. İkisi: içerik yedeği (.bak) ve "dosya yoktu" işareti (.yok).</summary>
    private static List<string> InstallBackups(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var name = Path.GetFileName(path);
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, name + ".soz-kurulum-*")
            .Where(f => f.EndsWith(".bak", StringComparison.Ordinal) || f.EndsWith(".yok", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>Yazar; önce yedek alır (dosya vardıysa içerik yedeği, yoksa ".yok" işareti). Yedek yolunu döndürür.</summary>
    public static string? WriteWithBackup(string path, Loaded before, string newText, string kind, bool backup = true)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!backup) { WriteAtomic(full, Encode(newText, before.Bom)); return null; }
        string backupPath;
        if (before.Existed) { backupPath = BackupPath(full, kind, "bak"); File.WriteAllBytes(backupPath, before.Bytes); }
        else { backupPath = BackupPath(full, kind, "yok"); File.WriteAllBytes(backupPath, Array.Empty<byte>()); }
        WriteAtomic(full, Encode(newText, before.Bom));
        return backupPath;
    }

    private static void WriteAtomic(string full, byte[] bytes)
    {
        var tmp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try { File.WriteAllBytes(tmp, bytes); File.Move(tmp, full, overwrite: true); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>Kaldırma sonucu. Kurulum öncesi yedekle aynı içerikse yedeğin baytları geri konur (bayt bayt özgün dosya).
    /// Kurulumda dosya yoktuysa ve geriye boş nesne kaldıysa Söz'ün yarattığı dosya silinir. Yoksa cerrahi sonuç yazılır.</summary>
    public static void RestoreOrWrite(string path, Loaded before, string cleaned)
    {
        var full = Path.GetFullPath(path);
        var backups = InstallBackups(full);
        var norm = SettingsText.Normalize(cleaned);
        // kaldırma öncesi durumun yedeği
        var pre = BackupPath(full, "kaldirma", "bak");
        File.WriteAllBytes(pre, before.Bytes);
        foreach (var b in Enumerable.Reverse(backups).Where(b => b.EndsWith(".bak", StringComparison.Ordinal)))
        {
            Loaded orig;
            try { orig = Read(b); } catch (Exception e) when (e is IOException or InvalidDataException) { continue; }
            var origNorm = SettingsText.Normalize(orig.Text);
            if (origNorm == "") origNorm = "{}"; // boş dosya ile boş nesne aynı sayılır
            if (origNorm == norm)
            {
                WriteAtomic(full, orig.Bytes);
                return;
            }
        }
        var createdBySoz = backups.Any(b => b.EndsWith(".yok", StringComparison.Ordinal)) && !backups.Any(b => b.EndsWith(".bak", StringComparison.Ordinal));
        if (createdBySoz && norm == "{}")
        {
            File.Delete(full);
            return;
        }
        WriteAtomic(full, Encode(cleaned, before.Bom));
    }

    /// <summary>Kurulum öncesi dosyada "hooks" ve hangi olay anahtarları vardı (Söz'ün kayıtlarını içermeyen en eski yedekten).</summary>
    public static OriginalShape Provenance(string path)
    {
        try
        {
            foreach (var b in InstallBackups(path).Where(b => b.EndsWith(".bak", StringComparison.Ordinal)))
            {
                var l = Read(b);
                if (string.IsNullOrWhiteSpace(l.Text)) continue;
                if (HookInstaller.RemoveAllOurs(l.Text, OriginalShape.None).Removed > 0) continue; // Söz'ün kayıtlarını içeriyor: özgün değil
                var root = SettingsText.Parse(l.Text);
                if (!root.IsObject) continue;
                var hooks = SettingsText.Member(root, "hooks");
                if (hooks is null || !hooks.Value.IsObject) return new OriginalShape(false, new HashSet<string>());
                return new OriginalShape(true, hooks.Value.Entries.Where(e => e.Name is not null).Select(e => e.Name!).ToHashSet());
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return OriginalShape.None;
    }
}
