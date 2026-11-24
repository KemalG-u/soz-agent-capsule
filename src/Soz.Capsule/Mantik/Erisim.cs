namespace Soz.Capsule;

/// <summary>
/// Ekran okuyucuya okunan metinler (saf mantık, sınanabilir). Kişi "UYKU", "SOLUK-DURUMLU" gibi iç durum adlarını duymaz: durumu sade cümleyle öğrenir.
/// </summary>
internal static class Erisim
{
    /// <summary>Kapsülün adı: "Söz küçük pencere. 2 iş seni bekliyor. Etkin proje: Söz."</summary>
    public static string Ad(PanelKipi panel, bool giris, Icerik? icerik, int bekleyen, string? proje)
    {
        var s = "Söz küçük pencere.";
        if (giris) s += " Yazı satırı açık.";
        else if (panel == PanelKipi.Tam) s += " Panel açık.";
        else if (panel == PanelKipi.OnBakis) s += " Ayrıntılar açık.";
        if (bekleyen > 0) s += $" {bekleyen} iş seni bekliyor.";
        else s += " " + DurumCumlesi(icerik);
        if (!string.IsNullOrWhiteSpace(proje)) s += $" Etkin proje: {proje.Trim()}.";
        return s;
    }

    private static string DurumCumlesi(Icerik? c)
    {
        if (c == null || string.IsNullOrWhiteSpace(c.Yazi) || c.Yazi == "hazır") return "Seni bekleyen iş yok.";
        var y = c.Yazi.Trim();
        switch (c.Sinif)
        {
            case DurumSinifi.Dikte: return "Sesle yazma: " + y + ".";
            case DurumSinifi.Calisiyor:
                var ilk = y.Split(' ')[0];
                return int.TryParse(ilk, out var n) ? $"{n} iş çalışıyor." : "Bir iş çalışıyor.";
            case DurumSinifi.Belirsiz:
                return y == BaglantiKarar.HapYazisi ? "Bilgiye ulaşamıyorum. Seni bekleyen iş olup olmadığını bilmiyorum." : "Durumu belli olmayan iş var: " + y + ".";
            default: return y.TrimEnd('.') + ".";
        }
    }

    /// <summary>Ekran okuyucu yardım metni. Kısayol yoksa ya da alınamadıysa (boş ya da "yok") kısayoldan söz edilmez; varsa ayardaki gerçek değer yazılır.</summary>
    public static string Yardim(string? kisayol)
    {
        var s = "Tıklayınca paneli açar, çift tıklayınca yazı satırını açar, sağ tık geri alır.";
        if (!string.IsNullOrWhiteSpace(kisayol) && !string.Equals(kisayol.Trim(), KisayolYok, StringComparison.OrdinalIgnoreCase)) s += $" Kısayol: {kisayol.Trim()}.";
        return s + " Tekerlek bekleyen işler arasında gezinir.";
    }

    /// <summary>Masaüstü programı kısayolu kaydedemediyse kapsüle bu değeri verir.</summary>
    public const string KisayolYok = "yok";
}
