using System.Runtime.InteropServices;

namespace Soz.Capsule;

[ComImport, Guid("D6DD68D1-86FD-4332-8666-9ABEDEA2D24C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRawElementProviderSimple
{
    int ProviderOptions { get; }
    [return: MarshalAs(UnmanagedType.IUnknown)] object? GetPatternProvider(int patternId);
    object? GetPropertyValue(int propertyId);
    IRawElementProviderSimple? HostRawElementProvider { get; }
}

[ComImport, Guid("54FCB24B-E18E-47A2-B4D3-ECCBE757D82C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInvokeProvider { void Invoke(); }

/// <summary>
/// En az düzey UI Automation: özel çizilen kapsül ekran okuyucuya "Söz kapsülü, düğme" olarak görünür; adı durumu ve içeriği söyler
/// ("Söz küçük pencere. 2 iş seni bekliyor."), yardım metni etkileşimleri anlatır, Invoke sol tık yerine geçer.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class KapsulUia : IRawElementProviderSimple, IInvokeProvider
{
    [DllImport("uiautomationcore.dll")] private static extern bool UiaClientsAreListening();
    [DllImport("uiautomationcore.dll")] private static extern int UiaRaiseAutomationPropertyChangedEvent([MarshalAs(UnmanagedType.IUnknown)] object provider, int propertyId, [MarshalAs(UnmanagedType.Struct)] object oldValue, [MarshalAs(UnmanagedType.Struct)] object newValue);
    [DllImport("uiautomationcore.dll")] private static extern int UiaRaiseAutomationEvent([MarshalAs(UnmanagedType.IUnknown)] object provider, int eventId);

    private readonly IntPtr _hwnd;
    private readonly Func<string> _ad;
    private readonly Func<string> _yardim;
    private readonly Action _tikla;
    private string _sonAd = "";
    public const int UIA_LiveRegionChangedEventId = 20024;

    public KapsulUia(IntPtr hwnd, Func<string> ad, Func<string> yardim, Action tikla) { _hwnd = hwnd; _ad = ad; _yardim = yardim; _tikla = tikla; }

    public int ProviderOptions => 2 | 0x100; // ServerSideProvider | UseComThreading (çağrılar pencerenin iş parçacığına gelir)
    public IRawElementProviderSimple? HostRawElementProvider
    {
        get { N.UiaHostProviderFromHwnd(_hwnd, out var p); return p as IRawElementProviderSimple; }
    }

    public object? GetPatternProvider(int patternId) => patternId == 10000 /*Invoke*/ ? this : null;

    public object? GetPropertyValue(int id) => id switch
    {
        30003 => 50000,                      // ControlType = Button
        30004 => "Söz kapsülü",              // LocalizedControlType
        30005 => _ad(),                      // Name
        30011 => "SozKapsul",                // AutomationId
        30012 => "SozKapsul",                // ClassName
        30010 => true,                       // IsEnabled
        30009 => false,                      // IsKeyboardFocusable (kapsül odak almaz)
        30013 => _yardim(),                  // HelpText: ayardaki gerçek kısayol (sabit metin değil)
        30018 => false,                      // IsKeyboardFocusable benzeri: HasKeyboardFocus
        30019 => false,
        _ => null,
    };

    public void Invoke() => _tikla();

    /// <summary>Ad değiştiyse dinleyen istemciye (ekran okuyucu) bildirir. İstemci yoksa hiçbir iş yapmaz.</summary>
    public void DegistiBildir()
    {
        string yeni;
        try { yeni = _ad(); } catch { return; }
        if (yeni == _sonAd) return;
        _sonAd = yeni;
        // SAPMA: UiaRaiseAutomationPropertyChangedEvent .NET 10 altında ExecutionEngineException ile süreci düşürdü (bir UIA istemcisi dinlerken).
        // Ad değişimi olayı gönderilmez; istemci adı her okuyuşta güncel değeri görür (GetPropertyValue canlı hesaplar).
    }
}
