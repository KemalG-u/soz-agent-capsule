using Soz.Core.Onboarding;

namespace Soz.Core.Assistants;

/// <summary>
/// Yardımcı araçların ve kod düzenleyicilerin merkezi kataloğu.
/// Sabit 4-araç listesi yerine Tanışma, Profil, Süreç Yoklama, Bağlayıcılar, Olay Akışı ve Ayarlar bu katalogdan beslenir.
/// </summary>
public sealed record AssistantDescriptor(
    string Id,
    string Title,
    string WhoLabel,
    IReadOnlyList<string> Commands,
    string? NpmPackage = null,
    string? PathSettingKey = null,
    string? Dialect = null,
    string? ReaderId = null,
    IReadOnlyList<string>? ProcessNames = null,
    bool IsAgentConnector = false,
    bool IsCoreProfileTool = false,
    bool IsDiscoveryOnly = false)
{
    public string PrimaryCommand => Commands.Count > 0 ? Commands[0] : Id;
    public IReadOnlyList<string> EffectiveProcessNames => ProcessNames ?? Commands;
}

public static class AssistantCatalog
{
    public static readonly IReadOnlyList<AssistantDescriptor> All = new[]
    {
        new AssistantDescriptor(
            Id: "claude",
            Title: "Claude Code",
            WhoLabel: "Claude",
            Commands: new[] { "claude" },
            NpmPackage: "@anthropic-ai/claude-code",
            PathSettingKey: "claudePath",
            Dialect: "claude-stream-json",
            ReaderId: "claude-jsonl",
            ProcessNames: new[] { "claude" },
            IsAgentConnector: true,
            IsCoreProfileTool: true),
        new AssistantDescriptor(
            Id: "codex",
            Title: "Codex",
            WhoLabel: "Codex",
            Commands: new[] { "codex" },
            NpmPackage: "@openai/codex",
            PathSettingKey: "codexPath",
            Dialect: "codex-exec-json",
            ReaderId: "codex-jsonl",
            ProcessNames: new[] { "codex" },
            IsAgentConnector: true,
            IsCoreProfileTool: true),
        new AssistantDescriptor(
            Id: "hermes",
            Title: "Hermes",
            WhoLabel: "Hermes",
            Commands: new[] { "hermes" },
            NpmPackage: null,
            PathSettingKey: "hermesPath",
            Dialect: "hermes-chat-stream-json",
            ReaderId: "hermes-db",
            ProcessNames: new[] { "hermes" },
            IsAgentConnector: true,
            IsCoreProfileTool: true),
        new AssistantDescriptor(
            Id: "opencode",
            Title: "OpenCode",
            WhoLabel: "OpenCode",
            Commands: new[] { "opencode" },
            NpmPackage: "opencode-ai",
            PathSettingKey: "opencodePath",
            Dialect: "opencode-run-json",
            ReaderId: "opencode-db",
            ProcessNames: new[] { "opencode" },
            IsAgentConnector: true,
            IsCoreProfileTool: true),
        new AssistantDescriptor(
            Id: "antigravity",
            Title: "Antigravity",
            WhoLabel: "Antigravity",
            Commands: new[] { "agy", "antigravity" },
            NpmPackage: null,
            PathSettingKey: "antigravityPath",
            Dialect: "antigravity-stream-json",
            ReaderId: "antigravity-db",
            ProcessNames: new[] { "antigravity", "agy" },
            IsAgentConnector: true,
            IsCoreProfileTool: false),
        new AssistantDescriptor(
            Id: "vscode",
            Title: "VS Code",
            WhoLabel: "VS Code",
            Commands: new[] { "code" },
            NpmPackage: null,
            PathSettingKey: null,
            Dialect: null,
            ReaderId: "vscode-storage",
            ProcessNames: new[] { "code" },
            IsAgentConnector: false,
            IsCoreProfileTool: false),
        new AssistantDescriptor(
            Id: "cursor",
            Title: "Cursor",
            WhoLabel: "Cursor",
            Commands: new[] { "cursor" },
            NpmPackage: null,
            PathSettingKey: null,
            Dialect: null,
            ReaderId: "cursor-storage",
            ProcessNames: new[] { "cursor" },
            IsAgentConnector: false,
            IsCoreProfileTool: false),
        new AssistantDescriptor(
            Id: "gemini",
            Title: "Gemini CLI",
            WhoLabel: "Gemini",
            Commands: new[] { "gemini" },
            NpmPackage: "@google/gemini-cli",
            IsDiscoveryOnly: true),
        new AssistantDescriptor(
            Id: "aider",
            Title: "Aider",
            WhoLabel: "Aider",
            Commands: new[] { "aider" },
            IsDiscoveryOnly: true),
        new AssistantDescriptor(
            Id: "goose",
            Title: "Goose",
            WhoLabel: "Goose",
            Commands: new[] { "goose" },
            NpmPackage: "block-goose",
            IsDiscoveryOnly: true),
    };

    private static readonly Dictionary<string, AssistantDescriptor> ByIdMap =
        All.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, AssistantDescriptor> ByProcessMap = BuildProcessMap();

    /// <summary>Çağıran kimliğinde (CallerIdentity) ata süreçte görüldüğünde ajan sayılan komut satırı süreç adları.</summary>
    public static readonly IReadOnlySet<string> CliAgentProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "codex", "hermes", "opencode", "agy"
    };

    public static IEnumerable<AssistantDescriptor> ProfileAssistants => All.Where(a => !a.IsDiscoveryOnly);
    public static IEnumerable<AssistantDescriptor> Connectors => All.Where(a => a.IsAgentConnector);
    public static IEnumerable<AssistantDescriptor> DiscoveryTools => All.Where(a => a.IsAgentConnector || a.IsDiscoveryOnly);

    public static AssistantDescriptor? Get(string? id)
        => !string.IsNullOrWhiteSpace(id) && ByIdMap.TryGetValue(id.Trim(), out var d) ? d : null;

    public static string Who(string? toolOrActor)
    {
        if (string.IsNullOrWhiteSpace(toolOrActor)) return "";
        if (string.Equals(toolOrActor, "soz", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolOrActor, "user", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolOrActor, "kisi", StringComparison.OrdinalIgnoreCase))
            return "Sen";
        return Get(toolOrActor)?.WhoLabel ?? toolOrActor;
    }

    public static string? ToolFromProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var clean = Path.GetFileNameWithoutExtension(processName.Trim());
        return ByProcessMap.TryGetValue(clean, out var d) ? d.Id : null;
    }

    /// <summary>Windows'ta PATH dışında aranacak ek kurulum yolları.</summary>
    public static IReadOnlyList<string> WindowsCandidatePaths(string commandOrId, string? home = null, string? localAppData = null, bool includeMachinePaths = true)
    {
        var h = string.IsNullOrWhiteSpace(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home!;
        var local = string.IsNullOrWhiteSpace(localAppData) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : localAppData!;
        var key = Path.GetFileNameWithoutExtension(commandOrId.Trim()).ToLowerInvariant();
        var list = new List<string>();

        if (key == "hermes")
        {
            if (h.Length > 0)
            {
                list.Add(Path.Combine(h, ".local", "bin", "hermes.cmd"));
                list.Add(Path.Combine(h, ".local", "bin", "hermes.exe"));
            }
            if (local.Length > 0)
                list.Add(Path.Combine(local, "hermes", "bin", "hermes.exe"));
            if (includeMachinePaths)
                list.Add(@"D:\tools\hermes-home\bin\hermes.exe");
        }
        else if (key is "agy" or "antigravity")
        {
            if (local.Length > 0)
            {
                list.Add(Path.Combine(local, "agy", "bin", "agy.exe"));
                list.Add(Path.Combine(local, "agy", "bin", "agy.cmd"));
                list.Add(Path.Combine(local, "Programs", "Antigravity", "bin", "agy.cmd"));
                list.Add(Path.Combine(local, "Programs", "Antigravity", "bin", "agy.exe"));
            }
            if (h.Length > 0)
            {
                list.Add(Path.Combine(h, ".local", "bin", "agy.cmd"));
                list.Add(Path.Combine(h, ".local", "bin", "agy.exe"));
            }
        }
        return list;
    }

    /// <summary>Tanışma tarayıcısının ek bilinen ikili klasörleri.</summary>
    public static IEnumerable<string> ExtraToolDirectories(OnboardingRoots r)
    {
        yield return Path.Combine(r.LocalAppData, "hermes", "bin");
        yield return Path.Combine(r.LocalAppData, "agy", "bin");
        yield return Path.Combine(r.LocalAppData, "Programs", "Antigravity", "bin");
        if (!r.IsSynthetic)
            yield return @"D:\tools\hermes-home\bin";
    }

    private static Dictionary<string, AssistantDescriptor> BuildProcessMap()
    {
        var d = new Dictionary<string, AssistantDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in ProfileAssistants)
            foreach (var p in a.EffectiveProcessNames)
                d.TryAdd(p, a);
        return d;
    }
}
