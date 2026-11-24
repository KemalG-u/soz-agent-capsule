using System.Text.Json.Nodes;
using Soz.Core.Registry;

namespace Soz.Core.Proof;

/// <summary>
/// Düz cümleyle ölçüt (EK A). Kullanıcı bir cümle seçer, boşluğu doldurur; cümle command/file/url/screenshot ölçütüne çevrilir,
/// etiket kullanıcının cümlesidir. Komut gerektiren cümleler (test, derleme) projede bulunan komutu kullanır; bulunamazsa gri ve nedeni yazılı.
/// HİÇBİR KOMUT ÇALIŞTIRILMAZ: tespit yalnız dosya okur (CheckStore.Scan).
/// </summary>
public static class CheckTemplates
{
    public sealed record Field(string Name, string Label, string Placeholder, string? Default);
    public sealed record Template(string Id, string Sentence, string Kind, IReadOnlyList<Field> Fields, bool Available, string? Reason, string? DetectedCmd, bool Weak = false, string? Note = null);
    public sealed record Catalog(IReadOnlyList<Template> Templates, bool HasTests, string? Hint);

    public const string NoTestsHint = "Bu projede otomatik test bulamadım. En azından 'şu adres açılsın' ya da 'ekran görüntüsü al' kontrolü ekle. Yoksa işler 'denenmedi' kalır.";
    public const int MaxText = 200;
    /// <summary>Çok satırlı boşluklar (onay listesi, beklenen dosya listesi) için üst sınır.</summary>
    public const int MaxBlockText = 4000;

    // Eski yedi cümle kodda donuk durur (dondurulmuş karşılaştırma testi). Yeni türlerin cümleleri kayıt defterindedir (olcutler.soz.json).
    private static readonly HashSet<string> LegacyIds = new(StringComparer.Ordinal) { "tests", "build", "url-opens", "page-text", "file-exists", "file-text", "screenshot" };

    private sealed record RegTemplate(string Id, string Sentence, string KindName, IReadOnlyList<Field> Fields);

    /// <summary>Kayıt defterindeki ölçüt türlerinin düz cümleleri (eski yedi cümle hariç). Ölçücüsü bulunmayan tür atlanır.</summary>
    private static List<RegTemplate> RegistryTemplates()
    {
        var res = new List<RegTemplate>();
        foreach (var e in FixedCatalog.Get.OfKind(RegistryKinds.CheckKind))
        {
            var kind = ((e.Body["measure"] as JsonObject)?["name"] as JsonValue)?.GetValue<string>();
            if (kind is null || MeasurerRegistry.Find(kind) is null || e.Body["templates"] is not JsonArray arr) continue;
            foreach (var tn in arr.OfType<JsonObject>())
            {
                var id = (tn["id"] as JsonValue)?.GetValue<string>();
                if (id is null || LegacyIds.Contains(id)) continue;
                var sentence = LocalizedText.From(tn["sentence"])?.Tr ?? id;
                var fields = new List<Field>();
                foreach (var fn in (tn["fields"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    fields.Add(new Field((fn["name"] as JsonValue)?.GetValue<string>() ?? "", LocalizedText.From(fn["label"])?.Tr ?? "", (fn["placeholder"] as JsonValue)?.GetValue<string>() ?? "",
                        fn["default"] is JsonValue dv ? dv.ToString() : null));
                res.Add(new RegTemplate(id, sentence, kind, fields));
            }
        }
        return res;
    }

    private static string ValueText(JsonNode? n) => n switch
    {
        null => "",
        JsonArray a => string.Join("\n", a.Select(ValueText)),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
        _ => n.ToJsonString().Trim('"'),
    };

    private static readonly Field UrlField = new("url", "adres", "http://localhost:3000", "http://localhost:3000");

    /// <summary>Projede bulunan test ve derleme komutları (dosya okuyarak).</summary>
    public static (string? test, string? build) Detect(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return (null, null);
        var s = CheckStore.Scan(root);
        string? test = s.Select(x => x.Cmd).FirstOrDefault(IsTestCmd);
        string? build = s.Select(x => x.Cmd).FirstOrDefault(c => c.StartsWith("npm run build", StringComparison.OrdinalIgnoreCase) || c.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase));
        if (build is null && File.Exists(Path.Combine(root, "Cargo.toml"))) build = "cargo build";
        if (build is null && File.Exists(Path.Combine(root, "go.mod"))) build = "go build ./...";
        return (test, build);
    }

    private static bool IsTestCmd(string c) => c is "npm test" or "cargo test" or "go test ./..." or "make test" or "python -m pytest -q"
        || c.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase);

    public static Catalog For(string? root)
    {
        var known = !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        var (test, build) = Detect(root);
        string? Why(string? cmd, string what) => cmd is not null ? null
            : known ? $"Bu projede {what} komutu bulamadım (npm, .NET, Python, Rust, Go ve Make projelerinin ayar dosyalarına baktım). \"Kendi komutum\" ile ekleyebilirsin."
            : "Projenin klasörü bilinmiyor. Komut aranamadı.";
        var list = new List<Template>
        {
            new("tests", "Testler geçsin", CheckKinds.Command, Array.Empty<Field>(), test is not null, Why(test, "test"), test),
            new("build", "Proje derlensin", CheckKinds.Command, Array.Empty<Field>(), build is not null, Why(build, "derleme"), build),
            new("url-opens", "Şu adres açılsın", CheckKinds.Url, new[] { UrlField }, true, null, null),
            new("page-text", "Sayfada şu yazı görünsün", CheckKinds.Url, new[] { UrlField, new Field("text", "yazı", "Hoş geldin", null) }, true, null, null),
            new("file-exists", "Şu dosya oluşsun", CheckKinds.File, new[] { new Field("path", "dosya yolu", "dist/index.html", null) }, true, null, null),
            new("file-text", "Dosyada şu yazı geçsin", CheckKinds.File, new[] { new Field("path", "dosya yolu", "README.md", null), new Field("text", "yazı", "Kurulum", null) }, true, null, null, Weak: true, Note: "Bu kontrolü yardımcı kendi yazdığı dosyayla da doldurabilir."),
            new("screenshot", "Sayfanın ekran görüntüsünü al", CheckKinds.Screenshot, new[] { UrlField }, true, null, null),
        };
        list.AddRange(RegistryTemplates().Select(t => new Template(t.Id, t.Sentence, t.KindName, t.Fields, true, null, null)));
        return new Catalog(list, test is not null, test is null ? NoTestsHint : null);
    }

    /// <summary>Şablon + değerler → (tür, etiket, spec). Hatalı girişte SozException(400).</summary>
    public static (string kind, string label, JsonObject spec) Build(string templateId, JsonObject? values, string? root)
    {
        var cat = For(root);
        var t = cat.Templates.FirstOrDefault(x => x.Id == templateId) ?? throw new SozException(400, "Bilinmeyen cümle: " + templateId);
        if (!t.Available) throw new SozException(409, t.Reason ?? "Bu cümle bu projede kullanılamıyor.");
        string V(string name)
        {
            var v = values?[name] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s.Trim() : "";
            if (v.Length == 0) v = t.Fields.FirstOrDefault(f => f.Name == name)?.Default ?? "";
            if (v.Length == 0) throw new SozException(400, $"\"{t.Sentence}\" için {t.Fields.First(f => f.Name == name).Label} boş olamaz.");
            if (v.Length > MaxText) throw new SozException(400, $"{t.Fields.First(f => f.Name == name).Label} en çok {MaxText} karakter olabilir.");
            return v;
        }
        string Url()
        {
            var u = V("url");
            if (!Uri.TryCreate(u, UriKind.Absolute, out var x) || x.Scheme is not ("http" or "https")) throw new SozException(400, "Adres http:// ya da https:// ile başlamalı: " + u);
            return u;
        }
        string PathIn()
        {
            var p = V("path");
            if (!string.IsNullOrWhiteSpace(root))
            {
                var full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(root, p));
                if (!SozService.IsUnder(full, root)) throw new SozException(400, "Dosya proje klasörünün içinde olmalı: " + p);
            }
            return p;
        }

        switch (t.Id)
        {
            case "tests": return (CheckKinds.Command, $"Testler geçsin ({t.DetectedCmd})", new JsonObject { ["cmd"] = t.DetectedCmd, ["expectExit"] = 0, ["template"] = t.Id });
            case "build": return (CheckKinds.Command, $"Proje derlensin ({t.DetectedCmd})", new JsonObject { ["cmd"] = t.DetectedCmd, ["expectExit"] = 0, ["template"] = t.Id });
            case "url-opens": { var u = Url(); return (CheckKinds.Url, $"Şu adres açılsın: {u}", new JsonObject { ["url"] = u, ["expectStatus"] = 200, ["template"] = t.Id }); }
            case "page-text": { var u = Url(); var x = V("text"); return (CheckKinds.Url, $"Sayfada şu yazı görünsün: \"{x}\" ({u})", new JsonObject { ["url"] = u, ["expectStatus"] = 200, ["contains"] = x, ["template"] = t.Id }); }
            case "file-exists": { var p = PathIn(); return (CheckKinds.File, $"Şu dosya oluşsun: {p}", new JsonObject { ["path"] = p, ["exists"] = true, ["template"] = t.Id }); }
            case "file-text": { var p = PathIn(); var x = V("text"); return (CheckKinds.File, $"Dosyada şu yazı geçsin: \"{x}\" ({p})", new JsonObject { ["path"] = p, ["contains"] = x, ["template"] = t.Id }); }
            case "screenshot": { var u = Url(); return (CheckKinds.Screenshot, $"Sayfanın ekran görüntüsünü al: {u}", new JsonObject { ["url"] = u, ["template"] = t.Id }); }
            default: return BuildFromRegistry(templateId, values, root);
        }
    }

    /// <summary>Kayıt defterindeki cümle: boşlukları doldurur (varsayılanı olmayan boş bırakılamaz), değerleri ölçücüye verir, ölçücünün ürettiği tanımı doğrular.</summary>
    private static (string kind, string label, JsonObject spec) BuildFromRegistry(string templateId, JsonObject? values, string? root)
    {
        var rt = RegistryTemplates().FirstOrDefault(x => x.Id == templateId) ?? throw new SozException(400, "Bilinmeyen cümle: " + templateId);
        var m = MeasurerRegistry.Find(rt.KindName) ?? throw new SozException(400, "Bilinmeyen cümle: " + templateId);
        var vals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in rt.Fields)
        {
            var v = ValueText(values?[f.Name]);
            if (v.Length == 0)
            {
                if (f.Default is null) throw new SozException(400, $"\"{rt.Sentence}\" için {f.Label} boş olamaz.");
                v = f.Default;
            }
            if (v.Length > MaxBlockText) throw new SozException(400, $"{f.Label} en çok {MaxBlockText} karakter olabilir.");
            vals[f.Name] = v;
        }
        var (label, spec) = m.FromTemplate(templateId, vals, root);
        if (m.Validate(spec) is { } err) throw new SozException(400, err);
        return (m.Kind, label, spec);
    }
}
