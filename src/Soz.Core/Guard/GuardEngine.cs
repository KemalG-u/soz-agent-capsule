using System.Text.Json.Nodes;

namespace Soz.Core.Guard;

public static class GuardKinds
{
    public const string ScopeOnly = "scope_only", PathProtect = "path_protect", CommandBlock = "command_block", AskBefore = "ask_before";
    public static readonly string[] All = { ScopeOnly, PathProtect, CommandBlock, AskBefore };
}

public static class GuardActions
{
    public const string Deny = "deny", Ask = "ask", Allow = "allow";
}

/// <summary>guard_rules satırı. Spec: scope_only {allow?:[yol/kalıp], allowTemp?:bool}, path_protect {globs[]}, command_block {patterns[]}, ask_before {patterns?[], globs?[]}.</summary>
public sealed record GuardRule(long Id, long? ProjectId, string Kind, string Label, JsonObject Spec, string Action, bool Enabled, long? DecisionId, string Origin, string? CreatedAt, string? Uid = null);

/// <summary>DataDir = Söz veri klasörü (soz.db burada); "{soz-data}" kalıbı bu klasörün altını korur. Bilinmiyorsa null.</summary>
public sealed record GuardInput(string ToolName, JsonNode? ToolInput, string? Cwd, string? ProjectRoot, string? Home = null, string? Temp = null, string? DataDir = null);

/// <summary>Karar. Verdict = allow | deny | ask. Unknown = hedef/komut çözülemedi (engellenmedi, olay yazılmaz).</summary>
public sealed record GuardDecision(string Verdict, GuardRule? Rule, string? Reason, string? Detail, string TargetSummary, bool Unknown, IReadOnlyList<string> Targets)
{
    public static GuardDecision AllowAll(string summary = "", bool unknown = false) => new(GuardActions.Allow, null, null, null, summary, unknown, Array.Empty<string>());
}

public static class GuardEngine
{
    /// <summary>
    /// Yol çıkarmanın kaçırdığı biçimler (dürüst liste): python/node/ruby -c "..." içinden dosya yazma, betik dosyası çalıştırma (bash x.sh),
    /// değişkenle kurulan yol ($DIR/x), base64/eval ile üretilen komut, git/npm gibi araçların kendi yan etkileri, Set-Content dışı .NET çağrıları
    /// ([IO.File]::WriteAllText), sembolik bağlantı/junction ve 8.3 kısa adlar, ek dizinler (additionalDirectories), MCP araçları.
    /// Çalışma klasörü izleme (cd/pushd/popd/Set-Location/-C ...) sonrası kalan sınırlar: (1) cd hedefi değişkenli ya da "cd -" ise sonraki GÖRELİ hedefler
    /// scope_only kuralında "çözülemedi" diye SORULUR (engellenmez), path_protect/ask_before kalıpları o hedeflerde uygulanamaz; (2) boru (|) ve arka plan (&)
    /// içindeki cd gerçekte kabuğun klasörünü değiştirmez, bekçi değiştirmiş sayar (yanlış alarm yönünde); cd'nin başarılı olduğu varsayılır;
    /// (3) cd sonrası yolu çıkarılamayan komutların yan etkisi (git clean, npm install, make, dotnet build...) ve make -C, npm run --prefix,
    /// Start-Process -WorkingDirectory ile başlayan sürecin içeriği görülmez; (4) cmd'e özgü "cd\" ve "cd/" kısaltmaları izlenmez.
    /// </summary>
    public const string KnownGaps = "python -c / node -e içinden yazma; betik dosyası içeriği; değişkenli yollar; [IO.File]::Write*; sembolik bağlantı; 8.3 kısa adlar; MCP araçları; yardımcının Söz kayıtlarını ya da ayarlarını başka araçlarla (python, node, .NET) değiştirmesi; "
        + "değişkenli ya da \"cd -\" sonrası göreli hedefler (yalnız sorulur); boru/arka plan içindeki cd; cd sonrası yolu çıkarılamayan komutların yan etkisi (git clean, npm install, make); make -C / Start-Process -WorkingDirectory içeriği";

    /// <summary>path_protect kalıbı: Söz veri klasörünün altındaki her yol (GuardInput.DataDir).</summary>
    public const string SozDataGlob = "{soz-data}";

    private static readonly HashSet<string> FileTools = new(StringComparer.OrdinalIgnoreCase) { "Write", "Edit", "MultiEdit", "NotebookEdit" };
    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase) { "Bash", "PowerShell", "Shell" };

    public static bool IsFileTool(string t) => FileTools.Contains(t);
    public static bool IsShellTool(string t) => ShellTools.Contains(t);

    public static string? Str(JsonNode? n, string key) => n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static GuardDecision Evaluate(IReadOnlyList<GuardRule> rules, GuardInput input)
    {
        var tool = input.ToolName ?? "";
        var isFile = IsFileTool(tool);
        var isShell = IsShellTool(tool);
        if (!isFile && !isShell) return GuardDecision.AllowAll();
        var active = rules.Where(r => r.Enabled).ToList();
        if (active.Count == 0) return GuardDecision.AllowAll();

        string summary;
        var cmds = new List<Cmd>();
        var raw = new List<WriteTarget>();
        if (isFile)
        {
            var p = Str(input.ToolInput, "file_path") ?? Str(input.ToolInput, "notebook_path") ?? Str(input.ToolInput, "path");
            summary = p ?? "";
            if (p is not null) raw.Add(new WriteTarget(p, false, tool.Equals("Write", StringComparison.OrdinalIgnoreCase) ? GuardText.WhyWrite : GuardText.WhyEdit));
        }
        else
        {
            var command = Str(input.ToolInput, "command") ?? "";
            summary = command;
            cmds = ShellParser.Parse(command, tool.Equals("PowerShell", StringComparison.OrdinalIgnoreCase));
            raw = TargetExtractor.Extract(cmds);
        }

        var root = input.ProjectRoot ?? input.Cwd;
        var rootN = root is null ? null : GuardPaths.Normalize(root, null, input.Home, input.Temp);
        var dataN = string.IsNullOrWhiteSpace(input.DataDir) ? null : GuardPaths.Normalize(input.DataDir, null, input.Home, input.Temp);
        var targets = new List<(WriteTarget Raw, NPath? Path, bool RelUnresolved)>();
        var unknown = false;
        foreach (var t in raw)
        {
            var (n, relUnres) = ResolveTarget(t, input.Cwd, input);
            if (n is null) unknown = true;
            targets.Add((t, n, relUnres));
        }

        GuardRule? denyRule = null, askRule = null;
        string? denyDetail = null, askDetail = null;
        void Hit(GuardRule r, string action, string detail)
        {
            if (action == GuardActions.Deny) { if (denyRule is null) { denyRule = r; denyDetail = detail; } }
            else if (askRule is null) { askRule = r; askDetail = detail; }
        }

        foreach (var r in active)
        {
            switch (r.Kind)
            {
                case GuardKinds.ScopeOnly:
                {
                    if (rootN is null) break;
                    var allow = SpecList(r.Spec, "allow");
                    var allowTemp = r.Spec["allowTemp"] is JsonValue av && av.TryGetValue<bool>(out var ab) ? ab : true;
                    var allowClaude = r.Spec["allowClaude"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) ? cb : true;
                    string? unresolvedDetail = null;
                    var denied = false;
                    foreach (var (rw, p, relUnres) in targets)
                    {
                        if (relUnres)
                        {
                            // cd $X / cd - gibi çözülemeyen klasörden sonra göreli hedef: proje dışı olabilir, kesin bilinmiyor
                            unresolvedDetail ??= rw.Why.Contains("xargs", StringComparison.Ordinal)
                                ? $"silinecek ya da yazılacak yollar başka bir komuttan geliyor ve çözülemedi, proje klasörünün ({rootN.Display}) dışında olabilir"
                                : $"«{rw.Raw}» göreli bir yol ve komutun hangi klasörde çalışacağı çözülemedi, proje klasörünün ({rootN.Display}) dışında olabilir";
                            continue;
                        }
                        if (p is null) continue;
                        if (GuardPaths.IsUnder(p.Key, rootN.Key)) continue;
                        if (allowTemp && input.Temp is not null && GuardPaths.Normalize(input.Temp, null, null, null) is { } tn && GuardPaths.IsUnder(p.Key, tn.Key)) continue;
                        if (allowClaude && IsClaudeOwnFile(p, input)) continue;
                        if (allow.Any(a => AllowedBy(a, p, rootN.Key, input))) continue;
                        Hit(r, ActionOf(r), $"«{p.Display}» proje klasörünün ({rootN.Display}) dışında. Yardımcı {GuardText.WhatPhrase(rw.Why)} çalıştı");
                        denied = true;
                        break;
                    }
                    if (!denied && unresolvedDetail is not null) Hit(r, GuardActions.Ask, unresolvedDetail);
                    break;
                }
                case GuardKinds.PathProtect:
                case GuardKinds.AskBefore:
                {
                    var action = ActionOf(r);
                    var globs = SpecList(r.Spec, "globs");
                    var hit = false;
                    foreach (var (rw, p, _) in targets)
                    {
                        if (p is null) continue;
                        var g = globs.FirstOrDefault(gl => gl == SozDataGlob ? dataN is not null && GuardPaths.IsUnder(p.Key, dataN.Key) : GuardPaths.GlobMatch(gl, p, rootN?.Key, input.Home, input.Temp));
                        if (g is not null) { Hit(r, action, $"«{p.Display}» korunan bir yol. Yardımcı {GuardText.WhatPhrase(rw.Why)} çalıştı"); hit = true; break; }
                    }
                    if (hit || r.Kind == GuardKinds.PathProtect) break;
                    goto case GuardKinds.CommandBlock;
                }
                case GuardKinds.CommandBlock:
                {
                    var action = ActionOf(r);
                    var patterns = SpecList(r.Spec, "patterns");
                    var regenOk = AllowsRegenerable(r);
                    foreach (var c in cmds)
                    {
                        if (regenOk && IsRegenerableDelete(c, rootN, input)) continue;
                        if (patterns.Contains(SozSelfGuard.Pattern) && SozSelfGuard.Hit(c, input.DataDir) is { } selfWhy)
                        {
                            Hit(r, action, $"«{(c.Flat.Length > 80 ? c.Flat[..80] + "…" : c.Flat)}» komutu: {selfWhy}");
                            break;
                        }
                        var pat = patterns.Where(pt => pt != SozSelfGuard.Pattern).FirstOrDefault(pt => PatternMatcher.Matches(pt, c));
                        if (pat is not null) { Hit(r, action, $"«{(c.Flat.Length > 80 ? c.Flat[..80] + "…" : c.Flat)}» komutuyla {GuardText.PatternPhrase(pat)} çalıştı"); break; }
                    }
                    break;
                }
            }
        }

        var targetList = targets.Select(t => t.Path?.Display ?? t.Raw.Raw).ToList();
        if (denyRule is not null) return new GuardDecision(GuardActions.Deny, denyRule, GuardText.Reason(denyRule, denyDetail!, deny: true), denyDetail, summary, unknown, targetList);
        if (askRule is not null) return new GuardDecision(GuardActions.Ask, askRule, GuardText.Reason(askRule, askDetail!, deny: false), askDetail, summary, unknown, targetList);
        return GuardDecision.AllowAll(summary, unknown);
    }

    private static string ActionOf(GuardRule r) => r.Action == GuardActions.Ask ? GuardActions.Ask : GuardActions.Deny;

    /// <summary>
    /// Komut zincirinde izlenen çalışma klasörünü (cd/pushd/-C ...) kancadan gelen cwd'ye uygular. Unresolved = bir adım çözülemedi
    /// (değişkenli ya da belirsiz); sonraki mutlak bir adım (cd /e/x, cd ~) bunu düzeltir.
    /// </summary>
    public static (string? Cwd, bool Unresolved) ResolveDir(DirState? dir, string? baseCwd, GuardInput input)
    {
        var cur = baseCwd; var unres = false;
        if (dir is null) return (cur, false);
        foreach (var step in dir.Steps)
        {
            if (step is null) { unres = true; continue; }
            if (unres)
            {
                if (!GuardPaths.IsRelative(step) && GuardPaths.Normalize(step, null, input.Home, input.Temp) is { } abs) { cur = abs.Display; unres = false; }
                continue;
            }
            var n = GuardPaths.Normalize(step, cur, input.Home, input.Temp);
            if (n is null) unres = true; else cur = n.Display;
        }
        return (cur, unres);
    }

    /// <summary>Hedef yolu, ait olduğu komutun çalışma klasörüne göre çözer. RelUnresolved = göreli yol ama klasör çözülemedi.</summary>
    public static (NPath? Path, bool RelUnresolved) ResolveTarget(WriteTarget t, string? baseCwd, GuardInput input)
    {
        var (cwd, unres) = ResolveDir(t.Dir, baseCwd, input);
        if (unres) return GuardPaths.IsRelative(t.Raw) ? (null, true) : (GuardPaths.Normalize(t.Raw, null, input.Home, input.Temp), false);
        return (GuardPaths.Normalize(t.Raw, cwd, input.Home, input.Temp), false);
    }

    /// <summary>Claude Code'un kendi çalışma dosyaları (plan, yapılacaklar, görev, proje belleği). scope_only bunları varsayılan olarak engellemez (spec.allowClaude=false ile kapatılır).</summary>
    public static readonly string[] ClaudeOwnGlobs = { "~/.claude/plans/**", "~/.claude/todos/**", "~/.claude/tasks/**", "~/.claude/projects/*/memory/**" };

    private static bool IsClaudeOwnFile(NPath p, GuardInput input)
    {
        if (string.IsNullOrEmpty(input.Home)) return false;
        return ClaudeOwnGlobs.Any(g => GuardPaths.GlobMatch(g, p, null, input.Home, input.Temp));
    }

    /// <summary>Proje içindeki yeniden üretilebilir klasörler: silinmeleri sorulmaz (toplu silme kuralında).</summary>
    public static readonly HashSet<string> RegenerableDirs = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", "dist", ".next", "target", "__pycache__" };

    private const string BulkDeleteLabel = "Toplu silme önce sorulur";

    /// <summary>spec.allowRegenerable; yoksa yalnız şablondan gelen toplu silme kuralında açık (eski kayıtlar için).</summary>
    private static bool AllowsRegenerable(GuardRule r)
    {
        if (r.Spec["allowRegenerable"] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return r.Origin == "template" && r.Label == BulkDeleteLabel;
    }

    /// <summary>Komut yalnız proje kökü altındaki yeniden üretilebilir klasörleri mi siliyor? Hedefin her biri çözülmeli, kökün altında olmalı ve son parçası listede olmalı.</summary>
    public static bool IsRegenerableDelete(Cmd c, NPath? rootN, GuardInput input)
    {
        if (rootN is null) return false;
        var ts = TargetExtractor.Extract(new[] { c });
        if (ts.Count == 0) return false;
        foreach (var t in ts)
        {
            if (t.Expands) return false;
            var raw = t.Raw.TrimEnd();
            if (raw.EndsWith("/*", StringComparison.Ordinal) || raw.EndsWith("\\*", StringComparison.Ordinal)) raw = raw[..^2];
            var (n, relUnres) = ResolveTarget(t with { Raw = raw }, input.Cwd ?? input.ProjectRoot, input);
            if (relUnres || n is null || !GuardPaths.IsUnder(n.Key, rootN.Key) || n.Key == rootN.Key.TrimEnd('/')) return false;
            var last = n.Key.TrimEnd('/');
            last = last[(last.LastIndexOf('/') + 1)..];
            if (!RegenerableDirs.Contains(last)) return false;
        }
        return true;
    }

    private static bool AllowedBy(string allow, NPath p, string rootKey, GuardInput input)
    {
        if (GuardPaths.GlobMatch(allow, p, rootKey, input.Home, input.Temp)) return true;
        var n = GuardPaths.Normalize(allow, input.ProjectRoot ?? input.Cwd, input.Home, input.Temp);
        return n is not null && GuardPaths.IsUnder(p.Key, n.Key);
    }

    public static List<string> SpecList(JsonObject spec, string key)
    {
        var res = new List<string>();
        if (spec[key] is JsonArray arr)
            foreach (var x in arr)
                if (x is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) res.Add(s);
        return res;
    }
}

/// <summary>Bekçi metinleri (tek yerde; sade Türkçe). Neden cümlesi kuralın etiketini içerir.</summary>
public static class GuardText
{
    public static string Reason(GuardRule r, string detail, bool deny)
        => deny
            ? $"Bekçi durdurdu: «{r.Label}» kuralı. {Cap(detail)}. Bu işlem yapılmadı. Gerekiyorsa Söz'de Bekçi sayfasından kuralı değiştirebilirsin."
            : $"Bekçi soruyor: «{r.Label}» kuralı. {Cap(detail)}. Devam edilsin mi?";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].TrimEnd('.');

    /// <summary>Dosya aracıyla yazma ve değiştirme için iç etiketler (<see cref="WhatPhrase"/> bunları sade söze çevirir).</summary>
    public const string WhyWrite = "dosya yazma";
    public const string WhyEdit = "dosya değiştirme";

    /// <summary>
    /// Yazma hedefinin iç nedenini ("rm (silme)", "curl -o" ...) "Yardımcı ___ çalıştı" cümlesine uyan sade parçaya çevirir.
    /// Komut adı, bayrak ve araç adı çıkmaz; ham neden yalnız kod içinde kalır.
    /// </summary>
    public static string WhatPhrase(string why)
    {
        var w = why.ToLowerInvariant();
        if (w == WhyWrite) return "buraya dosya yazmaya";
        if (w == WhyEdit) return "burayı değiştirmeye";
        if (w.Contains("(silme)") || w.Contains("-delete")) return "burayı silmeye";
        if (w.Contains("(taşıma)")) return "burayı taşımaya";
        if (w.Contains("(kopya hedefi)") || w.StartsWith("robocopy", StringComparison.Ordinal)) return "buraya kopyalamaya";
        if (w.StartsWith("curl", StringComparison.Ordinal) || w.StartsWith("wget", StringComparison.Ordinal) || w.Contains("-outfile")) return "buraya dosya indirmeye";
        if (w.StartsWith("tar", StringComparison.Ordinal) || w.StartsWith("unzip", StringComparison.Ordinal)) return "buraya dosya açmaya";
        if (w.StartsWith("git -c", StringComparison.Ordinal) || w.Contains("--prefix")) return "burada değişiklik yapmaya";
        if (w.StartsWith("sed -i", StringComparison.Ordinal)) return "burayı yerinde değiştirmeye";
        if (w.StartsWith("ln ", StringComparison.Ordinal)) return "buraya bağlantı kurmaya";
        return "buraya yazmaya";
    }

    /// <summary>Kuralın komut kalıbını ("rm -r|--recursive") "«komut» komutuyla ___ çalıştı" cümlesine uyan sade parçaya çevirir; kalıp ekranda görünmez.</summary>
    public static string PatternPhrase(string pattern)
    {
        var p = pattern.ToLowerInvariant();
        if (p.Contains("soz") || p.Contains("/api/")) return "Söz'ün ayarlarını ya da kayıtlarını değiştirmeye";
        if (p.Contains("push") && p.Contains("git")) return "değişiklikleri zorla göndermeye";
        if (p.StartsWith("git reset", StringComparison.Ordinal)) return "kaydedilmemiş değişiklikleri geri alıp silmeye";
        if (p.StartsWith("git clean", StringComparison.Ordinal) || p.StartsWith("git checkout", StringComparison.Ordinal) || p.StartsWith("git restore", StringComparison.Ordinal)) return "kaydedilmemiş değişiklikleri silmeye";
        if (p.StartsWith("find ", StringComparison.Ordinal) && p.Contains("-delete")) return "bulduğu dosyaları toplu silmeye";
        if (p.StartsWith("rm ", StringComparison.Ordinal) || p.StartsWith("rm|", StringComparison.Ordinal) || p.StartsWith("del|", StringComparison.Ordinal)
            || p.StartsWith("rimraf", StringComparison.Ordinal) || p.StartsWith("robocopy", StringComparison.Ordinal)) return "klasörü içindekilerle birlikte silmeye";
        if (p.Contains("publish") || p.StartsWith("gh release", StringComparison.Ordinal) || p.StartsWith("vercel", StringComparison.Ordinal)
            || p.StartsWith("docker push", StringComparison.Ordinal) || p.StartsWith("twine", StringComparison.Ordinal)) return "yayın yapmaya";
        if (p.StartsWith("taskkill", StringComparison.Ordinal) || p.StartsWith("stop-process", StringComparison.Ordinal) || p.StartsWith("pm2", StringComparison.Ordinal)
            || p.Contains("kill")) return "çalışan bir programı zorla kapatmaya";
        if (p.Contains("install") || p.Contains("global")) return "bilgisayara program kurmaya";
        if (p.StartsWith("format", StringComparison.Ordinal) || p.StartsWith("diskpart", StringComparison.Ordinal) || p.StartsWith("clear-disk", StringComparison.Ordinal)
            || p.StartsWith("mkfs", StringComparison.Ordinal) || p.StartsWith("fdisk", StringComparison.Ordinal)) return "diski silip biçimlendirmeye";
        return "durdurulması istenen bir işi yapmaya";
    }

    public static string Describe(GuardRule r)
    {
        // şablon cümlesi yalnız kural şablondaki hâlinde (aynı sor/durdur seçimi) kaldıysa geçerli; kullanıcı değiştirdiyse genel cümle yazılır
        if (r.Origin == "template" && GuardTemplates.All.Where(t => t.Rules.Any(x => x.Label == r.Label && x.Action == r.Action)).OrderBy(t => t.Rules.Count).FirstOrDefault() is { } tpl) return tpl.Description;
        var action = r.Action == GuardActions.Ask ? "önce sorulur" : "engellenir";
        switch (r.Kind)
        {
            case GuardKinds.ScopeOnly: return "Proje klasörünün dışına yazma/silme " + action + ".";
            case GuardKinds.PathProtect: return "Şu yollara yazma/silme " + action + ": " + string.Join(", ", GuardEngine.SpecList(r.Spec, "globs").Take(6)) + ".";
            default:
                var pats = GuardEngine.SpecList(r.Spec, "patterns");
                var globs = GuardEngine.SpecList(r.Spec, "globs");
                var parts = new List<string>();
                if (pats.Count > 0) parts.Add("şu komutlar " + action + ": " + string.Join(", ", pats.Take(6).Select(p => p == SozSelfGuard.Pattern ? "Söz'ü kendi eliyle kullanma" : p.StartsWith("re:", StringComparison.Ordinal) || p.StartsWith("rx:", StringComparison.Ordinal) ? "(desen)" : p)) + (pats.Count > 6 ? "…" : ""));
                if (globs.Count > 0) parts.Add("şu yollara yazma " + action + ": " + string.Join(", ", globs.Take(4)));
                var text = parts.Count == 0 ? "Kural boş" : string.Join(". ", parts.Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
                return char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }
    }
}
