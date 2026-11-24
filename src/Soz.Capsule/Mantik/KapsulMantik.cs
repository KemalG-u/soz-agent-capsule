namespace Soz.Capsule;

internal enum Seviye { Uyku, Soluk, Belirgin }
internal enum PanelKipi { Yok, OnBakis, Tam }
internal enum Gizlilik { Yok, Kullanici, TamEkran, Cikis }
internal enum Durak { Yok, Orb, Durumlu }
internal enum DikteFazi { Dinliyor, Ceviriyor, Yazdi }

/// <summary>Kapsülün dışarıya verdiği komutlar. Ana iş parçacığı bunları okuyup yapar (panel aç, giriş aç, günlüğe yaz, çık).</summary>
internal enum EylemTuru { PanelAc, PanelKapat, GirisAc, GirisKapat, MenuAc, MenuKapat, MetinGonder, KonumSifirla, AyarAc, Cikis, Uyut, HedefSec, TemaDegistir, Ses }

internal readonly record struct Eylem(EylemTuru Tur, PanelKipi Kip = PanelKipi.Yok, string? Metin = null, string? IsId = null);

internal sealed record Icerik(string Yazi, string Glif, DurumSinifi Sinif, bool Calisiyor, bool Hazir);

/// <summary>Bir andaki hedef görünüm. W, H dp; Op 0-1.</summary>
internal sealed record Gorunum(double W, double H, double Op, double Taban, Seviye Sev, bool Hap, Icerik? Icerik, bool Rozet, bool Giris, bool Gizli, string Ad);

/// <summary>
/// P1 durum makinesi + P2 girdi kuralları. Saf mantık: pencere, saat ve çizim yok. Saat dışarıdan (ms) verilir; son tarihler
/// <see cref="SonrakiMs"/> ile sorulur, böylece boşta zamanlayıcı bile gerekmez. Deneme sayfasındaki computeVis, idleReset, rightAction,
/// content, navJobs işlevlerinin birebir karşılığıdır.
/// </summary>
internal sealed class KapsulMantik
{
    public Ayar Ayar;
    private readonly Func<string, double> _yaziGenislik;

    public Seviye Sev = Seviye.Belirgin;
    public PanelKipi Panel = PanelKipi.Yok;
    public bool Giris, Surukle, Uzatma, Menu, HedefAcik, Hover, SleepHold, TamEkranUygulama, UstundePanel;
    public double UzatmaGenislik;
    public Durak Durak = Durak.Yok;
    public Gizlilik Gizli = Gizlilik.Yok;
    public (DikteFazi Faz, long T0)? Dikte;
    public bool DikteHedefVar = true;
    public string? DikteOzelMetin;
    public string ProjeAdi = "";
    public string HedefEtiketi => string.IsNullOrWhiteSpace(ProjeAdi) ? "Proje seç" : ProjeAdi.Trim();
    public double ImlecUzaklik = double.PositiveInfinity;   // imlecin kapsül merkezine uzaklığı (dp)
    public double ZeminParlaklik = 0;                    // 0 koyu zemin, 1 açık zemin
    public double EkranGenislikDp = 1920;                // giriş genişliği ekrandan taşmasın
    public IReadOnlyList<Is> Isler = Array.Empty<Is>();
    public string? SeciliId;
    public bool NoDwell;
    public long WokeAt;
    public bool WokeFromSleep;
    public (double X, double Y)? DurmaNoktasi;

    public readonly Queue<Eylem> Eylemler = new();

    // son tarihler (ms, saat dışarıdan)
    private long? _bostaAt; private Seviye _bostaHedef;
    private long? _tekerlekAt, _dwellAt, _onBakisKapanAt, _gizliBitisAt, _dikteAt, _geciciAt;
    private long _tekerlekBitis;

    /// <summary>Kısa bildirim (ör. "Paket panoda"): hapta bu kadar ms görünür, sonra kendiliğinden kalkar.</summary>
    public const long BildirimMs = 2600;
    /// <summary>Tam panel açma isteğinden sonra kapsüle gelen ikinci tık (çift tık sayılsa bile) bu süre boyunca yutulur: panel soğuk açılırken sabırsız tık onu geri kapatmasın.</summary>
    public const long PanelAcilisPayiMs = 1500;
    /// <summary>Görünen kısa bildirim metni; yoksa null. Süresi dolunca <see cref="Tik"/> kaldırır.</summary>
    public string? Gecici { get; private set; }

    public KapsulMantik(Ayar ayar, Func<string, double> yaziGenislikDp)
    {
        Ayar = ayar; _yaziGenislik = yaziGenislikDp;
    }

    // ------------------------------------------------------------ iş listesi ve içerik
    public IEnumerable<Is> Bekleyenler => Isler.Where(j => j.Bekliyor).OrderBy(j => j.Oncelik).ThenBy(j => j.Id, StringComparer.Ordinal);
    public IEnumerable<Is> Calisanlar => Isler.Where(j => j.Tur == IsTuru.Calisiyor);
    /// <summary>Söz'ün emin olmadığı işler: seni bekleyen sayılmazlar, kapsülü uyandırmazlar, ayrı gösterilirler.</summary>
    public IEnumerable<Is> Belirsizler => Isler.Where(j => j.Tur == IsTuru.Belirsiz);

    public Is? SeciliIs
    {
        get
        {
            var j = SeciliId == null ? null : Isler.FirstOrDefault(x => x.Id == SeciliId);
            return j ?? Bekleyenler.FirstOrDefault();
        }
    }

    public Seviye DinlenmeTabani => Ayar.Rest == "nokta" ? Seviye.Uyku : Seviye.Soluk;

    public string DikteMetni(long now)
    {
        if (Dikte is not { } d) return "";
        if (!string.IsNullOrWhiteSpace(DikteOzelMetin)) return DikteOzelMetin;
        if (d.Faz == DikteFazi.Dinliyor) { var s = (int)Math.Max(0, (now - d.T0) / 1000); return $"dinliyor {s / 60}:{s % 60:00}"; }
        if (d.Faz == DikteFazi.Ceviriyor) return "çeviriyor";
        return DikteHedefVar ? "yazdı" : "panoda";
    }

    public Icerik? IcerikHesapla(long now)
    {
        if (Dikte != null) return new Icerik(DikteMetni(now), "mic", DurumSinifi.Dikte, false, false);
        if (GeciciAktif(now)) return new Icerik(Gecici!, "brand", DurumSinifi.Yok, false, false);
        var w = Bekleyenler.ToList();
        if (_tekerlekBitis > now && w.Count > 0)
        {
            var j = SeciliIs ?? w[0];
            return new Icerik(j.Kisa, j.Glif, j.Sinif, false, false);
        }
        if (w.Count > 0)
        {
            var t = w[0];
            return new Icerik(BekleyenYazisi(w), t.Glif, t.Sinif, false, false);
        }
        var bel = Belirsizler.Count();
        // Motora ulaşılamıyorsa hap bunu açıkça söyler: eski sayılar canlıymış gibi durmaz, boş durum gibi de görünmez.
        if (Belirsizler.Any(j => j.Id == BaglantiKarar.BelirsizId)) return new Icerik(BaglantiKarar.HapYazisi, "unk", DurumSinifi.Belirsiz, false, false);
        if (bel > 0)
        {
            // Hepsi denenmeden bitmiş işse "denenmedi", hepsi soru sormadan durmuş oturumsa "durdu" ("belirsiz" ve "bitti" değil)
            var sozcuk = Belirsizler.All(j => j.Kanitsiz) ? "denenmedi" : Belirsizler.All(j => j.Durdu) ? "durdu" : "belirsiz";
            return new Icerik(bel > 1 ? $"{bel} {sozcuk}" : sozcuk, "unk", DurumSinifi.Belirsiz, false, false);
        }
        var r = Calisanlar.Count();
        if (r > 0) return new Icerik(r > 1 ? $"{r} çalışıyor" : "çalışıyor", "run", DurumSinifi.Calisiyor, true, false);
        if (Ayar.Rest == "durumlu" || Durak == Durak.Durumlu) return new Icerik("hazır", "brand", DurumSinifi.Yok, false, true);
        return null;
    }

    /// <summary>
    /// Hapta bekleyen işlerin yazısı: tek iş "karar"; tek tür çoksa o tür ("5 karar bekliyor"); karışıksa öncelik sırasıyla her tür ve sayısı ("2 soru, 3 bitti").
    /// Hap genişliğine (<see cref="Ayar.PillMax"/>) sığan ilk uzun biçim seçilir; sığmazsa en önemli tür ve toplam ("12 soru, toplam 43"), en sonda yalnız sayı ("43 iş").
    /// </summary>
    private string BekleyenYazisi(List<Is> w)
    {
        if (w.Count == 1) return w[0].Soz;
        var turler = w.GroupBy(j => j.Soz).Select(g => (Soz: g.Key, N: g.Count())).ToList();   // w öncelik sırasında, gruplar ilk görülme sırasında
        var toplam = w.Count;
        string[] adaylar;
        if (turler.Count == 1)
        {
            var (soz, n) = turler[0];
            var uzun = soz switch { "soru" => "soru bekliyor", "karar" => "karar bekliyor", "bitti" => "iş bitti", "olmadı" => "iş olmadı", _ => soz };
            adaylar = new[] { $"{n} {uzun}", $"{n} {soz}", $"{toplam} iş" };
        }
        else
        {
            var ilk = turler[0];
            adaylar = new[] { string.Join(", ", turler.Select(t => $"{t.N} {t.Soz}")), $"{ilk.N} {ilk.Soz}, toplam {toplam}", $"{toplam} iş" };
        }
        var bos = Math.Max(Ayar.Orb, Ayar.Big) + Ayar.PillPad;
        foreach (var a in adaylar) if (bos + _yaziGenislik(a) <= Ayar.PillMax) return a;
        return adaylar[^1];
    }

    /// <summary>Panel, giriş, sürükleme, üstünde durma gibi etkileşim varken kapsül belirgin kalır.</summary>
    public bool Mesgul => Panel != PanelKipi.Yok || Giris || Surukle || Uzatma || (Hover && !SleepHold) || Menu || Dikte != null || HedefAcik;

    public Gorunum GorunumHesapla(long now)
    {
        var a = Ayar;
        var c = IcerikHesapla(now);
        var lvl = Mesgul ? Seviye.Belirgin : Sev;
        var h = lvl switch { Seviye.Uyku => a.Dot, Seviye.Soluk => a.Orb, _ => a.Big };
        var w = h; var hap = false; double taban;
        if (Giris) { w = Math.Min(a.GirisW, EkranGenislikDp - 12); h = a.Big; taban = 1; }
        else
        {
            hap = c != null && lvl != Seviye.Uyku && (Durak != Durak.Orb || Dikte != null || GeciciAktif(now));
            if (hap) w = Math.Min(a.PillMax, h + a.PillPad + _yaziGenislik(c!.Yazi));
            taban = lvl == Seviye.Uyku ? Yaklasma.UykuOpaklik(a, ZeminParlaklik) : lvl == Seviye.Soluk ? a.Soft : 1;
        }
        if (Uzatma && UzatmaGenislik > 0) w = UzatmaGenislik;
        var op = taban;
        if (lvl != Seviye.Belirgin && a.R > 0 && ImlecUzaklik < a.R) op = Yaklasma.Opaklik(taban, ImlecUzaklik, a.R);
        var gizli = Gizli != Gizlilik.Yok;
        op = gizli ? 0 : Math.Max(a.SleepFloor, op);
        var rozet = c != null && !hap && !Giris && (c.Sinif is DurumSinifi.Bitti or DurumSinifi.Karar or DurumSinifi.Kaldi or DurumSinifi.Soru or DurumSinifi.Dikte or DurumSinifi.Belirsiz);
        return new Gorunum(w, h, op, taban, lvl, hap, c, rozet, Giris, gizli, DurumAdi(lvl, hap));
    }

    /// <summary>
    /// Ekran okuyucunun okuyacağı ad: iç durum adları (UYKU, BELİRGİN, ÖN BAKIŞ...) değil, kişinin anlayacağı sade cümleler.
    /// Ör. "Söz küçük pencere. 2 iş seni bekliyor. Etkin proje: Söz."
    /// </summary>
    public string EkranOkuyucuAdi(Gorunum g, string? proje) => Erisim.Ad(Panel, Giris, g.Icerik, Bekleyenler.Count(), proje);

    private string DurumAdi(Seviye lvl, bool hap)
    {
        if (Giris) return "GİRİŞ";
        if (Panel == PanelKipi.Tam) return "PANEL";
        if (Panel == PanelKipi.OnBakis) return "ÖN BAKIŞ";
        if (lvl == Seviye.Uyku) return "UYKU";
        if (hap) return lvl == Seviye.Soluk ? "SOLUK-DURUMLU" : "DURUMLU";
        return lvl == Seviye.Soluk ? "SOLUK" : "BELİRGİN";
    }

    // ------------------------------------------------------------ zaman
    /// <summary>Bir sonraki tarih (ms, saat dışarıdan) ya da null: hiçbir şey beklemiyor, zamanlayıcı kapatılabilir.</summary>
    public long? SonrakiMs()
    {
        long? en = null;
        void Min(long? v) { if (v != null && (en == null || v < en)) en = v; }
        Min(_bostaAt); Min(_tekerlekAt); Min(_dwellAt); Min(_onBakisKapanAt); Min(_gizliBitisAt); Min(_dikteAt); Min(_geciciAt);
        return en;
    }

    /// <summary>Gelen son tarihleri işler. Dönüş: görünüm değişmiş olabilir.</summary>
    public bool Tik(long now)
    {
        var degisti = false;
        if (_bostaAt is { } b && b <= now) { _bostaAt = null; Sev = _bostaHedef; BostaSifirla(now); degisti = true; }
        if (_tekerlekAt is { } t && t <= now) { _tekerlekAt = null; _tekerlekBitis = 0; degisti = true; }
        if (_geciciAt is { } gb && gb <= now) { _geciciAt = null; Gecici = null; degisti = true; }
        if (_dwellAt is { } d && d <= now)
        {
            _dwellAt = null;
            if (Hover && Panel == PanelKipi.Yok && !Surukle && !Giris && !Menu && Gizli == Gizlilik.Yok && !Uzatma)
            { Panel = PanelKipi.OnBakis; Eylemler.Enqueue(new Eylem(EylemTuru.PanelAc, PanelKipi.OnBakis)); degisti = true; }
        }
        if (_onBakisKapanAt is { } k && k <= now)
        {
            _onBakisKapanAt = null;
            if (Panel == PanelKipi.OnBakis && !Hover && !UstundePanel) { Panel = PanelKipi.Yok; Eylemler.Enqueue(new Eylem(EylemTuru.PanelKapat, PanelKipi.OnBakis)); BostaSifirla(now); degisti = true; }
        }
        if (_gizliBitisAt is { } g && g <= now) { _gizliBitisAt = null; if (Gizli == Gizlilik.Kullanici) { Geri(now); degisti = true; } }
        if (_dikteAt is { } dk && dk <= now) { _dikteAt = Dikte is { Faz: DikteFazi.Dinliyor } ? dk + 1000 : null; degisti = true; }
        return degisti;
    }

    /// <summary>Boşta zamanlayıcıları: belirgin → soluk → uyku (deneme sayfasındaki idleReset).</summary>
    public void BostaSifirla(long now, double? ilkMs = null)
    {
        _bostaAt = null;
        var calisir = (!Mesgul && Gizli == Gizlilik.Yok && !TamEkranUygulama) || (!Mesgul && Gizli == Gizlilik.TamEkran);
        if (!calisir) return;
        if (Sev == Seviye.Belirgin) { _bostaAt = now + (long)(ilkMs ?? Ayar.Idle1 * 1000); _bostaHedef = Seviye.Soluk; }
        else if (Sev == Seviye.Soluk)
        {
            if (DinlenmeTabani == Seviye.Uyku) { _bostaAt = now + (long)(Ayar.Idle2 * 1000); _bostaHedef = Seviye.Uyku; }
        }
        else if (Sev == Seviye.Uyku && DinlenmeTabani == Seviye.Soluk) Sev = Seviye.Soluk;
    }

    public void Uyandir(long now)
    {
        if (Sev != Seviye.Belirgin) { WokeFromSleep = Sev == Seviye.Uyku; WokeAt = now; Sev = Seviye.Belirgin; }
    }

    // ------------------------------------------------------------ iş listesi güncellemesi
    /// <summary>Kapsülün bu süreçte daha önce değerlendirdiği bekleyen öğe kimlikleri (aynı öğe iki kez uyandırmaz: motor yeniden başlayınca liste geri gelir, kapsül yine susar).</summary>
    private readonly HashSet<string> _degerlendirilen = new(StringComparer.Ordinal);
    /// <summary>Günlük için: kaç kez dikkat verildi, kaç yeni öğe sunucunun işaret hakkı olmadığı ya da sessiz başlangıç yüzünden uyandırmadı.</summary>
    public int DikkatSayaci, SessizSayaci;

    /// <summary>
    /// Motordan ya da deneyden gelen yeni liste. Yeni bekleyen iş varsa dikkat olayı: uykudan durumluya, 6 sn sonra soluğa, rozet kalır.
    /// Sunucu dikkat bütçesini sayar: öğenin <see cref="Is.Sinyal"/> alanı false ise (günlük hak bitti) öğe listeye ve sayıya girer ama kapsül uyanmaz.
    /// <paramref name="sessiz"/>: kapsül yeni açıldı, ilk liste yalnız durumu yükler (zaten var olan işler için uyandırma). <paramref name="kesin"/>: liste canlı motordan geldi (kopukluk ya da motor yokluğu değil).
    /// </summary>
    public bool IsleriGuncelle(IReadOnlyList<Is> yeni, long now, bool sessiz = false, bool kesin = false)
    {
        Isler = yeni;
        if (SeciliId != null && yeni.All(j => j.Id != SeciliId)) SeciliId = null;
        var uyandir = false;
        foreach (var j in yeni.Where(x => x.Bekliyor))
        {
            if (!_degerlendirilen.Add(j.Id)) continue;
            if (j.Sinyal && !sessiz) uyandir = true; else SessizSayaci++;
        }
        // canlı motordan gelen kesin listede olmayan öğe artık bekleyen değil: aynı kimlikle (eski motorun "j:7" gibi kimlikleri) tekrar bekleyince yeniden uyandırır. Kopukken gelen boş/belirsiz liste unutturmaz.
        if (kesin) _degerlendirilen.RemoveWhere(id => yeni.All(j => j.Id != id || !j.Bekliyor));
        if (uyandir) { DikkatSayaci++; Dikkat(now); return true; }
        // bekleyen kalmadıysa ve soluktaysa içerik değişir; zamanlayıcıyı yenile
        BostaSifirla(now);
        return true;
    }

    /// <summary>Dikkat isteyen olay: uykudan çıkar, orb kısaltması kalkar, 6 sn belirgin, sonra soluk-durumlu. Odak çalmaz, ses ayarla.</summary>
    public void Dikkat(long now)
    {
        if (Durak == Durak.Orb) Durak = Durak.Yok;
        if (Gizli == Gizlilik.Yok || Gizli == Gizlilik.TamEkran) Uyandir(now);
        BostaSifirla(now, Ayar.AttentionMs);
        if (Ayar.Sound >= 0.5) Eylemler.Enqueue(new Eylem(EylemTuru.Ses));
    }

    // ------------------------------------------------------------ imleç
    /// <summary>İmleç kapsülün üstüne girdi (hit-test alanı).</summary>
    public void HoverGirdi(long now, double xDp, double yDp)
    {
        Hover = true;
        if (Sev != Seviye.Belirgin && !SleepHold) Uyandir(now);
        _bostaAt = null; _onBakisKapanAt = null;
        DurmaNoktasi = (xDp, yDp);
        DwellBaslat(now);
        BostaSifirla(now);
    }

    public void HoverCikti(long now)
    {
        Hover = false; SleepHold = false; _dwellAt = null;
        if (Panel == PanelKipi.OnBakis) _onBakisKapanAt = now + (long)Ayar.HoverCloseMs;
        BostaSifirla(now);
    }

    /// <summary>İmleç hapın üstünde hareket etti: 4 dp'den çok oynadıysa durma sayacı yeniden başlar.</summary>
    public void HoverHareket(long now, double xDp, double yDp)
    {
        if (!Hover || Panel != PanelKipi.Yok || Surukle) return;
        if (DurmaNoktasi is { } p && Math.Sqrt((xDp - p.X) * (xDp - p.X) + (yDp - p.Y) * (yDp - p.Y)) > Ayar.DwellTol)
        {
            DurmaNoktasi = (xDp, yDp); NoDwell = false; DwellBaslat(now);
        }
    }

    private void DwellBaslat(long now)
    {
        _dwellAt = null;
        if (NoDwell) return;
        _dwellAt = now + (long)Ayar.HoverMs;
    }

    public void OnBakisUstunde(bool var_, long now)
    {
        UstundePanel = var_;
        if (var_) _onBakisKapanAt = null;
        else if (Panel == PanelKipi.OnBakis && !Hover) _onBakisKapanAt = now + (long)Ayar.HoverCloseMs;
    }

    // ------------------------------------------------------------ tıklamalar
    /// <summary>Sol tık, sürükleme olmadan bırakıldı. sleepy: tık sırasında kapsül uykudaydı (yalnız uyandırır).</summary>
    public void SolTik(long now, bool uykulu)
    {
        if (Menu) { MenuKapat(now); return; }
        if (Giris) return;
        if (Panel == PanelKipi.Tam) return;
        // k3: uykulu tık da asıl işi yapar (eskiden yalnız uyandırıyordu, panel için ikinci tık gerekiyordu)
        if (uykulu) Uyandir(now);
        TamPanelAc(now, null);
    }

    /// <summary>Bir tıkın "uykulu" sayılması: seviye belirgin değilse ya da uykudan yeni uyandıysa (250 ms).</summary>
    public bool TikUykulu(long now) => Sev != Seviye.Belirgin || (WokeFromSleep && now - WokeAt < 250);

    public void TamPanelAc(long now, string? isId)
    {
        if (isId != null) SeciliId = isId;
        TamPanelAcEylemi(now, SeciliIs?.PanelKimligi);
    }

    /// <summary>Tam paneli iş seçmeden belli bir sayfada açar ("+N daha", "Proje", "Söz'ü aç" satırları). sayfa: "suan", "is" ya da "ac" (olduğu yerde).</summary>
    public void TamPanelSayfaAc(long now, string sayfa) => TamPanelAcEylemi(now, OnBakisIcerigi.SayfaOn + sayfa);

    private void TamPanelAcEylemi(long now, string? secili)
    {
        Uyandir(now);
        // ön bakış açıksa tam panelle üst üste kalmasın: kapatma eylemi tam panel açmadan önce gelir
        if (Panel == PanelKipi.OnBakis) { _onBakisKapanAt = null; Eylemler.Enqueue(new Eylem(EylemTuru.PanelKapat, PanelKipi.OnBakis)); }
        _panelAcMs = now;
        Panel = PanelKipi.Tam;
        Eylemler.Enqueue(new Eylem(EylemTuru.PanelAc, PanelKipi.Tam, null, secili));
        BostaSifirla(now);
    }

    private long _panelAcMs = long.MinValue / 2;

    public void CiftTik(long now)
    {
        if (Giris) return;
        // k3: panel az önce açıldıysa (soğuk açılışta sabırsız ikinci tık) onu geri kapatma ve yazı satırı açma
        if (Panel == PanelKipi.Tam && now - _panelAcMs <= PanelAcilisPayiMs) return;
        GirisAc(now);
    }

    /// <summary>Sağ tık: bir geri. Menü, panel, giriş sırayla kapanır; kökte soluk uyutur, belirgin pano açar. Uykuda alttaki programa geçer (pencere katmanı yutmaz).</summary>
    public void SagTik(long now, bool kapsuldaMi)
    {
        if (Menu) { MenuKapat(now); return; }
        if (HedefAcik) { HedefAcik = false; Eylemler.Enqueue(new Eylem(EylemTuru.HedefSec, PanelKipi.Yok, "kapat")); BostaSifirla(now); return; }
        if (Panel != PanelKipi.Yok) { PaneliKapat(now); return; }
        if (Giris) { GirisKapat(now); return; }
        if (!kapsuldaMi) return;
        if (Sev == Seviye.Uyku) return;
        if (Sev == Seviye.Soluk) { Uyut(now); return; }
        MenuAc(now);
    }

    /// <summary>Sağ tık bu durumda alttaki programa geçmeli mi (P2: uykuda kapsül sağ tıkı yutmaz).</summary>
    public bool SagTikGecirgen => Sev == Seviye.Uyku && !Menu && Panel == PanelKipi.Yok && !Giris && !Mesgul;

    public void MenuAc(long now)
    {
        HedefAcik = false; Menu = true;
        Eylemler.Enqueue(new Eylem(EylemTuru.MenuAc));
        _bostaAt = null;
    }

    public void MenuKapat(long now)
    {
        if (!Menu) return;
        Menu = false;
        Eylemler.Enqueue(new Eylem(EylemTuru.MenuKapat));
        BostaSifirla(now);
    }

    public void PaneliKapat(long now)
    {
        if (Panel == PanelKipi.Yok) return;
        var k = Panel; Panel = PanelKipi.Yok; _onBakisKapanAt = null;
        Eylemler.Enqueue(new Eylem(EylemTuru.PanelKapat, k));
        BostaSifirla(now);
    }

    /// <summary>Panel kapsül dışından kapandı (süreç bitti, kullanıcı kapattı).</summary>
    public void PanelDisaridanKapandi(long now)
    {
        if (Panel == PanelKipi.Yok) return;
        Panel = PanelKipi.Yok; BostaSifirla(now);
    }

    public void Uyut(long now)
    {
        KapatHepsi();
        Sev = Seviye.Uyku; SleepHold = Hover; Hover = false;
        Eylemler.Enqueue(new Eylem(EylemTuru.Uyut));
        BostaSifirla(now);
    }

    private void KapatHepsi()
    {
        if (Menu) { Menu = false; Eylemler.Enqueue(new Eylem(EylemTuru.MenuKapat)); }
        HedefAcik = false;
        if (Giris) { Giris = false; Eylemler.Enqueue(new Eylem(EylemTuru.GirisKapat)); }
        if (Panel != PanelKipi.Yok) { var k = Panel; Panel = PanelKipi.Yok; Eylemler.Enqueue(new Eylem(EylemTuru.PanelKapat, k)); }
    }

    // ------------------------------------------------------------ giriş
    public void GirisAc(long now)
    {
        if (Gizli != Gizlilik.Yok) return;
        if (Panel != PanelKipi.Yok) PaneliKapat(now);
        if (Menu) MenuKapat(now);
        Giris = true; Durak = Durak.Yok; Uyandir(now);
        Eylemler.Enqueue(new Eylem(EylemTuru.GirisAc));
        _bostaAt = null;
    }

    public void GirisKapat(long now)
    {
        if (!Giris) return;
        HedefAcik = false;
        Giris = false;
        Eylemler.Enqueue(new Eylem(EylemTuru.GirisKapat));
        BostaSifirla(now);
    }

    public void MetinGonder(string metin, long now)
    {
        if (string.IsNullOrWhiteSpace(metin)) return;
        Eylemler.Enqueue(new Eylem(EylemTuru.MetinGonder, PanelKipi.Yok, metin.Trim()));
        GirisKapat(now);
    }

    public void HedefCipiTikla(long now)
    {
        HedefAcik = false;
        if (Giris) GirisKapat(now);
        TamPanelAc(now, null);
    }

    /// <summary>Ctrl+Alt+J: gizliyse geri getirir, girişi açar.</summary>
    public void Kisayol(long now)
    {
        if (Gizli == Gizlilik.Kullanici || Gizli == Gizlilik.TamEkran && !TamEkranUygulama) Geri(now);
        if (Giris) return;
        GirisAc(now);
    }

    /// <summary>Tepsiden "Küçük pencereyi göster": kişi gizlediyse geri getirir, yazı satırını açmaz. Gizli değilse hiçbir şey yapmaz (false).</summary>
    public bool Goster(long now)
    {
        if (Gizli != Gizlilik.Kullanici) return false;
        Geri(now);
        return true;
    }

    /// <summary>Esc: bir geri (menü, giriş, panel).</summary>
    public bool Esc(long now)
    {
        if (Menu) { MenuKapat(now); return true; }
        if (HedefAcik) { HedefAcik = false; BostaSifirla(now); return true; }
        if (Giris) { GirisKapat(now); return true; }
        if (Panel != PanelKipi.Yok) { PaneliKapat(now); return true; }
        return false;
    }

    // ------------------------------------------------------------ tekerlek
    public void Tekerlek(int yon, long now)
    {
        if (Gizli != Gizlilik.Yok) return;
        var w = Bekleyenler.ToList();
        if (w.Count == 0) return;
        var cur = SeciliIs; var idx = Math.Max(0, cur == null ? 0 : w.FindIndex(x => x.Id == cur.Id));
        idx = ((idx + yon) % w.Count + w.Count) % w.Count;
        SeciliId = w[idx].Id;
        _tekerlekBitis = now + (long)Ayar.WheelShowMs; _tekerlekAt = _tekerlekBitis;
        Uyandir(now); BostaSifirla(now);
    }

    // ------------------------------------------------------------ sürükleme ve uzatma
    public void SurukleBasla(long now)
    {
        Surukle = true; _dwellAt = null; SleepHold = false;
        if (Menu) MenuKapat(now);
        if (Panel == PanelKipi.OnBakis) PaneliKapat(now);
        Uyandir(now);
    }

    public void SurukleBitir(long now, double xDp, double yDp)
    {
        Surukle = false; NoDwell = true; DurmaNoktasi = (xDp, yDp); _dwellAt = null;
        BostaSifirla(now);
    }

    public void UzatmaBasla() { Uzatma = true; }
    public void UzatmaGuncelle(double genislikDp) { UzatmaGenislik = genislikDp; }

    /// <summary>Üç durak: orb, durumlu, giriş. Bırakılan genişliğe en yakını seçilir.</summary>
    public Durak UzatmaBitir(long now, double yukseklikDp)
    {
        var c = IcerikHesapla(now);
        var pillW = Math.Min(Ayar.PillMax, yukseklikDp + Ayar.PillPad + _yaziGenislik((c ?? new Icerik("hazır", "brand", DurumSinifi.Yok, false, true)).Yazi));
        var giW = Math.Min(Ayar.GirisW, EkranGenislikDp - 12);
        var w = UzatmaGenislik;
        Uzatma = false; UzatmaGenislik = 0;
        var duraklar = new (Durak D, double W, bool Giris)[] { (Durak.Orb, yukseklikDp, false), (Durak.Durumlu, pillW, false), (Durak.Yok, giW, true) };
        var en = duraklar[0];
        foreach (var d in duraklar) if (Math.Abs(d.W - w) < Math.Abs(en.W - w)) en = d;
        if (en.Giris) { GirisAc(now); return Durak.Yok; }
        Durak = en.D; BostaSifirla(now);
        return en.D;
    }

    // ------------------------------------------------------------ yaşam döngüsü
    public void Gizle(Gizlilik tur, long now)
    {
        KapatHepsi();
        Gizli = tur; _bostaAt = null;
        if (tur == Gizlilik.Kullanici) _gizliBitisAt = now + (long)Ayar.HideMs;
        if (tur == Gizlilik.Cikis) Eylemler.Enqueue(new Eylem(EylemTuru.Cikis));
    }

    public void Geri(long now)
    {
        Gizli = Gizlilik.Yok; _gizliBitisAt = null;
        Uyandir(now); BostaSifirla(now);
    }

    /// <summary>Tam ekran uygulama başladı ya da bitti (bekçi). Ayar kapalıysa kapsül görünür kalır.</summary>
    public void TamEkran(bool var_, long now)
    {
        if (var_ == TamEkranUygulama) return;
        TamEkranUygulama = var_;
        if (var_)
        {
            if (Ayar.FsHide >= 0.5 && Gizli == Gizlilik.Yok) { KapatHepsi(); Gizli = Gizlilik.TamEkran; _bostaAt = null; }
        }
        else if (Gizli == Gizlilik.TamEkran) { Gizli = Gizlilik.Yok; Uyandir(now); BostaSifirla(now, 4000); }
    }

    public void KonumSifirla() => Eylemler.Enqueue(new Eylem(EylemTuru.KonumSifirla));

    // ------------------------------------------------------------ dikte (P8 görünüm tarafı)
    public void DikteBaslat(long now)
    {
        if (Gizli != Gizlilik.Yok) Geri(now);
        KapatHepsi();
        DikteOzelMetin = null;
        Dikte = (DikteFazi.Dinliyor, now); _dikteAt = now + 1000; Uyandir(now);
    }

    public void DikteFaz(DikteFazi f, long now) { DikteOzelMetin = null; Dikte = (f, now); }

    /// <summary>Kabuktan gelen dikte durumu olayını (dinliyor | ceviriyor | yazdi | panoda | kapali) uygular.</summary>
    public bool DikteDurumUygula(string? faz, string? metin, long now)
    {
        var (f, m) = metin == null ? KabukProtokol.DikteCoz(faz) : ((faz ?? "").Trim().ToLowerInvariant(), metin.Trim());
        switch (f)
        {
            case "dinliyor":
                DikteBaslat(now);
                DikteOzelMetin = string.IsNullOrWhiteSpace(m) ? null : m;
                return true;
            case "ceviriyor":
                if (Gizli != Gizlilik.Yok) Geri(now);
                KapatHepsi();
                DikteFaz(DikteFazi.Ceviriyor, now);
                DikteOzelMetin = string.IsNullOrWhiteSpace(m) ? null : m;
                _dikteAt = null;
                Uyandir(now);
                return true;
            case "yazdi":
                if (Gizli != Gizlilik.Yok) Geri(now);
                KapatHepsi();
                DikteHedefVar = true;
                DikteFaz(DikteFazi.Yazdi, now);
                DikteOzelMetin = string.IsNullOrWhiteSpace(m) ? null : m;
                _dikteAt = null;
                Uyandir(now);
                return true;
            case "panoda":
                if (Gizli != Gizlilik.Yok) Geri(now);
                KapatHepsi();
                DikteHedefVar = false;
                DikteFaz(DikteFazi.Yazdi, now);
                DikteOzelMetin = string.IsNullOrWhiteSpace(m) ? null : m;
                _dikteAt = null;
                Uyandir(now);
                return true;
            case "kapali":
                DikteBitir(now);
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------ kısa bildirim (masaüstü programının işlerine verdiği cevap)
    public bool GeciciAktif(long now) => Gecici != null && _geciciAt is { } b && now < b;

    /// <summary>
    /// Masaüstü programından gelen kısa cevap ("Paket panoda", "Dikte komutu gönderildi"): kapsül uyanır, hapta kısa süre görünür, sonra kalkar.
    /// Kullanıcı kapsülü gizlemişse (1 saat gizle, tam ekran) görünmez, kapsül onu uyandırmaz. Boş metin yok sayılır.
    /// </summary>
    public void Bildir(string? metin, long now)
    {
        if (string.IsNullOrWhiteSpace(metin) || Gizli != Gizlilik.Yok) return;
        Gecici = metin.Trim(); _geciciAt = now + BildirimMs;
        if (Durak == Durak.Orb) Durak = Durak.Yok;
        Uyandir(now);
        BostaSifirla(now, BildirimMs + 400);
    }
    public void DikteBitir(long now) { Dikte = null; DikteOzelMetin = null; _dikteAt = null; BostaSifirla(now, 2500); }

    // ------------------------------------------------------------ uzak tıklama
    /// <summary>Kapsül dışına sol tık: menü, ön bakış, iğnesiz panel kapanır; hiçbiri değilse ve belirginse soluklaşır (uzağa tıklayınca soluklaş).</summary>
    public void DisariTik(long now, double kapsulaUzaklikDp)
    {
        var kullanildi = false;
        if (Menu) { MenuKapat(now); kullanildi = true; }
        if (HedefAcik) { HedefAcik = false; kullanildi = true; }
        if (Panel == PanelKipi.OnBakis) { PaneliKapat(now); kullanildi = true; }
        if (Giris && !kullanildi) { /* giriş yazılı metni taşır; boşsa dışarı tık kapatır, çağıran karar verir */ }
        if (!kullanildi && Ayar.FarFade >= 0.5 && Sev == Seviye.Belirgin && !Mesgul && kapsulaUzaklikDp > Math.Max(Ayar.R, 40))
        { Sev = Seviye.Soluk; BostaSifirla(now); }
    }
}
