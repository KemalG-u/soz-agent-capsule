namespace Soz.Shell;

/// <summary>
/// "Yeni sürüm var" balonunun ne zaman çıkacağı. Aynı sürüm için balon bir kez çıkar; kişi menüden elle denetlediyse
/// balon çıkmaz (zaten sonucu görüyor). Duman sınamasında hiç çıkmaz.
/// </summary>
internal sealed class GuncellemeBalonu
{
    private string? _duyurulan;

    public bool Gostermeli(string? yeniSurum, bool elleDenetim, bool duman)
    {
        if (string.IsNullOrWhiteSpace(yeniSurum)) return false;
        var ilkKez = !string.Equals(_duyurulan, yeniSurum, StringComparison.Ordinal);
        _duyurulan = yeniSurum;
        return ilkKez && !elleDenetim && !duman;
    }
}

/// <summary>Windows açılışında başlatma kaydını değiştirir ve sonucu dürüstçe söyler: kayıt yazılamadıysa Basarili false, işaret eski hâlinde kalır.</summary>
internal static class AcilisKaydi
{
    internal readonly record struct Sonuc(bool Basarili, bool YeniDeger);

    public static Sonuc Degistir(bool simdiAcik, Func<bool> ac, Action kapat, Func<bool> acikMi)
    {
        if (simdiAcik)
        {
            kapat();
            return acikMi() ? new Sonuc(false, true) : new Sonuc(true, false);
        }
        return ac() ? new Sonuc(true, true) : new Sonuc(false, false);
    }
}

/// <summary>
/// Tepsi ipucundaki sayılar küçük pencerenin kullandığı kaynaktan (GET api/attention) gelir: küçük pencere nasıl sayıyorsa tepsi de öyle sayar.
/// Bekleyen: soru, karar, kalan iş, biten iş (kişinin sırası gelenler). Durumu belli olmayan: emin olunmayan oturumlar.
/// </summary>
internal static class DikkatSayaci
{
    public static (int Bekleyen, int Belirsiz) Say(IReadOnlyList<AttentionItemInfo> ogeler)
    {
        var bekleyen = 0; var belirsiz = 0;
        foreach (var o in ogeler)
        {
            switch (o.Kind)
            {
                case "ask": case "accept": case "blocked": case "done": bekleyen++; break;
                default: belirsiz++; break;   // "uncertain" ve bilinmeyen tür: küçük pencere de bunları belirsiz sayar
            }
        }
        return (bekleyen, belirsiz);
    }
}
