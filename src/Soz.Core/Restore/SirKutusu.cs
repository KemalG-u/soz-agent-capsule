using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Soz.Core.Text;

namespace Soz.Core.Restore;

/// <summary>Sır türü dosyanın içeriği Windows kullanıcı korumasıyla açılamadı (başka hesap, başka bilgisayar ya da bozuk kayıt).</summary>
public sealed class SirAcilamadiException : Exception
{
    public SirAcilamadiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Geri alma deposunda sır türü dosyaların korunması (denetim 5, B5). Sır sayılan dosyalar (.env, anahtar ve sertifika dosyaları, kimlik bilgisi dosyaları
/// ve içinde anahtar deseni bulunanlar) depoya düz metin yazılmaz: Windows kullanıcı korumasıyla (DPAPI, CurrentUser) şifrelenir, geri almada çözülür ve
/// dosya bayt bayt aynı döner (blob özeti DÜZ içeriğin özetidir, geri yazılan baytlar bununla doğrulanır).
/// Bu dosyalar hiçbir dışa aktarma ya da paylaşma yoluna girmez (depo yalnız geri alma motoru tarafından okunur).
/// DÜRÜST SINIR: koruma aynı Windows hesabına karşı çalışmaz. O hesapta çalışan bir ajan (ya da herhangi bir program) bu kutuyu çözebilir.
/// Bu bir özen katmanıdır: depo klasörü kopyalanırsa, yedeklenirse ya da başka hesapla açılırsa sırlar düz görünmesin diye vardır; kasa değildir.
/// Sınırlar: bu koruma eklenmeden ÖNCE alınmış noktalardaki içerik olduğu gibi (düz) kalır; aynı içerik daha önce sır olmayan bir dosya olarak
/// saklanmışsa aynı özetle tek kopya tutulduğundan o kopya düz kalır.
/// </summary>
public static class SirKutusu
{
    public const string Codec = "dpapi";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Soz.GeriDepo.v1");

    /// <summary>İçeriğine bakılacak en büyük dosya (saklama kuralıyla aynı: küçük dosyalar).</summary>
    public const int ContentScanMaxBytes = 256 * 1024;

    private static readonly string[] SecretNamePrefixes = { ".env", "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", "credentials", "secrets", ".npmrc", ".netrc", ".pgpass", "service-account" };
    private static readonly string[] SecretExtensions = { ".pem", ".key", ".pfx", ".p12", ".p8", ".ppk", ".jks", ".keystore", ".cer", ".crt", ".der", ".kdbx", ".asc", ".gpg" };

    /// <summary>Ortak sır dosyası adı listesi (bekçinin korunan yolları ve kanıt motorunun hassas dosya listesiyle aynı kümeyi kapsar, biraz geniştir).</summary>
    public static bool IsSecretFile(string relPath)
    {
        var name = relPath.Replace('\\', '/');
        var i = name.LastIndexOf('/');
        if (i >= 0) name = name[(i + 1)..];
        name = name.ToLowerInvariant();
        foreach (var p in SecretNamePrefixes) if (name.StartsWith(p, StringComparison.Ordinal)) return true;
        foreach (var e in SecretExtensions) if (name.EndsWith(e, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Küçük, metin görünümlü içerikte anahtar deseni var mı (sır süzgeciyle aynı kurallar).</summary>
    public static bool ContentLooksSecret(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > ContentScanMaxBytes) return false;
        if (data[..Math.Min(data.Length, 4096)].Contains((byte)0)) return false; // ikili dosya
        string text;
        try { text = Encoding.UTF8.GetString(data); }
        catch (ArgumentException) { return false; }
        return SecretFilter.ContainsSecret(text);
    }

    // ---------------------------------------------------------------- DPAPI (crypt32)

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN; kapsam CurrentUser (LOCAL_MACHINE bayrağı yok)

    private static byte[] Run(ReadOnlySpan<byte> input, bool protect)
    {
        var inPtr = Marshal.AllocHGlobal(Math.Max(1, input.Length));
        var entPtr = Marshal.AllocHGlobal(Entropy.Length);
        try
        {
            if (input.Length > 0) Marshal.Copy(input.ToArray(), 0, inPtr, input.Length);
            Marshal.Copy(Entropy, 0, entPtr, Entropy.Length);
            var din = new DataBlob { cbData = input.Length, pbData = inPtr };
            var ent = new DataBlob { cbData = Entropy.Length, pbData = entPtr };
            DataBlob dout;
            var ok = protect
                ? CryptProtectData(ref din, null, ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dout)
                : CryptUnprotectData(ref din, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dout);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var res = new byte[dout.cbData];
                if (dout.cbData > 0) Marshal.Copy(dout.pbData, res, 0, dout.cbData);
                return res;
            }
            finally { if (dout.pbData != IntPtr.Zero) LocalFree(dout.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(entPtr);
        }
    }

    public static byte[] Protect(ReadOnlySpan<byte> plain) => Run(plain, protect: true);

    public static byte[] Unprotect(byte[] stored)
    {
        try { return Run(stored, protect: false); }
        catch (CryptographicException e) { throw new SirAcilamadiException("Sır türü dosyanın içeriği bu Windows hesabıyla açılamadı.", e); }
    }
}
