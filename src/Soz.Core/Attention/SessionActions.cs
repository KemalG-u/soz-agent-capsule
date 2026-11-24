using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;

namespace Soz.Core.Attention;

/// <summary>Eylemin sonucu: durum kodu (HTTP), kişiye gösterilecek tek sade cümle, varsa ek veri.</summary>
public sealed record ActionOutcome(bool Ok, int Status, string Message, object? Data = null);

/// <summary>Pencereyi öne getirme isteği: bilinen pencere/süreç numarası, projenin klasörü ve oturumun başlığı (sekme eşleştirmesi bunlara dayanır; hepsi isteğe bağlı).</summary>
public sealed record FocusRequest(long? Hwnd, int? Pid, string? ProjectPath = null, string? SessionTitle = null);

/// <summary>Pencere öne getirme sonucu. <c>TabSelected</c>: doğru sekme (ya da klasörün penceresi) seçilebildi mi; false ise yalnız pencere öne gelmiştir. <c>Hint</c>: belirsizlikte kişiye söylenecek sade cümle.</summary>
public sealed record FocusResult(bool Ok, long? Hwnd, bool TabSelected = false, string? Hint = null);

/// <summary>
/// "Benden Beklenen" kutusundaki oturum öğelerinin eylemleri (n6). Sunucu uçları bunu çağırır; masaüstü pencere işi (öne getirme) Windows çağrısına dayanır ve test için
/// <see cref="Focuser"/> ile değiştirilebilir.
/// <list type="bullet">
/// <item><b>Focus</b>: oturumun açık penceresini öne getirir. Pencere, oturumun canlı kaydındaki süreç ya da pencere numarasından bulunur. Bulunamazsa anlaşılır hata döner (uydurma yok).</item>
/// <item><b>Prove</b>: oturumun projesindeki kayıtlı ölçütlerle Söz'ün kanıt motorunu o klasörde çalıştırır (kişi "bu işi sına" dediğinde). Ölçüt yoksa sınamayı başlatmaz ve bunu söyler.</item>
/// </list>
/// </summary>
public sealed class SessionActions(SozDb db, AttentionService attention)
{
    /// <summary>Pencereyi öne getirir. Varsayılan: Windows pencere çağrısı (sekme eşleştirmesi yok). Masaüstü kabuğu kendi eşleştiricisini (Windows Terminal sekmesi, editör penceresi) buraya bağlar;
    /// sunucudaki bütün pencere uçları (<c>/api/live/focus</c>, <c>/api/sessions/{id}/focus</c>, <c>/api/attention/focus</c>) bunun üzerinden geçer.</summary>
    public static Func<FocusRequest, FocusResult> Focuser { get; set; } = r =>
    {
        var ok = Soz.Core.Profile.Win32ProcessInspector.FocusWindow(r.Hwnd, r.Pid, out var used);
        return new FocusResult(ok, used);
    };
    public static Func<string, bool> FolderOpener { get; set; } = p => Soz.Core.Profile.Win32ProcessInspector.OpenFolderInExplorer(p);
    /// <summary>Masaüstü var mı (Windows). Test kapatabilir.</summary>
    public static Func<bool> HasDesktop { get; set; } = OperatingSystem.IsWindows;

    private sealed record Target(long? SessionId, string? Tool, string? ExternalId, long? ProjectId, string? ProjectPath, string? ItemProjectPath);

    private Target? ByDb(long sessionId)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT s.id, s.tool, s.external_id, s.project_id, p.path FROM sessions s LEFT JOIN projects p ON p.id=s.project_id WHERE s.id=@id
            """, r => new Target(r.GetInt64(0), r.GetString(1), r.GetString(2), r.LongN(3), r.Str(4), null), null, ("@id", sessionId)).FirstOrDefault();
    }

    private Target? ByItem(string itemId)
    {
        var item = attention.Snapshot().Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
        if (item is null) return null;
        if (item.SessionId is { } sid) return ByDb(sid) is { } t ? t with { ItemProjectPath = item.ProjectPath } : new Target(sid, null, item.ExternalSessionId, null, null, item.ProjectPath);
        return new Target(null, null, item.ExternalSessionId, null, item.ProjectPath, item.ProjectPath);
    }

    private ExternalSession? Live(Target t)
    {
        IReadOnlyList<ExternalSession> rows;
        try { rows = attention.External?.Snapshot() ?? Array.Empty<ExternalSession>(); }
        catch (Exception) { return null; }
        if (t.ExternalId is null) return null;
        return rows.Where(e => e.SessionId == t.ExternalId && (t.Tool is null || e.Tool.Equals(t.Tool, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.ObservedUtc).FirstOrDefault();
    }


    /// <summary>
    /// Bilinen pencere/süreç ve proje bilgisiyle doğrudan öne getirme (<c>POST /api/live/focus</c>): <see cref="Focus"/> ile aynı <see cref="Focuser"/> yolundan geçer.
    /// Pencere bulunamazsa ve <paramref name="openFolder"/> istenmişse projenin klasörü açılır.
    /// </summary>
    public static ActionOutcome FocusDirect(FocusRequest req, bool openFolder)
    {
        if (!HasDesktop())
            return new(false, 501, "Pencereyi öne getirme yalnız Windows masaüstünde çalışır. Bu çalışmada masaüstü yok.");
        var res = Focuser(req);
        if (res.Ok) return new(true, 200, "Pencere öne getirildi.", new { focused = true, hwnd = res.Hwnd, openedFolder = false, tabSelected = res.TabSelected, hint = res.Hint });
        if (openFolder && req.ProjectPath is { Length: > 0 } folder && Directory.Exists(folder) && FolderOpener(folder))
            return new(true, 200, "Açık pencere bulunamadığı için proje klasörü açıldı.", new { focused = false, openedFolder = true, tabSelected = false });
        return new(false, 404, "Öne getirilecek açık bir pencere bulunamadı.", new { focused = false, openedFolder = false, tabSelected = false });
    }

    public ActionOutcome Focus(long? sessionId, string? itemId, bool openFolder = false)
    {
        var t = sessionId is { } sid ? ByDb(sid) : itemId is not null ? ByItem(itemId) : null;
        if (t is null)
            return new(false, 404, sessionId is null && itemId is null ? "Hangi çalışma olduğu belirtilmemiş." : "Bu çalışmayı bulamadım. Liste kendiliğinden güncellenmiş olabilir.");
        if (!HasDesktop())
            return new(false, 501, "Pencereyi öne getirme yalnız Windows masaüstünde çalışır. Bu çalışmada masaüstü yok.");
        var live = Live(t);
        if (live is null || (live.Pid is null && live.Hwnd is null))
        {
            if (openFolder && (t.ProjectPath ?? t.ItemProjectPath) is { Length: > 0 } folder && Directory.Exists(folder) && FolderOpener(folder))
                return new(true, 200, "Açık pencereyi bulamadım, proje klasörünü açtım.", new { focused = false, openedFolder = true });
            return new(false, 404, "Bu çalışmanın açık penceresini bulamadım. Pencere kapanmış ya da Söz o pencereyi bu çalışmayla eşleştirememiş olabilir. Pencereyi kendin açman gerekir.");
        }
        var res = Focuser(new FocusRequest(live.Hwnd, live.Pid, t.ProjectPath ?? t.ItemProjectPath ?? live.ProjectPath, live.Title));
        if (res.Ok) return new(true, 200, "Pencere öne getirildi.", new { focused = true, hwnd = res.Hwnd, openedFolder = false, tabSelected = res.TabSelected, hint = res.Hint });
        return new(false, 404, "Pencereyi öne getiremedim. Pencere kapanmış ya da küçültülmüş olabilir.");
    }

    public ActionOutcome Prove(long? sessionId, string? itemId, WorkService work)
    {
        var t = sessionId is { } sid ? ByDb(sid) : itemId is not null ? ByItem(itemId) : null;
        if (t is null) return new(false, 404, sessionId is null && itemId is null ? "Hangi çalışma olduğu belirtilmemiş." : "Bu çalışmayı bulamadım. Liste kendiliğinden güncellenmiş olabilir.");
        long? projectId = t.ProjectId;
        if (projectId is null && t.ProjectPath is { } path)
        {
            using var c = db.Open();
            projectId = c.Query("SELECT id FROM projects WHERE path=@p COLLATE NOCASE AND key<>'*' LIMIT 1", r => r.GetInt64(0), null, ("@p", path)).Cast<long?>().FirstOrDefault();
        }
        if (projectId is null) return new(false, 409, "Bu çalışmanın hangi projede olduğunu bilmiyorum, bu yüzden sınayamam.");
        int checks; bool running; string? projectName;
        using (var c = db.Open())
        {
            checks = CheckStore.EnabledFor(c, projectId.Value, null).Count;
            running = c.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE project_id=@p AND verdict IS NULL AND finished_at IS NULL", null, ("@p", projectId)) > 0;
            projectName = c.Scalar("SELECT name FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
        }
        if (checks == 0) return new(false, 409, "Bu projede neyle sınayacağımı bilmiyorum. Önce projeye bir kontrol eklemen gerekir.", new { projectId, checks = 0 });
        if (running) return new(false, 409, "Bu proje için bir sınama zaten sürüyor.", new { projectId });
        long runId;
        using (var c = db.Open())
            runId = ProofStore.Begin(c, new ProofStore.RunRequest(projectId.Value, null, t.SessionId, "manual", 1));
        _ = Task.Run(() =>
        {
            try { work.CompleteRun(runId); }
            catch (Exception e)
            {
                try { using var c = db.Open(); c.Exec("UPDATE proof_runs SET verdict='error', summary=@s, finished_at=@n WHERE id=@id AND verdict IS NULL", null, ("@s", "Çalıştırıcı hatası: " + e.Message), ("@n", Time.NowIso()), ("@id", runId)); }
                catch (SqliteException) { }
            }
        });
        return new(true, 202, $"«{projectName ?? "Proje"}» için {checks} kontrolle sınama başladı.", new { runId, projectId, checks });
    }
}
