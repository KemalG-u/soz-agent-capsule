using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Soz.Core.Agents;
using Soz.Core.Data;

namespace Soz.Core.Readers;

/// <summary>
/// Bir kayıt okuyucusunun künyesi. Kayıt defterindeki `history.reader` adı buraya çözülür.
/// FilePattern: dosya tabanlı okuyucuların aradığı dosya deseni (tanı ekranı için); veritabanı tabanlılarda null.
/// SubagentFolders: alt oturumlar ayrı klasörde (subagents) duruyor mu (tanı sayacı için).
/// </summary>
public sealed record ReaderDef(string Id, Func<ITranscriptReader> Create, string? FilePattern = null, bool SubagentFolders = false)
{
    public bool IsFileBased => FilePattern is not null;
}

/// <summary>Okuyucu adı → okuyucu. Yeni araç: bir okuyucu sınıfı + bir kayıt dosyası; çekirdekte başka satır yok.</summary>
public static class ReaderCatalog
{
    private static readonly ConcurrentDictionary<string, ReaderDef> Defs = new(StringComparer.Ordinal);

    static ReaderCatalog()
    {
        Register(new ReaderDef("claude-jsonl", () => new ClaudeCodeReader(), "*.jsonl", SubagentFolders: true));
        Register(new ReaderDef("codex-rollout", () => new CodexReader(), "rollout-*.jsonl"));
        Register(new ReaderDef("hermes-state", () => new HermesStateReader()));
        Register(new ReaderDef("opencode-db", () => new OpenCodeDbReader()));
        Register(new ReaderDef("antigravity-db", () => new AntigravityDbReader()));
    }

    public static void Register(ReaderDef def) => Defs[def.Id] = def;
    public static ReaderDef? Find(string? id) => id is not null && Defs.TryGetValue(id, out var d) ? d : null;
}

/// <summary>Bir bağlayıcının oturum kayıtları için okuma sonucu (kayıt, okuyucu adı, rapor ya da sade sorun cümlesi).</summary>
public sealed record ObserveResult(string Tool, string? ReaderId, ReadReport? Report, string? Problem);

/// <summary>
/// Kayıt defterindeki gözle bağlayıcılarını okur. KENDİLİĞİNDEN hiçbir araca bakmaz: yalnız çağıranın adını verdiği araçlar okunur
/// (değer null ise kök kayıttaki kurallarla çözülür). Hiçbir şey yazılmaz: yalnız Söz'ün kendi veritabanına oturum satırı eklenir.
/// </summary>
public static class ObserveRunner
{
    public static List<ObserveResult> Run(SozDb db, IReadOnlyDictionary<string, string?> tools, ReadOptions? options = null, IEnumerable<ConnectorSpec>? specs = null)
    {
        var all = (specs ?? AgentRegistry.Specs).ToList();
        var store = new IngestStore(db);
        var ro = options ?? new ReadOptions();
        var results = new List<ObserveResult>();
        foreach (var (tool, explicitRoot) in tools)
        {
            var spec = all.FirstOrDefault(s => s.Id.Equals(tool, StringComparison.OrdinalIgnoreCase));
            if (spec is null) { results.Add(new ObserveResult(tool, null, null, $"\"{tool}\" kayıt defterinde yok.")); continue; }
            if (!spec.CanObserve) { results.Add(new ObserveResult(spec.Id, spec.Reader, null, $"{spec.Title} yalnız gözlenemez: kayıtta okunacak kayıt yok.")); continue; }
            var def = ReaderCatalog.Find(spec.Reader);
            if (def is null) { results.Add(new ObserveResult(spec.Id, spec.Reader, null, $"{spec.Title} için \"{spec.Reader}\" okuyucusu bu Söz sürümünde yok.")); continue; }
            var root = SourceLocator.For(spec.Id, explicitRoot, null, all);
            if (!root.Exists || root.Root is null)
            {
                results.Add(new ObserveResult(spec.Id, def.Id, new ReadReport { Tool = spec.SessionTool, Root = root.Root ?? "", Found = false }, null));
                continue;
            }
            try { results.Add(new ObserveResult(spec.Id, def.Id, def.Create().Read(store, root.Root, ro), null)); }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            { results.Add(new ObserveResult(spec.Id, def.Id, null, $"{spec.Title} okunamadı ({e.GetType().Name}). Öteki araçlar etkilenmedi.")); }
        }
        return results;
    }
}
