namespace Soz.Capsule;

/// <summary>Pencere katmanının kararlarından saf olanlar (pencere ve sistem çağrısı yok): testte doğrudan çağrılır.</summary>
internal static class PencereKurallari
{
    /// <summary>
    /// Öndeki pencere bu sınıftaysa ekranı kaplasa bile "tam ekran uygulama" sayılmaz: masaüstü ve görev çubuğu ile görev görünümü / Alt+Tab pencereleri
    /// (Windows 10: MultitaskingViewFrame, TaskSwitcherWnd, ForegroundStaging; Windows 11: XamlExplorerHostIslandWindow). Aksi halde bunlar açılınca kapsül kaybolurdu.
    /// </summary>
    public static bool TamEkranSayilmazSinif(string sinif) => sinif is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
        or "MultitaskingViewFrame" or "TaskSwitcherWnd" or "ForegroundStaging" or "XamlExplorerHostIslandWindow";

    /// <summary>En üstte kalma sırasını ne zaman yenilemeli: son yenilemeden beri aralık geçtiyse ve kapsülün kendi panel/ön bakış/menü yüzeyi açık değilse (onları kapsülün altına itmemek için).</summary>
    public static bool UstteKalmaYenilensin(long simdi, long sonYenileme, long aralikMs, bool yuzeyAcik) => !yuzeyAcik && simdi - sonYenileme >= aralikMs;
}
