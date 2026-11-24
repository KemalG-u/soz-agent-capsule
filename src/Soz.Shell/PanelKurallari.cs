namespace Soz.Shell;

/// <summary>
/// Panelin sağ tık menüsü: Edge'in tam menüsü (Geri, Yenile, Farklı kaydet, Yazdır, İncele) yerine yalnız metin işleri kalır
/// (Kopyala, Kes, Yapıştır, Tümünü seç). Yazı kutusunda yazım önerileri de kalır. Saf: WebView2 öğe adları üzerinden karar verir.
/// </summary>
internal static class BaglamMenusu
{
    private static readonly HashSet<string> Izinli = new(StringComparer.OrdinalIgnoreCase) { "copy", "cut", "paste", "selectAll" };

    /// <summary>Tek öğe kalsın mı. Ayraçlar ayrıca <see cref="Suz"/> içinde düzenlenir.</summary>
    public static bool Kalsin(string? ad, bool duzenlenebilir)
    {
        if (string.IsNullOrEmpty(ad)) return false;
        if (Izinli.Contains(ad)) return true;
        // yazım önerileri ve sözlüğe ekleme yalnız yazı kutusunda (WebView2 bunlara "spellCheck..." biçiminde ad verir)
        return duzenlenebilir && (ad.Contains("spell", StringComparison.OrdinalIgnoreCase) || ad.Contains("dictionary", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Kalacak öğelerin sırası (sıfırdan sayı). Başta, sonda ve art arda gelen ayraçlar atılır.</summary>
    public static List<int> Suz(IReadOnlyList<(string? Ad, bool Ayirici)> ogeler, bool duzenlenebilir)
    {
        var sonuc = new List<int>();
        var bekleyenAyirici = -1;
        for (var i = 0; i < ogeler.Count; i++)
        {
            var (ad, ayirici) = ogeler[i];
            if (ayirici) { if (sonuc.Count > 0) bekleyenAyirici = i; continue; }
            if (!Kalsin(ad, duzenlenebilir)) continue;
            if (bekleyenAyirici >= 0) { sonuc.Add(bekleyenAyirici); bekleyenAyirici = -1; }
            sonuc.Add(i);
        }
        return sonuc;
    }
}

/// <summary>Geliştirici araçları (F12, İncele) yalnız <c>SOZ_DEVTOOLS=1</c> ile açılır.</summary>
internal static class GelistiriciAraclari
{
    public const string OrtamAdi = "SOZ_DEVTOOLS";
    public static bool Acik(string? deger) => deger?.Trim() == "1";
}

/// <summary>
/// Panelin ekran süreci (WebView2) ölünce ne yapılacağı. İlk çöküşte bir kez kendiliğinden toparlanır (sayfa ölmüşse yeniden yükle,
/// tarayıcı süreci ölmüşse ekranı yeniden kur). Kısa süre içinde yine çökerse beyaz sayfa yerine sade bir mesaj ve "Yeniden dene" gösterilir.
/// </summary>
internal sealed class WebViewKurtarma
{
    internal enum Tur { TarayiciKapandi, SayfaKapandi, SayfaYanitVermiyor, Diger }
    internal enum Karar { Yoksay, Yenile, YenidenKur, MesajGoster }

    /// <summary>Bu süre içinde gelen ikinci çöküş "kendiliğinden düzelmedi" sayılır.</summary>
    public static readonly TimeSpan Pencere = TimeSpan.FromMinutes(2);

    private DateTime? _sonOtomatik;

    public Karar Ver(Tur tur, DateTime simdi)
    {
        if (tur == Tur.Diger) return Karar.Yoksay;   // GPU, yardımcı ya da alt çerçeve süreci: WebView2 kendisi toparlar
        if (_sonOtomatik is { } t && simdi - t < Pencere) return Karar.MesajGoster;
        _sonOtomatik = simdi;
        return tur == Tur.TarayiciKapandi ? Karar.YenidenKur : Karar.Yenile;
    }

    /// <summary>Kişi "Yeniden dene" dedi: sayaç sıfırlanır, sonraki çöküş yine bir kez kendiliğinden toparlanır.</summary>
    public void Sifirla() => _sonOtomatik = null;
}
