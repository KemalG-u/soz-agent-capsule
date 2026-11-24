using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;

namespace Soz.Core.Queue;

/// <summary>Mesaj kuyruğu: panelden (ya da telefondan) yazılan mesaj ajanın bir sonraki duruşunda Stop kancasıyla iletilir.</summary>
public static class QueueStore
{
    public const int MaxText = 4000;

    private const string Select = """
        SELECT q.id, q.project_id, p.name, q.session_id, q.text, q.created_at, q.delivered_at, q.origin
        FROM queue q LEFT JOIN projects p ON p.id=q.project_id
        """;

    private static QueueItemDto Map(SqliteDataReader r) => new(r.GetInt64(0), r.LongN(1), r.Str(2), r.LongN(3), r.Str(4) ?? "", r.Str(5), r.Str(6), r.Str(7) ?? "panel");

    public static List<QueueItemDto> List(SqliteConnection c, long? projectId, long? sessionId, bool pendingOnly, int limit = 100)
        => c.Query(Select + """
             WHERE (@p IS NULL OR q.project_id=@p) AND (@s IS NULL OR q.session_id=@s) AND (@pend=0 OR q.delivered_at IS NULL)
             ORDER BY q.id DESC LIMIT @l
            """, Map, null, ("@p", projectId), ("@s", sessionId), ("@pend", pendingOnly ? 1 : 0), ("@l", Math.Clamp(limit, 1, 500)));

    public static QueueItemDto Add(SqliteConnection c, long? projectId, long? sessionId, string text, string origin)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new SozException(400, "Mesaj boş olamaz.");
        if (text.Length > MaxText) throw new SozException(400, $"Mesaj en çok {MaxText} karakter olabilir.");
        if (origin is not ("panel" or "telefon")) throw new SozException(400, "origin panel ya da telefon olmalı.");
        if (sessionId is not null)
        {
            var srow = c.Query("SELECT project_id, tool FROM sessions WHERE id=@s", r => (p: r.LongN(0), t: r.Str(1)), null, ("@s", sessionId)).ToList();
            if (srow.Count == 0) throw new SozException(404, $"Çalışma bulunamadı: {sessionId}");
            projectId ??= srow[0].p;
            // n6: mesajı yalnız ajanın durduğunda araya giren destek (Claude Code terminal desteği) teslim eder. Öteki yardımcılarda teslim yolu yok; kuyruğa alıp unutmak yerine baştan söyle.
            if (srow[0].t is { Length: > 0 } tool && !tool.Equals(Soz.Core.Agents.AgentRegistry.HookSessionTool, StringComparison.OrdinalIgnoreCase))
            {
                var helper = Soz.Core.Assistants.AssistantCatalog.All.FirstOrDefault(x => x.Id.Equals(tool, StringComparison.OrdinalIgnoreCase))?.Title ?? tool;
                throw new SozException(409, $"Bu çalışma {helper} ile açık. Söz mesajı şu an yalnız Claude Code pencerelerine bırakabiliyor, {helper} için onu ulaştıracak bir yol yok. Mesajı doğrudan {helper} penceresine yaz.");
            }
        }
        if (projectId is null) throw new SozException(400, "sessionId ya da projectId gerekli.");
        if (c.Scalar("SELECT id FROM projects WHERE id=@p", null, ("@p", projectId)) is null) throw new SozException(404, "Proje bulunamadı.");
        var safeText = Soz.Core.Text.SecretFilter.MaskSecrets(text.Trim());
        c.Exec("INSERT INTO queue(project_id, session_id, text, created_at, origin) VALUES(@p,@s,@t,@n,@o)", null,
            ("@p", projectId), ("@s", sessionId), ("@t", safeText), ("@n", Time.NowIso()), ("@o", origin));
        var id = c.ScalarLong("SELECT last_insert_rowid()");
        // origin ve kısa önizleme olay akışında (Ne oldu) "Telefondan mesaj gönderdin: «…»" diye görünsün diye yüke girer; önizleme sır süzgecinden geçmiş metinden alınır.
        Live.Emit(c, Live.Queue, projectId, new { id, delivered = false, origin, preview = safeText.Length <= 80 ? safeText : safeText[..79] + "…" });
        return c.Query(Select + " WHERE q.id=@id", Map, null, ("@id", id)).First() with { DeliveryNote = DeliveryNote(c, projectId.Value) };
    }

    /// <summary>Kullanıcı ana klasörü (kullanıcı geneli ayar dosyası için). Test sahte klasör verir.</summary>
    public static Func<string?> UserHome { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Mesajın ajana ulaşıp ulaşmayacağına dair sade uyarı. Proje klasörünün ya da kullanıcı genelinin Claude ayar dosyalarında Söz'ün "ajan durunca araya giren" desteği (stop) yoksa
    /// mesaj kuyrukta bekler ama kimse onu ajana vermez. Klasör bilinmiyorsa ya da bir dosya okunamıyorsa bir şey söylenmez (uydurma uyarı yok).
    /// </summary>
    public static string? DeliveryNote(SqliteConnection c, long projectId)
    {
        try
        {
            var path = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
            var files = new List<string>();
            foreach (var f in new[] { "settings.json", "settings.local.json" }) files.Add(Path.Combine(path, ".claude", f));
            if (UserHome() is { Length: > 0 } home) files.Add(Path.Combine(home, ".claude", "settings.json"));
            foreach (var f in files.Where(File.Exists))
            {
                var r = Soz.Core.Hooks.HookHealth.Read("kuyruk", null, null, f, "");
                if (r.ReadError is not null) return null;
                if (r.Hooks.Contains("stop")) return null;
            }
            return "Bu projede Söz'ün, yardımcı durunca mesajı ona ulaştıran terminal desteği kurulu görünmüyor. Mesaj kuyrukta bekler ama yardımcı onu görmeyecek. Projeye terminal desteğini kurman gerekir.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { return null; }
    }

    /// <summary>Bu oturuma (ya da oturumsuz olarak projesine) bekleyen mesajlar. Hiçbir şeyi işaretlemez: işaret, çıktı hazır olunca MarkDelivered ile en sona konur.</summary>
    public static List<QueueItemDto> Pending(SqliteConnection c, long sessionId, long? projectId)
        => c.Query(Select + """
             WHERE q.delivered_at IS NULL AND (q.session_id=@s OR (q.session_id IS NULL AND @p IS NOT NULL AND q.project_id=@p))
             ORDER BY q.id
            """, Map, null, ("@s", sessionId), ("@p", projectId));

    /// <summary>Mesajları teslim edildi işaretler (tek işlemde). Çağıran bunu ajana giden çıktıyı kurduktan SONRA yapar: "iletildi ama ulaşmadı" olmasın.</summary>
    public static void MarkDelivered(SqliteConnection c, long sessionId, IReadOnlyList<QueueItemDto> items)
    {
        if (items.Count == 0) return;
        using (var tx = c.BeginTransaction())
        {
            var now = Time.NowIso();
            foreach (var i in items) c.Exec("UPDATE queue SET delivered_at=@n, session_id=COALESCE(session_id,@s) WHERE id=@id AND delivered_at IS NULL", tx, ("@n", now), ("@s", sessionId), ("@id", i.Id));
            tx.Commit();
        }
        // Canlı bildirim yan etkidir; patlarsa teslim işareti geri alınmaz ve ajan çıktısı etkilenmez.
        foreach (var i in items)
        {
            try { Live.Emit(c, Live.Queue, i.ProjectId, new { id = i.Id, delivered = true }); }
            catch (Exception) { }
        }
    }
}
