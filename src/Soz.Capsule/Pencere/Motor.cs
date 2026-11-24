using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Soz.Capsule;

/// <summary>
/// Motorla (Söz sunucusu, 127.0.0.1) konuşan en küçük HTTP istemcisi. HttpClient yığını yüklenmez (bellek): düz soket, HTTP/1.1,
/// yalnız GET. Cevap Content-Length, chunked ya da bağlantı kapanışıyla biter. Yazma uçları kapsülün işi değil, belirteç gerekmez.
/// </summary>
internal static class MiniHttp
{
    public static async Task<(int Durum, string Govde)> GetAsync(string host, int port, string yol, CancellationToken ct, int zamanAsimiMs = 4000)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var zaman = CancellationTokenSource.CreateLinkedTokenSource(ct); zaman.CancelAfter(zamanAsimiMs);
        await s.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port), zaman.Token).ConfigureAwait(false);
        await using var ns = new NetworkStream(s, false);
        var istek = Encoding.ASCII.GetBytes($"GET {yol} HTTP/1.1\r\nHost: {host}:{port}\r\nAccept: application/json\r\nConnection: close\r\n\r\n");
        await ns.WriteAsync(istek, zaman.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[4096]; int n;
        while ((n = await ns.ReadAsync(buf, zaman.Token).ConfigureAwait(false)) > 0) ms.Write(buf, 0, n);
        return Ayristir(ms.ToArray());
    }

    internal static (int Durum, string Govde) Ayristir(byte[] ham)
    {
        var bitis = IndexOf(ham, "\r\n\r\n"u8);
        if (bitis < 0) return (0, "");
        var baslik = Encoding.ASCII.GetString(ham, 0, bitis);
        var satirlar = baslik.Split("\r\n");
        var durum = 0;
        var p = satirlar[0].Split(' ');
        if (p.Length >= 2) int.TryParse(p[1], out durum);
        var govde = new ArraySegment<byte>(ham, bitis + 4, ham.Length - bitis - 4).ToArray();
        if (baslik.Contains("chunked", StringComparison.OrdinalIgnoreCase)) govde = ChunkCoz(govde);
        return (durum, Encoding.UTF8.GetString(govde));
    }

    private static byte[] ChunkCoz(byte[] g)
    {
        var o = new MemoryStream(); var i = 0;
        while (i < g.Length)
        {
            var e = IndexOf(g, "\r\n"u8, i); if (e < 0) break;
            var hex = Encoding.ASCII.GetString(g, i, e - i); var sc = hex.IndexOf(';'); if (sc >= 0) hex = hex[..sc];
            if (!int.TryParse(hex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var len) || len == 0) break;
            i = e + 2; var al = Math.Min(len, g.Length - i); o.Write(g, i, al); i += len + 2;
        }
        return o.ToArray();
    }

    private static int IndexOf(byte[] a, ReadOnlySpan<byte> k, int from = 0)
    {
        var i = a.AsSpan(from).IndexOf(k);
        return i < 0 ? -1 : i + from;
    }

    /// <summary>SSE akışı: her olay bloğunda <paramref name="olay"/> (olay adı; adsız "data:" bloğunda boş metin). Akış kopunca döner (istisna atabilir).</summary>
    public static async Task AkisAsync(string host, int port, string yol, Action<string> olay, Action baglandi, CancellationToken ct)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var bag = CancellationTokenSource.CreateLinkedTokenSource(ct); bag.CancelAfter(4000);
        await s.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port), bag.Token).ConfigureAwait(false);
        await using var ns = new NetworkStream(s, false);
        var istek = Encoding.ASCII.GetBytes($"GET {yol} HTTP/1.1\r\nHost: {host}:{port}\r\nAccept: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n");
        await ns.WriteAsync(istek, bag.Token).ConfigureAwait(false);
        var okuyucu = new SatirOkuyucu(ns);
        // başlıklar
        string? satir; var chunked = false; var ilk = true;
        while ((satir = await okuyucu.SatirAsync(ct, 8000).ConfigureAwait(false)) != null && satir.Length > 0)
        {
            if (ilk) { ilk = false; if (!satir.Contains(" 200")) throw new IOException("SSE " + satir); }
            if (satir.StartsWith("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && satir.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
        }
        baglandi();
        var metin = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            // 45 sn sessizlik (sunucu 20 sn'de bir ": canlı" yollar; iki nabız kaçarsa) bağlantı kopmuş sayılır: donmuş motor "belirsiz" olur
            byte[]? parca = chunked ? await okuyucu.ChunkAsync(ct, 45000).ConfigureAwait(false) : await okuyucu.HamAsync(ct, 45000).ConfigureAwait(false);
            if (parca == null) return;
            metin.Append(Encoding.UTF8.GetString(parca));
            string? m; while ((m = Olay(metin)) != null) { if (m.Length > 0) olay(m == "x" ? "" : m); }
        }
    }

    /// <summary>Tamponda tam bir olay bloğu varsa (boş satırla biten) çıkarır: olay adı, adsız veri bloğunda "x", yalnız yorum satırıysa boş metin.</summary>
    private static string? Olay(StringBuilder sb)
    {
        var t = sb.ToString(); var i = t.IndexOf("\n\n", StringComparison.Ordinal);
        if (i < 0) { i = t.IndexOf("\r\n\r\n", StringComparison.Ordinal); if (i < 0) return null; }
        var blok = t[..i]; sb.Remove(0, Math.Min(sb.Length, i + (t[i] == '\r' ? 4 : 2)));
        string? ad = null; var veri = false;
        foreach (var l in blok.Split('\n'))
        {
            var k = l.Trim();
            if (k.StartsWith("event:")) ad = k[6..].Trim();
            else if (k.StartsWith("data:")) veri = true;
        }
        return !string.IsNullOrEmpty(ad) ? ad : veri || ad != null ? "x" : "";
    }

    private sealed class SatirOkuyucu
    {
        private readonly NetworkStream _ns; private readonly byte[] _buf = new byte[4096]; private int _i, _n;
        public SatirOkuyucu(NetworkStream ns) { _ns = ns; }

        private async Task<bool> DoldurAsync(CancellationToken ct, int ms)
        {
            using var z = CancellationTokenSource.CreateLinkedTokenSource(ct); z.CancelAfter(ms);
            _n = await _ns.ReadAsync(_buf, z.Token).ConfigureAwait(false); _i = 0;
            return _n > 0;
        }
        private async Task<int> ByteAsync(CancellationToken ct, int ms)
        {
            if (_i >= _n && !await DoldurAsync(ct, ms).ConfigureAwait(false)) return -1;
            return _buf[_i++];
        }
        public async Task<string?> SatirAsync(CancellationToken ct, int ms)
        {
            var sb = new StringBuilder(); int b;
            while ((b = await ByteAsync(ct, ms).ConfigureAwait(false)) >= 0) { if (b == '\n') return sb.ToString().TrimEnd('\r'); sb.Append((char)b); }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        public async Task<byte[]?> HamAsync(CancellationToken ct, int ms)
        {
            if (_i >= _n && !await DoldurAsync(ct, ms).ConfigureAwait(false)) return null;
            var r = new byte[_n - _i]; Array.Copy(_buf, _i, r, 0, r.Length); _i = _n; return r;
        }
        public async Task<byte[]?> ChunkAsync(CancellationToken ct, int ms)
        {
            var boyut = await SatirAsync(ct, ms).ConfigureAwait(false);
            if (boyut == null) return null;
            if (boyut.Length == 0) boyut = await SatirAsync(ct, ms).ConfigureAwait(false) ?? "";
            var sc = boyut.IndexOf(';'); if (sc >= 0) boyut = boyut[..sc];
            if (!int.TryParse(boyut.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var len) || len == 0) return null;
            var r = new byte[len]; var k = 0;
            while (k < len)
            {
                if (_i >= _n && !await DoldurAsync(ct, ms).ConfigureAwait(false)) return null;
                var al = Math.Min(len - k, _n - _i); Array.Copy(_buf, _i, r, k, al); _i += al; k += al;
            }
            return r;
        }
    }
}

internal sealed record SunucuKaydi(string Host, int Port, int Pid);

/// <summary>Motor bağlantısının kapsüle teslim ettiği durum. Isler null: nötr (motor yok). Ilk: kapsül açıldıktan sonraki ilk canlı liste (uyandırmaz). Kesin: canlı motordan geldi.</summary>
internal readonly record struct MotorTeslimi(IReadOnlyList<Is>? Isler, bool Ilk, bool Kesin);

/// <summary>
/// Canlı motoru bulur (<c>sunucu.json</c> = {url, pid, startedAt}), "Sıra bende" listesini alır ve değişim olaylarını dinler.
/// Olay güdümlüdür: boştayken saat uyandırması yoktur (yalnız sunucunun 20 sn'lik "canlı" satırı soketi uyandırır). Akış kopunca 0,5 sn'den 5 sn'ye kadar
/// artan beklemeyle yeniden dener, bağlanınca listeyi yeniden alır ve <c>since</c> (son bilinen sürüm) ile kopukken kaçırılanı öğrenir.
/// Kayıt hiç yokken (Söz kapalı) hiçbir şey göstermez (nötr). Kayıt var ama motora ulaşılamıyorsa (süreç ölmüş ya da yanıt vermiyor) 5 sn sonra "bilgiye ulaşamıyorum" gösterir ve motor dönene kadar öyle kalır (<see cref="BaglantiKarar"/>).
/// Eski motor (<c>/api/attention</c> yok, 404) ise <c>/api/now</c> ile eskisi gibi çalışır.
/// </summary>
internal sealed class MotorIstemci : IDisposable
{
    private readonly string? _veriKlasoru;
    private readonly Action<MotorTeslimi> _teslim;
    private readonly CancellationTokenSource _cts = new();
    private Task? _dongu;
    public volatile bool Bagli;
    public string? SonHata;
    public long SonRev;
    /// <summary>Son teslimin günlük özeti (deneme betikleri ve ölçüm için).</summary>
    public string SonOzet = "";

    public MotorIstemci(string? veriKlasoru, Action<MotorTeslimi> teslim) { _veriKlasoru = veriKlasoru; _teslim = teslim; }

    public static string VeriKlasoruBul(string? verilen)
    {
        if (!string.IsNullOrWhiteSpace(verilen)) return Path.GetFullPath(verilen);
        var env = Environment.GetEnvironmentVariable("SOZ_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var exe = AppContext.BaseDirectory;
        var yan = Path.Combine(exe, "veri"); if (Directory.Exists(yan)) return yan;
        var ust = Path.Combine(exe, "..", "veri"); if (File.Exists(Path.Combine(ust, "soz.db"))) return Path.GetFullPath(ust);
        return yan;
    }

    public static SunucuKaydi? KayitOku(string veriKlasoru)
    {
        try
        {
            var f = Path.Combine(veriKlasoru, "sunucu.json");
            if (!File.Exists(f)) return null;
            using var d = JsonDocument.Parse(File.ReadAllText(f));
            var url = d.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            var pid = d.RootElement.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return new SunucuKaydi(uri.Host == "localhost" ? "127.0.0.1" : uri.Host, uri.Port, pid);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static bool SozSureciMi(int pid)
    {
        if (pid <= 0) return true; // bilinmiyorsa dene; bağlanamazsa zaten düşer
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p.HasExited) return false;
            var n = p.ProcessName;
            return n.StartsWith("soz", StringComparison.OrdinalIgnoreCase) || n.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public void Baslat() { _dongu = Task.Run(() => DonguAsync(_cts.Token)); }

    private bool _eskiMotor;   // /api/attention yok: /api/now kipi

    /// <summary>Listeyi alır: yeni motorda /api/attention?since, eski motorda /api/now. Ulaşılamaz ya da biçim bozuksa istisna (bağlantı kopuk sayılır).</summary>
    private async Task<IReadOnlyList<Is>> ListeAl(SunucuKaydi k, CancellationToken ct)
    {
        if (!_eskiMotor)
        {
            var (durum, govde) = await MiniHttp.GetAsync(k.Host, k.Port, SonRev > 0 ? $"/api/attention?since={SonRev}" : "/api/attention", ct).ConfigureAwait(false);
            if (durum == 200)
            {
                using var d = JsonDocument.Parse(govde);
                var c = SiraBendeCevabi.Ayristir(d.RootElement) ?? throw new IOException("/api/attention cevabı anlaşılamadı");
                SonRev = Math.Max(SonRev, c.Rev);
                SonOzet = $"rev {c.Rev}, {c.Isler.Count(x => x.Bekliyor)} bekleyen, {c.Isler.Count(x => x.Tur == IsTuru.Calisiyor)} çalışan, {c.Isler.Count(x => x.Tur == IsTuru.Belirsiz)} belirsiz, hak {c.Harcanan}/{c.GunlukHak}, sessiz {c.Sessiz}";
                return c.Isler;
            }
            if (durum != 404) throw new IOException("/api/attention durum " + durum);
            _eskiMotor = true; Gunluk.Yaz("motor: /api/attention yok (eski motor), /api/now ile devam");
        }
        var (d2, g2) = await MiniHttp.GetAsync(k.Host, k.Port, "/api/now", ct).ConfigureAwait(false);
        if (d2 != 200) throw new IOException("/api/now durum " + d2);
        using var dd = JsonDocument.Parse(g2);
        var liste = MotorCevabi.NowAyristir(dd.RootElement);
        SonOzet = $"/api/now, {liste.Count(x => x.Bekliyor)} bekleyen, {liste.Count(x => x.Tur == IsTuru.Calisiyor)} çalışan";
        return liste;
    }

    private async Task DonguAsync(CancellationToken ct)
    {
        var deneme = 0; var ilk = true; long? kopuk0 = null;
        BaglantiGorunumu? sonG = null; SunucuKaydi? sonKayit = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Gorunum(BaglantiGorunumu g, string neden)
        {
            if (sonG == g) return;
            sonG = g; Gunluk.Yaz($"motor: {neden} → {g}");
            if (g == BaglantiGorunumu.Notr) _teslim(new MotorTeslimi(null, false, false));
            else if (g == BaglantiGorunumu.Belirsiz) _teslim(new MotorTeslimi(new[] { BaglantiKarar.BelirsizSatir() }, false, false));
        }
        while (!ct.IsCancellationRequested)
        {
            SunucuKaydi? k = null;
            try
            {
                k = _veriKlasoru == null ? null : KayitOku(_veriKlasoru);
                if (k == null || !SozSureciMi(k.Pid))
                {
                    Bagli = false; kopuk0 = null; deneme = 0; _eskiMotor = false;
                    // Kayıt hiç yoksa Söz kapalıdır (nötr). Kayıt var ama süreç yoksa Söz çökmüştür: "hiç iş yok" gibi görünmesin, "bilgiye ulaşamıyorum" gösterilir.
                    Gorunum(k == null ? BaglantiGorunumu.Notr : BaglantiGorunumu.Belirsiz, k == null ? "sunucu kaydı yok" : $"sunucu süreci yok (pid {k.Pid})");
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    continue;
                }
                if (sonKayit != k) { Gunluk.Yaz($"motor: kayıt {k.Host}:{k.Port} pid {k.Pid}"); sonKayit = k; SonRev = 0; _eskiMotor = false; }

                // önce akışa abone ol, sonra listeyi al: araya düşen olay kaybolmaz
                var olay = System.Threading.Channels.Channel.CreateBounded<byte>(new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });
                var baglandi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var akisCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // yeni motorda yalnız "attention" olayı listeyi tazeler (ajanın her çıktı satırı değil); eski motorda her olay
                var akis = MiniHttp.AkisAsync(k.Host, k.Port, "/api/events/stream", ad => { if (_eskiMotor || ad == "attention") olay.Writer.TryWrite(0); }, () => baglandi.TrySetResult(), akisCts.Token);
                if (await Task.WhenAny(baglandi.Task, akis).ConfigureAwait(false) != baglandi.Task) { await akis.ConfigureAwait(false); throw new IOException("olay akışı açılamadı"); }

                async Task Yenile(string neden)
                {
                    var liste = await ListeAl(k, ct).ConfigureAwait(false);
                    Bagli = true; kopuk0 = null; deneme = 0;
                    sonG = BaglantiGorunumu.Liste;
                    Gunluk.Yaz($"motor: liste alındı ({neden}): {SonOzet}{(ilk ? " [ilk]" : "")}");
                    _teslim(new MotorTeslimi(liste, ilk, true));
                    ilk = false;
                }
                await Yenile("bağlandı").ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    var oku = olay.Reader.WaitToReadAsync(ct).AsTask();
                    if (await Task.WhenAny(akis, oku).ConfigureAwait(false) == akis) break;
                    olay.Reader.TryRead(out _);
                    await Task.Delay(60, ct).ConfigureAwait(false);   // art arda gelen olayları tek istekte topla
                    olay.Reader.TryRead(out _);
                    await Yenile("olay").ConfigureAwait(false);
                }
                akisCts.Cancel();
                try { await akis.ConfigureAwait(false); } catch (Exception) { }
                throw new IOException("olay akışı kapandı");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                SonHata = e.GetType().Name + ": " + e.Message;
                if (Bagli || kopuk0 == null) Gunluk.Yaz("motor bağlantısı koptu, yeniden denenecek: " + SonHata);
                Bagli = false;
                kopuk0 ??= sw.ElapsedMilliseconds;
            }
            // kopukluk: ekrana ne yansıyacağı kopukluk süresine bağlı
            var kopukMs = kopuk0 is { } t0 ? sw.ElapsedMilliseconds - t0 : 0;
            Gorunum(BaglantiKarar.Karar(true, false, kopukMs), $"kopuk {kopukMs / 1000.0:0.0} sn");
            try { await Task.Delay(BaglantiKarar.YenidenDenemeMs(deneme++), ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() { _cts.Cancel(); try { _dongu?.Wait(500); } catch { } _cts.Dispose(); }
}
