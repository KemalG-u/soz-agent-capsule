namespace Soz.Core.Data;

/// <summary>
/// Kararlı kimlik (uid): UUIDv7, küçük harf, tireli metin. Sayı kimlikler içeride kalır; uid dışarıya (API, dışa aktarma) ve olay kaydına gider.
/// UUIDv7 zaman damgası taşır, bu yüzden kişiye gönderilecek kimliklerde ele verdiği zamanı unutma.
/// Sıralama için KULLANMA: aynı milisaniyede üretilen kimlikler sıralı değildir. Olay sırası için `seq` kullan; kimlik yalnız tekil addır.
/// </summary>
public static class Uid
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    /// <summary>Şimdiki ana göre yeni kimlik (yeni satırlar, ekleme anında).</summary>
    public static string New() => Guid.CreateVersion7().ToString("D");

    /// <summary>Verilen zamana göre kimlik (göçte eski satırlar için). Zaman okunamazsa ya da 1970 öncesiyse şimdiki an ya da 1970 kullanılır.</summary>
    public static string At(string? isoTime)
    {
        var t = Time.Parse(isoTime) ?? Time.UtcNow();
        var ts = new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc));
        if (ts < Epoch) ts = Epoch;
        return Guid.CreateVersion7(ts).ToString("D");
    }
}
