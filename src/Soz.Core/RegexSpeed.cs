using System.Text.RegularExpressions;

namespace Soz.Core;

/// <summary>
/// <c>RegexOptions.Compiled</c> bayrağı: uzun ömürlü süreçte (sunucu, masaüstü) eşleşmeyi hızlandırır, ama ifade başına ~2,6 ms kurulum maliyeti vardır.
/// Kanca her çağrıda yeni bir süreç olduğundan bu maliyet kancanın gecikmesine biner (gerçek veriyle ölçüldü: kt2, user-prompt-submit 282 ms → 193 ms).
/// Kanca süreci <see cref="ShortLived"/> değerini en başta açar; o zaman bayrak konmaz (yorumlayıcı). Eşleşme sonucu iki durumda da aynıdır.
/// Kalıpları derleme zamanında üretmek için <c>[GeneratedRegex]</c> uygun yerlerde kullanılır (TextNormalizer); bu sınıf, kalıbı çalışma anında gelen
/// ya da büyük-küçük harf kültürü seçeneği (IgnoreCase, CultureInvariant olmadan) taşıyan ifadeler içindir: onların kültür davranışı bu yüzden değiştirilmedi.
/// </summary>
public static class RegexSpeed
{
    /// <summary>Kısa ömürlü süreç (kanca): ifadeler derlenmez. Süreç başında bir kez, ifadelerden önce ayarlanır.</summary>
    public static volatile bool ShortLived;

    /// <summary>Kısa ömürlü süreçte <see cref="RegexOptions.None"/>, değilse <see cref="RegexOptions.Compiled"/>.</summary>
    public static RegexOptions Compiled => ShortLived ? RegexOptions.None : RegexOptions.Compiled;
}
