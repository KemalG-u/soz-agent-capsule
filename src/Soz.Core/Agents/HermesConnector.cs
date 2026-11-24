using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Text;

namespace Soz.Core.Agents;

/// <summary>
/// Hermes Agent bağlayıcısı (`hermes chat --format stream-json`). Başlatma: `chat --format stream-json --query-file - --in &lt;klasör&gt;`, istem standart girdiden (kabuk yorumu ve tırnak sorunu yok).
/// Sürdürme: aynı komut + `--resume &lt;oturum&gt;`. Devam turunda `-m` verilmez: Hermes devam eden oturumun donmuş seçeneklerini değiştiren bayrağı reddeder.
/// KULLANICININ HERMES KLASÖRÜNE SÖZ HİÇBİR ŞEY YAZMAZ: config.yaml'a dokunulmaz, kanca kurulmaz, `--yolo`, `--accept-hooks` ve `--source tool` verilmez.
/// Kaynaklar ve doğrulanamayanlar: `_gecici\h1\ARAYUZ.md` (hermes_cli/stream_json.py, gateway belgesi v0.23.1).
///
/// DÜRÜST SINIRLAR:
///  - Bekçi yok (GuardTier.None): Hermes tehlikeli komut onayını kendisi ister (kalıp tabanlı) ama başsız çalışmada bunu Söz'e değil kimseye soramaz; iş durur (çıkış 3).
///    Bu Söz'ün bekçisi sayılmaz. Geri alma noktası ve kanıt kapısı Söz tarafında çalışır.
///  - Durdur gerçek durdurma değil (Capabilities.Stop false): `hermes chat` paylaşılan bir gateway hizmetinin istemcisidir; istemci kapanınca iş hizmette sürer
///    ("Detached; accepted work continues at the gateway"). Söz izlemeyi keser ve kişiye bunu söyler (IAgentConnector.StopNote).
///  - Yeni oturumun kimliği tur SONUNDA (`result` kaydı) gelir; tur ortasında kesilen işe "devam" aynı oturuma bağlanamaz.
///  - Harcama yalnız token (dolar yok). Salt okunur kip desteklenmez: istenirse başlatma sade bir cümleyle reddedilir (sessizce serbest bırakılmaz).
/// </summary>
public sealed class HermesConnector : IAgentConnector
{
    public const string NotFoundText = "Hermes bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.";
    public const string ShimText = "Hermes açılamadı. Ayarlar'dan yolunu kontrol et.";
    public const string ReadOnlyText = "Hermes salt okunur kipi desteklemiyor, bu yüzden iş başlatılmadı. İşi Claude ya da Codex ile ver.";

    private readonly AgentIdentity _identity;
    public HermesConnector(AgentIdentity? identity = null) => _identity = identity ?? new AgentIdentity("hermes", "Hermes Agent", "hermes",
        new AgentCapabilities(Headless: true, EventStream: true, Resume: true, Stop: false,
            BeforeTool: GuardTier.None, TurnEnd: TurnEndMode.OuterLoop, Ask: AskMode.ResumeReply, Cost: CostMode.Tokens), "hermes");

    public string Id => _identity.Id;
    public string DisplayName => _identity.DisplayName;
    public string SessionTool => _identity.SessionTool;
    public AgentCapabilities Capabilities => _identity.Capabilities;

    public AgentLocation Locate(SozSettings settings) => AgentLocator.Resolve(AgentRegistry.ConfiguredPath(Id, settings), _identity.Command, NotFoundText, ShimText);

    public string? ReadVersion(LaunchCommand cmd, TimeSpan? timeout = null) => AgentVersion.Read(cmd, this, timeout);

    /// <summary>Claude'un çalıştırıcı modeli (runnerModel) Hermes'e verilmez. Yalnız iş başına açıkça verilen model; boşsa Hermes'in kendi ayarı.</summary>
    public string DefaultModel(SozSettings settings) => "";

    public AgentLaunch BuildStart(LaunchCommand cmd, AgentRunContext ctx)
    {
        var args = Common(cmd, ctx);
        if (ctx.Model.Length > 0) { args.Add("-m"); args.Add(ctx.Model); }
        return Launch(cmd, args, ctx);
    }

    public AgentLaunch BuildResume(LaunchCommand cmd, AgentRunContext ctx, string sessionId)
    {
        var args = Common(cmd, ctx);
        args.Add("--resume");
        args.Add(sessionId);
        return Launch(cmd, args, ctx); // model yok: oturumun donmuş seçeneklerini değiştiren bayrak reddedilir
    }

    private static List<string> Common(LaunchCommand cmd, AgentRunContext ctx)
    {
        if (ctx.Permission == AgentPermissionMode.ReadOnly) throw new AgentLaunchException(ReadOnlyText);
        // --in: açık klasör. Hizmet çağıranın klasörünü desteklemiyorsa (eski sürüm) Hermes bunu REDDEDER; sessizce başka klasörde çalışmaz.
        return new List<string>(cmd.PrefixArgs) { "chat", "--format", "stream-json", "--query-file", "-", "--in", ctx.ProjectPath };
    }

    private AgentLaunch Launch(LaunchCommand cmd, List<string> args, AgentRunContext ctx)
    {
        var env = new Dictionary<string, string?>(AgentEnv.Merge(cmd, ShouldStripEnv), StringComparer.OrdinalIgnoreCase)
        {
            ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8", // Türkçe istem standart girdiden bozulmadan gitsin
        };
        if ((!env.TryGetValue("HERMES_HOME", out var existingHome) || string.IsNullOrWhiteSpace(existingHome))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HERMES_HOME")))
        {
            var inferred = InferHermesHome(cmd.FileName);
            if (inferred is not null) env["HERMES_HOME"] = inferred;
        }
        return new AgentLaunch(cmd.FileName, args, ctx.Prompt, env, AgentEnv.StripList(ShouldStripEnv), cmd.Display);
    }

    /// <summary>
    /// hermes.exe doğrudan bulunduğunda bile bin\hermes.exe'nin üst klasöründe (veya yanında) state.db ya da config.yaml varsa HERMES_HOME olarak çözer.
    /// </summary>
    public static string? InferHermesHome(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            var full = Path.GetFullPath(exePath);
            var binDir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(binDir)) return null;
            var parent = Directory.GetParent(binDir)?.FullName;
            if (!string.IsNullOrEmpty(parent) && HasHermesHomeMarker(parent)) return parent;
            if (HasHermesHomeMarker(binDir)) return binDir;
        }
        catch (Exception) { }
        return null;
    }

    private static bool HasHermesHomeMarker(string dir)
        => File.Exists(Path.Combine(dir, "state.db")) || File.Exists(Path.Combine(dir, "config.yaml"));

    public string DescribePermission(AgentPermissionMode mode)
        => mode == AgentPermissionMode.ReadOnly ? "desteklenmiyor" : "chat --format stream-json (Hermes'in kendi onay ilkesi, --yolo yok)";

    public IAgentEventStream CreateEventStream() => new HermesEventStream();

    public void Stop(ProcessTree tree) => tree.KillTree();

    /// <summary>Hermes kendi sağlayıcı anahtarlarını ortamdan ya da kendi .env dosyasından okur: hiçbir değişken silinmez (silmek Hermes'i bozar).</summary>
    public bool ShouldStripEnv(string name) => false;

    private static readonly Regex LoginRe = new(@"(credentials or agent init failed|no (?:api )?(?:key|provider|credentials)|not logged in|hermes (?:setup|auth|model|login)|invalid api key|incorrect api key|unauthorized|\b401\b|token (?:has )?expired|authentication)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LimitRe = new(@"(usage limit|rate limit|rate-limited|limit reached|quota|insufficient[_ ]quota|too many requests|\b429\b|billing)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InputRe = new(@"(input required|detached without cancelling|resume this session interactively)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GatewayRe = new(@"(gateway (?:connection|read)|no local fallback|does not support creation options|--in)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string FailureReason(int? exit, IAgentEventStream events, string stderr)
    {
        var blob = string.Join("\n", new[] { events.ResultText, (events as HermesEventStream)?.LastError, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (exit == 3 || InputRe.IsMatch(blob))
            return "Hermes bir işlem için senden onay ya da cevap istedi ama başsız çalışmada bunu soramaz. İşi Hermes'in kendi ekranında sürdür, ya da işi tehlikeli komut gerektirmeyecek biçimde yaz.";
        if (LoginRe.IsMatch(blob))
            return "Hermes'e giriş ya da sağlayıcı ayarı eksik görünüyor. Terminalde bir kez `hermes setup` ya da `hermes model` yazıp ayarla, sonra işi yeniden başlat.";
        if (LimitRe.IsMatch(blob))
            return "Hermes kullanım sınırına takıldı. Sınır açılınca işi yeniden başlat.";
        if (GatewayRe.IsMatch(blob))
            return "Hermes arka plan hizmetine bağlanamadı ya da bu klasörde çalışmayı kabul etmedi. Hermes'i güncelleyip yeniden dene.";
        return AgentText.GenericFailure(exit, blob);
    }
}

/// <summary>
/// `hermes chat --format stream-json` satırlarını sade Türkçe satırlara çevirir (Claude'unkiyle aynı satır türleri: progress | agent).
/// `text` olayları metin PARÇASIDIR: biriktirilir, bir araç olayında ya da turun sonunda tek "agent" satırı olur. Araç adı ve ham komut satıra çıkmaz.
/// Bilinmeyen olay sessizce atlanır; bozuk satır boş döner. Oturum kimliği `init`te boş olabilir, `result`ta gelir.
/// </summary>
public sealed class HermesEventStream : IAgentEventStream
{
    public string? SessionId { get; private set; }
    public bool ResultSeen { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultText { get; private set; }
    public string? LastAgentText { get; private set; }
    public int? NumTurns { get; private set; }
    public AgentUsage? Usage { get; private set; }
    /// <summary>`result.error` ya da stderr'e düşmeyen son hata metni (başarısızlık nedeni için).</summary>
    public string? LastError { get; private set; }
    /// <summary>`result.exit_code` (yoksa null).</summary>
    public int? ExitCode { get; private set; }

    private readonly System.Text.StringBuilder _text = new();
    private readonly List<string> _pendingEdits = new();
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private string? _lastText;

    public IReadOnlyList<RunLine> Feed(string line)
    {
        var outp = new List<RunLine>();
        if (string.IsNullOrWhiteSpace(line)) return outp;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (Exception) { o = null; } // JSON olmayan satır (ör. araç günlüğü): yutulur
        if (o is null) return outp;
        switch (Str(o, "type"))
        {
            case "system":
                if (Str(o, "subtype") == "init" && Str(o, "session_id") is { Length: > 0 } sid) SessionId ??= sid;
                break;
            case "text":
                if (Str(o, "text") is { Length: > 0 } t) _text.Append(t);
                break;
            case "tool_use":
                FlushText(outp);
                ToolUse(o, outp);
                break;
            case "tool_result":
                ToolResult(o, outp);
                break;
            case "result":
                FlushText(outp);
                FlushEdits(outp);
                Result(o, outp);
                break;
        }
        return outp;
    }

    public IReadOnlyList<RunLine> Flush()
    {
        var l = new List<RunLine>();
        FlushText(l);
        FlushEdits(l);
        return l;
    }

    private void Result(JsonObject o, List<RunLine> outp)
    {
        ResultSeen = true;
        NumTurns = (NumTurns ?? 0) + 1;
        if (Str(o, "session_id") is { Length: > 0 } sid) SessionId = sid; // yeni oturumda kimlik ilk kez burada gelir
        ExitCode = o["exit_code"] is JsonValue ev && ev.TryGetValue<long>(out var e) ? (int)e : null;
        var err = Str(o, "error");
        if (!string.IsNullOrWhiteSpace(err)) LastError = TextNormalizer.Truncate(SecretFilter.MaskSecrets(err.Trim()), 300);
        ResultIsError = (ExitCode ?? 0) != 0 || !string.IsNullOrWhiteSpace(err);
        var text = Str(o, "text")?.Trim();
        ResultText = !string.IsNullOrEmpty(text) ? text : LastError;
        if (!string.IsNullOrEmpty(text) && LastAgentText is null) { _text.Clear(); _text.Append(text); FlushText(outp); } // metin parçası gelmediyse (kayıp parça) tam yanıt
        if (o["tokens"] is JsonObject tk)
        {
            var input = N(tk, "input"); var cached = N(tk, "cache_read"); var output = N(tk, "output");
            var prev = Usage;
            Usage = new AgentUsage(Sum(prev?.InputTokens, input), Sum(prev?.CachedInputTokens, cached), Sum(prev?.OutputTokens, output), null, null);
            if (Usage.IsEmpty) Usage = null;
        }
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
        _lastText = null;
    }

    private void FlushEdits(List<RunLine> outp)
    {
        if (_pendingEdits.Count == 0) return;
        var files = _pendingEdits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingEdits.Clear();
        var text = files.Count == 1 ? $"{files[0]} dosyasını değiştirdi" : $"{files.Count} dosyayı değiştirdi";
        outp.Add(new RunLine("progress", Mask(text), Mask("değişen: " + string.Join(", ", files.Take(20)))));
        _lastText = text;
    }

    private void ToolUse(JsonObject o, List<RunLine> outp)
    {
        var name = Str(o, "name") ?? "";
        if (Str(o, "tool_call_id") is { Length: > 0 } id) _toolNames[id] = name;
        var input = o["input"] as JsonObject ?? new JsonObject();
        switch (name)
        {
            case "write_file" or "patch" or "edit_file" or "apply_patch":
                _pendingEdits.Add(RunEventTranslator.FileName(Str(input, "path") ?? Str(input, "file_path")));
                return; // satır, değişiklik biter bitmez ("n dosyayı değiştirdi") bırakılır
        }
        FlushEdits(outp);
        var (text, detail) = name switch
        {
            "terminal" or "shell" or "bash" => (RunEventTranslator.DescribeCommand(Str(input, "command")), Str(input, "command") is { } c ? "Komut: " + c : null),
            "read_file" => ($"{RunEventTranslator.FileName(Str(input, "path") ?? Str(input, "file_path"))} dosyasını okuyor", Str(input, "path") is { } p ? "Dosya: " + p : null),
            "search_files" => (Str(input, "pattern") is { Length: > 0 } pat ? $"kodda arıyor: {TextNormalizer.Truncate(pat, 40)}" : "dosyaları tarıyor", Str(input, "pattern") is { } g ? "Arama: " + g : null),
            "web_search" => ("internette arıyor", Str(input, "query") is { } q ? "Arama: " + q : null),
            "web_extract" or "browser_navigate" => ("bir web sayfasını okuyor", Str(input, "url") is { } u ? "Adres: " + u : null),
            "delegate_task" => ("bir alt yardımcı çalıştırıyor", Str(input, "goal") is { } d ? "Görev: " + d : null),
            "todo" => ("yapılacakları güncelliyor", null),
            _ => ((string?)null, (string?)null), // bilinmeyen araç: sessizce atla
        };
        if (text is null) return;
        if (name == "delegate_task" && text == _lastText) return;
        _lastText = text;
        outp.Add(new RunLine("progress", Mask(text), detail is null ? null : Mask(TextNormalizer.Truncate(detail.ReplaceLineEndings(" "), RunEventTranslator.MaxDetail))));
    }

    private void ToolResult(JsonObject o, List<RunLine> outp)
    {
        if (!(o["is_error"] is JsonValue v && v.TryGetValue<bool>(out var bad) && bad)) return;
        var name = Str(o, "name") ?? (Str(o, "tool_call_id") is { } id && _toolNames.TryGetValue(id, out var n) ? n : "");
        FlushEdits(outp);
        var output = Str(o, "output");
        var detail = string.IsNullOrWhiteSpace(output) ? null : "Hata: " + Tail(output.Trim(), 600).ReplaceLineEndings(" ");
        var text = name is "terminal" or "shell" or "bash" ? "bir komut hata verdi" : "bir araç hata verdi";
        _lastText = text;
        outp.Add(new RunLine("progress", Mask(text), detail is null ? null : Mask(detail)));
    }

    private static long? Sum(long? a, long? b) => a is null ? b : b is null ? a : a + b;
    private static long? N(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : o[k] is JsonValue d && d.TryGetValue<double>(out var dd) ? (long)dd : null;
    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
    private static string Mask(string s) => SecretFilter.MaskSecrets(s);
    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
