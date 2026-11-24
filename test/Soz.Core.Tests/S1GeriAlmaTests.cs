using System.Text;
using Soz.Core.Restore;
using Xunit;
using static Soz.Core.Tests.GeriAlmaMotorTests;

namespace Soz.Core.Tests;

/// <summary>s1 / B5 (sır dosyaları pakette düz durmaz) ve B10 (önizleme sayısı = uygulanan sayı).</summary>
public class S1GeriAlmaTests
{
    private const string Parola = "hunter2s1parola";
    private const string Anahtar = "sk-ant-api03-S1SAKLIANAHTAR9876543210zyxwvutsrqpon";

    private static bool DepoIcindeVar(GeriRig r, string aranan)
    {
        var b = Encoding.UTF8.GetBytes(aranan);
        foreach (var f in Directory.EnumerateFiles(r.Depo.PackDir, "*", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(r.Depo.BlobDir, "*", SearchOption.AllDirectories)))
        {
            var data = File.ReadAllBytes(f);
            if (data.AsSpan().IndexOf(b) >= 0) return true;
        }
        return false;
    }

    [Fact]
    public void Sir_dosyalari_depoda_sifreli_durur_ve_geri_almada_bayt_bayt_ayni_doner()
    {
        using var r = new GeriRig();
        r.Yaz("hesap.js", "function topla(a, b) { return a + b }\n");
        r.Yaz("notlar.txt", "sade bir not duz metin kalir\n");
        r.Yaz(".env", $"DB_PASSWORD={Parola}\nPORT=3000\n");
        r.Yaz("config/ayar.txt", $"api_key = {Anahtar}\n");   // adı sıradan, içinde anahtar deseni var
        var pem = new byte[] { 0x2d, 0x2d, 0x2d, 0x2d, 0x2d, 0x42, 0x45, 0x47, 0x49, 0x4e, 0x00, 0xff, 0xfe, 0x01, 0x02, 0x03 }.Concat(Encoding.ASCII.GetBytes("MIIEvQIBADANBgkqhkiG9w0BAQEFAASC")).ToArray();
        r.YazBayt("anahtar.pem", pem);
        r.YazBayt("buyuk.key", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("GIZLIBUYUKANAHTARSATIRI0123456789\n", 12000)))); // ~400 KB: paket değil dosya blob'u

        var once = r.Ozet();
        var b = r.Al();
        Assert.True(b.Ok);
        r.Sil(".env"); r.Sil("config"); r.Sil("anahtar.pem"); r.Sil("buyuk.key"); r.Yaz("hesap.js", "bozuldu\n");
        var a = r.Al();

        // Depoda sır yok; sade dosya içeriği ise okunabiliyor (arama yöntemi çalışıyor).
        Assert.False(DepoIcindeVar(r, Parola), "parola depoda düz metin");
        Assert.False(DepoIcindeVar(r, Anahtar), "anahtar depoda düz metin");
        Assert.False(DepoIcindeVar(r, "GIZLIBUYUKANAHTARSATIRI"), "büyük anahtar dosyası depoda düz metin");
        Assert.False(DepoIcindeVar(r, "MIIEvQIBADANBgkqhkiG9w0BAQEFAASC"), "pem içeriği depoda düz metin");
        Assert.True(DepoIcindeVar(r, "sade bir not duz metin kalir"), "arama yöntemi sade içeriği bulamadı");

        var o = r.Geri(b, a);
        Assert.True(o.Ok);
        Assert.Empty(o.Unrestorable);
        Assert.Equal(once, r.Ozet());                                // bayt bayt aynı
        Assert.Equal(pem, File.ReadAllBytes(r.P("anahtar.pem")));
    }

    [Fact]
    public void Sir_kutusu_gidis_donus_ve_ad_kurallari()
    {
        var d = Encoding.UTF8.GetBytes("gizli değer 123");
        var s = SirKutusu.Protect(d);
        Assert.NotEqual(d, s);
        Assert.Equal(d, SirKutusu.Unprotect(s));
        Assert.Throws<SirAcilamadiException>(() => SirKutusu.Unprotect(new byte[] { 1, 2, 3, 4 }));
        foreach (var ad in new[] { ".env", ".env.local", "a/b/.env.production", "x/id_rsa", "id_ed25519.pub", "k.PEM", "s.pfx", "credentials.json", "secrets.yml", "sertifika.crt" })
            Assert.True(SirKutusu.IsSecretFile(ad), ad);
        foreach (var ad in new[] { "hesap.js", "README.md", "src/env.ts", "keyboard.js" })
            Assert.False(SirKutusu.IsSecretFile(ad), ad);
    }

    // ---------------------------------------------------------------- B10

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Onizleme_sayisi_uygulanan_sayiyla_ayni(int karisim)
    {
        using var r = new GeriRig();
        r.Yaz("hesap.js", "a\n"); r.Yaz("notlar.txt", "n\n"); r.Yaz("src/a.js", "x\n"); r.KlasorYap("bos-klasor");
        var b = r.Al();
        switch (karisim)
        {
            case 0: // yalnız yeni iç içe klasörler: dosyalar silinince klasörler de kalkar
                r.Yaz("yeni/ic/x.txt", "1"); r.Yaz("yeni/y.txt", "2"); break;
            case 1: // değişiklik + silme + yeni dosya + yeni klasör
                r.Yaz("hesap.js", "degisti\n"); r.Sil("notlar.txt"); r.Yaz("fazla.txt", "f"); r.Yaz("a/b/c/d.txt", "d"); break;
            case 2: // ajan boş klasörü sildi, başka yerde klasör ağacı kurdu
                r.Sil("bos-klasor"); r.Yaz("t1/t2/t3/a.txt", "a"); r.Yaz("t1/t2/b.txt", "b"); r.Yaz("src/yeni.txt", "y"); break;
            case 3: // var olan klasörün içine eklenen dosya: klasör kalmalı, sayıya girmemeli
                r.Yaz("src/ek1.txt", "e1"); r.Yaz("src/ek2.txt", "e2"); r.Sil("src/a.js"); break;
            case 4: // yeni klasörün içine kişi de bir şey yazdı: klasör dolu kalır (çakışma)
                r.Yaz("yeni/ajan.txt", "a"); break;
        }
        var a = r.Al();
        if (karisim == 4) r.Yaz("yeni/kisi.txt", "kisi"); // ajandan sonra

        var onizleme = r.Geri(b, a, uygula: false);
        Assert.True(onizleme.Ok);
        var uygulanan = r.Geri(b, a);
        Assert.True(uygulanan.Ok);
        var yapilan = uygulanan.Restored + uygulanan.Renamed + uygulanan.Removed + uygulanan.DirsCreated + uygulanan.DirsRemoved;
        Assert.Equal(onizleme.Items.Count, yapilan);
        Assert.True(yapilan > 0);
    }
}
