using Soz.Core.Registry;

namespace Soz.Core.Proof;

/// <summary>
/// "Bitti'nin tanımı" çekirdeği (d2a). Kişi bir işi verirken üç sade soruyu cevaplar (iş türü, neye bakılırsa bitmiş sayılır, geri alınamaz bir şey var mı);
/// Söz bu cevaplardan ve projenin klasöründe gördüklerinden bir KANIT ÖNERİSİ çıkarır. Kural tabanlıdır: yapay zekâ yok, ağ yok, hiçbir komut çalıştırılmaz
/// (yalnız dosya adları okunur). Hiçbir işte liste boş dönmez: en kötü durumda son madde "sen bakacaksın" onay listesidir.
/// Öneri bir TASLAKTIR: kişi her satırı kabul eder, değiştirir ya da atar; kabul edilene kadar hiçbir ölçüt eklenmez.
/// </summary>
public static class DoneDefinition
{
    public sealed record Option(string Value, string Label, string Hint);
    public sealed record Question(string Id, string Text, string Type, IReadOnlyList<Option> Options, string Hint);
    public sealed record FillHint(string Field, string Label, IReadOnlyList<string> Candidates);
    public sealed record Proposal(string Id, string Template, string Kind, string Sentence, string Level, string LevelLabel, string Who, string Why,
        bool Ready, bool EnabledByDefault, IReadOnlyDictionary<string, string> Values, IReadOnlyList<FillHint> Fill);
    /// <summary>
    /// Testsiz klasör (yalnız kod işinde): işin doğruluğunu sınayan bir test komutu bulunamadı. NothingToTry: test, derleme ve açılan sayfa dahil Söz'ün ölçebileceği hiçbir şey yok.
    /// Action "write-check-first": iş writeCheckFirst:true ile başlatılırsa ajan önce küçük bir deneme yazar (Söz sonradan komutunu ölçüt olarak ÖNERİR, kendiliğinden kabul etmez).
    /// </summary>
    public sealed record Testless(bool NothingToTry, string Message, string Action, string ActionLabel, string StartField);
    public sealed record Result(string JobKind, string JobKindLabel, IReadOnlyList<string> Found, IReadOnlyList<Proposal> Proposals, string Note, bool Measurable = false, Testless? Testless = null);

    public const string HumanWho = "Sen bakarsın", SozWho = "Söz ölçer";

    public static readonly IReadOnlyDictionary<string, string> KindLabels = new Dictionary<string, string>
    {
        ["kod"] = "Kod / yazılım", ["yazi"] = "Yazı / belge", ["arastirma"] = "Araştırma", ["tablo"] = "Tablo / hesap", ["tarayici"] = "Tarayıcı / site", ["dosya-duzeni"] = "Dosya düzeni",
    };

    private static readonly string[] Irreversible = { "posta", "eposta", "mail", "gonder", "mesaj", "odeme", "para", "fatura", "sil", "kaldir", "yayin", "paylas", "sat", "satin", "kargo", "iade", "dagit" };

    public static IReadOnlyList<Question> Questions() => new List<Question>
    {
        new("kind", "Bu iş ne türden?", "choice", KindLabels.Select(k => new Option(k.Key, k.Value, KindHint(k.Key))).ToList(), "Emin değilsen \"Kod / yazılım\" dışında en yakınını seç. Sonra değiştirebilirsin."),
        new("looks", "Biri bu işi bitmiş saymadan önce neye bakar?", "text", Array.Empty<Option>(), "Kendi cümlenle yaz. Söz buradaki sözcüklere bakıp ek madde önerir. Boş bırakabilirsin."),
        new("irreversible", "Bu işte geri alamayacağın bir şey var mı? (para, e-posta, silme, yayın)", "yesno", new List<Option> { new("yes", "Evet", "Sen bakacaksın maddesi eklenir"), new("no", "Hayır", "") }, "Emin değilsen Evet de."),
    };

    private static string KindHint(string k) => k switch
    {
        "kod" => "Test, derleme, açılan sayfa", "yazi" => "Rapor, mektup, belge", "arastirma" => "Kaynaklı bulgu", "tablo" => "CSV, hesap", "tarayici" => "Site ya da uygulama sayfası", "dosya-duzeni" => "Taşıma, adlandırma, düzenleme", _ => "",
    };

    public static string NormalizeKind(string? kind) => kind is not null && KindLabels.ContainsKey(kind) ? kind : "kod";

    /// <summary>Klasörde görülenler (yalnız dosya adları): ilk üç düzey, en çok 4000 giriş.</summary>
    private sealed class Seen
    {
        public List<string> Csv = new(), Docs = new(), Html = new(), Docx = new(), Xlsx = new(), TopDirs = new();
        public bool PackageJson, WebScript;
        /// <summary>package.json'daki sunucu komutundan (dev, start, preview, serve) bilinen adres; dayanak yoksa null.</summary>
        public string? DevUrl;
    }

    private static Seen Look(string? root)
    {
        var s = new Seen();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return s;
        var seenCount = 0;
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seenCount < 4000)
        {
            var (dir, depth) = queue.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (++seenCount >= 4000) break;
                var name = Path.GetFileName(e);
                var rel = Path.GetRelativePath(root, e).Replace('\\', '/');
                FileAttributes attr;
                try { attr = File.GetAttributes(e); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                if ((attr & FileAttributes.Directory) != 0)
                {
                    if (EvidenceBase.IsIgnoredDir(name) || name.StartsWith('.')) continue;
                    if (depth == 0) s.TopDirs.Add(name);
                    if (depth < 2) queue.Enqueue((e, depth + 1));
                    continue;
                }
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) && depth == 0) { s.PackageJson = true; s.WebScript = HasWebScript(e); s.DevUrl = DevUrlOf(e); }
                switch (ext)
                {
                    case ".csv": case ".tsv": s.Csv.Add(rel); break;
                    case ".md": case ".txt": s.Docs.Add(rel); break;
                    case ".docx": s.Docx.Add(rel); break;
                    case ".xlsx": s.Xlsx.Add(rel); break;
                    case ".html": case ".htm": s.Html.Add(rel); break;
                }
            }
        }
        return s;
    }

    private static bool HasWebScript(string packageJson)
    {
        try
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(packageJson)) as System.Text.Json.Nodes.JsonObject;
            return o?["scripts"] is System.Text.Json.Nodes.JsonObject sc && (sc["dev"] is not null || sc["start"] is not null || sc["preview"] is not null);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return false; }
    }

    private static readonly System.Text.RegularExpressions.Regex PortFlag = new(@"(?:--port[= ]|(?<![\w-])-p[= ]|\bPORT=|(?:localhost|127.0.0.1):)(\d{2,5})\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Klasörde çalıştırılabilir bir sunucu komutu ve BİLİNEN bir port var mı? Dayanak yalnız package.json betiğidir (hiçbir komut çalıştırılmaz):
    /// komutta açık port (--port 4000, PORT=4000, localhost:4000) ya da aracın bilinen varsayılan portu (vite 5173, next 3000, webpack serve 8080...). Bilinmiyorsa null: adres uydurulmaz.
    /// </summary>
    internal static string? DevUrlOf(string packageJson)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(packageJson)) is not System.Text.Json.Nodes.JsonObject o || o["scripts"] is not System.Text.Json.Nodes.JsonObject sc) return null;
            foreach (var name in new[] { "dev", "start", "preview", "serve" })
            {
                if (sc[name] is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue<string>(out var body) || string.IsNullOrWhiteSpace(body)) continue;
                var m = PortFlag.Match(body);
                if (m.Success) return "http://localhost:" + m.Groups[1].Value;
                var b = body.ToLowerInvariant();
                bool Has(string x) => System.Text.RegularExpressions.Regex.IsMatch(b, @"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(x) + @"(?![\w-])");
                if (Has("vite")) return name == "preview" ? "http://localhost:4173" : "http://localhost:5173";
                if (Has("next") || Has("react-scripts") || Has("craco") || Has("nuxt") || Has("remix")) return "http://localhost:3000";
                if (Has("astro")) return "http://localhost:4321";
                if (Has("webpack serve") || Has("webpack-dev-server") || Has("vue-cli-service serve") || Has("http-server") || Has("live-server")) return "http://localhost:8080";
                if (Has("ng serve")) return "http://localhost:4200";
                if (Has("parcel")) return "http://localhost:1234";
                if (Has("gatsby develop")) return "http://localhost:8000";
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return null;
    }

    private static List<string> Take(IEnumerable<string> src, int n = 5) => src.OrderBy(x => x.Count(c => c == '/')).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).Take(n).ToList();

    private static IReadOnlyList<string> DefaultHuman(string kind) => kind switch
    {
        "kod" => new[] { "Çalıştırıp kendi gözümle denedim, istediğim gibi davranıyor", "Beklemediğim bir değişiklik ya da silinen şey yok" },
        "yazi" => new[] { "Metni baştan sona okudum, anlatım ve adlar doğru", "İstediğim ton ve uzunluk tutmuş" },
        "arastirma" => new[] { "Verilen kaynaklardan en az ikisini açıp baktım, söylenenle uyuşuyor", "Rakamlar ve tarihler kaynaktaki gibi" },
        "tablo" => new[] { "Tabloyu açıp birkaç satırı elle kontrol ettim", "Toplamlar ve sütun adları beklediğim gibi" },
        "tarayici" => new[] { "Sayfayı kendi gözümle açıp gezdim, bozuk yer yok", "Telefon boyutunda da düzgün görünüyor" },
        "dosya-duzeni" => new[] { "Dosyalar olması gereken yerde, kaybolan yok", "Adlandırma benim istediğim düzende" },
        _ => new[] { "İşin sonucuna kendim baktım, beklediğim gibi" },
    };

    private static List<string> ExtraHuman(string? text, bool irreversible)
    {
        var res = new List<string>();
        var t = Fold.Text(text ?? "");
        var words = System.Text.RegularExpressions.Regex.Split(t, @"[^\p{L}\p{N}]+").Where(x => x.Length > 0).ToList();
        var hit = words.Any(w => Irreversible.Any(x => w == x || (x.Length >= 5 && w.StartsWith(x, StringComparison.Ordinal))));
        if (irreversible || hit)
            res.Add("Geri alınamayacak adımı (gönderme, ödeme, silme, yayın) kendim gözden geçirip onayladım");
        return res;
    }

    private static string Id(string kind, string template, int n) => $"{kind}:{template}:{n}";

    /// <summary>Üç cevaptan ve klasörde görülenlerden öneri listesi. Boş dönmez.</summary>
    public static Result Suggest(string? root, string? kind, string? text, bool irreversible)
    {
        var k = NormalizeKind(kind);
        var seen = Look(root);
        var catalog = CheckTemplates.For(root);
        var list = new List<Proposal>();
        var found = new List<string>();
        var n = 0;

        Proposal? Make(string template, string why, IDictionary<string, string>? values = null, IReadOnlyList<FillHint>? fill = null, string[]? required = null)
        {
            var t = catalog.Templates.FirstOrDefault(x => x.Id == template);
            if (t is null || !t.Available) return null;
            var v = new Dictionary<string, string>(values ?? new Dictionary<string, string>());
            // Adres alanının varsayılanı (http://localhost:3000) hazır sayılmaz: ulaşılamayan adres ölçüt olarak önerilmez (denetim 5, B2). Adres ancak klasörden ya da kişiden gelirse hazırdır.
            var ready = (required ?? Array.Empty<string>()).All(r => v.TryGetValue(r, out var x) && x.Length > 0 || (r != "url" && t.Fields.Any(f => f.Name == r && !string.IsNullOrEmpty(f.Default))));
            var level = MeasurerRegistry.Find(t.Kind)?.Level ?? Confidence.D;
            var sentence = t.Sentence + (t.DetectedCmd is { } c ? $" ({c})" : "");
            // Adres ölçütü sunucu açıkken ölçülür; Söz sunucuyu başlatmaz. Bu yüzden varsayılan işaretli gelmez.
            var byDefault = level != Confidence.D && !t.Fields.Any(f => f.Name == "url");
            return new Proposal(Id(k, template, ++n), template, t.Kind, sentence, level, Confidence.Label(level), level == Confidence.D ? HumanWho : SozWho, why, ready, byDefault, v, fill ?? Array.Empty<FillHint>());
        }
        void Add(Proposal? p) { if (p is not null) list.Add(p); }
        FillHint PathHint(string label, IEnumerable<string> cands, string field = "path") => new(field, label, Take(cands));
        IDictionary<string, string> Pre(string field, IReadOnlyList<string> cands) => cands.Count == 1 ? new Dictionary<string, string> { [field] = cands[0] } : new Dictionary<string, string>();

        var (testCmd, buildCmd) = CheckTemplates.Detect(root);
        if (testCmd is not null) found.Add($"test komutu: {testCmd}");
        if (buildCmd is not null) found.Add($"derleme komutu: {buildCmd}");
        if (seen.Csv.Count > 0) found.Add($"{seen.Csv.Count} tablo dosyası (csv)");
        if (seen.Xlsx.Count > 0) found.Add($"{seen.Xlsx.Count} Excel dosyası (xlsx, Söz henüz okuyamaz, csv olarak kaydetmeni önerir)");
        if (seen.Docs.Count + seen.Docx.Count > 0) found.Add($"{seen.Docs.Count + seen.Docx.Count} belge dosyası");
        if (seen.Html.Count > 0) found.Add($"{seen.Html.Count} web sayfası dosyası");

        var docs = seen.Docs.Concat(seen.Docx).ToList();
        switch (k)
        {
            case "kod":
                Add(Make("tests", "Projede test komutu bulundu. İşin sonunda çalıştırılır.", required: Array.Empty<string>()));
                Add(Make("build", "Projede derleme komutu bulundu.", required: Array.Empty<string>()));
                // Yalnız klasörde çalıştırılabilir bir sunucu komutu ve bilinen port varsa önerilir; html dosyası ya da tahmin edilmiş adres yetmez. İşaretsiz gelir: sunucu açıkken ölçülür.
                if (seen.DevUrl is { } devUrl)
                    Add(Make("local-up", $"Projede sunucu komutu var ({devUrl}). Sunucu açıkken bu adrese cevap vermesi ölçülür; Söz sunucuyu kendisi başlatmaz.", new Dictionary<string, string> { ["url"] = devUrl }, required: new[] { "url" },
                        fill: new[] { new FillHint("url", "adres", new[] { devUrl }) }));
                if (seen.TopDirs.Contains("src", StringComparer.OrdinalIgnoreCase))
                    Add(Make("diff-only", "Yardımcı yalnız kod klasöründe değişiklik yapsın. Başka yere dokunursa görürsün.",
                        new Dictionary<string, string> { ["allowed"] = "src/**" }, new[] { new FillHint("allowed", "değişebilecek dosyalar", new[] { "src/**", "test/**", "tests/**" }) }, new[] { "allowed" }));
                break;
            case "yazi":
                Add(Make("doc-heading", "Belgede beklediğin bölüm başlığı olsun.", Pre("path", Take(docs, 2)), new[] { PathHint("belge", docs), new FillHint("heading", "başlık", Array.Empty<string>()) }, new[] { "path", "heading" }));
                Add(Make("file-range", "Dosya oluşsun ve boş olmasın.", Pre("path", Take(docs, 2)), new[] { PathHint("dosya", docs) }, new[] { "path" }));
                break;
            case "arastirma":
                Add(Make("file-range", "Rapor dosyası oluşsun ve boş olmasın.", Pre("path", Take(docs, 2)), new[] { PathHint("rapor dosyası", docs) }, new[] { "path" }));
                Add(Make("doc-heading", "Raporda \"Kaynaklar\" başlığı olsun (kaynaksız araştırma bitmiş sayılmaz).",
                    new Dictionary<string, string>(Pre("path", Take(docs, 2))) { ["heading"] = "Kaynaklar" }, new[] { PathHint("rapor dosyası", docs) }, new[] { "path", "heading" }));
                break;
            case "tablo":
                Add(Make("table-rows", "Beklediğin satır sayısı tutsun.", Pre("path", Take(seen.Csv, 2)), new[] { PathHint("tablo (csv)", seen.Csv), new FillHint("rows", "satır sayısı", Array.Empty<string>()) }, new[] { "path", "rows" }));
                Add(Make("table-sum", "Bir sütunun toplamı elle bildiğin sayıyla uyuşsun.", Pre("path", Take(seen.Csv, 2)), new[] { PathHint("tablo (csv)", seen.Csv), new FillHint("column", "sütun adı", Array.Empty<string>()), new FillHint("total", "beklenen toplam", Array.Empty<string>()) }, new[] { "path", "column", "total" }));
                Add(Make("table-filled", "Önemli bir sütun boş kalmasın.", Pre("path", Take(seen.Csv, 2)), new[] { PathHint("tablo (csv)", seen.Csv), new FillHint("column", "sütun adı", Array.Empty<string>()) }, new[] { "path", "column" }));
                break;
            case "tarayici":
                var urlVals = seen.DevUrl is { } du ? new Dictionary<string, string> { ["url"] = du } : null;
                var urlCands = seen.DevUrl is { } du2 ? new[] { du2 } : new[] { "http://localhost:3000", "http://localhost:5173", "http://127.0.0.1:8080" };
                Add(Make("local-up", "Sayfa kendi bilgisayarında cevap versin. Adresi sen yaz; sunucu açıkken ölçülür.", urlVals, required: new[] { "url" },
                    fill: new[] { new FillHint("url", "adres", urlCands) }));
                Add(Make("page-text", "Sayfada beklediğin yazı görünsün.", urlVals, required: new[] { "url", "text" }, fill: new[] { new FillHint("url", "adres", urlCands), new FillHint("text", "yazı", Array.Empty<string>()) }));
                Add(Make("screenshot", "Sayfanın ekran görüntüsü deneme sonucu olarak saklansın (bakmak sana kalır).", urlVals, required: new[] { "url" }, fill: new[] { new FillHint("url", "adres", urlCands) }));
                break;
            case "dosya-duzeni":
                Add(Make("diff-only", "Yalnız düzenlemek istediğin dosyalar değişsin. Başka dosyaya dokunulursa görürsün.", required: new[] { "allowed" },
                    fill: new[] { new FillHint("allowed", "değişebilecek dosyalar", Take(seen.TopDirs.Select(d => d + "/**"))) }));
                Add(Make("file-absent", "Taşınan ya da silinmesi beklenen eski dosya yerinde durmasın.", required: new[] { "path" }, fill: new[] { new FillHint("path", "dosya", Array.Empty<string>()) }));
                Add(Make("file-range", "Yeni yerinde dosya olsun ve boş olmasın.", required: new[] { "path" }, fill: new[] { new FillHint("path", "dosya", Array.Empty<string>()) }));
                break;
        }

        // Metinden gelen ek maddeler ile tür varsayılanları tek onay listesinde birleşir; liste her zaman son sıradadır.
        var human = ExtraHuman(text, irreversible).Concat(DefaultHuman(k)).Distinct().ToList();
        var humanWhy = list.Count == 0
            ? "Bu işte Söz'ün kendi başına ölçebileceği bir şey bulamadım. Bitti demek senin bakışına kalıyor."
            : "Ölçülemeyen kısım için: bunları sen kendin işaretleyeceksin. Bu bir ölçüm değil, senin onayındır.";
        var hv = new Dictionary<string, string> { ["items"] = string.Join("\n", human) };
        var hp = Make("human-list", humanWhy, hv, required: new[] { "items" })
                 ?? throw new SozException(500, "İnsan onay listesi şablonu kayıt defterinde yok.");
        list.Add(hp);

        var note = list.Count == 1
            ? "Söz bu iş için kendi başına ölçebileceği bir şey bulamadı. Yalnız senin onay listen var. Bir dosya ya da adres belirtirsen ölçülebilir madde eklenir."
            : "Önerilerin hepsi taslaktır. Kabul etmeden hiçbir kontrol eklenmez. \"Söz ölçer\" satırları gerçekten ölçülür, \"Sen bakarsın\" satırlarını sen işaretlersin.";
        // Söz'ün ölçebileceği (insan listesi dışında, doldurulmuş) madde var mı; kod işinde test komutu yoksa testsiz klasör bilgisi.
        var measurable = list.Any(x => x.Who == SozWho && x.Ready);
        Testless? testless = null;
        if (k == "kod" && testCmd is null)
        {
            var strong = list.Any(x => x.Template is "build" or "local-up");
            testless = new Testless(!strong, !strong ? "Bu klasörde Söz'ün kendi başına deneyebileceği bir şey yok." : "Bu klasörde işin doğruluğunu sınayan bir test yok.",
                "write-check-first", "Yardımcıya önce küçük bir deneme yazdır", "writeCheckFirst");
        }
        return new Result(k, KindLabels[k], found, list, note, measurable, testless);
    }

    /// <summary>Güven etiketi sözlüğü (arayüz için): düzey → ad + sade anlam.</summary>
    public static IReadOnlyList<(string Level, string Label, string Meaning)> Levels() => new[]
    {
        (Confidence.A, Confidence.Label(Confidence.A), "Söz kendisi ölçtü: komut çalıştı, dosya ya da tablo okundu."),
        (Confidence.B, Confidence.Label(Confidence.B), "Söz baktı ama dolaylı: sayfa cevap verdi ama içeriği ayrıca senin gözün ister."),
        (Confidence.C, Confidence.Label(Confidence.C), "Yardımcı öyle söyledi. Söz doğrulayamadı."),
        (Confidence.D, Confidence.Label(Confidence.D), "Deneme sonucu yok ya da kabul senin: kendi işaretlediğin maddeler."),
    };
}
