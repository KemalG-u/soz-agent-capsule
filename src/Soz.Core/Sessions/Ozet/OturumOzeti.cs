using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Sessions.Ozet;

/// <summary>Son deneme: kuralın adı (komut değil), sonuç (gecti | kaldi | bilinmiyor), zamanı (ISO UTC) ve kayıtta varsa çıkış kodu.</summary>
public sealed record SonDenemeDto(string KomutTuru, string Sonuc, string? An, int? CikisKodu);

/// <summary>
/// Bir oturumun üç satırlık sade özeti ve makine okur alanları (GET /api/sessions/{id}/ozet). Dil modeli kullanılmaz; yalnız kayıttaki araç olayları.
/// Bekleyen: izin | soru | inceleme | yok. BekleyenKesin=false: kayıt ayırt etmeye yetmiyor (ör. açık komut: çalışıyor mu izin mi bekliyor bilinmez).
/// Guven: yuksek | dusuk (kayıt okunamadı, boş ya da biçimi tanınmadı; bu durumda sayılar 0'dır ve satır bunu söyler).
/// </summary>
public sealed record OturumOzeti(
    long OturumId, string Arac,
    string NeYapti, string DenediMi, string SendenBeklenen,
    int DegisenDosyaSayisi, List<string> DosyaAdlari, int KomutSayisi, SonDenemeDto? SonDeneme,
    bool BittiDedi, bool BittiSonrasiDeneme, bool SonDegisiklikSonrasiDeneme,
    string Bekleyen, bool BekleyenKesin, string Guven, string? GuvenNedeni, string Kaynak, string Hesaplandi)
{
    public OturumOzetiKisa Kisa() => new(NeYapti, DenediMi, SendenBeklenen, Bekleyen, Guven);
}

/// <summary>Oturum listesindeki her öğe için kısa özet (üç satır + bekleyen + güven).</summary>
public sealed record OturumOzetiKisa(string NeYapti, string DenediMi, string SendenBeklenen, string Bekleyen, string Guven);

/// <summary>Veritabanındaki kanca işareti (needs_user_kind/since) ve tur sonu: kaydın tek başına ayırt edemediği "izin bekliyor"u kesinleştirir.</summary>
public sealed record OzetIpuclari(string? NeedsUserKind = null, string? NeedsUserSince = null, bool DbTurnEnded = false);

public static class Bekleyenler
{
    public const string Izin = "izin", Soru = "soru", Inceleme = "inceleme", Yok = "yok";
}

/// <summary>Olgulardan cümle kurar. Saf işlev: aynı girdi, aynı çıktı.</summary>
public static class OzetYazici
{
    public const double CalisiyorSaniye = 90;
    public const double SessizDakika = 5;
    public const int EnCokDosyaAdi = 5;

    public static OturumOzeti Yaz(long id, string arac, OzetBirikimi? b, string? okunamadi, OzetIpuclari ip, DateTime simdi, DateTime? dosyaZamani)
    {
        string? guvenNedeni = okunamadi;
        if (b is not null && guvenNedeni is null)
        {
            if (b.TaninmayanBicim || b.Satir > 0 && b.Taninan == 0) guvenNedeni = "Kayıt biçimini tanımadım.";
            else if (b.Satir == 0) guvenNedeni = "Kayıt henüz boş.";
            else if (b.BozukSatir * 2 > b.Satir) guvenNedeni = "Kaydın çoğu bozuk.";
        }
        var hesaplandi = Time.Iso(simdi);
        if (guvenNedeni is not null || b is null)
        {
            guvenNedeni ??= "Bu kaydı okuyamadım.";
            var (bek, kesin, cumle, kanca) = KancadanBekleyen(ip, null, null) ?? DbdenBekleyen(ip);
            return new OturumOzeti(id, arac,
                guvenNedeni == "Kayıt henüz boş." ? "Kayıt henüz boş, ne yaptığını gösteremiyorum." : "Bu kaydı okuyamadım, ne yaptığını gösteremiyorum.",
                "Deneyip denemediğini bilmiyorum.", cumle,
                0, new List<string>(), 0, null, false, false, false, bek, kesin, "dusuk", guvenNedeni, kanca ? "kanca" : "yok", hesaplandi);
        }

        var bittiDedi = b.SonIddiaSira > 0 && b.SonIddiaSira > b.SonInsanSira;
        var d = b.SonDeneme;
        var bittiSonrasi = bittiDedi && d is not null && d.Sira > b.SonIddiaSira;
        var degisiklikSonrasi = d is not null && d.Sira > b.SonDegisiklikSira;

        var neYapti = NeYapti(b.DegisenDosyaSayisi, b.DosyaAdlari(EnCokDosyaAdi), b.KomutSayisi);
        var denedi = DenediMi(bittiDedi, bittiSonrasi, degisiklikSonrasi, d, simdi);

        var son = Max(Time.Parse(b.SonOlayAn), dosyaZamani);
        var yas = son is null ? TimeSpan.MaxValue : simdi - son.Value;
        var (bekleyen, bekleyenKesin, beklenen, kancaKullanildi) = KancadanBekleyen(ip, b.SonOlayAn, b.SonAcikCagri) ?? KayittanBekleyen(b, bittiDedi, yas);

        return new OturumOzeti(id, arac, neYapti, denedi, beklenen,
            b.DegisenDosyaSayisi, b.DosyaAdlari(EnCokDosyaAdi), b.KomutSayisi,
            d is null ? null : new SonDenemeDto(d.KomutTuru, d.Sonuc, d.An, d.CikisKodu),
            bittiDedi, bittiSonrasi, degisiklikSonrasi, bekleyen, bekleyenKesin, "yuksek", null, kancaKullanildi ? "kayit+kanca" : "kayit", hesaplandi);
    }

    public static string NeYapti(int dosya, IReadOnlyList<string> adlar, int komut)
    {
        if (dosya == 0 && komut == 0) return "Henüz dosya değiştirmedi, komut da çalıştırmadı.";
        if (dosya == 0) return $"Dosya değiştirmedi, {komut} komut çalıştırdı.";
        string liste;
        if (adlar.Count == 0) liste = "";
        else if (dosya == 1 || adlar.Count == 1) liste = $" ({adlar[0]}{(dosya > 1 ? $" ve {dosya - 1} dosya daha" : "")})";
        else if (dosya == 2) liste = $" ({adlar[0]} ve {adlar[1]})";
        else if (dosya == 3 && adlar.Count >= 3) liste = $" ({adlar[0]}, {adlar[1]} ve {adlar[2]})";
        else liste = $" ({adlar[0]}, {adlar[1]} ve {dosya - 2} dosya daha)";
        var bas = $"{dosya} dosyayı değiştirdi{liste}";
        return komut == 0 ? bas + ", komut çalıştırmadı." : $"{bas}, {komut} komut çalıştırdı.";
    }

    public static string DenediMi(bool bittiDedi, bool bittiSonrasi, bool degisiklikSonrasi, DenemeIzi? d, DateTime simdi)
    {
        if (!bittiDedi)
        {
            if (d is null) return "Henüz bitti demedi.";
            return d.Sonuc switch
            {
                DenemeSonuc.Gecti => $"Henüz bitti demedi. Son denemesi geçti ({Ek(d, simdi)}).",
                DenemeSonuc.Kaldi => $"Henüz bitti demedi. Son denemesi hata verdi ({Ek(d, simdi)}).",
                _ => $"Henüz bitti demedi. Son denemesinin sonucunu kayıtta göremedim ({Ek(d, simdi)}).",
            };
        }
        if (bittiSonrasi && d is not null)
            return d.Sonuc switch
            {
                DenemeSonuc.Gecti => $"Bitti dedi, sonra denedi: son denemesi geçti ({Ek(d, simdi)}).",
                DenemeSonuc.Kaldi => $"Bitti dedi ama sonraki denemesi hata verdi ({Ek(d, simdi)}).",
                _ => $"Bitti dedi, sonra denedi ama sonucunu kayıtta göremedim ({Ek(d, simdi)}).",
            };
        if (degisiklikSonrasi && d is not null)
            return d.Sonuc switch
            {
                DenemeSonuc.Gecti => $"Bitti dedi. Son denemesi geçti ({Ek(d, simdi)}).",
                DenemeSonuc.Kaldi => $"Bitti dedi ama son denemesi hata verdi ({Ek(d, simdi)}).",
                _ => $"Bitti dedi ama son denemesinin sonucunu kayıtta göremedim ({Ek(d, simdi)}).",
            };
        if (d is not null) return "Bitti dedi ama son değişiklikten sonra hiçbir deneme çalıştırmadı.";
        return "Bitti dedi ama sonrasında hiçbir deneme çalıştırmadı.";
    }

    private static string Ek(DenemeIzi d, DateTime simdi)
    {
        var t = Time.Parse(d.An);
        return t is null ? d.KomutTuru : $"{d.KomutTuru}, {Once(simdi - t.Value)}";
    }

    public static string Once(TimeSpan f)
    {
        if (f.TotalMinutes < 1) return "az önce";
        if (f.TotalHours < 1) return $"{(int)f.TotalMinutes} dk önce";
        if (f.TotalDays < 1) return $"{(int)f.TotalHours} saat önce";
        return $"{(int)f.TotalDays} gün önce";
    }

    /// <summary>Kanca işareti hâlâ geçerliyse (işaretten sonra kayıtta yeni olay yok) onun dediği. Değilse null.</summary>
    private static (string, bool, string, bool)? KancadanBekleyen(OzetIpuclari ip, string? sonOlayAn, AcikCagri? acik)
    {
        if (ip.NeedsUserKind is null || ip.NeedsUserSince is null) return null;
        var since = Time.Parse(ip.NeedsUserSince);
        var son = Time.Parse(sonOlayAn);
        if (since is not null && son is not null && son.Value > since.Value.AddSeconds(2)) return null; // işaretten sonra iş sürdü: eskidi
        if (ip.NeedsUserKind == "permission_prompt")
            return (Bekleyenler.Izin, true, "İzin bekliyor: " + IstedigiIs(acik) + ".", true);
        if (ip.NeedsUserKind is "elicitation_dialog" or "agent_needs_input")
            return (Bekleyenler.Soru, true, "Sana soru sordu.", true);
        if (ip.NeedsUserKind.StartsWith(Soz.Core.Attention.FailureProbe.HookKindPrefix, StringComparison.Ordinal))
            return (Bekleyenler.Inceleme, true, "Hata verip durdu, bakmanı bekliyor.", true);
        return null;
    }

    private static (string, bool, string, bool) DbdenBekleyen(OzetIpuclari ip)
        => ip.DbTurnEnded ? (Bekleyenler.Inceleme, false, "Turunu bitirmiş görünüyor, bakmanı bekliyor olabilir.", false)
                          : (Bekleyenler.Yok, false, "Senden ne beklediğini kayıttan çıkaramadım.", false);

    private static string IstedigiIs(AcikCagri? a) => a?.Tur switch
    {
        "komut" => "bir komut çalıştırmak istiyor",
        "dosya" => a.DosyaAdi is null ? "bir dosyayı değiştirmek istiyor" : $"bir dosyayı değiştirmek istiyor ({a.DosyaAdi})",
        "plan" => "planını onaylamanı istiyor",
        _ => "bir işlem yapmak istiyor",
    };

    private static (string, bool, string, bool) KayittanBekleyen(OzetBirikimi b, bool bittiDedi, TimeSpan yas)
    {
        if (b.OnayIstegi is { } o)
            return o.Tur == "soru" ? (Bekleyenler.Soru, true, "Sana soru sordu.", false) : (Bekleyenler.Izin, true, "İzin bekliyor: " + IstedigiIs(o) + ".", false);
        if (b.SonAcikCagri is { } a)
        {
            if (a.Tur == "soru") return (Bekleyenler.Soru, true, "Sana soru sordu.", false);
            if (a.Tur == "plan") return (Bekleyenler.Izin, true, "İzin bekliyor: planını onaylamanı istiyor.", false);
            if (yas.TotalSeconds <= CalisiyorSaniye) return (Bekleyenler.Yok, true, "Bir şey beklemiyor, çalışıyor.", false);
            return (Bekleyenler.Yok, false, "Bir işlem yapıyor ya da senden izin bekliyor olabilir, kayıttan ayırt edemiyorum.", false);
        }
        if (b.TurBitti)
        {
            if (b.SonMetinSoruyor && b.SonMetinSira >= b.SonAracSira) return (Bekleyenler.Soru, true, "Sana soru sordu.", false);
            if (bittiDedi) return (Bekleyenler.Inceleme, true, "Bitti, bakmanı bekliyor.", false);
            return (Bekleyenler.Inceleme, true, "Turunu bitirdi, sıra sende.", false);
        }
        if (yas.TotalMinutes <= SessizDakika) return (Bekleyenler.Yok, true, "Bir şey beklemiyor, çalışıyor.", false);
        return (Bekleyenler.Yok, false, "Bir süredir sessiz, takılmış olabilir.", false);
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);
}

/// <summary>
/// Oturum özetlerinin giriş noktası. Kayıt dosyası mevcut okuyucuların veritabanına yazdığı yoldan (sessions.file_path) bulunur.
/// Sonuç dosya başına bellekte önbelleklenir ve dosya büyüdükçe yalnız yeni kısmı okunur (dosya uzunluğu ve son değişim zamanına göre).
/// Hiçbir şey yazılmaz: veritabanına, günlüğe ya da diske sohbet metni girmez.
/// </summary>
public static class OturumOzetleri
{
    private static readonly ConcurrentDictionary<string, OzetBirikimi> Onbellek = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<(string tool, string path)> IsitmeKuyrugu = new();
    private static int _isiniyor;
    public const int EnCokOnbellek = 3000;

    /// <summary>Yalnız Claude Code ve Codex kaydı ayrıntılı okunur; ötekiler düşük güvenle döner.</summary>
    public static bool Destekli(string tool) => tool is "claude" or "codex";

    public sealed record OturumSatiri(long Id, string Tool, string? FilePath, OzetIpuclari Ipuclari);

    public static OturumSatiri? Satir(SqliteConnection c, long id)
    {
        using var cmd = c.Cmd("SELECT id, tool, file_path, needs_user_kind, needs_user_since, turn_ended FROM sessions WHERE id=@id", null, ("@id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new OturumSatiri(r.GetInt64(0), r.GetString(1), r.Str(2), new OzetIpuclari(r.Str(3), r.Str(4), r.Long(5) == 1));
    }

    /// <summary>Tek oturumun tam özeti; oturum yoksa null. İlk çağrıda kayıt baştan okunur (büyük dosyada saniyeler sürebilir), sonra yalnız eklenen kısım.</summary>
    public static OturumOzeti? Getir(SqliteConnection c, long id, DateTime? simdi = null)
    {
        var s = Satir(c, id);
        return s is null ? null : Hesapla(s, simdi ?? Time.UtcNow(), okumaSiniri: null);
    }

    /// <summary>
    /// Yardımcının kendi oturum kimliğiyle özet (Projelerim listesi bu kimliği taşır). Önce Söz'ün oturum tablosu, yoksa tarama tablosundaki (profile_session)
    /// kayıt dosyası; ikisinde de yoksa null. Tarama tablosundan gelen oturumun Söz kimliği 0'dır ve kanca işareti yoktur.
    /// </summary>
    public static OturumOzeti? GetirDisKimlik(SqliteConnection c, string tool, string externalId, DateTime? simdi = null)
    {
        var now = simdi ?? Time.UtcNow();
        if (c.Scalar("SELECT id FROM sessions WHERE tool=@t AND external_id=@e", null, ("@t", tool), ("@e", externalId)) is { } id)
            return Getir(c, Convert.ToInt64(id), now);
        string? yol;
        try
        {
            yol = c.Scalar("""
                SELECT record_path FROM profile_session WHERE tool=@t AND session_id=@e AND record_path IS NOT NULL
                ORDER BY (record_path LIKE '%.jsonl') DESC, present DESC, record_modified_at DESC LIMIT 1
                """, null, ("@t", tool), ("@e", externalId)) as string;
        }
        catch (SqliteException) { return null; }
        if (yol is null) return null;
        return Hesapla(new OturumSatiri(0, tool, yol, new OzetIpuclari()), now, okumaSiniri: null);
    }

    /// <summary>Okuma sınırı verilirse ve okunmamış kısım ondan büyükse null döner (çağıran sonra arka planda ısıtır).</summary>
    public static OturumOzeti? Hesapla(OturumSatiri s, DateTime simdi, long? okumaSiniri)
    {
        if (!Destekli(s.Tool))
            return OzetYazici.Yaz(s.Id, s.Tool, null, "Bu yardımcının kaydını henüz ayrıntılı okuyamıyorum.", s.Ipuclari, simdi, null);
        if (string.IsNullOrWhiteSpace(s.FilePath))
            return OzetYazici.Yaz(s.Id, s.Tool, null, "Kayıt dosyası bilinmiyor.", s.Ipuclari, simdi, null);
        var (b, neden, zaman, bekliyor) = Birikim(s.Tool, s.FilePath, okumaSiniri);
        if (bekliyor) return null;
        if (b is null) return OzetYazici.Yaz(s.Id, s.Tool, null, neden, s.Ipuclari, simdi, zaman);
        lock (b.Kilit) return OzetYazici.Yaz(s.Id, s.Tool, b, null, s.Ipuclari, simdi, zaman);
    }

    /// <summary>
    /// Oturum listesi için kısa özetler. Önbellekte hazır olan ya da az okuma gerektirenler hemen hesaplanır (toplam süre bütçesi içinde);
    /// kalanlar arka planda ısıtılır ve sözlükte yer almaz (arayüz bir sonraki yenilemede görür ya da tek oturum ucunu çağırır).
    /// </summary>
    public static Dictionary<long, OturumOzetiKisa> Kisalar(SqliteConnection c, IEnumerable<long> ids, TimeSpan butce, DateTime? simdi = null)
    {
        var now = simdi ?? Time.UtcNow();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sonuc = new Dictionary<long, OturumOzetiKisa>();
        foreach (var id in ids)
        {
            var s = Satir(c, id);
            if (s is null) continue;
            long? sinir = sw.Elapsed < butce ? 4L << 20 : 0; // bütçe bittiyse yalnız önbellekte tam hazır olanlar
            var o = Hesapla(s, now, sinir);
            if (o is not null) sonuc[id] = o.Kisa();
            else if (s.FilePath is not null) Isit(s.Tool, s.FilePath);
        }
        return sonuc;
    }

    /// <summary>Dosyayı arka planda okumaya sıraya koyar (tek işçi, sırayla).</summary>
    public static void Isit(string tool, string path)
    {
        IsitmeKuyrugu.Enqueue((tool, path));
        Basla();
    }

    private static void Basla()
    {
        if (Interlocked.CompareExchange(ref _isiniyor, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (IsitmeKuyrugu.TryDequeue(out var x))
                {
                    try { Birikim(x.tool, x.path, null); } catch (Exception) { /* özet isteğe bağlıdır: ısıtma hatası sessiz geçer, tek oturum ucu nedenini söyler */ }
                }
            }
            finally { Interlocked.Exchange(ref _isiniyor, 0); }
            if (!IsitmeKuyrugu.IsEmpty) Basla(); // bırakırken araya giren iş kalmasın
        });
    }

    /// <summary>Isıtma kuyruğu boşalana kadar bekler (testler için).</summary>
    public static bool IsitmeBitti(TimeSpan enCok)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < enCok) { if (IsitmeKuyrugu.IsEmpty && Volatile.Read(ref _isiniyor) == 0) return true; Thread.Sleep(20); }
        return false;
    }

    public static void OnbellegiBosalt() => Onbellek.Clear();

    /// <summary>Dosyanın birikimi (gerekirse kaldığı yerden okur). okumaSiniri: okunmamış bayt bundan fazlaysa okumadan bekliyor=true döner.</summary>
    internal static (OzetBirikimi? b, string? neden, DateTime? zaman, bool bekliyor) Birikim(string tool, string path, long? okumaSiniri)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists) return (null, "Kayıt dosyası bulunamadı.", null, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (null, "Kayıt dosyası okunamadı.", null, false); }
        var mtime = fi.LastWriteTimeUtc;
        var b = Onbellek.GetOrAdd(path, _ => new OzetBirikimi(tool));
        lock (b.Kilit)
        {
            if (b.Bicim != tool || fi.Length < b.Offset)
            {
                // Dosya kısaldı ya da başka araca ait: baştan.
                var yeni = new OzetBirikimi(tool);
                Onbellek[path] = yeni;
                return Birikim(tool, path, okumaSiniri);
            }
            if (b.Length == fi.Length && b.MtimeTicks == mtime.Ticks && b.Offset >= fi.Length) return (b, null, mtime, false);
            var okunmamis = fi.Length - b.Offset;
            if (okumaSiniri is { } sinir && okunmamis > sinir) return (null, null, mtime, true);
            try { b.Oku(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Onbellek.TryRemove(path, out _);
                return (null, "Kayıt dosyası okunamadı.", mtime, false);
            }
            b.MtimeTicks = mtime.Ticks;
        }
        if (Onbellek.Count > EnCokOnbellek) Onbellek.Clear();
        return (b, null, mtime, false);
    }
}
