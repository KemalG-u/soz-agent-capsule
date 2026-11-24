using System.Text.Json;

namespace Soz.Capsule;

/// <summary>"Sıra bende" cevabının kapsüle gereken kısmı: sürüm (rev), cümle, iş listesi, bugünkü dikkat bütçesi.</summary>
internal sealed record SiraBendeCevap(long Rev, string Cumle, IReadOnlyList<Is> Isler, int GunlukHak, int Harcanan, int Sessiz);

/// <summary>
/// Motorun <c>GET /api/attention</c> cevabını kapsülün iş listesine çevirir. Gevşek ayrıştırır (eksik alan ya da bozuk biçim çökertmez).
/// Sıra: öğedeki `priority` alanı aynen kullanılır (tek kaynak sunucu). Tür eşleşmesi: ask→Soru, accept→Karar, blocked→Kaldı, done→Bitti, uncertain→Belirsiz (session_turn_end nedeniyle "sırayı sana verdi", "bitti" değil); bilinmeyen tür Belirsiz sayılır (tahminle "bitti" denmez).
/// Çalışan işler <c>running</c> dizisinden gelir ("j:{iş no}" kimliğiyle, eski <c>/api/now</c> ile aynı).
/// </summary>
internal static class SiraBendeCevabi
{
    private static JsonElement? Al(JsonElement e, string ad)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, ad, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
    private static string Metin(JsonElement e, string ad) => Al(e, ad) is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
    private static long Uzun(JsonElement e, string ad) => Al(e, ad) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var i) ? i : 0;
    /// <summary>Alan yoksa ya da doğru/yanlış değilse null (eski sunucu: bilgi yok, uydurulmaz).</summary>
    private static bool? Mantiksal(JsonElement e, string ad) => Al(e, ad) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : null;
    private static bool Dogru(JsonElement e, string ad, bool varsayilan) => Al(e, ad) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : varsayilan;

    /// <summary>Durumun başladığı an: öğenin "at" ya da "startedAt" alanı (ISO), yoksa "ageSeconds" ile şimdiden geriye. Hiçbiri yoksa null (süre yazılmaz).</summary>
    private static DateTimeOffset? Baslangic(JsonElement o)
    {
        foreach (var ad in new[] { "at", "startedAt" })
            if (Metin(o, ad) is { Length: > 0 } at && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) return t;
        if (Al(o, "ageSeconds") is { ValueKind: JsonValueKind.Number } a && a.TryGetInt64(out var sn) && sn >= 0) return DateTimeOffset.UtcNow.AddSeconds(-sn);
        return null;
    }

    public static IsTuru Tur(string kind) => kind switch
    {
        "ask" => IsTuru.Soru, "accept" => IsTuru.Karar, "blocked" => IsTuru.Kaldi, "done" => IsTuru.Bitti, _ => IsTuru.Belirsiz,
    };

    /// <summary>Cevap bu biçimde değilse (ör. eski motor 404 sayfası, boş gövde) null.</summary>
    public static SiraBendeCevap? Ayristir(JsonElement kok)
    {
        if (kok.ValueKind != JsonValueKind.Object || Al(kok, "items") is not { ValueKind: JsonValueKind.Array } items) return null;
        var liste = new List<Is>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in items.EnumerateArray())
        {
            var id = Metin(o, "id"); if (id.Length == 0 || !ids.Add(id)) continue;
            var baslik = Metin(o, "title"); if (baslik.Length == 0) baslik = Metin(o, "sentence");
            // Değişiklik yok (reason no_change) kapsülü uyandırmaz: sunucu işaret hakkı verse de sakin bir bilgi satırıdır.
            var neden = Metin(o, "reason");
            var sinyal = Dogru(o, "signal", false) && neden != "no_change";
            var isNo = Uzun(o, "jobId"); var oturumNo = Uzun(o, "sessionId");
            liste.Add(new Is(id, Tur(Metin(o, "kind")), baslik, MotorCevabi.Kisalt(baslik.Length > 0 ? baslik : "Yardımcı"), sinyal, Al(o, "priority") is { ValueKind: JsonValueKind.Number } p && p.TryGetInt32(out var sira) ? sira : null, neden.Length > 0 ? neden : null,
                isNo > 0 ? isNo : null, oturumNo > 0 ? oturumNo : null, Baslangic(o), Metin(o, "externalSessionId") is { Length: > 0 } dis ? dis : null,
                Metin(o, "sozNotu") is { Length: > 0 } sn ? sn : null, Mantiksal(o, "canProve")));
        }
        if (Al(kok, "running") is { ValueKind: JsonValueKind.Array } run)
            foreach (var o in run.EnumerateArray())
            {
                var baslik = Metin(o, "title"); var id = "j:" + Uzun(o, "jobId");
                if (ids.Add(id)) liste.Add(new Is(id, IsTuru.Calisiyor, baslik, MotorCevabi.Kisalt(baslik), Baslangic: Baslangic(o)));
            }
        long Sayi(string grup, string alan) => Al(kok, grup) is { } g ? Uzun(g, alan) : 0;
        return new SiraBendeCevap(Uzun(kok, "rev"), Metin(kok, "sentence"), liste, (int)Sayi("budget", "dailyLimit"), (int)Sayi("budget", "usedToday"), (int)Sayi("budget", "suppressedToday"));
    }
}

/// <summary>Kapsülün motorla bağlantı kararı (saf mantık, saat dışarıdan): ne göstereceği ve ne zaman yeniden deneyeceği.</summary>
internal enum BaglantiGorunumu
{
    /// <summary>Söz hiç çalışmıyor (kayıt yok: kapatılmış ya da hiç açılmamış): nötr, hiçbir şey gösterilmez. Kapsül yine çalışır.</summary>
    Notr,
    /// <summary>Canlı liste (ya da kopukluk çok yeni, son liste korunur).</summary>
    Liste,
    /// <summary>Kayıt var ama motora ulaşılamıyor (süreç ölmüş ya da yanıt vermiyor): eski durumu doğru gibi göstermek ve "hiç iş yok" demek yanlış olur, "bilgiye ulaşamıyorum" gösterilir. Motor dönene kadar öyle kalır.</summary>
    Belirsiz,
}

internal static class BaglantiKarar
{
    /// <summary>Kopukluk bu kadar sürünce eski liste yerine "belirsiz" gösterilir (kısa kopuklukta ekran titremesin).</summary>
    public const int BelirsizMs = 5000;
    public const string BelirsizId = "m:baglanti";
    /// <summary>Hapta (küçük pencerenin kendisinde) görünen tek sözcük.</summary>
    public const string HapYazisi = "ulaşamıyorum";
    /// <summary>Ön bakış kutusunda tek açık cümle: eski bilgi canlı gibi gösterilmez, "hiç iş yok" da denmez.</summary>
    public const string KutuCumlesi = "Bilgiye ulaşamıyorum. Seni bekleyen iş olup olmadığını bilmiyorum.";

    /// <param name="kayitVar">sunucu.json okundu (Söz en az bir kez açılmış ve temiz kapanmamış ya da hâlâ açık).</param>
    /// <param name="bagli">şu an bağlantı kurulu / son istek başarılı.</param>
    /// <param name="koptuMs">bağlantı kopalı geçen ms (bağlıyken 0).</param>
    public static BaglantiGorunumu Karar(bool kayitVar, bool bagli, long koptuMs)
    {
        if (!kayitVar) return BaglantiGorunumu.Notr;
        if (bagli || koptuMs < BelirsizMs) return BaglantiGorunumu.Liste;
        return BaglantiGorunumu.Belirsiz;   // 30 sn sonra nötr olmaz: sessizlik "hiç iş yok" gibi okunurdu
    }

    /// <summary>Yeniden deneme beklemesi (ms): 0,5 sn'den başlar, her başarısızlıkta ikiye katlanır, en çok 5 sn. Motor yeniden başlayınca en çok 5 sn içinde toparlanır.</summary>
    public static int YenidenDenemeMs(int deneme) => (int)Math.Min(500L << Math.Clamp(deneme, 0, 10), 5000);

    /// <summary>Kopukken gösterilecek tek satır: bilgiye ulaşılamıyor, durum belirsiz.</summary>
    public static Is BelirsizSatir() => new(BelirsizId, IsTuru.Belirsiz, "Bilgiye ulaşamıyorum", "bağlantı yok", Sinyal: false, Neden: "baglanti");
}
