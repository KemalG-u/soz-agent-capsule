using System.Text.Json;
using System.Text.RegularExpressions;
using Soz.Core.Data;
using Soz.Core.Readers;
using Soz.Core.Text;

namespace Soz.Core.Sessions.Ozet;

/// <summary>Bir deneme komutunun kayıttaki izi. Sonuc: gecti | kaldi | bilinmiyor. Yalnız kuralın adı tutulur, komut tutulmaz.</summary>
public sealed record DenemeIzi(string KomutTuru, string Sonuc, int? CikisKodu, string? An, long Sira);

/// <summary>Kayıtta sonucu henüz görülmemiş araç çağrısı. Tur: komut | dosya | soru | plan | onay | diger.</summary>
public sealed record AcikCagri(string Tur, long Sira, string? An, string? DosyaAdi);

/// <summary>
/// Bir kayıt dosyasından çıkan OLGULAR (sohbet metni yok): değişen dosyalar, komut sayısı, son deneme, "bitti" iddiasının ve son insan mesajının sırası,
/// açık çağrılar, tur sonu. Dosya büyüdükçe kaldığı yerden okunur (<see cref="Oku"/>); kaydın sonu kısalırsa baştan.
/// Okuma mevcut okuyucuların akış tarayıcısıyla (<see cref="JsonlScanner"/>) yapılır; biçim kuralları <see cref="ClaudeCodeReader"/> ve <see cref="CodexReader"/> ile aynıdır.
/// Bu sınıf yalnız bellekte yaşar: veritabanına, günlüğe ya da dosyaya hiçbir şey yazmaz.
/// </summary>
public sealed class OzetBirikimi
{
    public string Bicim { get; }
    public OzetBirikimi(string bicim) => Bicim = bicim;

    // ---- okuma durumu ----
    internal long Offset, LineNo, Length, MtimeTicks;
    internal readonly object Kilit = new();

    // ---- olgular ----
    public int Satir { get; private set; }
    public int Taninan { get; private set; }
    public int BozukSatir { get; private set; }
    /// <summary>Codex: ilk satır session_meta değildi (tanınmayan biçim).</summary>
    public bool TaninmayanBicim { get; private set; }
    private bool _codexIlkGoruldu, _codexOgeGoruldu;

    private long _sira;
    private readonly Dictionary<string, (string ad, long sira)> _degisen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AcikCagri cagri, string? tamYol, string? deneme, DenemeIzi? onceki, bool arkaPlan)> _acik = new(StringComparer.Ordinal);
    public int KomutSayisi { get; private set; }
    public DenemeIzi? SonDeneme { get; private set; }
    public long SonDegisiklikSira { get; private set; }
    public long SonIddiaSira { get; private set; }
    public string? SonIddiaAn { get; private set; }
    public long SonInsanSira { get; private set; }
    public long SonMetinSira { get; private set; }
    public bool SonMetinSoruyor { get; private set; }
    public long SonAracSira { get; private set; }
    public bool TurBitti { get; private set; }
    public string? SonOlayAn { get; private set; }
    /// <summary>Codex onay isteği (izin | soru) ve sırası; sonrasında başka etkinlik görülürse kalkar.</summary>
    public AcikCagri? OnayIstegi { get; private set; }

    public int DegisenDosyaSayisi => _degisen.Count;
    /// <summary>En son değişenden başlayarak dosya adları (tam yol değil).</summary>
    public List<string> DosyaAdlari(int enCok) => _degisen.Values.OrderByDescending(v => v.sira).Select(v => v.ad).Take(enCok).ToList();
    public AcikCagri? SonAcikCagri => _acik.Count == 0 ? null : _acik.Values.Select(v => v.cagri).MaxBy(c => c.Sira);

    // ============ okuma ============

    /// <summary>Dosyayı kaldığı yerden okur. Dosya kısaldıysa ya da yeniden yazıldıysa birikim sıfırdan kurulmalıdır (çağıran yeni nesne açar).</summary>
    internal void Oku(string yol)
    {
        LineHandler h = Bicim == "codex" ? Codex : Claude;
        Offset = JsonlScanner.Scan(yol, Offset, LineNo, h, out var son, out var len, acceptUnterminatedTail: true);
        LineNo = son;
        Length = len;
    }

    private long Ileri(string? an)
    {
        _sira++;
        if (an is not null && (SonOlayAn is null || string.CompareOrdinal(an, SonOlayAn) > 0)) SonOlayAn = an;
        return _sira;
    }

    // ---------------- Claude Code ----------------
    private static readonly string[] InsanDegil = { "Another Claude", "This session is being continued", "Caveat:", "[SYSTEM NOTIFICATION" };

    private void Claude(ReadOnlySpan<byte> line, long off, long lno)
    {
        Satir++;
        var type = JsonlScanner.RootType(line, out var bad);
        if (bad) { BozukSatir++; return; }
        switch (type)
        {
            case "assistant":
            {
                Taninan++;
                if (!JsonlScanner.Contains(line, "\"tool_use\""u8) && !JsonlScanner.Contains(line, "\"text\""u8) && !JsonlScanner.Contains(line, "end_turn"u8)) return;
                using var doc = JsonlScanner.TryParse(line);
                if (doc is null) { BozukSatir++; return; }
                var r = doc.RootElement;
                if (r.Bool("isSidechain")) return;
                var an = Time.NormalizeIso(r.StrN("timestamp"));
                var msg = r.Obj("message");
                var content = msg.Obj("content");
                var aracVar = false;
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in content.EnumerateArray())
                    {
                        switch (b.Str("type"))
                        {
                            case "text": Metin(b.Str("text"), an); break;
                            case "tool_use": aracVar = true; ClaudeArac(b, an); break;
                        }
                    }
                }
                else if (content.ValueKind == JsonValueKind.String) Metin(content.GetString(), an);
                if (aracVar) TurBitti = false;
                else if (msg.StrN("stop_reason") is "end_turn" or "stop_sequence") { TurBitti = true; Ileri(an); }
                return;
            }
            case "user":
            {
                Taninan++;
                using var doc = JsonlScanner.TryParse(line);
                if (doc is null) { BozukSatir++; return; }
                var r = doc.RootElement;
                if (r.Bool("isSidechain")) return;
                var an = Time.NormalizeIso(r.StrN("timestamp"));
                var content = r.Obj("message").Obj("content");
                var sonucVar = false;
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var tur = r.Obj("toolUseResult");
                    foreach (var b in content.EnumerateArray())
                        if (b.Str("type") == "tool_result") { sonucVar = true; ClaudeSonuc(b, tur, an); }
                }
                if (sonucVar) { TurBitti = false; return; }
                ClaudeInsan(r, an);
                return;
            }
            case "attachment":
            {
                Taninan++;
                if (!JsonlScanner.Contains(line, "\"queued_command\""u8)) return;
                using var doc = JsonlScanner.TryParse(line);
                if (doc is null) { BozukSatir++; return; }
                var a = doc.RootElement.Obj("attachment");
                if (a.Str("type") == "queued_command" && a.Obj("origin").StrN("kind") == "human" && a.StrN("commandMode") == "prompt")
                    Insan(Time.NormalizeIso(a.StrN("timestamp")) ?? Time.NormalizeIso(doc.RootElement.StrN("timestamp")));
                return;
            }
            case "system":
            {
                Taninan++;
                if (!JsonlScanner.Contains(line, "\"turn_duration\""u8)) return;
                using var doc = JsonlScanner.TryParse(line);
                if (doc?.RootElement.StrN("subtype") == "turn_duration") { TurBitti = true; Ileri(Time.NormalizeIso(doc.RootElement.StrN("timestamp"))); }
                return;
            }
            default:
                if (type is "summary" or "queue-operation" or "last-prompt" or "file-history-snapshot" or "mode" or "permission-mode" or "custom-title" or "ai-title") Taninan++;
                return;
        }
    }

    private void ClaudeInsan(JsonElement r, string? an)
    {
        if (r.Bool("isMeta") || r.Bool("isCompactSummary")) return;
        var originKind = r.Obj("origin").StrN("kind");
        if (originKind is not null && originKind != "human") return;
        if (originKind is null && (r.StrN("entrypoint") == "sdk-cli" || r.StrN("promptSource") is "sdk" or "system" || r.Has("scheduledTaskId"))) return;
        var text = JsonlScanner.ContentText(r.Obj("message").Obj("content"));
        var head = text.TrimStart();
        if (head.StartsWith("[Request interrupted", StringComparison.Ordinal)) { TurBitti = true; _acik.Clear(); Ileri(an); return; }
        foreach (var p in InsanDegil) if (head.StartsWith(p, StringComparison.Ordinal)) return;
        var clean = TextNormalizer.CleanUserText(text);
        if (clean.Length == 0 || TextNormalizer.IsSlashCommand(clean)) return;
        Insan(an);
    }

    private void Insan(string? an)
    {
        SonInsanSira = Ileri(an);
        TurBitti = false;
        _acik.Clear();       // yeni istem: yarım kalan çağrılar terk edildi
        OnayIstegi = null;
    }

    private void Metin(string? text, string? an)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var s = Ileri(an);
        SonMetinSira = s;
        SonMetinSoruyor = SessionStateResolver.Flags(text).asks;
        if (BittiIddiasi.Var(text)) { SonIddiaSira = s; SonIddiaAn = an; }
    }

    private static readonly HashSet<string> KomutAraclari = new(StringComparer.Ordinal) { "Bash", "PowerShell" };
    private static readonly HashSet<string> DosyaAraclari = new(StringComparer.Ordinal) { "Edit", "Write", "MultiEdit", "NotebookEdit" };

    private void ClaudeArac(JsonElement b, string? an)
    {
        var id = b.StrN("id");
        var ad = b.Str("name");
        var input = b.Obj("input");
        var s = Ileri(an);
        SonAracSira = s;
        if (id is null) return;
        if (KomutAraclari.Contains(ad))
        {
            var komut = input.StrN("command");
            var deneme = DenemeKurallari.Tani(komut);
            var arka = input.Bool("run_in_background");
            DenemeIzi? onceki = SonDeneme;
            if (deneme is not null) SonDeneme = new DenemeIzi(deneme, DenemeSonuc.Bilinmiyor, null, an, s);
            _acik[id] = (new AcikCagri("komut", s, an, null), null, deneme, onceki, arka);
        }
        else if (DosyaAraclari.Contains(ad))
        {
            var yol = input.StrN("file_path") ?? input.StrN("notebook_path");
            _acik[id] = (new AcikCagri("dosya", s, an, DosyaAdi(yol)), yol, null, null, false);
        }
        else if (ad == "AskUserQuestion") _acik[id] = (new AcikCagri("soru", s, an, null), null, null, null, false);
        else if (ad == "ExitPlanMode") _acik[id] = (new AcikCagri("plan", s, an, null), null, null, null, false);
        else _acik[id] = (new AcikCagri("diger", s, an, null), null, null, null, false);
    }

    private static readonly Regex CikisKoduRe = new(@"^\s*Exit code:?\s*(-?\d+)", RegexOptions.CultureInvariant);

    private void ClaudeSonuc(JsonElement b, JsonElement toolUseResult, string? an)
    {
        Ileri(an);
        var id = b.StrN("tool_use_id");
        if (id is null || !_acik.Remove(id, out var a)) return;
        var hata = b.Bool("is_error");
        // Sonuç metninden yalnız iki sabit işaret okunur (ret ve çıkış kodu); metnin kendisi saklanmaz.
        var ilk = SonucBasi(b.Obj("content"));
        var reddedildi = hata && (ilk.StartsWith("The user doesn't want to proceed", StringComparison.Ordinal) || ilk.StartsWith("User rejected", StringComparison.Ordinal)
                                   || ilk.Contains("Permission to use", StringComparison.Ordinal) && ilk.Contains("denied", StringComparison.Ordinal));
        switch (a.cagri.Tur)
        {
            case "komut":
                if (reddedildi) { if (a.deneme is not null && SonDeneme?.Sira == a.cagri.Sira) SonDeneme = a.onceki; return; }
                KomutSayisi++;
                if (a.deneme is null || SonDeneme?.Sira != a.cagri.Sira) return;
                var kesildi = toolUseResult.ValueKind == JsonValueKind.Object && toolUseResult.Bool("interrupted");
                var arkaPlan = a.arkaPlan || toolUseResult.ValueKind == JsonValueKind.Object && toolUseResult.Has("backgroundTaskId") && !hata;
                if (kesildi || arkaPlan) return; // sonuç kayıtta yok: bilinmiyor kalır
                int? kod = null;
                var m = CikisKoduRe.Match(ilk);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var k)) kod = k;
                SonDeneme = SonDeneme with { Sonuc = hata ? DenemeSonuc.Kaldi : DenemeSonuc.Gecti, CikisKodu = hata ? kod : (kod ?? 0) };
                return;
            case "dosya":
                if (hata || a.tamYol is null) return;
                Degisti(a.tamYol, a.cagri.Sira);
                return;
        }
    }

    private static string SonucBasi(JsonElement content)
    {
        string s = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => content.EnumerateArray().Where(x => x.Str("type") == "text").Select(x => x.Str("text")).FirstOrDefault() ?? "",
            _ => "",
        };
        return s.Length > 200 ? s[..200] : s;
    }

    private void Degisti(string tamYol, long sira)
    {
        var ad = DosyaAdi(tamYol);
        if (ad is null) return;
        _degisen[tamYol.Replace('\\', '/')] = (ad, sira);
        if (sira > SonDegisiklikSira) SonDegisiklikSira = sira;
    }

    /// <summary>Tam yoldan yalnız dosya adı (sır süzgecinden geçmiş). Boşsa null.</summary>
    public static string? DosyaAdi(string? yol)
    {
        if (string.IsNullOrWhiteSpace(yol)) return null;
        var p = yol.Trim().TrimEnd('\\', '/');
        var i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        var ad = i >= 0 ? p[(i + 1)..] : p;
        if (ad.Length == 0) return null;
        if (ad.Length > 80) ad = ad[..80];
        return SecretFilter.MaskSecrets(ad);
    }

    // ---------------- Codex ----------------
    private void Codex(ReadOnlySpan<byte> line, long off, long lno)
    {
        if (TaninmayanBicim) return;
        Satir++;
        if (!_codexIlkGoruldu)
        {
            _codexIlkGoruldu = true;
            using var d0 = JsonlScanner.TryParse(line);
            if (d0 is null || d0.RootElement.ValueKind != JsonValueKind.Object || d0.RootElement.StrN("type") != "session_meta") { TaninmayanBicim = true; return; }
            Taninan++;
            Ileri(Time.NormalizeIso(d0.RootElement.StrN("timestamp")));
            return;
        }
        var type = JsonlScanner.RootType(line, out var bad);
        if (bad) { BozukSatir++; return; }
        if (type is null) return;
        Taninan++;
        if (type != "event_msg") return;
        using var doc = JsonlScanner.TryParse(line);
        if (doc is null) { BozukSatir++; return; }
        var r = doc.RootElement;
        var an = Time.NormalizeIso(r.StrN("timestamp"));
        var pl = r.Obj("payload");
        switch (pl.StrN("type"))
        {
            case "item_completed":
            {
                var it = pl.Obj("item");
                switch (it.StrN("type"))
                {
                    case "CommandExecution":
                        _codexOgeGoruldu = true;
                        CodexKomut(KomutMetni(it.Obj("command")), Int(it, "exit_code"), it.StrN("status"), an);
                        return;
                    case "FileChange":
                    {
                        _codexOgeGoruldu = true;
                        var s = Etkinlik(an);
                        if (it.StrN("status") is "failed" or "declined") return;
                        var ch = it.Obj("changes");
                        if (ch.ValueKind == JsonValueKind.Object) foreach (var p in ch.EnumerateObject()) Degisti(p.Name, s);
                        else if (ch.ValueKind == JsonValueKind.Array) foreach (var x in ch.EnumerateArray()) if (x.StrN("path") is { } yp) Degisti(yp, s);
                        return;
                    }
                    case "UserMessage": Insan(an); return;
                    case "AgentMessage": Metin(JsonlScanner.ContentText(it.Obj("content")), an); OnayIstegi = null; return;
                    case "Reasoning": case "ContextCompaction": case null: return;
                    default: SonAracSira = Etkinlik(an); return;
                }
            }
            case "task_started": TurBitti = false; Ileri(an); return;
            case "task_complete":
            {
                var last = pl.StrN("last_agent_message");
                if (!string.IsNullOrWhiteSpace(last)) Metin(last, an);
                TurBitti = true; OnayIstegi = null; Ileri(an);
                return;
            }
            case "turn_aborted": TurBitti = true; OnayIstegi = null; Ileri(an); return;
            case "exec_approval_request": case "apply_patch_approval_request":
                OnayIstegi = new AcikCagri(pl.StrN("type") == "exec_approval_request" ? "komut" : "dosya", Ileri(an), an, null); TurBitti = false; return;
            case "request_user_input": case "elicitation_request":
                OnayIstegi = new AcikCagri("soru", Ileri(an), an, null); TurBitti = false; return;
            // ---- eski Codex sürümleri (öğe olayları yoksa) ----
            case "exec_command_end":
                if (!_codexOgeGoruldu) CodexKomut(KomutMetni(pl.Obj("command")), Int(pl, "exit_code"), null, an);
                return;
            case "patch_apply_end":
            {
                if (_codexOgeGoruldu) return;
                var s = Etkinlik(an);
                if (pl.Has("success") && !pl.Bool("success")) return;
                var ch = pl.Obj("changes");
                if (ch.ValueKind == JsonValueKind.Object) foreach (var p in ch.EnumerateObject()) Degisti(p.Name, s);
                return;
            }
            case "user_message": if (!_codexOgeGoruldu) Insan(an); return;
            case "agent_message": if (!_codexOgeGoruldu) { Metin(pl.StrN("message"), an); OnayIstegi = null; } return;
        }
    }

    private long Etkinlik(string? an)
    {
        var s = Ileri(an);
        OnayIstegi = null;
        TurBitti = false;
        return s;
    }

    private void CodexKomut(string? komut, int? kod, string? durum, string? an)
    {
        var s = Etkinlik(an);
        SonAracSira = s;
        if (durum is "declined") return;
        KomutSayisi++;
        var deneme = DenemeKurallari.Tani(komut);
        if (deneme is null) return;
        var sonuc = kod is null ? DenemeSonuc.Bilinmiyor : kod == 0 ? DenemeSonuc.Gecti : DenemeSonuc.Kaldi;
        SonDeneme = new DenemeIzi(deneme, sonuc, kod, an, s);
    }

    private static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static readonly HashSet<string> Kabuklar = new(StringComparer.OrdinalIgnoreCase) { "bash", "sh", "zsh", "powershell", "pwsh", "cmd" };

    /// <summary>Codex komut alanı: dizi (["bash","-lc","npm test"]) ya da metin. Kabukla sarılıysa son öğe komuttur.</summary>
    internal static string? KomutMetni(JsonElement c)
    {
        if (c.ValueKind == JsonValueKind.String) return c.GetString();
        if (c.ValueKind != JsonValueKind.Array) return null;
        var parts = c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList();
        if (parts.Count == 0) return null;
        var ilk = Path.GetFileNameWithoutExtension(parts[0].Replace('\\', '/').Split('/')[^1]);
        if (parts.Count >= 2 && Kabuklar.Contains(ilk)) return parts[^1];
        return string.Join(' ', parts);
    }
}

public static class DenemeSonuc
{
    public const string Gecti = "gecti", Kaldi = "kaldi", Bilinmiyor = "bilinmiyor";
}

/// <summary>
/// "Bitti" iddiası (sezgisel, model kullanmaz). Asistanın metni yalnız bu denetime girer, hiçbir yere yazılmaz.
/// Mevcut <see cref="SessionStateResolver.Flags"/> "tamam", "çalışıyor" gibi ara sözleri de bitti sayar; burada daha dar bir sözcük listesi kullanılır,
/// soru biçimi ("bitti mi") ve iki noktayla biten giriş cümlesi ("şimdi testleri koşuyorum:") sayılmaz.
/// </summary>
public static class BittiIddiasi
{
    private static readonly Regex Soz = new(
        @"\b(bitti|bitirdim|bitirildi|tamamlandi|tamamladim|tamamdir|hazir(dir)?|teslim (ediyorum|edildi|ettim)|hallettim|halloldu|duzelttim|duzeltildi|cozuldu|cozdum|"
        + @"done|completed|finished|all (the )?tests pass(ed|ing)?|is ready|ready for review|implemented|fixed|resolved)\b(?!\s*(mi|mı|mu|mü)\b)(?!\?)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool Var(string? metin)
    {
        if (string.IsNullOrWhiteSpace(metin)) return false;
        var t = metin.TrimEnd();
        if (t.EndsWith(':')) return false;
        return Soz.IsMatch(TextNormalizer.Fold(t));
    }
}
