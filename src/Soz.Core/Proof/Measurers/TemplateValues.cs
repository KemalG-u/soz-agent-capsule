using System.Globalization;

namespace Soz.Core.Proof;

/// <summary>Düz cümle şablonlarında kişinin yazdığı değerleri okuma ve doğrulama (hata: SozException 400, sade Türkçe).</summary>
internal static class TemplateValues
{
    public static string Get(IReadOnlyDictionary<string, string> v, string name) => v.TryGetValue(name, out var s) ? s.Trim() : "";

    /// <summary>Proje klasörü bilinirse yol kökün içinde olmalı (kısayol, `..` ve mutlak yol reddedilir).</summary>
    public static string Path(IReadOnlyDictionary<string, string> v, string name, string? root)
    {
        var p = Get(v, name);
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            var err = SafeFiles.Resolve(root, p, out _);
            if (err is not null) throw new SozException(400, err);
        }
        return p;
    }

    /// <summary>Boşsa null; doluysa 0 ya da büyük tam sayı olmalı.</summary>
    public static long? Long(IReadOnlyDictionary<string, string> v, string name, string label)
    {
        var s = Get(v, name);
        if (s.Length == 0) return null;
        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0)
            throw new SozException(400, $"{label} 0 ya da daha büyük bir tam sayı olmalı: {s}");
        return n;
    }

    /// <summary>Boşsa null; doluysa sayı (nokta ya da virgülle).</summary>
    public static decimal? Number(IReadOnlyDictionary<string, string> v, string name, string label)
    {
        var s = Get(v, name);
        if (s.Length == 0) return null;
        if (!decimal.TryParse(s.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            throw new SozException(400, $"{label} bir sayı olmalı: {s}");
        return n;
    }
}
