using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Soz.Capsule;

/// <summary>Ekranın bir bölgesini PNG olarak yazar (DWM'nin birleştirdiği son görüntü). Yalnız okur, fare ya da klavye göndermez.</summary>
internal static unsafe class Goruntu
{
    public static bool Kaydet(string dosya, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        var sdc = N.GetDC(IntPtr.Zero);
        var mdc = N.CreateCompatibleDC(sdc);
        var bmp = N.CreateCompatibleBitmap(sdc, w, h);
        var eski = N.SelectObject(mdc, bmp);
        N.BitBlt(mdc, 0, 0, w, h, sdc, x, y, 0x00CC0020 /*SRCCOPY*/);
        var bi = new N.BITMAPINFOHEADER { biSize = (uint)sizeof(N.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        fixed (byte* p = px) N.GetDIBits(mdc, bmp, 0, (uint)h, p, ref bi, 0);
        N.SelectObject(mdc, eski); N.DeleteObject(bmp); N.DeleteDC(mdc); N.ReleaseDC(IntPtr.Zero, sdc);
        for (var i = 0; i < w * h; i++) { var b = px[i * 4]; px[i * 4] = px[i * 4 + 2]; px[i * 4 + 2] = b; px[i * 4 + 3] = 255; }
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dosya))!); File.WriteAllBytes(dosya, PngYaz(px, w, h)); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>Bölgenin piksellerinin özeti (FNV-1a). Ekranda bir şey değişti mi sorusuna; başka iş parçacığından çağrılabilir.</summary>
    public static ulong Ozet(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return 0;
        var sdc = N.GetDC(IntPtr.Zero);
        var mdc = N.CreateCompatibleDC(sdc);
        var bmp = N.CreateCompatibleBitmap(sdc, w, h);
        var eski = N.SelectObject(mdc, bmp);
        N.BitBlt(mdc, 0, 0, w, h, sdc, x, y, 0x00CC0020);
        var bi = new N.BITMAPINFOHEADER { biSize = (uint)sizeof(N.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        fixed (byte* p = px) N.GetDIBits(mdc, bmp, 0, (uint)h, p, ref bi, 0);
        N.SelectObject(mdc, eski); N.DeleteObject(bmp); N.DeleteDC(mdc); N.ReleaseDC(IntPtr.Zero, sdc);
        ulong hh = 14695981039346656037UL;
        for (var i = 0; i < px.Length; i += 4) { hh = (hh ^ px[i]) * 1099511628211UL; hh = (hh ^ px[i + 1]) * 1099511628211UL; hh = (hh ^ px[i + 2]) * 1099511628211UL; }
        return hh;
    }

    private static uint[]? _tablo;
    private static uint Crc(ReadOnlySpan<byte> d, uint c = 0xFFFFFFFF)
    {
        if (_tablo == null) { _tablo = new uint[256]; for (uint n = 0; n < 256; n++) { var k = n; for (var j = 0; j < 8; j++) k = (k & 1) != 0 ? 0xEDB88320 ^ (k >> 1) : k >> 1; _tablo[n] = k; } }
        foreach (var b in d) c = _tablo[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }

    internal static byte[] PngYaz(byte[] rgba, int w, int h)
    {
        var ham = new byte[(w * 4 + 1) * h];
        for (var y = 0; y < h; y++) { ham[y * (w * 4 + 1)] = 0; Buffer.BlockCopy(rgba, y * w * 4, ham, y * (w * 4 + 1) + 1, w * 4); }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Parca(string tip, byte[] veri)
        {
            var uz = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(uz, (uint)veri.Length); ms.Write(uz);
            var t = System.Text.Encoding.ASCII.GetBytes(tip); ms.Write(t); ms.Write(veri);
            var c = Crc(veri, Crc(t)) ^ 0xFFFFFFFF; var cb = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cb, c); ms.Write(cb);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 6; // 8 bit, RGBA
        Parca("IHDR", ihdr);
        using (var z = new MemoryStream()) { using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(ham); Parca("IDAT", z.ToArray()); }
        Parca("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
}
