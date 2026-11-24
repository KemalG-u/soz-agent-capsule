namespace Soz.Capsule;

/// <summary>Ön bakış kutusunda bir satır (pencere türlerinden bağımsız, sınanabilir). Tiklanir: satır gerçekten bir şey yapar; yapmayan satır tıklanır görünmez (vurgu ve el imleci yok).</summary>
internal readonly record struct OnBakisSatiri(string Kimlik, string Metin, string? Sag = null, string? Glif = null, DurumSinifi Sinif = DurumSinifi.Yok,
    bool Baslik = false, bool Soluk = false, bool Tiklanir = false);

/// <summary>
/// Küçük pencerenin üstüne gelince açılan kutunun satırları. Kural: tıklanabilir görünen her satır bir şey yapar.
/// İş satırı o işi (ya da oturumu) açar; "+N daha" ve "Proje" satırı tam paneli açar; boş durumda "Söz'ü aç" tam paneli açar. Başlıklar ve düz cümleler tıklanamaz.
/// Kimlik "sayfa:" ile başlıyorsa iş değil, bir sayfadır (<see cref="KabukProtokol.PanelAdresi"/> karşılığını verir).
/// </summary>
internal static class OnBakisIcerigi
{
    public const string BosCumle = "Şu an seni bekleyen iş yok.";
    public const string SozuAc = "Söz'ü aç";
    public const string SayfaOn = "sayfa:";
    public const string SayfaSuAn = "sayfa:suan", SayfaIsler = "sayfa:is", SayfaAc = "sayfa:ac";
    public const int BekleyenEn = 4, CalisanEn = 3, BelirsizEn = 3;

    public static List<OnBakisSatiri> Kur(IReadOnlyList<Is> bekleyen, IReadOnlyList<Is> calisan, IReadOnlyList<Is> belirsiz, string? projeAdi, DateTimeOffset simdi)
    {
        var l = new List<OnBakisSatiri>();
        static string Ad(Is j) => j.Baslik.Length == 0 ? j.Etiket : j.Baslik;

        // Motora ulaşılamıyorsa kutu "şu an seni bekleyen iş yok" demez (bilmiyoruz): tek açık cümle ve paneli açan tek satır.
        if (belirsiz.Any(j => j.Id == BaglantiKarar.BelirsizId))
        {
            l.Add(new("baglanti", BaglantiKarar.KutuCumlesi));
            l.Add(new("ac", SozuAc, Tiklanir: true));
            return DuzeltKimlik(l);
        }

        if (bekleyen.Count > 0)
        {
            l.Add(new("b1", $"Seni bekleyenler  {bekleyen.Count}", Baslik: true));
            foreach (var j in bekleyen.Take(BekleyenEn)) l.Add(new(j.Id, Ad(j), j.SagYazi(simdi), j.Glif, j.Sinif, Tiklanir: true));
            if (bekleyen.Count > BekleyenEn) l.Add(new("fazla", $"+{bekleyen.Count - BekleyenEn} daha", Tiklanir: true));
        }
        else if (calisan.Count > 0 || belirsiz.Count > 0) l.Add(new("bos", BosCumle));
        else
        {
            // Hiçbir şey yok: tek açık cümle ve tıklanır tek satır.
            l.Add(new("bos", BosCumle));
            l.Add(new("ac", SozuAc, Tiklanir: true));
            return DuzeltKimlik(l);
        }

        if (calisan.Count > 0)
        {
            l.Add(new("b2", $"Çalışanlar  {calisan.Count}", Baslik: true));
            foreach (var j in calisan.Take(CalisanEn)) l.Add(new(j.Id, Ad(j), j.SagYazi(simdi), j.Glif, j.Sinif, Tiklanir: true));
            if (calisan.Count > CalisanEn) l.Add(new("fazla2", $"+{calisan.Count - CalisanEn} daha", Tiklanir: true));
        }
        if (belirsiz.Count > 0)
        {
            l.Add(new("b3", $"Emin olamadıklarım  {belirsiz.Count}", Baslik: true));
            foreach (var j in belirsiz.Take(BelirsizEn)) l.Add(new(j.Id, Ad(j), j.SagYazi(simdi), j.Glif, j.Sinif, Tiklanir: true));
            if (belirsiz.Count > BelirsizEn) l.Add(new("fazla3", $"+{belirsiz.Count - BelirsizEn} daha", Tiklanir: true));
        }
        if (!string.IsNullOrWhiteSpace(projeAdi)) l.Add(new("proje", "Proje: " + projeAdi.Trim(), Tiklanir: true));
        return DuzeltKimlik(l);
    }

    // "+N daha", "Proje" ve "Söz'ü aç" satırları bir iş değil bir sayfa açar: kimlikleri sayfa kimliğine çevrilir.
    private static List<OnBakisSatiri> DuzeltKimlik(List<OnBakisSatiri> l)
    {
        for (var i = 0; i < l.Count; i++)
        {
            var k = l[i].Kimlik;
            var yeni = k switch { "fazla" or "fazla2" or "fazla3" => SayfaSuAn, "proje" => SayfaIsler, "ac" => SayfaAc, _ => k };
            if (!ReferenceEquals(yeni, k)) l[i] = l[i] with { Kimlik = yeni };
        }
        return l;
    }
}
