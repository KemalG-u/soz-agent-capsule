using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Soz.Capsule;

/// <summary>Tek tek ayar alanı: ad, birim, sınır ve açıklama. Kemal sayıları değiştirip getirecek, bu yüzden hepsi tek tabloda.</summary>
internal sealed record AyarTanim(string Ad, string Baslik, double Min, double Max, string Birim, string Aciklama, Func<Ayar, double> Oku, Action<Ayar, double> Yaz);

/// <summary>
/// Kapsülün bütün sayıları (FABLE-VIZYON-4 P1, P2, P4, P5). Varsayılanlar sözleşmedeki ve deneme sayfasındaki (DEF) değerlerdir.
/// JSON dosyasından okunur, çalışırken değişirse yeniden okunur. Bilinmeyen anahtar yok sayılır, sınır dışı değer sınıra çekilir.
/// </summary>
internal sealed class Ayar
{
    // ---- P1: boyutlar (dp), opaklıklar ----
    public double SleepD = 0.42;          // uyku opaklığı, koyu zemin
    public double SleepL = 0.50;          // uyku opaklığı, açık zemin
    public double SleepFloor = 0.10;      // taban, altına inmez
    public double Soft = 0.60;            // soluk opaklık
    public double Dot = 14;               // uyku noktası
    public double Orb = 34;               // soluk orb
    public double Big = 44;               // belirgin orb, hap ve giriş yüksekliği
    public double PillMax = 292;          // durumlu hap en çok
    public double GirisW = 420;           // giriş genişliği
    public double PillPad = 18;           // hapta glif ile yazı dışında kalan boşluk (dp)
    // ---- P1: süreler ----
    public double Idle1 = 4;              // sn, belirgin -> soluk
    public double Idle2 = 30;             // sn, soluk -> uyku
    public double FadeMs = 800;           // sönüş oturma süresi
    public double WakeMs = 150;           // uyanış oturma süresi
    public double AttentionMs = 6000;     // dikkat olayından sonra belirgin kalma
    public double LuminanceMs = 10000;    // zemin parlaklığı ölçümü aralığı
    public double R = 40;                 // yaklaşma yarıçapı (dp)
    // ---- P2: girdi ----
    public double HoverMs = 600;          // üstünde durma, ön bakış
    public double HoverCloseMs = 300;     // ayrılınca ön bakış kapanır
    public double DwellTol = 4;           // üstünde durma toleransı (dp)
    public double DragThreshold = 4;      // sürükleme eşiği (dp)
    public double Snap = 28;              // kenara yapışma mesafesi (dp)
    public double EdgeGap = 6;            // kenar payı (dp)
    public double MinHit = 32;            // en az dokunma hedefi (dp)
    public double StretchGrip = 10;       // hapın serbest ucundan tutma payı (dp)
    public double WheelShowMs = 2600;     // tekerlek sonrası başlığın görünme süresi
    public double DoubleClickMs = 500;    // çift tık aralığı
    public double HideMs = 3600000;       // "1 saat gizle"
    public double GuardMs = 1500;         // tam ekran ve görünürlük bekçisi
    // ---- P4: hareket ----
    public double K = 380;                // konum ve boyut yayı, sertlik
    public double Z = 0.8;                // sönüm
    public double KFast = 800;            // hızlı etkileşim (sürükleme, uzatma)
    public double ZFast = 0.6;
    public double OpK = 1600;             // opaklık yayı (yedek, süreler FadeMs ve WakeMs'ten gelir)
    public double ReducedFadeMs = 80;     // hareketi azalt: opaklık geçişi
    public double ProgressMs = 60000;     // çalışan iş çizgisi bu kadar sonra durur
    // ---- anahtarlar ----
    public double FarFade = 1;            // uzağa tıklayınca soluklaş
    public double FsHide = 1;             // tam ekranda gizle
    public double Sound = 0;              // dikkat sesi (kapalı)
    public double ReduceMotion = -1;      // -1 sistemden, 0 kapalı, 1 açık
    // ---- metin ayarları ----
    public string Rest = "nokta";         // nokta | orb | durumlu
    public string Tema = "cam";           // cam | terminal | mat | ton | duz | sicak
    public string Mod = "sistem";         // sistem | koyu | acik
    public string Kisayol = "Ctrl+Alt+J";

    public static readonly AyarTanim[] Tablo =
    {
        new("sleepD", "Uyku opaklığı, koyu zemin", 0.1, 0.8, "", "Uyku noktasının koyu zeminde görünürlüğü. 0,10 altına inemez.", a => a.SleepD, (a, v) => a.SleepD = v),
        new("sleepL", "Uyku opaklığı, açık zemin", 0.1, 0.8, "", "Açık zeminde ayrı değer. Zemin parlaklığına göre karışır.", a => a.SleepL, (a, v) => a.SleepL = v),
        new("sleepFloor", "Opaklık tabanı", 0.02, 0.5, "", "Hiçbir durumda bunun altına inilmez.", a => a.SleepFloor, (a, v) => a.SleepFloor = v),
        new("soft", "Soluk opaklık", 0.2, 1, "", "Soluk orb ve soluk durumlu hap.", a => a.Soft, (a, v) => a.Soft = v),
        new("dot", "Uyku noktası", 8, 24, "dp", "Varsayılan 14.", a => a.Dot, (a, v) => a.Dot = v),
        new("orb", "Soluk orb", 24, 44, "dp", "Varsayılan 34.", a => a.Orb, (a, v) => a.Orb = v),
        new("big", "Belirgin orb", 32, 60, "dp", "Varsayılan 44. Hapın ve giriş satırının yüksekliği de budur.", a => a.Big, (a, v) => a.Big = v),
        new("pillMax", "Hap en çok", 120, 420, "dp", "Durumlu hapın üst sınırı. Varsayılan 292.", a => a.PillMax, (a, v) => a.PillMax = v),
        new("girisW", "Giriş genişliği", 240, 640, "dp", "Varsayılan 420.", a => a.GirisW, (a, v) => a.GirisW = v),
        new("idle1", "Belirgin, soluğa geçiş", 1, 20, "sn", "Boşta bu kadar durunca soluklaşır.", a => a.Idle1, (a, v) => a.Idle1 = v),
        new("idle2", "Soluk, uykuya geçiş", 5, 300, "sn", "Soluk hâlde bu kadar durunca noktaya iner.", a => a.Idle2, (a, v) => a.Idle2 = v),
        new("fadeMs", "Sönüş süresi", 100, 2000, "ms", "Opaklık düşerken oturma süresi. Yavaş olmalı.", a => a.FadeMs, (a, v) => a.FadeMs = v),
        new("wakeMs", "Uyanış süresi", 50, 600, "ms", "Opaklık artarken oturma süresi. Hızlı olmalı.", a => a.WakeMs, (a, v) => a.WakeMs = v),
        new("attentionMs", "Dikkat süresi", 1000, 30000, "ms", "Dikkat isteyen olaydan sonra belirgin kalma.", a => a.AttentionMs, (a, v) => a.AttentionMs = v),
        new("luminanceMs", "Zemin ölçümü aralığı", 2000, 60000, "ms", "Uykuda zemin parlaklığı bu aralıkla ölçülür.", a => a.LuminanceMs, (a, v) => a.LuminanceMs = v),
        new("R", "Uyanma yarıçapı", 0, 400, "dp", "İmleç bu mesafeye girince opaklık artmaya başlar. 0 kapatır.", a => a.R, (a, v) => a.R = v),
        new("hoverMs", "Üstünde durma süresi", 200, 1500, "ms", "Bu kadar durunca ön bakış açılır.", a => a.HoverMs, (a, v) => a.HoverMs = v),
        new("hoverCloseMs", "Ön bakış kapanma süresi", 100, 1500, "ms", "İmleç ayrılınca bu kadar sonra kapanır.", a => a.HoverCloseMs, (a, v) => a.HoverCloseMs = v),
        new("dwellTol", "Durma toleransı", 1, 16, "dp", "Bu kadar oynamak durmayı bozmaz.", a => a.DwellTol, (a, v) => a.DwellTol = v),
        new("dragThreshold", "Sürükleme eşiği", 1, 24, "dp", "Bu kadar oynayınca tık değil sürükleme olur.", a => a.DragThreshold, (a, v) => a.DragThreshold = v),
        new("snap", "Kenara yapışma", 0, 80, "dp", "Bırakınca kenara bu kadar yakınsa yapışır.", a => a.Snap, (a, v) => a.Snap = v),
        new("edgeGap", "Kenar payı", 0, 24, "dp", "Yapışınca kenarla arası.", a => a.EdgeGap, (a, v) => a.EdgeGap = v),
        new("minHit", "En az dokunma hedefi", 24, 64, "dp", "Görünen orb küçük olsa da tıklanabilir alan.", a => a.MinHit, (a, v) => a.MinHit = v),
        new("wheelShowMs", "Tekerlek başlık süresi", 500, 8000, "ms", "Tekerlekten sonra başlık bu kadar görünür.", a => a.WheelShowMs, (a, v) => a.WheelShowMs = v),
        new("doubleClickMs", "Çift tık aralığı", 150, 1200, "ms", "İki tık bu kadar yakınsa çift tıktır.", a => a.DoubleClickMs, (a, v) => a.DoubleClickMs = v),
        new("hideMs", "Gizleme süresi", 60000, 86400000, "ms", "Sağ tık panosundaki 1 saat gizle.", a => a.HideMs, (a, v) => a.HideMs = v),
        new("guardMs", "Bekçi aralığı", 500, 10000, "ms", "Tam ekran ve görünürlük denetimi.", a => a.GuardMs, (a, v) => a.GuardMs = v),
        new("k", "Yay sertliği", 100, 1200, "", "Konum ve boyut yayı. Büyüdükçe daha hızlı oturur.", a => a.K, (a, v) => a.K = v),
        new("z", "Yay sönümü", 0.3, 1.2, "", "1 ve üstü taşmasız, altı hafif yaylanır.", a => a.Z, (a, v) => a.Z = v),
        new("kFast", "Hızlı yay sertliği", 200, 2400, "", "Sürükleme ve uzatma sırasında.", a => a.KFast, (a, v) => a.KFast = v),
        new("zFast", "Hızlı yay sönümü", 0.3, 1.2, "", "", a => a.ZFast, (a, v) => a.ZFast = v),
        new("reducedFadeMs", "Hareketi azalt: geçiş", 20, 400, "ms", "Hareketi azalt açıkken yay yok, bu kadar opaklık geçişi.", a => a.ReducedFadeMs, (a, v) => a.ReducedFadeMs = v),
        new("progressMs", "İlerleme çizgisi süresi", 5000, 600000, "ms", "Çalışan işin çizgisi bu kadar sonra durur.", a => a.ProgressMs, (a, v) => a.ProgressMs = v),
        new("farFade", "Uzağa tıklayınca soluklaş", 0, 1, "", "1 açık, 0 kapalı.", a => a.FarFade, (a, v) => a.FarFade = v),
        new("fsHide", "Tam ekranda gizle", 0, 1, "", "1 açık, 0 kapalı.", a => a.FsHide, (a, v) => a.FsHide = v),
        new("sound", "Dikkat sesi", 0, 1, "", "1 açık, 0 kapalı. Varsayılan kapalı.", a => a.Sound, (a, v) => a.Sound = v),
        new("reduceMotion", "Hareketi azalt", -1, 1, "", "-1 sistemden oku, 0 kapalı, 1 açık.", a => a.ReduceMotion, (a, v) => a.ReduceMotion = v),
    };

    public static readonly string[] RestDegerleri = { "nokta", "orb", "durumlu" };
    public static readonly string[] TemaDegerleri = { "cam", "terminal", "mat", "ton", "duz", "sicak" };
    public static readonly string[] ModDegerleri = { "sistem", "koyu", "acik" };

    public Ayar Kopya() { var a = new Ayar(); a.Al(this); return a; }

    /// <summary>Başka bir ayarın bütün değerlerini bu nesneye alır (nesne kimliği korunur, mantık ve çizim aynı nesneyi görür).</summary>
    public void Al(Ayar d) { foreach (var t in Tablo) t.Yaz(this, t.Oku(d)); Rest = d.Rest; Tema = Temalar.KanoniAd(d.Tema); Mod = d.Mod; Kisayol = d.Kisayol; }

    public static double Sinirla(AyarTanim t, double v) => double.IsNaN(v) ? t.Oku(new Ayar()) : Math.Clamp(v, t.Min, t.Max);

    /// <summary>Metin ayarını geçerli değerlerle sınırlar. Geçersizse null döner.</summary>
    public static string? SecimGecerli(string[] izinli, string? v)
    {
        if (v == null) return null;
        if (ReferenceEquals(izinli, TemaDegerleri)) return Temalar.Esle(v);
        if (ReferenceEquals(izinli, ModDegerleri)) return Temalar.KipEsle(v);
        var s = v.Trim().ToLowerInvariant().Replace('ü', 'u').Replace('â', 'a').Replace('ı', 'i').Replace('ö', 'o').Replace('ç', 'c').Replace('ş', 's').Replace('ğ', 'g');
        return izinli.Contains(s) ? s : null;
    }

    /// <summary>JSON metninden okur. Dönüş: uyarılar (bilinmeyen ya da geçersiz alanlar). Dosya bozuksa mevcut değerler korunur.</summary>
    public List<string> JsonYukle(string json)
    {
        var uyari = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { uyari.Add("ayar dosyası okunamadı: " + e.Message); return uyari; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { uyari.Add("ayar dosyası bir nesne olmalı"); return uyari; }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var t = Tablo.FirstOrDefault(x => string.Equals(x.Ad, p.Name, StringComparison.OrdinalIgnoreCase));
                if (t != null)
                {
                    double v;
                    if (p.Value.ValueKind == JsonValueKind.Number) v = p.Value.GetDouble();
                    else if (p.Value.ValueKind == JsonValueKind.True) v = 1;
                    else if (p.Value.ValueKind == JsonValueKind.False) v = 0;
                    else { uyari.Add($"{p.Name}: sayı bekleniyordu"); continue; }
                    t.Yaz(this, Sinirla(t, v));
                    continue;
                }
                var s = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                switch (p.Name.ToLowerInvariant())
                {
                    case "rest": { var g = SecimGecerli(RestDegerleri, s); if (g != null) Rest = g; else uyari.Add("rest: nokta, orb ya da durumlu"); break; }
                    case "tema": { var g = SecimGecerli(TemaDegerleri, s); if (g != null) Tema = g; else uyari.Add("tema: cam, terminal, mat, ton, duz ya da sicak"); break; }
                    case "mod": { var g = SecimGecerli(ModDegerleri, s); if (g != null) Mod = g; else uyari.Add("mod: sistem, koyu ya da acik"); break; }
                    case "kisayol": if (!string.IsNullOrWhiteSpace(s)) Kisayol = s!; break;
                    default: uyari.Add("bilinmeyen alan: " + p.Name); break;
                }
            }
        }
        return uyari;
    }

    /// <summary>Bütün ayarlar, okunur biçimde (Kemal sayıları değiştirip getirsin diye).</summary>
    public string JsonYaz()
    {
        var sb = new StringBuilder("{\n");
        foreach (var t in Tablo)
            sb.Append("  \"").Append(t.Ad).Append("\": ").Append(t.Oku(this).ToString("0.####", CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"rest\": \"").Append(Rest).Append("\",\n");
        sb.Append("  \"tema\": \"").Append(Tema).Append("\",\n");
        sb.Append("  \"mod\": \"").Append(Mod).Append("\",\n");
        sb.Append("  \"kisayol\": \"").Append(Kisayol).Append("\"\n}\n");
        return sb.ToString();
    }

    /// <summary>Hareketi azalt gerçekten açık mı (ayar -1 ise sistem değeri verilir).</summary>
    public bool HareketAzalt(bool sistem) => ReduceMotion < 0 ? sistem : ReduceMotion >= 0.5;
}
