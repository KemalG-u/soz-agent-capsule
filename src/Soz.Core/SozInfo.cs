using System.Reflection;

namespace Soz.Core;

/// <summary>
/// Ürün sürüm bilgisi (tek kaynak): <c>urun\Directory.Build.props</c> içindeki <c>Version</c>. Derleme bunu derlemenin sürüm özelliğine yazar;
/// burası oradan okur (artı işaretinden sonraki kaynak sürüm eki atılır). Tepsi, Hakkında, komut satırı, sunucu ve veritabanı kaydı aynı değeri görür.
/// </summary>
public static class SozInfo
{
    public static string Version { get; } = Read();

    private static string Read()
    {
        var asm = typeof(SozInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
