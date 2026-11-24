using Microsoft.Data.Sqlite;

namespace Soz.Core.Data;

/// <summary>Veritabanı Söz'ün bu sürümünden daha yeni bir şema sürümüyle yazılmış. Veritabanına dokunulmadan çıkılır.</summary>
public sealed class SozSchemaTooNewException : InvalidOperationException
{
    public int FoundVersion { get; }
    public int SupportedVersion { get; }
    public SozSchemaTooNewException(int found, int supported)
        : base($"Bu veri Söz'ün daha yeni bir sürümüyle yazılmış (şema v{found}, bu sürüm v{supported} biliyor). Söz'ü güncelle.")
    {
        FoundVersion = found;
        SupportedVersion = supported;
    }
}

/// <summary>Dosya Söz'ün veritabanı değil (başka bir programın application_id değeri var ya da SQLite dosyası değil). Dosyaya dokunulmaz.</summary>
public sealed class SozForeignDatabaseException : InvalidOperationException
{
    public long ApplicationId { get; }
    public SozForeignDatabaseException(long applicationId, string message) : base(message) => ApplicationId = applicationId;
}

/// <summary>Göç öncesi yedek alınamadı ya da doğrulanamadı. Göç başlamadı, veri olduğu gibi duruyor.</summary>
public sealed class SozBackupFailedException : InvalidOperationException
{
    public SozBackupFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Veri dosyasına erişilemedi (salt okunur, kilitli, disk dolu, izin yok, göç kilidi beklemesi aşıldı).
/// İleti sade Türkçedir: ne olduğunu ve kişinin ne yapacağını söyler. Ham SQLite/.NET ayrıntısı <see cref="Exception.InnerException"/> içinde kalır.
/// </summary>
public sealed class SozDataAccessException : InvalidOperationException
{
    /// <summary>Makine okunur neden: salt_okunur | kilitli | disk_dolu | izin | acilamadi | bozuk | disk_hatasi | goc_kilidi | bilinmiyor.</summary>
    public string Reason { get; }

    public SozDataAccessException(string reason, string message, Exception? inner = null) : base(message, inner) => Reason = reason;

    /// <summary>Ham istisnayı (SQLite, dosya sistemi) sade Türkçe iletiye çevirir. <paramref name="what"/> iletinin başında neyin başarısız olduğunu söyler (ör. "Veri dosyası açılamadı").</summary>
    public static SozDataAccessException From(Exception e, string? what = null)
    {
        var (reason, text) = Explain(e);
        var head = string.IsNullOrEmpty(what) ? "" : what.TrimEnd('.', ' ') + ". ";
        return new SozDataAccessException(reason, head + text, e);
    }

    /// <summary>Neden kodu ve "ne oldu, ne yapmalı" cümleleri.</summary>
    public static (string Reason, string Text) Explain(Exception e)
    {
        const string Safe = " Veriye dokunulmadı.";
        if (e is SqliteException s)
        {
            // Genişletilmiş kodun alt baytı birincil koddur.
            switch (s.SqliteErrorCode)
            {
                case 5: // SQLITE_BUSY
                case 6: // SQLITE_LOCKED
                    return ("kilitli", "Veri dosyası şu an başka bir program ya da Söz süreci tarafından kullanılıyor. Birkaç saniye sonra yeniden dene. Sürerse açık Söz pencerelerini ve veri klasörünü tarayan programları (yedekleme, eşitleme) kapat." + Safe);
                case 8: // SQLITE_READONLY
                    return ("salt_okunur", "Veri dosyası salt okunur durumda, Söz yazamıyor. Dosyanın özelliklerinden 'Salt okunur' işaretini kaldır ve klasörün yazma iznini kontrol et." + Safe);
                case 13: // SQLITE_FULL
                    return ("disk_dolu", "Diskte yer kalmamış, Söz yazamıyor. Diskte yer aç ve yeniden dene." + Safe);
                case 14: // SQLITE_CANTOPEN
                    return ("acilamadi", "Veri dosyası açılamadı. Veri klasörünün yolunu ve erişim iznini kontrol et." + Safe);
                case 10: // SQLITE_IOERR
                    return ("disk_hatasi", "Veri dosyası okunurken ya da yazılırken disk hatası oldu. Diskin dolu ya da bağlantısının kopuk olmadığını kontrol et, sonra yeniden dene." + Safe);
                case 11: // SQLITE_CORRUPT
                case 26: // SQLITE_NOTADB
                    return ("bozuk", "Veri dosyası bozuk görünüyor. Dosyaya dokunma. 'Yedek' klasöründeki son yedekten geri dönmeyi dene." + Safe);
            }
        }
        if (e is UnauthorizedAccessException)
            return ("izin", "Veri klasörüne ya da dosyasına yazma izni yok. Klasörün yazma iznini kontrol et ve yeniden dene." + Safe);
        if (e is IOException io)
        {
            var hr = io.HResult & 0xFFFF;
            if (hr is 112 or 39) // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
                return ("disk_dolu", "Diskte yer kalmamış, Söz yazamıyor. Diskte yer aç ve yeniden dene." + Safe);
            if (hr is 32 or 33) // paylaşım / kilit ihlali
                return ("kilitli", "Veri klasöründeki bir dosya başka bir program tarafından kullanılıyor. Birkaç saniye sonra yeniden dene." + Safe);
        }
        return ("bilinmiyor", "Veri dosyasına erişirken beklenmeyen bir sorun çıktı. Yeniden dene. Sürerse veri klasörünün yolunu ve erişim iznini kontrol et." + Safe);
    }
}
