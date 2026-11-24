using System.Runtime.InteropServices;

namespace Soz.Shell;

/// <summary>
/// Windows Terminal gibi sekmeli pencerelerde sekmeleri okumak ve birini seçmek için en küçük UI Automation sarmalayıcısı.
/// Yalnız gereken COM yöntemleri tanımlıdır (sıra Windows'un UIAutomationClient.h başlığıyla aynıdır); ek bir kitaplık gerektirmez.
/// Her işlev hata durumunda istisna atmaz, boş ya da false döner: sekme seçilemezse çağıran pencereyi yine öne getirir.
/// </summary>
internal static class UiaSekme
{
    internal sealed record Sekme(string Ad, int Sira, bool Secili, object? Oge);

    internal static string? SonHata;
    private const int TreeScopeDescendants = 4;
    private const int PropName = 30005;
    private const int PropControlType = 30003;
    private const int ControlTypeTabItem = 50019;
    private const int PatternSelectionItem = 10010;
    private const int PropSelectionItemIsSelected = 30079;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker(); void get_ControlViewWalker();
        void get_ContentViewWalker(); void get_RawViewWalker(); void get_RawViewCondition(); void get_ControlViewCondition();
        void get_ContentViewCondition(); void CreateCacheRequest();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationCondition CreateTrueCondition();
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx(); void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationSelectionItemPattern { void Select(); }

    /// <summary>Pencerenin içindeki sekme öğeleri (sırayla). Sekme yoksa ya da erişilemezse boş liste.</summary>
    internal static List<Sekme> Sekmeler(IntPtr pencere)
    {
        var sonuc = new List<Sekme>();
        try
        {
            var ui = (IUIAutomation)new CUIAutomation();
            var kok = ui.ElementFromHandle(pencere);
            var tumu = kok.FindAll(TreeScopeDescendants, ui.CreateTrueCondition());
            var n = tumu.Length;
            var sira = 0;
            for (var i = 0; i < n; i++)
            {
                var e = tumu.GetElement(i);
                try
                {
                    if (e.GetCurrentPropertyValue(PropControlType) is not int tur || tur != ControlTypeTabItem) continue;
                    var ad = e.GetCurrentPropertyValue(PropName) as string ?? "";
                    var secili = false;
                    try { secili = e.GetCurrentPropertyValue(PropSelectionItemIsSelected) is bool b && b; } catch { }
                    sonuc.Add(new Sekme(ad, sira++, secili, e));
                }
                catch { /* bu öğe okunamadı, ötekilere devam */ }
            }
        }
        catch (Exception ex) { SonHata = "oku: " + ex.GetType().Name + " " + ex.Message; sonuc.Clear(); }
        return sonuc;
    }

    /// <summary>Tanı (yalnız deneme): sekmelerin hangi desenleri sunduğunu yazar.</summary>
    internal static string Tani(IntPtr pencere)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in Sekmeler(pencere))
        {
            var e = (IUIAutomationElement)s.Oge!;
            string P(int id) { try { return e.GetCurrentPropertyValue(id)?.ToString() ?? "null"; } catch (Exception ex) { return "hata:" + ex.GetType().Name; } }
            sb.AppendLine($"{s.Sira} [{s.Ad}] secili={s.Secili} selItem={P(30036)} invoke={P(30031)} legacy={P(30090)} rect={P(30001)}");
        }
        return sb.ToString();
    }

    /// <summary>Verilen sekmeyi seçer (yoksa ya da seçilemezse false).</summary>
    internal static bool Sec(Sekme s)
    {
        try
        {
            if (s.Oge is not IUIAutomationElement e) return false;
            var desen = (IUIAutomationSelectionItemPattern)e.GetCurrentPattern(PatternSelectionItem);
            desen.Select();
            return true;
        }
        catch (Exception ex) { SonHata = "sec: " + ex.GetType().Name + " " + ex.Message; return false; }
    }
}
