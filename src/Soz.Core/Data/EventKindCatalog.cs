using Microsoft.Data.Sqlite;

namespace Soz.Core.Data;

/// <summary>
/// Bilinen olay türlerinin TEK listesi (event_kind tablosu). Her şerit kendi türlerini ayrı ayrı, ilk kullanımda ya da hiç kaydediyordu (tutarsızdı);
/// şimdi hepsi şema göçünde (v13) tek elden kaydolur ve her yazan yer (<c>RestoreService</c> gibi) aynı listeyi kullanabilir.
/// Yeni olay türü eklemek: buraya bir satır. Kayıt tekrar çalıştırılabilir (INSERT OR IGNORE), var olan satıra dokunulmaz.
/// Ad kuralı: <c>alan.fiil</c> küçük harfli, noktalı. (Eski şeritlerin yazdığı <c>kesif.bitti</c>, <c>kullanici.*</c> ve <c>check.human_ticked</c> adları yayınlanmış olay kayıtlarında geçtiği için değiştirilmedi.)
/// </summary>
public static class EventKindCatalog
{
    public sealed record Kind(string Name, string Owner, string Description);

    public static IReadOnlyList<Kind> All { get; } = Build();

    private static List<Kind> Build()
    {
        var list = new List<Kind>
        {
            new("migration.applied", "soz.core", "Şema göçü tamamlandı (from, to, backup_file, ms)."),
            new("backup.taken", "soz.core", "Göç öncesi yedek alındı ve doğrulandı (file, bytes, from_version)."),
            new("repair.applied", "soz.core", "Eski sürümün bıraktığı izler onarıldı (uids, legacy_events, noise_marked, ms)."),
            new("project.relocated", "soz.core", "Projenin klasör yeri güncellendi (project, old_name, new_name)."),
            new("attention.acked", "soz.attention", "Kişi \"Sıra bende\" listesinde bir öğeyi gördü ve kapattı (itemId, kind, reason)."),
            new("kesif.bitti", "soz.onboarding", "Tanışma keşfi bitti (found, new, updated, unchanged, gone, keptDismissed, dropped, unreadable, ms)."),
            new("kullanici.onay", "soz.onboarding", "Kişi Tanışma'daki bir maddeyi onayladı (id, group). Değer yazılmaz."),
            new("kullanici.duzeltme", "soz.onboarding", "Kişi Tanışma'daki bir maddeyi kendi değeriyle düzeltti (id, group). Değer yazılmaz."),
            new("kullanici.izleme-kapat", "soz.onboarding", "Kişi Tanışma'daki bir maddeye \"bir daha gösterme\" dedi (id, group)."),
            new("kullanici.izleme-ac", "soz.onboarding", "Kişi kapattığı Tanışma maddesini geri getirdi (id, group)."),
            new("check.human_ticked", "soz.proof", "Kişi bir insan onay listesi maddesini işaretledi ya da işaretini kaldırdı (item, ticked, text)."),
            new("registry.approved", "soz.registry", "Kişi kayıt defterinde onay bekleyen bir kaydı onayladı (id, kind, origin, sha256)."),
            new("registry.revoked", "soz.registry", "Kişi bir kaydın onayını geri aldı (id, kind, origin, sha256)."),
        };
        list.AddRange(Restore.RestoreService.EventKinds.Select(k => new Kind(k.Kind, "soz.core", k.Description)));
        list.AddRange(Discovery.DiscoverySchema.EventKinds.Select(k => new Kind(k.Kind, "soz.discovery", k.Description)));
        return list;
    }

    /// <summary>Bütün türleri event_kind tablosuna yazar (varsa dokunmaz).</summary>
    public static void Ensure(SqliteConnection c, SqliteTransaction? tx)
    {
        var now = Time.NowIso();
        foreach (var k in All)
            c.Exec("INSERT OR IGNORE INTO event_kind(kind, owner, schema_v, since, description) VALUES(@k,@o,1,@t,@d)", tx, ("@k", k.Name), ("@o", k.Owner), ("@t", now), ("@d", k.Description));
    }
}
