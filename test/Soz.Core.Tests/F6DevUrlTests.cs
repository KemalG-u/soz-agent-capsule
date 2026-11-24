using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>f6: DoneDefinition içindeki düzenli ifadelerde ters bölü kaybolmuştu ((d{2,5}) ve [w-]); açık port ve sözcük sınırı çalışmıyordu.</summary>
public class F6DevUrlTests
{
    private static string? Url(string scripts)
    {
        var dir = Path.Combine(TestEnv.RepoRoot, "_gecici", "testler", "f6-devurl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "package.json");
            File.WriteAllText(f, "{\"scripts\":" + scripts + "}");
            return DoneDefinition.DevUrlOf(f);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("{\"dev\":\"vite --port 4000\"}", "http://localhost:4000")]
    [InlineData("{\"dev\":\"next dev -p 3456\"}", "http://localhost:3456")]
    [InlineData("{\"start\":\"PORT=8123 node server.js\"}", "http://localhost:8123")]
    [InlineData("{\"dev\":\"node app.js --host localhost:9001\"}", "http://localhost:9001")]
    public void AcikPortOkunur(string scripts, string beklenen) => Assert.Equal(beklenen, Url(scripts));

    [Theory]
    [InlineData("{\"dev\":\"vite\"}", "http://localhost:5173")]
    [InlineData("{\"preview\":\"vite preview\"}", "http://localhost:4173")]
    [InlineData("{\"dev\":\"next dev\"}", "http://localhost:3000")]
    [InlineData("{\"dev\":\"astro dev\"}", "http://localhost:4321")]
    public void BilinenAracVarsayilanPortunuVerir(string scripts, string beklenen) => Assert.Equal(beklenen, Url(scripts));

    [Theory]
    [InlineData("{\"dev\":\"my-vite-tool run\"}")]   // başka bir adın parçası: araç değil
    [InlineData("{\"dev\":\"xvite\"}")]
    [InlineData("{\"dev\":\"node run.js\"}")]
    public void SozcukSiniriYanlisEslesmeyiEngeller(string scripts) => Assert.Null(Url(scripts));
}
