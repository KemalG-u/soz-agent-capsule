using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Text;

namespace Soz.Core.Run;

/// <summary>Kullanıcıya gösterilen tek bir ilerleme satırı. Kind: progress | agent | error. Detail: ham olayın maskeli, kısaltılmış hâli ("ayrıntı" ile açılır).</summary>
public sealed record RunLine(string Kind, string Text, string? Detail);

/// <summary>
/// `claude -p --output-format stream-json --verbose` satırlarını sade Türkçe tek satıra çevirir.
/// Biçim, claude 2.1.322 ile gerçek çıktıdan doğrulandı: type=system (subtype init: session_id; hook_*, thinking_tokens atlanır),
/// type=assistant (message.content[]: thinking | tool_use{name,input} | text), type=user (tool_result), type=result (subtype, is_error, result, session_id), rate_limit_event (atlanır).
/// Araç adı kullanıcıya gösterilmez; bilinmeyen olay ve araç sessizce atlanır. Her metin sır süzgecinden geçer.
/// </summary>
public sealed class RunEventTranslator : Soz.Core.Agents.IAgentEventStream
{
    public const int MaxDetail = 1500;
    public const int MaxAgentLine = 240;
    public const int MaxAgentDetail = 6000;

    public string? SessionId { get; private set; }
    public bool ResultSeen { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultSubtype { get; private set; }
    public string? ResultText { get; private set; }
    public string? LastAgentText { get; private set; }
    public int? NumTurns { get; private set; }

    /// <summary>Harcama (12. işlem): sonuç olayındaki total_cost_usd ve usage alanları. Yoksa null.</summary>
    public Soz.Core.Agents.AgentUsage? Usage { get; private set; }

    /// <summary>Alt ajanın (parent_tool_use_id taşıyan) olayı: ana ajanın sözü, oturum durumu ya da ilerleme satırı sayılmaz; yalnız sayılır.</summary>
    public int SubAgentEvents { get; private set; }

    private readonly Dictionary<string, string> _toolNames = new();
    private readonly List<string> _pendingEdits = new();
    private string? _lastText;

    /// <summary>Bir stdout satırını işler; üretilen satırlar sırayla döner (bozuk JSON ve bilinmeyen olay: boş).</summary>
    public IReadOnlyList<RunLine> Feed(string line)
    {
        var outp = new List<RunLine>();
        if (string.IsNullOrWhiteSpace(line)) return outp;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return outp; }
        if (o is null) return outp;
        var type = Str(o, "type");
        switch (type)
        {
            case "system":
                if (Str(o, "subtype") == "init" && Str(o, "session_id") is { Length: > 0 } sid) SessionId = sid;
                break;
            case "assistant":
                if (IsSubAgent(o)) { SubAgentEvents++; break; }
                if (o["message"]?["content"] is JsonArray blocks)
                    foreach (var b in blocks.OfType<JsonObject>()) Assistant(b, line, outp);
                break;
            case "user":
                if (IsSubAgent(o)) { SubAgentEvents++; break; }
                if (o["message"]?["content"] is JsonArray ub)
                    foreach (var b in ub.OfType<JsonObject>()) UserResult(b, line, outp);
                break;
            case "result":
                Flush(outp);
                ResultSeen = true;
                ResultSubtype = Str(o, "subtype");
                ResultIsError = o["is_error"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb || (ResultSubtype is not null && ResultSubtype != "success");
                ResultText = Str(o, "result");
                if (Str(o, "session_id") is { Length: > 0 } rs) SessionId = rs;
                if (o["num_turns"] is JsonValue nv && nv.TryGetValue<int>(out var nt)) NumTurns = nt;
                Usage = ReadUsage(o);
                break;
        }
        return outp;
    }

    /// <summary>result olayından harcama: total_cost_usd (dolar, aracın kendi bildirdiği) ve usage{input_tokens, output_tokens, cache_read_input_tokens}. Hiçbiri yoksa null.</summary>
    internal static Soz.Core.Agents.AgentUsage? ReadUsage(JsonObject o)
    {
        double? cost = o["total_cost_usd"] is JsonValue cv && cv.TryGetValue<double>(out var cd) && cd >= 0 ? cd : null;
        var u = o["usage"] as JsonObject;
        var input = Num(u, "input_tokens");
        var cached = Num(u, "cache_read_input_tokens");
        var output = Num(u, "output_tokens");
        var usage = new Soz.Core.Agents.AgentUsage(input, cached, output, null, cost);
        return usage.IsEmpty ? null : usage;
    }

    private static long? Num(JsonObject? o, string k)
        => o?[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : o?[k] is JsonValue d && d.TryGetValue<double>(out var dd) ? (long)dd : null;

    private static bool IsSubAgent(JsonObject o)
        => o["parent_tool_use_id"] is JsonValue v && v.TryGetValue<string>(out var p) && !string.IsNullOrEmpty(p);

    /// <summary>Bekleyen "dosya değiştirdi" satırını bırakır (olay akışı bittiğinde ya da başka türden olaydan önce).</summary>
    public IReadOnlyList<RunLine> Flush()
    {
        var l = new List<RunLine>();
        Flush(l);
        return l;
    }

    private void Flush(List<RunLine> outp)
    {
        if (_pendingEdits.Count == 0) return;
        var files = _pendingEdits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingEdits.Clear();
        var text = files.Count == 1 ? $"{files[0]} dosyasını değiştirdi" : $"{files.Count} dosyayı değiştirdi";
        outp.Add(new RunLine("progress", Mask(text), Mask("değişen: " + string.Join(", ", files.Take(20)))));
        _lastText = text;
    }

    private void Assistant(JsonObject b, string rawLine, List<RunLine> outp)
    {
        var bt = Str(b, "type");
        if (bt == "thinking")
        {
            Flush(outp);
            Emit(outp, "progress", "düşünüyor", null);
            return;
        }
        if (bt == "text")
        {
            var text = Str(b, "text")?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            Flush(outp);
            LastAgentText = text;
            var plain = Plain(text);
            var first = plain.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Replace('\n', ' ').Trim() ?? "";
            outp.Add(new RunLine("agent", Mask(TextNormalizer.Truncate(first, MaxAgentLine)), Mask(TextNormalizer.Truncate(plain, MaxAgentDetail))));
            _lastText = null;
            return;
        }
        if (bt != "tool_use") return;

        var name = Str(b, "name") ?? "";
        var id = Str(b, "id");
        if (id is not null) _toolNames[id] = name;
        var input = b["input"] as JsonObject ?? new JsonObject();
        switch (name)
        {
            case "Edit" or "MultiEdit" or "Write" or "NotebookEdit":
                _pendingEdits.Add(FileName(Str(input, "file_path") ?? Str(input, "notebook_path")));
                return; // satır, değişiklik biter bitmez ("n dosyayı değiştirdi") bırakılır
        }
        Flush(outp);
        string? text2 = name switch
        {
            "Read" => $"{FileName(Str(input, "file_path"))} dosyasını okuyor",
            "Glob" or "LS" or "ListDirectory" => "dosyaları tarıyor",
            "Grep" => Str(input, "pattern") is { Length: > 0 } pat ? $"kodda arıyor: {TextNormalizer.Truncate(pat, 40)}" : "kodda arıyor",
            "Bash" or "PowerShell" => DescribeCommand(Str(input, "command")),
            "WebSearch" => "internette arıyor",
            "WebFetch" => "bir web sayfasını okuyor",
            "Task" or "Agent" => "bir alt yardımcı çalıştırıyor",
            "TodoWrite" or "TaskCreate" or "TaskUpdate" => "yapılacakları güncelliyor",
            _ => null, // bilinmeyen araç: sessizce atla
        };
        if (text2 is null) return;
        // Ham komut yalnız (maskeli) ayrıntıda durur; satırın kendisi sade adı taşır. Alt ajan satırı üst üste tekrar etmez.
        if (name is "Task" or "Agent" && text2 == _lastText) return;
        // Ayrıntı ham akış JSON'u değil, sade bir cümledir (denetim 3, Y8); maskelenmiş JSON bozulur, sade metin bozulmaz.
        Emit(outp, "progress", text2, ToolDetail(name, input));
    }

    private void UserResult(JsonObject b, string rawLine, List<RunLine> outp)
    {
        if (Str(b, "type") != "tool_result") return;
        var isError = b["is_error"] is JsonValue v && v.TryGetValue<bool>(out var e) && e;
        if (!isError) return;
        var tool = Str(b, "tool_use_id") is { } id && _toolNames.TryGetValue(id, out var n) ? n : "";
        if (tool is "Bash" or "PowerShell")
        {
            Flush(outp);
            Emit(outp, "progress", "bir komut hata verdi", ErrorDetail(b));
        }
    }

    private static string? ToolDetail(string name, JsonObject input)
    {
        string? v = name switch
        {
            "Bash" or "PowerShell" => Str(input, "command") is { } c ? "Komut: " + c : null,
            "Read" => Str(input, "file_path") is { } p ? "Dosya: " + p : null,
            "Grep" => Str(input, "pattern") is { } g ? "Arama: " + g + (Str(input, "path") is { Length: > 0 } gp ? " (" + gp + ")" : "") : null,
            "Glob" => Str(input, "pattern") is { } gl ? "Desen: " + gl : null,
            "LS" or "ListDirectory" => Str(input, "path") is { } lp ? "Klasör: " + lp : null,
            "WebSearch" => Str(input, "query") is { } q ? "Arama: " + q : null,
            "WebFetch" => Str(input, "url") is { } u ? "Adres: " + u : null,
            "Task" or "Agent" => Str(input, "description") is { } d ? "Görev: " + d : null,
            _ => null,
        };
        return v?.ReplaceLineEndings(" ");
    }

    private static string? ErrorDetail(JsonObject b)
    {
        string? text = null;
        if (b["content"] is JsonValue cv && cv.TryGetValue<string>(out var cs)) text = cs;
        else if (b["content"] is JsonArray arr)
            text = string.Join(" ", arr.OfType<JsonObject>().Select(x => Str(x, "text")).Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(text)) return null;
        return "Hata: " + text.Trim().ReplaceLineEndings(" ");
    }

    private void Emit(List<RunLine> outp, string kind, string text, string? rawDetail)
    {
        if (kind == "progress" && text == _lastText && text == "düşünüyor") return; // art arda "düşünüyor" tekrar etmesin
        _lastText = text;
        outp.Add(new RunLine(kind, Mask(text), rawDetail is null ? null : Mask(TextNormalizer.Truncate(rawDetail, MaxDetail))));
    }

    private static readonly Regex TestCmd = new(@"(?:^|[\s;&|])(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?(?:test|t)\b|\bnode\s+(?:--\S+\s+)*--test\b|\bpytest\b|\bdotnet\s+test\b|\bcargo\s+test\b|\bgo\s+test\b|\bvitest\b|\bjest\b|\bmocha\b|\bphpunit\b|\bctest\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallCmd = new(@"\b(?:npm|pnpm|yarn|bun)\s+(?:install|i|add|ci)\b|\bpip3?\s+install\b|\bdotnet\s+restore\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Komutu sade bir tanıma çevirir (testler, paket kurulumu, ya da kısaltılmış komut).</summary>
    public static string DescribeCommand(string? command)
    {
        var cmd = SecretFilter.MaskSecrets((command ?? "").Trim()); // önce maskele, sonra kısalt: yarım kalan sır sızmasın
        if (cmd.Length == 0) return "bir komut çalıştırıyor";
        if (TestCmd.IsMatch(cmd)) return "bir komut çalıştırıyor: testler";
        if (InstallCmd.IsMatch(cmd)) return "bir komut çalıştırıyor: paket kurulumu";
        if (BuildCmd.IsMatch(cmd)) return "bir komut çalıştırıyor: derleme";
        if (GitCmd.IsMatch(cmd)) return "bir komut çalıştırıyor: git durumu";
        if (ListCmd.IsMatch(cmd)) return "bir komut çalıştırıyor: dosya listeleme";
        if (ReadCmd.IsMatch(cmd)) return "bir komut çalıştırıyor: dosya okuma";
        return "bir komut çalıştırıyor"; // ham komut satıra çıkmaz (Ö-6); yalnız maskeli ayrıntıda
    }

    private const string Start = @"(?:^|[;&|]\s*)";
    private static readonly Regex BuildCmd = new(@"\b(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?build\b|\bdotnet\s+(?:build|publish|msbuild)\b|\bcargo\s+build\b|\bgo\s+build\b|" + Start + @"(?:tsc|make|msbuild|vite\s+build)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GitCmd = new(Start + @"git\s+(?:status|diff|log|show|branch)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ListCmd = new(Start + @"(?:ls|dir|tree|find|get-childitem|gci)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReadCmd = new(Start + @"(?:cat|type|head|tail|less|more|get-content|wc|sed\s+-n)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WinPath = new(@"[A-Za-z]:[\\/](?:[^\s""'`<>|*?\\/]+[\\/])*([^\s""'`<>|*?\\/]+)", RegexOptions.Compiled);
    private static readonly Regex UnixPath = new(@"(?<![\w.:/~])/(?:[\w.\-@]+/)+([\w.\-@]+)", RegexOptions.Compiled);
    private static readonly Regex ToolWord = new(@"\b(?:Bash|PowerShell|MultiEdit|NotebookEdit|TodoWrite|WebFetch|WebSearch|Read|Write|Edit|Grep|Glob)\s+(?:tool|aracı\w*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Ajan cümlesini sadeleştirir: tam yol yerine dosya adı, araç adı yerine "bir araç".</summary>
    public static string Plain(string text)
    {
        var t = WinPath.Replace(text, "$1");
        t = UnixPath.Replace(t, "$1");
        return ToolWord.Replace(t, "bir araç");
    }

    public static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "bir";
        var p = path.TrimEnd('\\', '/');
        var i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        return i >= 0 ? p[(i + 1)..] : p;
    }

    private static string Mask(string s) => SecretFilter.MaskSecrets(s);
    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
