namespace Soz.Shell;

internal enum KapsulTuru { Yeni, Eski }

/// <summary>Kapsül süreci beklenmedik biçimde bitince ne yapılacağı.</summary>
internal enum KapsulKarari { Yok, Yeniden, EskiyeDus }

/// <summary>
/// Hangi kapsülün kullanılacağı ve yeni kapsül çökerse ne olacağı. Saf mantık: süreç, saat ve pencere yok, bu yüzden ayrı sınanır.
/// Kural: ayar "eski" değilse yeni kapsül denenir (varsayılan yeni). Çalıştırılabilir dosya yoksa ya da başlamazsa eski kapsül açılır.
/// Yeni kapsül kullanıcı istemeden biterse bir kez yeniden başlatılır, ikincisinde eski kapsüle düşülür (kullanıcı kapsülsüz kalmaz, döngü olmaz).
/// </summary>
internal sealed class KapsulPolitikasi
{
    public const string ExeAdi = "Soz-Kapsul.exe";
    public const string AyarAdi = "capsuleKind";
    public const string YeniDeger = "yeni", EskiDeger = "eski";
    /// <summary>Bir oturumda yeni kapsül için izin verilen yeniden başlatma sayısı.</summary>
    public const int EnCokYenidenBaslatma = 1;

    private int _yeniden;
    private bool _cikisIstendi;

    public int YenidenBaslatma => _yeniden;

    /// <summary>
    /// Ayar değerinden tür. komutSatiri: deneme için komut satırından gelen değer ayarın önüne geçer. Tanınmayan değer varsayılana (yeni) düşer.
    /// </summary>
    public static KapsulTuru Sec(string? komutSatiri, string? ayar)
    {
        var v = (string.IsNullOrWhiteSpace(komutSatiri) ? ayar : komutSatiri)?.Trim().ToLowerInvariant();
        return v == EskiDeger ? KapsulTuru.Eski : KapsulTuru.Yeni;
    }

    /// <summary>Kapsülün çalıştırılabilir dosyasını arar: kabuğun yanında, sonra yanındaki "kapsul" klasöründe. Yoksa null.</summary>
    public static string? ExeBul(string klasor, Func<string, bool>? varMi = null)
    {
        varMi ??= File.Exists;
        foreach (var a in new[] { Path.Combine(klasor, ExeAdi), Path.Combine(klasor, "kapsul", ExeAdi) })
            if (varMi(a)) return a;
        return null;
    }

    /// <summary>Kabuğun tema ayarından (system, light, dark ya da acik, koyu) kapsülün zemin kipi (sistem, acik, koyu).</summary>
    public static string ZeminKipi(string? tema) => tema?.Trim().ToLowerInvariant() switch
    {
        "light" or "acik" => "acik",
        "dark" or "koyu" => "koyu",
        _ => "sistem",
    };

    /// <summary>Kullanıcı kapsülden Çıkış dedi ya da masaüstü programı kapanıyor: bundan sonraki süreç bitişi çökme sayılmaz.</summary>
    public void CikisIstendi() => _cikisIstendi = true;

    /// <summary>Kapsül süreci bitti (standart çıktısı kapandı). Dönüş: ne yapılacak. Yeniden başlatma sayacı burada artar.</summary>
    public KapsulKarari CikistaKarar()
    {
        if (_cikisIstendi) return KapsulKarari.Yok;
        if (_yeniden < EnCokYenidenBaslatma) { _yeniden++; return KapsulKarari.Yeniden; }
        return KapsulKarari.EskiyeDus;
    }

    /// <summary>Günlüğe yazılacak sade cümle.</summary>
    public static string Cumle(KapsulKarari k, int cikisKodu) => k switch
    {
        KapsulKarari.Yeniden => $"yeni kapsül beklenmedik bitti (kod {cikisKodu}), bir kez yeniden başlatılıyor",
        KapsulKarari.EskiyeDus => $"yeni kapsül yine bitti (kod {cikisKodu}), eski kapsül kullanılıyor",
        _ => "yeni kapsül kapandı",
    };
}
