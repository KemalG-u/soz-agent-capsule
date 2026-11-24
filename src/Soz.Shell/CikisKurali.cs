namespace Soz.Shell;

/// <summary>
/// "Söz'ü kapat" seçilince ne olacağının saf kararı (pencere gerekmez, sınanabilir).
/// Söz kapanırsa çalışan işlerin süreçleri durdurulur (iş "durduruldu" olur, kişi sonradan işin sayfasından "Aynı işe yeni bir şey ekle" ile sürdürebilir);
/// bu yüzden çalışan iş varken sormadan kapatmak kişinin işini sessizce keser. Çalışan iş yoksa soru sorulmaz.
/// </summary>
internal static class CikisKurali
{
    public enum Karar { Kapat, Sor, BilinmiyorSor }

    /// <param name="sunucuVar">Söz'ün arka plandaki kısmı bu süreçte ayakta mı (açılışı başarısızsa kaybedilecek iş yoktur).</param>
    /// <param name="calisanIs">Çalışan iş sayısı; sunucuya ulaşılamadıysa null.</param>
    public static Karar Ver(bool sunucuVar, int? calisanIs)
    {
        if (!sunucuVar) return Karar.Kapat;
        if (calisanIs is null) return Karar.BilinmiyorSor;
        return calisanIs.Value > 0 ? Karar.Sor : Karar.Kapat;
    }

    /// <summary>Soru metni: sayıyı söyler, işlere gerçekte ne olacağını söyler.</summary>
    public static string Soru(Karar karar, int calisanIs)
        => karar == Karar.BilinmiyorSor ? Strings.Get("dlg.quit.unknown") : Strings.Get("dlg.quit.text", calisanIs);
}
