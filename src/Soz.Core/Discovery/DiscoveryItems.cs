using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Registry;

namespace Soz.Core.Discovery;

/// <summary>Aranabilir nesne kaynağı. Yeni nesne türü eklemek = yeni bir kaynak yazmak; arama ve öneri kodu değişmez.</summary>
public interface IDiscoveryItemSource
{
    IEnumerable<DiscoveryItem> Items(SqliteConnection c);
}

/// <summary>Kayıt defterindeki yetenekler (ayarlar, bağlayıcılar, ölçütler...). Söz'ün kendisi: kuzeybatı yönü. Kullanım dışı kayıtlar listelenmez.</summary>
public sealed class RegistryItemSource : IDiscoveryItemSource
{
    private readonly RegistryLoadResult _catalog;
    public RegistryItemSource(RegistryLoadResult catalog) => _catalog = catalog;

    public IEnumerable<DiscoveryItem> Items(SqliteConnection c)
        => _catalog.Entries.Where(e => e.State != RegistryStates.Deprecated && e.State != RegistryStates.Broken)
            .Select(e => DiscoveryItem.Of(e.Id, Directions.ByCode("soz")!.Dir, e.Title.Tr, e.Description?.Tr, null, e.Keywords));
}

/// <summary>Projeler (kimlik: projenin uid'si). Yol ve içerik aranmaz; yalnız ad.</summary>
public sealed class ProjectItemSource : IDiscoveryItemSource
{
    public IEnumerable<DiscoveryItem> Items(SqliteConnection c)
        => c.Query("SELECT uid, COALESCE(NULLIF(name,''), key) FROM projects WHERE uid IS NOT NULL AND key<>'*'", r => (r.GetString(0), r.GetString(1)))
            .Select(x => DiscoveryItem.Of(x.Item1, Directions.ByCode("project")!.Dir, x.Item2));
}

/// <summary>Bağlam sinyali: şimdi hangi proje etkin. Ön plan penceresi okuma bu işte YOK; yalnız Söz'ün kendi verisinden etkin proje.</summary>
public interface IContextSource
{
    string? ActiveProjectUid(SqliteConnection c);
}

/// <summary>Etkin proje = en son oturumu olan proje (Genel hariç). Kişi açıkça proje verirse o kazanır (çağıran karar verir).</summary>
public sealed class LatestSessionContext : IContextSource
{
    public string? ActiveProjectUid(SqliteConnection c)
        => c.Scalar("""
            SELECT p.uid FROM sessions s JOIN projects p ON p.id=s.project_id
            WHERE p.uid IS NOT NULL AND p.key<>'*' AND s.last_at IS NOT NULL ORDER BY s.last_at DESC LIMIT 1
            """) as string;
}
