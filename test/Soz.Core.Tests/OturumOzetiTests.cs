using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Sessions.Ozet;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Oturum özeti (Sessions/Ozet): üç satır + makine alanları, dil modeli yok. Bütün kayıtlar bu testin yazdığı SENTETİK dosyalardır.
/// Gizlilik: sohbet metni, araç çıktısı, tam yol ve komuttaki sır özete sızmaz (serileştirilmiş JSON üzerinde denetlenir).
/// </summary>
public sealed class OturumOzetiTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 10, 11, 0, 0, DateTimeKind.Utc);
    private readonly string _kok;

    public OturumOzetiTests()
    {
        _kok = Path.Combine(TestEnv.RepoRoot, "_gecici", "testler", "oz1-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_kok);
    }

    public void Dispose()
    {
        try { Directory.Delete(_kok, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------- sentetik kayıt yazıcıları ----------------

    /// <summary>Claude Code biçiminde satır üretir; her olay bir dakika ilerler.</summary>
    private sealed class Claude
    {
        public readonly List<string> Satirlar = new();
        public int Dakika;
        private string Ts() => T0.AddMinutes(Dakika++).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        public DateTime SonAn => T0.AddMinutes(Dakika - 1);
        private static string J(JsonNode n) => n.ToJsonString();

        public Claude Insan(string metin) { Satirlar.Add(J(new JsonObject { ["type"] = "user", ["timestamp"] = Ts(), ["cwd"] = @"C:\GizliKlasor\proje", ["message"] = new JsonObject { ["role"] = "user", ["content"] = metin } })); return this; }

        public Claude Metin(string metin, bool turSonu = false)
        {
            Satirlar.Add(J(new JsonObject
            {
                ["type"] = "assistant", ["timestamp"] = Ts(),
                ["message"] = new JsonObject { ["role"] = "assistant", ["stop_reason"] = turSonu ? "end_turn" : null, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = metin }) },
            }));
            return this;
        }

        public Claude Arac(string id, string ad, JsonObject input)
        {
            Satirlar.Add(J(new JsonObject
            {
                ["type"] = "assistant", ["timestamp"] = Ts(),
                ["message"] = new JsonObject { ["role"] = "assistant", ["stop_reason"] = "tool_use", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = ad, ["input"] = input }) },
            }));
            return this;
        }

        public Claude Komut(string id, string komut, bool arkaPlan = false)
            => Arac(id, "Bash", arkaPlan ? new JsonObject { ["command"] = komut, ["run_in_background"] = true } : new JsonObject { ["command"] = komut, ["description"] = "x" });

        public Claude Duzenle(string id, string yol) => Arac(id, "Edit", new JsonObject { ["file_path"] = yol, ["old_string"] = "eski", ["new_string"] = "YENI_ICERIK_SIZMAMALI" });

        public Claude Sonuc(string id, bool hata = false, string icerik = "tamam", JsonObject? toolUseResult = null)
        {
            var o = new JsonObject
            {
                ["type"] = "user", ["timestamp"] = Ts(),
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = icerik, ["is_error"] = hata }) },
                ["toolUseResult"] = toolUseResult ?? new JsonObject { ["stdout"] = "CIKTI_SIZMAMALI", ["stderr"] = "", ["interrupted"] = false, ["isImage"] = false },
            };
            Satirlar.Add(J(o));
            return this;
        }

        public Claude TurSonu() { Satirlar.Add(J(new JsonObject { ["type"] = "system", ["subtype"] = "turn_duration", ["timestamp"] = Ts(), ["durationMs"] = 1000 })); return this; }
    }

    private sealed class Codex
    {
        public readonly List<string> Satirlar = new();
        public int Dakika;
        private string Ts() => T0.AddMinutes(Dakika++).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        public DateTime SonAn => T0.AddMinutes(Dakika - 1);
        public Codex() => Satirlar.Add(new JsonObject { ["type"] = "session_meta", ["timestamp"] = Ts(), ["payload"] = new JsonObject { ["id"] = "cx-1", ["cwd"] = @"C:\GizliKlasor\proje" } }.ToJsonString());
        private Codex Ev(JsonObject payload) { Satirlar.Add(new JsonObject { ["type"] = "event_msg", ["timestamp"] = Ts(), ["payload"] = payload }.ToJsonString()); return this; }
        private Codex Oge(JsonObject item) => Ev(new JsonObject { ["type"] = "item_completed", ["item"] = item });
        public Codex Insan(string m) => Oge(new JsonObject { ["type"] = "UserMessage", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = m }) });
        public Codex Metin(string m) => Oge(new JsonObject { ["type"] = "AgentMessage", ["phase"] = "final_answer", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = m }) });
        public Codex Komut(string komut, int kod) => Oge(new JsonObject
        {
            ["type"] = "CommandExecution", ["command"] = new JsonArray("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "-Command", komut),
            ["exit_code"] = kod, ["status"] = kod == 0 ? "completed" : "failed", ["aggregated_output"] = "CIKTI_SIZMAMALI",
        });
        public Codex Degisiklik(params string[] yollar)
        {
            var ch = new JsonObject();
            foreach (var y in yollar) ch[y] = new JsonObject { ["type"] = "update", ["unified_diff"] = "+YENI_ICERIK_SIZMAMALI" };
            return Oge(new JsonObject { ["type"] = "FileChange", ["status"] = "completed", ["changes"] = ch });
        }
        public Codex TurBitti(string? son = null) => Ev(new JsonObject { ["type"] = "task_complete", ["last_agent_message"] = son });
        public Codex Olay(string tur) => Ev(new JsonObject { ["type"] = tur });
        public Codex EskiKomutSonu(string komut, int kod) => Ev(new JsonObject { ["type"] = "exec_command_end", ["command"] = new JsonArray("bash", "-lc", komut), ["exit_code"] = kod });
    }

    private string Yaz(IEnumerable<string> satirlar, DateTime mtime, string ad = "oturum.jsonl")
    {
        var p = Path.Combine(_kok, Guid.NewGuid().ToString("N")[..8] + "-" + ad);
        File.WriteAllText(p, string.Join("\n", satirlar) + (satirlar.Any() ? "\n" : ""));
        File.SetLastWriteTimeUtc(p, mtime);
        return p;
    }

    private static OturumOzeti Ozet(string tool, string? yol, DateTime simdi, OzetIpuclari? ip = null)
        => OturumOzetleri.Hesapla(new OturumOzetleri.OturumSatiri(7, tool, yol, ip ?? new OzetIpuclari()), simdi, null)!;

    private OturumOzeti ClaudeOzet(Claude k, double dakikaSonra = 1, OzetIpuclari? ip = null)
        => Ozet("claude", Yaz(k.Satirlar, k.SonAn), k.SonAn.AddMinutes(dakikaSonra), ip);

    private static string Json(OturumOzeti o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // ---------------- "denedi mi" satırı ----------------

    [Fact]
    public void BittiDedi_SonrasindaDenemeYok()
    {
        var k = new Claude().Insan("sepeti düzelt").Duzenle("e1", @"C:\GizliKlasor\proje\sepet.js").Sonuc("e1")
            .Metin("Bitti, sepet artık doğru hesaplıyor.", turSonu: true).TurSonu();
        var o = ClaudeOzet(k);
        Assert.True(o.BittiDedi);
        Assert.False(o.BittiSonrasiDeneme);
        Assert.Null(o.SonDeneme);
        Assert.Equal("Bitti dedi ama sonrasında hiçbir deneme çalıştırmadı.", o.DenediMi);
        Assert.Equal(Bekleyenler.Inceleme, o.Bekleyen);
        Assert.Equal("Bitti, bakmanı bekliyor.", o.SendenBeklenen);
        Assert.Equal("yuksek", o.Guven);
    }

    [Fact]
    public void BittiDedi_SonraDenediVeGecti()
    {
        var k = new Claude().Insan("x").Duzenle("e1", @"C:\p\giris.js").Sonuc("e1")
            .Metin("Tamamlandı. Şimdi doğrulamak için testleri de koşuyorum.")
            .Komut("c1", "cd urun && npm test 2>&1 | tail -20").Sonuc("c1")
            .Metin("Testler de temiz.", turSonu: true).TurSonu();
        var o = ClaudeOzet(k, dakikaSonra: 2);
        Assert.True(o.BittiDedi);
        Assert.True(o.BittiSonrasiDeneme);
        Assert.True(o.SonDegisiklikSonrasiDeneme);
        Assert.NotNull(o.SonDeneme);
        Assert.Equal("npm test", o.SonDeneme!.KomutTuru);
        Assert.Equal(DenemeSonuc.Gecti, o.SonDeneme.Sonuc);
        Assert.Equal(0, o.SonDeneme.CikisKodu);
        Assert.Equal("Bitti dedi, sonra denedi: son denemesi geçti (npm test, 5 dk önce).", o.DenediMi);
    }

    [Fact]
    public void BittiDedi_OncedenDenemisVeGecmis()
    {
        var k = new Claude().Insan("x").Duzenle("e1", @"C:\p\a.py").Sonuc("e1")
            .Komut("c1", "python -m pytest -q").Sonuc("c1").Metin("Bitti, testler geçti.", turSonu: true).TurSonu();
        var o = ClaudeOzet(k, dakikaSonra: 12);
        Assert.True(o.BittiDedi);
        Assert.False(o.BittiSonrasiDeneme);
        Assert.True(o.SonDegisiklikSonrasiDeneme);
        Assert.Equal("Bitti dedi. Son denemesi geçti (pytest, 15 dk önce).", o.DenediMi);
    }

    [Fact]
    public void DenemeKaldi_CikisKoduKayittan()
    {
        var k = new Claude().Insan("x").Komut("c1", "dotnet test Soz.sln").Sonuc("c1", hata: true, icerik: "Exit code 1\nGIZLI_HATA_CIKTISI")
            .Metin("Bir test kaldı, bakıyorum.");
        var o = ClaudeOzet(k, dakikaSonra: 0.5);
        Assert.Equal(DenemeSonuc.Kaldi, o.SonDeneme!.Sonuc);
        Assert.Equal(1, o.SonDeneme.CikisKodu);
        Assert.Equal("dotnet test", o.SonDeneme.KomutTuru);
        Assert.False(o.BittiDedi);
        Assert.StartsWith("Henüz bitti demedi. Son denemesi hata verdi (dotnet test,", o.DenediMi);
        Assert.DoesNotContain("GIZLI_HATA_CIKTISI", Json(o));
    }

    [Fact]
    public void BittiDedi_AmaSonDenemeHataVermis()
    {
        var k = new Claude().Insan("x").Duzenle("e1", @"C:\p\a.go").Sonuc("e1").Komut("c1", "go test ./...").Sonuc("c1", hata: true, icerik: "Exit code 2")
            .Metin("Hazır, teslim ediyorum.", turSonu: true).TurSonu();
        var o = ClaudeOzet(k);
        Assert.True(o.BittiDedi);
        Assert.StartsWith("Bitti dedi ama son denemesi hata verdi (go test,", o.DenediMi);
        Assert.Equal(2, o.SonDeneme!.CikisKodu);
    }

    [Fact]
    public void DenemedenSonraDegisiklik_SonraBitti()
    {
        var k = new Claude().Insan("x").Komut("c1", "npx vitest run").Sonuc("c1").Duzenle("e1", @"C:\p\b.ts").Sonuc("e1")
            .Metin("Done, all fixed.", turSonu: true).TurSonu();
        var o = ClaudeOzet(k);
        Assert.True(o.BittiDedi);
        Assert.False(o.SonDegisiklikSonrasiDeneme);
        Assert.Equal("Bitti dedi ama son değişiklikten sonra hiçbir deneme çalıştırmadı.", o.DenediMi);
    }

    [Fact]
    public void YeniIstem_EskiBittiIddiasiniSayar_Degil()
    {
        var k = new Claude().Insan("ilk iş").Metin("Bitti.", turSonu: true).TurSonu().Insan("şimdi ikinci iş").Duzenle("e1", @"C:\p\c.js").Sonuc("e1");
        var o = ClaudeOzet(k, dakikaSonra: 0.5);
        Assert.False(o.BittiDedi);
        Assert.Equal("Henüz bitti demedi.", o.DenediMi);
        Assert.Equal("Bir şey beklemiyor, çalışıyor.", o.SendenBeklenen);
    }

    [Fact]
    public void ArkaPlanVeKesilenDeneme_SonucBilinmiyor()
    {
        var k = new Claude().Insan("x").Komut("c1", "npm test", arkaPlan: true)
            .Sonuc("c1", toolUseResult: new JsonObject { ["backgroundTaskId"] = "b1", ["stdout"] = "", ["interrupted"] = false })
            .Metin("Testleri arka planda başlattım.", turSonu: true).TurSonu();
        var o = ClaudeOzet(k);
        Assert.Equal(DenemeSonuc.Bilinmiyor, o.SonDeneme!.Sonuc);
        Assert.Null(o.SonDeneme.CikisKodu);
        Assert.Contains("sonucunu kayıtta göremedim", o.DenediMi);

        var k2 = new Claude().Insan("x").Komut("c1", "pytest").Sonuc("c1", toolUseResult: new JsonObject { ["interrupted"] = true, ["stdout"] = "" });
        Assert.Equal(DenemeSonuc.Bilinmiyor, ClaudeOzet(k2, 0.5).SonDeneme!.Sonuc);
    }

    // ---------------- "senden ne bekleniyor" satırı ----------------

    [Fact]
    public void IzinBekliyor_KancaIsaretiVeAcikKomut()
    {
        var k = new Claude().Insan("x").Komut("c1", "git push origin main");
        var since = k.SonAn.AddSeconds(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var o = ClaudeOzet(k, dakikaSonra: 3, ip: new OzetIpuclari("permission_prompt", since));
        Assert.Equal(Bekleyenler.Izin, o.Bekleyen);
        Assert.True(o.BekleyenKesin);
        Assert.Equal("İzin bekliyor: bir komut çalıştırmak istiyor.", o.SendenBeklenen);
        Assert.Equal("kayit+kanca", o.Kaynak);
        Assert.Equal(0, o.KomutSayisi); // izin verilmedi: henüz çalışmadı
    }

    [Fact]
    public void IzinIsareti_SonrasindaIsSurduyse_Eskimistir()
    {
        var k = new Claude().Insan("x").Komut("c1", "npm run build");
        var since = k.SonAn.AddSeconds(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        k.Sonuc("c1").Metin("Derleme bitti.", turSonu: true).TurSonu();
        var o = ClaudeOzet(k, ip: new OzetIpuclari("permission_prompt", since));
        Assert.NotEqual(Bekleyenler.Izin, o.Bekleyen);
        Assert.Equal("kayit", o.Kaynak);
        Assert.Equal(1, o.KomutSayisi);
    }

    [Fact]
    public void IzinBekliyor_DosyaDegisikligi_DosyaAdiyla()
    {
        var k = new Claude().Insan("x").Duzenle("e1", @"C:\GizliKlasor\src\odeme.ts");
        var since = k.SonAn.AddSeconds(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var o = ClaudeOzet(k, 2, new OzetIpuclari("permission_prompt", since));
        Assert.Equal("İzin bekliyor: bir dosyayı değiştirmek istiyor (odeme.ts).", o.SendenBeklenen);
        Assert.Equal(0, o.DegisenDosyaSayisi);
        Assert.DoesNotContain("GizliKlasor", Json(o));
    }

    [Fact]
    public void SoruBekliyor_AskUserQuestionAcik()
    {
        var k = new Claude().Insan("x").Arac("q1", "AskUserQuestion", new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "GIZLI_SORU_METNI?" }) });
        var o = ClaudeOzet(k, 10);
        Assert.Equal(Bekleyenler.Soru, o.Bekleyen);
        Assert.Equal("Sana soru sordu.", o.SendenBeklenen);
        Assert.DoesNotContain("GIZLI_SORU_METNI", Json(o));
    }

    [Fact]
    public void SoruBekliyor_TurSonuSoruCumlesi()
    {
        var k = new Claude().Insan("x").Metin("İki yol var. Hangisini istersin?", turSonu: true).TurSonu();
        var o = ClaudeOzet(k, 30);
        Assert.Equal(Bekleyenler.Soru, o.Bekleyen);
        Assert.Equal("Sana soru sordu.", o.SendenBeklenen);
        Assert.Equal("Henüz bitti demedi.", o.DenediMi);
    }

    [Fact]
    public void AcikKomut_KancaYok_Belirsiz()
    {
        var k = new Claude().Insan("x").Komut("c1", "npm test");
        var eski = ClaudeOzet(k, 10);
        Assert.Equal(Bekleyenler.Yok, eski.Bekleyen);
        Assert.False(eski.BekleyenKesin);
        Assert.Contains("ayırt edemiyorum", eski.SendenBeklenen);
        var taze = Ozet("claude", Yaz(k.Satirlar, k.SonAn), k.SonAn.AddSeconds(20));
        Assert.Equal("Bir şey beklemiyor, çalışıyor.", taze.SendenBeklenen);
        Assert.True(taze.BekleyenKesin);
    }

    [Fact]
    public void HataVeripDurdu_KancaIsareti()
    {
        var k = new Claude().Insan("x");
        var o = ClaudeOzet(k, 1, new OzetIpuclari("failure:rate_limit", k.SonAn.AddSeconds(5).ToString("o")));
        Assert.Equal("Hata verip durdu, bakmanı bekliyor.", o.SendenBeklenen);
    }

    // ---------------- "ne yaptı" satırı ----------------

    [Fact]
    public void NeYapti_DortDosyaYediKomut()
    {
        var k = new Claude().Insan("x");
        var dosyalar = new[] { "giris.js", "sepet.js", "odeme.js", "liste.js" };
        for (var i = 0; i < dosyalar.Length; i++) k.Duzenle("e" + i, $@"C:\GizliKlasor\src\{dosyalar[i]}").Sonuc("e" + i);
        k.Duzenle("e9", @"C:\GizliKlasor\src\giris.js").Sonuc("e9"); // aynı dosya ikinci kez: bir kez sayılır
        for (var i = 0; i < 7; i++) k.Komut("c" + i, "ls -la").Sonuc("c" + i);
        var o = ClaudeOzet(k, 0.5);
        Assert.Equal(4, o.DegisenDosyaSayisi);
        Assert.Equal(7, o.KomutSayisi);
        Assert.Equal("4 dosyayı değiştirdi (giris.js, liste.js ve 2 dosya daha), 7 komut çalıştırdı.", o.NeYapti);
        Assert.Equal(new[] { "giris.js", "liste.js", "odeme.js", "sepet.js" }, o.DosyaAdlari);
    }

    [Fact]
    public void NeYapti_Bicimler()
    {
        Assert.Equal("Henüz dosya değiştirmedi, komut da çalıştırmadı.", OzetYazici.NeYapti(0, Array.Empty<string>(), 0));
        Assert.Equal("Dosya değiştirmedi, 3 komut çalıştırdı.", OzetYazici.NeYapti(0, Array.Empty<string>(), 3));
        Assert.Equal("1 dosyayı değiştirdi (a.js), komut çalıştırmadı.", OzetYazici.NeYapti(1, new[] { "a.js" }, 0));
        Assert.Equal("2 dosyayı değiştirdi (a.js ve b.js), 1 komut çalıştırdı.", OzetYazici.NeYapti(2, new[] { "a.js", "b.js" }, 1));
        Assert.Equal("3 dosyayı değiştirdi (a.js, b.js ve c.js), 1 komut çalıştırdı.", OzetYazici.NeYapti(3, new[] { "a.js", "b.js", "c.js" }, 1));
    }

    [Fact]
    public void DosyaAdlari_EnCokBes_TamYolYok()
    {
        var k = new Claude().Insan("x");
        for (var i = 0; i < 7; i++) k.Duzenle("e" + i, $@"C:\Users\demo\GizliKlasor\d{i}.cs").Sonuc("e" + i);
        k.Arac("w1", "Write", new JsonObject { ["file_path"] = "/home/u/GizliKlasor/yeni.md", ["content"] = "YENI_ICERIK_SIZMAMALI" }).Sonuc("w1");
        var o = ClaudeOzet(k, 0.5);
        Assert.Equal(8, o.DegisenDosyaSayisi);
        Assert.Equal(5, o.DosyaAdlari.Count);
        Assert.Equal("yeni.md", o.DosyaAdlari[0]);
        var json = Json(o);
        Assert.DoesNotContain("GizliKlasor", json);
        Assert.DoesNotContain("demo", json);
        Assert.DoesNotContain("YENI_ICERIK_SIZMAMALI", json);
    }

    [Fact]
    public void HataliDuzenlemeVeReddedilenKomut_Sayilmaz()
    {
        var k = new Claude().Insan("x").Duzenle("e1", @"C:\p\a.js").Sonuc("e1", hata: true, icerik: "String to replace not found")
            .Komut("c1", "npm test").Sonuc("c1", hata: true, icerik: "The user doesn't want to proceed with this tool use.")
            .Komut("c2", "npm run build").Sonuc("c2");
        var o = ClaudeOzet(k, 0.5);
        Assert.Equal(0, o.DegisenDosyaSayisi);
        Assert.Equal(1, o.KomutSayisi);
        Assert.Null(o.SonDeneme);
    }

    // ---------------- güven: boş, bozuk, tanınmayan ----------------

    [Fact]
    public void BosKayit_DusukGuven()
    {
        var o = Ozet("claude", Yaz(Array.Empty<string>(), T0), T0.AddMinutes(1));
        Assert.Equal("dusuk", o.Guven);
        Assert.Equal("Kayıt henüz boş, ne yaptığını gösteremiyorum.", o.NeYapti);
        Assert.Equal("Deneyip denemediğini bilmiyorum.", o.DenediMi);
        Assert.Equal(0, o.KomutSayisi);
        Assert.False(o.BittiDedi);
    }

    [Fact]
    public void BozukSatir_AzsaAtlanir_CoksaDusukGuven()
    {
        var k = new Claude().Insan("x").Komut("c1", "cargo test").Sonuc("c1");
        var satirlar = new List<string>(k.Satirlar) { "{bozuk json", "yarım satır" };
        satirlar.Insert(1, "]]]");
        var o = Ozet("claude", Yaz(satirlar, k.SonAn), k.SonAn.AddMinutes(1));
        Assert.Equal("yuksek", o.Guven);
        Assert.Equal("cargo test", o.SonDeneme!.KomutTuru);
        Assert.Equal(1, o.KomutSayisi);

        var cokBozuk = new List<string> { k.Satirlar[0], "{x", "{y", "{z" };
        var o2 = Ozet("claude", Yaz(cokBozuk, k.SonAn), k.SonAn.AddMinutes(1));
        Assert.Equal("dusuk", o2.Guven);
        Assert.Equal("Kaydın çoğu bozuk.", o2.GuvenNedeni);
    }

    [Fact]
    public void TaninmayanBicim_Claude_Uydurmaz()
    {
        var satirlar = new[] { "{\"kind\":\"chat\",\"body\":\"bitti npm test\"}", "{\"type\":\"yeni_tur\",\"command\":\"npm test\"}" };
        var o = Ozet("claude", Yaz(satirlar, T0), T0.AddMinutes(1));
        Assert.Equal("dusuk", o.Guven);
        Assert.Equal("Bu kaydı okuyamadım, ne yaptığını gösteremiyorum.", o.NeYapti);
        Assert.Equal("Kayıt biçimini tanımadım.", o.GuvenNedeni);
        Assert.Null(o.SonDeneme);
        Assert.False(o.BittiDedi);
    }

    [Fact]
    public void TaninmayanBicim_Codex_IlkSatirMetaDegil()
    {
        var satirlar = new[] { "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}" };
        var o = Ozet("codex", Yaz(satirlar, T0, "rollout-x.jsonl"), T0.AddMinutes(1));
        Assert.Equal("dusuk", o.Guven);
        Assert.Contains("okuyamadım", o.NeYapti);
    }

    [Fact]
    public void DesteklenmeyenArac_VeEksikDosya_DurustDusukGuven()
    {
        var h = Ozet("hermes", @"C:\yok\state.db", T0, new OzetIpuclari(DbTurnEnded: true));
        Assert.Equal("dusuk", h.Guven);
        Assert.Equal("Bu kaydı okuyamadım, ne yaptığını gösteremiyorum.", h.NeYapti);
        Assert.Equal(Bekleyenler.Inceleme, h.Bekleyen);
        Assert.False(h.BekleyenKesin);

        var izin = Ozet("opencode", null, T0, new OzetIpuclari("permission_prompt", T0.ToString("o")));
        Assert.Equal(Bekleyenler.Izin, izin.Bekleyen);
        Assert.Equal("İzin bekliyor: bir işlem yapmak istiyor.", izin.SendenBeklenen);

        var yok = Ozet("claude", Path.Combine(_kok, "olmayan.jsonl"), T0);
        Assert.Equal("dusuk", yok.Guven);
        Assert.Equal("Kayıt dosyası bulunamadı.", yok.GuvenNedeni);
    }

    // ---------------- gizlilik ----------------

    [Fact]
    public void SirIcerenKomut_OzeteSizmaz()
    {
        const string parola = "hunter2SuperGizli99";
        const string jeton = "gh" + "p_abcdefghijklmnopqrstuvwx1234";
        var k = new Claude().Insan("x")
            .Komut("c1", $"DB_PASSWORD={parola} npm test -- --token {jeton}").Sonuc("c1")
            .Komut("c2", "curl -u " + $"demo:{parola} https://ornek.test/api").Sonuc("c2", hata: true, icerik: $"Exit code 7 {jeton}");
        var o = ClaudeOzet(k, 1);
        var json = Json(o);
        Assert.DoesNotContain(parola, json);
        Assert.DoesNotContain(jeton, json);
        Assert.DoesNotContain("curl", json);
        Assert.DoesNotContain("DB_PASSWORD", json);
        Assert.Equal("npm test", o.SonDeneme!.KomutTuru);
        Assert.Equal(2, o.KomutSayisi);
    }

    [Fact]
    public void SohbetMetni_OzeteSizmaz()
    {
        var k = new Claude().Insan("KULLANICI_GIZLI_CUMLE lütfen giriş sayfasını düzelt")
            .Metin("ASISTAN_GIZLI_ACIKLAMA önce dosyaya bakıyorum")
            .Duzenle("e1", @"C:\p\giris.js").Sonuc("e1", icerik: "ARAC_CIKTISI_GIZLI")
            .Komut("c1", "npm test").Sonuc("c1", icerik: "ARAC_CIKTISI_GIZLI 12 passing")
            .Metin("Bitti. ASISTAN_GIZLI_SON_SOZ", turSonu: true).TurSonu();
        var o = ClaudeOzet(k, 1);
        var json = Json(o);
        foreach (var gizli in new[] { "KULLANICI_GIZLI_CUMLE", "ASISTAN_GIZLI_ACIKLAMA", "ASISTAN_GIZLI_SON_SOZ", "ARAC_CIKTISI_GIZLI", "CIKTI_SIZMAMALI", "YENI_ICERIK_SIZMAMALI", "giriş sayfasını" })
            Assert.DoesNotContain(gizli, json);
        Assert.True(o.BittiDedi);
        Assert.Equal(1, o.DegisenDosyaSayisi);
    }

    // ---------------- Codex ----------------

    [Fact]
    public void Codex_TamAkis_DegisiklikDenemeBitti()
    {
        var k = new Codex().Olay("task_started").Insan("KULLANICI_GIZLI_CUMLE").Degisiklik(@"C:\GizliKlasor\a.rs", @"C:\GizliKlasor\b.rs")
            .Komut("cargo build", 0).Komut("cargo test --all", 0).Metin("Bitti, testler geçti. ASISTAN_GIZLI").TurBitti("Bitti, testler geçti. ASISTAN_GIZLI");
        var o = Ozet("codex", Yaz(k.Satirlar, k.SonAn, "rollout-a.jsonl"), k.SonAn.AddMinutes(5));
        Assert.Equal("yuksek", o.Guven);
        Assert.Equal(2, o.DegisenDosyaSayisi);
        Assert.Equal(2, o.KomutSayisi);
        Assert.Equal("cargo test", o.SonDeneme!.KomutTuru);
        Assert.Equal(DenemeSonuc.Gecti, o.SonDeneme.Sonuc);
        Assert.True(o.BittiDedi);
        Assert.True(o.SonDegisiklikSonrasiDeneme);
        Assert.Equal("2 dosyayı değiştirdi (a.rs ve b.rs), 2 komut çalıştırdı.", o.NeYapti);
        Assert.StartsWith("Bitti dedi. Son denemesi geçti (cargo test,", o.DenediMi);
        Assert.Equal("Bitti, bakmanı bekliyor.", o.SendenBeklenen);
        var json = Json(o);
        Assert.DoesNotContain("GIZLI", json);
        Assert.DoesNotContain("CIKTI_SIZMAMALI", json);
    }

    [Fact]
    public void Codex_DenemeKaldi()
    {
        var k = new Codex().Insan("x").Komut("pytest -x tests", 1);
        var o = Ozet("codex", Yaz(k.Satirlar, k.SonAn, "rollout-b.jsonl"), k.SonAn.AddMinutes(1));
        Assert.Equal(DenemeSonuc.Kaldi, o.SonDeneme!.Sonuc);
        Assert.Equal(1, o.SonDeneme.CikisKodu);
        Assert.StartsWith("Henüz bitti demedi. Son denemesi hata verdi (pytest, 1 dk önce)", o.DenediMi);
    }

    [Fact]
    public void Codex_OnayIstegi_IzinVeSoru()
    {
        var k = new Codex().Insan("x").Olay("exec_approval_request");
        var o = Ozet("codex", Yaz(k.Satirlar, k.SonAn, "rollout-c.jsonl"), k.SonAn.AddMinutes(4));
        Assert.Equal(Bekleyenler.Izin, o.Bekleyen);
        Assert.Equal("İzin bekliyor: bir komut çalıştırmak istiyor.", o.SendenBeklenen);

        k.Komut("npm run lint", 0);
        var sonra = Ozet("codex", Yaz(k.Satirlar, k.SonAn, "rollout-c2.jsonl"), k.SonAn.AddMinutes(1));
        Assert.NotEqual(Bekleyenler.Izin, sonra.Bekleyen);

        var s = new Codex().Insan("x").Olay("request_user_input");
        Assert.Equal(Bekleyenler.Soru, Ozet("codex", Yaz(s.Satirlar, s.SonAn, "rollout-d.jsonl"), s.SonAn.AddMinutes(2)).Bekleyen);
    }

    [Fact]
    public void Codex_EskiSurum_KomutSonuOlayi()
    {
        var k = new Codex().EskiKomutSonu("go test ./...", 0).EskiKomutSonu("ls", 0);
        var o = Ozet("codex", Yaz(k.Satirlar, k.SonAn, "rollout-e.jsonl"), k.SonAn.AddMinutes(1));
        Assert.Equal(2, o.KomutSayisi);
        Assert.Equal("go test", o.SonDeneme!.KomutTuru);
        Assert.Equal(DenemeSonuc.Gecti, o.SonDeneme.Sonuc);
    }

    // ---------------- kurallar ve bitti iddiası ----------------

    [Theory]
    [InlineData("npm test", "npm test")]
    [InlineData("cd urun && npm run test:unit 2>&1 | tail -5", "npm test")]
    [InlineData("AGIR_ANAHTAR=x bash /d/agir.sh etiket \"/d/dotnet10/dotnet.exe\" test Soz.sln --artifacts-path _g", "dotnet test")]
    [InlineData("python -m pytest -q tests/", "pytest")]
    [InlineData("pytest", "pytest")]
    [InlineData("go test ./...", "go test")]
    [InlineData("cargo nextest run", "cargo test")]
    [InlineData("npx vitest run", "vitest")]
    [InlineData("npx jest --ci", "jest")]
    [InlineData("npx playwright test e2e/", "playwright test")]
    [InlineData("node --test test/", "node --test")]
    [InlineData("pnpm test", "pnpm test")]
    [InlineData("grep -rn pytest .", null)]
    [InlineData("cat jest.config.js", null)]
    [InlineData("npm install", null)]
    [InlineData("npm run build", null)]
    [InlineData("git log --grep test", null)]
    [InlineData("", null)]
    public void DenemeKurallari_Tanir(string komut, string? beklenen) => Assert.Equal(beklenen, DenemeKurallari.Tani(komut));

    [Theory]
    [InlineData("Bitti, testler geçti.", true)]
    [InlineData("Tamamlandı.", true)]
    [InlineData("Done. All tests pass.", true)]
    [InlineData("Düzelttim, artık çalışıyor.", true)]
    [InlineData("Bitti mi diye kontrol ediyorum", false)]
    [InlineData("Şimdi testleri koşuyorum, bitti:", false)]
    [InlineData("Tamam, dosyaya bakıyorum.", false)]
    [InlineData("Is it done?", false)]
    public void BittiIddiasi_Taninir(string metin, bool beklenen) => Assert.Equal(beklenen, BittiIddiasi.Var(metin));

    // ---------------- önbellek ve kaldığı yerden okuma ----------------

    [Fact]
    public void Onbellek_DosyaBuyuyunceYalnizYeniKisim_KisalincaBastan()
    {
        var k = new Claude().Insan("x").Komut("c1", "ls").Sonuc("c1");
        var yol = Yaz(k.Satirlar, k.SonAn);
        var s = new OturumOzetleri.OturumSatiri(9, "claude", yol, new OzetIpuclari());
        Assert.Equal(1, OturumOzetleri.Hesapla(s, k.SonAn.AddMinutes(1), null)!.KomutSayisi);
        var (b1, _, _, _) = OturumOzetleri.Birikim("claude", yol, null);
        var ilkOfset = b1!.Offset;

        k.Satirlar.Clear();
        k.Komut("c2", "npm test").Sonuc("c2");
        File.AppendAllText(yol, string.Join("\n", k.Satirlar) + "\n");
        File.SetLastWriteTimeUtc(yol, k.SonAn);
        var o2 = OturumOzetleri.Hesapla(s, k.SonAn.AddMinutes(1), null)!;
        Assert.Equal(2, o2.KomutSayisi);
        Assert.Equal("npm test", o2.SonDeneme!.KomutTuru);
        var (b2, _, _, _) = OturumOzetleri.Birikim("claude", yol, null);
        Assert.Same(b1, b2);
        Assert.True(b2!.Offset > ilkOfset);

        // Okuma sınırı: okunmamış kısım sınırdan büyükse hesaplamaz (liste arka planda ısıtır).
        File.AppendAllText(yol, new string(' ', 10) + "\n");
        File.SetLastWriteTimeUtc(yol, k.SonAn.AddSeconds(1));
        Assert.Null(OturumOzetleri.Hesapla(s, k.SonAn.AddMinutes(1), okumaSiniri: 2));

        // Dosya kısaldı: baştan okunur.
        File.WriteAllText(yol, new Claude().Insan("y").Satirlar[0] + "\n");
        File.SetLastWriteTimeUtc(yol, k.SonAn.AddMinutes(2));
        Assert.Equal(0, OturumOzetleri.Hesapla(s, k.SonAn.AddMinutes(3), null)!.KomutSayisi);
    }

    // ---------------- veritabanı ve uç nokta ----------------

    private static string ClaudeOturumuYaz(TestEnv env, string kimlik, Claude k)
    {
        var dir = Path.Combine(env.ClaudeRoot, "C--GizliKlasor-proje");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, kimlik + ".jsonl");
        File.WriteAllText(p, string.Join("\n", k.Satirlar) + "\n");
        File.SetLastWriteTimeUtc(p, k.SonAn);
        return p;
    }

    [Fact]
    public void Veritabani_OturumKimligiyle_VeKancaIsaretiyle()
    {
        using var env = new TestEnv(copySamples: false);
        env.Svc.PutSettings(new JsonObject { ["claudeRoot"] = env.ClaudeRoot, ["codexRoot"] = env.CodexRoot });
        OturumOzetleri.OnbellegiBosalt();
        var k = new Claude().Insan("KULLANICI_GIZLI_CUMLE").Komut("c1", "rm -rf build");
        ClaudeOturumuYaz(env, "11111111-2222-3333-4444-555555555555", k);
        env.Import();
        using var c = env.Svc.Db.Open();
        var id = Convert.ToInt64(c.Scalar("SELECT id FROM sessions WHERE external_id='11111111-2222-3333-4444-555555555555'"));
        c.Exec("UPDATE sessions SET needs_user_kind='permission_prompt', needs_user_since=@s WHERE id=@id", null, ("@s", k.SonAn.AddSeconds(3).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")), ("@id", id));
        var o = OturumOzetleri.Getir(c, id, k.SonAn.AddMinutes(2))!;
        Assert.Equal(id, o.OturumId);
        Assert.Equal(Bekleyenler.Izin, o.Bekleyen);
        Assert.Equal("İzin bekliyor: bir komut çalıştırmak istiyor.", o.SendenBeklenen);
        Assert.Null(OturumOzetleri.Getir(c, 987654));
        var dis = OturumOzetleri.GetirDisKimlik(c, "claude", "11111111-2222-3333-4444-555555555555", k.SonAn.AddMinutes(2));
        Assert.Equal(id, dis!.OturumId);
        Assert.Null(OturumOzetleri.GetirDisKimlik(c, "claude", "yok-boyle-oturum"));
        // Kullanıcının sözü özete girmez.
        Assert.DoesNotContain("KULLANICI_GIZLI_CUMLE", Json(o));
    }

    [Fact]
    public async Task UcNokta_OzetVeListedeKisaOzet()
    {
        using var env = new TestEnv(copySamples: false);
        env.Svc.PutSettings(new JsonObject { ["claudeRoot"] = env.ClaudeRoot, ["codexRoot"] = env.CodexRoot });
        OturumOzetleri.OnbellegiBosalt();
        var k = new Claude().Insan("KULLANICI_GIZLI_CUMLE").Duzenle("e1", @"C:\GizliKlasor\proje\giris.js").Sonuc("e1")
            .Komut("c1", "npm test").Sonuc("c1").Metin("Bitti. ASISTAN_GIZLI", turSonu: true).TurSonu();
        ClaudeOturumuYaz(env, "aaaaaaaa-2222-3333-4444-555555555555", k);
        env.Import();
        Time.UtcNow = () => k.SonAn.AddMinutes(3);
        long id;
        using (var c = env.Svc.Db.Open()) id = Convert.ToInt64(c.Scalar("SELECT id FROM sessions WHERE external_id='aaaaaaaa-2222-3333-4444-555555555555'"));

        await using var h = await SozServer.StartAsync(env.DataDir, 49577, autoScan: false);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var o = JsonNode.Parse(await http.GetStringAsync($"/api/sessions/{id}/ozet"))!.AsObject();
        foreach (var alan in new[] { "neYapti", "denediMi", "sendenBeklenen", "degisenDosyaSayisi", "dosyaAdlari", "komutSayisi", "sonDeneme", "bittiDedi", "bittiSonrasiDeneme", "bekleyen", "guven" })
            Assert.True(o.ContainsKey(alan), alan);
        Assert.Equal("1 dosyayı değiştirdi (giris.js), 1 komut çalıştırdı.", (string?)o["neYapti"]);
        Assert.Equal("gecti", (string?)o["sonDeneme"]!["sonuc"]);
        Assert.Equal("inceleme", (string?)o["bekleyen"]);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/sessions/987654/ozet")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/sessions/ozet")).StatusCode);
        var dis = JsonNode.Parse(await http.GetStringAsync("/api/sessions/ozet?tool=claude&sessionId=aaaaaaaa-2222-3333-4444-555555555555"))!;
        Assert.Equal(id, (long)dis["oturumId"]!);

        var liste = JsonNode.Parse(await http.GetStringAsync("/api/sessions?hours=48&includeStale=true"))!.AsArray();
        var oge = liste.Single(x => (long)x!["id"]! == id)!;
        Assert.Equal("Bitti, bakmanı bekliyor.", (string?)oge["ozet"]!["sendenBeklenen"]);
        var ham = await http.GetStringAsync($"/api/sessions/{id}/ozet");
        Assert.DoesNotContain("GIZLI", ham);
    }
}
