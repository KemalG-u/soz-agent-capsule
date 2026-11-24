using Soz.Core.Proof;
using Soz.Core.Registry;
using Soz.Core.Run;

namespace Soz.Core.Agents;

/// <summary>
/// Bekçinin (araçtan önce araya girme) hangi kademede çalıştığı. Arayüz bunu dürüstçe gösterir, "güvenli" ya da "güvensiz" demez.
/// NativeHook: aracın kendi kancası, her çağrıdan önce (yalnız kanca çalıştığı sürece). ProtocolPermission: ajan izin sorduğunda protokol yoluyla.
/// OsSandbox: işletim sistemi ya da araç sınırı, proje dışına yazmayı keser ama komut bazında "sor" yoktur. None: hiçbiri.
/// </summary>
public enum GuardTier { None, NativeHook, ProtocolPermission, OsSandbox }

/// <summary>
/// Tur bitince (kanıt kapısı) araya girme biçimi. NativeHook: aracın Stop kancası, ajan kendi turunun içinde geri yollanır.
/// OuterLoop: "dış kapı döngüsü": süreç çıkınca Söz ölçütleri kendisi koşar, kalırsa aynı oturuma sürdürme komutuyla geri yollar.
/// </summary>
public enum TurnEndMode { None, NativeHook, OuterLoop }

/// <summary>Kullanıcıya soru: ResumeReply, ajanın son sözü soruysa iş "cevap bekliyor" olur, cevap aynı oturuma yeni tur olarak gider. Live: protokol üzerinden canlı.</summary>
public enum AskMode { None, ResumeReply, Live }

/// <summary>Harcama bilgisinin ne olduğu: yok, token sayıları, dolar tutarı (token sayıları ile birlikte).</summary>
public enum CostMode { None, Tokens, Dollars }

/// <summary>Söz'ün ortak izin kipi. Careful ("temkinli"): proje içinde düzenleme serbest, tehlikeli işlemler bekçiye ya da sınıra takılır. ReadOnly: yalnız okuma.</summary>
public enum AgentPermissionMode { Careful, ReadOnly }

/// <summary>Bağlayıcının beyan ettiği yetenekler (P7). Arayüz ve iş akışı alternatif kademeyi buna göre seçer.</summary>
public sealed record AgentCapabilities(
    bool Headless, bool EventStream, bool Resume, bool Stop,
    GuardTier BeforeTool, TurnEndMode TurnEnd, AskMode Ask, CostMode Cost);

/// <summary>Bulma sonucu: çalıştırılabilir komut ya da sade hata cümlesi.</summary>
public sealed record AgentLocation(LaunchCommand? Cmd, string? Error);

/// <summary>Çalıştırma bağlamı: bir tur için bağlayıcıya verilen her şey. HookSettings tembeldir: yalnız kancası olan bağlayıcı çağırır (Söz'ün kanca ayar dosyası yazılır).</summary>
public sealed record AgentRunContext(
    string ProjectPath, string Prompt, string Model, AgentPermissionMode Permission,
    long RunId, long JobId, SozSettings Settings, Func<string>? HookSettings);

/// <summary>
/// Başlatma tanımı: çalıştırılacak dosya, tam argüman listesi (kabuk yok, ArgumentList), standart girdiye yazılacak metin (istem),
/// çocuk ortama EKLENECEK değişkenler (.cmd sarmalayıcısının ayarladıkları dahil) ve SİLİNECEK değişken adları (araç başına).
/// </summary>
public sealed record AgentLaunch(string FileName, IReadOnlyList<string> Args, string? Stdin, IReadOnlyDictionary<string, string?> AddEnv, IReadOnlyList<string> RemoveEnv, string Display);

/// <summary>Başlatma hazırlığı sade bir nedenle başarısız oldu (ör. kanca ayarı yazılamadı).</summary>
public sealed class AgentLaunchException : Exception
{
    public AgentLaunchException(string message) : base(message) { }
}

/// <summary>Harcama: bir turun sonunda aracın bildirdiği sayılar. Dolar yalnız araç bildiriyorsa dolar (Claude), Codex yalnız token verir.</summary>
public sealed record AgentUsage(long? InputTokens, long? CachedInputTokens, long? OutputTokens, long? ReasoningTokens, double? CostUsd)
{
    public bool IsEmpty => InputTokens is null && CachedInputTokens is null && OutputTokens is null && ReasoningTokens is null && CostUsd is null;
}

/// <summary>Bir süreç çıktısını sade Türkçe satırlara çeviren, durum tutan okuyucu (süreç başına bir tane).</summary>
public interface IAgentEventStream
{
    /// <summary>Bir stdout satırını işler (bozuk JSON ve bilinmeyen olay boş döner).</summary>
    IReadOnlyList<RunLine> Feed(string line);
    /// <summary>Bekleyen satırı bırakır (akış bitince).</summary>
    IReadOnlyList<RunLine> Flush();
    string? SessionId { get; }
    bool ResultSeen { get; }
    bool ResultIsError { get; }
    string? ResultText { get; }
    string? LastAgentText { get; }
    int? NumTurns { get; }
    /// <summary>Harcama (12. işlem): sonuç olayından okunur; yoksa null.</summary>
    AgentUsage? Usage { get; }
}

/// <summary>
/// Ajan bağlayıcısı (P7). Söz çekirdeği (RunManager) ajanı bilmez, yalnız bunu bilir. Araca özgü her şey (bayraklar, olay biçimi, yer bulma, giriş hatası cümleleri) burada yaşar.
/// 12 işlem:
///  1 bul (Locate), 2 sürüm (ReadVersion), 3 başlat (BuildStart), 4 istem ver (AgentLaunch.Stdin), 5 olay akışı (CreateEventStream),
///  6 sürdür (BuildResume), 7 durdur (Stop), 8 izin kipi (AgentRunContext.Permission, DescribePermission),
///  9 bitişte araya gir (Capabilities.TurnEnd), 10 araçtan önce araya gir (Capabilities.BeforeTool),
///  11 soru sor (Capabilities.Ask), 12 harcama oku (IAgentEventStream.Usage).
/// Zorunlu çekirdek 1-8; 9-12 yetenek beyanıdır: araç desteklemiyorsa Söz alternatif kademeyi seçer ve arayüzde dürüstçe gösterir.
/// </summary>
public interface IAgentConnector
{
    /// <summary>Kararlı kimlik (kayıt defterindeki connector:<kimlik>), ör. claude, codex. jobs.agent ve runs.agent bunu taşır.</summary>
    string Id { get; }
    string DisplayName { get; }
    /// <summary>sessions.tool değeri (Söz'ün oturum kaydı).</summary>
    string SessionTool { get; }
    AgentCapabilities Capabilities { get; }

    /// <summary>Ayardaki yola (yoksa PATH üzerinde) göre başlatma komutunu çözer.</summary>
    AgentLocation Locate(SozSettings settings);
    /// <summary>Aracın sürümü (kısa süre sınırı ile; okunamazsa null). Model çağırmaz.</summary>
    string? ReadVersion(LaunchCommand cmd, TimeSpan? timeout = null);
    AgentLaunch BuildStart(LaunchCommand cmd, AgentRunContext ctx);
    AgentLaunch BuildResume(LaunchCommand cmd, AgentRunContext ctx, string sessionId);
    IAgentEventStream CreateEventStream();
    /// <summary>İşin süreç ağacını kapatır (yalnız Söz'ün başlattığı).</summary>
    void Stop(ProcessTree tree);
    /// <summary>Ortak izin kipinin bu araçtaki karşılığı (bayrak özeti, sade metin değil; test ve belge için).</summary>
    string DescribePermission(AgentPermissionMode mode);
    /// <summary>İş başına model verilmediyse bu araçta kullanılacak varsayılan model (boş = aracın kendi varsayılanı).</summary>
    string DefaultModel(SozSettings settings);
    /// <summary>Çocuk ortamdan silinecek değişken mi (kullanıcının aboneliği kullanılsın, faturalı anahtar sızmasın).</summary>
    bool ShouldStripEnv(string name);
    /// <summary>Süreç hata ile bitince kullanıcıya gösterilecek sade neden.</summary>
    string FailureReason(int? exit, IAgentEventStream events, string stderr);
    /// <summary>Bekçi etiketinin kısa sade hâli ("komut komut", "sınırlı", "sorulur", "yok").</summary>
    string GuardLabel => Capabilities.BeforeTool switch
    {
        GuardTier.NativeHook => "komut komut",
        GuardTier.ProtocolPermission => "sorulur",
        GuardTier.OsSandbox => "sınırlı",
        _ => "yok",
    };
    /// <summary>
    /// "Durdur" araç işini gerçekten durdurmuyorsa (Capabilities.Stop false) kişiye gösterilecek sade not; durduruyorsa null.
    /// Söz yalnız kendi başlattığı süreç ağacını kapatır; araç işi kendi arka plan hizmetinde sürdürüyorsa (ör. Hermes) bu durdurmak sayılmaz ve kişi bunu bilmelidir.
    /// </summary>
    string? StopNote => Capabilities.Stop ? null
        : $"{DisplayName} işi kendi arka plan hizmetinde yürütüyor olabilir: Söz izlemeyi kesti ama yardımcının durduğunu garanti edemez. Gerekirse {DisplayName}'in kendi ekranında durdur.";
}

/// <summary>
/// Bilinen bağlayıcılar. Kaynak: kayıt defteri (`connector:*`, Registry\kayit\baglayicilar.soz.json). Çalıştırılabilen bağlayıcı (depth run, protocol dialect)
/// koddaki lehçe sınıfına bağlanır; yalnız gözleyenler (depth observe) ajan listesine girmez, ObserveRunner okur. Kimlik büyük küçük harfe duyarsız.
/// Yeni ajan: kayıt dosyası + (gözlemek için) bir okuyucu sınıfı; bu dosyaya satır eklenmez.
/// </summary>
public static class AgentRegistry
{
    /// <summary>Lehçe adı → bağlayıcı üreticisi. Yeni lehçe = yeni sınıf + bir satır (ACP gibi genel lehçeler ileride).</summary>
    private static readonly Dictionary<string, Func<AgentIdentity, IAgentConnector>> Dialects = new(StringComparer.Ordinal)
    {
        ["claude-stream-json"] = i => new ClaudeConnector(i),
        ["codex-exec-json"] = i => new CodexConnector(i),
        ["hermes-chat-stream-json"] = i => new HermesConnector(i),
        ["opencode-run-json"] = i => new OpenCodeConnector(i),
        ["antigravity-stream-json"] = i => new AntigravityConnector(i),
    };

    private static readonly Lazy<RegistryLoadResult> Loaded = new(() => RegistryLoader.LoadEmbedded());
    private static readonly Lazy<IReadOnlyList<ConnectorSpec>> SpecList = new(() => DefinitionsFrom(Loaded.Value));

    /// <summary>Verilen yükleme sonucundaki etkin bağlayıcı kayıtları (kişi dosyasından gelenler dahil; onay bekleyenler "suggested" olduğu için girmez).</summary>
    public static IReadOnlyList<ConnectorSpec> SpecsFrom(RegistryLoadResult load)
        => load.OfKind(RegistryKinds.Connector).Where(e => e.State is RegistryStates.Enabled or RegistryStates.Deprecated).Select(ConnectorSpec.From).OfType<ConnectorSpec>().ToList();

    /// <summary>
    /// Tanımlar: etkin kayıtlar + Söz'ün kendi kapalı gelen kayıtları (state suggested, ör. Hermes ve OpenCode: çalıştırılabilir ama kapalı gelir). Kök çözmek ve araç adını bulmak içindir;
    /// kapalı bir kayıt bununla etkinleşmez (<see cref="ConnectorSpec.IsActive"/>). Kişi dosyasından gelen onaysız kayıt buraya GİRMEZ.
    /// </summary>
    public static IReadOnlyList<ConnectorSpec> DefinitionsFrom(RegistryLoadResult load)
        => load.OfKind(RegistryKinds.Connector)
            .Where(e => e.State is RegistryStates.Enabled or RegistryStates.Deprecated || (e.State is RegistryStates.Suggested or RegistryStates.Disabled && e.Owner == RegistryOwners.Fixed))
            .Select(ConnectorSpec.From).OfType<ConnectorSpec>().ToList();
    private static readonly Lazy<IReadOnlyList<IAgentConnector>> RunList = new(BuildRunConnectors);

    /// <summary>Pakete gömülü kayıt defteri (bir kez yüklenir).</summary>
    public static RegistryLoadResult Registry => Loaded.Value;

    /// <summary>Bütün bağlayıcı tanımları (çalıştıranlar ve yalnız gözleyenler; kapalı gelenler dahil, bkz. <see cref="DefinitionsFrom"/>).</summary>
    public static IReadOnlyList<ConnectorSpec> Specs => SpecList.Value;

    private static IReadOnlyList<IAgentConnector> BuildRunConnectors() => RunConnectorsFrom(Specs);

    /// <summary>Tanımlardan çalıştırılabilir bağlayıcı listesi (kayıt sırasıyla). Tam yol ve sınama aynı işlevi kullanır.</summary>
    internal static IReadOnlyList<IAgentConnector> RunConnectorsFrom(IReadOnlyList<ConnectorSpec> specs)
    {
        var list = Build(specs, onlyActive: true);
        if (list.Count == 0) throw new InvalidOperationException("Kayıt defterinde çalıştırılabilir yardımcı aracı yok (baglayicilar.soz.json kaydı bozuk).");
        return list;
    }

    private static List<IAgentConnector> Build(IReadOnlyList<ConnectorSpec> specs, bool onlyActive)
    {
        var list = new List<IAgentConnector>();
        foreach (var s in specs.Where(x => (!onlyActive || x.IsActive) && x.CanRun && x.Protocol == "dialect" && x.Dialect is not null && x.Capabilities is not null))
        {
            if (!Dialects.TryGetValue(s.Dialect!, out var make)) continue; // lehçesi bu sürümde yok: ajan listesine girmez
            list.Add(make(new AgentIdentity(s.Id, s.Title, s.SessionTool, s.Capabilities!, s.Command)));
        }
        return list;
    }

    private static readonly Lazy<IReadOnlyList<IAgentConnector>> KnownList = new(() => Build(Specs, onlyActive: false));

    /// <summary>
    /// Bilinen çalıştırılabilir bağlayıcılar: açık olanlar (claude, codex) ve kapalı gelenler (ör. hermes, opencode; kişi Tanışma'da onaylayana kadar).
    /// Yalnız ad çözmek içindir (iş yazarken "bilinmeyen ajan" denmesin); bir işin gerçekten başlaması <see cref="IsOpenIn"/> ile onaya bağlıdır.
    /// </summary>
    public static IReadOnlyList<IAgentConnector> Known => KnownList.Value;

    /// <summary>Verilen yükleme sonucundaki (kişi onayları dahil) AÇIK çalıştırılabilir bağlayıcılar, kayıt sırasıyla.</summary>
    public static IReadOnlyList<IAgentConnector> OpenFrom(RegistryLoadResult load) => Build(SpecsFrom(load), onlyActive: true);

    /// <summary>Bu ajan açık mı? Kişi veritabanındaki kapatma/onay durumunu dikkate alır.</summary>
    public static bool IsOpenIn(string agentId, Func<RegistryLoadResult> loadFactory)
        => OpenFrom(loadFactory()).Any(c => c.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Kimliği verilmeyen işte kullanılan ajan: kayıttaki ilk çalıştırılabilir bağlayıcı.</summary>
    public static string Default => Connectors[0].Id;

    public static IReadOnlyList<IAgentConnector> Connectors => RunList.Value;
    public static IEnumerable<string> Ids => Connectors.Select(c => c.Id);

    public static IAgentConnector? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : Connectors.FirstOrDefault(c => c.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bilinmeyen kimlik varsayılana düşmez, hata verir (sessiz yanlış ajan olmasın). Boş değer varsayılandır.
    /// Kapalı gelen ama tanımlı bağlayıcı (hermes, opencode) da çözülür: iş yazılabilir, başlatma onaya bağlıdır (RunManager, bkz. <see cref="IsOpenIn"/>).
    /// </summary>
    public static IAgentConnector Get(string? id)
        => string.IsNullOrWhiteSpace(id) ? Connectors[0]
            : Find(id) ?? Known.FirstOrDefault(c => c.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new SozException(400, $"Bilinmeyen yardımcı: {id}. Geçerli yardımcılar: {string.Join(", ", Ids)}.");

    public static string? Check(string? id)
        => id is null || Find(id) is not null ? null : $"Bilinmeyen yardımcı: {id}. Geçerli yardımcılar: {string.Join(", ", Ids)}.";

    /// <summary>Kancası (araçtan önce araya girme) aracın kendi kancası olan ajanın oturum adı: kanca olayları bu aracın oturumuna yazılır.</summary>
    public static string HookSessionTool
        => (RegistryLoader.EmbeddedOverride.Value is null ? HookToolLazy.Value : HookToolLight(RegistryLoader.EmbeddedSources())) ?? HookToolFull(Connectors);

    private static readonly Lazy<string?> HookToolLazy = new(() => HookToolLight(RegistryLoader.EmbeddedSources()));

    /// <summary>Tam yol: yüklenmiş kayıt defterinden çıkan bağlayıcı listesinden seçim. Hafif okumanın eşitlik ölçütü budur.</summary>
    internal static string HookToolFull(IReadOnlyList<IAgentConnector> connectors)
        => (connectors.FirstOrDefault(c => c.Capabilities.BeforeTool == GuardTier.NativeHook) ?? connectors[0]).SessionTool;

    /// <summary>
    /// Kanca yolu için hafif okuma: gömülü kayıt dosyalarında yalnız bağlayıcı kayıtlarına bakar (JsonDocument, tek geçiş; şema denetimi ve düzenli ifade yok).
    /// Kayıt defterinin tamamını yüklemek (şema doğrulaması, NonBacktracking ifadeler) her kancada onlarca ms tutuyordu. Her zaman tam yolla AYNI sonucu verir:
    /// gömülü kayıt normal biçimde değilse (bozuk JSON, tanınmayan sürüm, eksik zorunlu alan, tekrarlı kimlik, bilinmeyen durum, çalıştırılabilir bağlayıcı yok)
    /// null döner ve çağıran tam yola düşer; böylece bozuk kayıtta davranış eskisiyle aynı kalır. Kişi klasöründen gelen kayıtlar zaten
    /// <see cref="Loaded"/> içine girmez (yalnız gömülü kayıtlar yüklenir), bu yüzden burada da okunmaz.
    /// </summary>
    internal static string? HookToolLight(IEnumerable<RegistrySource> sources)
    {
        string? first = null, native = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in sources)
        {
            var bytes = src.Bytes.AsSpan();
            if (bytes.IndexOf("connector"u8) < 0) continue; // bağlayıcı kaydı olmayan dosya
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(bytes.ToArray());
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                if (!root.TryGetProperty("schemaVersion", out var ver) || ver.ValueKind != System.Text.Json.JsonValueKind.Number || !ver.TryGetInt32(out var v) || v != 1) return null;
                if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                foreach (var e in entries.EnumerateArray())
                {
                    if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                    if (!e.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != System.Text.Json.JsonValueKind.String) return null;
                    if (kindEl.GetString() != RegistryKinds.Connector) continue;
                    if (!e.TryGetProperty("id", out var idEl) || idEl.ValueKind != System.Text.Json.JsonValueKind.String) return null;
                    var id = idEl.GetString()!;
                    if (!RegistryLoader.IsValidId(id) || !id.StartsWith("connector:", StringComparison.Ordinal) || !seen.Add(id)) return null;
                    if (!e.TryGetProperty("owner", out var ownerEl) || ownerEl.ValueKind != System.Text.Json.JsonValueKind.String || ownerEl.GetString() != RegistryOwners.Fixed) return null;
                    if (!e.TryGetProperty("title", out var titleEl) || titleEl.ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Object)) return null;
                    string? state = null;
                    if (e.TryGetProperty("state", out var stEl))
                    {
                        if (stEl.ValueKind != System.Text.Json.JsonValueKind.String) return null;
                        state = stEl.GetString();
                        if (state is not (RegistryStates.Enabled or RegistryStates.Disabled or RegistryStates.Suggested or RegistryStates.Deprecated)) return null; // broken ve bilinmeyen: tam yol karar versin
                    }
                    if (!e.TryGetProperty("body", out var b) || b.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                    if (!b.TryGetProperty("depth", out var depth) || depth.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                    if (!b.TryGetProperty("detect", out var detect) || detect.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                    var canRun = false;
                    foreach (var d in depth.EnumerateArray())
                    {
                        if (d.ValueKind != System.Text.Json.JsonValueKind.String) return null;
                        if (d.GetString() == "run") canRun = true;
                    }
                    if (state is not (null or RegistryStates.Enabled or RegistryStates.Deprecated)) continue; // etkin değil: çalıştırma listesine girmez
                    if (!canRun) continue;
                    if (!b.TryGetProperty("launch", out var launch) || launch.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                    var protocol = launch.TryGetProperty("protocol", out var pr) && pr.ValueKind == System.Text.Json.JsonValueKind.String ? pr.GetString() : null;
                    if (protocol != "dialect") continue;
                    var dialect = launch.TryGetProperty("dialect", out var dl) && dl.ValueKind == System.Text.Json.JsonValueKind.String ? dl.GetString() : null;
                    if (dialect is null || !Dialects.ContainsKey(dialect)) continue;
                    if (!b.TryGetProperty("capabilities", out var caps) || caps.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var name = id["connector:".Length..];
                    first ??= name;
                    if (native is null && caps.TryGetProperty("beforeTool", out var bt) && bt.ValueKind == System.Text.Json.JsonValueKind.String && bt.GetString() == "native-hook") native = name;
                }
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return native ?? first;
    }

    /// <summary>Ayarlarda bu ajan için yazılmış yol (kayıttaki detect.pathSetting → ayarın eski anahtarı). Boşsa null.</summary>
    public static string? ConfiguredPath(string agentId, SozSettings settings)
    {
        var spec = Specs.FirstOrDefault(s => s.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (spec?.PathSetting is null) return null;
        var key = Loaded.Value.Get(spec.PathSetting)?.Setting?.LegacyKey;
        var v = key is null ? null : settings.Str(key);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
