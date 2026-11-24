using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Text;

namespace Soz.Core.Agents;

/// <summary>
/// Codex CLI bağlayıcısı (`codex exec --json`). Başlatma: `exec --json -s workspace-write`, istem standart girdiden ("-"). Sürdürme: `exec resume --json &lt;oturum&gt; -`.
/// KULLANICININ ~/.codex YA DA CODEX_HOME KLASÖRÜNE SÖZ HİÇBİR ŞEY YAZMAZ: ayar `-c` ile yalnız o süreç için verilir, kanca kurulmaz, auth.json'a dokunulmaz.
/// (Codex kendi oturum dosyalarını kendi klasörüne yazar; sürdürme için gereklidir ve Söz'ün işi değildir.)
/// Bekçi: işletim sistemi sınırı (sandbox), araç başına "sor" yoktur. Kanıt kapısı: dış kapı döngüsü (Söz ölçer, kalırsa `exec resume` ile geri yollar).
/// Olay biçimi (GERÇEK Codex 0.167.0 duman denemesiyle doğrulandı, 11 Kas 2026): thread.started{thread_id} (exec resume de aynı kimlikle yeniden basar), turn.started,
/// item.started ve item.completed çifti (command_execution{command, aggregated_output, exit_code (çalışırken null), status in_progress|completed|failed}, agent_message{text}),
/// turn.completed{usage{input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens, reasoning_output_tokens}}. Dosyayı PowerShell Set-Content ile yazınca ayrı file_change olayı GELMEZ:
/// dosya değişikliği olaydan değil diskten ölçülür (kanıt tabanı). stdout'a JSON olmayan günlük satırları da karışır ("... ERROR codex_core::tools::router: ..."), çevirici bunlarda patlamaz.
/// Windows'ta bu makinenin ayarı [windows] sandbox = "elevated" ise başsız çalışmada her komut "orchestrator_helper_launch_canceled ... 1223" ile düşer: çağrı başına
/// -c windows.sandbox="unelevated" verilir (kullanıcının config dosyasına yazılmaz).
/// </summary>
public sealed class CodexConnector : IAgentConnector
{
    public const string NotFoundText = "Codex bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.";
    public const string ShimText = "Codex açılamadı. Ayarlar'dan yolunu kontrol et.";

    /// <summary>Kimlik ve yetenekler kayıt defterinden gelir (AgentRegistry verir). Parametresiz üretimde kaydın bilinen varsayılanı kullanılır (testler).</summary>
    private readonly AgentIdentity _identity;
    public CodexConnector(AgentIdentity? identity = null) => _identity = identity ?? new AgentIdentity("codex", "Codex", "codex",
        new AgentCapabilities(Headless: true, EventStream: true, Resume: true, Stop: true,
            BeforeTool: GuardTier.OsSandbox, TurnEnd: TurnEndMode.OuterLoop, Ask: AskMode.ResumeReply, Cost: CostMode.Tokens), "codex");

    public string Id => _identity.Id;
    public string DisplayName => _identity.DisplayName;
    public string SessionTool => _identity.SessionTool;
    public AgentCapabilities Capabilities => _identity.Capabilities;

    public AgentLocation Locate(SozSettings settings) => AgentLocator.Resolve(AgentRegistry.ConfiguredPath(Id, settings), _identity.Command, NotFoundText, ShimText);

    public string? ReadVersion(LaunchCommand cmd, TimeSpan? timeout = null) => AgentVersion.Read(cmd, this, timeout);

    /// <summary>Claude'un çalıştırıcı modeli (runnerModel) Codex'e verilmez: Claude model adı Codex'te geçersiz olur. Yalnız iş başına açıkça verilen model.</summary>
    public string DefaultModel(SozSettings settings) => "";

    public AgentLaunch BuildStart(LaunchCommand cmd, AgentRunContext ctx)
    {
        var args = new List<string>(cmd.PrefixArgs) { "exec", "--json", "--skip-git-repo-check" };
        args.AddRange(new[] { "-s", SandboxFor(ctx.Permission) });
        args.AddRange(new[] { "-C", ctx.ProjectPath });
        args.AddRange(new[] { "-c", "approval_policy=\"never\"" });
        AddWindowsSandbox(args);
        if (ctx.Model.Length > 0) { args.Add("-m"); args.Add(ctx.Model); }
        args.Add("-"); // istem standart girdiden: komut satırı uzunluğu ve tırnak sorunu yok
        return Launch(cmd, args, ctx);
    }

    public AgentLaunch BuildResume(LaunchCommand cmd, AgentRunContext ctx, string sessionId)
    {
        // `exec resume` -s ve -C bayraklarını almaz (codex exec resume --help, 0.167.0): sınır `-c sandbox_mode` ile verilir, klasör süreç klasörüdür.
        var args = new List<string>(cmd.PrefixArgs) { "exec", "resume", "--json", "--skip-git-repo-check" };
        args.AddRange(new[] { "-c", $"sandbox_mode=\"{SandboxFor(ctx.Permission)}\"" });
        args.AddRange(new[] { "-c", "approval_policy=\"never\"" });
        AddWindowsSandbox(args);
        if (ctx.Model.Length > 0) { args.Add("-m"); args.Add(ctx.Model); }
        args.Add(sessionId);
        args.Add("-");
        return Launch(cmd, args, ctx);
    }

    /// <summary>Windows'ta kum havuzu "unelevated" kipinde kurulur: "elevated" kipi başsız çalışmada yönetici onayı penceresi ister (ShellExecuteExW 1223) ve her komut düşer. Yalnız bu çağrı için, config dosyasına yazılmaz.</summary>
    internal const string WindowsSandboxArg = "windows.sandbox=\"unelevated\"";
    private static void AddWindowsSandbox(List<string> args)
    {
        if (!OperatingSystem.IsWindows()) return;
        args.Add("-c");
        args.Add(WindowsSandboxArg);
    }

    private AgentLaunch Launch(LaunchCommand cmd, List<string> args, AgentRunContext ctx)
        => new(cmd.FileName, args, ctx.Prompt, AgentEnv.Merge(cmd, ShouldStripEnv), AgentEnv.StripList(ShouldStripEnv), cmd.Display);

    /// <summary>Söz'ün "temkinli" şablonu → proje klasörüne yazabilen, dışına yazamayan sandbox. Salt okunur → read-only. danger-full-access hiçbir kipe eşlenmez.</summary>
    internal static string SandboxFor(AgentPermissionMode mode) => mode == AgentPermissionMode.ReadOnly ? "read-only" : "workspace-write";

    public string DescribePermission(AgentPermissionMode mode) => $"-s {SandboxFor(mode)} -c approval_policy=\"never\"" + (OperatingSystem.IsWindows() ? $" -c {WindowsSandboxArg}" : "");

    public const string SandboxBrokenText = "Codex bu bilgisayarda komut çalıştıramadı (Windows kum havuzu kurulumu eksik). Kendi terminalinde bir kez codex açıp kum havuzu kurulumunu tamamla, sonra işi yeniden başlat.";

    public IAgentEventStream CreateEventStream() => new CodexEventStream();

    public void Stop(ProcessTree tree) => tree.KillTree();

    /// <summary>Kullanıcının ChatGPT girişi kullanılır: faturalı anahtar ve yönlendirme değişkenleri çocuk sürece geçmez. (Liste belgeden, gerçek koşuyla doğrulanmadı.)</summary>
    public bool ShouldStripEnv(string name)
        => name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("AZURE_OPENAI_", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CODEX_API_KEY", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex LoginRe = new(@"(not logged in|please (?:log ?in|sign ?in)|codex login|run `?codex login|invalid api key|incorrect api key|unauthorized|\b401\b|token (?:has )?expired|authentication)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LimitRe = new(@"(usage limit|rate limit|limit reached|hit your|quota|insufficient[_ ]quota|too many requests|\b429\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string FailureReason(int? exit, IAgentEventStream events, string stderr)
    {
        var cx = events as CodexEventStream;
        if (cx is { SandboxBroken: true }) return SandboxBrokenText;
                var blob = string.Join("\n", new[] { events.ResultText, cx?.LastError, stderr, cx?.LastNoise }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (LoginRe.IsMatch(blob))
            return "Codex'e giriş yapılmamış görünüyor. Codex uygulamasını bir kez aç ve hesabınla giriş yap, sonra işi yeniden başlat.";
        if (LimitRe.IsMatch(blob))
            return "Codex kullanım sınırına takıldı. Sınır açılınca işi yeniden başlat.";
        return AgentText.GenericFailure(exit, blob);
    }
}

/// <summary>
/// `codex exec --json` satırlarını sade Türkçe satırlara çevirir (Claude'unkiyle aynı satır türleri: progress | agent). Araç adı ve ham komut satıra çıkmaz, yalnız maskeli ayrıntıda durur.
/// Bilinmeyen olay ve öğe türü sessizce atlanır; bozuk satır boş döner.
/// </summary>
public sealed class CodexEventStream : IAgentEventStream
{
    public string? SessionId { get; private set; }
    public bool ResultSeen { get; private set; }
    private bool _turnFailed;
    public bool ResultIsError => _turnFailed || (ResultSeen && SandboxBroken && !_fileChanged);
    public string? ResultText { get; private set; }
    public string? LastAgentText { get; private set; }
    public int? NumTurns { get; private set; }
    public AgentUsage? Usage { get; private set; }
    /// <summary>Son `error` olayının ya da `turn.failed` hatasının metni (başarısızlık nedeni için).</summary>
    public string? LastError { get; private set; }
    /// <summary>stdout'a karışan JSON olmayan günlük satırlarının sonuncusu (ERROR içerenler). Tek başına başarısızlık sayılmaz, yalnız neden ipucu.</summary>
    public string? LastNoise { get; private set; }
    /// <summary>Hiçbir komut gerçekten koşmadı ve en az biri kum havuzu kurulum hatasıyla düştü: Codex bu bilgisayarda komut çalıştıramıyor.</summary>
    public bool SandboxBroken => _cmdBroken > 0 && _cmdRan == 0;

    private int _cmdBroken, _cmdRan;
    private bool _fileChanged;

    private readonly List<string> _pendingEdits = new();
    private readonly HashSet<string> _announced = new();
    private string? _lastText;

    public IReadOnlyList<RunLine> Feed(string line)
    {
        var outp = new List<RunLine>();
        if (string.IsNullOrWhiteSpace(line)) return outp;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (Exception) { o = null; } // JSON olmayan günlük satırı: yutulur
        if (o is null)
        {
            if (line.Contains("ERROR", StringComparison.Ordinal)) LastNoise = TextNormalizer.Truncate(SecretFilter.MaskSecrets(line.Trim()), 300);
            return outp;
        }
        switch (Str(o, "type"))
        {
            case "thread.started":
                // exec resume aynı kimlikle thread.started'ı yeniden basar: ilk kimlik korunur, yeni oturum açılmaz.
                if (SessionId is null && Str(o, "thread_id") is { Length: > 0 } tid) SessionId = tid;
                break;
            case "item.started":
            case "item.updated":
                if (o["item"] is JsonObject si) Item(si, completed: false, outp);
                break;
            case "item.completed":
                if (o["item"] is JsonObject ci) Item(ci, completed: true, outp);
                break;
            case "turn.completed":
                Flush(outp);
                ResultSeen = true;
                _turnFailed = false;
                NumTurns = (NumTurns ?? 0) + 1;
                ResultText = LastAgentText;
                AddUsage(o["usage"] as JsonObject);
                break;
            case "turn.failed":
                Flush(outp);
                ResultSeen = true;
                _turnFailed = true;
                var msg = (o["error"] as JsonObject) is { } eo ? Str(eo, "message") : null;
                if (!string.IsNullOrWhiteSpace(msg)) { LastError = msg; ResultText = msg; }
                break;
            case "error":
                // Yeniden bağlanma gibi geçici hatalar da `error` olarak gelir: tek başına başarısızlık sayılmaz, yalnız neden olarak saklanır.
                if (Str(o, "message") is { Length: > 0 } em) LastError = em;
                break;
        }
        return outp;
    }

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

    private void Item(JsonObject item, bool completed, List<RunLine> outp)
    {
        var type = Str(item, "type");
        var id = Str(item, "id") ?? "";
        switch (type)
        {
            case "agent_message":
            {
                if (!completed) return;
                var text = Str(item, "text")?.Trim();
                if (string.IsNullOrEmpty(text)) return;
                Flush(outp);
                LastAgentText = text;
                var plain = RunEventTranslator.Plain(text);
                var first = plain.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Replace('\n', ' ').Trim() ?? "";
                outp.Add(new RunLine("agent", Mask(TextNormalizer.Truncate(first, RunEventTranslator.MaxAgentLine)), Mask(TextNormalizer.Truncate(plain, RunEventTranslator.MaxAgentDetail))));
                _lastText = null;
                return;
            }
            case "reasoning":
                Flush(outp);
                Emit(outp, "düşünüyor", null, dedupe: true);
                return;
            case "command_execution":
            {
                var cmd = UnwrapShell(Str(item, "command"));
                if (_announced.Add("c:" + id))
                {
                    Flush(outp);
                    Emit(outp, RunEventTranslator.DescribeCommand(cmd), cmd.Length > 0 ? "Komut: " + cmd.ReplaceLineEndings(" ") : null, dedupe: false);
                }
                if (completed && Str(item, "status") != "in_progress")
                {
                    var outText = Str(item, "aggregated_output");
                    var helperFail = SetupHelperFailure(outText);
                    var cannotRun = helperFail || (Str(item, "status") == "failed" && ExitCode(item) == -1);
                    if (helperFail) _cmdBroken++; else _cmdRan++;
                    if (IsFailed(item))
                    {
                        Flush(outp);
                        var detail = string.IsNullOrWhiteSpace(outText) ? null : "Hata: " + Tail(outText.Trim(), 600).ReplaceLineEndings(" ");
                        Emit(outp, cannotRun ? "komut çalıştırılamadı" : "bir komut hata verdi", detail, dedupe: false);
                    }
                }
                return;
            }
            case "file_change":
            {
                if (!completed || IsFailed(item)) return;
                _fileChanged = true;
                if (item["changes"] is JsonArray changes)
                    foreach (var ch in changes.OfType<JsonObject>())
                        if (Str(ch, "path") is { Length: > 0 } p) _pendingEdits.Add(RunEventTranslator.FileName(p));
                return; // satır, değişiklik biter bitmez ("n dosyayı değiştirdi") bırakılır
            }
            case "mcp_tool_call":
                if (_announced.Add("m:" + id)) { Flush(outp); Emit(outp, "bir yardımcı araç çalıştırıyor", null, dedupe: false); }
                return;
            case "web_search":
                if (_announced.Add("w:" + id)) { Flush(outp); Emit(outp, "internette arıyor", Str(item, "query") is { Length: > 0 } q ? "Arama: " + q : null, dedupe: false); }
                return;
            case "todo_list":
                Flush(outp);
                Emit(outp, "yapılacakları güncelliyor", null, dedupe: true);
                return;
        }
        // error ve bilinmeyen öğe türleri: sessizce atla
    }

    private static readonly Regex SetupHelperRe = new(@"orchestrator_helper_launch_canceled|setup helper|Failed to create unified exec process", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static bool SetupHelperFailure(string? output) => !string.IsNullOrEmpty(output) && SetupHelperRe.IsMatch(output);

    private static long? ExitCode(JsonObject item) => item["exit_code"] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static bool IsFailed(JsonObject item)
    {
        if (Str(item, "status") == "failed") return true;
        return item["exit_code"] is JsonValue v && ((v.TryGetValue<long>(out var l) && l != 0) || (v.TryGetValue<double>(out var d) && d != 0));
    }

    private void AddUsage(JsonObject? u)
    {
        if (u is null) return;
        var input = N(u, "input_tokens"); var cached = N(u, "cached_input_tokens"); var output = N(u, "output_tokens"); var reasoning = N(u, "reasoning_output_tokens");
        var prev = Usage;
        Usage = new AgentUsage(Sum(prev?.InputTokens, input), Sum(prev?.CachedInputTokens, cached), Sum(prev?.OutputTokens, output), Sum(prev?.ReasoningTokens, reasoning), null);
        if (Usage.IsEmpty) Usage = null;
    }

    private static long? Sum(long? a, long? b) => a is null ? b : b is null ? a : a + b;
    private static long? N(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : o[k] is JsonValue d && d.TryGetValue<double>(out var dd) ? (long)dd : null;

    private void Emit(List<RunLine> outp, string text, string? rawDetail, bool dedupe)
    {
        if (dedupe && text == _lastText) return; // art arda "düşünüyor" tekrar etmesin
        _lastText = text;
        outp.Add(new RunLine("progress", Mask(text), rawDetail is null ? null : Mask(TextNormalizer.Truncate(rawDetail, RunEventTranslator.MaxDetail))));
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];

    private static readonly Regex ShellWrap = new(
        @"^\s*(?:""(?<sh>[^""]*)""|(?<sh>\S+))\s+(?:-[A-Za-z]+\s+)*?(?:-Command|-c|-lc|/c|-Cmd)\s+(?<rest>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Codex komutu kabuk sarmalayıcısıyla bildirir (`powershell.exe -Command "npm test"`, `bash -lc "ls"`): sarmalayıcıyı atar, asıl komutu verir.</summary>
    internal static string UnwrapShell(string? command)
    {
        var c = (command ?? "").Trim();
        var m = ShellWrap.Match(c);
        if (!m.Success) return c;
        var sh = Path.GetFileNameWithoutExtension(m.Groups["sh"].Value.Replace('/', '\\')).ToLowerInvariant();
        if (sh is not ("powershell" or "pwsh" or "bash" or "sh" or "zsh" or "cmd")) return c;
        var rest = m.Groups["rest"].Value.Trim();
        if (rest.Length >= 2 && ((rest[0] == '"' && rest[^1] == '"') || (rest[0] == '\'' && rest[^1] == '\''))) rest = rest[1..^1];
        return rest;
    }

    private static string Mask(string s) => SecretFilter.MaskSecrets(s);
    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
