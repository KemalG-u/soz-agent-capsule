namespace Soz.Shell;

/// <summary>
/// Güncelleme adresinin nereden geldiği (saf, sınanabilir). Sıra: ortam değişkeni <c>SOZ_UPDATE_URL</c>, sonra <c>shell-tercihleri.json</c>,
/// sonra Söz'ün kendi beslemesi (<see cref="Varsayilan"/>). Tercih dosyasında <c>guncellemeAdresi</c> boş dize ya da
/// <c>guncellemeDenetimi: false</c> ise denetim kapalıdır, ağa hiç çıkılmaz (ortam değişkeni yine de üstündür: geliştirici ve sınama yolu).
/// </summary>
internal static class GuncellemeAdresi
{
    public const string Varsayilan = "https://kokpit.oluk.org/guncelleme/soz";

    /// <summary>Açılıştan sonra ilk sessiz denetime kadar beklenen süre.</summary>
    public static readonly TimeSpan IlkBekleme = TimeSpan.FromSeconds(20);

    /// <summary>Sonraki sessiz denetimlerin aralığı (Windows ile açılıp günlerce açık kalan Söz de yeni sürümü görür).</summary>
    public static readonly TimeSpan Aralik = TimeSpan.FromHours(6);

    /// <returns>Adres null ise denetim yapılmaz. Kapali: kişi tercihle kapattı (menü bunu söyler).</returns>
    public static (string? Adres, bool Kapali) Coz(string? ortamDegiskeni, KabukTercihleri tercih)
    {
        if (!string.IsNullOrWhiteSpace(ortamDegiskeni)) return (ortamDegiskeni.Trim(), false);
        if (tercih.GuncellemeDenetimi == false || tercih.GuncellemeAdresiBos) return (null, true);
        if (!string.IsNullOrWhiteSpace(tercih.GuncellemeAdresi)) return (tercih.GuncellemeAdresi.Trim(), false);
        return (Varsayilan, false);
    }
}

/// <summary>"Yeni sürüm var, şimdi kurulsun mu?" sorusunun metni. Çalışan iş varsa sayısını ve durdurulacağını söyler, yoksa bu cümle çıkmaz.</summary>
internal static class GuncellemeSorusu
{
    /// <param name="calisanIs">Çalışan iş sayısı. null: öğrenilemedi.</param>
    public static string Metin(string yeniSurum, int? calisanIs)
    {
        var bas = Strings.Get("dlg.update.text", yeniSurum);
        var ek = calisanIs switch
        {
            null => Strings.Get("dlg.update.unknown"),
            > 0 => Strings.Get("dlg.update.jobs", calisanIs.Value),
            _ => Strings.Get("dlg.update.nojobs"),
        };
        return bas + "\n\n" + ek;
    }

    /// <summary>İş varsa ya da bilinmiyorsa varsayılan düğme Hayır olur (yanlışlıkla Enter işleri durdurmasın).</summary>
    public static bool HayirVarsayilan(int? calisanIs) => calisanIs is not 0;
}

/// <summary>İndirme kilidi: indirme sürerken ikinci tıklama yeni indirme başlatmaz.</summary>
internal sealed class GuncellemeKilidi
{
    private int _suruyor;
    public bool Suruyor => Volatile.Read(ref _suruyor) == 1;
    /// <summary>Kilit alındıysa true. Zaten indiriliyorsa false.</summary>
    public bool Basla() => Interlocked.CompareExchange(ref _suruyor, 1, 0) == 0;
    public void Bitir() => Interlocked.Exchange(ref _suruyor, 0);
}

/// <summary>Tepsi menüsündeki güncelleme satırı: her zaman görünür, durumu sade söyler.</summary>
internal static class GuncellemeMenusu
{
    public static (string Metin, bool Etkin) Satir(bool kapali, bool indiriliyor, bool denetleniyor, string? yeniSurum)
    {
        if (kapali) return (Strings.Get("menu.update.off"), false);
        if (indiriliyor) return (Strings.Get("menu.update.downloading"), false);
        if (yeniSurum != null) return (Strings.Get("menu.update.available", yeniSurum), true);
        if (denetleniyor) return (Strings.Get("menu.update.checking"), false);
        return (Strings.Get("menu.update.check"), true);
    }
}

/// <summary>Açılış hatasının nedeni: veri Söz'ün daha yeni bir sürümüyle mi yazılmış (eski sürüm veriye dokunmadan durur).</summary>
internal static class AcilisHatasi
{
    /// <summary><c>Soz.Core.Data.SozSchemaTooNewException</c> zincirin herhangi bir yerinde mi. Ad ile bakılır, bu dosya Core'a bağlı kalmasın diye (Core sınaması adı korur).</summary>
    public const string SemaYeniTuru = "Soz.Core.Data.SozSchemaTooNewException";

    public static bool SemaYeni(Exception? ex)
    {
        var bakilan = 0;
        var kuyruk = new Queue<Exception>();
        if (ex != null) kuyruk.Enqueue(ex);
        while (kuyruk.Count > 0 && bakilan++ < 32)
        {
            var e = kuyruk.Dequeue();
            if (e.GetType().FullName == SemaYeniTuru) return true;
            if (e is AggregateException ag) foreach (var i in ag.InnerExceptions) kuyruk.Enqueue(i);
            else if (e.InnerException != null) kuyruk.Enqueue(e.InnerException);
        }
        return false;
    }
}
