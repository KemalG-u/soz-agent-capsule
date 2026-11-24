using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// DONDURULMUŞ KARŞILAŞTIRMA (d2a). Ölçütler kayda bağlanmadan ÖNCE yazıldı ve eski kodda yeşil koştu:
/// mevcut dört tür (komut, dosya, adres, ekran görüntüsü) ve yedi düz cümle şablonu kayda bağlandıktan sonra BİREBİR aynı sonucu vermeli.
/// Bu dosyada bir beklenen değeri değiştirmek eski davranışı değiştirmek demektir: gerekçesiz yapılmaz.
/// </summary>
public class ProofFrozenTests
{
    private static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static CheckToRun Chk(string kind, string specJson, int timeout = 30) => new(1, kind, "deneme", J(specJson), timeout);

    private static ProofRunner Runner(TestEnv env) => new(env.Root, Path.Combine(env.Root, "kanit"), () => null);

    private static string Run(ProofRunner r, string kind, string specJson, out CheckOutcome o)
    {
        o = r.Run(Chk(kind, specJson), TimeSpan.FromSeconds(30));
        return o.Status;
    }

    // ---------- dosya ----------
    [Fact]
    public void File_Kind_Behaves_As_Before()
    {
        using var env = new TestEnv(copySamples: false);
        File.WriteAllText(Path.Combine(env.Root, "a.txt"), "merhaba");
        var r = Runner(env);

        Assert.Equal(ResultStatus.Passed, Run(r, "file", """{"path":"a.txt"}""", out var o));
        Assert.Equal("Dosya var (7 bayt): a.txt", o.OutputTail);
        Assert.Null(o.ExitCode); Assert.Null(o.ArtifactName);

        Assert.Equal(ResultStatus.Failed, Run(r, "file", """{"path":"yok.txt"}""", out o));
        Assert.Equal("Dosya yok: yok.txt", o.OutputTail);

        Assert.Equal(ResultStatus.Failed, Run(r, "file", """{"path":"a.txt","exists":false}""", out o));
        Assert.Equal("Dosya var (olmaması bekleniyordu): a.txt", o.OutputTail);

        Assert.Equal(ResultStatus.Passed, Run(r, "file", """{"path":"yok.txt","exists":false}""", out o));
        Assert.Equal("Dosya yok (beklendiği gibi).", o.OutputTail);

        Assert.Equal(ResultStatus.Failed, Run(r, "file", """{"path":"a.txt","minBytes":100}""", out o));
        Assert.Equal("a.txt: boyut 7 bayt (en az 100)", o.OutputTail);

        Assert.Equal(ResultStatus.Passed, Run(r, "file", """{"path":"a.txt","contains":"MERHABA"}""", out o));
        Assert.Equal("Dosya var (7 bayt): a.txt", o.OutputTail);

        Assert.Equal(ResultStatus.Failed, Run(r, "file", """{"path":"a.txt","contains":"dunya"}""", out o));
        Assert.Equal("a.txt: içinde \"dunya\" yok", o.OutputTail);

        Assert.Equal(ResultStatus.Error, Run(r, "file", """{"path":""}""", out o));
        Assert.Equal("Dosya yolu boş.", o.OutputTail);
    }

    // ---------- adres ----------
    internal sealed class MiniServer : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public string Url => $"http://127.0.0.1:{Port}/";

        public MiniServer(string body, int status = 200, string? location = null)
        {
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(); } catch (Exception) { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            try
                            {
                                var s = c.GetStream();
                                var buf = new byte[4096];
                                var got = new StringBuilder();
                                while (!got.ToString().Contains("\r\n\r\n")) { var n = await s.ReadAsync(buf); if (n <= 0) return; got.Append(Encoding.ASCII.GetString(buf, 0, n)); }
                                var b = Encoding.UTF8.GetBytes(body);
                                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {b.Length}\r\n{(location is null ? "" : "Location: " + location + "\r\n")}Connection: close\r\n\r\n");
                                await s.WriteAsync(head); await s.WriteAsync(b);
                            }
                            catch (Exception) { }
                        }
                    });
                }
            });
        }

        public void Dispose() { try { _l.Stop(); } catch (Exception) { } }
    }

    [Fact]
    public void Url_Kind_Behaves_As_Before()
    {
        using var env = new TestEnv(copySamples: false);
        using var srv = new MiniServer("Merhaba dunya");
        var r = Runner(env);

        Assert.Equal(ResultStatus.Passed, Run(r, "url", $$"""{"url":"{{srv.Url}}"}""", out var o));
        Assert.Equal(srv.Url + " → 200", o.OutputTail); Assert.Equal(200, o.ExitCode);

        Assert.Equal(ResultStatus.Passed, Run(r, "url", $$"""{"url":"{{srv.Url}}","contains":"DUNYA"}""", out o));

        Assert.Equal(ResultStatus.Failed, Run(r, "url", $$"""{"url":"{{srv.Url}}","contains":"yok"}""", out o));
        Assert.Equal(srv.Url + " → sayfada \"yok\" yok", o.OutputTail); Assert.Equal(200, o.ExitCode);

        Assert.Equal(ResultStatus.Failed, Run(r, "url", $$"""{"url":"{{srv.Url}}","expectStatus":404}""", out o));
        Assert.Equal(srv.Url + " → durum 200 (beklenen 404)", o.OutputTail);

        Assert.Equal(ResultStatus.Failed, Run(r, "url", """{"url":"ftp://x"}""", out o));
        Assert.Equal("Adres http/https değil: ftp://x", o.OutputTail);

        Assert.Equal(ResultStatus.Error, Run(r, "url", """{"url":""}""", out o));
        Assert.Equal("Adres boş.", o.OutputTail);
    }

    // ---------- komut ----------
    [Fact]
    public void Command_Kind_Behaves_As_Before()
    {
        using var env = new TestEnv(copySamples: false);
        var r = Runner(env);

        Assert.Equal(ResultStatus.Passed, Run(r, "command", """{"cmd":"echo merhaba","expectExit":0}""", out var o));
        Assert.Equal(0, o.ExitCode); Assert.Contains("merhaba", o.OutputTail); Assert.Equal("cikti-1.txt", o.ArtifactName);
        Assert.Equal("echo merhaba|", o.CmdKey);

        Assert.Equal(ResultStatus.Failed, Run(r, "command", """{"cmd":"exit 3"}""", out o));
        Assert.Equal(3, o.ExitCode); Assert.Contains("[Söz] Geçmedi: çıkış kodu 3 (beklenen 0)", o.OutputTail);

        Assert.Equal(ResultStatus.Passed, Run(r, "command", """{"cmd":"exit 3","expectExit":3}""", out o));

        Assert.Equal(ResultStatus.Failed, Run(r, "command", """{"cmd":"echo merhaba","mustContain":"dunya"}""", out o));
        Assert.Contains("[Söz] Geçmedi: çıktıda \"dunya\" yok", o.OutputTail);

        Assert.Equal(ResultStatus.Error, Run(r, "command", """{"cmd":""}""", out o));
        Assert.Equal("Komut boş.", o.OutputTail);

        Assert.Equal(ResultStatus.Error, Run(r, "command", """{"cmd":"echo x","cwd":".."}""", out o));
        Assert.Equal("Kontrol çalıştırılamadı: InvalidOperationException: Kontrol klasörü proje kökünün dışında: ..", o.OutputTail);

        Assert.Equal(ResultStatus.Error, Run(r, "command", """{"cmd":"echo x","cwd":"yok-klasor"}""", out o));
        Assert.StartsWith("Çalışma klasörü yok: ", o.OutputTail);
    }

    // ---------- ekran görüntüsü ----------
    [Fact]
    public void Screenshot_Kind_Behaves_As_Before()
    {
        using var env = new TestEnv(copySamples: false);
        var r = Runner(env);
        var page = Path.Combine(env.Root, "s.html");
        File.WriteAllText(page, "<html>x</html>");
        var missing = new Uri(Path.Combine(env.Root, "yok.html")).AbsoluteUri;

        Assert.Equal(ResultStatus.Failed, Run(r, "screenshot", $$"""{"url":"{{missing}}"}""", out var o));
        Assert.Equal("Dosya yok: " + new Uri(missing).LocalPath, o.OutputTail);

        Assert.Equal(ResultStatus.Error, Run(r, "screenshot", $$"""{"url":"{{new Uri(page).AbsoluteUri}}"}""", out o));
        Assert.Equal("Edge (msedge.exe) bulunamadı. Ekran görüntüsü alınamadı.", o.OutputTail);

        Assert.Equal(ResultStatus.Error, Run(r, "screenshot", """{"url":""}""", out o));
        Assert.Equal("Adres boş.", o.OutputTail);
    }

    [Fact]
    public void Unknown_Kind_Is_Error_Never_Passed()
    {
        using var env = new TestEnv(copySamples: false);
        var r = Runner(env);
        Assert.Equal(ResultStatus.Error, Run(r, "uydurma-tur", "{}", out var o));
        Assert.Equal("Bilinmeyen kontrol türü: uydurma-tur", o.OutputTail);
    }

    // ---------- doğrulama ----------
    [Fact]
    public void Validate_Messages_Of_Old_Kinds_Are_Unchanged()
    {
        Assert.Equal("command kontrolü için spec.cmd gerekli.", CheckStore.Validate("command", J("{}")));
        Assert.Equal("file kontrolü için spec.path gerekli.", CheckStore.Validate("file", J("{}")));
        Assert.Equal("url kontrolü için spec.url gerekli.", CheckStore.Validate("url", J("{}")));
        Assert.Equal("screenshot kontrolü için spec.url gerekli.", CheckStore.Validate("screenshot", J("{}")));
        Assert.Null(CheckStore.Validate("command", J("""{"cmd":"x"}""")));
        Assert.Null(CheckStore.Validate("file", J("""{"path":"x"}""")));
        Assert.Null(CheckStore.Validate("url", J("""{"url":"http://x"}""")));
        Assert.Null(CheckStore.Validate("screenshot", J("""{"url":"http://x"}""")));
        Assert.NotNull(CheckStore.Validate("uydurma-tur", J("{}")));
    }

    // ---------- yedi düz cümle ----------
    [Fact]
    public void Seven_Sentences_Build_The_Same_Checks()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "p");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "x", "scripts": { "test": "node --test", "build": "node build.js" } }""");

        void Same(string tpl, string? values, string kind, string label, string specJson)
        {
            var (k, l, s) = CheckTemplates.Build(tpl, values is null ? null : J(values), dir);
            Assert.Equal(kind, k);
            Assert.Equal(label, l);
            Assert.Equal(J(specJson).ToJsonString(), s.ToJsonString());
        }

        Same("tests", null, "command", "Testler geçsin (npm test)", """{"cmd":"npm test","expectExit":0,"template":"tests"}""");
        Same("build", null, "command", "Proje derlensin (npm run build)", """{"cmd":"npm run build","expectExit":0,"template":"build"}""");
        Same("url-opens", null, "url", "Şu adres açılsın: http://localhost:3000", """{"url":"http://localhost:3000","expectStatus":200,"template":"url-opens"}""");
        Same("page-text", """{"text":"Hoş geldin"}""", "url", "Sayfada şu yazı görünsün: \"Hoş geldin\" (http://localhost:3000)",
            """{"url":"http://localhost:3000","expectStatus":200,"contains":"Hoş geldin","template":"page-text"}""");
        Same("file-exists", """{"path":"dist/index.html"}""", "file", "Şu dosya oluşsun: dist/index.html", """{"path":"dist/index.html","exists":true,"template":"file-exists"}""");
        Same("file-text", """{"path":"README.md","text":"Kurulum"}""", "file", "Dosyada şu yazı geçsin: \"Kurulum\" (README.md)",
            """{"path":"README.md","contains":"Kurulum","template":"file-text"}""");
        Same("screenshot", """{"url":"http://localhost:3000"}""", "screenshot", "Sayfanın ekran görüntüsünü al: http://localhost:3000", """{"url":"http://localhost:3000","template":"screenshot"}""");

        var e1 = Assert.Throws<SozException>(() => CheckTemplates.Build("uydurma", null, dir));
        Assert.Equal((400, "Bilinmeyen cümle: uydurma"), (e1.Status, e1.Message));
        var e2 = Assert.Throws<SozException>(() => CheckTemplates.Build("file-exists", J("""{"path":"..\\x"}"""), dir));
        Assert.Equal((400, "Dosya proje klasörünün içinde olmalı: ..\\x"), (e2.Status, e2.Message));
        var e3 = Assert.Throws<SozException>(() => CheckTemplates.Build("url-opens", J("""{"url":"ftp://x"}"""), dir));
        Assert.Equal((400, "Adres http:// ya da https:// ile başlamalı: ftp://x"), (e3.Status, e3.Message));
        var e4 = Assert.Throws<SozException>(() => CheckTemplates.Build("file-text", J("""{"path":"a"}"""), dir));
        Assert.Equal((400, "\"Dosyada şu yazı geçsin\" için yazı boş olamaz."), (e4.Status, e4.Message));
        var empty = Path.Combine(env.Root, "bos");
        Directory.CreateDirectory(empty);
        var e5 = Assert.Throws<SozException>(() => CheckTemplates.Build("tests", null, empty));
        Assert.Equal(409, e5.Status);
        Assert.StartsWith("Bu projede test komutu bulamadım", e5.Message);
    }

    [Fact]
    public void Catalog_Starts_With_The_Same_Seven_Sentences()
    {
        var cat = CheckTemplates.For(null);
        var first = cat.Templates.Take(7).Select(t => (t.Id, t.Sentence, t.Kind, t.Available)).ToArray();
        Assert.Equal(new[]
        {
            ("tests", "Testler geçsin", "command", false),
            ("build", "Proje derlensin", "command", false),
            ("url-opens", "Şu adres açılsın", "url", true),
            ("page-text", "Sayfada şu yazı görünsün", "url", true),
            ("file-exists", "Şu dosya oluşsun", "file", true),
            ("file-text", "Dosyada şu yazı geçsin", "file", true),
            ("screenshot", "Sayfanın ekran görüntüsünü al", "screenshot", true),
        }, first);
        Assert.False(cat.HasTests);
        Assert.Equal(CheckTemplates.NoTestsHint, cat.Hint);
        Assert.Equal("Projenin klasörü bilinmiyor. Komut aranamadı.", cat.Templates[0].Reason);
    }

    // ---------- hüküm ----------
    private static CheckOutcome Out(string status, int skipped = 0, string? note = null) => new(1, "L" + status, status, null, null, null, 0, skipped, note);

    [Fact]
    public void Judge_Is_Unchanged_For_Old_Statuses()
    {
        Assert.Equal((Verdicts.Unchecked, "Bu iş için denenecek açık bir kontrol yok. Bu bir \"geçti\" değildir."), ProofStore.Judge(Array.Empty<CheckOutcome>()));
        Assert.Equal((Verdicts.Passed, "1 kontrol geçti."), ProofStore.Judge(new[] { Out(ResultStatus.Passed) }));
        Assert.Equal((Verdicts.Passed, "2 kontrolün hepsi geçti. 3 test atlandı."), ProofStore.Judge(new[] { Out(ResultStatus.Passed, 3), Out(ResultStatus.Passed) }));
        Assert.Equal((Verdicts.Failed, "2 kontrolden 1 tanesi geçmedi: Lfailed"), ProofStore.Judge(new[] { Out(ResultStatus.Passed), Out(ResultStatus.Failed) }));
        Assert.Equal((Verdicts.Error, "2 kontrolden 1 tanesinde emin olunamadı: Lerror"), ProofStore.Judge(new[] { Out(ResultStatus.Passed), Out(ResultStatus.Error) }));
        Assert.Equal((Verdicts.Unchecked, "2 kontrolden 1 tanesi doğrulanamadı: Lunchecked (hiç test yok). Bu bir \"geçti\" değildir."),
            ProofStore.Judge(new[] { Out(ResultStatus.Passed), Out(ResultStatus.Unchecked, 0, "hiç test yok") }));
    }
}
