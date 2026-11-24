using Velopack;

namespace Soz.Shell;

/// <summary>
/// Güncelleme denetimi (Velopack). Adres sırası ve kapatma anahtarı <see cref="GuncellemeAdresi"/> içinde: ortam değişkeni <c>SOZ_UPDATE_URL</c>,
/// <c>shell-tercihleri.json</c> içinde <c>guncellemeAdresi</c>, sonra Söz'ün kendi beslemesi. Kural: açılıştan kısa süre sonra ve 6 saatte bir sessiz denetim,
/// yeni sürüm varsa kullanıcıya "şimdi kur" seçeneği sunulur, ZORLA kurma yoktur, ağ yoksa ya da adres ulaşılamazsa sessiz geçilir.
/// Kurulumla gelmeyen kopyada (geliştirme, sınama, yayın klasörü) ağa hiç çıkılmaz. Sahte paket riskine karşı yalnız https adresleri, yerel klasörler ve 127.0.0.1 kabul edilir.
/// Sürüm düşürmeye izin verilmez (<c>UpdateOptions.AllowVersionDowngrade</c> kullanılmaz): geri dönüş elle yapılır.
/// </summary>
internal sealed class GuncellemeDenetimi
{
    internal const string AdresEnv = "SOZ_UPDATE_URL";

    internal enum Durum { AdresYok, KurulumDegil, Guncel, YeniSurum, Ulasilamadi, AdresGecersiz }

    internal sealed record Sonuc(Durum Durum, string? YeniSurum = null, string? Ayrinti = null);

    private readonly string _adres;
    private UpdateManager? _mgr;
    private UpdateInfo? _bilgi;

    public GuncellemeDenetimi(string adres) { _adres = adres; }

    /// <summary>Adres ve kapatma durumu (sıra: ortam değişkeni, tercih dosyası, varsayılan besleme). Adres null ise denetim yapılmaz.</summary>
    public static (string? Adres, bool Kapali) AdresBul(string veriKlasoru)
        => GuncellemeAdresi.Coz(Environment.GetEnvironmentVariable(AdresEnv), KabukTercihleri.Oku(veriKlasoru));

    /// <summary>https, loopback http ve yerel/ağ klasörü kabul. Düz http ile uzak adres reddedilir (paket yolda değiştirilebilir).</summary>
    internal static bool AdresGuvenli(string adres)
    {
        if (string.IsNullOrWhiteSpace(adres)) return false;
        if (Uri.TryCreate(adres, UriKind.Absolute, out var u))
        {
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            if (u.Scheme == Uri.UriSchemeHttp) return u.IsLoopback;
            if (u.IsFile) return true; // file:// ve sürücü yolu (C:\...) ve UNC
            return false;
        }
        return false;
    }

    public async Task<Sonuc> DenetleAsync(CancellationToken ct = default)
    {
        if (!AdresGuvenli(_adres)) return new Sonuc(Durum.AdresGecersiz, null, "adres https, 127.0.0.1 ya da yerel klasör olmalı");
        try
        {
            if (Program.KurulumKoku() == null) return new Sonuc(Durum.KurulumDegil); // taşınabilir ya da geliştirme klasörü: güncelleyecek kurulum yok, ağa hiç çıkılmaz
            _mgr ??= new UpdateManager(_adres);
            if (!_mgr.IsInstalled) return new Sonuc(Durum.KurulumDegil);
            _bilgi = await _mgr.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (_bilgi == null) return new Sonuc(Durum.Guncel);
            return new Sonuc(Durum.YeniSurum, _bilgi.TargetFullRelease.Version.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Sonuc(Durum.Ulasilamadi, null, ex.GetType().Name); }
    }

    /// <summary>Yalnız kullanıcı "şimdi kur" dediğinde çağrılır: yeni sürümü indirir. Başarısızsa false (çalışan sürüm bozulmaz).</summary>
    public async Task<bool> IndirAsync(CancellationToken ct = default)
    {
        try
        {
            if (_mgr == null || _bilgi == null) return false;
            await _mgr.DownloadUpdatesAsync(_bilgi, cancelToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) { Log.Write("güncelleme indirilemedi: " + ex.GetType().Name + ": " + ex.Message); return false; }
    }

    /// <summary>
    /// İndirilen sürümü uygulayacak yardımcıyı başlatır: o, Söz kapanana kadar bekler (en çok 60 sn), sonra kurar ve Söz'ü yeniden açar.
    /// Çağıran hemen ardından Söz'ü düzgünce kapatmalıdır (işler durdurulur, kancalar geri çevrilir). Başlatılamazsa false, Söz açık kalır.
    /// </summary>
    public bool UygulamayiBaslat()
    {
        try
        {
            if (_mgr == null || _bilgi == null) return false;
            _mgr.WaitExitThenApplyUpdates(_bilgi.TargetFullRelease, silent: false, restart: true);
            return true;
        }
        catch (Exception ex) { Log.Write("güncelleme uygulanamadı: " + ex.GetType().Name + ": " + ex.Message); return false; }
    }
}
