using Soz.Core.Data;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// sh1: kabuk, eski sürüm yeni şemalı veriyi açmayınca kişiye "yeni sürümü kur" der. Bunu istisnanın tam adıyla tanır
/// (<c>Soz.Shell.AcilisHatasi.SemaYeniTuru</c>). Ad değişirse kabuk yanıltıcı "kapatıp yeniden aç" cümlesine düşerdi: bu sınama adı korur.
/// </summary>
public class Sh1SemaAdiTests
{
    [Fact]
    public void Yeni_sema_istisnasinin_tam_adi_kabugun_bildigi_adla_ayni()
    {
        Assert.Equal("Soz.Core.Data.SozSchemaTooNewException", typeof(SozSchemaTooNewException).FullName);
        var kabuk = File.ReadAllText(Path.Combine(Kok(), "src", "Soz.Shell", "GuncellemeKurallari.cs"));
        Assert.Contains("public const string SemaYeniTuru = \"Soz.Core.Data.SozSchemaTooNewException\";", kabuk);
    }

    [Fact]
    public void Yeni_sema_istisnasi_veriye_dokunmadan_atilir_ve_surumleri_tasir()
    {
        var e = new SozSchemaTooNewException(17, 16);
        Assert.Equal(17, e.FoundVersion);
        Assert.Equal(16, e.SupportedVersion);
        Assert.IsAssignableFrom<InvalidOperationException>(e);
    }

    private static string Kok()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
    }
}
