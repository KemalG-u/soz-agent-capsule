using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Onboarding;

namespace Soz.Core.Registry;

/// <summary>
/// Üretimde kayıt defterinin TEK giriş noktası: gömülü (fixed) kayıtlar + kişi kayıt klasörü (<c>&lt;veri&gt;\yetenek\</c>) + veritabanındaki onaylar.
/// Dosyadan gelen çalıştırılabilir kayıt ve kapalı gelen sabit kayıt (ör. Hermes: çalıştırılabilir ama kapalı gelir) kişi onaylayana kadar öneridir; onay yalnız insan eylemidir
/// (<see cref="Approve"/> ve <see cref="Revoke"/> sunucuda yazan uçlardır, ajana kapalıdır) ve olay defterine yazılır.
/// Tam doğrulamadan geçemeyen gömülü paket <see cref="RegistryUnavailable"/> atar (kişi dosyası bozuksa gömülü kayıtlar yine yüklenir, bozuk dosya sorun listesine düşer).
/// </summary>
public static class RegistryHost
{
    public const string UserDirName = "yetenek";

    /// <summary>Kişi kayıt klasörü (var olmayabilir; yoksa hiçbir şey okunmaz, klasörü kendiliğinden oluşturmayız).</summary>
    public static string UserDir(SozDb db) => Path.Combine(db.DataDir, UserDirName);

    /// <summary>Etkin kayıt defteri: gömülü + kişi klasörü + onaylar. Her çağrıda yeniden okunur (dosya ya da onay değişince hemen yansır).</summary>
    public static RegistryLoadResult Load(SozDb db)
    {
        _ = FixedCatalog.Get; // gömülü paket bozuksa tek sade cümleyle dur
        var dir = UserDir(db);
        return RegistryLoader.LoadEmbedded(Directory.Exists(dir) ? dir : null, new DbRegistryApprovals(db));
    }

    /// <summary>Onay bekleyen bir kayıt mı (kişi onaylayınca etkinleşecek olan)?</summary>
    public static bool AwaitsApproval(RegistryEntry e)
        => e.State == RegistryStates.Suggested && e.Owner != RegistryOwners.System
           && (e.Owner == RegistryOwners.Fixed || RegistryLoader.NeedsApproval(e.Kind, e.Body, e.Permissions));

    /// <summary>Kaydı onaylar veya kapatılmış sabit bağlayıcıyı yeniden açar. Kayıt yoksa 404, onay beklemiyorsa 409 (sade cümleyle). Dönen: onaydan sonraki durum.</summary>
    public static JsonObject Approve(SozDb db, string? id, string actor = "kisi")
    {
        var e = Require(Load(db), id);
        if (e.State == RegistryStates.Enabled) return State(e, changed: false);
        if (e.Owner == RegistryOwners.Fixed && e.Kind == RegistryKinds.Connector && e.State == RegistryStates.Disabled)
        {
            new DbRegistryApprovals(db).SetEnabled(e.Id, true);
            Record(db, "registry.approved", e, actor);
            return State(Require(Load(db), id), changed: true);
        }
        if (!AwaitsApproval(e)) throw new SozException(409, "Bu kayıt onay beklemiyor.");
        new DbRegistryApprovals(db).Approve(e.Id, e.ApprovalSha);
        Record(db, "registry.approved", e, actor);
        return State(Require(Load(db), id), changed: true);
    }

    /// <summary>Onayı geri alır ya da açık gelen sabit bağlayıcıyı kapatır (kayıt silinmez). Zaten kapalıysa hiçbir şey yapmaz.</summary>
    public static JsonObject Revoke(SozDb db, string? id, string actor = "kisi")
    {
        var e = Require(Load(db), id);
        var approvals = new DbRegistryApprovals(db);
        if (approvals.IsApproved(e.Id, e.ApprovalSha))
        {
            approvals.Revoke(e.Id, e.ApprovalSha);
            Record(db, "registry.revoked", e, actor);
            return State(Require(Load(db), id), changed: true);
        }
        if (e.Owner == RegistryOwners.Fixed && e.Kind == RegistryKinds.Connector && e.State == RegistryStates.Enabled)
        {
            approvals.SetEnabled(e.Id, false);
            Record(db, "registry.revoked", e, actor);
            return State(Require(Load(db), id), changed: true);
        }
        return State(e, changed: false);
    }

    private static RegistryEntry Require(RegistryLoadResult load, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new SozException(400, "Hangi kayıt olduğu (id) gerekli.");
        return load.Get(id.Trim()) ?? throw new SozException(404, "Böyle bir kayıt yok.");
    }

    private static JsonObject State(RegistryEntry e, bool changed) => new() { ["id"] = e.Id, ["kind"] = e.Kind, ["state"] = e.State, ["origin"] = e.Origin == "core" ? "core" : "file", ["changed"] = changed };

    private static void Record(SozDb db, string kind, RegistryEntry e, string actor)
    {
        using var c = db.Open();
        Events.Append(c, kind, "registry", null, null, null, actor,
            new JsonObject { ["id"] = e.Id, ["kind"] = e.Kind, ["origin"] = e.Origin == "core" ? "core" : "file", ["sha256"] = e.ApprovalSha }.ToJsonString());
    }
}
