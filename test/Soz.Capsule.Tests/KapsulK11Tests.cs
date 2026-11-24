using System.Text.Json;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>
/// k11: soru sormadan durmuş oturum "bitti" değil "sırayı sana verdi" (sunucu nedeni session_turn_end), eski /api/now yolunda da "durdu";
/// Söz'ün deneyemediği öğede "dene" yönlendirmesi yok; kapsül ve tepsi metinlerinde "ajan", "kanıt", "ölçüt" yok.
/// </summary>
public class KapsulK11Tests
{
    private static readonly DateTimeOffset Simdi = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static string Kok()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
    }
    private static string Oku(params string[] yol) => File.ReadAllText(Path.Combine(new[] { Kok() }.Concat(yol).ToArray()));

    private static List<Is> Attention(string json) { using var d = JsonDocument.Parse(json); return SiraBendeCevabi.Ayristir(d.RootElement)!.Isler.ToList(); }
    private static List<Is> Now(string json) { using var d = JsonDocument.Parse(json); return MotorCevabi.NowAyristir(d.RootElement); }
    private static KapsulMantik Yeni() { var m = new KapsulMantik(new Ayar(), s => s.Length * 7.0); m.BostaSifirla(0); return m; }

    private const string DurduJson = """
        { "items": [ { "id": "uncertain:ses:9:1", "kind": "uncertain", "reason": "session_turn_end", "priority": 5, "title": "Rapor oturumu", "signal": false, "at": "2026-11-23T11:48:00Z" } ], "running": [] }
        """;

    // ------------------------------------------------------------ 1. session_turn_end: "sırayı sana verdi"
    [Fact]
    public void Turn_end_nedeni_sirayi_sana_verdi_der_bitti_demez()
    {
        var i = Attention(DurduJson).Single();
        Assert.Equal("sırayı sana verdi", i.NedenMetni);
        Assert.DoesNotContain("bitti", i.NedenMetni);
        Assert.Equal("sırayı sana verdi · 12 dk", i.SagYazi(Simdi));                 // satırdaki biçim: neden · süre
        Assert.Equal("sırayı sana verdi", new Is("x", IsTuru.Belirsiz, "t", "t", Neden: "session_turn_end").SagYazi(Simdi));   // süre bilinmiyorsa yalnız neden
    }

    [Fact]
    public void Turn_end_oturumu_yesil_bitti_degil_belirsiz_sinifinda_ve_uyandirmaz()
    {
        var i = Attention(DurduJson).Single();
        Assert.True(i.Durdu);
        Assert.Equal(IsTuru.Belirsiz, i.Tur); Assert.Equal(DurumSinifi.Belirsiz, i.Sinif);
        Assert.NotEqual("ok", i.Glif); Assert.Equal("unk", i.Glif);                  // yeşil tik değil
        Assert.False(i.Bekliyor); Assert.False(i.Sinyal);
        Assert.Equal("durdu", i.Soz); Assert.Equal("Sırayı sana verdi", i.Etiket);
        Assert.Equal(5, i.Oncelik);
    }

    [Fact]
    public void Turn_end_on_bakista_ve_hapta_tutarli_gorunur()
    {
        var i = Attention(DurduJson).Single();
        var l = OnBakisIcerigi.Kur(Array.Empty<Is>(), Array.Empty<Is>(), new[] { i }, null, Simdi);
        var satir = l.Single(s => s.Kimlik == i.Id);
        Assert.Equal("sırayı sana verdi · 12 dk", satir.Sag);
        Assert.NotEqual(DurumSinifi.Bitti, satir.Sinif);
        Assert.DoesNotContain(l, s => s.Baslik && s.Metin.StartsWith("Seni bekleyenler"));   // seni bekleyen sayılmaz
        var m = Yeni();
        m.IsleriGuncelle(new[] { i }, 0);
        Assert.Equal("durdu", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { i, i with { Id = "uncertain:ses:9:2" } }, 0);
        Assert.Equal("2 durdu", m.IcerikHesapla(0)!.Yazi);
    }

    // ------------------------------------------------------------ 2. eski /api/now yedek yolu
    [Fact]
    public void Kapsul_once_api_attention_dener_yalniz_404te_api_nowa_duser()
    {
        var motor = Oku("src", "Soz.Capsule", "Pencere", "Motor.cs");
        var a = motor.IndexOf("\"/api/attention\"", StringComparison.Ordinal); var n = motor.IndexOf("\"/api/now\"", StringComparison.Ordinal);
        Assert.True(a > 0 && n > a);
        Assert.Contains("durum != 404", motor);                                       // yalnız 404'te (eski sunucu) yedek yol
    }

    [Fact]
    public void Eski_now_yolunda_soru_isaretsiz_durmus_oturum_bitti_degil_durdu()
    {
        var l = Now("""{"needsYou":[{"sessionId":1,"title":"Boş neden"},{"sessionId":2,"title":"Bekliyor","reason":"waiting"},{"sessionId":3,"title":"Bildirim","reason":"notification"}]}""");
        Assert.All(l, i => { Assert.Equal(IsTuru.Belirsiz, i.Tur); Assert.True(i.Durdu); Assert.False(i.Sinyal); Assert.False(i.Bekliyor); Assert.Equal("sırayı sana verdi", i.NedenMetni); });
        Assert.DoesNotContain(l, i => i.Tur == IsTuru.Bitti);
    }

    [Fact]
    public void Eski_now_yolunda_soru_izin_hata_ve_acik_bitti_sozu_eskisi_gibi_kalir()
    {
        var l = Now("""
            {"needsYou":[{"sessionId":1,"title":"S","question":"Hangisi?"},{"sessionId":2,"title":"I","reason":"permission_prompt"},
                         {"sessionId":3,"title":"H","reason":"session_failed"},{"sessionId":4,"title":"B","reason":"session_done"},{"sessionId":5,"title":"T","reason":"stalled"}]}
            """);
        Assert.Equal(IsTuru.Soru, l.Single(i => i.Id == "o:1").Tur);
        Assert.Equal(IsTuru.Karar, l.Single(i => i.Id == "o:2").Tur);
        Assert.Equal(IsTuru.Kaldi, l.Single(i => i.Id == "o:3").Tur);
        Assert.Equal(IsTuru.Bitti, l.Single(i => i.Id == "o:4").Tur);                  // açıkça "bitti" denmiş: Bitti
        Assert.False(l.Single(i => i.Id == "o:5").Durdu);                              // takılan oturum hâlâ "uzun süredir sessiz"
    }

    [Fact]
    public void Eski_now_durdu_satiri_bekleyen_listesine_girmez_ama_gorunur()
    {
        var m = Yeni();
        m.IsleriGuncelle(Now("""{"needsYou":[{"sessionId":9,"title":"Rapor"}]}""").ToArray(), 0);
        Assert.Empty(m.Bekleyenler);
        Assert.Equal("o:9", m.Belirsizler.Single().Id);
    }

    // ------------------------------------------------------------ 3. sozNotu / canProve:false: "dene" yönlendirmesi yok
    [Fact]
    public void Soz_notu_olan_ogede_dene_sunulmaz_ve_kontrol_et_denmez()
    {
        var i = Attention("""{"items":[{"id":"done:ses:1:1","kind":"done","reason":"session_done","priority":4,"title":"Oturum","signal":false,"sozNotu":"Bu projede neyle sınayacağımı bilmiyorum."}],"running":[]}""").Single();
        Assert.False(i.DeneSunulur);
        Assert.Equal("bitti dedi, denenemedi", i.NedenMetni);
        Assert.DoesNotContain("kontrol et", i.NedenMetni);
    }

    [Fact]
    public void CanProve_false_olan_ogede_dene_sunulmaz_bitti_yerine_bitti_dedi_der()
    {
        var i = Attention("""{"items":[{"id":"done:ses:1:1","kind":"done","reason":"session_done","priority":4,"title":"Oturum","signal":true,"canProve":false,"canProveReason":"no_checks"}],"running":[]}""").Single();
        Assert.False(i.DeneSunulur); Assert.True(i.BittiDedi);
        Assert.Equal("bitti dedi", i.Soz); Assert.Equal("Bitti dedi, denenemedi", i.Etiket);
        Assert.Equal("bitti dedi, denenemedi · 12 dk", (i with { Baslangic = Simdi.AddMinutes(-12) }).SagYazi(Simdi));
    }

    [Fact]
    public void CanProve_true_ya_da_alan_yoksa_eski_gibi_kontrol_et_der()
    {
        var liste = Attention("""{"items":[{"id":"a","kind":"done","reason":"session_done","priority":4,"title":"A","canProve":true},{"id":"b","kind":"done","reason":"passed","priority":4,"title":"B"}],"running":[]}""");
        Assert.All(liste, i => { Assert.True(i.DeneSunulur); Assert.False(i.BittiDedi); Assert.Equal("bitti, kontrol et", i.NedenMetni); Assert.Equal("bitti", i.Soz); });
        Assert.Null(liste[1].DenenebilirMi);                                          // eski sunucu: bilgi yok, uydurulmaz
    }

    [Fact]
    public void Soz_notu_bos_metin_dene_sunulmasini_engellemez()
    {
        var i = Attention("""{"items":[{"id":"a","kind":"done","reason":"session_done","priority":4,"title":"A","sozNotu":"  "}],"running":[]}""").Single();
        Assert.True(i.DeneSunulur);
    }

    // ------------------------------------------------------------ 5. kapsül ve tepsi metinlerinde "ajan", "kanıt", "ölçüt" yok
    [Fact]
    public void Her_neden_ve_tur_icin_kapsul_metinlerinde_ajan_kanit_olcut_gecmez()
    {
        var nedenler = new string?[] { null, "approval", "session_permission", "question", "session_question", "session_input", "session_waiting", "decision", "proof_failed", "start_failed", "session_failed",
            "passed", "session_done", "no_change", "run_lost", "session_quiet", "stalled", "baglanti", "unproven", "session_turn_end", "failure" };
        foreach (var tur in Enum.GetValues<IsTuru>())
            foreach (var n in nedenler)
                foreach (var i in new[] { new Is("x", tur, "t", "t", Neden: n), new Is("x", tur, "t", "t", Neden: n, DenenebilirMi: false) })
                    foreach (var metin in new[] { i.NedenMetni, i.Soz, i.Etiket })
                        foreach (var y in new[] { "ajan", "kanıt", "ölçüt" })
                            Assert.DoesNotContain(y, metin, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eski_now_yedek_basliklari_yardimci_der_ajan_demez()
    {
        var l = Now("""{"needsYou":[{"sessionId":1}],"uncertain":[{"sessionId":2}]}""");
        Assert.Equal("Yardımcı seni bekliyor", l.Single(i => i.Id == "o:1").Baslik);
        Assert.Equal("Yardımcı", l.Single(i => i.Id == "o:2").Baslik);
        var a = Attention("""{"items":[{"id":"z","kind":"ask"}],"running":[]}""").Single();
        Assert.Equal("Yardımcı", a.Kisa);
    }

    [Fact]
    public void Tepsi_ve_kabuk_metinlerinde_ajan_kanit_olcut_sozu_kalmadi()
    {
        var strings = Oku("src", "Soz.Shell", "Strings.cs");
        var satir = strings.Split('\n').Single(s => s.Contains("dlg.deletedata.text"));
        var tr = satir[..satir.IndexOf("\", \"When", StringComparison.Ordinal)];       // yalnız Türkçe metin
        Assert.Contains("deneme sonuçların", tr);
        foreach (var y in new[] { "ajan", "kanıt", "ölçüt" }) Assert.DoesNotContain(y, tr, StringComparison.OrdinalIgnoreCase);
        var veri = Oku("src", "Soz.Shell", "VeriKorumasi.cs");
        Assert.Contains("deneme sonuçların ve ayarların duruyor", veri);
        Assert.DoesNotContain("kanıtların", veri);
    }
}
