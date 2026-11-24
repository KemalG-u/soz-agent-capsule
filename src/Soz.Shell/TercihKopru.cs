using Soz.Server.Endpoints;

namespace Soz.Shell;

/// <summary>
/// Sunucudaki <c>/api/shell/prefs</c> ucunu kabuğun gerçek tercihlerine bağlar: açılışta başlama (kayıt defteri), bildirimler, kaldırırken veriyi sil (kabuk tercih dosyası).
/// Tepsi menüsüyle aynı yerlere yazar; ikisi birbirinin değişikliğini görür. Kayıt yolları ve çıkış davranışı dışarıdan verilebilir (yalıtılmış kayıt anahtarıyla sınanır).
/// </summary>
internal sealed class TercihKopru(string veriKlasoru, Func<bool> acilisOku, Func<string?, bool> acilisAc, Action acilisKapat,
    Func<bool> bildirimOku, Action<bool> bildirimYaz, Action? degisti = null)
{
    internal static TercihKopru Gercek(string veriKlasoru, Func<bool> bildirimOku, Action<bool> bildirimYaz, Action? degisti)
        => new(veriKlasoru, () => StartWithWindows.IsEnabled(), exe => StartWithWindows.Enable(exe), () => StartWithWindows.Disable(), bildirimOku, bildirimYaz, degisti);

    public ShellPrefs Oku() => new(acilisOku(), bildirimOku(), KabukTercihleri.Oku(veriKlasoru).KaldirirkenVeriyiSil);

    /// <summary>Verilen alanları yazar. Açılışta başlama açılamazsa (exe yok, kayıt yazılamadı) null döner, hiçbir şey yarım kalmaz.</summary>
    public ShellPrefs? Yaz(bool? acilis, bool? bildirim, bool? veriyiSil)
    {
        try
        {
            var t = KabukTercihleri.Oku(veriKlasoru);
            if (acilis is { } a)
            {
                if (a) { if (!acilisAc(Environment.ProcessPath)) return null; t.AcilistaBasla = true; }
                else { acilisKapat(); t.AcilistaBasla = false; }
            }
            if (bildirim is { } b) { t.Bildirimler = b; bildirimYaz(b); }
            if (veriyiSil is { } v) t.KaldirirkenVeriyiSil = v;
            if (!t.Yaz(veriKlasoru)) return null;
            try { degisti?.Invoke(); } catch (Exception) { }
            return Oku();
        }
        catch (Exception ex) { Log.Error("tercih köprüsü", ex); return null; }
    }

    /// <summary>
    /// Panelden gelen tercih iletisini işler ve sayfaya gidecek cevabı (<c>shell-prefs</c> JSON) döndürür.
    /// <c>get-shell-prefs</c>: mevcut durum. <c>set-shell-pref</c>: tek anahtar yazılır; veriyi silmeyi AÇMAK için <c>confirmed:true</c> gerekir (sayfa önce kişiye sorar),
    /// yazılamazsa ya da bilinmeyen anahtarda değişiklik olmaz ve mevcut gerçek durum yine döner (sayfa anahtarı eski yerine çevirir).
    /// </summary>
    public string Isle(WebMessage m)
    {
        if (m.Kind == WebMessageKind.SetShellPref && m.Value is { } deger)
        {
            switch (m.Key)
            {
                case WebMessages.PrefNotifications: Yaz(null, deger, null); break;
                case WebMessages.PrefStartWithWindows: Yaz(deger, null, null); break;
                case WebMessages.PrefDeleteData:
                    if (!deger || m.Confirmed) Yaz(null, null, deger);
                    else Log.Write("tercih: veriyi silme onaysız açılmak istendi, yok sayıldı");
                    break;
                default: Log.Write("tercih: bilinmeyen anahtar yok sayıldı"); break;
            }
        }
        var p = Oku();
        return WebMessages.ShellPrefsJson(p.Notifications, p.DeleteDataOnUninstall, p.StartWithWindows);
    }

    public void Bagla()
    {
        ShellBridge.Read = Oku;
        ShellBridge.Write = Yaz;
    }
}
