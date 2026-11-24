using System.Text.Json.Nodes;
using Soz.Core.Registry;
using Xunit;
using static Soz.Core.Tests.RegistryTestKit;

namespace Soz.Core.Tests;

/// <summary>`test\kayit-ornekleri\` paketi: doğru örnekler yüklenir, bozuk örnekler DOĞRU NEDENLE reddedilir (neden metni kontrol edilir).</summary>
public class RegistryExamplesTests
{
    private static string[] Files(string prefix)
        => Directory.GetFiles(ExamplesDir, prefix + "*.soz.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    private static RegistryLoadResult LoadFile(string path, IRegistryApprovals? approvals = null)
        => RegistryLoader.Load(Array.Empty<RegistrySource>(), new[] { new RegistrySource(Path.GetFileName(path), "file:" + path, File.ReadAllBytes(path)) }, approvals);

    [Fact]
    public void Paket_EnAzDortDogru_EnAzDokuzBozukOrnekIcerir()
    {
        Assert.True(Files("dogru-").Length >= 4);
        Assert.True(Files("bozuk-").Length >= 9);
        Assert.True(File.Exists(Path.Combine(ExamplesDir, "beklenen.json")));
    }

    [Fact]
    public void DogruOrnekler_HatasizYuklenir_ve_BekleneDurumdadir()
    {
        var expected = new Dictionary<string, (int entries, int warnings, string[] states)>
        {
            ["dogru-1-baglayici-opencode.soz.json"] = (1, 0, new[] { "suggested" }),              // bağlayıcı çalıştırılabilir: onaysız öneri
            ["dogru-2-uc-ayar.soz.json"] = (3, 0, new[] { "enabled", "enabled", "enabled" }),     // K1, K2, K3 ayar
            ["dogru-3-taninan-turler.soz.json"] = (9, 9, Enumerable.Repeat("enabled", 9).ToArray()), // gövdesi tanımsız dokuz tür: uyarıyla geçer (d2m: reader tanımlı türlere girdi, kendi örneği dogru-6)
            ["dogru-4-olcut-dosya-boyutu.soz.json"] = (1, 0, new[] { "enabled" }),                // yerleşik "file" ölçer çalıştırılabilir sayılmaz
            ["dogru-5-sistem-onerisi.soz.json"] = (1, 0, new[] { "suggested" }),                  // sistem önerisi hiç etkin başlamaz
            ["dogru-6-okuyucu.soz.json"] = (1, 0, new[] { "enabled" }),                          // reader: bildirge gövdesiyle, uyarısız
        };
        foreach (var f in Files("dogru-"))
        {
            var name = Path.GetFileName(f);
            Assert.True(expected.ContainsKey(name), $"{name}: beklenen sonuç tablosunda yok");
            var res = LoadFile(f);
            Assert.True(res.Rejected.Count == 0, $"{name}: reddedildi: {string.Join(" | ", res.Rejected)}");
            var (entries, warnings, states) = expected[name];
            Assert.Equal(entries, res.Entries.Count);
            Assert.Equal(warnings, res.Warnings.Count);
            Assert.Equal(states, res.Entries.Select(e => e.State).ToArray());
            // dosya bütün olarak şemaya da uyar (kök şema EntryFile)
            var errs = RegistryLoader.Schema.Validate(JsonNode.Parse(File.ReadAllText(f)));
            Assert.True(errs.Count == 0, $"{name}: şema: {string.Join(" | ", errs)}");
            Assert.True(RegistryChecker.Check(res).Ok, name);
        }
        Assert.Equal(expected.Keys.OrderBy(k => k), Files("dogru-").Select(Path.GetFileName).OrderBy(k => k)!);
    }

    [Fact]
    public void OnayliOzetle_ConnectorOrnegi_EtkinOlur()
    {
        var f = Files("dogru-1-").Single();
        var first = LoadFile(f);
        var approvals = new MemoryRegistryApprovals();
        approvals.Approve("connector:opencode", first.Entries[0].Sha256);
        Assert.Equal("enabled", LoadFile(f, approvals).Entries.Single().State);
    }

    [Fact]
    public void BozukOrnekler_DogruNedenleReddedilir()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, "beklenen.json")))!["bozuk"]!.AsObject();
        var files = Files("bozuk-");
        Assert.Equal(files.Select(Path.GetFileName).OrderBy(x => x), manifest.Select(kv => kv.Key).OrderBy(x => x));
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var want = manifest[name]!;
            var expect = want["expect"]!.GetValue<string>();
            var code = want["code"]!.GetValue<string>();
            var contains = want["contains"]!.GetValue<string>();
            var res = LoadFile(f);
            var pool = expect == "warning" ? res.Warnings : res.Rejected;
            Assert.True(pool.Any(p => p.Code == code && p.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)), // cümle başı büyük harfi neden metnini değiştirmez (b2)
                $"{name}: beklenen '{code}' / '{contains}' bulunamadı. Gelen: {string.Join(" | ", res.Rejected.Concat(res.Warnings))}");
            if (expect == "warning") Assert.Empty(res.Entries);   // bilinmeyen tür yüklenmez
            if (name.StartsWith("bozuk-14")) Assert.Single(res.Entries); // ilk kayıt yüklendi, tekrar reddedildi
            else Assert.Empty(res.Entries);                          // bozuk kayıt hiçbir koşulda yüklenmez
            // kural denetimi de bozuk dosyayı kabul etmez (uyarı olan bilinmeyen tür kişi dosyasında yalnız uyarıdır)
            var check = RegistryChecker.Check(res);
            Assert.Equal(expect == "warning", check.Ok);
        }
    }
}
