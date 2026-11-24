using System.Text.Json.Nodes;
using Soz.Core.Registry;

namespace Soz.Core.Agents;

/// <summary>Bir bağlayıcı kaydının koda dökülmüş hâli (okunan tek kaynak: kayıt defteri, `connector:*`).</summary>
public sealed record ConnectorSpec(
    string Id, string Title, IReadOnlyList<string> Depth, IReadOnlyList<string> Commands, string? PathSetting,
    string? Protocol, string? Dialect, AgentCapabilities? Capabilities,
    string? Reader, string? RootSetting, IReadOnlyList<string> RootEnv, bool UserEnv, bool EnvNeedsSubdir, string? DefaultRoot, string? Subdir,
    string State = RegistryStates.Enabled)
{
    /// <summary>Etkin mi? Kapalı gelen kayıt (state suggested, ör. Hermes ve OpenCode: çalıştırılabilir ama kapalı gelir) kişi onaylayana kadar tanım olarak durur, hiçbir şeye bakmaz.</summary>
    public bool IsActive => State is RegistryStates.Enabled or RegistryStates.Deprecated;
    public bool CanRun => Depth.Contains("run");
    public bool CanObserve => Depth.Contains("observe") && !string.IsNullOrEmpty(Reader) && Reader != "none";
    /// <summary>Oturum kayıtlarında (sessions.tool) bu aracın adı. Kayıt kimliğiyle aynıdır.</summary>
    public string SessionTool => Id;
    public string Command => Commands.Count > 0 ? Commands[0] : Id;

    public static ConnectorSpec? From(RegistryEntry e)
    {
        if (e.Kind != RegistryKinds.Connector || !e.Id.StartsWith("connector:", StringComparison.Ordinal)) return null;
        var b = e.Body;
        var launch = b["launch"] as JsonObject;
        var detect = b["detect"] as JsonObject;
        var hist = b["history"] as JsonObject;
        return new ConnectorSpec(
            e.Id["connector:".Length..], e.Title.Tr,
            Strs(b["depth"]), Strs(detect?["commands"]), Str(detect, "pathSetting"),
            Str(launch, "protocol"), Str(launch, "dialect"), Caps(b["capabilities"] as JsonObject),
            Str(hist, "reader"), Str(hist, "rootSetting"), Strs(hist?["rootEnv"]),
            Bool(hist, "userEnv"), Bool(hist, "envNeedsSubdir"), Str(hist, "defaultRoot"), Str(hist, "subdir"), e.State);
    }

    private static string? Str(JsonObject? o, string k) => (o?[k] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    private static bool Bool(JsonObject? o, string k) => (o?[k] as JsonValue)?.TryGetValue<bool>(out var v) == true && v;
    private static IReadOnlyList<string> Strs(JsonNode? n) => n is JsonArray a ? a.Select(x => (x as JsonValue)?.TryGetValue<string>(out var s) == true ? s : "").Where(x => x.Length > 0).ToList() : Array.Empty<string>();

    private static AgentCapabilities? Caps(JsonObject? c)
    {
        if (c is null) return null;
        bool B(string k) => Bool(c, k);
        return new AgentCapabilities(B("headless"), B("eventStream"), B("resume"), B("stop"),
            Str(c, "beforeTool") switch { "native-hook" => GuardTier.NativeHook, "protocol-permission" => GuardTier.ProtocolPermission, "os-sandbox" => GuardTier.OsSandbox, _ => GuardTier.None },
            Str(c, "turnEnd") switch { "native-hook" => TurnEndMode.NativeHook, "outer-loop" => TurnEndMode.OuterLoop, _ => TurnEndMode.None },
            Str(c, "ask") switch { "resume-reply" => AskMode.ResumeReply, "live" => AskMode.Live, _ => AskMode.None },
            Str(c, "cost") switch { "dollars" => CostMode.Dollars, "tokens" => CostMode.Tokens, _ => CostMode.None });
    }
}

/// <summary>Bir bağlayıcının koda verilen kimliği: kayıttan gelir. Lehçe sınıfları (Claude, Codex) bunu taşır; verilmezse bilinen varsayılanlar kullanılır.</summary>
public sealed record AgentIdentity(string Id, string DisplayName, string SessionTool, AgentCapabilities Capabilities, string Command);
