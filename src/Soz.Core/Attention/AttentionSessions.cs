using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Morning;
using Soz.Core.Proof;

namespace Soz.Core.Attention;

/// <summary>
/// "Benden Beklenen" kutusunun ajan oturumları bölümü (n6). Üç kaynağı TEK öğede birleştirir: terminal kancası (Notification, kesin neden), oturum kaydı (soru, hata) ve
/// dışarıdaki canlı oturum listesi (<see cref="IExternalSessionSource"/>: durum, ne beklediği). Aynı oturum birden çok kaynaktan gelse de tek öğedir; öğe kaynağını ve
/// kaynağın bilgiyi en son gördüğü anı taşır. Çalışıyor diyen taze bir kaynak, daha eski "bekliyor / takıldı" işaretini geçersiz kılar.
/// Sıra (priority): önce seni bekleyenler (izin, bilgi, soru), sonra hata verip duranlar, sonra bitip sınanmayı bekleyenler; her grupta en eski bekleyen üstte.
/// </summary>
public static partial class AttentionBuilder
{
    /// <summary>Kayıttan türeyen "bitirdi" öğelerinden kutuya en çok bu kadarı girer (en yeniler); yüzlerce eski oturum kutuyu boğmasın.</summary>
    public const int MaxDoneSessionItems = 12;
    /// <summary>Dış oturum bilgisi bu saniyeden eskiyse güvenilmez, kutuya girmez.</summary>
    public const double ExternalFreshSeconds = 180;

    private sealed record SessionMeta(long Id, string Tool, string ExternalId, string? FilePath, long? ProjectId, string? ProjectName, string? ProjectPath, string? Title, string? LastAt);

    private static string Key(string tool, string id) => tool.Trim().ToLowerInvariant() + "|" + id;

    private static string NormPath(string? p) => (p ?? "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static List<AttentionItem> SessionItems(SqliteConnection c, NowDto now, DateTime nowUtc, SozSettings st, string nowIso,
        IReadOnlyList<ExternalSession>? external, Func<ExternalSession, DateTime>? externalSince)
    {
        var doneCut = Time.Iso(nowUtc.AddHours(-Math.Max(1, st.ClampedInt("attentionDoneHours"))));
        var metas = c.Query("""
            SELECT s.id, s.tool, s.external_id, s.file_path, s.project_id, p.name, p.path, s.title, s.last_at
            FROM sessions s LEFT JOIN projects p ON p.id=s.project_id
            WHERE (s.kind='human' AND COALESCE(s.last_at, s.last_event_ts, '') >= @since) OR s.needs_user_since IS NOT NULL
            """, r => new SessionMeta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.Str(3), r.LongN(4), r.Str(5), r.Str(6), r.Str(7), r.Str(8)), null, ("@since", Time.Iso(nowUtc.AddHours(-24))))
            .ToDictionary(m => m.Id);
        var metaByKey = new Dictionary<string, SessionMeta>();
        foreach (var m in metas.Values) metaByKey.TryAdd(Key(m.Tool, m.ExternalId), m);
        var skip = MorningBuilder.NotWaitingSessions(c); // Söz'ün yürüttüğü ya da işi bitmiş oturumlar bekleyen sayılmaz

        var extByKey = new Dictionary<string, ExternalSession>();
        foreach (var e in (external ?? Array.Empty<ExternalSession>()).Where(e => (nowUtc - e.ObservedUtc).TotalSeconds <= ExternalFreshSeconds).OrderBy(e => e.ObservedUtc))
            extByKey[Key(e.Tool, e.SessionId)] = e;
        ExternalSession? ExtOf(SessionMeta? m) => m is not null && extByKey.TryGetValue(Key(m.Tool, m.ExternalId), out var e) ? e : null;

        var needsBySession = now.NeedsYou.GroupBy(n => n.SessionId).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<long, AttentionItem>();
        var extra = new List<AttentionItem>(); // oturum kaydı olmayan dış oturumlar

        // ---- 1. hata verip duranlar: kancadan (StopFailure) ya da kaydın sonundaki hata satırından ----
        var failed = new Dictionary<long, (FailureInfo info, string source)>();
        foreach (var n in now.NeedsYou)
            if (FailureProbe.FromHookKind(n.Reason, n.Since) is { } hf) failed[n.SessionId] = (hf with { At = n.Since ?? nowIso }, "hook");
        foreach (var m in metas.Values)
        {
            if (failed.ContainsKey(m.Id) || skip.Contains(m.Id) || m.FilePath is null || string.CompareOrdinal(m.LastAt ?? "", doneCut) < 0) continue;
            var ext = ExtOf(m);
            if (ext is { State: ExternalStates.Working }) continue; // süreç çalışıyor diyorsa hata durması eskidir
            if (FailureProbe.Probe(m.Tool, m.FilePath) is { } pf)
            {
                var at = pf.At ?? m.LastAt ?? nowIso;
                if (string.CompareOrdinal(at, doneCut) >= 0) failed[m.Id] = (pf with { At = at }, "log");
            }
        }

        // ---- 2. kancadan ya da kayıttan "seni bekliyor" diyen oturumlar ----
        foreach (var n in now.NeedsYou)
        {
            if (failed.ContainsKey(n.SessionId)) continue;
            metas.TryGetValue(n.SessionId, out var meta);
            var e = ExtOf(meta);
            var since = Time.Parse(n.Since);
            // Çalışıyor diyen taze süreç bilgisi (işaretten sonra görüldü): izin verilmiş ya da cevap yazılmıştır, eski işaret geçersiz.
            if (e is { State: ExternalStates.Working } && (since is null || e.ObservedUtc > since.Value.AddSeconds(2))) continue;
            var waitingExt = e is { State: ExternalStates.WaitingForYou };
            if (n.Reason == "stalled" && waitingExt) continue; // dış kaynak kesin söylüyor: aşağıda tek öğe olarak girer
            var hookFlag = n.Reason is not (null or "stalled" or "question" or "waiting");
            var title = TitleOf(n.Title, n.ProjectName);
            var at = n.Since ?? nowIso;
            var source = (hookFlag ? "hook" : "log") + (waitingExt ? "+agents" : "");
            var observed = waitingExt ? Time.Iso(e!.ObservedUtc) : at;
            AttentionItem item;
            if (n.Reason == "permission_prompt")
                item = SessionOne(n.SessionId, meta, n.Tool, AttentionKinds.Ask, "session_permission", $"«{title}» yardımcısı bir işlem için senden izin bekliyor. İzni yardımcının açık olduğu pencerede vermelisin.",
                    Clean(e?.WaitingFor, 240), title, n.ProjectName, at, true, source, observed, e, true, false);
            else if (n.Reason is "elicitation_dialog" or "agent_needs_input")
                item = SessionOne(n.SessionId, meta, n.Tool, AttentionKinds.Ask, "session_input", $"«{title}» yardımcısı senden bir bilgi bekliyor. Cevabı yardımcının açık olduğu pencerede yazmalısın.",
                    Clean(e?.WaitingFor, 240), title, n.ProjectName, at, true, source, observed, e, true, false);
            else if (n.Reason == "stalled")
                item = SessionOne(n.SessionId, meta, n.Tool, AttentionKinds.Uncertain, "session_quiet", $"«{title}» uzun süredir sessiz. Takılmış mı çalışıyor mu bilmiyorum.",
                    null, title, n.ProjectName, at, false, source, observed, e, true, false);
            else if (!string.IsNullOrWhiteSpace(n.Question))
                item = SessionOne(n.SessionId, meta, n.Tool, AttentionKinds.Ask, "session_question", $"«{title}» yardımcısı sana bir soru sordu. Cevabı yardımcının açık olduğu pencerede yazmalısın.",
                    Clean(n.Question, 240), title, n.ProjectName, at, true, source, observed, e, true, false);
            else
            {
                // Durdu ama izin/soru işareti yok: "bitti" yalnız yardımcının bitirdiğine dair işaret varsa. Yoksa "sırayı sana verdi" (uncertain), "bitti" denmez.
                var (tk, tr) = TurnEnd.Classify(TurnEnd.ClaimFromSession(c, n.SessionId));
                var said = tr == TurnEnd.DoneReason;
                item = SessionOne(n.SessionId, meta, n.Tool, tk, tr, TurnEnd.Sentence(said, title),
                    null, title, n.ProjectName, at, said, source, observed, e, true, said);
            }
            result[n.SessionId] = WithProveHint(c, item, meta);
        }

        // ---- 3. durumundan emin olunmayanlar (dış kaynak biliyorsa emin olunmayan kalmaz) ----
        foreach (var n in now.Uncertain ?? new())
        {
            if (failed.ContainsKey(n.SessionId) || result.ContainsKey(n.SessionId)) continue;
            metas.TryGetValue(n.SessionId, out var meta);
            if (ExtOf(meta) is { State: ExternalStates.Working or ExternalStates.WaitingForYou }) continue;
            var title = TitleOf(n.Title, n.ProjectName);
            result[n.SessionId] = SessionOne(n.SessionId, meta, n.Tool, AttentionKinds.Uncertain, "session_unsure",
                $"«{title}» yardımcısının durumundan emin değilim: çalışıyor ya da senden izin bekliyor olabilir.", null, title, n.ProjectName, n.Since ?? nowIso, false, "log", n.Since ?? nowIso, null, true, false);
        }

        // ---- 4. hata verip duranlar öğeye dönüşür (aynı oturumun öteki öğesini ezer: durmuş oturum "bitirdi" ya da "izin bekliyor" değildir) ----
        foreach (var (sid, f) in failed)
        {
            metas.TryGetValue(sid, out var meta);
            needsBySession.TryGetValue(sid, out var n);
            var title = TitleOf(n?.Title ?? meta?.Title, n?.ProjectName ?? meta?.ProjectName);
            var tool = n?.Tool ?? meta?.Tool ?? "";
            result[sid] = SessionOne(sid, meta, tool, AttentionKinds.Blocked, "session_failed", $"«{title}» yardımcısı hata yüzünden durdu: {f.info.Cause}.", null, title, n?.ProjectName ?? meta?.ProjectName,
                f.info.At ?? nowIso, true, f.source, f.info.At ?? nowIso, ExtOf(meta), true, false);
        }

        // ---- 5. yalnız dış kaynaktan "seni bekliyor" diyenler (kancası kurulu olmayan oturumlar dahil) ----
        var pathNames = new Lazy<Dictionary<string, string>>(() =>
            c.Query("SELECT path, name FROM projects WHERE path IS NOT NULL AND key<>'*'", r => (p: NormPath(r.Str(0)), n: r.Str(1) ?? ""))
                .Where(x => x.p.Length > 0).GroupBy(x => x.p).ToDictionary(g => g.Key, g => g.First().n));
        foreach (var e in extByKey.Values.Where(e => e.State == ExternalStates.WaitingForYou))
        {
            metaByKey.TryGetValue(Key(e.Tool, e.SessionId), out var meta);
            if (meta is not null && skip.Contains(meta.Id)) continue;
            var sinceUtc = externalSince?.Invoke(e) ?? e.SinceUtc ?? e.ObservedUtc;
            var at = Time.Iso(sinceUtc);
            var observed = Time.Iso(e.ObservedUtc);
            if (meta is not null && result.TryGetValue(meta.Id, out var have))
            {
                if (have.Reason == "session_failed") continue;
                if (have.Reason is "session_permission" or "session_input" or "session_question")
                {
                    // aynı oturum iki kaynaktan: tek öğe, kaynak ikisini de söyler, ne beklediği dış kaynaktan tamamlanır
                    result[meta.Id] = have with
                    {
                        Source = have.Source is { } hs && !hs.Contains("agents") ? hs + "+agents" : have.Source ?? "agents",
                        ObservedAt = string.CompareOrdinal(observed, have.ObservedAt ?? "") > 0 ? observed : have.ObservedAt,
                        Detail = have.Detail ?? Clean(e.WaitingFor, 240),
                        ExternalSessionId = e.SessionId,
                    };
                    continue;
                }
            }
            var projectPath = e.ProjectPath ?? meta?.ProjectPath;
            var projectName = meta?.ProjectName ?? (projectPath is not null && pathNames.Value.TryGetValue(NormPath(projectPath), out var pn) ? pn
                : projectPath is null ? null : Path.GetFileName(projectPath.TrimEnd('\\', '/')));
            var title = TitleOf(e.Title ?? meta?.Title, projectName);
            var permission = e.WaitingFor is { } w && PermissionRe.IsMatch(w);
            var sentence = permission ? $"«{title}» yardımcısı bir işlem için senden izin bekliyor. İzni yardımcının açık olduğu pencerede vermelisin."
                : $"«{title}» yardımcısı senden bir şey bekliyor. Ne beklediğini yardımcının açık olduğu pencerede görürsün.";
            var sid = meta?.Id;
            var id = sid is not null ? $"{AttentionKinds.Ask}:ses:{sid}:{at}" : $"{AttentionKinds.Ask}:ext:{e.Tool}:{e.SessionId}:{at}";
            var actions = new List<AttentionAction>();
            actions.Add(sid is not null ? new("go", "O pencereye git", "POST", $"/api/sessions/{sid}/focus")
                : new("go", "O pencereye git", "POST", "/api/attention/focus", new JsonObject { ["id"] = id }));
            var item = new AttentionItem(id, AttentionKinds.Ask, permission ? "session_permission" : "session_waiting", AttentionKinds.Priority(AttentionKinds.Ask), sentence, Clean(e.WaitingFor, 240), title,
                null, null, sid, projectName, at, "firm", false, false, actions, HelperName(e.Tool), e.SessionId, "agents", observed, null, projectPath);
            if (sid is not null) result[sid.Value] = item; else extra.Add(item);
        }

        // ---- 6. "bitirdi" öğelerinden yalnız en yeni birkaçı ----
        foreach (var reason in new[] { TurnEnd.DoneReason, TurnEnd.TurnEndReason })
        {
            var done = result.Where(kv => kv.Value.Reason == reason).OrderByDescending(kv => kv.Value.At, StringComparer.Ordinal).Skip(MaxDoneSessionItems).Select(kv => kv.Key).ToList();
            foreach (var k in done) result.Remove(k);
        }
        return result.Values.Concat(extra).ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex PermissionRe = new(@"(permission|approve|approval|allow|izin|onay)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string TitleOf(string? title, string? projectName)
        => !string.IsNullOrWhiteSpace(title) ? title! : (string.IsNullOrWhiteSpace(projectName) ? "Yardımcı" : projectName!);

    /// <summary>Oturum öğesi. Eylemler: her öğede "o pencereye git"; "bitirdi" öğesinde ayrıca "bu işi sına" (projesi biliniyorsa).</summary>
    private static AttentionItem SessionOne(long sessionId, SessionMeta? meta, string tool, string kind, string reason, string sentence, string? detail, string title, string? projectName,
        string at, bool firm, string source, string observed, ExternalSession? e, bool withGo, bool withProve)
    {
        var actions = new List<AttentionAction>();
        if (withGo) actions.Add(new("go", "O pencereye git", "POST", $"/api/sessions/{sessionId}/focus"));
        if (withProve && meta?.ProjectId is not null) actions.Add(new("prove", "Bu işi sına", "POST", $"/api/sessions/{sessionId}/prove"));
        var t = meta?.Tool ?? tool;
        return new AttentionItem($"{kind}:ses:{sessionId}:{at}", kind, reason, AttentionKinds.Priority(kind, reason), sentence, detail, title, null, null, sessionId,
            projectName ?? meta?.ProjectName, at, firm ? "firm" : "unknown", false, false, actions, HelperName(t), meta?.ExternalId ?? e?.SessionId, source, observed, null, e?.ProjectPath ?? meta?.ProjectPath);
    }

    /// <summary>
    /// "Bitirdi" öğesinde "bu işi sına" çalışır mı? Proje bilinmiyorsa ya da kayıtlı kontrol yoksa çalışmaz (SessionActions.Prove 409 döner): düğme listeden çıkar,
    /// <c>canProve=false</c> ve nedeni söylenir. Söz'ün notu yardımcının sözüne (<c>Detail</c>) KARIŞMAZ, <c>SozNotu</c> alanına gider.
    /// </summary>
    private static AttentionItem WithProveHint(SqliteConnection c, AttentionItem item, SessionMeta? meta)
    {
        if (item.Reason != TurnEnd.DoneReason) return item;
        var noProve = item.Actions.Where(a => a.Id != "prove").ToList();
        if (meta?.ProjectId is not { } pid) return item with { Actions = noProve, CanProve = false, CanProveReason = "no_project" };
        try
        {
            if (CheckStore.EnabledFor(c, pid, null).Count == 0)
                return item with { Actions = noProve, CanProve = false, CanProveReason = "no_checks", SozNotu = "Bu projede neyle sınayacağımı bilmiyorum." };
            return item with { CanProve = true };
        }
        catch (SqliteException) { }
        return item;
    }
}
