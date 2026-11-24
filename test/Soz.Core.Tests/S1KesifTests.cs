using System.Text.Json.Nodes;
using Soz.Core.Discovery;
using Soz.Core.Onboarding;
using Soz.Core.Registry;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>s1 / B8: öneriler ilk gün boş kalmasın. Dayanak "en az 2 gerçek kullanım" ve pencereden bağımsız; boş durum nedenini söyler; kurulu ama kapalı araç gerçek dayanaktır.</summary>
public class S1KesifTests
{
    private static DiscoveryParams Acik => DiscoveryDb.P with { ShadowMode = false };

    [Fact]
    public void Iki_gercek_kullanim_baska_saat_diliminde_de_oneri_dogurur_tek_kullanim_dogurmaz()
    {
        using var t = new DiscoveryDb();
        var p = Acik;
        UsageStore.Record(t.C, "skill:fatura", null, DiscoveryDb.M(0, 10), p);
        UsageStore.Record(t.C, "skill:fatura", null, DiscoveryDb.M(1, 10), p);
        UsageStore.Record(t.C, "skill:tek", null, DiscoveryDb.M(1, 10), p);
        var r = SuggestionService.Suggest(t.C, null, DiscoveryDb.M(1, 22), p); // proje yok, saat dilimi de başka
        var s = Assert.Single(r.Items);
        Assert.Equal("skill:fatura", s.ObjectUid);
        Assert.Equal("recent", s.ReasonCode);
        Assert.StartsWith("Toplam 2 kez kullandın", s.Reason);
        Assert.Null(r.Nearest); // öneri varken en yakın dayanak hesaplanmaz
    }

    [Fact]
    public void Tek_kullanim_oneri_vermez_en_yakin_dayanak_gercek_sayidan_gelir()
    {
        using var t = new DiscoveryDb();
        var p = Acik;
        UsageStore.Record(t.C, "skill:tek", null, DiscoveryDb.M(0, 10), p);
        var r = SuggestionService.Suggest(t.C, null, DiscoveryDb.M(0, 10), p);
        Assert.Empty(r.Items);
        Assert.Equal(new Nearest("skill:tek", 1, 1), r.Nearest);
        using var t2 = new DiscoveryDb();
        Assert.Null(SuggestionService.Suggest(t2.C, null, DiscoveryDb.M(0, 10), p).Nearest); // hiç kullanım yok: uydurma yok
    }

    [Fact]
    public void Susturulan_ya_da_yuvadaki_nesne_dayanak_kapisindan_gecmez()
    {
        using var t = new DiscoveryDb();
        var p = Acik;
        for (var d = 0; d < 3; d++) { UsageStore.Record(t.C, "skill:yuvada", null, DiscoveryDb.M(d, 10), p); UsageStore.Record(t.C, "skill:sus", null, DiscoveryDb.M(d, 10), p); }
        LayoutStore.Place(t.C, new SlotAddress(2, 0), "skill:yuvada");
        DismissStore.Dismiss(t.C, "skill:sus", null, DismissMode.Never, DiscoveryDb.M(3, 10), p);
        Assert.Empty(SuggestionService.Suggest(t.C, null, DiscoveryDb.M(3, 22), p).Items);
    }

    [Fact]
    public void Servis_bos_durumda_nedeni_ve_en_yakin_dayanagi_soyler()
    {
        using var t = new DiscoveryDb();
        var svc = new DiscoveryService(t.Db);
        var bos = svc.Suggestions(null, null);
        Assert.Equal("Henüz öneri yok. Söz birkaç gün neyi sık kullandığına bakar, sonra burada önerir.", (string?)bos["emptyText"]);
        Assert.Null(bos["nearest"]);
        Assert.Empty(bos["firstDay"]!.AsArray());
        svc.RecordUsage("setting:theme", null, null);
        var bir = svc.Suggestions(null, null);
        var near = bir["nearest"]!.AsObject();
        Assert.Equal(1L, (long)near["count"]!);
        Assert.EndsWith("1 kez kullandın. Bir kez daha kullanırsan öneririm.", (string)near["text"]!);
    }

    [Fact]
    public void Kurulu_ama_kapali_arac_ilk_gun_dayanakli_oneridir_susturunca_ya_da_acilinca_gider()
    {
        using var t = new DiscoveryDb();
        RegistryHost.Revoke(t.Db, "connector:hermes");
        var svc = new DiscoveryService(t.Db);
        Assert.Empty(svc.Suggestions(null, null)["firstDay"]!.AsArray());      // tanışma bulgusu yok: öneri yok

        var body = new JsonObject
        {
            ["owner"] = "system", ["state"] = "suggested", ["present"] = true, ["group"] = FindingGroups.Tools, ["title"] = "Hermes Agent kurulu",
            ["confidence"] = Confidence.Installed, ["facts"] = new JsonObject { ["tool"] = "hermes" },
        };
        SuggestionStore.Insert(t.C, null, "tanisma:tool:hermes", "tanisma.tools", body, new JsonArray(), "2026-03-02T10:00:00Z");
        var fd = new DiscoveryService(t.Db).Suggestions(null, null)["firstDay"]!.AsArray();
        var it = Assert.Single(fd)!.AsObject();
        Assert.Equal("connector:hermes", (string)it["objectUid"]!);
        Assert.Equal("Hermes Agent kurulu görünüyor ama kapalı. Açayım mı?", (string)it["reason"]!);
        Assert.Equal("installed-closed", (string)it["reasonCode"]!);
        Assert.Null(svc.Suggestions(null, null)["emptyText"]);                  // öneri var: boş durum cümlesi yok

        svc.Dismiss("connector:hermes", null, "never", restore: false);
        Assert.Empty(svc.Suggestions(null, null)["firstDay"]!.AsArray());
        svc.Dismiss("connector:hermes", null, "never", restore: true);
        Assert.Single(svc.Suggestions(null, null)["firstDay"]!.AsArray());
        RegistryHost.Approve(t.Db, "connector:hermes");                         // kişi açtı: artık "kapalı" değil
        Assert.Empty(new DiscoveryService(t.Db).Suggestions(null, null)["firstDay"]!.AsArray()); // kayıt defteri her istekte yeniden okunur (uç noktada da öyle)
    }
}
