using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Text;

namespace Soz.Core.Agents;

/// <summary>
/// Antigravity CLI (`agy`) bağlayıcısı.
/// Başlatma: `agy -p --output-format stream-json --mode auto --add-dir &lt;proje&gt;` (salt okunur kipte `--mode plan`), istem standart girdiden verilir.
/// Devam turu: aynı komut + `--conversation &lt;sessionId&gt;`, istem standart girdiden verilir.
/// </summary>
public sealed class AntigravityConnector : IAgentConnector
{
    public const string NotFoundText = "Antigravity bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.";
    public const string ShimText = "Antigravity açılamadı. Ayarlar'dan yolunu kontrol et.";

    private readonly AgentIdentity _identity;
    public AntigravityConnector(AgentIdentity? identity = null) => _identity = identity ?? new AgentIdentity("antigravity", "Antigravity", "antigravity",
        new AgentCapabilities(Headless: true, EventStream: true, Resume: true, Stop: true,
            BeforeTool: GuardTier.None, TurnEnd: TurnEndMode.OuterLoop, Ask: AskMode.ResumeReply, Cost: CostMode.Tokens), "agy");

    public string Id => _identity.Id;
    public string DisplayName => _identity.DisplayName;
    public string SessionTool => _identity.SessionTool;
    public AgentCapabilities Capabilities => _identity.Capabilities;

    public AgentLocation Locate(SozSettings settings)
        => AgentLocator.Resolve(AgentRegistry.ConfiguredPath(Id, settings), _identity.Command, NotFoundText, ShimText);

    public string? ReadVersion(LaunchCommand cmd, TimeSpan? timeout = null) => AgentVersion.Read(cmd, this, timeout);

    public string DefaultModel(SozSettings settings) => "";

    public AgentLaunch BuildStart(LaunchCommand cmd, AgentRunContext ctx)
        => Launch(cmd, Common(cmd, ctx), ctx);

    public AgentLaunch BuildResume(LaunchCommand cmd, AgentRunContext ctx, string sessionId)
    {
        var args = Common(cmd, ctx);
        args.Add("--conversation");
        args.Add(sessionId);
        return Launch(cmd, args, ctx);
    }

    private static List<string> Common(LaunchCommand cmd, AgentRunContext ctx)
    {
        var mode = ctx.Permission == AgentPermissionMode.ReadOnly ? "plan" : "auto";
        var args = new List<string>(cmd.PrefixArgs)
        {
            "-p",
            "--output-format", "stream-json",
            "--mode", mode,
            "--add-dir", ctx.ProjectPath,
        };
        if (ctx.Model.Length > 0)
        {
            args.Add("--model");
            args.Add(ctx.Model);
        }
        return args;
    }

    private AgentLaunch Launch(LaunchCommand cmd, List<string> args, AgentRunContext ctx)
        => new(cmd.FileName, args, ctx.Prompt, AgentEnv.Merge(cmd, ShouldStripEnv), AgentEnv.StripList(ShouldStripEnv), cmd.Display);

    public string DescribePermission(AgentPermissionMode mode)
        => mode == AgentPermissionMode.ReadOnly ? "--mode plan" : "--mode auto";

    public IAgentEventStream CreateEventStream() => new AntigravityEventStream();

    public void Stop(ProcessTree tree) => tree.KillTree();

    public bool ShouldStripEnv(string name) => false;

    private static readonly Regex LoginRe = new(@"(not logged in|sign in|authenticate|authentication|unauthorized|\b401\b|invalid credentials|agy auth)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LimitRe = new(@"(usage limit|rate limit|rate-limited|limit reached|quota|resource_exhausted|insufficient[_ ]quota|too many requests|\b429\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string FailureReason(int? exit, IAgentEventStream events, string stderr)
    {
        var blob = string.Join("\n", new[] { events.ResultText, (events as AntigravityEventStream)?.LastError, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (LoginRe.IsMatch(blob))
            return "Antigravity hesabına giriş yapılmamış görünüyor. Antigravity uygulamasını bir kez aç ve hesabınla giriş yap, sonra işi yeniden başlat.";
        if (LimitRe.IsMatch(blob))
            return "Antigravity kullanım sınırına takıldı. Sınır açılınca işi yeniden başlat.";
        return AgentText.GenericFailure(exit, blob);
    }
}

/// <summary>
/// `agy -p --output-format stream-json` çıktı satırlarını sade Türkçe satırlara çevirir.
/// Hem Claude benzeri `assistant/user/result` zarfını hem düz `init/text/tool_use/tool_call/tool_result/turn_complete` JSON satırlarını esnek biçimde ayrıştırır.
/// </summary>
public sealed class AntigravityEventStream : IAgentEventStream
{
    public string? SessionId { get; private set; }
    public bool ResultSeen { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultText { get; private set; }
    public string? LastAgentText { get; private set; }
    public int? NumTurns { get; private set; }
    public AgentUsage? Usage { get; private set; }
    public string? LastError { get; private set; }

    private readonly System.Text.StringBuilder _text = new();
    private readonly List<string> _pendingEdits = new();
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private string? _lastProgress;

    public IReadOnlyList<RunLine> Feed(string line)
    {
        var outp = new List<RunLine>();
        if (string.IsNullOrWhiteSpace(line)) return outp;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (Exception) { o = null; }
        if (o is null) return outp;

        CaptureSessionId(o);
        var type = Str(o, "type") ?? "";
        switch (type)
        {
            case "system" or "init" or "session":
                break;

            case "text" or "delta" or "content_block_delta":
            {
                var delta = Str(o, "text") ?? (o["delta"] is JsonObject d ? Str(d, "text") : null);
                if (!string.IsNullOrEmpty(delta)) _text.Append(delta);
                break;
            }

            case "assistant" or "message":
            {
                var role = Str(o, "role") ?? (o["message"] is JsonObject mo ? Str(mo, "role") : null);
                if (type == "message" && role == "user") break;
                HandleAssistantMessage(o, outp);
                break;
            }

            case "user":
                HandleUserToolResult(o, outp);
                break;

            case "tool_use" or "tool_call":
                FlushText(outp);
                HandleToolUse(o, outp);
                break;

            case "tool_result":
                HandleFlatToolResult(o, outp);
                break;

            case "error":
            {
                var err = Str(o, "error") ?? Str(o, "message");
                if (!string.IsNullOrWhiteSpace(err))
                {
                    LastError = TextNormalizer.Truncate(SecretFilter.MaskSecrets(err.Trim()), 300);
                    ResultIsError = true;
                }
                break;
            }

            case "result" or "turn_complete" or "done":
                FlushText(outp);
                FlushEdits(outp);
                HandleResult(o, outp);
                break;
        }
        return outp;
    }

    public IReadOnlyList<RunLine> Flush()
    {
        var list = new List<RunLine>();
        FlushText(list);
        FlushEdits(list);
        return list;
    }

    private void CaptureSessionId(JsonObject o)
    {
        var sid = Str(o, "conversation_id") ?? Str(o, "session_id") ?? Str(o, "conversationId") ?? Str(o, "sessionId");
        if (!string.IsNullOrWhiteSpace(sid)) SessionId = sid.Trim();
    }

    private void HandleAssistantMessage(JsonObject o, List<RunLine> outp)
    {
        var msg = o["message"] as JsonObject ?? o;
        if (msg["content"] is JsonArray arr)
        {
            foreach (var item in arr.OfType<JsonObject>())
            {
                var btype = Str(item, "type");
                if (btype == "text" && Str(item, "text") is { Length: > 0 } bt)
                    _text.Append(bt);
                else if (btype is "tool_use" or "tool_call")
                {
                    FlushText(outp);
                    HandleToolUse(item, outp);
                }
            }
            FlushText(outp);
        }
        else if (Str(msg, "content") is { Length: > 0 } contentStr)
        {
            var delta = o["delta"] is JsonValue dv && dv.TryGetValue<bool>(out var isDelta) && isDelta;
            _text.Append(contentStr);
            if (!delta) FlushText(outp);
        }
        else if (Str(o, "text") is { Length: > 0 } txt)
        {
            _text.Append(txt);
            FlushText(outp);
        }
        CaptureUsage(msg["usage"] as JsonObject ?? o["usage"] as JsonObject);
    }

    private void HandleUserToolResult(JsonObject o, List<RunLine> outp)
    {
        var msg = o["message"] as JsonObject ?? o;
        if (msg["content"] is not JsonArray arr) return;
        foreach (var item in arr.OfType<JsonObject>())
            if (Str(item, "type") == "tool_result")
                HandleFlatToolResult(item, outp);
    }

    private void HandleToolUse(JsonObject o, List<RunLine> outp)
    {
        var name = Str(o, "name") ?? Str(o, "tool_name") ?? Str(o, "tool") ?? "";
        var callId = Str(o, "id") ?? Str(o, "tool_call_id") ?? Str(o, "call_id");
        if (!string.IsNullOrEmpty(callId)) _toolNames[callId] = name;
        var input = o["input"] as JsonObject
            ?? o["arguments"] as JsonObject
            ?? o["args"] as JsonObject
            ?? o["parameters"] as JsonObject
            ?? (Str(o, "arguments") is { Length: > 1 } rawArgs && rawArgs.TrimStart().StartsWith('{') ? ParseSafeObj(rawArgs) : null)
            ?? new JsonObject();
        var lower = name.ToLowerInvariant();

        if (lower is "write_file" or "edit_file" or "write" or "edit" or "multiedit" or "patch" or "apply_patch" or "replace_file_content" or "multi_replace_file_content")
        {
            var p = Str(input, "Path") ?? Str(input, "path") ?? Str(input, "file_path") ?? Str(input, "TargetFile");
            _pendingEdits.Add(RunEventTranslator.FileName(p));
            return;
        }

        FlushEdits(outp);
        var (text, detail) = lower switch
        {
            "run_command" or "bash" or "terminal" or "shell" or "exec_command" => (
                RunEventTranslator.DescribeCommand(Str(input, "CommandLine") ?? Str(input, "command") ?? Str(input, "cmd")),
                (Str(input, "CommandLine") ?? Str(input, "command") ?? Str(input, "cmd")) is { } c ? "Komut: " + c : null),
            "read_file" or "read" or "view_file" => (
                $"{RunEventTranslator.FileName(Str(input, "Path") ?? Str(input, "path") ?? Str(input, "file_path"))} dosyasını okuyor",
                (Str(input, "Path") ?? Str(input, "path") ?? Str(input, "file_path")) is { } p ? "Dosya: " + p : null),
            "grep_search" or "grep" or "search_files" or "find_by_name" or "glob" => (
                (Str(input, "Query") ?? Str(input, "pattern") ?? Str(input, "Pattern")) is { Length: > 0 } pat
                    ? $"kodda arıyor: {TextNormalizer.Truncate(pat, 40)}"
                    : "dosyaları tarıyor",
                (Str(input, "Query") ?? Str(input, "pattern") ?? Str(input, "Pattern")) is { } g ? "Arama: " + g : null),
            "search_web" or "web_search" => (
                "internette arıyor",
                (Str(input, "query") ?? Str(input, "Query")) is { } q ? "Arama: " + q : null),
            "read_url_content" or "webfetch" or "web_extract" => (
                "bir web sayfasını okuyor",
                (Str(input, "Url") ?? Str(input, "url")) is { } u ? "Adres: " + u : null),
            "invoke_subagent" or "task" or "delegate_task" => (
                "bir alt yardımcı çalıştırıyor",
                (Str(input, "Role") ?? Str(input, "description") ?? Str(input, "goal")) is { } r ? "Görev: " + r : null),
            _ => ((string?)null, (string?)null),
        };
        if (text is null || text == _lastProgress) return;
        _lastProgress = text;
        outp.Add(new RunLine("progress", Mask(text), detail is null ? null : Mask(TextNormalizer.Truncate(detail.ReplaceLineEndings(" "), RunEventTranslator.MaxDetail))));
    }

    private static JsonObject? ParseSafeObj(string raw)
    {
        try { return JsonNode.Parse(raw) as JsonObject; }
        catch (Exception) { return null; }
    }

    private void HandleFlatToolResult(JsonObject o, List<RunLine> outp)
    {
        var isErr = (o["is_error"] is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                    || string.Equals(Str(o, "status"), "error", StringComparison.OrdinalIgnoreCase);
        if (!isErr) return;
        var callId = Str(o, "tool_use_id") ?? Str(o, "tool_call_id") ?? Str(o, "id");
        var name = Str(o, "name") ?? (callId is not null && _toolNames.TryGetValue(callId, out var n) ? n : "");
        FlushEdits(outp);
        var rawOut = Str(o, "output") ?? Str(o, "content") ?? Str(o, "error");
        var detail = string.IsNullOrWhiteSpace(rawOut) ? null : "Hata: " + Tail(rawOut.Trim(), 600).ReplaceLineEndings(" ");
        var lower = name.ToLowerInvariant();
        var text = lower is "run_command" or "bash" or "terminal" or "shell" ? "bir komut hata verdi" : "bir araç hata verdi";
        _lastProgress = text;
        outp.Add(new RunLine("progress", Mask(text), detail is null ? null : Mask(detail)));
    }

    private void HandleResult(JsonObject o, List<RunLine> outp)
    {
        ResultSeen = true;
        NumTurns = (NumTurns ?? 0) + 1;
        CaptureSessionId(o);
        if (o["num_turns"] is JsonValue nt && nt.TryGetValue<long>(out var turns) && turns > 0) NumTurns = (int)turns;

        var err = Str(o, "error");
        var isErr = (o["is_error"] is JsonValue iv && iv.TryGetValue<bool>(out var b) && b)
                    || string.Equals(Str(o, "status"), "error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Str(o, "subtype"), "error", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(err);
        if (!string.IsNullOrWhiteSpace(err))
            LastError = TextNormalizer.Truncate(SecretFilter.MaskSecrets(err.Trim()), 300);
        ResultIsError = isErr;

        var resText = (Str(o, "result") ?? Str(o, "text") ?? Str(o, "response"))?.Trim();
        ResultText = !string.IsNullOrEmpty(resText) ? resText : LastError;
        if (!string.IsNullOrEmpty(resText) && LastAgentText is null && !isErr)
        {
            _text.Clear();
            _text.Append(resText);
            FlushText(outp);
        }

        CaptureUsage(o["usage"] as JsonObject ?? o["tokens"] as JsonObject ?? o["stats"] as JsonObject,
            o["total_cost_usd"] ?? o["cost_usd"]);
    }

    private void CaptureUsage(JsonObject? tk, JsonNode? costNode = null)
    {
        if (tk is null && costNode is null) return;
        var input = tk is null ? null : N(tk, "input_tokens") ?? N(tk, "input") ?? N(tk, "prompt_tokens");
        var cached = tk is null ? null : N(tk, "cache_read_input_tokens") ?? N(tk, "cache_read") ?? N(tk, "cached");
        var output = tk is null ? null : N(tk, "output_tokens") ?? N(tk, "output") ?? N(tk, "candidates_tokens");
        var reasoning = tk is null ? null : N(tk, "reasoning_tokens") ?? N(tk, "thoughts_tokens");
        double? cost = null;
        if (costNode is JsonValue cv && cv.TryGetValue<double>(out var cd)) cost = cd;
        var prev = Usage;
        var next = new AgentUsage(
            Sum(prev?.InputTokens, input),
            Sum(prev?.CachedInputTokens, cached),
            Sum(prev?.OutputTokens, output),
            Sum(prev?.ReasoningTokens, reasoning),
            cost ?? prev?.CostUsd);
        if (!next.IsEmpty) Usage = next;
    }

    private void FlushText(List<RunLine> outp)
    {
        var text = _text.ToString().Trim();
        _text.Clear();
        if (text.Length == 0) return;
        FlushEdits(outp);
        LastAgentText = text;
        var plain = RunEventTranslator.Plain(text);
        var first = plain.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Replace('\n', ' ').Trim() ?? "";
        outp.Add(new RunLine("agent", Mask(TextNormalizer.Truncate(first, RunEventTranslator.MaxAgentLine)), Mask(TextNormalizer.Truncate(plain, RunEventTranslator.MaxAgentDetail))));
        _lastProgress = null;
    }

    private void FlushEdits(List<RunLine> outp)
    {
        if (_pendingEdits.Count == 0) return;
        var files = _pendingEdits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingEdits.Clear();
        var text = files.Count == 1 ? $"{files[0]} dosyasını değiştirdi" : $"{files.Count} dosyayı değiştirdi";
        outp.Add(new RunLine("progress", Mask(text), Mask("değişen: " + string.Join(", ", files.Take(20)))));
        _lastProgress = text;
    }

    private static long? Sum(long? a, long? b) => a is null ? b : b is null ? a : a + b;
    private static long? N(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : o[k] is JsonValue d && d.TryGetValue<double>(out var dd) ? (long)dd : null;
    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
    private static string Mask(string s) => SecretFilter.MaskSecrets(s);
    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
