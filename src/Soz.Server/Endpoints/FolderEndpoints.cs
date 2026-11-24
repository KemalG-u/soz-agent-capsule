using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soz.Core;
using Soz.Core.Jobs;

namespace Soz.Server.Endpoints;

/// <summary>
/// Klasör gezme (denetim 5 tur 2, N8): tarayıcı sürümünde "klasör ekle" için tam yol yazmak yerine klasör seçilir.
/// Sunucu bu bilgisayarda çalıştığı için dosya sistemini kendisi gezer. Güvenlik:
///   - Yalnız insan çağırabilir (CallerGate: ajan süreci 403; ajan klasör haritası çıkaramaz).
///   - Yalnız KLASÖR ADLARI döner, dosya adı ve içerik asla. Gizli, sistem ve nokta ile başlayan klasörler listelenmez.
///   - Telefon/LAN yüzeyi (PhoneServer) ayrı bir uygulamadır, bu uç orada YOKTUR.
///   - Ağ yolları (\\sunucu\paylasim) gezilmez.
///   GET /api/fs/browse            sürücü listesi
///   GET /api/fs/browse?path=      o klasörün alt klasörleri, üst klasör, "bu klasör proje olabilir mi" (selectable + reason)
/// </summary>
public static class FolderEndpoints
{
    public const int MaxFolders = 500;

    public static RouteGroupBuilder MapFolders(this RouteGroupBuilder api)
    {
        api.MapGet("/fs/browse", (string? path) => Results.Json(Browse(path)));
        return api;
    }

    public sealed record FolderEntry(string Name, string Path);

    /// <summary>Kişi klasör yerine var olan bir dosyanın yolunu yazdığında söylenen sade cümle.</summary>
    public const string NotAFolder = "Bu bir dosya, klasör değil. Klasörün yolunu yaz.";

    public static object Browse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new { path = (string?)null, parent = (string?)null, selectable = false, reason = (string?)null, truncated = false, folders = Drives() };
        var p = raw.Trim().Trim('"');
        if (p.StartsWith(@"\\") || p.StartsWith("//")) throw new SozException(400, "Ağ klasörleri gezilemez. Bu bilgisayardaki bir klasörü seç.");
        if (!Path.IsPathFullyQualified(p)) throw new SozException(400, "Klasör yolu tam olmalı.");
        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new SozException(400, "Klasör yolu geçersiz."); }
        if (!Directory.Exists(full) && File.Exists(full)) throw new SozException(400, NotAFolder);
        if (!Directory.Exists(full)) throw new SozException(404, "Bu klasör bulunamadı.");

        var folders = new List<FolderEntry>();
        var truncated = false;
        try
        {
            foreach (var d in new DirectoryInfo(full).EnumerateDirectories().OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (IsHiddenOrSystem(d)) continue;
                if (folders.Count >= MaxFolders) { truncated = true; break; }
                folders.Add(new FolderEntry(d.Name, d.FullName));
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new SozException(400, "Bu klasör açılamadı.");
        }

        var parent = Directory.GetParent(full.TrimEnd('\\', '/'))?.FullName;
        var reason = WorkService.CheckProjectPath(full);
        return new { path = full, parent, selectable = reason is null, reason, truncated, folders };
    }

    private static bool IsHiddenOrSystem(DirectoryInfo d)
    {
        if (d.Name.StartsWith('.') || d.Name.StartsWith('$')) return true;
        try { return (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
        catch (Exception) { return true; }
    }

    private static List<FolderEntry> Drives()
    {
        var res = new List<FolderEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : " (" + d.VolumeLabel + ")";
                res.Add(new FolderEntry(d.Name.TrimEnd('\\', '/') + label, d.Name));
            }
            catch (Exception) { /* hazır olmayan sürücü listelenmez */ }
        }
        return res;
    }
}
