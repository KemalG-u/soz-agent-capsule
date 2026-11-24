namespace Soz.Shell;

/// <summary>Panelin neden açıldığı. Odak almasına yalnız kullanıcının kendi eylemi izin verir.</summary>
internal enum PanelAcilisi
{
    /// <summary>Kullanıcı tıkladı ya da tuşa bastı (kapsül, tepsi, menü, kısayol, bildirim balonuna tıklama): panel öne gelir ve odak alır.</summary>
    Kullanici,
    /// <summary>Kullanıcı Söz'ü bir daha başlattı: panel görünsün istiyor. Dikte sürerken odak çalınmaz.</summary>
    IkinciOrnek,
    /// <summary>Program açılırken kendiliğinden (kullanıcı tıklamasından saniyeler sonra): odak almaz.</summary>
    Acilis,
    /// <summary>Ölçüm ve deneme düzeni: odak almaz.</summary>
    Olcum,
}

/// <summary>
/// Odak kuralı (saf, sınanabilir): Söz yalnız kullanıcı açıkça istediğinde pencereyi öne alır ve odak alır. Kendiliğinden açılan ya da
/// dikte sürerken tetiklenen hiçbir pencere odak almaz. Dikte (KarıncaAI) dinlerken ya da çevirirken odağın değişmesi yazıyı yanlış pencereye düşürür.
/// </summary>
internal static class OdakPolitikasi
{
    public static bool OdakAlsin(PanelAcilisi neden, bool dikteSuruyor) => neden switch
    {
        PanelAcilisi.Kullanici => true,
        PanelAcilisi.IkinciOrnek => !dikteSuruyor,
        _ => false,
    };

    /// <summary>"Yeni iş" penceresinin odak alması dikte bitene kadar en çok bu kadar ertelenir (dikte takılırsa pencere sonsuza dek odaksız kalmasın).</summary>
    public static readonly TimeSpan YeniIsEnCokBekleme = TimeSpan.FromSeconds(15);

    /// <summary>
    /// "Yeni iş" tıklandığında panelin odak alıp almayacağı. Dikte durumu OKUNABİLİYOR ve "sürüyor" (dinliyor ya da çeviriyor) ise ve ilk istekten bu yana <see cref="YeniIsEnCokBekleme"/> dolmadıysa
    /// odak ertelenir (dikte metni yanlış pencereye düşmesin ya da kaybolmasın). Durum okunamıyorsa (eski KarıncaAI, köprü yok: <see cref="DictationHostStatus.Unavailable"/>), dikte sürmüyorsa,
    /// süre dolduysa ya da süre anlamsızsa (negatif) odak hemen alınır: bugünkü davranış.
    /// </summary>
    public static YeniIsOdak YeniIsKarari(DictationHostStatus durum, TimeSpan beklenen)
        => DikteSuruyor(durum) && beklenen >= TimeSpan.Zero && beklenen < YeniIsEnCokBekleme ? YeniIsOdak.Ertele : YeniIsOdak.Simdi;

    /// <summary>Dikte köprüsünün bildirdiği durum: dinliyor ya da çeviriyor ise dikte sürüyor demektir.</summary>
    public static bool DikteSuruyor(DictationHostStatus durum) => durum is DictationHostStatus.Recording or DictationHostStatus.Processing;
}

/// <summary>"Yeni iş" penceresinin odak kararı: hemen al ya da dikte bitene kadar ertele.</summary>
internal enum YeniIsOdak { Simdi, Ertele }
