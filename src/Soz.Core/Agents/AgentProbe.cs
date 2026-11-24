using System.Collections.Concurrent;

namespace Soz.Core.Agents;

/// <summary>Bir ajanın yetenek beyanı, arayüze gidecek biçimde (kararlı küçük harf adlar).</summary>
public sealed record AgentCapabilitiesDto(bool Headless, bool EventStream, bool Resume, bool Stop, string GuardTier, string TurnEnd, string Ask, string Cost);

/// <summary>
/// GET /api/agents satırı. Found: komut çözüldü. Ready: bulundu ve sürüm okunabildi (çalışıyor görünüyor).
/// GuardTier: native_hook | protocol_permission | os_sandbox | none. GuardLabel: "kanca" | "sorulur" | "sınırlı" | "yok" (arayüzdeki dürüst etiket).
/// TurnEnd: native_hook | outer_loop | none. Ask: resume_reply | live | none. Cost: dollars | tokens | none.
/// Path: ayardaki yol ya da çözülen komut (kullanıcının kendi bilgisayarındaki yol). Error: bulunamadıysa sade neden.
/// </summary>
public sealed record AgentInfo(string Id, string Name, bool Found, bool Ready, string? Version, string? Path, string? ConfiguredPath, string? Error,
    AgentCapabilitiesDto Capabilities, string GuardLabel, bool IsDefault);

/// <summary>Bulunan ajan araçlarını, sürümlerini ve yeteneklerini çıkarır. Sürüm okuma bir süreç başlatır (model çağırmaz); sonuç 60 sn önbelleğe alınır.</summary>
public static class AgentProbe
{
    private sealed record Cached(DateTime At, string? Version);
    private static readonly ConcurrentDictionary<string, Cached> Cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public static AgentCapabilitiesDto Describe(AgentCapabilities c) => new(c.Headless, c.EventStream, c.Resume, c.Stop,
        c.BeforeTool switch { GuardTier.NativeHook => "native_hook", GuardTier.ProtocolPermission => "protocol_permission", GuardTier.OsSandbox => "os_sandbox", _ => "none" },
        c.TurnEnd switch { TurnEndMode.NativeHook => "native_hook", TurnEndMode.OuterLoop => "outer_loop", _ => "none" },
        c.Ask switch { AskMode.ResumeReply => "resume_reply", AskMode.Live => "live", _ => "none" },
        c.Cost switch { CostMode.Dollars => "dollars", CostMode.Tokens => "tokens", _ => "none" });

    /// <summary>Açık ajanlar. <paramref name="load"/> verilirse (Söz'ün veri klasörüyle yüklenmiş kayıt defteri) kişinin Tanışma'da açtığı ajanlar da (hermes, opencode) listeye girer; verilmezse yalnız gömülü açıklar.</summary>
    public static List<AgentInfo> All(SozSettings st, bool readVersions = true, Soz.Core.Registry.RegistryLoadResult? load = null)
        => (load is null ? AgentRegistry.Connectors : AgentRegistry.OpenFrom(load)).Select(c => One(c, st, readVersions)).ToList();

    public static AgentInfo One(IAgentConnector c, SozSettings st, bool readVersion = true)
    {
        var loc = c.Locate(st);
        var configured = AgentRegistry.ConfiguredPath(c.Id, st);
        string? version = null;
        if (loc.Cmd is not null && readVersion)
        {
            var key = c.Id + "|" + loc.Cmd.FileName + "|" + string.Join(' ', loc.Cmd.PrefixArgs);
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < Ttl) version = hit.Version;
            else
            {
                version = c.ReadVersion(loc.Cmd);
                Cache[key] = new Cached(DateTime.UtcNow, version);
            }
        }
        var found = loc.Cmd is not null;
        var ready = found && (!readVersion || version is not null);
        return new AgentInfo(c.Id, c.DisplayName, found, ready, version, loc.Cmd?.Display, configured,
            found ? (ready ? null : $"{c.DisplayName} bulundu ama sürümü okunamadı. Bozuk olabilir.") : loc.Error,
            Describe(c.Capabilities), c.GuardLabel, c.Id.Equals(st.DefaultAgent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Testler için: önbelleği boşaltır.</summary>
    public static void ClearCache() => Cache.Clear();
}
