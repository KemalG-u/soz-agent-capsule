using Soz.Core.Agents;

namespace Soz.Core.Run;

/// <summary>
/// Bir ajan aracının iş vermeden önce görülen durumu (k1). Login: "yes" | "no" | "unknown". Status: ready | login_missing | login_unknown | broken | not_found | closed (kişi Tanışma'da onaylayana kadar kapalı araç: hiçbir şeye bakılmaz).
/// Sentence kişiye gösterilecek tek sade cümle, Action ne yapılacağı (hazırsa null), Detail bulunamama/bozukluk nedeninin ayrıntısı.
/// </summary>
public sealed record AgentReadyDto(string Id, string Name, bool Found, bool Ready, string? Version, string? Path, string Login, string Status, bool CanStart, string Sentence, string? Action, string? Detail, bool IsDefault);

/// <summary>CanStart: varsayılan ajanla iş şimdi başlatılabilir mi. Sentence: ilk işten önce gösterilecek tek cümle.</summary>
public sealed record AgentReadyReply(bool CanStart, string Sentence, string? DefaultAgent, List<AgentReadyDto> Agents);

/// <summary>
/// Ön kontrol: her çalıştırılabilir ajan aracı için bulundu mu, sürüm, giriş yapılmış görünüyor mu. Yalnız zararsız yollar: bağlayıcının sürüm okuması (`--version`, model çağırmaz)
/// ve giriş için aracın kimlik DOSYASININ var olup olmadığı (içeriği ASLA okunmaz). Gerçek istem gönderilmez. Araç listesi bağlayıcı kayıt defterinden gelir,
/// yeni bağlayıcı kendiliğinden kapsanır (giriş dosyası bilinmiyorsa Login "unknown").
/// </summary>
public static class AgentReadiness
{
    /// <summary>Kullanıcının ev klasörü. Testler ve izole denemeler USERPROFILE/HOME ile yönlendirir.</summary>
    public static string HomeDir()
        => Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } u ? u
         : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h
         : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Aracın kimlik dosyası (yalnız var/yok bakılır). Bilinmeyen araç için null.</summary>
    public static string? CredentialPath(string agentId)
    {
        string Dir(string env, string sub) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } d ? d : System.IO.Path.Combine(HomeDir(), sub);
        return agentId.ToLowerInvariant() switch
        {
            "claude" => System.IO.Path.Combine(Dir("CLAUDE_CONFIG_DIR", ".claude"), ".credentials.json"),
            "codex" => System.IO.Path.Combine(Dir("CODEX_HOME", ".codex"), "auth.json"),
            _ => null,
        };
    }

    /// <summary>Kod bilmeyene komut ve "terminal" söylenmez: yapılacak iş iki sade adımdır.</summary>
    private static string LoginHow(string id, string name) => $"{name} uygulamasını bir kez aç ve hesabınla giriş yap, sonra buradan işi başlat.";

    /// <summary>
    /// Kimlik dosyası yokken de giriş olabilir: anahtar ya da jeton ortam değişkeniyle verilmiş olabilir. Değerin kendisi okunmaz, yalnız var/yok bakılır.
    /// Bu durumda "giriş yapılmamış" demek yanlış alarm olur; hüküm "bilinmiyor" kalır.
    /// </summary>
    public static bool HasEnvCredential(string agentId)
    {
        static bool Has(string n) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n));
        return agentId.ToLowerInvariant() switch
        {
            "claude" => Has("ANTHROPIC_API_KEY") || Has("ANTHROPIC_AUTH_TOKEN") || Has("CLAUDE_CODE_OAUTH_TOKEN") || Has("CLAUDE_CODE_USE_BEDROCK") || Has("CLAUDE_CODE_USE_VERTEX"),
            "codex" => Has("OPENAI_API_KEY") || Has("CODEX_API_KEY"),
            _ => false,
        };
    }

    /// <summary>Aracın kendi sürüm yazısından ("codex-cli 0.9.1" gibi) yalnız sürüm numarasını alır; kişiye araç adı ya da kısaltma gösterilmez. Numara bulunamazsa yazı olduğu gibi kalır.</summary>
    public static string? CleanVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"\d+(\.\d+)+[\w.+-]*");
        return m.Success ? m.Value : raw.Trim();
    }

    public static AgentReadyDto Describe(AgentInfo a0)
    {
        var a = a0 with { Version = CleanVersion(a0.Version) };
        var cred = CredentialPath(a.Id);
        var login = cred is null ? "unknown" : File.Exists(cred) ? "yes" : HasEnvCredential(a.Id) ? "unknown" : "no";
        if (!a.Found)
            return new AgentReadyDto(a.Id, a.Name, false, false, null, a.Path, login, "not_found", false, $"{a.Name} bulunamadı.",
                "Kur ve yeniden dene. Ya da Ayarlar'da aracın yolunu göster.", a.Error, a.IsDefault);
        if (!a.Ready)
            return new AgentReadyDto(a.Id, a.Name, true, false, a.Version, a.Path, login, "broken", false, $"{a.Name} bulundu ama çalışmıyor görünüyor.",
                "Aracı yeniden kur ya da Ayarlar'da doğru yolu göster.", a.Error, a.IsDefault);
        var ver = a.Version is { Length: > 0 } v ? $" (sürüm {v})" : "";
        return login switch
        {
            "no" => new AgentReadyDto(a.Id, a.Name, true, true, a.Version, a.Path, login, "login_missing", false, $"{a.Name} kurulu ama giriş yapılmamış görünüyor.", LoginHow(a.Id, a.Name), null, a.IsDefault),
            "yes" => new AgentReadyDto(a.Id, a.Name, true, true, a.Version, a.Path, login, "ready", true, $"{a.Name} hazır{ver}. Giriş yapılmış görünüyor.", null, null, a.IsDefault),
            _ => new AgentReadyDto(a.Id, a.Name, true, true, a.Version, a.Path, login, "login_unknown", true, $"{a.Name} hazır{ver}. Giriş durumunu önceden göremiyorum, ilk işte anlaşılır.", null, null, a.IsDefault),
        };
    }

    /// <summary>Kapalı araç satırı: hiçbir şeye bakılmaz (sürüm, kimlik dosyası), yalnız nasıl açılacağı söylenir.</summary>
    public static AgentReadyDto DescribeClosed(IAgentConnector c, bool isDefault)
        => new(c.Id, c.DisplayName, false, false, null, null, "unknown", "closed", false, "Tanışma sayfasında onaylayınca açılır.", null, null, isDefault);

    /// <summary>
    /// <paramref name="load"/> verilirse (kişi onaylarıyla yüklenmiş kayıt defteri) açık araçlar ondan, kapalılar `closed` satırı olarak gelir. Verilmezse yalnız gömülü açıklar açıktır.
    /// Kök neden notu (b2): önceki sürüm yalnız gömülü açık araçları (Claude, Codex) listeliyordu, Hermes ve OpenCode hiç görünmüyordu.
    /// </summary>
    public static AgentReadyReply Check(SozSettings st, Soz.Core.Registry.RegistryLoadResult? load = null)
    {
        var open = (load is null ? AgentRegistry.Connectors : AgentRegistry.OpenFrom(load)).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<AgentReadyDto>();
        foreach (var c in AgentRegistry.Known)
            list.Add(open.Contains(c.Id) ? Describe(AgentProbe.One(c, st)) : DescribeClosed(c, c.Id.Equals(st.DefaultAgent, StringComparison.OrdinalIgnoreCase)));
        var def = list.FirstOrDefault(x => x.IsDefault) ?? list.FirstOrDefault();
        if (def is null) return new AgentReadyReply(false, "Hiçbir yardımcı tanımlı değil.", null, list);
        var sentence = def.Sentence;
        if (!def.CanStart && list.FirstOrDefault(x => x.CanStart && x.Id != def.Id) is { } alt)
            sentence += $" {alt.Name} hazır. İşe onu seçebilirsin.";
        return new AgentReadyReply(def.CanStart, sentence, def.Id, list);
    }
}
