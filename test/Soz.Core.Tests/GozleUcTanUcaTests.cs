using Soz.Core.Data;
using Soz.Core.Onboarding;
using Soz.Core.Registry;
using Soz.Core.Sessions;
using Xunit;
using static Soz.Core.Tests.SahteAracVeritabani;

namespace Soz.Core.Tests;

/// <summary>
/// d2m madde 8: gözle bağlayıcıları (Hermes) KAPALI gelir, kişi Tanışma'da kabul edince açılır ve içe aktarma kartı gösterir.
/// Her şey sentetik: sahte ev klasörü (SahteEv) altında, sahte Hermes veritabanıyla. Gerçek ev klasörü, gerçek hermes-home ve gerçek ajan araçları açılmaz.
/// </summary>
public class GozleUcTanUcaTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static void HermesVeriYaz(string home)
    {
        var db = Path.Combine(home, ".hermes", "state.db");
        File.Delete(db);                              // SahteEv'in yer tutucu dosyası (SQLite değil)
        using (var c = Ac(db, wal: false))
        {
            HermesKur(c);
            HermesOturum(c, "h-bekliyor", "2026-11-10T11:38:00Z", cwd: "E:\\sahte\\proje-a", baslik: "Fatura düzeni", sonEtkinlik: "2026-11-10T11:42:00Z", mesaj: 3);
            HermesMesaj(c, "h-bekliyor", "user", "Faturaları klasöre göre ayır", "2026-11-10T11:40:00Z");
            HermesMesaj(c, "h-bekliyor", "assistant", "Hazır. Hangisini istersin?", "2026-11-10T11:42:00Z", bitisNedeni: "stop");
            HermesOturum(c, "h-cron", "2026-11-10T08:00:00Z", bitis: "2026-11-10T08:05:00Z", neden: "completed", kaynak: "cron", cwd: "E:\\sahte\\proje-f", mesaj: 2);
            HermesMesaj(c, "h-cron", "user", "OTOMASYON-GIRDISI", "2026-11-10T08:00:30Z");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(home, ".hermes", "runtime", "active_sessions.json"));    // canlılık dosyası yok: hiçbir oturum "çalışıyor" denmez
    }

    private static List<SessionCard> Kartlar(SozDb db)
    {
        using var c = db.Open();
        return SessionQueries.Cards(c, 48, true, new StateThresholds(), Now, humanOnly: false).Where(k => k.Tool == "hermes").ToList();
    }

    [Fact]
    public void Hermes_KapaliGelir_KabulEdinceAcilir_IceAktarinca_KartGorunur_DurumDogru()
    {
        using var ev = new SahteEv("d2m-gozle");
        HermesVeriYaz(ev.Home);
        var roots = ev.Ctx().Roots;
        var dbDosyasi = Path.Combine(ev.Home, ".hermes", "state.db");
        var once = Ozet(dbDosyasi);

        // 1) Kayıt defterinde Hermes bağlayıcısı kurulu araçlar engele takılmasın diye açık gelir, ancak kayıt klasörü onayı olmadan içe aktarma okuma yapmaz
        var load = RegistryHost.Load(ev.Db);
        Assert.Equal(RegistryStates.Enabled, load.Get("connector:hermes")!.State);
        Assert.Contains(Soz.Core.Agents.AgentRegistry.SpecsFrom(load), s => s.Id == "hermes");
        Assert.Contains(Soz.Core.Agents.AgentRegistry.DefinitionsFrom(load), s => s.Id == "hermes");

        // 2) Tanışma keşfi: Hermes kayıt klasörü bulundu, ama kişi kabul etmedi → içe aktarma hiçbir dosyasına bakmaz
        ev.Scan();
        var oneri = Assert.Single(ev.Svc.View()["groups"]!.AsArray().SelectMany(g => g!["items"]!.AsArray()), i => (string?)i!["id"] == "records:hermes")!;
        Assert.NotEqual("accepted", (string?)oneri["state"]);
        var kapali = Importer.Run(ev.Db, new ImportOptions { SkipClaude = true, SkipCodex = true, Roots = roots });
        Assert.Empty(kapali.Sources);
        Assert.Empty(kapali.ObserveNotes);                                   // kabul edilmemiş araç listeye bile girmez (not yalnız açık bayrakla istenince çıkar, ikinci test)
        Assert.Empty(Kartlar(ev.Db));

        // 3) Kişi kabul eder: bağlayıcı onay özetiyle etkinleşir (kabul edilen yoldan; varsayılan gerçek klasörden değil)
        var kabul = ev.Svc.Accept("records:hermes");
        Assert.Equal("enabled", (string?)kabul["connector"]!["state"]);
        Assert.Equal(RegistryStates.Enabled, RegistryHost.Load(ev.Db).Get("connector:hermes")!.State);
        Assert.Contains(Soz.Core.Agents.AgentRegistry.SpecsFrom(RegistryHost.Load(ev.Db)), s => s.Id == "hermes");

        // 4) İçe aktarma: yalnız kabul edilen kökten okunur; kart görünür, durum doğru, otomasyon insan listesinde değil
        var ac = Importer.Run(ev.Db, new ImportOptions { SkipClaude = true, SkipCodex = true, Roots = roots });
        Assert.Empty(ac.ObserveNotes);
        var rapor = Assert.Single(ac.Sources);
        Assert.True(rapor.Found);
        Assert.Equal(2, rapor.FilesRead);                                    // iki oturum
        var kartlar = Kartlar(ev.Db);
        var bekliyor = Assert.Single(kartlar, k => k.ExternalId == "h-bekliyor");
        Assert.Equal("Fatura düzeni", bekliyor.Title);
        Assert.Equal("human", bekliyor.Kind);
        Assert.Equal("automation", kartlar.Single(k => k.ExternalId == "h-cron").Kind);
        // oturum asistanın son sözüyle (soru) bitmiş: durum "bekliyor" (sıra kişide); canlı süreç dosyası yok, bu yüzden hiçbiri "çalışıyor" değil
        Assert.DoesNotContain(kartlar, k => k.State == "working");
        Assert.Equal("waiting", bekliyor.State);
        // salt okunur: Hermes dosyası değişmedi, yanına dosya açılmadı
        Assert.Equal(once, Ozet(dbDosyasi));
        Assert.False(File.Exists(dbDosyasi + "-wal")); Assert.False(File.Exists(dbDosyasi + "-journal"));
        // insan sözü yalnız insan oturumundan; otomasyon girdisi veritabanına girmedi
        using (var c = ev.Db.Open()) Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM utterances WHERE text LIKE '%OTOMASYON-GIRDISI%'"));

        // 5) Kişi sonradan "bir daha gösterme" derse bağlayıcı kapanır, içe aktarma yine hiçbir şey okumaz
        var kapat = ev.Svc.Dismiss("records:hermes");
        Assert.Equal("disabled", (string?)kapat["connector"]!["state"]);
        var tekrar = Importer.Run(ev.Db, new ImportOptions { SkipClaude = true, SkipCodex = true, Roots = roots });
        Assert.Empty(tekrar.Sources);
    }

    [Fact]
    public void Gozle_AcikBayrakYoluYine_BaglayiciKapaliysaOkumaz()
    {
        using var ev = new SahteEv("d2m-gozle-bayrak");
        HermesVeriYaz(ev.Home);
        RegistryHost.Revoke(ev.Db, "connector:hermes");
        var opt = new ImportOptions { SkipClaude = true, SkipCodex = true, Roots = ev.Ctx().Roots };
        opt.ToolRoots["hermes"] = Path.Combine(ev.Home, ".hermes");
        var s = Importer.Run(ev.Db, opt);
        Assert.Empty(s.Sources);                                             // açık kök verilse de bağlayıcı kapatılmışsa okunmaz
        Assert.Contains(s.ObserveNotes, n => n.Contains("kapalı"));
        opt.ToolRoots["yok-arac"] = ev.Home;
        Assert.Contains(Importer.Run(ev.Db, opt).ObserveNotes, n => n.Contains("kayıt defterinde yok"));
    }
    // d2m madde 7: "emin değilim" (uncertain) her tüketicide ayrı bir sınıf: Şimdi, Sıra bende (uyandırmaz, en sonda) ve sabah özeti.
    [Fact]
    public void Belirsiz_Oturum_Simdi_SiraBende_Ve_Sabah_Ozetinde_AyriSinif_Uyandirmaz()
    {
        using var ev = new SahteEv("d2m-belirsiz");
        var db = Path.Combine(ev.Home, ".hermes", "state.db");
        File.Delete(db);
        using (var c = Ac(db, wal: false))
        {
            HermesKur(c);
            // saat 12:00; son kayıt 11:57: açık araç çağrısı, 3 dk önce (90 sn geçti, takıldı eşiği olan 5 dk dolmadı) = belirsiz
            HermesOturum(c, "h-belirsiz", "2026-11-11T11:50:00Z", cwd: "E:/sahte/proje-c", baslik: "Rapor", sonEtkinlik: "2026-11-11T11:57:00Z", mesaj: 2);
            HermesMesaj(c, "h-belirsiz", "user", "Raporu yaz", "2026-11-11T11:56:00Z");
            HermesMesaj(c, "h-belirsiz", "assistant", null, "2026-11-11T11:57:00Z", aracCagrisi: true, bitisNedeni: "tool_calls");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(ev.Home, ".hermes", "runtime", "active_sessions.json"));
        ev.Scan();
        ev.Svc.Accept("records:hermes");
        Importer.Run(ev.Db, new ImportOptions { SkipClaude = true, SkipCodex = true, Roots = ev.Ctx().Roots });

        var now = new Soz.Core.Jobs.WorkService(ev.Db).Now();
        Assert.Equal(1, now.Counts.Uncertain);
        var u = Assert.Single(now.Uncertain!);
        Assert.Equal("tool_call_pending", u.EndClass);
        Assert.Equal("unknown", u.StateConfidence);
        Assert.DoesNotContain(now.NeedsYou, x => x.SessionId == u.SessionId);          // tahminle "bekliyor" denmez

        var snap = new Soz.Core.Attention.AttentionService(ev.Db).Snapshot();
        var item = Assert.Single(snap.Items, i => i.Kind == "uncertain");
        Assert.Equal("session_unsure", item.Reason);
        Assert.Equal(5, item.Priority);                       // tablonun en sonu
        Assert.False(item.Signal);                            // kapsülü uyandırmaz
        Assert.Equal(1, snap.Counts.Uncertain);
        Assert.Contains("emin değilim", snap.Sentence);
        Assert.Equal("unknown", item.Confidence);
    }
}
