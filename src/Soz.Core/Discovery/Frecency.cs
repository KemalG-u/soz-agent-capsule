namespace Soz.Core.Discovery;

/// <summary>
/// Yarılanmalı kullanım ağırlığı (frecency) ve öneri puanı: saf fonksiyonlar, veritabanı yok.
/// Formül repo-4 bölüm 2.2 (düzeltilmiş) ve derin-3 bölüm 3c: <c>puan = Fw / (Fw + K) x U x (1 - C)</c>, <c>Fw = Fg + ağırlıkP x Fc + ağırlıkZ x Fs</c>.
/// Tüm sayılar (<see cref="DiscoveryParams"/>) kayıt defterinden gelir. Hiçbir sayı ölçülmedi: gölge kip bu yüzden vardır.
/// </summary>
public static class Frecency
{
    /// <summary>Bir kullanımın yaşa göre ağırlığı: 0,5^(yaş / yarılanma). Gelecek tarihli (negatif yaş) kullanım 1 sayılır.</summary>
    public static double Decay(double ageDays, double halfLifeDays) => Math.Pow(0.5, Math.Max(0, ageDays) / halfLifeDays);

    /// <summary>Olay listesinden toplam: F = Σ decay(yaş). Yalnız sınama ve karşılaştırma içindir; üretimde artımlı saklama kullanılır.</summary>
    public static double FromAges(IEnumerable<double> agesDays, double halfLifeDays) => agesDays.Sum(a => Decay(a, halfLifeDays));

    /// <summary>O(1) güncelleme: F_yeni = F_eski x decay(tŞimdi - tSon) + 1. İki sayı (F, tSon) olay listesinin yerini tutar.</summary>
    public static double Accumulate(double f, double tLast, double tNow, double halfLifeDays) => Current(f, tLast, tNow, halfLifeDays) + 1;

    /// <summary>Saklanan (F, tSon) değerinin tŞimdi anındaki değeri.</summary>
    public static double Current(double f, double tLast, double tNow, double halfLifeDays) => f * Decay(tNow - tLast, halfLifeDays);

    /// <summary>Doyma: Fw / (Fw + K), 0 ile 1 arası.</summary>
    public static double Saturate(double fw, double saturation) => fw <= 0 ? 0 : fw / (fw + saturation);

    /// <summary>Bileşik ağırlık Fw.</summary>
    public static double Combined(double fg, double fc, double fs, DiscoveryParams p) => fg + p.ProjectWeight * fc + p.TimeWeight * fs;

    /// <summary>
    /// Öneri puanı. <paramref name="u"/>: ulaşılabilirlik çarpanı (1 ya da yuvadaysa InSlotFactor), <paramref name="c"/>: yok sayma cezası 0..1.
    /// </summary>
    public static double Score(double fg, double fc, double fs, double u, double c, DiscoveryParams p)
        => Saturate(Combined(fg, fc, fs, p), p.Saturation) * u * (1 - Math.Clamp(c, 0, 1));

    /// <summary>Eski (D3) formülün F^ değeri: F / (F + K). repo-4 karşılaştırma tablosunun "D3 F^" sütunu.</summary>
    public static double D3Hat(double f, double saturation) => Saturate(f, saturation);
}
