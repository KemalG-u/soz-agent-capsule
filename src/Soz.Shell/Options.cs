namespace Soz.Shell;

/// <summary>Komut satırı seçenekleri ve veri klasörü kararı.</summary>
internal sealed class Options
{
    public string? DataDir { get; private set; }
    public int Port { get; private set; } = 49300;
    public string? SmokePath { get; private set; }
    public bool FakeApi { get; private set; }
    public bool NoCapsule { get; private set; }
    public bool SmokePanel { get; private set; }
    public string? SnapshotPath { get; private set; }
    public bool SnapshotPanel { get; private set; }
    public bool SelfTest { get; private set; }
    public string? SelfTestPath { get; private set; }
    public bool RequestExit { get; private set; }
    /// <summary>Windows açılışında sessiz başlatma kipi: kapsül ve tepsi açılır ama panel kendiliğinden öne gelmez.</summary>
    public bool Background { get; private set; }
    /// <summary>Yalnız deneme için: dikte köprüsünün çalıştıracağı yol (gerçek KarincaAI yerine sahte program).</summary>
    public string? DictationPathOverride { get; private set; }
    /// <summary>Hangi kapsül: "yeni" ya da "eski". Verilirse kayıt defterindeki ayarın önüne geçer (deneme için). Smoke kipinde verilmezse eski kapsül kullanılır (duman sınaması eski kapsülün düğmelerini ölçer).</summary>
    public string? KapsulTuru { get; private set; }
    /// <summary>Yalnız deneme için: yeni kapsüle eklenecek ham argümanlar (ör. kısa ömürlü çalıştırma).</summary>
    public string? KapsulEk { get; private set; }
    /// <summary>Tek örnek kilidine eklenen ad: aynı bilgisayarda ayrı bir deneme örneği kullanıcının açık programını susturmasın diye.</summary>
    public string? OrnekAdi { get; private set; }
    /// <summary>Ölçüm kipi: sonuç dosyası ve boşta bekleme süresi (sn). Bkz. <see cref="Olcum"/>.</summary>
    public string? OlcPath { get; private set; }
    public int OlcBekleSn { get; private set; } = 60;
    /// <summary>Ölçüm penceresinin uzunluğu (sn) ve panelin açık ölçülmesi.</summary>
    public int OlcSureSn { get; private set; } = 60;
    public bool OlcPanel { get; private set; }
    /// <summary>Yalnız deneme: "Pencereyi Aç" mantığını proje yoluyla çalıştırır ve sonucu yazar (<c>--pencere-ac &lt;proje yolu&gt; [--pencere-pid n] [--pencere-hwnd n]</c>).</summary>
    public string? PencereAcYolu { get; private set; }
    public string? PencereBaslik { get; private set; }
    public int? PencerePid { get; private set; }
    public long? PencereHwnd { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// Tek örnek kilidinin adı. Veri klasörü açıkça verildiyse (--data ya da SOZ_DATA) kilit o klasöre özeldir: başka bir veri klasörüyle açılan örnek (ör. deneme sürümü)
    /// kullanıcının kendi açık programını susturmaz ve onun tarafından susturulmaz. Varsayılan klasörde eski ad aynen kalır (eski ve yeni sürüm birbirini görür).
    /// Aynı veri klasörüyle ikinci açılış yine sessizce çıkar.
    /// </summary>
    public string TekOrnekAdi(string taban, string veriKlasoru)
    {
        var ad = taban + (string.IsNullOrWhiteSpace(OrnekAdi) ? "" : "." + OrnekAdi);
        var acik = !string.IsNullOrWhiteSpace(DataDir) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOZ_DATA"));
        if (!acik) return ad;
        ulong h = 14695981039346656037UL;
        foreach (var c in Path.GetFullPath(veriKlasoru).TrimEnd('\\', '/').ToLowerInvariant()) { h ^= c; h *= 1099511628211UL; }
        return ad + "." + h.ToString("x16");
    }

    public bool IsHeadless => SnapshotPath != null || SelfTest;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--data": o.DataDir = Next() ?? Missing(o, "--data"); break;
                case "--port":
                    var p = Next();
                    if (!int.TryParse(p, out var port) || port < 0 || port > 65535) o.Error ??= "--port geçersiz";
                    else o.Port = port;
                    break;
                case "--smoke": o.SmokePath = Next() ?? Missing(o, "--smoke"); break;
                case "--fake-api": o.FakeApi = true; break;
                case "--no-capsule": o.NoCapsule = true; break;
                case "--smoke-panel": o.SmokePanel = true; break;
                case "--snapshot": o.SnapshotPath = Next() ?? Missing(o, "--snapshot"); break;
                case "--snapshot-panel": o.SnapshotPanel = true; break;
                case "--selftest":
                    o.SelfTest = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.SelfTestPath = args[++i];
                    break;
                case "--exit": o.RequestExit = true; break;
                case "--background": o.Background = true; break;
                case "--kapsul": o.KapsulTuru = Next() ?? Missing(o, "--kapsul"); break;
                case "--kapsul-ek": o.KapsulEk = Next() ?? Missing(o, "--kapsul-ek"); break;
                case "--ornek-adi": o.OrnekAdi = Next() ?? Missing(o, "--ornek-adi"); break;
                case "--olc-json": o.OlcPath = Next() ?? Missing(o, "--olc-json"); break;
                case "--olc-bekle": if (int.TryParse(Next(), out var ob) && ob >= 0 && ob <= 3600) o.OlcBekleSn = ob; else o.Error ??= "--olc-bekle 0-3600 sn olmalı"; break;
                case "--pencere-ac": o.PencereAcYolu = Next() ?? Missing(o, "--pencere-ac"); break;
                case "--pencere-baslik": o.PencereBaslik = Next() ?? Missing(o, "--pencere-baslik"); break;
                case "--pencere-pid": if (int.TryParse(Next(), out var pp)) o.PencerePid = pp; else o.Error ??= "--pencere-pid sayı olmalı"; break;
                case "--pencere-hwnd": if (long.TryParse(Next(), out var ph)) o.PencereHwnd = ph; else o.Error ??= "--pencere-hwnd sayı olmalı"; break;
                case "--olc-sure": if (int.TryParse(Next(), out var os) && os >= 5 && os <= 3600) o.OlcSureSn = os; else o.Error ??= "--olc-sure 5-3600 sn olmalı"; break;
                case "--olc-panel": o.OlcPanel = true; break;
                case "--dictation-path": o.DictationPathOverride = Next() ?? Missing(o, "--dictation-path"); break;
                default:
                    if (args[i].StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)
                        || args[i].StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = Next();
                        break;
                    }
                    o.Error ??= "Bilinmeyen argüman: " + args[i];
                    break;
            }
        }
        return o;
    }

    private static string? Missing(Options o, string name) { o.Error ??= name + " için değer gerekli"; return null; }

    /// <summary>Komut satırıyla aynı sıra: --data, SOZ_DATA, exe yanındaki veri\ (varsa), üst klasörde veri\soz.db (varsa), yeni exe yanı veri\.</summary>
    public string ResolveDataDir()
        => Path.GetFullPath(Soz.Core.SourceLocator.ResolveDataDirWithSource(string.IsNullOrWhiteSpace(DataDir) ? null : DataDir, AppContext.BaseDirectory).Path);
}
