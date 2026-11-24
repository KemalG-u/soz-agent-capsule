using System.Text.Json;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>"Sıra bende" akışının kapsül tarafı (d2c): cevabı ayrıştırma, "belirsiz" durum, dikkat bütçesi sinyali, bağlantı kararı.</summary>
public class SiraBendeTests
{
    private static List<Is> Ayristir(string json, out SiraBendeCevap? cevap)
    {
        using var d = JsonDocument.Parse(json);
        cevap = SiraBendeCevabi.Ayristir(d.RootElement);
        return cevap?.Isler.ToList() ?? new();
    }

    private const string Ornek = """
        { "rev": 812, "now": "2026-11-11T20:01:02.123Z", "sentence": "1 soru, 1 bitti seni bekliyor. 2 iş çalışıyor.",
          "counts": { "ask": 1, "accept": 1, "blocked": 1, "done": 1, "uncertain": 1, "total": 5, "running": 2 },
          "budget": { "dailyLimit": 3, "usedToday": 2, "suppressedToday": 1, "day": "2026-11-11" },
          "items": [
            { "id": "ask:job:a:1", "kind": "ask", "title": "Hesap makinesi", "sentence": "x", "signal": true,  "isNew": false },
            { "id": "accept:job:b:1", "kind": "accept", "title": "Veritabanı", "signal": true },
            { "id": "blocked:job:c:1", "kind": "blocked", "title": "Ödeme testi", "signal": false },
            { "id": "done:job:d:1", "kind": "done", "title": "", "sentence": "«Giriş» kanıtıyla bitti.", "signal": true },
            { "id": "uncertain:ses:9:1", "kind": "uncertain", "title": "Oturum", "signal": false },
            { "id": "ask:job:a:1", "kind": "ask", "title": "yinelenen" },
            { "kind": "ask", "title": "kimliksiz" }
          ],
          "running": [ { "jobUid": "u1", "jobId": 7, "title": "Rapor yazımı", "projectName": "p" }, { "jobId": 8, "title": "Derleme" } ] }
        """;

    [Fact]
    public void Cevap_turleri_eslesir_calisanlar_j_kimligiyle_gelir_yinelenen_ve_kimliksiz_atilir()
    {
        var l = Ayristir(Ornek, out var c);
        Assert.NotNull(c);
        Assert.Equal(812, c!.Rev);
        Assert.Equal(3, c.GunlukHak); Assert.Equal(2, c.Harcanan); Assert.Equal(1, c.Sessiz);
        Assert.Equal(new[] { IsTuru.Soru, IsTuru.Karar, IsTuru.Kaldi, IsTuru.Bitti, IsTuru.Belirsiz, IsTuru.Calisiyor, IsTuru.Calisiyor }, l.Select(x => x.Tur).ToArray());
        Assert.Equal(new[] { "j:7", "j:8" }, l.Where(x => x.Tur == IsTuru.Calisiyor).Select(x => x.Id).ToArray());
        Assert.Equal(new[] { true, true, false, true, false }, l.Take(5).Select(x => x.Sinyal).ToArray());
        Assert.Equal("«Giriş» kanıtıyla bitti.", l[3].Baslik);          // başlık boşsa cümle
        Assert.False(l[4].Bekliyor);                                      // belirsiz bekleyen sayılmaz
        Assert.Equal("unk", l[4].Glif); Assert.Equal(DurumSinifi.Belirsiz, l[4].Sinif); Assert.Equal("belirsiz", l[4].Soz);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{ "error": "Bilinmeyen API ucu." }""")]
    [InlineData("""{ "items": "yok" }""")]
    public void Eski_motor_ya_da_bozuk_cevap_null_verir_cokertmez(string json) { Ayristir(json, out var c); Assert.Null(c); }

    [Fact]
    public void Bilinmeyen_tur_belirsiz_sayilir_tahminle_bitti_denmez()
    {
        var l = Ayristir("""{ "items": [ { "id": "x:1", "kind": "yepyeni", "title": "T", "signal": true } ] }""", out _);
        Assert.Equal(IsTuru.Belirsiz, l[0].Tur);
    }

    // ------------------------------------------------------------ u4: kanıtsız ve değişiklik yok (yeni kanıt ilkesi)
    private const string KanitsizVeDegisiklikYok = """
        { "items": [
            { "id": "uncertain:job:k:1", "kind": "uncertain", "reason": "unproven", "priority": 5, "title": "Deneme yazdı", "signal": false },
            { "id": "done:job:n:1", "kind": "done", "reason": "no_change", "priority": 4, "title": "Şablon", "signal": true },
            { "id": "done:job:p:1", "kind": "done", "reason": "passed", "priority": 4, "title": "Giriş", "signal": true },
            { "id": "uncertain:ses:9:1", "kind": "uncertain", "reason": "session_quiet", "priority": 5, "title": "Oturum", "signal": false }
          ] }
        """;

    [Fact]
    public void Kanitsiz_biten_is_denenmedi_gorunur_belirsiz_degil()
    {
        var l = Ayristir(KanitsizVeDegisiklikYok, out _);
        var k = l[0];
        Assert.Equal(IsTuru.Belirsiz, k.Tur);                     // tahminle bitti ya da kaldı denmez: bekleyen sayılmaz, uyandırmaz
        Assert.True(k.Kanitsiz); Assert.False(k.Bekliyor); Assert.False(k.Sinyal);
        Assert.Equal("denenmedi", k.Soz); Assert.Equal("Denenmedi", k.Etiket);
        Assert.False(l[3].Kanitsiz); Assert.Equal("belirsiz", l[3].Soz); Assert.Equal("Durum belirsiz", l[3].Etiket);   // takılan oturum hâlâ belirsiz
    }

    [Fact]
    public void Kanitsiz_hap_yazisi_denenmedi_der_karisiksa_belirsiz_der()
    {
        var m = Yeni();
        var l = Ayristir(KanitsizVeDegisiklikYok, out _);
        var k = l[0];
        m.IsleriGuncelle(new[] { k }, 0);
        Assert.Equal("denenmedi", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { k, k with { Id = "uncertain:job:k:2" } }, 0);
        Assert.Equal("2 denenmedi", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { k, l[3] }, 0);
        Assert.Equal("2 belirsiz", m.IcerikHesapla(0)!.Yazi);
    }

    [Fact]
    public void Degisiklik_yok_sakin_etiketle_gorunur_sunucu_isaret_verse_de_kapsulu_uyandirmaz()
    {
        var l = Ayristir(KanitsizVeDegisiklikYok, out _);
        var n = l[1];
        Assert.True(n.DegisiklikYok);
        Assert.False(n.Sinyal);                                   // sunucu signal true dedi, kapsül yine de uyanmaz
        Assert.Equal("değişiklik yok", n.Soz); Assert.Equal("Değişiklik yok", n.Etiket);
        Assert.Equal(DurumSinifi.Tamam, n.Sinif); Assert.Equal("tam", n.Glif);   // yeşil "bitti" tiki değil
        Assert.Equal(4, n.Oncelik);                               // sunucu önceliği aynen
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { n }, 0);
        Assert.Equal(Seviye.Uyku, m.Sev); Assert.Equal(0, m.DikkatSayaci);
        Assert.Equal("değişiklik yok", m.IcerikHesapla(0)!.Yazi);
    }

    [Fact]
    public void Kanitiyla_biten_is_bitti_olarak_kalir_ve_uyandirir()
    {
        var l = Ayristir(KanitsizVeDegisiklikYok, out _);
        var p = l[2];
        Assert.False(p.DegisiklikYok); Assert.False(p.Kanitsiz);
        Assert.Equal("bitti", p.Soz); Assert.Equal("İş bitti", p.Etiket); Assert.Equal(DurumSinifi.Bitti, p.Sinif); Assert.True(p.Sinyal);
    }

    // ------------------------------------------------------------ belirsiz durum ayrı gösterilir
    private static KapsulMantik Yeni(Action<Ayar>? ayarla = null) { var a = new Ayar(); ayarla?.Invoke(a); var m = new KapsulMantik(a, s => s.Length * 7.0); m.BostaSifirla(0); return m; }
    private static Is Bel(string id = "u:1") => new(id, IsTuru.Belirsiz, "Oturum", "oturum", Sinyal: false);
    private static Is Soru(string id = "a:1", bool sinyal = true) => new(id, IsTuru.Soru, "Sürüm", "sürüm sorusu", sinyal);
    private static Is Bitti(string id = "d:1", bool sinyal = true) => new(id, IsTuru.Bitti, "Giriş", "giriş", sinyal);
    private static Is Calisan(string id = "j:5") => new(id, IsTuru.Calisiyor, "Derleme", "derleme");

    [Fact]
    public void Belirsiz_ayri_gosterilir_bekleyenden_sonra_calisandan_once()
    {
        var m = Yeni();
        m.IsleriGuncelle(new[] { Calisan(), Bel() }, 0);
        var c = m.IcerikHesapla(0)!;
        Assert.Equal("belirsiz", c.Yazi); Assert.Equal("unk", c.Glif); Assert.Equal(DurumSinifi.Belirsiz, c.Sinif); Assert.False(c.Calisiyor);
        m.IsleriGuncelle(new[] { Calisan(), Bel("u:1"), Bel("u:2") }, 0);
        Assert.Equal("2 belirsiz", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { Calisan(), Bel(), Bitti() }, 0);
        Assert.Equal("bitti", m.IcerikHesapla(0)!.Yazi);            // bekleyen önce
        Assert.DoesNotContain(m.Bekleyenler, j => j.Tur == IsTuru.Belirsiz);
        m.IsleriGuncelle(new[] { Calisan() }, 0);
        Assert.Equal("çalışıyor", m.IcerikHesapla(0)!.Yazi);
    }

    [Fact]
    public void Belirsiz_kapsulu_uyandirmaz_ama_rozeti_vardir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { Bel() }, 0);
        Assert.Equal(Seviye.Uyku, m.Sev); Assert.Equal(0, m.DikkatSayaci);
        m.Sev = Seviye.Soluk; m.Durak = Durak.Orb;
        var g = m.GorunumHesapla(0);
        Assert.True(g.Rozet);
        Assert.Equal(DurumSinifi.Belirsiz, g.Icerik!.Sinif);
    }

    // ------------------------------------------------------------ dikkat bütçesi sinyali
    [Fact]
    public void Sinyali_olmayan_yeni_bekleyen_listeye_girer_ama_uyandirmaz()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { Bitti("d:1", sinyal: false) }, 0);
        Assert.Equal(Seviye.Uyku, m.Sev);
        Assert.Equal(0, m.DikkatSayaci); Assert.Equal(1, m.SessizSayaci);
        Assert.Equal("bitti", m.IcerikHesapla(0)!.Yazi);              // yine de listede ve içerikte görünür
        m.IsleriGuncelle(new[] { Bitti("d:1", false), Soru("a:2", true) }, 10);
        Assert.Equal(Seviye.Belirgin, m.Sev); Assert.Equal(1, m.DikkatSayaci);
    }

    [Fact]
    public void Kapsul_yeni_acilinca_ilk_liste_sessizdir_sonraki_yeni_oge_uyandirir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { Soru("a:1"), Bitti("d:1") }, 0, sessiz: true, kesin: true);
        Assert.Equal(Seviye.Uyku, m.Sev); Assert.Equal(0, m.DikkatSayaci);
        Assert.Equal("1 soru, 1 bitti", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { Soru("a:1"), Bitti("d:1"), Soru("a:2") }, 5, kesin: true);
        Assert.Equal(Seviye.Belirgin, m.Sev); Assert.Equal(1, m.DikkatSayaci);
    }

    [Fact]
    public void Motor_kopup_geri_gelince_ayni_oge_ikinci_kez_uyandirmaz_yeni_oge_uyandirir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { Soru("a:1") }, 0, kesin: true);
        Assert.Equal(1, m.DikkatSayaci);
        m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(Array.Empty<Is>(), 100000, kesin: false);   // motor yok: nötr
        m.IsleriGuncelle(new[] { Soru("a:1") }, 110000, kesin: true); // motor geri geldi, aynı öğe
        Assert.Equal(Seviye.Uyku, m.Sev); Assert.Equal(1, m.DikkatSayaci);
        m.IsleriGuncelle(new[] { Soru("a:1"), Bitti("d:9") }, 120000, kesin: true); // kopukken biten yeni öğe
        Assert.Equal(Seviye.Belirgin, m.Sev); Assert.Equal(2, m.DikkatSayaci);
    }

    [Fact]
    public void Kopuklukta_gelen_belirsiz_satir_sonrasinda_ayni_oge_yine_susar()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { Bitti("d:1") }, 0, kesin: true);
        m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { BaglantiKarar.BelirsizSatir() }, 6000, kesin: false);
        Assert.Equal("ulaşamıyorum", m.IcerikHesapla(6000)!.Yazi);   // iç durum adı değil, ne olduğunu söyleyen sözcük
        Assert.Empty(m.Bekleyenler);
        m.IsleriGuncelle(new[] { Bitti("d:1") }, 9000, kesin: true);
        Assert.Equal(Seviye.Uyku, m.Sev); Assert.Equal(1, m.DikkatSayaci);
    }

    [Fact]
    public void Eski_motorun_kimlikli_oge_gidip_gelirse_kesin_listeyle_yeniden_uyandirir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { new Is("j:7", IsTuru.Soru, "S", "s") }, 0, kesin: true);
        m.IsleriGuncelle(new[] { new Is("j:7", IsTuru.Calisiyor, "S", "s") }, 1, kesin: true);   // cevaplandı, çalışıyor
        m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { new Is("j:7", IsTuru.Soru, "S", "s") }, 2, kesin: true);        // yine sordu
        Assert.Equal(2, m.DikkatSayaci); Assert.Equal(Seviye.Belirgin, m.Sev);
    }

    [Fact]
    public void Eski_davranis_korunur_varsayilan_sinyal_acik_ve_ilk_liste_uyandirir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { new Is("j:1", IsTuru.Bitti, "B", "b") }, 0);
        Assert.Equal(Seviye.Belirgin, m.Sev); Assert.Equal(1, m.DikkatSayaci);
    }

    // ------------------------------------------------------------ bağlantı kararı
    [Theory]
    [InlineData(false, false, 0, "Notr")]
    [InlineData(false, true, 0, "Notr")]        // kayıt yok (Söz kapalı): bağlı görünse de nötr
    [InlineData(true, true, 0, "Liste")]
    [InlineData(true, false, 0, "Liste")]       // yeni koptu: titreme yok, son liste
    [InlineData(true, false, 4999, "Liste")]
    [InlineData(true, false, 5000, "Belirsiz")] // 5 sn: eski durum doğru gibi gösterilmez
    [InlineData(true, false, 29999, "Belirsiz")]
    [InlineData(true, false, 30000, "Belirsiz")]    // 30 sn sonra da nötr olmaz: sessizlik "hiç iş yok" gibi okunurdu
    [InlineData(true, false, 3600000, "Belirsiz")]  // bir saat sonra da "bilgiye ulaşamıyorum"
    public void Baglanti_karari(bool kayitVar, bool bagli, long koptuMs, string beklenen)
        => Assert.Equal(beklenen, BaglantiKarar.Karar(kayitVar, bagli, koptuMs).ToString());

    [Fact]
    public void Yeniden_deneme_yarim_saniyeden_baslar_ikiye_katlanir_bes_saniyede_durur()
    {
        Assert.Equal(new[] { 500, 1000, 2000, 4000, 5000, 5000, 5000 }, Enumerable.Range(0, 7).Select(BaglantiKarar.YenidenDenemeMs).ToArray());
        Assert.Equal(500, BaglantiKarar.YenidenDenemeMs(-3));
        Assert.Equal(5000, BaglantiKarar.YenidenDenemeMs(int.MaxValue));
    }

    [Fact]
    public void Belirsiz_satir_sessizdir_ve_baglanti_kimligi_sabittir()
    {
        var s = BaglantiKarar.BelirsizSatir();
        Assert.Equal(IsTuru.Belirsiz, s.Tur); Assert.False(s.Sinyal); Assert.False(s.Bekliyor);
        Assert.Equal("m:baglanti", s.Id);
    }
}
