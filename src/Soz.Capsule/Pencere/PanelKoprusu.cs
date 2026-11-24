using System.Diagnostics;

namespace Soz.Capsule;

/// <summary>
/// Kapsülden dışarıya giden olaylar: tam panel aç, metin gönder, yeni iş, dikte, paket, ayar aç.
/// Masaüstü programıyla birlikte çalışırken (<see cref="KabukKanali"/> var) hepsi o programa komut olarak gider: panel onun penceresidir.
/// Tek başına çalışırken: <c>--panel-komut &lt;exe&gt;</c> verilmişse "panel aç" o programı <c>--is &lt;kimlik&gt;</c> ile başlatır, öteki olaylar yalnız günlüğe yazılır.
/// Varsayılan olarak tarayıcıda hiçbir şey açılmaz (kullanıcının tarayıcısına dokunulmaz). Başlatılan süreç kapatılmaz; kapsül yalnız izler.
/// </summary>
internal sealed class PanelKoprusu : IDisposable
{
    private readonly string? _komut;
    private readonly KabukKanali? _kanal;
    private Process? _surec;
    private volatile bool _kanalPaneliAcik;

    /// <summary>Kapsülün o anki ekran konumu (pencere iş parçacığında çağrılır). Kabuğa giden her komuta eklenir: panel, tepsiden ya da "yeni iş"ten açılsa da kapsülün GÜNCEL yerine göre yerleşir.</summary>
    public Func<Dik?>? KonumVer { get; set; }

    private KapsulKomutu Dikli(KapsulKomutu k)
        => k.Dik is null && KonumVer?.Invoke() is { } d ? k with { Dik = new KabukDik(d.L, d.T, d.R, d.B) } : k;

    /// <summary>Kapsül yerine oturdu (sürükleme bitti, ekran değişti, açılış): kabuk güncel konumu bilsin diye sessiz bildirim.</summary>
    public void KonumBildir() { if (_kanal != null) _kanal.Gonder(Dikli(new KapsulKomutu(KomutTuru.Konum))); }

    public PanelKoprusu(string? komut, KabukKanali? kanal = null) { _komut = string.IsNullOrWhiteSpace(komut) ? null : komut; _kanal = kanal; }

    public bool KabukVar => _kanal != null && !_kanal.Kopuk;

    public void Gunluk(string s) => Soz.Capsule.Gunluk.Yaz("panel köprüsü: " + s);

    /// <summary>Panel çalışıyor mu? Kabukla birlikteyken kabuğun söylediği (açtım / kapandı), tek başınayken başlatılan süreç yaşıyor mu. Komut yoksa hiç panel yoktur.</summary>
    public bool Calisiyor { get { try { return _kanal != null ? _kanalPaneliAcik : _surec is { HasExited: false }; } catch { return false; } } }

    /// <summary>Kabuk paneli kapattı (odak başka yere gitti, Esc, kullanıcı kapattı).</summary>
    public void KabukPaneliKapandi() => _kanalPaneliAcik = false;

    /// <summary>Tam paneli aç olayı. Dönüş: panel gerçekten açıldı mı. Komut ya da kabuk yoksa false: kapsül panelin kapalı olduğunu bilir.</summary>
    public bool TamPaneliAc(string? isId, Dik? kapsul = null)
    {
        Gunluk("TAM PANEL AÇ is=" + (isId ?? "yok"));
        if (_kanal != null)
        {
            KabukKanali.OnPlanHakkiVer();
            var ok = _kanal.Gonder(new KapsulKomutu(KomutTuru.Panel, isId, null, 0, kapsul is { } d ? new KabukDik(d.L, d.T, d.R, d.B) : null));
            _kanalPaneliAcik = ok;
            return ok;
        }
        if (_komut == null) return false;
        if (Calisiyor) return true;
        try
        {
            var bilgi = new ProcessStartInfo(_komut) { UseShellExecute = false };
            if (isId != null && !isId.StartsWith(OnBakisIcerigi.SayfaOn, StringComparison.Ordinal)) { bilgi.ArgumentList.Add("--is"); bilgi.ArgumentList.Add(isId); }
            _surec = Process.Start(bilgi);
            return _surec != null;
        }
        catch (Exception e) { Gunluk("panel başlatılamadı: " + e.Message); return false; }
    }

    public void TamPaneliKapat()
    {
        if (_kanal != null) { _kanalPaneliAcik = false; _kanal.Gonder(new KapsulKomutu(KomutTuru.PanelKapat)); return; }
        Gunluk("tam panel kapat isteği (süreç kapatılmaz)");
    }

    /// <summary>Yazı satırından metin: kabukla birlikteyken öndeki pencereye yapıştırılır (kabuk karar verir, olmazsa panoda bırakır).</summary>
    public void MetinGonder(string metin, string hedef, long hedefPencere = 0)
    {
        Gunluk($"METİN GÖNDER hedef={hedef} uzunluk={metin.Length}");
        _kanal?.Gonder(Dikli(new KapsulKomutu(KomutTuru.Metin, null, metin, hedefPencere)));
    }

    /// <summary>Ayarlar paneli kabuğun penceresidir: yalnız kabukla birlikteyken açılır (menüde de o zaman görünür). Dönüş: komut gitti mi.</summary>
    public bool AyarAc(string? dosya)
    {
        Gunluk("AYAR AÇ " + (dosya ?? "varsayılan"));
        if (_kanal == null) return false;
        KabukKanali.OnPlanHakkiVer();
        return _kanal.Gonder(Dikli(new KapsulKomutu(KomutTuru.Ayar)));
    }

    public bool HedefSec(string? kip, Dik? kapsul = null)
    {
        Gunluk("HEDEF SEÇ " + (kip ?? ""));
        if (kip == "kapat") return false;
        return TamPaneliAc(null, kapsul);
    }

    /// <summary>Yeni iş, dikte, paket kopyala, şimdi içe al: masaüstü programının işleri. Kabuk yoksa false (pano zaten bu satırları göstermez).</summary>
    public bool Komut(KomutTuru tur)
    {
        Gunluk("KOMUT " + tur);
        if (_kanal == null) return false;
        if (tur == KomutTuru.YeniIs) KabukKanali.OnPlanHakkiVer();
        return _kanal.Gonder(Dikli(new KapsulKomutu(tur)));
    }

    /// <summary>Çıkış: kabukla birlikteyken masaüstü programı da kapanır (eski kapsülün tepsi menüsündeki Çıkış böyleydi).</summary>
    public bool CikisBildir() => _kanal != null && _kanal.Gonder(new KapsulKomutu(KomutTuru.Cikis));

    /// <summary>Kişi küçük pencereyi gizledi ya da yeniden görünür oldu: masaüstü programı tepside geri getirme satırını buna göre gösterir.</summary>
    public void GizlilikBildir(bool gizli) { _kanal?.Gonder(new KapsulKomutu(KomutTuru.Gizli, null, gizli ? "1" : "0")); }

    public void Dispose() { _surec?.Dispose(); _surec = null; _kanal?.Dispose(); }
}
