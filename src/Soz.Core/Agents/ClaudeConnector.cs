using System.Text.RegularExpressions;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Sessions;
using Soz.Core.Text;

namespace Soz.Core.Agents;

/// <summary>
/// Claude Code bağlayıcısı. Claude'a özgü her şey burada: bayraklar (`-p --output-format stream-json --verbose --settings ... --permission-mode acceptEdits --allowedTools`),
/// kanca ayarının `--settings` ile verilmesi (kullanıcının ~/.claude klasörüne yazılmaz), stream-json çevirisi (RunEventTranslator), yer bulma, giriş ve sınır cümleleri.
/// Bekçi yerli kanca (PreToolUse), kanıt kapısı yerli kanca (Stop). Davranış, bağlayıcı arayüzü çıkarılmadan önceki yürütücüyle aynıdır.
/// </summary>
public sealed class ClaudeConnector : IAgentConnector
{
    /// <summary>Kimlik ve yetenekler kayıt defterinden gelir (AgentRegistry verir). Parametresiz üretimde kaydın bilinen varsayılanı kullanılır (testler).</summary>
    private readonly AgentIdentity _identity;
    public ClaudeConnector(AgentIdentity? identity = null) => _identity = identity ?? new AgentIdentity("claude", "Claude Code", "claude",
        new AgentCapabilities(Headless: true, EventStream: true, Resume: true, Stop: true,
            BeforeTool: GuardTier.NativeHook, TurnEnd: TurnEndMode.NativeHook, Ask: AskMode.ResumeReply, Cost: CostMode.Dollars), "claude");

    public string Id => _identity.Id;
    public string DisplayName => _identity.DisplayName;
    public string SessionTool => _identity.SessionTool;
    public AgentCapabilities Capabilities => _identity.Capabilities;

    public AgentLocation Locate(SozSettings settings)
    {
        var r = ClaudeLocator.ResolveDetailed(AgentRegistry.ConfiguredPath(Id, settings));
        return new AgentLocation(r.Cmd, r.Error);
    }

    public string? ReadVersion(LaunchCommand cmd, TimeSpan? timeout = null) => AgentVersion.Read(cmd, this, timeout);

    public string DefaultModel(SozSettings settings) => settings.RunnerModel;

    public AgentLaunch BuildStart(LaunchCommand cmd, AgentRunContext ctx) => Build(cmd, ctx, null);
    public AgentLaunch BuildResume(LaunchCommand cmd, AgentRunContext ctx, string sessionId) => Build(cmd, ctx, sessionId);

    private AgentLaunch Build(LaunchCommand cmd, AgentRunContext ctx, string? resume)
    {
        string settingsFile;
        try { settingsFile = ctx.HookSettings?.Invoke() ?? throw new InvalidOperationException("terminal desteği ayarı verilmedi"); }
        catch (Exception e) { throw new AgentLaunchException("Söz'ün terminal desteği ayarı hazırlanamadı: " + SecretFilter.MaskSecrets(e.Message)); }

        var args = new List<string>(cmd.PrefixArgs);
        args.AddRange(new[] { "-p", "--output-format", "stream-json", "--verbose", "--settings", settingsFile });
        args.AddRange(PermissionArgs(ctx.Permission, ctx.Settings.RunnerAllowedTools));
        if (ctx.Model.Length > 0) { args.Add("--model"); args.Add(ctx.Model); }
        if (resume is not null) { args.Add("--resume"); args.Add(resume); }
        var env = new Dictionary<string, string?>(AgentEnv.Merge(cmd, ShouldStripEnv), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOZ_SERVER_URL")))
            env["SOZ_SERVER_URL"] = Environment.GetEnvironmentVariable("SOZ_SERVER_URL");
        return new AgentLaunch(cmd.FileName, args, ctx.Prompt, env, AgentEnv.StripList(ShouldStripEnv), cmd.Display);
    }

    private static string[] PermissionArgs(AgentPermissionMode mode, string allowedTools) => mode switch
    {
        // Temkinli: düzenleme serbest, izin verilen araçlar listesi + bekçi kancası. Salt okunur: planlama kipi, yazan araç yok.
        AgentPermissionMode.ReadOnly => new[] { "--permission-mode", "plan", "--allowedTools", "Read,Glob,Grep" },
        _ => new[] { "--permission-mode", "acceptEdits", "--allowedTools", allowedTools },
    };

    public string DescribePermission(AgentPermissionMode mode)
        => string.Join(' ', PermissionArgs(mode, "<araçlar>"));

    public IAgentEventStream CreateEventStream() => new RunEventTranslator();

    public void Stop(ProcessTree tree) => tree.KillTree();

    /// <summary>Kullanıcının aboneliği kullanılır: ücretli API anahtarı ve yönlendirme değişkenleri çocuk sürece geçmez.</summary>
    public bool ShouldStripEnv(string name) => PlainExplainer.ShouldStrip(name);

    private static readonly Regex LoginRe = new(@"(not logged in|please run /login|/login\b|invalid api key|authentication[_ ]error|oauth token|token has expired|unauthorized|401)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LimitRe = new(@"(usage limit|rate limit|limit reached|credit balance|quota)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string FailureReason(int? exit, IAgentEventStream events, string stderr)
    {
        var blob = string.Join("\n", new[] { events.ResultText, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (LoginRe.IsMatch(blob))
            return "Claude Code'a giriş yapılmamış görünüyor. Claude Code uygulamasını bir kez aç ve hesabınla giriş yap, sonra işi yeniden başlat.";
        if (LimitRe.IsMatch(blob))
            return "Claude kullanım sınırına takıldı. Sınır açılınca işi yeniden başlat.";
        return AgentText.GenericFailure(exit, blob);
    }
}

/// <summary>Bağlayıcılar arasında ortak küçük yardımcılar.</summary>
internal static class AgentText
{
    public static string FirstLine(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    public static string GenericFailure(int? exit, string blob)
    {
        var first = FirstLine(blob);
        if (first.Length > 0) return "Yardımcı işi yarıda bırakıp durdu."; // ham hata metni cümleye girmez: yürütücü onu "error" satırının teknik ayrıntısına yazar
        return exit is not (null or 0) ? "Yardımcı beklenmedik biçimde kapandı." : "Yardımcı sonuç vermeden kapandı.";
    }
}

/// <summary>Ortam değişkeni birleştirme: .cmd sarmalayıcısının ayarları eklenir, ama bağlayıcının sildirdiği değişkenler (faturalı anahtar vb.) sarmalayıcıdan da geçmez.</summary>
internal static class AgentEnv
{
    public static IReadOnlyDictionary<string, string?> Merge(LaunchCommand cmd, Func<string, bool> strip)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (cmd.Env is not null)
            foreach (var (k, v) in cmd.Env)
                if (!strip(k)) d[k] = v;
        return d;
    }

    /// <summary>Çocuk ortamdan silinecek, bu süreçte var olan değişken adları.</summary>
    public static IReadOnlyList<string> StripList(Func<string, bool> strip)
        => Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(strip).ToList();
}

/// <summary>Sürüm okuma: `--version`, kısa süre sınırı, model çağırmaz. Hata ya da zaman aşımında null.</summary>
internal static class AgentVersion
{
    public static string? Read(LaunchCommand cmd, IAgentConnector connector, TimeSpan? timeout)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(cmd.FileName)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            };
            foreach (var a in cmd.PrefixArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--version");
            foreach (var k in AgentEnv.StripList(connector.ShouldStripEnv)) psi.Environment.Remove(k);
            foreach (var (k, v) in AgentEnv.Merge(cmd, connector.ShouldStripEnv)) psi.Environment[k] = v;
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            try { p.StandardInput.Close(); } catch (Exception) { }
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(8)).TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            Task.WaitAll(new Task[] { outTask, errTask }, 2000);
            if (p.ExitCode != 0) return null;
            var text = outTask.IsCompletedSuccessfully ? outTask.Result : "";
            var line = AgentText.FirstLine(text);
            return line.Length == 0 ? null : TextNormalizer.Truncate(SecretFilter.MaskSecrets(line), 80);
        }
        catch (Exception) { return null; }
    }
}
