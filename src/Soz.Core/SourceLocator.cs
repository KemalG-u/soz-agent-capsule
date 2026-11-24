namespace Soz.Core;

public sealed record SourceRoot(string Tool, string? Root, string Origin, bool Exists);

/// <summary>Veri klasörü ve kayıt köklerinin bulunması.</summary>
public static class SourceLocator
{
    /// <summary>Son çözülen veri klasörü ve nereden seçildiği (doctor ve Ayarlar gösterir).</summary>
    public static (string Path, string Source)? LastResolved { get; private set; }

    /// <summary>
    /// Veri klasörü sırası: --data → SOZ_DATA → exe'nin yanında veri\ VARSA o → bir üst klasörde veri\soz.db VARSA o → exe'nin yanında yeni veri\.
    /// Böylece yayın klasöründen çift tıklayan kullanıcı, geliştirme sırasında birikmiş verisini (urun\veri) kaybolmuş görmez.
    /// </summary>
    public static string ResolveDataDir(string? arg) => ResolveDataDirWithSource(arg, AppContext.BaseDirectory).Path;

    public static (string Path, string Source) ResolveDataDirWithSource(string? arg, string exeDir)
    {
        (string, string) r;
        var env = Environment.GetEnvironmentVariable("SOZ_DATA");
        var beside = Path.Combine(exeDir, "veri");
        var parentDir = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDir)))?.FullName;
        var parent = parentDir is null ? null : Path.Combine(parentDir, "veri");
        var isVelopackInstall = parentDir is not null && File.Exists(Path.Combine(parentDir, "Update.exe"));
        if (!string.IsNullOrWhiteSpace(arg)) r = (Path.GetFullPath(arg), "--data");
        else if (!string.IsNullOrWhiteSpace(env)) r = (Path.GetFullPath(env), "SOZ_DATA");
        else if (Directory.Exists(beside)) r = (Path.GetFullPath(beside), "exe yanındaki veri klasörü");
        else if (parent is not null && File.Exists(Path.Combine(parent, "soz.db"))) r = (Path.GetFullPath(parent), "üst klasördeki veri klasörü (soz.db bulundu)");
        else if (isVelopackInstall && parent is not null) r = (Path.GetFullPath(parent), "kurulum kökündeki veri klasörü");
        else r = (Path.GetFullPath(beside), "exe yanında yeni veri klasörü");
        LastResolved = r;
        return r;
    }

    /// <summary>Verilen klasör en son çözülen klasörse onun kaynağı; değilse null.</summary>
    public static string? SourceOf(string dataDir)
        => LastResolved is { } l && string.Equals(Path.GetFullPath(l.Path).TrimEnd('\\', '/'), Path.GetFullPath(dataDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ? l.Source : null;

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Claude Code: argüman → ayar → CLAUDE_CONFIG_DIRprojects → %USERPROFILE%.claudeprojects (kuralları kayıt defterindeki connector:claude kaydının history alanı verir).</summary>
    public static SourceRoot Claude(string? arg, string? setting) => For("claude", arg, setting);

    /// <summary>Codex: argüman → ayar → CODEX_HOME (süreç, sonra kullanıcı ortamı)sessions → %USERPROFILE%.codexsessions (kayıttaki connector:codex).</summary>
    public static SourceRoot Codex(string? arg, string? setting) => For("codex", arg, setting);

    /// <summary>Hermes: argüman → ayar → HERMES_HOME (süreç, sonra kullanıcı ortamı) → %USERPROFILE%.hermes. Yalnız yolu çözer, hiçbir şeyi açmaz ya da listelemez.</summary>
    public static SourceRoot Hermes(string? arg, string? setting = null) => For("hermes", arg, setting);

    /// <summary>OpenCode: argüman → ayar → OPENCODE_DATA_DIR → %USERPROFILE%.localshareopencode. Yalnız yolu çözer.</summary>
    public static SourceRoot OpenCode(string? arg, string? setting = null) => For("opencode", arg, setting);

    /// <summary>Antigravity: argüman → ayar → %USERPROFILE%\.gemini\antigravity. Yalnız yolu çözer.</summary>
    public static SourceRoot Antigravity(string? arg = null, string? setting = null) => For("antigravity", arg, setting);

    /// <summary>
    /// Dosya tabanlı okuyucusu olan (jsonl) bağlayıcıların kayıt kökleri, ayardaki yol (kayıttaki history.rootSetting) dikkate alınarak. Doktor ve kaynak listesi için.
    /// Veritabanı tabanlı gözle araçları (Hermes, OpenCode) BURADA yoktur: onlar yalnız adı verilince okunur, tanı ekranı bile kendiliğinden onlara bakmaz.
    /// </summary>
    public static List<(SourceRoot Root, Readers.ReaderDef Reader)> FileSources(SozSettings st)
    {
        var list = new List<(SourceRoot, Readers.ReaderDef)>();
        foreach (var spec in Agents.AgentRegistry.Specs.Where(x => x.CanObserve))
        {
            var def = Readers.ReaderCatalog.Find(spec.Reader);
            if (def is null || !def.IsFileBased) continue;
            var key = spec.RootSetting is null ? null : Agents.AgentRegistry.Registry.Get(spec.RootSetting)?.Setting?.LegacyKey;
            list.Add((For(spec.Id, null, key is null ? null : st.Str(key)), def));
        }
        return list;
    }

    /// <summary>Kayıt defterindeki bağlayıcı kaydına (history alanı) göre kayıt kökü. Bilinmeyen araç: kök yok, var değil.</summary>
    public static SourceRoot For(string tool, string? arg, string? setting, IEnumerable<Agents.ConnectorSpec>? specs = null)
    {
        var spec = (specs ?? Agents.AgentRegistry.Specs).FirstOrDefault(x => x.Id.Equals(tool, StringComparison.OrdinalIgnoreCase));
        if (spec is null) return new SourceRoot(tool, null, "kayıt defterinde yok", false);
        var id = spec.Id;
        if (!string.IsNullOrWhiteSpace(arg)) return Make(spec, arg, "argüman");
        if (!string.IsNullOrWhiteSpace(setting)) return Make(spec, setting, "ayar");
        foreach (var name in spec.RootEnv)
        {
            var v = Environment.GetEnvironmentVariable(name);
            var origin = name;
            if (string.IsNullOrWhiteSpace(v) && spec.UserEnv && OperatingSystem.IsWindows())
            {
                v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                origin = name + " (kullanıcı)";
            }
            if (string.IsNullOrWhiteSpace(v)) continue;
            var cand = spec.Subdir is null ? v : Path.Combine(v, spec.Subdir);
            if (spec.EnvNeedsSubdir && !Directory.Exists(cand)) continue;
            return Make(spec, cand, origin);
        }
        var def = spec.DefaultRoot ?? Path.Combine("%USERPROFILE%", "." + id);
        var defPath = Environment.ExpandEnvironmentVariables(def.Replace("%USERPROFILE%", UserProfile, StringComparison.OrdinalIgnoreCase));
        return Make(spec, spec.Subdir is null ? defPath : Path.Combine(defPath, spec.Subdir), def);
    }

    private static SourceRoot Make(Agents.ConnectorSpec spec, string root, string origin)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
        // Kullanıcı üst klasörü (sessions ya da projects olmadan) verdiyse alt klasörü kullan.
        if (spec.Subdir is not null && !full.EndsWith(spec.Subdir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(full, spec.Subdir)))
            full = Path.Combine(full, spec.Subdir);
        return new SourceRoot(spec.Id, full, origin, Directory.Exists(full));
    }
}
