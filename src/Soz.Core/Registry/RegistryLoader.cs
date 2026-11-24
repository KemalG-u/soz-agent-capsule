using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soz.Core.Registry;

/// <summary>Bir kayıt dosyasının ham hâli: ad (görünen), köken (core ya da file:yol) ve baytlar (özet bunlardan çıkar).</summary>
public sealed record RegistrySource(string Name, string Origin, byte[] Bytes)
{
    public static RegistrySource FromText(string name, string origin, string text) => new(name, origin, new UTF8Encoding(false).GetBytes(text));
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
}

/// <summary>Kişi klasörünün okunması: okunan dosyalar, reddedilenler (hata) ve uyarılar. `var (sources, problems) = ...` ile de açılır.</summary>
public sealed class UserDirRead
{
    public List<RegistrySource> Sources { get; } = new();
    public List<RegistryProblem> Problems { get; } = new();
    public List<RegistryProblem> Warnings { get; } = new();
    public void Deconstruct(out IReadOnlyList<RegistrySource> sources, out IReadOnlyList<RegistryProblem> problems) { sources = Sources; problems = Problems; }
}

/// <summary>
/// Kayıt yükleyici: pakete gömülü `fixed` kayıtlar + verilen bir klasörden `user` kayıtları (klasörü çağıran verir; bu sınıf veri klasörü yolu çözmez).
/// Sahiplik kuralları (rapor 1.2): fixed kimliğini başka sahip kullanamaz; owner=system yalnız suggested; dosyadan gelen çalıştırılabilir kayıt
/// (bağlayıcı, çalıştırılabilir ölçüt türü, K4 ayar, exec izni isteyen) ilk gelişte `suggested` yüklenir, onaylı özetle (<see cref="IRegistryApprovals"/>) etkinleşir.
/// Hiçbir kullanıcı dosyası yükleyiciyi çökertmemeli: bozuk dosya ya da kayıt adıyla ve nedeniyle işaretlenir, öteki dosyalar ve gömülü kayıtlar yüklenmeye devam eder.
/// </summary>
public static class RegistryLoader
{
    private const string FixedPrefix = "Soz.Core.kayit.";
    private const string SchemaResource = "Soz.Core.registry-1.schema.json";
    public const int MaxErrorsPerEntry = 8;
    /// <summary>Kişi dosyası başına en çok bayt (1 MB).</summary>
    public const int MaxFileBytes = 1_048_576;
    /// <summary>Klasörde okunacak en çok dosya; fazlası atlanır ve uyarı olur.</summary>
    public const int MaxFilesInDir = 500;
    /// <summary>Klasör içinde okunacak en derin alt klasör sayısı; daha derindeki dosya atlanır ve uyarı olur (sessiz atlanmaz).</summary>
    public const int MaxDirDepth = 8;
    private const int MaxDirScan = 5000;
    private const int MaxDirDepthSeen = 40;

    private static readonly Lazy<string> SchemaJson = new(() => ReadResource(SchemaResource));
    private static readonly Lazy<SchemaCheck> SchemaLazy = new(() => SchemaCheck.FromJson(SchemaJson.Value));

    /// <summary>
    /// Sınama için: doluysa gömülü kayıt dosyaları yerine bunlar kullanılır (yalnız çağıran bağlamda, `AsyncLocal`; başka testleri etkilemez) ve önbellek atlanır.
    /// Üretimde hiçbir zaman dolu olmaz.
    /// </summary>
    internal static readonly AsyncLocal<IReadOnlyList<RegistrySource>?> EmbeddedOverride = new();

    /// <summary>Aygıt adı (NUL, COM1...) denetimi yalnız klasör yolu denetlenirken gerekir; düzenli ifade o zaman kurulur.</summary>
    private static readonly Lazy<Regex> DeviceName = new(() => new Regex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));

    /// <summary>
    /// Kimlik biçimi: ASCII küçük harf, rakam, tire/nokta/alt çizgi, sonda satır sonu yok. Düzenli ifade değil karakter döngüsüdür (sıcak yolda ifade kurulmaz).
    /// Şemadaki `pattern` (`^[a-z][a-z0-9-]*:[a-z][a-z0-9._-]{0,63}\z`) ile aynı kuraldır; bir test ikisini karşılaştırır.
    /// </summary>
    private static bool MatchesIdShape(string id)
    {
        if (id.Length == 0 || !IsLower(id[0])) return false;
        var i = 0;
        while (i < id.Length && (IsLower(id[i]) || IsDigit(id[i]) || id[i] == '-')) i++;
        if (i >= id.Length || id[i] != ':') return false;
        var rest = id.Length - (i + 1);
        if (rest < 1 || rest > 64 || !IsLower(id[i + 1])) return false;
        for (var j = i + 2; j < id.Length; j++)
            if (!(IsLower(id[j]) || IsDigit(id[j]) || id[j] is '.' or '_' or '-')) return false;
        return true;
    }
    private static bool IsLower(char c) => c is >= 'a' and <= 'z';
    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>Gömülü şema (kaynak: schema\registry-1.schema.json).</summary>
    public static SchemaCheck Schema => SchemaLazy.Value;
    public static string SchemaText => SchemaJson.Value;

    /// <summary>Kimlik biçimine uyuyor mu (<c>tür:ad</c>, ASCII küçük harf)?</summary>
    public static bool IsValidId(string? id) => id is not null && MatchesIdShape(id);

    private static string ReadResource(string name)
    {
        using var s = typeof(RegistryLoader).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Gömülü kaynak yok: " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Pakete gömülü sabit kayıt dosyaları (ada göre sıralı).</summary>
    public static IReadOnlyList<RegistrySource> EmbeddedSources()
    {
        if (EmbeddedOverride.Value is { } over) return over;
        var asm = typeof(RegistryLoader).Assembly;
        var list = new List<RegistrySource>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(FixedPrefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            list.Add(new RegistrySource(name[FixedPrefix.Length..], "core", ms.ToArray()));
        }
        return list;
    }

    /// <summary>
    /// Kayıt klasörü yolunu denetler. Geçerliyse null döner ve tam yolu verir; değilse sade Türkçe neden.
    /// Reddedilenler: boş ya da geçersiz yol, ağ yolu (UNC) ve aygıt yolları, aygıt adları (NUL, COM1...), dosya, olmayan klasör, bağlantı (junction/sembolik bağ).
    /// </summary>
    public static string? CheckDir(string? dir, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(dir)) return "klasör yolu boş.";
        if (dir.Length > 1024) return "klasör yolu çok uzun.";
        if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || dir.Contains('\0')) return "klasör yolunda geçersiz karakter var.";
        var t = dir.Trim();
        const string unc = "ağ yolları (\\\\sunucu\\paylaşım) ve aygıt yolları (\\\\.\\, \\\\?\\) kabul edilmez. Bu bilgisayardaki bir klasörün yolunu ver.";
        if (t.StartsWith(@"\\", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal)) return unc;
        string full;
        try { full = Path.GetFullPath(t); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return "klasör yolu geçerli değil."; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return unc;
        foreach (var seg in full.Split('\\', '/'))
            if (seg.Length > 0 && DeviceName.Value.IsMatch(seg.TrimEnd(' ', '.'))) return "yolda bir aygıt adı var (NUL, COM1 gibi); gerçek bir klasörün yolunu ver.";
        try
        {
            if (File.Exists(full)) return "verilen yol bir dosya. Bir klasör olmalı.";
            if (!Directory.Exists(full)) return "klasör bulunamadı.";
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return "klasör bir bağlantı (junction ya da sembolik bağ). Bağlantılar izlenmez, gerçek klasörün yolunu ver.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "klasöre erişilemedi."; }
        fullPath = full;
        return null;
    }

    /// <summary>
    /// Verilen klasördeki (alt klasörler dahil, bağlantılar izlenmeden) *.soz.json dosyaları. Klasör null ya da boşsa hiçbir şey okunmaz.
    /// Klasör verilip geçersizse (yok, dosya, UNC...) bir sorun döner. Dosya başına 1 MB, klasörde en çok 500 dosya.
    /// </summary>
    public static UserDirRead ReadUserDir(string? dir)
    {
        var read = new UserDirRead();
        if (string.IsNullOrWhiteSpace(dir)) return read;
        if (CheckDir(dir, out var full) is { } why)
        {
            read.Problems.Add(new RegistryProblem("kayıt klasörü", null, "dir", "Kayıt klasörü kullanılamıyor: " + why));
            return read;
        }
        var names = new List<string>();
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = MaxDirDepthSeen, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
            foreach (var f in Directory.EnumerateFiles(full!, "*.soz.json", opt)) { names.Add(f); if (names.Count >= MaxDirScan) break; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            read.Problems.Add(new RegistryProblem("kayıt klasörü", null, "read", "Klasör okunamadı."));
            return read;
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        // Çok derindeki dosya sessizce atlanmaz: adıyla uyarı olur (en çok 5 ad, fazlası sayıyla).
        var tooDeep = names.Where(f => Path.GetRelativePath(full!, f).Count(ch => ch is '\\' or '/') > MaxDirDepth).ToList();
        if (tooDeep.Count > 0)
        {
            foreach (var f in tooDeep.Take(5))
                read.Warnings.Add(new RegistryProblem(Path.GetFileName(f), null, "dir-depth", $"Dosya {MaxDirDepth} alt klasörden daha derinde. Okunmadı. Dosyayı daha yukarı bir klasöre taşı."));
            if (tooDeep.Count > 5) read.Warnings.Add(new RegistryProblem("kayıt klasörü", null, "dir-depth", $"... ve {tooDeep.Count - 5} dosya daha çok derinde olduğu için okunmadı."));
            var skip = tooDeep.ToHashSet(StringComparer.Ordinal);
            names.RemoveAll(skip.Contains);
        }
        if (names.Count > MaxFilesInDir)
        {
            read.Warnings.Add(new RegistryProblem("kayıt klasörü", null, "dir-limit", $"Klasörde en çok {MaxFilesInDir} dosya okunur. Fazlası atlandı (ilk {MaxFilesInDir} dosya ada göre alındı)."));
            names.RemoveRange(MaxFilesInDir, names.Count - MaxFilesInDir);
        }
        foreach (var f in names)
        {
            var rel = Path.GetRelativePath(full!, f);
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                var buf = new byte[MaxFileBytes + 1];
                var n = 0;
                int got;
                while (n < buf.Length && (got = fs.Read(buf, n, buf.Length - n)) > 0) n += got;
                if (n > MaxFileBytes) { read.Problems.Add(new RegistryProblem(rel, null, "size", $"Dosya çok büyük (en çok {MaxFileBytes / 1_048_576} MB). Okunmadı.")); continue; }
                read.Sources.Add(new RegistrySource(rel, "file:" + f, buf.AsSpan(0, n).ToArray()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { read.Problems.Add(new RegistryProblem(rel, null, "read", "Dosya okunamadı.")); }
        }
        return read;
    }

    /// <summary>Gömülü sabit kayıtlar + (verildiyse) kişi klasörü.</summary>
    public static RegistryLoadResult LoadEmbedded(string? userDir = null, IRegistryApprovals? approvals = null)
    {
        var read = ReadUserDir(userDir);
        var res = Load(EmbeddedSources(), read.Sources, approvals);
        res.Rejected.AddRange(read.Problems);
        res.Warnings.AddRange(read.Warnings);
        return res;
    }

    public static RegistryLoadResult Load(IEnumerable<RegistrySource> fixedSources, IEnumerable<RegistrySource> userSources, IRegistryApprovals? approvals = null)
    {
        var res = new RegistryLoadResult();
        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in fixedSources)
        {
            LoadSource(s, true, res, firstSeen, new HashSet<string>(StringComparer.Ordinal), approvals);
        }
        var fixedIds = new HashSet<string>(res.Entries.Where(e => e.Owner == RegistryOwners.Fixed).Select(e => e.Id), StringComparer.Ordinal);
        foreach (var s in userSources) LoadSource(s, false, res, firstSeen, fixedIds, approvals);
        return res;
    }

    private static void LoadSource(RegistrySource src, bool isFixed, RegistryLoadResult res, Dictionary<string, string> firstSeen, HashSet<string> fixedIds, IRegistryApprovals? approvals)
    {
        try { LoadSourceCore(src, isFixed, res, firstSeen, fixedIds, approvals); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Genel güvenlik ağı: asıl iş her durumu adıyla yakalamaktır; buraya düşen şey bir yükleyici açığıdır ama başka dosyayı bozmaz.
            res.Rejected.Add(new RegistryProblem(src.Name, null, "internal", $"Dosya işlenirken beklenmeyen bir hata oldu ({e.GetType().Name}). Dosya yüklenmedi, öteki dosyalar etkilenmedi.", isFixed));
        }
    }

    private static void LoadSourceCore(RegistrySource src, bool isFixed, RegistryLoadResult res, Dictionary<string, string> firstSeen, HashSet<string> fixedIds, IRegistryApprovals? approvals)
    {
        void Reject(string? id, string code, string msg) => res.Rejected.Add(new RegistryProblem(src.Name, id, code, msg, isFixed));

        if (!isFixed && src.Bytes.Length > MaxFileBytes) { Reject(null, "size", $"Dosya çok büyük (en çok {MaxFileBytes / 1_048_576} MB). Okunmadı."); return; }

        var text = new UTF8Encoding(false, false).GetString(src.Bytes).TrimStart('﻿');
        JsonNode? root;
        try
        {
            using (var doc = JsonDocument.Parse(text))
            {
                if (FindDuplicate(doc.RootElement, "$") is { } dup)
                { Reject(null, "json", $"Aynı nesnede bir alan iki kez yazılmış (yinelenen anahtar, {dup}). Dosya yüklenmedi."); return; }
            }
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            Reject(null, "json", $"JSON okunamadı (satır {(e.LineNumber ?? 0) + 1}, sütun {(e.BytePositionInLine ?? 0) + 1}): biçim bozuk. Sondaki virgül, yorum satırı, kapanmamış ayraç ya da 64'ten derin iç içe yapı olabilir.");
            return;
        }
        if (root is not JsonObject file) { Reject(null, "file", "Dosya bir JSON nesnesi olmalı: {\"schemaVersion\": 1, \"entries\": [...]}."); return; }

        if (file["schemaVersion"] is not JsonValue ver || ver.GetValueKind() != JsonValueKind.Number)
        { Reject(null, "file", "schemaVersion yok ya da sayı değil. Dosya yüklenmedi."); return; }
        if (!ver.TryGetValue<int>(out var v) || v != 1)
        {
            var shown = ver.ToJsonString();
            Reject(null, "file", $"Bildirge sürümü {(shown.Length <= 12 ? shown : "(çok uzun sayı)")} desteklenmiyor. Bu Söz yalnız 1'i bilir. Dosya yüklenmedi (silinmedi).");
            return;
        }
        if (file["entries"] is not JsonArray entries) { Reject(null, "file", "entries bir dizi olmalı."); return; }

        var sha = src.Sha256;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JsonObject e) { Reject($"#{i + 1}", "schema", "Kayıt bir JSON nesnesi olmalı."); continue; }
            string? id = null;
            if (e["id"] is JsonValue idNode && idNode.TryGetValue<string>(out var idText)) id = idText;
            // Etikete yalnız biçimi geçerli kimlik girer; öteki durumda sıra numarası (dosyadaki rastgele metin hata satırına taşınmaz).
            var label = IsValidId(id) ? id! : $"#{i + 1}";
            try { LoadEntry(e, i, id, label, src, sha, isFixed, res, firstSeen, fixedIds, approvals); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { Reject(label, "internal", $"Kayıt işlenirken beklenmeyen bir hata oldu ({ex.GetType().Name}). Kayıt yüklenmedi, öteki kayıtlar etkilenmedi."); }
        }
    }

    private static void LoadEntry(JsonObject e, int index, string? id, string label, RegistrySource src, string sha, bool isFixed, RegistryLoadResult res,
        Dictionary<string, string> firstSeen, HashSet<string> fixedIds, IRegistryApprovals? approvals)
    {
        void Reject(string? rid, string code, string msg) => res.Rejected.Add(new RegistryProblem(src.Name, rid, code, msg, isFixed));

        // Bilinmeyen tür: yüklenmez, uyarı listesine düşer.
        if (e["kind"] is JsonValue kv && kv.TryGetValue<string>(out var kindText) && !RegistryKinds.IsKnown(kindText))
        {
            res.Warnings.Add(new RegistryProblem(src.Name, label, "unknown-kind",
                $"bilinmeyen tür. Kayıt yüklenmedi (silinmedi). Bu Söz şu türleri bilir: {string.Join(", ", RegistryKinds.All)}.", isFixed));
            return;
        }

        var errs = RegistryLoader.Schema.Validate(e, "Entry").ToList();
        // Kişi dosyasındaki şema da çalıştırılmadan önce denetlenir (kötü anahtar sözcük, bozuk ya da pahalı regex, olmayan $ref).
        if (errs.Count == 0 && e["body"] is JsonObject lintBody)
        {
            var kindName = e["kind"]!.GetValue<string>();
            if (kindName == RegistryKinds.Setting) errs.AddRange(SchemaCheck.Lint(lintBody["schema"], "$.body.schema"));
            else if (kindName == RegistryKinds.CheckKind) errs.AddRange(SchemaCheck.Lint(lintBody["specSchema"], "$.body.specSchema"));
        }
        if (errs.Count > 0)
        {
            foreach (var m in errs.Take(MaxErrorsPerEntry)) Reject(label, "schema", m);
            if (errs.Count > MaxErrorsPerEntry) Reject(label, "schema", $"... ve {errs.Count - MaxErrorsPerEntry} sorun daha.");
            return;
        }

        var owner = e["owner"]!.GetValue<string>();
        var kind = e["kind"]!.GetValue<string>();
        var declared = (e["state"] as JsonValue)?.GetValue<string>();
        var bad = new List<(string code, string msg)>();
        if (id is null) return; // geçersiz kimlik yukarıda reddedildi
        var idPrefix = id[..id.IndexOf((char)58)];
        if (!kind.Equals(idPrefix, StringComparison.Ordinal)) bad.Add(("id-kind", $"kimliğin öneki (\"{idPrefix}\") kayıt türüyle (\"{kind}\") aynı olmalı."));
        if (isFixed && owner != RegistryOwners.Fixed) bad.Add(("owner", "sabit pakette yalnız owner=fixed kayıt olabilir."));
        if (!isFixed && owner == RegistryOwners.Fixed) bad.Add(("owner", "owner=fixed yalnız Söz'ün kendi paketinden gelir. Dosyadan yazılamaz."));
        if (!isFixed && fixedIds.Contains(id)) bad.Add(("fixed-id", $"\"{id}\" sabit (fixed) bir kaydın kimliği. Başka sahip bir fixed kimliğini kullanamaz."));
        if (owner == RegistryOwners.System && declared is not (null or RegistryStates.Suggested)) bad.Add(("system-state", "owner=system kayıt yalnız state=suggested olabilir. Sistem önerisi kendiliğinden etkin başlamaz."));
        if (declared == RegistryStates.Broken) bad.Add(("state", "state=broken dosyada yazılamaz. Bu durumu yalnız çekirdek koyar."));
        if (bad.Count == 0 && firstSeen.TryGetValue(id, out var other)) bad.Add(("duplicate", $"kimlik tekrarı: \"{id}\" zaten {other} dosyasında var."));
        // İki ayar aynı eski anahtarı (legacyKey; yoksa kimliği) paylaşamaz: biri ötekinin değerini okur ve yazardı. Sonra gelen reddedilir.
        if (bad.Count == 0 && kind == RegistryKinds.Setting && (e["body"] as JsonObject)?["legacyKey"] is JsonValue lkNode && lkNode.TryGetValue<string>(out var legacyKey))
        {
            var holder = res.Settings.FirstOrDefault(s => string.Equals(s.Setting!.LegacyKey ?? s.Id, legacyKey, StringComparison.Ordinal));
            if (holder is not null) bad.Add(("legacy-key", $"ayar anahtarı \"{SchemaCheck.Safe(legacyKey)}\" zaten \"{holder.Id}\" kaydında kullanılıyor. İki ayar aynı anahtarı paylaşamaz."));
        }
        if (bad.Count > 0) { foreach (var (code, msg) in bad) Reject(id, code, msg); return; }
        firstSeen[id] = src.Name;

        var entry = Build(e, id, kind, owner, declared, src, sha, isFixed, approvals);
        if (!RegistryKinds.IsDefined(kind))
        {
            var note = $"tanınan ama henüz kullanılmayan tür: \"{kind}\" (gövdesi şimdilik serbest nesne, hiçbir şey onu okumaz).";
            entry.Problems.Add(note);
            res.Warnings.Add(new RegistryProblem(src.Name, id, "unused-kind", note, isFixed));
        }
        res.Entries.Add(entry);
    }

    /// <summary>Bir nesnede aynı alan iki kez yazılmışsa yolunu (alan adıyla) verir; yoksa null.</summary>
    private static string? FindDuplicate(JsonElement el, string path)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in el.EnumerateObject())
                {
                    if (!seen.Add(p.Name)) return $"{path}.{SchemaCheck.Safe(p.Name)}";
                    if (FindDuplicate(p.Value, path + "." + SchemaCheck.Safe(p.Name)) is { } inner) return inner;
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    if (FindDuplicate(item, $"{path}[{i}]") is { } inner) return inner;
                    i++;
                }
                break;
        }
        return null;
    }

    private static RegistryEntry Build(JsonObject e, string id, string kind, string owner, string? declared, RegistrySource src, string sha, bool isFixed, IRegistryApprovals? approvals)
    {
        var perms = new List<RegistryPermission>();
        if (e["permissions"] is JsonArray pa)
            foreach (var p in pa.OfType<JsonObject>())
                perms.Add(new RegistryPermission(p["cap"]!.GetValue<string>(), (p["scope"] as JsonValue)?.GetValue<string>(), LocalizedText.From(p["why"])!));

        RegistryDeprecation? dep = null;
        if (e["deprecated"] is JsonObject d)
            dep = new RegistryDeprecation(d["since"]!.GetValue<string>(), (d["replacedBy"] as JsonValue)?.GetValue<string>(), LocalizedText.From(d["message"]));

        var body = (e["body"]?.DeepClone() as JsonObject) ?? new JsonObject();
        var state = declared ?? (owner == RegistryOwners.System ? RegistryStates.Suggested : RegistryStates.Enabled);
        var notes = new List<string>();
        // Onay özeti: kişi dosyasında dosyanın özeti; sabit kayıtta yalnız kaydın kendi özeti (aynı dosyadaki başka kayıt değişince onay düşmesin).
        var approvalSha = isFixed ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.ToJsonString()))).ToLowerInvariant() : sha;

        if (!isFixed && NeedsApproval(kind, body, perms))
        {
            if (approvals?.IsApproved(id, approvalSha) != true)
            {
                state = RegistryStates.Suggested;
                notes.Add("Onay bekliyor: çalıştırılabilir ya da korumayı etkileyen kayıt dosyadan ilk gelişte öneri olarak yüklenir. Kişi onaylayana kadar etkin değil.");
            }
        }
        else if (isFixed && state == RegistryStates.Suggested)
        {
            // Söz'ün kendi kaydı ama kapalı gelir: kişi onaylayınca etkinleşir, onaylamadıkça öneri kalır.
            if (approvals?.IsApproved(id, approvalSha) == true) state = RegistryStates.Enabled;
        }
        else if (isFixed && state == RegistryStates.Enabled && kind == RegistryKinds.Connector && approvals?.IsDisabled(id) == true)
        {
            // Sabit bağlayıcı varsayılan açık gelir ama kişi Ayarlar'dan kapattıysa kapalı kalır.
            state = RegistryStates.Disabled;
        }

        var entry = new RegistryEntry
        {
            Id = id,
            Kind = kind,
            Owner = owner,
            Title = LocalizedText.From(e["title"])!,
            Description = LocalizedText.From(e["description"]),
            Keywords = (e["keywords"] as JsonArray)?.Select(k => k!.GetValue<string>()).ToList() ?? new List<string>(),
            Surfaces = (e["surfaces"] as JsonArray)?.Select(k => k!.GetValue<string>()).ToList() ?? new List<string>(),
            Permissions = perms,
            Stability = (e["stability"] as JsonValue)?.GetValue<string>() ?? "ground",
            Since = (e["since"] as JsonValue)?.GetValue<string>(),
            Deprecated = dep,
            Contract = RegistryContract.From(e["contract"]),
            Body = body,
            DeclaredState = declared,
            State = state,
            Origin = src.Origin,
            Sha256 = sha,
            ApprovalSha = approvalSha,
        };
        entry.Problems.AddRange(notes);
        return entry;
    }

    /// <summary>
    /// Dosyadan gelen hangi kayıt onaysız etkin olamaz: bağlayıcı (komut başlatır), çalıştırılabilir ölçüt türü (komut koşar ya da modül çağırır),
    /// K4 ayar (güvenlik kapısı) ve exec izni isteyen her kayıt. Dosyayı ajan da yazabilir (aynı Windows hesabı), bu yüzden kapı burada.
    /// </summary>
    public static bool NeedsApproval(string kind, JsonObject body, IEnumerable<RegistryPermission> permissions)
    {
        if (permissions.Any(p => p.Cap == "exec")) return true;
        return kind switch
        {
            RegistryKinds.Connector => true,
            RegistryKinds.CheckKind => IsExecutableCheckKind(body),
            RegistryKinds.Setting => (body["class"] as JsonValue)?.GetValue<string>() == "K4",
            _ => false,
        };
    }

    /// <summary>Ölçüt türü komut koşuyor ya da modül çağırıyorsa çalıştırılabilirdir.</summary>
    public static bool IsExecutableCheckKind(JsonObject body)
    {
        var m = body["measure"] as JsonObject;
        var via = (m?["via"] as JsonValue)?.GetValue<string>();
        var name = (m?["name"] as JsonValue)?.GetValue<string>();
        return via == "module" || name == "command";
    }
}
