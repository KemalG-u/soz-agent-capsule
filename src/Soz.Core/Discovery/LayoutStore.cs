using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Discovery;

public enum SlotSource { Fixed, User }

public sealed record SlotItem(SlotAddress At, string ObjectUid, SlotSource Source, string PlacedAt);

/// <summary>Yerleştirme sonucu: Ok; boş olmayan yuvaya `replace` olmadan yazılmak istendi (Conflict); adres ya da kimlik geçersiz (Invalid).</summary>
public sealed record PlaceResult(bool Ok, string? Error, SlotItem? Placed, SlotItem? Occupant)
{
    public static PlaceResult Success(SlotItem placed, SlotItem? replaced) => new(true, null, placed, replaced);
    public static PlaceResult Conflict(SlotItem occupant) => new(false, "Bu yuva dolu. Yerine koymak için açıkça \"değiştir\" denmeli.", null, occupant);
    public static PlaceResult Invalid(string why) => new(false, why, null, null);
}

/// <summary>
/// Sabit yerleşim: 8 yön x 8 yuva = 64 yuva. Yuvaya YALNIZ kişi koyar ve YALNIZ kişi boşaltır; sistem hiçbir şeyi kendiliğinden yerleştirmez,
/// taşımaz, sıkıştırmaz, boşluk doldurmaz (ilke 1: "Windows gibi: herkes neyi nerede bulacağını biliyor").
/// `discovery_slot` tablosuna yazan TEK yer burasıdır. Her yazma kapıyı (<c>discovery_guard</c>) kendi işleminin içinde açıp kapatır.
/// Bu sınıftaki yazma yöntemleri yalnız kişinin açık eylemi için çağrılır: öneri, sayaç, arama ve ölçüm kodu bu sınıfı yazmak için kullanmaz.
/// Yuva doluysa yerine koymak için `replace: true` gerekir; "hangisinin yerine" sorusu kişiye bırakılır. Aynı nesne birden çok yuvada durabilir.
/// </summary>
public static class LayoutStore
{
    private const string Cols = "kind_dir, slot_dir, object_uid, source, placed_at";

    public static IReadOnlyList<SlotItem> All(SqliteConnection c, SqliteTransaction? tx = null)
        => c.Query($"SELECT {Cols} FROM discovery_slot ORDER BY kind_dir, slot_dir", Map, tx);

    public static SlotItem? Get(SqliteConnection c, SlotAddress at, SqliteTransaction? tx = null)
        => c.Query($"SELECT {Cols} FROM discovery_slot WHERE kind_dir=@k AND slot_dir=@s", Map, tx, ("@k", at.KindDir), ("@s", at.SlotDir)).FirstOrDefault();

    /// <summary>Nesne bir yuvada duruyor mu (öneri puanında "zaten bir hareket uzağında" kararı için).</summary>
    public static bool IsPlaced(SqliteConnection c, string objectUid, SqliteTransaction? tx = null)
        => c.Scalar("SELECT 1 FROM discovery_slot WHERE object_uid=@o LIMIT 1", tx, ("@o", objectUid)) is not null;

    public static IReadOnlySet<string> PlacedUids(SqliteConnection c, SqliteTransaction? tx = null)
        => c.Query("SELECT DISTINCT object_uid FROM discovery_slot", r => r.GetString(0), tx).ToHashSet(StringComparer.Ordinal);

    private static SlotItem Map(SqliteDataReader r)
        => new(new SlotAddress((int)r.GetInt64(0), (int)r.GetInt64(1)), r.GetString(2), r.GetString(3) == "fixed" ? SlotSource.Fixed : SlotSource.User, r.GetString(4));

    /// <summary>Kişi bir yuvaya nesne koyar. Yuva doluysa <paramref name="replace"/> olmadan hiçbir şey değişmez.</summary>
    public static PlaceResult Place(SqliteConnection c, SlotAddress at, string objectUid, bool replace = false, SlotSource source = SlotSource.User)
    {
        if (!at.IsValid) return PlaceResult.Invalid("Yön 0 ile 7 arasında olmalı (tür yönü ve yuva yönü).");
        if (string.IsNullOrWhiteSpace(objectUid) || objectUid.Length > 200) return PlaceResult.Invalid("Nesne kimliği boş ya da çok uzun.");
        return Guarded(c, tx =>
        {
            var old = Get(c, at, tx);
            if (old is not null && !replace) return PlaceResult.Conflict(old);
            var now = Time.NowIso();
            c.Exec("INSERT INTO discovery_slot(kind_dir, slot_dir, object_uid, source, placed_at) VALUES(@k,@s,@o,@src,@t) " +
                   "ON CONFLICT(kind_dir, slot_dir) DO UPDATE SET object_uid=excluded.object_uid, source=excluded.source, placed_at=excluded.placed_at",
                tx, ("@k", at.KindDir), ("@s", at.SlotDir), ("@o", objectUid), ("@src", source == SlotSource.Fixed ? "fixed" : "user"), ("@t", now));
            Events.Append(c, "discovery.slot.placed", "discovery_slot", null, null, null, "user",
                new JsonObject { ["kind_dir"] = at.KindDir, ["slot_dir"] = at.SlotDir, ["object_uid"] = objectUid, ["replaced"] = old?.ObjectUid }.ToJsonString(), tx);
            return PlaceResult.Success(new SlotItem(at, objectUid, source, now), old);
        });
    }

    /// <summary>Kişi bir yuvayı boşaltır. Komşu yuvalar yer değiştirmez (boşluk kapatılmaz). Yuva zaten boşsa false.</summary>
    public static bool Clear(SqliteConnection c, SlotAddress at)
    {
        if (!at.IsValid) return false;
        return Guarded(c, tx =>
        {
            var old = Get(c, at, tx);
            if (old is null) return false;
            c.Exec("DELETE FROM discovery_slot WHERE kind_dir=@k AND slot_dir=@s", tx, ("@k", at.KindDir), ("@s", at.SlotDir));
            Events.Append(c, "discovery.slot.cleared", "discovery_slot", null, null, null, "user",
                new JsonObject { ["kind_dir"] = at.KindDir, ["slot_dir"] = at.SlotDir, ["object_uid"] = old.ObjectUid }.ToJsonString(), tx);
            return true;
        });
    }

    /// <summary>Kapıyı işlemin içinde açar, işi yapar, kapıyı kapatır; işlem tek parçadır (hata olursa hem iş hem kapı geri alınır).</summary>
    private static T Guarded<T>(SqliteConnection c, Func<SqliteTransaction, T> body)
    {
        using var tx = c.BeginTransaction(deferred: false);
        c.Exec("UPDATE discovery_guard SET open=1 WHERE id=1", tx);
        var r = body(tx);
        c.Exec("UPDATE discovery_guard SET open=0 WHERE id=1", tx);
        tx.Commit();
        return r;
    }
}
