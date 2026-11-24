using System.Text.RegularExpressions;

namespace Soz.Core.Guard;

/// <summary>Komuttan çıkarılan yazma/silme hedefi (ham metin; çözümleme motorda).</summary>
/// <summary>Dir = hedefin göreli çözüleceği klasör durumu (cd/pushd/-C izlenmiş); null = kancadan gelen cwd.</summary>
public sealed record WriteTarget(string Raw, bool Expands, string Why, DirState? Dir = null);

/// <summary>Komutun hangi yollara yazdığını/sildiğini çıkarır (en iyi çaba). Çıkarılamayan biçimler dürüstçe listelenir: bkz. GuardEngine.KnownGaps.</summary>
public static class TargetExtractor
{
    private static readonly HashSet<string> DeleteHeads = new() { "rm", "del", "erase", "rmdir", "rd", "unlink", "shred", "remove-item", "ri", "trash", "rimraf" };
    private static readonly HashSet<string> MoveHeads = new() { "mv", "move", "move-item", "mi", "ren", "rename", "rename-item", "rni" };
    private static readonly HashSet<string> CopyHeads = new() { "cp", "copy", "copy-item", "cpi", "xcopy", "robocopy", "install", "rsync" };
    private static readonly HashSet<string> CreateHeads = new() { "mkdir", "md", "touch", "truncate", "new-item", "ni" };
    private static readonly HashSet<string> WriteFirstHeads = new() { "set-content", "sc", "add-content", "ac", "out-file", "clear-content", "tee-object", "export-csv", "set-itemproperty", "tee", "new-item", "ni" };
    private static readonly HashSet<string> CmdFamily = new() { "del", "erase", "rd", "rmdir", "copy", "xcopy", "move", "mkdir", "md", "ren", "rename", "robocopy" };
    private static readonly HashSet<string> PsValueParams = new(StringComparer.OrdinalIgnoreCase)
        { "-encoding", "-value", "-itemtype", "-stream", "-width", "-delimiter", "-type", "-filter", "-include", "-exclude", "-erroraction", "-newname", "-credential", "-inputobject" };
    private static readonly HashSet<string> PsPathParams = new(StringComparer.OrdinalIgnoreCase) { "-path", "-literalpath", "-filepath", "-lp", "-outfile", "-destination" };

    private const string RedirWhy = "yönlendirme (";

    public static List<WriteTarget> Extract(IEnumerable<Cmd> cmds)
    {
        var all = new List<WriteTarget>();
        foreach (var c in cmds)
        {
            var local = new List<WriteTarget>();
            ExtractOne(c, local);
            ToolDirTargets(c, local);
            // Klasör damgası: yönlendirmeyi kabuk çözer (Dir); argümanları komut (ArgDir: env -C gibi sarmalayıcılar değiştirebilir).
            foreach (var t in local)
                all.Add(t.Dir is not null ? t : t with { Dir = t.Why.StartsWith(RedirWhy, StringComparison.Ordinal) ? c.Dir : c.ArgDir ?? c.Dir });
        }
        return all;
    }

    private static readonly HashSet<string> GitMutating = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "rm", "mv", "checkout", "switch", "restore", "reset", "clean", "commit", "merge", "rebase", "pull", "apply", "am",
        "cherry-pick", "revert", "stash", "init", "clone", "gc", "worktree", "submodule", "tag", "branch", "fetch", "config",
    };
    private static readonly HashSet<string> NpmMutating = new(StringComparer.OrdinalIgnoreCase)
        { "install", "i", "ci", "add", "remove", "rm", "uninstall", "update", "up", "init", "link", "dedupe", "prune", "rebuild" };

    /// <summary>
    /// Çalışma klasörünü bayrakla değiştiren araçlar: git -C yol, tar -C yol, npm --prefix yol. Komut yazan bir alt komutsa
    /// o klasörün kendisi yazma hedefi sayılır (yoksa "git -C ../baska checkout ." proje dışını değiştirir ama hedefsiz kalırdı).
    /// </summary>
    private static void ToolDirTargets(Cmd c, List<WriteTarget> list)
    {
        var a = c.Args;
        var steps = new List<string>();
        string? sub = null, why = null;
        switch (c.Head)
        {
            case "git":
                for (var i = 0; i < a.Count; i++)
                {
                    var t = a[i].Text;
                    if (a[i].Quoted || !t.StartsWith("-", StringComparison.Ordinal)) { sub = t.ToLowerInvariant(); break; }
                    if (t == "-C" && i + 1 < a.Count) { steps.Add(a[++i].Text); continue; }
                    if (t == "-c" && i + 1 < a.Count) { i++; continue; }
                    if (t == "--work-tree" && i + 1 < a.Count) { steps.Add(a[++i].Text); continue; }
                    if (t.StartsWith("--work-tree=", StringComparison.Ordinal)) steps.Add(t["--work-tree=".Length..]);
                }
                if (steps.Count == 0 || sub is null || !GitMutating.Contains(sub)) return;
                why = "git -C " + sub;
                break;
            case "tar" or "bsdtar":
            {
                var extract = false;
                for (var i = 0; i < a.Count; i++)
                {
                    var t = a[i].Text;
                    if ((t is "-C" or "--directory") && i + 1 < a.Count) { steps.Add(a[++i].Text); continue; }
                    if (t.StartsWith("--directory=", StringComparison.Ordinal)) { steps.Add(t["--directory=".Length..]); continue; }
                    if (t is "-x" or "--extract" or "--get") extract = true;
                    else if (!a[i].Quoted && i == 0 && t.Length > 1 && t[0] != '-' && t.Contains('x')) extract = true; // tar xzf a.tgz
                    else if (!a[i].Quoted && t.Length > 1 && t[0] == '-' && t[1] != '-' && t.Contains('x')) extract = true; // -xzf
                }
                if (steps.Count == 0 || !extract) return;
                why = "tar -C (açma)";
                break;
            }
            case "npm" or "pnpm" or "yarn" or "bun":
                for (var i = 0; i < a.Count; i++)
                {
                    var t = a[i].Text;
                    if ((t is "--prefix" or "--dir" or "--cwd" or "-C") && i + 1 < a.Count) { steps.Add(a[++i].Text); continue; }
                    if (t.StartsWith("--prefix=", StringComparison.Ordinal)) { steps.Add(t["--prefix=".Length..]); continue; }
                    if (t.StartsWith("--dir=", StringComparison.Ordinal)) { steps.Add(t["--dir=".Length..]); continue; }
                    if (t.StartsWith("--cwd=", StringComparison.Ordinal)) { steps.Add(t["--cwd=".Length..]); continue; }
                    if (sub is null && !t.StartsWith("-", StringComparison.Ordinal)) sub = t;
                }
                if (steps.Count == 0 || sub is null || !NpmMutating.Contains(sub)) return;
                why = c.Head + " --prefix " + sub;
                break;
            default:
                return;
        }
        var dir = c.ArgDir ?? c.Dir;
        foreach (var s in steps) dir = dir.Then(s);
        list.Add(new WriteTarget(".", false, why!, dir));
    }

    private static bool IsFlag(Tok t, string head)
    {
        if (t.Quoted || t.Text.Length < 2) return false;
        if (t.Text[0] == '-') return true;
        if (t.Text[0] == '/' && CmdFamily.Contains(head) && Regex.IsMatch(t.Text, @"^/[A-Za-z?]{1,3}(:.*)?$")) return true;
        return false;
    }

    private static string? NamedValue(List<Tok> args, params string[] names)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (!args[i].Quoted && names.Any(n => args[i].Text.Equals(n, StringComparison.OrdinalIgnoreCase))) return args[i + 1].Text;
        return null;
    }

    /// <summary>Bayrak olmayan, PowerShell değer-parametrelerinin değeri de olmayan argümanlar.</summary>
    private static List<Tok> Positional(Cmd c)
    {
        var res = new List<Tok>();
        for (var i = 0; i < c.Args.Count; i++)
        {
            var t = c.Args[i];
            if (IsFlag(t, c.Head))
            {
                if (PsValueParams.Contains(t.Text) || PsPathParams.Contains(t.Text)) i++; // değeri ayrıca ele alınır
                continue;
            }
            res.Add(t);
        }
        return res;
    }

    private static void Add(List<WriteTarget> list, Tok t, string why)
    {
        if (t.Text.Length == 0) return;
        if (GuardPaths.IsNullDevice(t.Text)) return;
        list.Add(new WriteTarget(t.Text, t.Expands, why));
    }

    /// <summary>Büyük/küçük harf duyarlı adlandırılmış değer (curl: -o ile -O farklıdır).</summary>
    private static string? CaseSensitiveValue(List<Tok> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Quoted) continue;
            if (args[i].Text == name && i + 1 < args.Count) return args[i + 1].Text;
            if (args[i].Text.StartsWith(name + "=", StringComparison.Ordinal)) return args[i].Text[(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>PowerShell -OutFile (ve en az 5 harfli kısaltması -OutF, -OutFi ...): indirmenin yazılacağı dosya.</summary>
    private static void AddOutFile(List<Tok> a, List<WriteTarget> list, string why)
    {
        for (var i = 0; i < a.Count - 1; i++)
        {
            var t = a[i];
            if (t.Quoted || t.Text.Length < 5 || t.Text[0] != '-') continue;
            if ("-outfile".StartsWith(t.Text, StringComparison.OrdinalIgnoreCase)) { list.Add(new WriteTarget(a[i + 1].Text, a[i + 1].Expands, why)); return; }
        }
    }

    private static void ExtractOne(Cmd c, List<WriteTarget> list)
    {
        foreach (var (op, t) in c.Redirs)
            if (op != "<") Add(list, t, "yönlendirme (" + op + ")");

        var h = c.Head;
        var a = c.Args;
        if (h.Length == 0) return;
        // xargs rm / xargs mv: silinecek yollar boru girdisinden gelir; çözülemez (scope_only kuralında SORULUR).
        if (c.StdinTargets && (DeleteHeads.Contains(h) || MoveHeads.Contains(h)))
            list.Add(new WriteTarget(".", true, h + " (hedefler xargs ile boru girdisinden gelir)", DirState.Start.Then(null)));

        if (DeleteHeads.Contains(h))
        {
            foreach (var t in Positional(c)) Add(list, t, h + " (silme)");
            var named = NamedValue(a, "-path", "-literalpath");
            if (named is not null) list.Add(new WriteTarget(named, false, h + " (silme)"));
            return;
        }
        if (MoveHeads.Contains(h))
        {
            foreach (var t in Positional(c)) Add(list, t, h + " (taşıma)");
            var named = NamedValue(a, "-path", "-literalpath", "-destination");
            if (named is not null) list.Add(new WriteTarget(named, false, h + " (taşıma)"));
            return;
        }
        if (CopyHeads.Contains(h))
        {
            var dest = NamedValue(a, "-destination", "-t", "--target-directory");
            if (dest is null)
            {
                var eq = a.FirstOrDefault(x => x.Text.StartsWith("--target-directory=", StringComparison.Ordinal));
                if (eq is not null) dest = eq.Text["--target-directory=".Length..];
            }
            if (dest is not null) { list.Add(new WriteTarget(dest, false, h + " (kopya hedefi)")); return; }
            var pos = Positional(c);
            if (h == "robocopy") { if (pos.Count >= 2) Add(list, pos[1], "robocopy (hedef)"); }
            else if (pos.Count >= 2) Add(list, pos[^1], h + " (kopya hedefi)");
            return;
        }
        if (CreateHeads.Contains(h) || WriteFirstHeads.Contains(h))
        {
            var named = NamedValue(a, "-path", "-literalpath", "-filepath", "-lp", "-outfile");
            if (named is not null) { list.Add(new WriteTarget(named, false, h + " (yazma)")); return; }
            var pos = Positional(c).Where(t => !(h == "truncate" && t.Text.All(char.IsDigit))).ToList();
            if (h is "mkdir" or "md" or "touch" or "truncate" or "tee") foreach (var t in pos) Add(list, t, h + " (yazma)");
            else if (pos.Count > 0) Add(list, pos[0], h + " (yazma)");
            return;
        }
        switch (h)
        {
            case "sed":
            {
                var inPlace = a.Any(t => !t.Quoted && (t.Text.StartsWith("--in-place", StringComparison.Ordinal)
                    || (t.Text.StartsWith("-", StringComparison.Ordinal) && !t.Text.StartsWith("--", StringComparison.Ordinal) && t.Text.Length < 12 && t.Text.Contains('i'))));
                if (!inPlace) return;
                var hasScriptFlag = a.Any(t => !t.Quoted && (t.Text is "-e" or "-f" or "--expression" or "--file" || t.Text.StartsWith("--expression=", StringComparison.Ordinal)));
                var pos = new List<Tok>();
                for (var i = 0; i < a.Count; i++)
                {
                    if (!a[i].Quoted && a[i].Text.StartsWith("-", StringComparison.Ordinal) && a[i].Text.Length > 1) { if (a[i].Text is "-e" or "-f") i++; continue; }
                    pos.Add(a[i]);
                }
                foreach (var t in pos.Skip(hasScriptFlag ? 0 : 1)) Add(list, t, "sed -i (yerinde düzenleme)");
                return;
            }
            case "dd":
                foreach (var t in a.Where(x => x.Text.StartsWith("of=", StringComparison.OrdinalIgnoreCase))) list.Add(new WriteTarget(t.Text[3..], t.Expands, "dd of="));
                return;
            case "curl":
            {
                // -o dosya, -sSLo dosya (küme), -odosya, --output dosya; -O (uzak ad) cwd'ye yazar, --output-dir klasörü değiştirir.
                var dirOpt = CaseSensitiveValue(a, "--output-dir");
                var any = false;
                for (var i = 0; i < a.Count; i++)
                {
                    var t = a[i];
                    if (t.Quoted || t.Text.Length < 2 || t.Text[0] != '-') continue;
                    if (t.Text == "--output" && i + 1 < a.Count) { list.Add(new WriteTarget(a[++i].Text, false, "curl -o")); any = true; }
                    else if (t.Text.StartsWith("--output=", StringComparison.Ordinal)) { list.Add(new WriteTarget(t.Text["--output=".Length..], false, "curl -o")); any = true; }
                    else if (t.Text[1] != '-' && t.Text.Length >= 2 && t.Text.Skip(1).All(char.IsLetter) && t.Text[^1] == 'o' && i + 1 < a.Count) { list.Add(new WriteTarget(a[++i].Text, false, "curl -o")); any = true; }
                    else if (t.Text[1] != '-' && t.Text.Length > 2 && t.Text[1] == 'o') { list.Add(new WriteTarget(t.Text[2..], false, "curl -o")); any = true; }
                }
                if (dirOpt is not null) list.Add(new WriteTarget(dirOpt, false, "curl --output-dir"));
                else if (!any && a.Any(t => !t.Quoted && (t.Text == "--remote-name" || t.Text == "-O" || (t.Text.Length > 2 && t.Text[0] == '-' && t.Text[1] != '-' && t.Text.Skip(1).All(char.IsLetter) && t.Text.Contains('O')))))
                    list.Add(new WriteTarget(".", false, "curl -O (çalışma klasörüne yazar)"));
                AddOutFile(a, list, "curl -OutFile");
                return;
            }
            case "wget":
            {
                // -O dosya / --output-document, -P klasör / --directory-prefix, -o günlük dosyası; küçük -p (sayfa gereksinimleri) hedef değildir.
                for (var i = 0; i < a.Count; i++)
                {
                    var t = a[i];
                    if (t.Quoted) continue;
                    if ((t.Text is "-O" or "--output-document") && i + 1 < a.Count) list.Add(new WriteTarget(a[++i].Text, false, "wget -O"));
                    else if (t.Text.StartsWith("--output-document=", StringComparison.Ordinal)) list.Add(new WriteTarget(t.Text["--output-document=".Length..], false, "wget -O"));
                    else if ((t.Text is "-P" or "--directory-prefix") && i + 1 < a.Count) list.Add(new WriteTarget(a[++i].Text, false, "wget -P"));
                    else if (t.Text.StartsWith("--directory-prefix=", StringComparison.Ordinal)) list.Add(new WriteTarget(t.Text["--directory-prefix=".Length..], false, "wget -P"));
                    else if ((t.Text is "-o" or "--output-file") && i + 1 < a.Count) list.Add(new WriteTarget(a[++i].Text, false, "wget -o (günlük dosyası)"));
                }
                AddOutFile(a, list, "wget -OutFile");
                return;
            }
            case "invoke-webrequest" or "iwr" or "invoke-restmethod" or "irm":
                AddOutFile(a, list, h + " -OutFile");
                return;
            case "find":
            {
                // find <yollar> ... -delete : başlangıç yollarının altını siler
                if (!a.Any(t => !t.Quoted && t.Text == "-delete")) return;
                foreach (var sp in ShellParser.FindStartPaths(a)) Add(list, sp, "find -delete");
                return;
            }
            case "ln":
            {
                // ln -s kaynak HEDEF: bağlantının kendisi hedefte oluşur (kaynağa yazılmaz)
                var td = NamedValue(a, "-t", "--target-directory");
                if (td is not null) { list.Add(new WriteTarget(td, false, "ln (bağlantı hedefi)")); return; }
                var pos = Positional(c);
                if (pos.Count >= 2) Add(list, pos[^1], "ln (bağlantı hedefi)");
                return;
            }
            case "unzip":
            {
                var o = NamedValue(a, "-d");
                if (o is not null) list.Add(new WriteTarget(o, false, "unzip -d"));
                return;
            }
        }
    }
}

/// <summary>Bekçi kalıbı eşleyici. Sözdizimi: "komut alt-komut -bayrak|-alternatif" (bkz. GuardTemplates). "re:" öneki ile düz regex.</summary>
public static class PatternMatcher
{
    public static bool Matches(string pattern, Cmd cmd)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        if (pattern.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Regex.IsMatch(cmd.Flat, pattern[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return false; }
        }
        // "rx:" = düz regex, ama komut adı + bütün argümanlar (tırnak içi dahil) üzerinde: adres ve yol arayan kalıplar için ("re:" tırnak içini boşaltır).
        if (pattern.StartsWith("rx:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Regex.IsMatch(cmd.Full, pattern[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return false; }
        }
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var heads =parts[0].Split('|', StringSplitOptions.RemoveEmptyEntries).Select(ShellParser.NormalizeHead).ToArray();
        if (!heads.Contains(cmd.Head)) return false;
        if (IsProbeOnly(cmd)) return false;
        var cmdlet = cmd.Head.Contains('-');
        var words = new List<string[]>();
        var flags = new List<string[]>();
        foreach (var p in parts.Skip(1))
        {
            var alts = p.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (p[0] is '-' or '/') flags.Add(alts); else words.Add(alts);
        }
        // alt-komut sözcükleri sırayla geçmeli
        var idx = 0;
        foreach (var w in words)
        {
            var found = false;
            for (; idx < cmd.Args.Count; idx++)
            {
                var t = cmd.Args[idx];
                if (!w.Any(x => x.Equals(t.Text, StringComparison.OrdinalIgnoreCase))) continue;
                // "npm run publish" bir betik adıdır, "npm publish" alt komutu değil
                if (idx > 0 && ScriptRunners.Contains(cmd.Args[idx - 1].Text)) continue;
                found = true; idx++; break;
            }
            if (!found) return false;
        }
        foreach (var f in flags)
            if (!cmd.Args.Any(t => FlagHit(f, t, cmdlet))) return false;
        return true;
    }

    private static readonly HashSet<string> ScriptRunners = new(StringComparer.OrdinalIgnoreCase) { "run", "run-script" };

    /// <summary>Hiçbir şeyi değiştirmeyen yoklama çağrıları: "kill -0 PID" (süreç var mı), "kill -l" (sinyal listesi).</summary>
    public static bool IsProbeOnly(Cmd cmd)
    {
        if (cmd.Head != "kill") return false;
        for (var i = 0; i < cmd.Args.Count; i++)
        {
            var a = cmd.Args[i].Text;
            if (a is "-0" or "-l" or "-L" or "--list" or "--table") return true;
            if (a is "-s" or "--signal" && i + 1 < cmd.Args.Count && cmd.Args[i + 1].Text == "0") return true;
        }
        return false;
    }

    private static bool FlagHit(string[] alts, Tok t, bool cmdlet)
    {
        var a = t.Text;
        if (a.Length < 2 || (a[0] != '-' && a[0] != '/')) return false;
        foreach (var f in alts)
        {
            if (a.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
            // kısa bayrak kümesi: -r tek harfi -rf, -fr, -Rf içinde de aranır
            if (f.Length == 2 && f[0] == '-' && a[0] == '-' && !a.StartsWith("--", StringComparison.Ordinal) && a.Length <= 6 && !a.Contains('='))
                if (a.Skip(1).Any(ch => char.ToLowerInvariant(ch) == char.ToLowerInvariant(f[1]))) return true;
            // PowerShell parametre kısaltması: -rec, -recurse
            if (cmdlet && f.Length > 3 && f[0] == '-' && a[0] == '-' && a.Length >= 2 && f.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return true;
            // --flag=değer
            if (f.StartsWith("--", StringComparison.Ordinal) && a.StartsWith(f + "=", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
