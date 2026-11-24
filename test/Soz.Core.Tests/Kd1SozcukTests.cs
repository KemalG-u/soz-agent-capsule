using System.Text.RegularExpressions;
using Soz.Core.Attention;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>kd1: ekrana çıkan cümlelerde "ajan" sözcüğü yok ("yardımcı" denir).</summary>
public class Kd1SozcukTests
{
    [Fact]
    public void Tur_sonu_cumlesi_yardimci_der_ajan_ve_tur_demez()
    {
        var bitti = TurnEnd.Sentence(true, "Hesap makinesi");
        Assert.Equal("«Hesap makinesi» yardımcısı işini bitirdi ve seni bekliyor.", bitti);
        foreach (var s in new[] { bitti, TurnEnd.Sentence(false, "Hesap makinesi") })
        {
            Assert.DoesNotContain("ajan", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("turunu", s);
            Assert.DoesNotContain('—', s); Assert.DoesNotContain(';', s);
        }
    }

    [Fact]
    public void Core_kaynaginda_kullaniciya_giden_dizelerde_ajan_sozcugu_kalmadi()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Core"))) d = d.Parent;
        Assert.NotNull(d);
        // Kalanlar bilerek: makine kodları ("ajan-sildi" gibi neden kodları, "Local\SozAjan" adı), modele giden istemler (Judge, explain.prompt) ve çevirici kuralları (UserWords).
        var izinli = new[] { "ajan-", "SozAjan", "explain.prompt" };
        var bulunan = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(d!.FullName, "src", "Soz.Core"), "*.cs", SearchOption.AllDirectories))
        {
            var yol = f.Replace('\\', '/');
            if (yol.Contains("/obj/") || yol.Contains("/bin/") || yol.EndsWith("/Judge/Judge.cs") || yol.EndsWith("/Text/UserWords.cs")) continue;
            var n = 0;
            foreach (var satir in File.ReadLines(f))
            {
                n++;
                var t = satir.TrimStart();
                if (t.StartsWith("//") || izinli.Any(satir.Contains)) continue;
                var kod = satir.Contains("// ") ? satir[..satir.IndexOf("// ", StringComparison.Ordinal)] : satir;
                if (Regex.IsMatch(kod, "\"[^\"]*\b[Aa]jan", RegexOptions.None)) bulunan.Add($"{Path.GetFileName(f)}:{n}");
            }
        }
        Assert.True(bulunan.Count == 0, "ekrana çıkabilecek 'ajan' dizeleri: " + string.Join(", ", bulunan));
    }
}
