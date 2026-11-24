using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Soz.Core.Registry;

namespace Soz.Core.Proof;

/// <summary>
/// Yeni ölçücülerin ortak dosya okuma kuralları: yol proje klasöründen çıkamaz (`..`, mutlak yol, kısayol/bağlantı, akış adı), dosya en çok sınır kadar okunur
/// (1 GB'lık dosya belleği doldurmaz), kodlama sezilir ve söylenir, ikili dosya metin diye okunmaz. Okunamayan her durum "error" olur, asla "geçti" değil.
/// </summary>
internal static class SafeFiles
{
    /// <summary>Metin olarak en çok bu kadar bayt okunur.</summary>
    public const long MaxTextBytes = 8L * 1024 * 1024;
    /// <summary>Tablo için okunan en çok bayt.</summary>
    public const long MaxTableBytes = 32L * 1024 * 1024;

    /// <summary>Yolu kökün içinde çözer. Sorun varsa sade Türkçe neden döner (full boş kalır); yoksa null.</summary>
    public static string? Resolve(string root, string? rel, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(rel)) return "Dosya yolu boş.";
        if (rel.Contains('\0')) return "Dosya yolu geçersiz.";
        var tail = rel.Length >= 2 && char.IsLetter(rel[0]) && rel[1] == ':' ? rel[2..] : rel;
        if (tail.Contains(':')) return "Dosya yolu geçersiz (iki nokta üst üste içeriyor): " + rel;
        string f;
        try { f = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "Dosya yolu geçersiz: " + rel; }
        var r = Path.GetFullPath(root);
        if (!SozService.IsUnder(f, r)) return "Yol proje klasörünün dışına çıkıyor: " + rel;
        // Kökten dosyaya kadar var olan her parça gerçek klasör/dosya olmalı: kısayol ya da bağlantı kök dışına götürebilir.
        var relative = Path.GetRelativePath(r, f);
        var cur = r;
        foreach (var seg in relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, seg);
            try
            {
                if (!File.Exists(cur) && !Directory.Exists(cur)) break;
                if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0) return "Yolda kısayol (bağlantı) var, proje dışına çıkabilir. Ölçülmedi: " + rel;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Yol okunamadı: " + rel; }
        }
        full = f;
        return null;
    }

    /// <summary>Okunan metin. Error doluysa Text yoktur. Note: kodlama hakkında söylenecek şey (UTF-8 değilse hangi kodlama varsayıldı).</summary>
    public sealed record TextFile(string? Text, string? Error, bool Truncated, long Length, string? Note);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static bool _providerRegistered;

    private static Encoding Win1254()
    {
        if (!_providerRegistered) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); _providerRegistered = true; }
        return Encoding.GetEncoding(1254);
    }

    public static TextFile ReadText(string fullPath, long maxBytes = MaxTextBytes)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var want = (int)Math.Min(length, maxBytes);
            var buf = new byte[want];
            var got = 0;
            while (got < want) { var n = fs.Read(buf, got, want - got); if (n <= 0) break; got += n; }
            if (got < buf.Length) Array.Resize(ref buf, got);
            var truncated = length > got;
            return Decode(buf, truncated, length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TextFile(null, "Dosya okunamadı: " + e.Message, false, 0, null);
        }
    }

    internal static TextFile Decode(byte[] b, bool truncated, long length)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Ok(StrictOrLoose(b, 3, truncated, out var note), truncated, length, note);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Ok(Encoding.Unicode.GetString(b, 2, b.Length - 2 - (b.Length % 2)), truncated, length, "UTF-16 (BOM)");
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Ok(Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2 - (b.Length % 2)), truncated, length, "UTF-16 (BOM)");
        // BOM yok: ilk 8 KiB'de NUL baytı varsa ikili dosyadır (docx, pdf, resim...).
        var probe = Math.Min(b.Length, 8192);
        for (var i = 0; i < probe; i++)
            if (b[i] == 0) return new TextFile(null, "Dosya ikili görünüyor (metin değil), yazı olarak okunamadı.", truncated, length, null);
        return Ok(StrictOrLoose(b, 0, truncated, out var n2), truncated, length, n2);
    }

    private static TextFile Ok(string text, bool truncated, long length, string? note) => new(text.TrimStart('﻿'), null, truncated, length, note);

    /// <summary>Önce katı UTF-8; olmazsa (kesilmiş son karakter hariç) Windows-1254 varsayılır ve söylenir.</summary>
    private static string StrictOrLoose(byte[] b, int start, bool truncated, out string? note)
    {
        note = null;
        try { return StrictUtf8.GetString(b, start, b.Length - start); }
        catch (DecoderFallbackException) { }
        if (truncated)
        {
            // Okuma sınırı çok baytlı bir karakteri ortadan kesmiş olabilir: sondan en çok 3 bayt at.
            for (var cut = 1; cut <= 3 && b.Length - start - cut > 0; cut++)
            {
                try { return StrictUtf8.GetString(b, start, b.Length - start - cut); } catch (DecoderFallbackException) { }
            }
        }
        note = "Dosya UTF-8 değil. Windows-1254 (Türkçe) kodlaması varsayıldı.";
        return Win1254().GetString(b, start, b.Length - start);
    }

    // ---------- metin karşılaştırma ----------
    private static readonly Regex Spaces = new(@"\s+", RegexSpeed.Compiled);

    /// <summary>Türkçe harf katlaması + görünmez/ayrılmaz boşlukların atılması + boşluk sadeleştirme (İ/ı, ş, NBSP, sıfır genişlikli karakter tuzakları).</summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '​' or '‌' or '‍' or '﻿' or '­' or '⁠') continue;
            sb.Append(ch is ' ' or ' ' or ' ' ? ' ' : ch);
        }
        return Spaces.Replace(Fold.Text(sb.ToString()), " ").Trim();
    }

    // ---------- docx ----------
    /// <summary>Docx'ten paragraflar: (metin, başlık düzeyi ya da 0). Hata: null ve neden.</summary>
    public static List<(string text, int level)>? ReadDocx(string fullPath, out string? error)
    {
        error = null;
        try
        {
            using var zip = ZipFile.OpenRead(fullPath);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) { error = "Bu bir Word (docx) belgesi gibi görünmüyor: word/document.xml yok."; return null; }
            using var raw = entry.Open();
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = raw.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > 16L * 1024 * 1024) { error = "Word belgesi çok büyük, tamamı okunamadı."; return null; }
                ms.Write(buf, 0, n);
            }
            ms.Position = 0;
            var list = new List<(string, int)>();
            using var xr = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var sb = new StringBuilder();
            var level = 0;
            var inPara = false;
            var advance = true;
            while (true)
            {
                if (advance && !xr.Read()) break;
                advance = true;
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "p") { inPara = true; sb.Clear(); level = 0; }
                else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "pStyle" && inPara)
                {
                    var v = xr.GetAttribute("val", xr.NamespaceURI) ?? xr.GetAttribute("w:val") ?? "";
                    var m = Regex.Match(v, @"^(?:heading|başlık|baslik|ba.l.k)\s*(\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (m.Success) level = m.Groups[1].Value[0] - '0';
                    else if (v.Equals("Title", StringComparison.OrdinalIgnoreCase) || v.Equals("Baslik", StringComparison.OrdinalIgnoreCase)) level = 1;
                }
                else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t" && inPara) { sb.Append(xr.ReadElementContentAsString()); advance = false; }
                else if (xr.NodeType == XmlNodeType.Element && xr.LocalName is "tab" or "br" && inPara) sb.Append(' ');
                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "p" && inPara) { list.Add((sb.ToString(), level)); inPara = false; }
            }
            return list;
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
        {
            error = "Word belgesi açılamadı (bozuk ya da şifreli olabilir).";
            return null;
        }
    }
}
