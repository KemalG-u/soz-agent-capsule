using System.Text;

namespace Soz.Core.Guard;

/// <summary>Komut sözcüğü. Quoted = tırnak içinde (başlık/komut adı sayılmaz); Expands = $(..), `..` ya da değişken içeriyor.</summary>
public sealed record Tok(string Text, bool Quoted, bool Expands);

/// <summary>
/// Komut zincirinde izlenen çalışma klasörü: kancadan gelen cwd'ye sırayla uygulanan adımlar (cd/pushd/Set-Location/-C ... yolları).
/// Adım null = çözülemedi (cd $X, cd -, boş popd): bundan sonraki GÖRELİ yollar "çözülemedi" sayılır; sonraki mutlak bir adım durumu düzeltir.
/// </summary>
public sealed record DirState(IReadOnlyList<string?> Steps)
{
    public static readonly DirState Start = new(Array.Empty<string?>());
    public DirState Then(string? step) => new(Steps.Append(step).ToArray());
}

/// <summary>Ayrıştırma sırasında akan klasör durumu: geçerli klasör, alt kabuk yığını "( ... )" ve pushd yığını.</summary>
public sealed class DirCtx
{
    public DirState State;
    public readonly List<DirState> SubShell = new();
    public readonly List<DirState> Pushed = new();
    public DirCtx(DirState start) => State = start;
}

/// <summary>Ayrıştırılmış tek komut: baş (komut adı, küçük harf, yolsuz, uzantısız) + argümanlar + yönlendirme hedefleri.</summary>
public sealed class Cmd
{
    public string Head { get; set; } = "";
    public string HeadRaw { get; set; } = "";
    public List<Tok> Args { get; } = new();
    public List<(string Op, Tok Target)> Redirs { get; } = new();
    public bool Powershell { get; set; }
    /// <summary>Komut çalışırken kabuğun klasörü (yönlendirme hedefleri bunu kullanır).</summary>
    public DirState Dir { get; set; } = DirState.Start;
    /// <summary>Argümanların göreli çözüleceği klasör (env -C, wsl --cd gibi sarmalayıcılar değiştirir); null = Dir.</summary>
    public DirState? ArgDir { get; set; }
    /// <summary>xargs ile çalışıyor: silinecek/yazılacak yollar boru girdisinden gelir, komut satırından çıkarılamaz.</summary>
    public bool StdinTargets { get; set; }

    /// <summary>Komut adı ve bütün argümanlar, tırnak içi dahil (Flat tırnak içini boşaltır; adres/yol arayan "rx:" kalıpları bunu kullanır).</summary>
    public string Full
    {
        get
        {
            var sb = new StringBuilder(Head);
            foreach (var a in Args) sb.Append(' ').Append(a.Text);
            return sb.ToString();
        }
    }

    /// <summary>Tırnak içi içerik boşaltılmış düz metin (regex kalıpları için).</summary>
    public string Flat
    {
        get
        {
            var sb = new StringBuilder(Head);
            foreach (var a in Args) sb.Append(' ').Append(a.Quoted ? "\"\"" : a.Text);
            return sb.ToString();
        }
    }
}

/// <summary>
/// Bash/PowerShell komut metnini komutlara böler: tırnak, yorum, heredoc, $(..)/`..`, ;, &&, ||, |, &, yeni satır.
/// Sarmalayıcıları (sudo, env, bash -c, cmd /c, powershell -Command, xargs, find -exec, eval...) açar.
/// Tırnak içindeki metin KOMUT sayılmaz: echo "rm -rf /" bir rm komutu değildir.
/// </summary>
public static class ShellParser
{
    private const int MaxDepth = 5;

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "if", "then", "else", "elif", "fi", "do", "done", "while", "until", "for", "in", "case", "esac", "function", "time", "!", "{", "}", "select", "coproc" };

    public static List<Cmd> Parse(string text, bool powershell)
    {
        var res = new List<Cmd>();
        try { ParseInto(text ?? "", powershell, 0, res, DirState.Start); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException) { /* en iyi çaba */ }
        return res;
    }

    // ---------------------------------------------------------------- ayrıştırma
    private sealed class Seg
    {
        public List<Tok> Toks = new();
        public List<(string op, Tok target)> Redirs = new();
    }

    private static DirState ParseInto(string s, bool ps, int depth, List<Cmd> output, DirState startDir)
    {
        if (depth > MaxDepth) return startDir;
        var ctx = new DirCtx(startDir);
        var seg = new Seg();
        var word = new StringBuilder();
        bool wordQuoted = false, wordExpands = false, wordStarted = false;
        string? pendingRedir = null;
        var heredocs = new List<(string delim, bool dash)>();
        string? pendingHeredoc = null; bool pendingHeredocDash = false;
        var n = s.Length;

        void FlushWord()
        {
            if (!wordStarted) return;
            var t = new Tok(word.ToString(), wordQuoted, wordExpands);
            if (pendingHeredoc is not null) { heredocs.Add((t.Text, pendingHeredocDash)); pendingHeredoc = null; }
            else if (pendingRedir is not null)
            {
                if (pendingRedir != "<") seg.Redirs.Add((pendingRedir, t));
                pendingRedir = null;
            }
            else seg.Toks.Add(t);
            word.Clear(); wordQuoted = false; wordExpands = false; wordStarted = false;
        }

        void EndSegment()
        {
            FlushWord();
            pendingRedir = null;
            if (seg.Toks.Count > 0 || seg.Redirs.Count > 0) Emit(seg, ps, depth, output, ctx);
            seg = new Seg();
        }

        for (var i = 0; i < n; i++)
        {
            var ch = s[i];
            // ---- yorum
            if (ch == '#' && !wordStarted && (i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] is ';' or '&' or '|' or '(' or '{'))
            {
                while (i < n && s[i] != '\n') i++;
                i--; continue;
            }
            if (ps && ch == '<' && i + 1 < n && s[i + 1] == '#' && !wordStarted)
            {
                var end = s.IndexOf("#>", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 1; continue;
            }
            // ---- PowerShell here-string
            if (ps && ch == '@' && i + 1 < n && (s[i + 1] == '\'' || s[i + 1] == '"') && !wordStarted)
            {
                var q = s[i + 1];
                var close = s.IndexOf("\n" + q + "@", i + 2, StringComparison.Ordinal);
                var body = close < 0 ? s[(i + 2)..] : s[(i + 2)..close];
                if (q == '"') CollectSubstitutions(body, ps, depth, output, ref wordExpands, ctx.State);
                word.Append(body); wordQuoted = true; wordStarted = true;
                i = close < 0 ? n : close + 2; continue;
            }
            // ---- tırnaklar
            if (ch == '\'')
            {
                wordStarted = true; wordQuoted = true;
                var end = s.IndexOf('\'', i + 1);
                if (end < 0) { word.Append(s, i + 1, n - i - 1); i = n; break; }
                word.Append(s, i + 1, end - i - 1);
                i = end; continue;
            }
            if (ch == '"')
            {
                wordStarted = true; wordQuoted = true;
                i++;
                for (; i < n && s[i] != '"'; i++)
                {
                    var c = s[i];
                    if (!ps && c == '\\' && i + 1 < n && (s[i + 1] is '"' or '\\' or '$' or '`'))
                    { word.Append(s[i + 1]); i++; continue; }
                    if (ps && c == '`' && i + 1 < n) { word.Append(s[i + 1]); i++; continue; }
                    if (c == '$' && i + 1 < n && s[i + 1] == '(')
                    {
                        var close = FindParen(s, i + 1);
                        ParseInto(s.Substring(i + 2, Math.Max(0, close - i - 2)), ps, depth + 1, output, ctx.State);
                        word.Append("$()"); wordExpands = true; i = close; continue;
                    }
                    if (!ps && c == '`')
                    {
                        var close = s.IndexOf('`', i + 1);
                        if (close < 0) close = n;
                        ParseInto(s.Substring(i + 1, close - i - 1), ps, depth + 1, output, ctx.State);
                        word.Append("$()"); wordExpands = true; i = close; continue;
                    }
                    if (c == '$' && i + 1 < n && (char.IsLetter(s[i + 1]) || s[i + 1] == '_' || s[i + 1] == '{')) wordExpands = true;
                    word.Append(c);
                }
                continue;
            }
            // ---- kaçış
            if (ch == '\\' && !ps)
            {
                if (i + 1 < n)
                {
                    var nx = s[i + 1];
                    if (nx == '\n') { i++; continue; }
                    if (char.IsWhiteSpace(nx) || nx is '"' or '\'' or '$' or '\\' or ';' or '&' or '|' or '>' or '<' or '(' or ')' or '`' or '*' or '?' or '#')
                    { word.Append(nx); wordStarted = true; i++; continue; }
                }
                word.Append('\\'); wordStarted = true; continue; // Windows yolu: ters eğik çizgi olduğu gibi
            }
            if (ch == '`' && ps)
            {
                if (i + 1 < n) { word.Append(s[i + 1]); wordStarted = true; i++; }
                continue;
            }
            // ---- komut ikamesi
            if (ch == '$' && i + 1 < n && s[i + 1] == '(')
            {
                var close = FindParen(s, i + 1);
                ParseInto(s.Substring(i + 2, Math.Max(0, close - i - 2)), ps, depth + 1, output, ctx.State);
                word.Append("$()"); wordExpands = true; wordStarted = true; i = close; continue;
            }
            if (ch == '`' && !ps)
            {
                var close = s.IndexOf('`', i + 1);
                if (close < 0) close = n;
                ParseInto(s.Substring(i + 1, close - i - 1), ps, depth + 1, output, ctx.State);
                word.Append("$()"); wordExpands = true; wordStarted = true; i = close; continue;
            }
            if (ch == '$' && i + 1 < n && s[i + 1] == '{')
            {
                var close = s.IndexOf('}', i + 2);
                if (close < 0) close = n - 1;
                word.Append(s, i, close - i + 1); wordExpands = true; wordStarted = true; i = close; continue;
            }
            if (ch == '$' && i + 1 < n && (char.IsLetter(s[i + 1]) || s[i + 1] == '_' || s[i + 1] == '?' ))
                wordExpands = true;
            // ---- boşluk / ayırıcılar
            if (ch == '\n')
            {
                EndSegment();
                if (heredocs.Count > 0) i = SkipHeredocBodies(s, i + 1, heredocs) - 1;
                continue;
            }
            if (ch is ' ' or '\t' or '\r') { FlushWord(); continue; }
            if (ch == ';') { EndSegment(); continue; }
            if (ch == '|')
            {
                if (i + 1 < n && (s[i + 1] == '|' || s[i + 1] == '&')) i++;
                EndSegment(); continue;
            }
            if (ch == '&')
            {
                if (i + 1 < n && s[i + 1] == '>')
                {
                    FlushWord(); i++;
                    var op = "&>"; if (i + 1 < n && s[i + 1] == '>') { op = "&>>"; i++; }
                    pendingRedir = op; continue;
                }
                if (i + 1 < n && s[i + 1] == '&') { i++; EndSegment(); continue; }
                if (ps) { FlushWord(); seg.Toks.Add(new Tok("&", false, false)); continue; }
                EndSegment(); continue;
            }
            if (ch is '(' or ')')
            {
                EndSegment();
                // bash alt kabuğu: ( cd x && ... ) kapanınca klasör eski hâline döner. PowerShell parantezi kapsam açmaz.
                if (!ps)
                {
                    if (ch == '(') ctx.SubShell.Add(ctx.State);
                    else if (ctx.SubShell.Count > 0) { ctx.State = ctx.SubShell[^1]; ctx.SubShell.RemoveAt(ctx.SubShell.Count - 1); }
                }
                continue;
            }
            if (ch is '{' or '}')
            {
                if (ps || (!wordStarted && (i + 1 >= n || char.IsWhiteSpace(s[i + 1]) || s[i + 1] == ';')))
                { EndSegment(); continue; }
            }
            if (ch == '>')
            {
                // fd öneki (2>) : yalnız rakam olan, tırnaksız sözcük
                if (wordStarted && !wordQuoted && word.ToString().All(char.IsDigit)) { word.Clear(); wordStarted = false; }
                else FlushWord();
                var op = ">";
                if (i + 1 < n && s[i + 1] == '>') { op = ">>"; i++; }
                else if (i + 1 < n && s[i + 1] == '|') { op = ">|"; i++; }
                else if (i + 1 < n && s[i + 1] == '&')
                {
                    // 2>&1 / >&2 : dosya değil
                    i++;
                    var j = i + 1;
                    while (j < n && (char.IsDigit(s[j]) || s[j] == '-')) j++;
                    if (j > i + 1) { i = j - 1; continue; }
                    op = ">";
                }
                pendingRedir = op; continue;
            }
            if (ch == '<')
            {
                FlushWord();
                if (i + 1 < n && s[i + 1] == '<')
                {
                    i++;
                    if (i + 1 < n && s[i + 1] == '<') { i++; pendingRedir = "<"; continue; } // <<<
                    var dash = false;
                    if (i + 1 < n && s[i + 1] == '-') { dash = true; i++; }
                    pendingHeredoc = ""; pendingHeredocDash = dash; continue;
                }
                pendingRedir = "<"; continue;
            }
            word.Append(ch); wordStarted = true;
        }
        EndSegment();
        return ctx.State;
    }

    private static void CollectSubstitutions(string body, bool ps, int depth, List<Cmd> output, ref bool expands, DirState dir)
    {
        for (var i = 0; i + 1 < body.Length; i++)
        {
            if (body[i] == '$' && body[i + 1] == '(')
            {
                var close = FindParen(body, i + 1);
                ParseInto(body.Substring(i + 2, Math.Max(0, close - i - 2)), ps, depth + 1, output, dir);
                expands = true; i = close;
            }
        }
    }

    /// <summary>"(" konumundan eşleşen ")" konumu (tırnak ve heredoc farkında). Bulunamazsa metin sonu.</summary>
    private static int FindParen(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\') { i++; continue; }
            if (c == '\'') { var e = s.IndexOf('\'', i + 1); if (e < 0) return s.Length; i = e; continue; }
            if (c == '"')
            {
                for (i++; i < s.Length && s[i] != '"'; i++)
                {
                    if (s[i] == '\\') { i++; continue; }
                    if (s[i] == '$' && i + 1 < s.Length && s[i + 1] == '(') { i = FindParen(s, i + 1); }
                }
                continue;
            }
            if (c == '<' && i + 1 < s.Length && s[i + 1] == '<' && !(i + 2 < s.Length && s[i + 2] == '<'))
            {
                var j = i + 2; var dash = false;
                if (j < s.Length && s[j] == '-') { dash = true; j++; }
                while (j < s.Length && s[j] == ' ') j++;
                var q = j < s.Length && (s[j] == '\'' || s[j] == '"') ? s[j] : '\0';
                if (q != '\0') j++;
                var st = j;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                var delim = s[st..j];
                if (q != '\0' && j < s.Length) j++;
                if (delim.Length > 0)
                {
                    var nl = s.IndexOf('\n', j);
                    if (nl < 0) return s.Length;
                    var after = SkipHeredocBodies(s, nl + 1, new List<(string, bool)> { (delim, dash) });
                    i = Math.Max(nl, after - 1);
                    continue;
                }
            }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return i; }
        }
        return s.Length;
    }

    private static int SkipHeredocBodies(string s, int pos, List<(string delim, bool dash)> docs)
    {
        foreach (var (delim, dash) in docs.ToList())
        {
            while (pos < s.Length)
            {
                var nl = s.IndexOf('\n', pos);
                var line = nl < 0 ? s[pos..] : s[pos..nl];
                pos = nl < 0 ? s.Length : nl + 1;
                var cmp = line.TrimEnd('\r');
                if (dash) cmp = cmp.TrimStart('\t');
                if (cmp == delim) break;
            }
        }
        docs.Clear();
        return pos;
    }

    // ---------------------------------------------------------------- sarmalayıcıları aç
    private static void Emit(Seg seg, bool ps, int depth, List<Cmd> output, DirCtx ctx)
    {
        var toks = seg.Toks;
        var before = ctx.State; // komut çalışırken geçerli klasör (kendi cd'si henüz uygulanmadı)
        var iso = false;
        var viaXargs = false;
        var cmd = Resolve(toks, ps, depth, output, ctx, ref iso, ref viaXargs);
        if (cmd is null && seg.Redirs.Count == 0) return;
        cmd ??= new Cmd { Powershell = ps };
        if (viaXargs) cmd.StdinTargets = true;
        cmd.Dir = before;
        foreach (var r in seg.Redirs) cmd.Redirs.Add(r);
        output.Add(cmd);
        if (!iso) ApplyDirEffect(cmd, ctx);
    }

    private static readonly System.Text.RegularExpressions.Regex PwdVar = new(@"^\$\{?PWD\}?(?=[/\\]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | RegexSpeed.Compiled);

    /// <summary>cd, chdir, Set-Location (sl), pushd/Push-Location, popd/Pop-Location kabuğun klasörünü değiştirir.</summary>
    private static void ApplyDirEffect(Cmd c, DirCtx ctx)
    {
        switch (c.Head)
        {
            case "cd" or "chdir" or "sl" or "set-location" or "cd..":
                ctx.State = ctx.State.Then(c.Head == "cd.." ? ".." : CdStep(c));
                break;
            case "pushd" or "push-location":
            {
                ctx.Pushed.Add(ctx.State);
                ctx.State = ctx.State.Then(c.Args.Count == 0 ? null : CdStep(c)); // argümansız pushd (yer değiştirme) çözülemez
                break;
            }
            case "popd" or "pop-location":
                if (ctx.Pushed.Count > 0) { ctx.State = ctx.Pushed[^1]; ctx.Pushed.RemoveAt(ctx.Pushed.Count - 1); }
                else ctx.State = ctx.State.Then(null);
                break;
        }
    }

    /// <summary>cd hedefi: argümansız = ev klasörü ("~"); "cd -", +n ve boş sonuç = null (çözülemedi); $PWD/x = ./x.</summary>
    private static string? CdStep(Cmd c)
    {
        string? named = null;
        var pos = new List<Tok>();
        for (var i = 0; i < c.Args.Count; i++)
        {
            var t = c.Args[i];
            if (!t.Quoted && t.Text.Length > 1 && t.Text[0] == '-')
            {
                if (t.Text.Equals("-Path", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("-LiteralPath", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("-lp", StringComparison.OrdinalIgnoreCase))
                { if (i + 1 < c.Args.Count) { named = c.Args[i + 1].Text; i++; } }
                else if (t.Text.Equals("-StackName", StringComparison.OrdinalIgnoreCase)) i++;
                continue; // -P -L -e -@ -PassThru --
            }
            if (!t.Quoted && t.Text.Equals("/d", StringComparison.OrdinalIgnoreCase) && i + 1 < c.Args.Count) continue; // cmd: cd /d sürücü
            pos.Add(t);
        }
        var text = named ?? (pos.Count == 0 ? "~" : string.Join(' ', pos.Select(p => p.Text)));
        if (text == "-" || text.StartsWith('+')) return null;
        var m = PwdVar.Match(text);
        if (m.Success) text = "." + text[m.Length..];
        return text;
    }

    public static string NormalizeHead(string raw)
    {
        var h = raw.Replace('\\', '/');
        var slash = h.LastIndexOf('/');
        if (slash >= 0) h = h[(slash + 1)..];
        h = h.ToLowerInvariant();
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".ps1", ".com" })
            if (h.EndsWith(ext, StringComparison.Ordinal)) { h = h[..^ext.Length]; break; }
        return h;
    }

    private static bool IsAssignment(Tok t)
        => !t.Quoted && t.Text.Length > 1 && (char.IsLetter(t.Text[0]) || t.Text[0] == '_') && t.Text.Contains('=')
           && t.Text.IndexOf('=') is var eq && eq > 0 && t.Text[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Sarmalayıcıları (sudo, env, bash -c, cmd /c ...) açıp asıl komutu döndürür. iso = komut ayrı bir süreçte çalışacak
    /// (sudo/env/xargs/cmd /c ...): içindeki "cd" kabuğun klasörünü değiştirmez. cwd = bu komut çalışırken kabuğun klasörü.
    /// </summary>
    private static Cmd? Resolve(List<Tok> toks, bool ps, int depth, List<Cmd> output, DirCtx ctx, ref bool iso, ref bool viaXargs)
    {
        var list = new List<Tok>(toks);
        DirState? argDir = null; // env -C, wsl --cd, sudo -D: asıl komutun göreli yollarının çözüleceği klasör
        for (var guard = 0; guard < 12; guard++)
        {
            // baştaki anahtar sözcük / atama / çağrı işleçlerini at
            while (list.Count > 0 && ((!list[0].Quoted && (Keywords.Contains(list[0].Text) || list[0].Text is "&" or "." or "!")) || IsAssignment(list[0])))
                list.RemoveAt(0);
            if (list.Count == 0) return null;
            var headTok = list[0];
            if (headTok.Quoted && !ps) { /* "cmd" args: tırnaklı komut adı yine komuttur */ }
            var head = NormalizeHead(headTok.Text);
            var rest = list.GetRange(1, list.Count - 1);
            if (headTok.Expands && headTok.Text.StartsWith("$", StringComparison.Ordinal)) return null; // $cmd gibi: bilinmiyor
            var cur = argDir ?? ctx.State; // iç komutların başlangıç klasörü

            switch (head)
            {
                case "sudo" or "doas" or "command" or "exec" or "nohup" or "nice" or "time" or "builtin" or "stdbuf" or "setsid" or "ionice" or "unbuffer" or "call" or "start" or "runas":
                {
                    // command/builtin/time kabuğun kendi içindir ("command cd x" klasörü değiştirir); diğerleri ayrı süreç ya da kabuğu değiştirir
                    if (head is not ("command" or "builtin" or "time")) iso = true;
                    var i = 0;
                    var valueFlags = head is "sudo" or "doas" ? new[] { "-u", "-g", "-h", "-p", "-C", "-D" } : head is "nice" ? new[] { "-n" } : Array.Empty<string>();
                    if (head == "start" && rest.Count > 0 && rest[0].Quoted && rest[0].Text.Length == 0) i = 1;
                    else if (head == "start" && rest.Count > 1 && rest[0].Quoted && !rest[1].Quoted) i = 1; // start "başlık" komut
                    while (i < rest.Count && !rest[i].Quoted && rest[i].Text.StartsWith("-", StringComparison.Ordinal) || (head is "start" or "call" && i < rest.Count && rest[i].Text.StartsWith("/", StringComparison.Ordinal) && rest[i].Text.Length <= 3))
                    {
                        var skip = valueFlags.Contains(rest[i].Text) ? 2 : 1;
                        if (head is "sudo" or "doas" && rest[i].Text == "-D" && i + 1 < rest.Count) argDir = (argDir ?? ctx.State).Then(rest[i + 1].Text);
                        i += skip;
                    }
                    list = i >= rest.Count ? new List<Tok>() : rest.GetRange(i, rest.Count - i);
                    continue;
                }
                case "env":
                {
                    iso = true;
                    var i = 0;
                    while (i < rest.Count && ((!rest[i].Quoted && rest[i].Text.StartsWith("-", StringComparison.Ordinal)) || IsAssignment(rest[i])))
                    {
                        var t = rest[i];
                        if (!t.Quoted && t.Text is "-C" or "--chdir" && i + 1 < rest.Count) argDir = (argDir ?? ctx.State).Then(rest[i + 1].Text);
                        else if (!t.Quoted && t.Text.StartsWith("--chdir=", StringComparison.Ordinal)) argDir = (argDir ?? ctx.State).Then(t.Text["--chdir=".Length..]);
                        i += !t.Quoted && t.Text is "-u" or "-C" or "-S" or "--chdir" ? 2 : 1;
                    }
                    list = i >= rest.Count ? new List<Tok>() : rest.GetRange(i, rest.Count - i);
                    continue;
                }
                case "timeout":
                {
                    iso = true;
                    var i = 0;
                    while (i < rest.Count && rest[i].Text.StartsWith("-", StringComparison.Ordinal)) i += rest[i].Text is "-s" or "-k" ? 2 : 1;
                    if (i < rest.Count) i++; // süre
                    list = i >= rest.Count ? new List<Tok>() : rest.GetRange(i, rest.Count - i);
                    continue;
                }
                case "xargs":
                {
                    iso = true;
                    viaXargs = true;
                    var i = 0;
                    while (i < rest.Count && rest[i].Text.StartsWith("-", StringComparison.Ordinal)) i += rest[i].Text is "-I" or "-n" or "-P" or "-d" or "-L" or "-s" or "-E" ? 2 : 1;
                    list = i >= rest.Count ? new List<Tok>() : rest.GetRange(i, rest.Count - i);
                    continue;
                }
                case "wsl":
                {
                    iso = true;
                    var i = 0;
                    while (i < rest.Count && (rest[i].Text.StartsWith("-", StringComparison.Ordinal)))
                    {
                        if (rest[i].Text == "--cd" && i + 1 < rest.Count) argDir = (argDir ?? ctx.State).Then(rest[i + 1].Text);
                        i += rest[i].Text is "-d" or "-u" or "--distribution" or "--user" or "--cd" ? 2 : 1;
                    }
                    list = i >= rest.Count ? new List<Tok>() : rest.GetRange(i, rest.Count - i);
                    continue;
                }
                case "cmd":
                {
                    iso = true;
                    var i = 0;
                    while (i < rest.Count && !rest[i].Quoted && rest[i].Text.StartsWith("/", StringComparison.Ordinal) && !rest[i].Text.Equals("/c", StringComparison.OrdinalIgnoreCase) && !rest[i].Text.Equals("/k", StringComparison.OrdinalIgnoreCase) && !rest[i].Text.Equals("/r", StringComparison.OrdinalIgnoreCase)) i++;
                    if (i < rest.Count && rest[i].Text is var f && (f.Equals("/c", StringComparison.OrdinalIgnoreCase) || f.Equals("/k", StringComparison.OrdinalIgnoreCase) || f.Equals("/r", StringComparison.OrdinalIgnoreCase)))
                    {
                        var after = rest.GetRange(i + 1, rest.Count - i - 1);
                        if (after.Count == 1) { ParseInto(after[0].Text, false, depth + 1, output, cur); return null; }
                        list = after; continue;
                    }
                    return Make(headTok, rest, ps, argDir);
                }
                case "bash" or "sh" or "zsh" or "dash" or "ksh" or "fish" or "ash":
                {
                    for (var i = 0; i < rest.Count; i++)
                    {
                        var t = rest[i];
                        if (!t.Quoted && t.Text.StartsWith("-", StringComparison.Ordinal) && !t.Text.StartsWith("--", StringComparison.Ordinal) && t.Text.Contains('c') && i + 1 < rest.Count)
                        { ParseInto(rest[i + 1].Text, false, depth + 1, output, cur); return null; }
                    }
                    return Make(headTok, rest, ps, argDir);
                }
                case "powershell" or "pwsh":
                {
                    for (var i = 0; i < rest.Count; i++)
                    {
                        var t = rest[i].Text.ToLowerInvariant();
                        if (rest[i].Quoted) continue;
                        if (t is "-command" or "-c" or "-cmd" or "-com" or "-comm" or "-comma" or "-comman")
                        {
                            var after = rest.GetRange(i + 1, rest.Count - i - 1);
                            var code = after.Count == 1 ? after[0].Text : string.Join(' ', after.Select(a => a.Quoted ? "'" + a.Text + "'" : a.Text));
                            ParseInto(code, true, depth + 1, output, cur); return null;
                        }
                        if (t is "-encodedcommand" or "-enc" or "-e" or "-ec" or "-en" or "-enco" && i + 1 < rest.Count)
                        {
                            try { ParseInto(Encoding.Unicode.GetString(Convert.FromBase64String(rest[i + 1].Text)), true, depth + 1, output, cur); } catch (FormatException) { }
                            return null;
                        }
                        if (t is "-file" or "-f") return null; // betik içeriği görülemez
                    }
                    return Make(headTok, rest, ps, argDir);
                }
                case "eval" or "invoke-expression" or "iex":
                {
                    // eval kabuğun kendi içinde çalışır: içindeki cd sonraki komutları da etkiler
                    if (rest.Count > 0) ctx.State = ParseInto(string.Join(' ', rest.Select(r => r.Text)), ps, depth + 1, output, cur);
                    return null;
                }
                case "find" or "fd":
                {
                    // find <yollar> ... -exec KOMUT {} \; : {} find'ın BAŞLANGIÇ yollarıdır; iç komut her başlangıç yolu için ayrı ele alınır
                    // (aksi halde "find /dışarı -exec rm {} \;" hedefi "{}" göreli görünür ve proje içi sayılırdı).
                    var starts = head == "find" ? FindStartPaths(rest) : new List<Tok>();
                    for (var i = 0; i < rest.Count; i++)
                    {
                        if (rest[i].Text is not ("-exec" or "-execdir" or "-ok" or "-okdir" or "-x" or "--exec")) continue;
                        var sub = new List<Tok>();
                        var j = i + 1;
                        for (; j < rest.Count && rest[j].Text is not (";" or "+"); j++) sub.Add(rest[j]);
                        var variants = new List<List<Tok>>();
                        if (starts.Count > 0 && sub.Any(t => t.Text.Contains("{}", StringComparison.Ordinal)))
                            foreach (var sp in starts)
                                variants.Add(sub.Select(t => t.Text.Contains("{}", StringComparison.Ordinal) ? new Tok(t.Text.Replace("{}", sp.Text, StringComparison.Ordinal), t.Quoted || sp.Quoted, t.Expands || sp.Expands) : t).ToList());
                        else variants.Add(sub);
                        foreach (var v in variants)
                        {
                            var isoInner = false; var xargsInner = false;
                            var inner = Resolve(v, ps, depth + 1, output, ctx, ref isoInner, ref xargsInner);
                            if (inner is not null) { inner.Dir = argDir ?? ctx.State; output.Add(inner); }
                        }
                        i = j;
                    }
                    return Make(headTok, rest, ps, argDir);
                }
            }
            return Make(headTok, rest, ps, argDir);
        }
        return null;
    }

    /// <summary>find'ın başlangıç yolları: baştaki -H/-L/-P/-O seçeneklerinden sonra, ilk ifade belirtecine (-name, (, !, ...) kadar olan sözcükler. Yoksa ".".</summary>
    internal static List<Tok> FindStartPaths(IReadOnlyList<Tok> args)
    {
        var i = 0;
        while (i < args.Count && !args[i].Quoted && (args[i].Text is "-H" or "-L" or "-P" || (args[i].Text.StartsWith("-O", StringComparison.Ordinal) && args[i].Text.Length <= 3))) i++;
        var paths = new List<Tok>();
        for (; i < args.Count; i++)
        {
            var t = args[i];
            if (!t.Quoted && (t.Text.StartsWith("-", StringComparison.Ordinal) || t.Text is "(" or ")" or "!" or ",")) break;
            paths.Add(t);
        }
        if (paths.Count == 0) paths.Add(new Tok(".", false, false));
        return paths;
    }

    private static Cmd Make(Tok head, List<Tok> args, bool ps, DirState? argDir)
    {
        var c = new Cmd { Head = NormalizeHead(head.Text), HeadRaw = head.Text, Powershell = ps, ArgDir = argDir };
        c.Args.AddRange(args);
        return c;
    }
}
