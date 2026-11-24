using Soz.Capsule;

namespace Soz.Shell;

/// <summary>
/// Gösterilecek tek bir Windows bildirimi. Sayi birden büyükse birleştirilmiştir ("3 oturum seni bekliyor").
/// Adres: balona tıklayınca paneli açacağı yer (o oturumun kartı ya da işin sayfası; birden çok oturum birleştiyse genel sayfa).
/// </summary>
internal sealed record Bildirim(string Baslik, string Metin, int Sayi, string? IlkId, string Adres = "#/suan");

/// <summary>
/// Seni bekleyen oturum çıkınca bildirim kararı. Saf mantık: ağ, pencere ve saat dışarıdan verilir, böylece kabuk açılmadan sınanır.
/// Kurallar: ilk yükleme sessizdir (zaten bekleyenler açılışta yağmaz); aynı öğe için bir kez bildirilir; panel açıkken bildirilmez
/// (kişi zaten bakıyor); dakikada en çok 3 bildirim, fazlası beklemeye alınır ve tek bir birleşik bildirim olur.
/// Uygun nedenler yalnız kişiyi bekleyen dört türdür: soru, izin, bilgi, hata ile durma.
/// </summary>
internal sealed class BildirimYoneticisi
{
    internal const int DakikadaEnCok = 3;
    private const int GorulduSiniri = 2000;

    private static readonly HashSet<string> Uygun = new(StringComparer.Ordinal)
    {
        "session_question", "session_permission", "session_input", "session_failed",
    };

    private readonly Func<DateTime> _simdi;
    private readonly HashSet<string> _goruldu = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _gonderilenler = new();
    private bool _ilkYuklemeBitti;

    public BildirimYoneticisi(Func<DateTime>? simdi = null) { _simdi = simdi ?? (() => DateTime.UtcNow); }

    internal static bool UygunMu(string reason) => Uygun.Contains(reason);

    /// <summary>
    /// Güncel öğe listesini değerlendirir. Gösterilecek bildirim varsa döner (en çok bir tane), yoksa null.
    /// <paramref name="panelAcik"/> true ise yeni öğeler görülmüş sayılır ve bildirim yapılmaz.
    /// </summary>
    public Bildirim? Degerlendir(IReadOnlyList<AttentionItemInfo> ogeler, bool panelAcik)
    {
        var uygunlar = ogeler.Where(o => UygunMu(o.Reason)).ToList();

        if (!_ilkYuklemeBitti)
        {
            foreach (var o in uygunlar) _goruldu.Add(o.Id);
            _ilkYuklemeBitti = true;
            return null;
        }

        var yeniler = uygunlar.Where(o => !_goruldu.Contains(o.Id)).ToList();
        if (yeniler.Count == 0) return null;

        if (panelAcik)
        {
            foreach (var o in yeniler) Isaretle(o.Id);
            return null;
        }

        var simdi = _simdi();
        while (_gonderilenler.Count > 0 && (simdi - _gonderilenler.Peek()).TotalSeconds >= 60) _gonderilenler.Dequeue();
        if (_gonderilenler.Count >= DakikadaEnCok) return null; // yeniler görülmedi sayılır, pencere açılınca birleşik gösterilir

        foreach (var o in yeniler) Isaretle(o.Id);
        _gonderilenler.Enqueue(simdi);
        return yeniler.Count == 1 ? Tek(yeniler[0]) : new Bildirim("Söz", $"{yeniler.Count} oturum seni bekliyor", yeniler.Count, yeniler[0].Id, "#/suan");
    }

    /// <summary>Kişi bildirimi ya da paneli görünce bekleyenlerin hepsi görülmüş sayılır (panel açılınca aynı öğeler yeniden çıkmasın).</summary>
    public void HepsiniGoruldu(IReadOnlyList<AttentionItemInfo> ogeler)
    {
        foreach (var o in ogeler.Where(x => UygunMu(x.Reason))) Isaretle(o.Id);
    }

    private void Isaretle(string id)
    {
        if (_goruldu.Count >= GorulduSiniri) _goruldu.Clear(); // sınır dolarsa baştan (nadir; en kötü bir öğe bir kez daha bildirilir)
        _goruldu.Add(id);
    }

    internal static Bildirim Tek(AttentionItemInfo o)
    {
        var proje = Temiz(o.Project);
        if (proje.Length == 0) proje = Temiz(o.Title);
        if (proje.Length == 0) proje = "Bir proje";
        var yardimci = Temiz(o.Helper);
        var baslik = yardimci.Length > 0 ? $"{proje} · {yardimci}" : proje;

        var ayrinti = Temiz(o.Detail);
        var metin = o.Reason switch
        {
            "session_permission" => "Senden izin bekliyor",
            "session_input" => "Senden bir bilgi bekliyor",
            "session_failed" => ayrinti.Length > 0 ? "Hata yüzünden durdu: " + ayrinti : "Hata yüzünden durdu",
            _ => ayrinti.Length > 0 ? ayrinti : "Sana bir soru sordu",
        };
        return new Bildirim(Kisalt(baslik, 60), Kisalt(metin, 150), 1, o.Id, Adres(o));
    }

    /// <summary>
    /// Balona tıklayınca açılacak adres: Söz işiyse "#/is/&lt;no&gt;", yardımcı oturumuysa "#/suan?oturum=&lt;kimlik&gt;" (arayüz o kartı görünür kılıp vurgular).
    /// Kimlik sırası küçük pencere ile aynıdır (iş, yardımcıdaki kimlik, oturum numarası). Hiçbiri yoksa genel "#/suan".
    /// </summary>
    internal static string Adres(AttentionItemInfo o)
    {
        var kimlik = o.JobId > 0 ? "j:" + o.JobId
            : !string.IsNullOrWhiteSpace(o.ExternalId) ? "x:" + o.ExternalId.Trim()
            : o.SessionId > 0 ? "o:" + o.SessionId
            : "";
        return KabukProtokol.PanelAdresi(kimlik) ?? "#/suan";
    }

    /// <summary>Gösterim işaretlerini ve satır sonlarını sadeleştirir: « » kalkar, yan yana boşluklar teke iner.</summary>
    internal static string Temiz(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Replace("«", "").Replace("»", "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Trim();
    }

    internal static string Kisalt(string s, int en) => s.Length <= en ? s : s[..(en - 1)].TrimEnd() + "…";
}
