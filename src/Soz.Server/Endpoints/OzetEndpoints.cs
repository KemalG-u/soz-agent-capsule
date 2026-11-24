using Soz.Core;
using Soz.Core.Sessions.Ozet;

namespace Soz.Server.Endpoints;

/// <summary>
/// Oturum özeti (dil modeli yok, yalnız kayıttaki araç olayları):
///   GET /api/sessions/{id}/ozet                       → Söz oturum kimliğiyle (GET /api/sessions öğelerindeki id)
///   GET /api/sessions/ozet?tool=claude&amp;sessionId=… → yardımcının kendi oturum kimliğiyle (GET /api/profile/sessions öğeleri)
/// Gövde: <see cref="OturumOzeti"/> (neYapti, denediMi, sendenBeklenen, degisenDosyaSayisi, dosyaAdlari, komutSayisi, sonDeneme, bittiDedi,
/// bittiSonrasiDeneme, sonDegisiklikSonrasiDeneme, bekleyen, bekleyenKesin, guven, guvenNedeni, kaynak, hesaplandi). Oturum yoksa 404.
/// Sohbet metni gövdeye girmez; dosya adı tam yol değildir; deneme komutu yalnız kural adıyla ("npm test") görünür.
/// </summary>
public static class OzetEndpoints
{
    public static RouteGroupBuilder MapOzet(this RouteGroupBuilder api, SozService service)
    {
        api.MapGet("/sessions/{id:long}/ozet", async (long id) =>
        {
            var o = await Task.Run(() => { using var c = service.Db.Open(); return OturumOzetleri.Getir(c, id); });
            return o is null ? Results.Json(new { error = $"Çalışma bulunamadı: {id}" }, statusCode: 404) : Results.Json(o);
        });
        api.MapGet("/sessions/ozet", async (string? tool, string? sessionId) =>
        {
            if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(sessionId))
                return Results.Json(new { error = "tool ve sessionId gerekli." }, statusCode: 400);
            var o = await Task.Run(() => { using var c = service.Db.Open(); return OturumOzetleri.GetirDisKimlik(c, tool.Trim().ToLowerInvariant(), sessionId.Trim()); });
            return o is null ? Results.Json(new { error = "Çalışma bulunamadı." }, statusCode: 404) : Results.Json(o);
        });
        return api;
    }

    /// <summary>GET /api/sessions listesine kısa özet ekler: hazır olanlar hemen (en çok ~300 ms), kalanlar arka planda hazırlanır ve alan null kalır.</summary>
    public static List<SessionDto> KisaOzetEkle(SozService service, List<SessionDto> list)
    {
        if (list.Count == 0) return list;
        try
        {
            using var c = service.Db.Open();
            var kisa = OturumOzetleri.Kisalar(c, list.Select(x => x.Id), TimeSpan.FromMilliseconds(300));
            foreach (var s in list) if (kisa.TryGetValue(s.Id, out var k)) s.Ozet = k;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { /* özet isteğe bağlıdır: liste özetsiz döner */ }
        return list;
    }
}
