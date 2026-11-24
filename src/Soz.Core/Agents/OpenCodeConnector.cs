using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Text;

namespace Soz.Core.Agents;

/// <summary>
/// OpenCode bağlayıcısı (`opencode run --format json`, opencode.ai / anomalyco hattı). Başlatma: `run [--standalone] --format json`, istem standart girdiden.
/// Sürdürme: aynı komut + `--session &lt;oturum&gt;`. Salt okunur kip: `--agent plan` (düzenlemeyi reddeder).
/// KULLANICININ OPENCODE KLASÖRÜNE VE AYARINA SÖZ HİÇBİR ŞEY YAZMAZ: `--auto` ve `--dangerously-skip-permissions` verilmez.
/// `--standalone` (2.x): `run` varsayılan olarak ortak bir arka plan hizmetine bağlanır ve süreç kapanınca iş hizmette sürer; `--standalone` özel sunucu açar,
/// sunucu Söz'ün süreç ağacında kalır ve Durdur gerçekten durdurur. 1.x hattında bayrak yoktur (kendi sunucusunu süreç içinde açar): ana sürüm `--version` ile okunur,
/// 1 ise bayrak verilmez; okunamazsa 2.x varsayılır. Kaynaklar ve doğrulanamayanlar: `_gecici\h1\ARAYUZ.md`.
///
/// DÜRÜST SINIRLAR:
///  - Bekçi yok (GuardTier.None): başsız `run` hiçbir şey sormaz. Varsayılan ilke her eylemi serbest bırakır (`.env` okuma dışında), proje dışına erişim de kısıtlı değildir
///    (kullanıcının kendi opencode.jsonc kuralları dışında). `ask` çıkarsa `run` onu otomatik reddeder. Geri alma noktası ve kanıt kapısı Söz tarafında çalışır.
///  - Ayrı bir "sonuç" olayı yoktur: bitiş son `step_finish` ve süreç çıkış koduyla anlaşılır (akış bitince <see cref="OpenCodeEventStream.Flush"/> hükmü tamamlar).
///  - Harcama: `step_finish.cost` (dolar; araç fiyat bilmiyorsa 0, o zaman dolar gösterilmez) ve token sayıları.
/// </summary>
public sealed class OpenCodeConnector : IAgentConnector
{
    public const string NotFoundText = "OpenCode bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.";
    public const string ShimText = "OpenCode açılamadı. Ayarlar'dan yolunu kontrol et.";

    private readonly AgentIdentity _identity;
    public OpenCodeConnector(AgentIdentity? identity = null) => _identity = identity ?? new AgentIdentity("opencode", "OpenCode", "opencode",
        new AgentCapabilities(Headless: true, EventStream: true, Resume: true, Stop: true,
            BeforeTool: GuardTier.None, TurnEnd: TurnEndMode.OuterLoop, Ask: AskMode.ResumeReply, Cost: CostMode.Dollars), "opencode");

    public string Id => _identity.Id;
    public string DisplayName => _identity.DisplayName;
    public string SessionTool => _identity.SessionTool;
    public AgentCapabilities Capabilities => _identity.Capabilities;

    public AgentLocation Locate(SozSettings settings) => AgentLocator.Resolve(AgentRegistry.ConfiguredPath(Id, settings), _identity.Command, NotFoundText, ShimText);

    public string? ReadVersion(LaunchCommand cmd, TimeSpan? timeout = null) => AgentVersion.Read(cmd, this, timeout);

    /// <summary>Claude'un çalıştırıcı modeli OpenCode'a verilmez (sağlayıcı/model biçimi ayrıdır). Yalnız iş başına açıkça verilen model; boşsa aracın kendi ayarı.</summary>
    public string DefaultModel(SozSettings settings) => "";

    public AgentLaunch BuildStart(LaunchCommand cmd, AgentRunContext ctx) => Build(cmd, ctx, null);
    public AgentLaunch BuildResume(LaunchCommand cmd, AgentRunContext ctx, string sessionId) => Build(cmd, ctx, sessionId);

    private AgentLaunch Build(LaunchCommand cmd, AgentRunContext ctx, string? session)
    {
        var args = new List<string>(cmd.PrefixArgs) { "run" };
        if (NeedsStandalone(cmd)) args.Add("--standalone");
        args.AddRange(new[] { "--format", "json" });
        if (ctx.Permission == AgentPermissionMode.ReadOnly) args.AddRange(new[] { "--agent", "plan" });
        if (session is not null) { args.Add("--session"); args.Add(session); }
        if (ctx.Model.Length > 0) { args.Add("--model"); args.Add(ctx.Model); }
        // İstem standart girdiden (mesaj argümanı yok): komut satırı uzunluğu ve tırnak sorunu yok. Klasör: süreç klasörü (proje).
        return new AgentLaunch(cmd.FileName, args, ctx.Prompt, AgentEnv.Merge(cmd, ShouldStripEnv), AgentEnv.StripList(ShouldStripEnv), cmd.Display);
    }

    public string DescribePermission(AgentPermissionMode mode)
        => mode == AgentPermissionMode.ReadOnly ? "--agent plan" : "(--auto yok, başsız çalışmada araç hiçbir şey sormaz, ilke aracın kendi ayarıdır)";

    public IAgentEventStream CreateEventStream() => new OpenCodeEventStream();

    public void Stop(ProcessTree tree) => tree.KillTree();

    /// <summary>OpenCode kendi sağlayıcı anahtarlarını ortamdan ya da kendi auth dosyasından okur: hiçbir değişken silinmez.</summary>
    public bool ShouldStripEnv(string name) => false;

    // ---- sürüm: `--standalone` yalnız 2.x'te var ----
    private sealed record CachedMajor(DateTime At, int? Major);
    private static readonly ConcurrentDictionary<string, CachedMajor> MajorCache = new();
    private static readonly TimeSpan MajorTtl = TimeSpan.FromMinutes(10);

    /// <summary>Testler için: sürüm önbelleğini boşaltır.</summary>
    public static void ClearVersionCache() => MajorCache.Clear();

    private bool NeedsStandalone(LaunchCommand cmd)
    {
        var key = cmd.FileName + "|" + string.Join(' ', cmd.PrefixArgs);
        if (!MajorCache.TryGetValue(key, out var hit) || DateTime.UtcNow - hit.At > MajorTtl)
        {
            hit = new CachedMajor(DateTime.UtcNow, ParseMajor(ReadVersion(cmd)));
            MajorCache[key] = hit;
        }
        return hit.Major != 1;
    }

    private static readonly Regex VersionRe = new(@"(\d+)\.\d+(?:\.\d+)?", RegexOptions.Compiled);
    internal static int? ParseMajor(string? version)
        => version is not null && VersionRe.Match(version) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    private static readonly Regex LoginRe = new(@"(not logged in|opencode auth login|no (?:api )?(?:key|provider|credentials)|invalid api key|incorrect api key|unauthorized|\b401\b|token (?:has )?expired|authentication|credentials)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LimitRe = new(@"(usage limit|rate limit|limit reached|quota|insufficient[_ ]quota|too many requests|\b429\b|credit balance)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string FailureReason(int? exit, IAgentEventStream events, string stderr)
    {
        var blob = string.Join("\n", new[] { events.ResultText, (events as OpenCodeEventStream)?.LastError, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (LoginRe.IsMatch(blob))
            return "OpenCode'a giriş ya da sağlayıcı ayarı eksik görünüyor. OpenCode uygulamasını bir kez aç ve hesabınla giriş yap, sonra işi yeniden başlat.";
        if (LimitRe.IsMatch(blob))
            return "OpenCode kullanım sınırına takıldı. Sınır açılınca işi yeniden başlat.";
        return AgentText.GenericFailure(exit, blob);
    }
}

/// <summary>
/// `opencode run --format json` satırlarını sade Türkçe satırlara çevirir (Claude'unkiyle aynı satır türleri: progress | agent). Her satır `{type, timestamp, sessionID, part|error}`.
/// Araç olayı (`tool_use`) araç BİTİNCE gelir. İki hat (1.x ve 2.x) tek çeviriciyle okunur: hata `error.message` (2.x) ya da `error.data.message` (1.x), araç adı `part.tool`.
/// Bilinmeyen olay ve araç sessizce atlanır; bozuk satır boş döner.
/// </summary>
public sealed class OpenCodeEventStream : IAgentEventStream
{
    public string? SessionId { get; private set; }
    public bool ResultSeen { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultText { get; private set; }
    public string? LastAgentText { get; private set; }
    public int? NumTurns { get; private set; }
    public AgentUsage? Usage { get; private set; }
    /// <summary>Son `error` olayının metni (başarısızlık nedeni için).</summary>
    public string? LastError { get; private set; }

    private bool _stepSeen;
    private readonly List<string> _pendingEdits = new();
    private string? _lastText;
    private double _cost;
    private bool _costSeen;

    public IReadOnlyList<RunLine> Feed(string line)
    {
        var outp = new List<RunLine>();
        if (string.IsNullOrWhiteSpace(line)) return outp;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (Exception) { o = null; } // JSON olmayan satır: yutulur
        if (o is null) return outp;
        if (Str(o, "sessionID") is { Length: > 0 } sid) SessionId ??= sid;
        var part = o["part"] as JsonObject;
        switch (Str(o, "type"))
        {
            case "step_start":
                _stepSeen = true;
                break;
            case "step_finish":
                StepFinish(part);
                break;
            case "text":
                Text(part, outp);
                break;
            case "reasoning":
                FlushEdits(outp);
                Emit(outp, "düşünüyor", null, dedupe: true);
                break;
            case "tool_use":
                Tool(part, outp);
                break;
            case "retry":
                FlushEdits(outp);
                Emit(outp, "yeniden deniyor", null, dedupe: true);
                break;
            case "background_wait":
                FlushEdits(outp);
                Emit(outp, "arka plandaki işleri bekliyor", null, dedupe: true);
                break;
            case "error":
                Error(o["error"], outp);
                break;
        }
        return outp;
    }

    public IReadOnlyList<RunLine> Flush()
    {
        var l = new List<RunLine>();
        FlushEdits(l);
        // Ayrı bir sonuç olayı yok: akış bitti ve en az bir adım görüldüyse hüküm süreç çıkış koduna kalır (hata olayı varsa ResultIsError zaten açık).
        if (!ResultSeen && _stepSeen) { ResultSeen = true; NumTurns ??= 1; ResultText ??= LastAgentText; }
        return l;
    }

    private void StepFinish(JsonObject? part)
    {
        _stepSeen = true;
        var reason = Str(part, "reason") ?? "";
        if (!reason.Replace('_', '-').Equals("tool-calls", StringComparison.OrdinalIgnoreCase) && !reason.Replace('_', '-').Equals("tool-call", StringComparison.OrdinalIgnoreCase))
        {
            ResultSeen = true;
            NumTurns = (NumTurns ?? 0) + 1;
            ResultText = LastAgentText;
        }
        if (part?["cost"] is JsonValue cv && cv.TryGetValue<double>(out var c)) { _cost += c; _costSeen = true; }
        if (part?["tokens"] is JsonObject tk)
        {
            var cache = tk["cache"] as JsonObject;
            var prev = Usage;
            Usage = new AgentUsage(Sum(prev?.InputTokens, N(tk, "input")), Sum(prev?.CachedInputTokens, cache is null ? null : N(cache, "read")),
                Sum(prev?.OutputTokens, N(tk, "output")), Sum(prev?.ReasoningTokens, N(tk, "reasoning")), null);
        }
        if (_costSeen && _cost > 0) Usage = (Usage ?? new AgentUsage(null, null, null, null, null)) with { CostUsd = _cost }; // 0 ise araç fiyat bilmiyor: dolar gösterilmez
        if (Usage is { IsEmpty: true }) Usage = null;
    }

    private void Text(JsonObject? part, List<RunLine> outp)
    {
        var text = Str(part, "text")?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        FlushEdits(outp);
        LastAgentText = text;
        var plain = RunEventTranslator.Plain(text);
        var first = plain.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Replace('\n', ' ').Trim() ?? "";
        outp.Add(new RunLine("agent", Mask(TextNormalizer.Truncate(first, RunEventTranslator.MaxAgentLine)), Mask(TextNormalizer.Truncate(plain, RunEventTranslator.MaxAgentDetail))));
        _lastText = null;
    }

    private void Error(JsonNode? err, List<RunLine> outp)
    {
        string? msg = null;
        if (err is JsonObject eo)
            msg = Str(eo, "message") ?? ((eo["data"] as JsonObject) is { } d ? Str(d, "message") : null) ?? Str(eo, "name") ?? Str(eo, "type");
        else if (err is JsonValue ev && ev.TryGetValue<string>(out var s)) msg = s;
        msg = string.IsNullOrWhiteSpace(msg) ? "bilinmeyen hata" : TextNormalizer.Truncate(SecretFilter.MaskSecrets(msg.Trim()), 300);
        LastError = LastError is null ? msg : LastError + "\n" + msg;
        ResultSeen = true;
        ResultIsError = true;
        ResultText = LastError;
        FlushEdits(outp);
    }

    private void Tool(JsonObject? part, List<RunLine> outp)
    {
        var name = (Str(part, "tool") ?? "").ToLowerInvariant();
        var state = part?["state"] as JsonObject;
        var input = state?["input"] as JsonObject ?? new JsonObject();
        var failed = Str(state, "status") == "error";
        switch (name)
        {
            case "edit" or "write" or "patch" or "multiedit" or "apply_patch":
                if (!failed) _pendingEdits.Add(RunEventTranslator.FileName(Str(input, "filePath") ?? Str(input, "file_path") ?? Str(input, "path")));
                else { FlushEdits(outp); Emit(outp, "bir dosya işlemi hata verdi", ErrorDetail(state), dedupe: false); }
                return; // satır, değişiklik biter bitmez ("n dosyayı değiştirdi") bırakılır
        }
        FlushEdits(outp);
        if (name is "bash" or "shell")
        {
            var cmdText = Str(input, "command");
            Emit(outp, RunEventTranslator.DescribeCommand(cmdText), cmdText is { Length: > 0 } ? "Komut: " + cmdText.ReplaceLineEndings(" ") : null, dedupe: false);
            if (failed || ExitNonZero(state)) Emit(outp, "bir komut hata verdi", ErrorDetail(state) ?? Output(state), dedupe: false);
            return;
        }
        var (text, detail) = name switch
        {
            "read" => ($"{RunEventTranslator.FileName(Str(input, "filePath") ?? Str(input, "file_path") ?? Str(input, "path"))} dosyasını okuyor", Str(input, "filePath") is { } fp ? "Dosya: " + fp : null),
            "glob" or "list" or "ls" => ("dosyaları tarıyor", Str(input, "pattern") is { } gl ? "Desen: " + gl : null),
            "grep" => (Str(input, "pattern") is { Length: > 0 } pat ? $"kodda arıyor: {TextNormalizer.Truncate(pat, 40)}" : "kodda arıyor", Str(input, "pattern") is { } g ? "Arama: " + g : null),
            "webfetch" => ("bir web sayfasını okuyor", Str(input, "url") is { } u ? "Adres: " + u : null),
            "websearch" or "codesearch" => ("internette arıyor", Str(input, "query") is { } q ? "Arama: " + q : null),
            "task" => ("bir alt yardımcı çalıştırıyor", Str(input, "description") is { } d ? "Görev: " + d : null),
            "todowrite" or "todoread" => ("yapılacakları güncelliyor", null),
            _ => ((string?)null, (string?)null), // bilinmeyen araç: sessizce atla
        };
        if (text is null) return;
        Emit(outp, text, detail, dedupe: name == "task");
    }

    private static bool ExitNonZero(JsonObject? state)
        => state?["metadata"] is JsonObject m && m["exit"] is JsonValue v && ((v.TryGetValue<long>(out var l) && l != 0) || (v.TryGetValue<double>(out var d) && d != 0));

    private static string? Output(JsonObject? state) => Str(state, "output") is { Length: > 0 } s ? "Çıktı: " + Tail(s.Trim(), 600).ReplaceLineEndings(" ") : null;
    private static string? ErrorDetail(JsonObject? state) => Str(state, "error") is { Length: > 0 } s ? "Hata: " + Tail(s.Trim(), 600).ReplaceLineEndings(" ") : null;

    private void FlushEdits(List<RunLine> outp)
    {
        if (_pendingEdits.Count == 0) return;
        var files = _pendingEdits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingEdits.Clear();
        var text = files.Count == 1 ? $"{files[0]} dosyasını değiştirdi" : $"{files.Count} dosyayı değiştirdi";
        outp.Add(new RunLine("progress", Mask(text), Mask("değişen: " + string.Join(", ", files.Take(20)))));
        _lastText = text;
    }

    private void Emit(List<RunLine> outp, string text, string? rawDetail, bool dedupe)
    {
        if (dedupe && text == _lastText) return;
        _lastText = text;
        outp.Add(new RunLine("progress", Mask(text), rawDetail is null ? null : Mask(TextNormalizer.Truncate(rawDetail, RunEventTranslator.MaxDetail))));
    }

    private static long? Sum(long? a, long? b) => a is null ? b : b is null ? a : a + b;
    private static long? N(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : o[k] is JsonValue d && d.TryGetValue<double>(out var dd) ? (long)dd : null;
    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
    private static string Mask(string s) => SecretFilter.MaskSecrets(s);
    private static string? Str(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
