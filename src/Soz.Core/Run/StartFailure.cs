using System.ComponentModel;

namespace Soz.Core.Run;

/// <summary>
/// Yardımcı hiç başlatılamadığında (yol yanlış, dosya çalıştırılamıyor) kişiye gidecek tek sade cümle (C9). Ham .NET hatası kişiye gitmez, ayrı teknik alanda kalır
/// (run_lines.detail). Sentence "yardımcının sözü" değil Söz'ün kendi notudur.
/// </summary>
public static class StartFailure
{
    /// <summary>Win32 kodu: 2 dosya yok, 3 yol yok, 193 geçerli program değil, 5 erişim engeli, 740 yönetici gerekli.</summary>
    public static string Sentence(string displayName, Exception e)
    {
        var code = e is Win32Exception w ? w.NativeErrorCode : 0;
        return code is 2 or 3
            ? $"{displayName} bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et."
            : code == 5
                ? $"{displayName} açılamadı, Windows izin vermedi. Ayarlar'dan yolunu kontrol et."
                : $"{displayName} açılamadı. Ayarlar'dan yolunu kontrol et.";
    }
}
