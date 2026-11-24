using System.Text.Json;

namespace Soz.Capsule;

/// <summary>Belirsiz: Söz durumdan emin değil (tahminle "bitti" ya da "kaldı" denmez). Seni bekleyen sayılmaz, uyandırmaz, ayrı gösterilir.</summary>
internal enum IsTuru { Bitti, Karar, Kaldi, Soru, Calisiyor, Tamam, Belirsiz }

/// <summary>
/// Kapsülün gördüğü iş. Kimlik motordan gelir ("o:12" oturum, "j:7" iş, "ask:job:..." Sıra bende öğesi); sentetik satırların kimliği sayıdan türer.
/// Sinyal: sunucu bu öğe için bugünkü istem dışı işaret hakkını verdi mi (dikkat bütçesi sunucuda sayılır); false ise öğe listede görünür ama kapsül uyanmaz.
/// Neden: Sıra bende öğesinin <c>reason</c> alanı (yoksa null). "unproven" = denenmeden bitti, "no_change" = yardımcı hiçbir dosyayı değiştirmedi, "session_turn_end" = durdu ve sırayı kişiye verdi. Ekranlarda aynı sözcükler kullanılır.
/// </summary>
// IsNo ve OturumNo: sunucunun verdiği iş ve oturum numarası (varsa). Satıra tıklanınca panel bunlarla doğru yere gider; kimlik yalnız satırı ayırt eder.
// Baslangic: durumun başladığı an (süreyi "12 dk" diye göstermek için; yoksa süre yazılmaz). DisOturum: oturumun yardımcıdaki kimliği (sunucunun externalSessionId alanı); ana pencere satırı bununla bulur.
// SozNotu ve DenenebilirMi: sunucunun öğedeki sozNotu ("bu projede neyle deneyeceğimi bilmiyorum" gibi Söz'ün kendi notu) ve canProve alanları. Söz bunu deneyemiyorsa kapsül "dene" yönlendirmesi sunmaz.
internal sealed record Is(string Id, IsTuru Tur, string Baslik, string Kisa, bool Sinyal = true, int? Sira = null, string? Neden = null,
    long? IsNo = null, long? OturumNo = null, DateTimeOffset? Baslangic = null, string? DisOturum = null, string? SozNotu = null, bool? DenenebilirMi = null)
{
    /// <summary>
    /// Kapsül bu satır için "dene" türü bir yönlendirme (ör. "bitti, kontrol et") sunabilir mi? Sunucu öğede sozNotu verdiyse ya da canProve false dediyse Söz bunu deneyemez:
    /// kapsül yardımcının "bitti" sözünü denenmiş gibi göstermez ve "kontrol et" demez. Bilgi gelmediyse (eski sunucu) eskisi gibi sunulur.
    /// </summary>
    public bool DeneSunulur => DenenebilirMi != false && string.IsNullOrWhiteSpace(SozNotu);

    /// <summary>Yardımcı "bitti" dedi ama Söz bunu deneyemiyor: yeşil "bitti" değil, yardımcının sözü olarak gösterilir.</summary>
    public bool BittiDedi => Tur == IsTuru.Bitti && !DeneSunulur && Neden is null or "session_done" or "passed";

    /// <summary>Soru sormadan durmuş oturum (sunucu nedeni session_turn_end): bittiği de beklediği de bilinmiyor, sırayı kişiye verdi. "Bitti" değildir.</summary>
    public bool Durdu => Tur == IsTuru.Belirsiz && Neden == "session_turn_end";

    /// <summary>Tıklanınca kabuğa giden kimlik: sayılı iş "j:7", yardımcı oturumu "x:<yardımcıdaki kimlik>", sayılı oturum "o:12", yoksa satırın kendi kimliği.</summary>
    public string PanelKimligi => IsNo is > 0 ? "j:" + IsNo : !string.IsNullOrWhiteSpace(DisOturum) ? "x:" + DisOturum.Trim() : OturumNo is > 0 ? "o:" + OturumNo : Id;

    /// <summary>Satırın nedeni, sade Türkçe ("izin bekliyor", "soru sordu", "bitti, kontrol et"). Sunucunun neden kodundan, yoksa türden gelir.</summary>
    public string NedenMetni => Kanitsiz ? "bitti, denenmedi" : BittiDedi ? "bitti dedi, denenemedi" : Neden switch
    {
        "approval" or "session_permission" or "permission_prompt" => "izin bekliyor",
        "question" or "session_question" => "soru sordu",
        "session_input" => "bilgi bekliyor",
        "session_waiting" or "elicitation_dialog" or "agent_needs_input" or "idle_prompt" => "seni bekliyor",
        "decision" => "karar bekliyor",
        "proof_failed" => "kontrol geçmedi",
        "start_failed" => "başlatılamadı",
        "session_failed" or "failure" => "hata verip durdu",
        "session_turn_end" => "sırayı sana verdi",
        "passed" or "session_done" => "bitti, kontrol et",
        "no_change" => "değişiklik yok",
        "run_lost" => "yardımcının süreci kayıp",
        "session_quiet" or "stalled" => "uzun süredir sessiz",
        "baglanti" => "bağlantı yok",
        _ => Tur switch
        {
            IsTuru.Bitti => "bitti, kontrol et", IsTuru.Karar => "karar bekliyor", IsTuru.Kaldi => "takıldı", IsTuru.Soru => "soru sordu",
            IsTuru.Calisiyor => "çalışıyor", IsTuru.Belirsiz => "durumu belli değil", _ => "tamam",
        },
    };

    /// <summary>Önizleme kutusunda satırın sağ yazısı: "izin bekliyor · 12 dk", çalışan işte "çalışıyor · 5 dk". Başlangıç bilinmiyorsa (sunucu çalışan işe başlangıç vermiyorsa) yalnız neden, uydurma süre yazılmaz.</summary>
    public string SagYazi(DateTimeOffset simdi)
        => Baslangic is { } b ? NedenMetni + " · " + SureMetni(simdi - b) : NedenMetni;

    /// <summary>Geçen süre: "az önce", "12 dk", "1 saat", "3 gün".</summary>
    public static string SureMetni(TimeSpan s)
    {
        var dk = (long)Math.Floor(s.TotalMinutes);
        if (dk < 1) return "az önce";
        if (dk < 60) return dk + " dk";
        var sa = (long)Math.Floor(s.TotalHours);
        return sa < 24 ? sa + " saat" : (long)Math.Floor(s.TotalDays) + " gün";
    }

    /// <summary>Denenmeden bitti: yardımcı bitirdi ama Söz bunu deneyemedi. "belirsiz" değil, ayrı sözcük.</summary>
    public bool Kanitsiz => Tur == IsTuru.Belirsiz && Neden == "unproven";
    /// <summary>Yardımcı hiçbir dosyayı değiştirmedi, testler zaten geçiyordu: yeşil "bitti" değil, sakin ayrı bir durum (kapsülü uyandırmaz).</summary>
    public bool DegisiklikYok => Tur == IsTuru.Bitti && Neden == "no_change";

    /// <summary>Seni bekleyen mi (bitti, karar, kaldı, soru)?</summary>
    public bool Bekliyor => Tur is IsTuru.Bitti or IsTuru.Karar or IsTuru.Kaldi or IsTuru.Soru;
    /// <summary>
    /// Öncelik sırası (küçük önce). TEK kaynak sunucudur: <c>/api/attention</c> öğesindeki <c>priority</c> (<c>Sira</c>) aynen kullanılır, kapsül kendi sırasını icat etmez.
    /// Sunucudan sıra gelmeyen satırlar için (eski <c>/api/now</c>, sentetik satır) yedek, sunucudaki tabloyla aynıdır: soru 1, karar 2, kaldı 2 (kanıtı kalan iş kararı kişiye bırakır), başlatılamayan ve öteki engeller 3 (<c>Sira</c> ile verilir), bitti 4, belirsiz (takılan oturum dahil) 5, ötesi 9.
    /// </summary>
    public int Oncelik => Sira ?? Tur switch { IsTuru.Soru => 1, IsTuru.Karar => 2, IsTuru.Kaldi => 2, IsTuru.Bitti => 4, IsTuru.Belirsiz => 5, _ => 9 };

    public DurumSinifi Sinif => DegisiklikYok ? DurumSinifi.Tamam : Tur switch
    {
        IsTuru.Bitti => DurumSinifi.Bitti, IsTuru.Karar => DurumSinifi.Karar, IsTuru.Kaldi => DurumSinifi.Kaldi,
        IsTuru.Soru => DurumSinifi.Soru, IsTuru.Calisiyor => DurumSinifi.Calisiyor, IsTuru.Belirsiz => DurumSinifi.Belirsiz, _ => DurumSinifi.Tamam,
    };

    /// <summary>Hapta görünen tek sözcük (P1: "bekliyor", "çalışıyor", "kaldı").</summary>
    public string Soz => Kanitsiz ? "denenmedi" : Durdu ? "durdu" : BittiDedi ? "bitti dedi" : DegisiklikYok ? "değişiklik yok" : Tur switch
    {
        IsTuru.Bitti => "bitti", IsTuru.Karar => "karar", IsTuru.Kaldi => "olmadı", IsTuru.Soru => "soru",
        IsTuru.Calisiyor => "çalışıyor", IsTuru.Belirsiz => "belirsiz", _ => "tamam",
    };

    public string Glif => DegisiklikYok ? "tam" : Tur switch
    {
        IsTuru.Bitti => "ok", IsTuru.Karar => "dec", IsTuru.Kaldi => "fail", IsTuru.Soru => "ask", IsTuru.Calisiyor => "run", IsTuru.Belirsiz => "unk", _ => "tam",
    };

    /// <summary>Ekran okuyucuya okunacak ad.</summary>
    public string Etiket => Kanitsiz ? "Denenmedi" : Durdu ? "Sırayı sana verdi" : BittiDedi ? "Bitti dedi, denenemedi" : DegisiklikYok ? "Değişiklik yok" : Tur switch
    {
        IsTuru.Bitti => "İş bitti", IsTuru.Karar => "Karar bekliyor", IsTuru.Kaldi => "Olmadı", IsTuru.Soru => "Soru sordu",
        IsTuru.Calisiyor => "Çalışıyor", IsTuru.Belirsiz => "Durum belirsiz", _ => "Tamamlandı",
    };
}

/// <summary>Motorun GET api/now cevabını kapsülün iş listesine çevirir. Gevşek ayrıştırır: eksik alan ya da bozuk biçim çökertmez.</summary>
internal static class MotorCevabi
{
    private static JsonElement? Al(JsonElement e, string ad)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, ad, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
    private static string Metin(JsonElement e, string ad) => Al(e, ad) is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
    private static int Sayi(JsonElement e, string ad)
    {
        var v = Al(e, ad);
        if (v is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i)) return i;
        return 0;
    }
    private static long Uzun(JsonElement e, string ad) => Al(e, ad) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var i) ? i : 0;

    internal static string Kisalt(string s, int en = 24)
    {
        s = (s ?? "").Trim();
        if (s.Length <= en) return s;
        return s[..(en - 1)].TrimEnd() + "…";
    }

    /// <summary>/api/now cevabından iş listesi. Sayılar listeden büyükse eksik satırlar adsız satır olarak eklenir (sayı doğru kalsın).</summary>
    public static List<Is> NowAyristir(JsonElement kok)
    {
        var liste = new List<Is>();
        if (kok.ValueKind != JsonValueKind.Object) return liste;
        var ids = new HashSet<string>();
        void Ekle(Is x) { if (ids.Add(x.Id)) liste.Add(x); }

        if (Al(kok, "needsYou") is { ValueKind: JsonValueKind.Array } ny)
            foreach (var n in ny.EnumerateArray())
            {
                var baslik = Metin(n, "title"); if (baslik.Length == 0) baslik = Metin(n, "projectName");
                var reason = Metin(n, "reason"); var soru = Metin(n, "question"); var durum = Metin(n, "state");
                // Takılan (uzun süre sessiz) oturum sunucuda "belirsiz" (session_quiet, öncelik 5): takılmış mı çalışıyor mu bilinmez, uyandırmaz.
                var takildi = reason == "stalled" || durum == "stalled";
                // Yardımcı hata verip durdu (kanca: "failure:<ad>", sunucu öğesi: session_failed): "bitti" değil, engel ("kaldı", öncelik 3).
                var hata = reason.StartsWith("failure", StringComparison.OrdinalIgnoreCase) || reason == "session_failed";
                // İzin istedi: karar kişide. Bilgi/soru bekliyor: soru. Soru işareti yok ve açıkça "bitti" denmemişse oturum yalnız DURDU ve sırayı kişiye verdi:
                // "bitti" denmez (kullanıcının bir numaralı derdi), belirsiz sınıfında durur ve uyandırmaz (sunucudaki session_turn_end ile aynı).
                var izin = reason is "permission_prompt" or "session_permission";
                var bilgi = reason is "session_question" or "session_input" or "elicitation_dialog" or "agent_needs_input" or "idle_prompt";
                var bittiDendi = reason is "session_done" or "passed" or "done";
                var durdu = !takildi && !hata && !izin && !bilgi && soru.Length == 0 && !bittiDendi;
                var tur = takildi || durdu ? IsTuru.Belirsiz : hata ? IsTuru.Kaldi : izin ? IsTuru.Karar : soru.Length > 0 || bilgi ? IsTuru.Soru : IsTuru.Bitti;
                Ekle(new Is("o:" + Uzun(n, "sessionId"), tur, baslik.Length > 0 ? baslik : "Yardımcı seni bekliyor", Kisalt(baslik.Length > 0 ? baslik : "Yardımcı"),
                    Sinyal: !takildi && !durdu, Sira: hata ? 3 : null, Neden: hata ? "failure" : durdu ? "session_turn_end" : reason.Length > 0 ? reason : null));
            }
        // Söz'ün durumundan emin olmadığı oturumlar: seni bekleyen sayılmaz, uyandırmaz (Sinyal false), "belirsiz" diye ayrı durur.
        if (Al(kok, "uncertain") is { ValueKind: JsonValueKind.Array } un)
            foreach (var n in un.EnumerateArray())
            {
                var baslik = Metin(n, "title"); if (baslik.Length == 0) baslik = Metin(n, "projectName");
                Ekle(new Is("o:" + Uzun(n, "sessionId"), IsTuru.Belirsiz, baslik.Length > 0 ? baslik : "Yardımcı", Kisalt(baslik.Length > 0 ? baslik : "Yardımcı"), Sinyal: false));
            }
        if (Al(kok, "waitingJobs") is { ValueKind: JsonValueKind.Array } wj)
            foreach (var j in wj.EnumerateArray())
            {
                var baslik = Metin(j, "title"); var durum = Metin(j, "state");
                int? sira = null;
                var tur = durum switch
                {
                    "kaldi" => IsTuru.Kaldi,                      // kanıtı kalan iş: karar kişide (2)
                    "baslatilamadi" => IsTuru.Kaldi,              // başlatılamadı: engel (3)
                    "karar_bekliyor" or "onay_bekliyor" => IsTuru.Karar,
                    "cevap_bekliyor" => IsTuru.Soru,
                    "gecti" => IsTuru.Bitti,
                    _ => Metin(j, "question").Length > 0 ? IsTuru.Soru : IsTuru.Bitti,
                };
                if (durum == "baslatilamadi") sira = 3;
                Ekle(new Is("j:" + Uzun(j, "id"), tur, baslik, Kisalt(baslik), Sira: sira));
            }
        if (Al(kok, "runningJobs") is { ValueKind: JsonValueKind.Array } rj)
            foreach (var j in rj.EnumerateArray())
            {
                var baslik = Metin(j, "title");
                Ekle(new Is("j:" + Uzun(j, "id"), IsTuru.Calisiyor, baslik, Kisalt(baslik)));
            }

        if (Al(kok, "counts") is { ValueKind: JsonValueKind.Object } c)
        {
            void Tamamla(IsTuru tur, int hedef, string ad)
            {
                var var_ = liste.Count(x => x.Tur == tur);
                for (var i = var_; i < hedef; i++) Ekle(new Is($"s:{tur}:{i}", tur, ad, ad));
            }
            Tamamla(IsTuru.Bitti, Sayi(c, "jobsPassed"), "Bitti");
            Tamamla(IsTuru.Kaldi, Sayi(c, "jobsFailed"), "Olmadı");
            Tamamla(IsTuru.Belirsiz, Sayi(c, "stalled"), "Belirsiz");
            Tamamla(IsTuru.Calisiyor, Math.Max(Sayi(c, "jobsRunning"), Sayi(c, "working")), "Çalışıyor");
        }
        return liste;
    }
}
