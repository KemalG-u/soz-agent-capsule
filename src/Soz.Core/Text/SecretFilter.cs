using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Text;

/// <summary>
/// Sır süzgeci. Parola/şifre/token/anahtar değeri, bilinen anahtar önekleri (sk-, ghp_, AKIA, Bearer ...) ve
/// uzun yüksek çeşitlilikli tek parça dizgeler "sır" sayılır.
/// Sır içeren cümle öneri adayı olmaz, pakete ve kanca çıktısına girmez; aramada yalnız sır parçası "•••" ile maskelenir.
/// Sezgiseldir: yanlış alarm güvenli yöndedir (cümle öneri olmaz), kaçırma ise yalnız ayıklama kapısında olur
/// ve ham kayıt zaten kullanıcının kendi veri klasöründe durur.
/// </summary>
public static class SecretFilter
{
    public const string Mask = "•••";

    /// <summary>Süzgeç değişince artar; eski adaylar bir kez yeniden taranır.</summary>
    public const int Version = 4;

    private static readonly RegexOptions O = RegexSpeed.Compiled | RegexOptions.CultureInvariant;

    // "parola: x", "password=x", "şifrem x", "api key is x", "\"token\": \"x\""
    private static readonly Regex Keyword = new(
        @"(?<![\p{L}\p{N}_])(?<k>parola|[sş][iİıI]fre|password|passwd|passphrase|pwd|token|api[ _\-]?key|apikey|client[ _\-]?secret|secret|anahtar)(?<suf>\p{L}{0,8})[""']?[ \t]*(?<sep>:=|=>|->|[:=]|\bis\b|\bwas\b|\bşudur\b|\bşu\b|\bolarak\b|\bbudur\b)?[ \t]*[""'`]?(?<v>[^\s""'`,;]{4,})",
        O | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> NotValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "yok", "boş", "bos", "none", "null", "empty", "true", "false", "nedir", "gizli", "değil", "degil", "olsun", "olmalı", "lazım",
    };

    private static readonly (Regex Re, Func<Match, bool>? Ok)[] Prefixes =
    {
        (new Regex(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9][A-Za-z0-9_\-]{5,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{10,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{10,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])(?:AKIA|ASIA)[0-9A-Z]{8,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])AIza[0-9A-Za-z_\-]{20,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])xox[abprs]-[0-9A-Za-z\-]{8,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9_\-])eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}(?:\.[A-Za-z0-9_\-]*)?", O), null),
        // Özel anahtar bloğu: başlıktan bitişe kadar hepsi (bitiş yoksa 4000 karaktere kadar). Yalnız başlığı maskelemek gövdeyi açıkta bırakırdı.
        (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY(?: BLOCK)?-----(?:[\s\S]{0,4000}?-----END [A-Z ]*PRIVATE KEY(?: BLOCK)?-----|[\s\S]{0,4000})", O), null),
        (new Regex(@"(?<![A-Za-z0-9])[sr]k_(?:live|test)_[A-Za-z0-9]{10,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])npm_[A-Za-z0-9]{30,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_\-]{16,}", O), null),
        (new Regex(@"(?<![A-Za-z0-9])hf_[A-Za-z]{30,}", O), null),
        // "Bearer <değer>": düz sözcük ("Bearer authentication") sayılmaz; rakam ya da işaret taşımalı.
        (new Regex(@"(?<![A-Za-z0-9])Bearer[ \t]+(?<v>[A-Za-z0-9._~+/=\-]{8,})", O | RegexOptions.IgnoreCase),
            m => m.Groups["v"].Value.Any(ch => char.IsDigit(ch) || "._~+/=-".Contains(ch))),
    };


    // ---- 2. sürüm (denetim B-4) ----
    // Ortam değişkeni / yapılandırma anahtarı: DB_PASSWORD=x, PGPASSWORD=x, MYSQL_PWD=x, GITHUB_TOKEN: x, "db_password": "çok sözcüklü değer".
    // Kimlik anahtar kelimeyle BİTER (TOKEN_LIMIT=8000 değildir); yalın KEY yalnız büyük harfle ("monkey: 3", "sort key: ad" değildir).
    private static readonly Regex EnvKey = new(
        @"(?<![A-Za-z0-9_\-])(?<id>[A-Za-z0-9_\-]*(?:PASSWORD|PASSWD|PWD|SECRET|TOKEN|APIKEY|API_KEY|[_\-]KEY|(?-i:(?<![A-Za-z0-9])KEY)))[""']?[ \t]*[=:][ \t]*(?:""(?<qv>[^""\r\n]{3,})""|'(?<qv>[^'\r\n]{3,})'|(?<v>[^\s""'`,;]{4,}))",
        O | RegexOptions.IgnoreCase);

    // Adres içinde "kullanıcı:PAROLA@sunucu" biçimi: yalnız parola maskelenir.
    private static readonly Regex UrlCreds = new(
        @"(?<![A-Za-z0-9+.\-])[A-Za-z][A-Za-z0-9+.\-]{1,15}://[^\s/:@""'<>]+:(?<p>[^\s/@""'<>]+)@", O);

    // Authorization: Basic xxx / Bearer xxx / Token xxx / Authorization: xxx
    private static readonly Regex AuthHeader = new(
        @"(?<![A-Za-z0-9])Authorization[""']?[ \t]*[:=][ \t]*(?:(?:Basic|Bearer|Token|Negotiate|NTLM)[ \t]+)?(?!(?:Basic|Bearer|Token|Negotiate|NTLM|Digest)\b)(?<v>[^\s""'`<>,;]{6,})", O | RegexOptions.IgnoreCase);

    // mysql -u root -pParola / -p'Çok sözcüklü parola' (yalın -p parola sorar, maskelenmez)
    private static readonly Regex MysqlPwd = new(
        @"(?<![A-Za-z0-9_])(?:mysql|mysqldump|mysqladmin|mysqlimport|mysqlshow|mariadb)(?:\.exe)?(?![A-Za-z0-9_])[^\r\n|;&]*?[ \t](?-i:-p)(?:(?<q>[""'])(?<qv>[^""'\r\n]+)\k<q>|(?<v>[^\s""'\-][^\s""']{2,}))",
        O | RegexOptions.IgnoreCase);

    // ---- 3. sürüm (denetim 2, Ö-6) ----
    // curl -u kullanici:parola, --user x:y, --password x, --password=x, --passwd=x
    private static readonly Regex UserPass = new(
        @"(?<![A-Za-z0-9_\-])(?:-u|--user|--proxy-user)[ \t=]+(?:(?<q>[""'])(?<qv>[^""'\r\n]*:[^""'\r\n]+)\k<q>|[^\s:""']+:(?<v>[^\s""']+))", O);
    private static readonly Regex PassFlag = new(
        @"(?<![A-Za-z0-9_])--(?:password|passwd|pass|passphrase|parola|sifre)(?:[ \t]+|=)(?:(?<q>[""'])(?<qv>[^""'\r\n]+)\k<q>|(?<v>[^\s""'\-][^\s""']*))", O | RegexOptions.IgnoreCase);
    // Çok satırlı YAML: "password:" satırı, değer bir sonraki girintili satırda.
    private static readonly Regex YamlNext = new(
        @"(?<![\p{L}\p{N}_])(?:parola|[sş][iİıI]fre|password|passwd|secret|token|api[_\-]?key)[""']?[ \t]*:[ \t]*\r?\n[ \t]+[""']?(?<v>[^\s""'`,;#]{4,})", O | RegexOptions.IgnoreCase);
    private static readonly Regex PureHex = new(@"^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{40}|[0-9a-fA-F]{56}|[0-9a-fA-F]{64}|[0-9a-fA-F]{96}|[0-9a-fA-F]{128})$", O);

    // Adres sorgusundaki anahtar adı bağlamı: ?key=x, &token=x, ?api_key=x, &access_token=x, &sig=x
    private static readonly Regex QuerySecret = new(
        @"(?<=[?&;])(?:api[_\-]?key|apikey|key|access[_\-]?token|auth[_\-]?token|token|secret|client[_\-]?secret|password|passwd|pwd|sig|signature|auth|x-api-key)=(?<v>[^\s&#""'`<>]{4,})", O | RegexOptions.IgnoreCase);

    private static readonly Regex Webhook = new(
        @"https?://(?:hooks\.slack\.com/services/[A-Za-z0-9/_\-]{10,}|(?:discord|discordapp)\.com/api/webhooks/\d+/[A-Za-z0-9_\-]{20,})", O | RegexOptions.IgnoreCase);

    private static void AddGeneric(string text, List<(int, int)> spans)
    {
        foreach (Match m in EnvKey.Matches(text))
        {
            var q = m.Groups["qv"];
            if (q.Success)
            {
                var qv = q.Value.Trim();
                if (qv.Length >= 3 && !NotValues.Contains(qv) && !IsReference(qv)) spans.Add((q.Index, q.Length));
                continue;
            }
            var v = m.Groups["v"];
            var val = v.Value.TrimEnd('.', '!', '?', ')', ']', '}');
            if (val.Length < 4 || NotValues.Contains(val) || IsReference(val)) continue;
            // "TOKEN: 8000" gibi sayaç değerleri sır değildir; gerçek token çok daha uzundur.
            if (m.Groups["id"].Value.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) && val.All(char.IsDigit) && val.Length < 12) continue;
            spans.Add((v.Index, val.Length));
        }
        foreach (Match m in UrlCreds.Matches(text)) spans.Add((m.Groups["p"].Index, m.Groups["p"].Length));
        foreach (Match m in AuthHeader.Matches(text)) spans.Add((m.Groups["v"].Index, m.Groups["v"].Length));
        foreach (Match m in MysqlPwd.Matches(text))
        {
            var g = m.Groups["qv"].Success ? m.Groups["qv"] : m.Groups["v"];
            spans.Add((g.Index, g.Length));
        }
        foreach (Match m in Webhook.Matches(text)) spans.Add((m.Index, m.Length));
        foreach (Match m in QuerySecret.Matches(text))
            if (!IsReference(m.Groups["v"].Value)) spans.Add((m.Groups["v"].Index, m.Groups["v"].Length));
        foreach (var re in new[] { UserPass, PassFlag })
            foreach (Match m in re.Matches(text))
            {
                var g = m.Groups["qv"].Success ? m.Groups["qv"] : m.Groups["v"];
                var val = g.Value;
                // -u kullanıcı:parola içinde yalnız parola kısmı maskelenir.
                if (re == UserPass && m.Groups["qv"].Success) { var i = val.IndexOf(':'); if (i >= 0) { spans.Add((g.Index + i + 1, val.Length - i - 1)); continue; } }
                if (val.Length >= 1 && !IsReference(val)) spans.Add((g.Index, g.Length));
            }
        foreach (Match m in YamlNext.Matches(text))
        {
            var v = m.Groups["v"];
            if (!NotValues.Contains(v.Value) && !IsReference(v.Value)) spans.Add((v.Index, v.Length));
        }
    }

    /// <summary>Değişken başvurusu ya da yer tutucu ($X, ${X}, %X%, &lt;değer&gt;, {{x}}): gerçek değer değil.</summary>
    private static bool IsReference(string v) => v.Length > 0 && (v[0] is '$' or '<' or '%' or '{' or '*');

    private static readonly Regex LongToken = new(@"[^\s]{24,}", O);
    private static readonly Regex Uuid = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", O);
    private const string EdgePunct = "\"'`()[]{}<>,.;:!?";

    /// <summary>Metindeki sır aralıkları (çakışanlar birleştirilmiş, sıralı).</summary>
    public static IReadOnlyList<(int Start, int Length)> Find(string? text)
    {
        var spans = new List<(int, int)>();
        if (string.IsNullOrEmpty(text)) return spans;

        foreach (Match m in Keyword.Matches(text))
        {
            var v = m.Groups["v"];
            if (KeywordValueIsSecret(m)) spans.Add((v.Index, v.Length));
        }
        AddGeneric(text, spans);
        foreach (var (re, ok) in Prefixes)
            foreach (Match m in re.Matches(text))
                if (ok is null || ok(m)) spans.Add((m.Index, m.Length));
        foreach (Match m in LongToken.Matches(text))
        {
            var raw = m.Value;
            var lead = 0;
            while (lead < raw.Length && EdgePunct.Contains(raw[lead])) lead++;
            var end = raw.Length;
            while (end > lead && EdgePunct.Contains(raw[end - 1])) end--;
            var tok = raw[lead..end];
            if (tok.Length < MinRandomLength) continue;
            // ad=değer biçiminde yalnız değer sınanır: sha40=<özet>, uuid=<kimlik> gibi meşru atamalar sır sayılmaz.
            var eq = tok.IndexOf('=');
            if (eq > 0 && eq < 32 && eq < tok.Length - 1 && tok[..eq].All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
            {
                var val = tok[(eq + 1)..];
                if (val.Length < MinRandomLength || !LooksRandom(val)) continue;
            }
            else if (!LooksRandom(tok)) continue;
            spans.Add((m.Index + lead, tok.Length));
        }

        spans.Sort();
        var merged = new List<(int Start, int Length)>();
        foreach (var (s, l) in spans)
        {
            if (merged.Count > 0 && s <= merged[^1].Start + merged[^1].Length)
            {
                var (ps, pl) = merged[^1];
                merged[^1] = (ps, Math.Max(ps + pl, s + l) - ps);
            }
            else merged.Add((s, l));
        }
        return merged;
    }

    public static bool ContainsSecret(string? text) => Find(text).Count > 0;

    /// <summary>İş yazılırken gösterilen tek satırlık uyarı (B4). Metinde anahtar benzeri bir değer yoksa null.</summary>
    public const string JobWarning = "Bu metinde anahtar gibi bir şey var. Yardımcı bunu görecek ve kayıtlarına geçebilir.";
    public static string? WarningFor(string? text) => ContainsSecret(text) ? JobWarning : null;

    /// <summary>Sır parçalarını "•••" ile değiştirir; gerisine dokunmaz.</summary>
    public static string MaskSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var spans = Find(text);
        if (spans.Count == 0) return text;
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var (s, l) in spans)
        {
            sb.Append(text, pos, s - pos).Append(Mask);
            pos = s + l;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    private static bool KeywordValueIsSecret(Match m)
    {
        var v = m.Groups["v"].Value.TrimEnd('.', '!', '?', ')', ']', '}');
        if (v.Length < 4 || NotValues.Contains(v)) return false;
        var sep = m.Groups["sep"].Value;
        var explicitSep = sep is ":" or "=" or ":=" or "=>" or "->";
        var hasDigit = v.Any(char.IsDigit);
        var hasLetter = v.Any(char.IsLetter);
        var hasSymbol = v.Any(ch => !char.IsLetterOrDigit(ch));
        if (!explicitSep && v.StartsWith('_')) return false; // TOKEN_LIMIT=8000: tanımlayıcının devamı, değer değil
        if (explicitSep)
        {
            // "token: 8000" gibi sayaç değerleri sır değildir; gerçek token çok daha uzundur.
            var kw = m.Groups["k"].Value;
            if (kw.Equals("token", StringComparison.OrdinalIgnoreCase) && v.All(char.IsDigit)) return v.Length >= 12;
            return true;
        }
        // Ayraçsız ("şifrem hunter2", "password is Abc12345"): değer rakam ve harf ya da işaret taşımalı.
        if (v.Length < 6) return false;
        return (hasDigit && hasLetter) || (hasSymbol && (hasDigit || hasLetter) && v.Length >= 8);
    }

    private static readonly Regex IsoTimestamp = new(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?)?$", O);
    private static readonly Regex DriveLetterPath =new(@"^[A-Za-z]:[\\/]", O);

    private static bool LooksRandom(string tok)
    {
        if (DriveLetterPath.IsMatch(tok)) return false; // C:/klasor/dosya.exe: yol, anahtar değil
        if (tok.Contains('\\') || tok.Contains("://") || tok.StartsWith('/') || tok.StartsWith('~') || tok.StartsWith("./")) return false;
        if (Uuid.IsMatch(tok)) return false;
        if (IsoTimestamp.IsMatch(tok)) return false; // 2026-11-12T04:10:27.123Z: zaman damgası, anahtar değil
        if (PureHex.IsMatch(tok)) return false; // git sha / sha256: bağlam yoksa sır sayılmaz
        if (LooksLikeHumanName(tok)) return false; // kebab-case ad, sürüm dizgesi, CamelCase ad, hata kodu: insan okuyabilir
        if (HasLongRun(tok)) return false; // "AAAA" gibi tekrar: kodlanmış veri (ör. PNG başlığı), anahtar değil
        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (var ch in tok)
        {
            if (char.IsLower(ch)) lower = true;
            else if (char.IsUpper(ch)) upper = true;
            else if (char.IsDigit(ch)) digit = true;
            else symbol = true;
        }
        var kinds = (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);
        if (kinds < 2) return false;
        var h = Entropy(tok);
        // Bağlamsız dizge yalnız yeterince uzun ve karışıksa anahtar sayılır:
        // üç tür karakter ve yüksek çeşitlilik (28+), ya da yalnız harf+rakam ise daha uzun (32+) ve daha yüksek çeşitlilik.
        if (kinds >= 3) return h >= 4.0;
        if (tok.Length < 32) return false;
        return digit ? h >= 3.9 : h >= 4.3; // harf+rakam ya da büyük+küçük harf karışımı
    }

    /// <summary>Anahtar uzunluğunun alt sınırı. Bağlamsız dizge bundan kısaysa hiç sır sayılmaz.</summary>
    private const int MinRandomLength = 28;

    private static bool HasLongRun(string tok)
    {
        var run = 1;
        for (var i = 1; i < tok.Length; i++)
        {
            run = tok[i] == tok[i - 1] ? run + 1 : 1;
            if (run >= 4) return true;
        }
        return false;
    }

    /// <summary>
    /// İnsanın okuyup yazdığı ad mı: harf dizilerinin çoğu gerçek kelime (4+ harf, CamelCase parçalarına bölünmüş), rakam grupları (2+)
    /// ve ayraçlar toplam uzunluğun en az yüzde 65'ini oluşturuyorsa. Rastgele anahtarda bu oran çok düşük kalır.
    /// </summary>
    private static bool LooksLikeHumanName(string tok)
    {
        var covered = 0;
        var i = 0;
        while (i < tok.Length)
        {
            var ch = tok[i];
            if (char.IsLetter(ch))
            {
                // Sürüm eki: V2, v1, V10 gibi kalıplar meşru proje adlarında sık geçer.
                if (ch is 'V' or 'v' && i + 1 < tok.Length && char.IsDigit(tok[i + 1])
                    && (i == 0 || !char.IsLower(tok[i - 1])))
                {
                    var d = i + 1;
                    while (d < tok.Length && char.IsDigit(tok[d])) d++;
                    if (d - (i + 1) <= 3 && (d == tok.Length || !char.IsLower(tok[d])))
                    {
                        covered += d - i;
                        i = d;
                        continue;
                    }
                }

                // CamelCase parçası: büyük harf + küçük harfler, ya da bütünüyle büyük harf (kısaltma).
                var j = i + 1;
                var isTitleWord = false;
                if (char.IsUpper(ch))
                {
                    if (j < tok.Length && char.IsLower(tok[j]))
                    {
                        isTitleWord = true;
                        while (j < tok.Length && char.IsLower(tok[j])) j++;
                    }
                    else { while (j < tok.Length && char.IsUpper(tok[j]) && !(j + 1 < tok.Length && char.IsLower(tok[j + 1]))) j++; }
                }
                else { while (j < tok.Length && char.IsLower(tok[j])) j++; }
                var len = j - i;
                var separated = (i == 0 || tok[i - 1] is '-' or '_' or '.') && (j == tok.Length || tok[j] is '-' or '_' or '.');
                if ((len >= 4 || (len == 3 && (isTitleWord || separated))) && HasVowelBalance(tok, i, len))
                    covered += len;
                i = j;
            }
            else if (char.IsDigit(ch))
            {
                var j = i + 1;
                while (j < tok.Length && char.IsDigit(tok[j])) j++;
                if (j - i >= 2) covered += j - i;
                i = j;
            }
            else { covered++; i++; } // ayraç
        }
        return covered >= tok.Length * 0.65;
    }

    /// <summary>Gerçek kelimede sesli harf bulunur ve üst üste çok ünsüz gelmez (rastgele harf dizisinde sık bozulur).</summary>
    private static bool HasVowelBalance(string tok, int start, int len)
    {
        const string vowels = "aeiouıöüAEIOUİÖÜâîû";
        var v = 0; var cons = 0;
        for (var k = start; k < start + len; k++)
        {
            if (vowels.Contains(tok[k])) { v++; cons = 0; }
            else if (++cons >= 5) return false;
        }
        return v >= 1 && v * 10 >= len * 2;
    }

    private static double Entropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var ch in s) counts[ch] = counts.GetValueOrDefault(ch) + 1;
        double h = 0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / s.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    /// <summary>
    /// Süzgeç öncesi içe alınmış adayları bir kez temizler: sır içeren adaylar silinir, onlardan doğan bekleyen öneriler geri çekilir,
    /// etkilenen kümelerin sayıları yeniden hesaplanır. Süzgeç sürümü ayarda tutulur; aynı sürüm yeniden taranmaz.
    /// </summary>
    public static int PurgeExisting(SqliteConnection c)
    {
        var done = SozSettings.GetInternal(c, "_secretFilterVersion");
        if (int.TryParse(done, out var v) && v >= Version) return 0;

        var bad = c.Query("SELECT id, text, cluster_id FROM candidates", r => (id: r.GetInt64(0), text: r.Str(1) ?? "", cl: r.LongN(2)))
            .Where(x => ContainsSecret(x.text)).ToList();
        using var tx = c.BeginTransaction();
        foreach (var b in bad)
        {
            c.Exec("DELETE FROM candidates WHERE id=@id", tx, ("@id", b.id));
            c.Exec("UPDATE decisions SET origin_candidate_id=NULL WHERE origin_candidate_id=@id AND status<>'proposed'", tx, ("@id", b.id));
            c.Exec("DELETE FROM decisions WHERE origin_candidate_id=@id AND status='proposed'", tx, ("@id", b.id));
        }
        foreach (var cl in bad.Where(x => x.cl is not null).Select(x => x.cl!.Value).Distinct())
        {
            var left = c.ScalarLong("SELECT COUNT(*) FROM candidates WHERE cluster_id=@c", tx, ("@c", cl));
            if (left == 0)
            {
                c.Exec("DELETE FROM decisions WHERE cluster_id=@c AND status='proposed'", tx, ("@c", cl));
                c.Exec("UPDATE decisions SET cluster_id=NULL WHERE cluster_id=@c", tx, ("@c", cl));
                c.Exec("DELETE FROM clusters WHERE id=@c", tx, ("@c", cl));
            }
            else
            {
                c.Exec("""
                    UPDATE clusters SET count=@n,
                      session_count=(SELECT COUNT(DISTINCT u.session_id) FROM candidates k JOIN utterances u ON u.id=k.utterance_id WHERE k.cluster_id=@c)
                    WHERE id=@c
                    """, tx, ("@n", left), ("@c", cl));
            }
        }
        // Metni sır içeren bekleyen öneriler (kümesi dışında kalmışsa) de çekilir.
        foreach (var d in c.Query("SELECT id, text FROM decisions WHERE status='proposed'", r => (id: r.GetInt64(0), text: r.Str(1) ?? ""), tx).Where(x => ContainsSecret(x.text)))
            c.Exec("DELETE FROM decisions WHERE id=@id", tx, ("@id", d.id));
        tx.Commit();
        SozSettings.SetInternal(c, "_secretFilterVersion", Version.ToString());
        return bad.Count;
    }
}
