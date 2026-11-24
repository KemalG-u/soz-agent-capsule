using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Packet;
using Soz.Core.Proof;
using Soz.Core.Text;

namespace Soz.Core.Jobs;

/// <summary>İş kartı (M2): konuşma → istem metni (modelsiz) → oturuma bağlanma → kapı hükmüyle bitiş.</summary>
public static class JobStore
{
    public const int RulesBudget = 1200;
    private static readonly Regex Tag = new(@"\(Söz iş #(\d+)\)", RegexSpeed.Compiled);

    private const string Select = """
        SELECT j.id, j.project_id, p.name, j.session_id, j.title, j.raw_text, j.prompt_text, j.source, j.state, j.night, j.restate,
               j.created_at, j.given_at, j.finished_at,
               (SELECT COUNT(*) FROM proof_runs r WHERE r.job_id=j.id AND r.trigger IN ('stop_hook','outer_gate')),
               (SELECT r.verdict FROM proof_runs r WHERE r.job_id=j.id ORDER BY r.id DESC LIMIT 1),
               (SELECT r.id FROM proof_runs r WHERE r.job_id=j.id ORDER BY r.id DESC LIMIT 1), j.agent_state,
               COALESCE(j.accepted_by_user,0), j.start_error, j.question, j.base_note, COALESCE(j.agent,'claude'), j.uid, COALESCE(j.kind,'kod'), COALESCE(j.write_check_first,0)
        FROM jobs j LEFT JOIN projects p ON p.id=j.project_id
        """;

    private static JobDto Map(SqliteDataReader r) => new(r.GetInt64(0), r.Long(1), r.Str(2), r.LongN(3), SecretFilter.MaskSecrets(r.Str(4) ?? ""), r.Str(5) ?? "", r.Str(6) ?? "",
        r.Str(7) ?? "yazi", r.Str(8) ?? JobStates.Draft, r.Long(9) == 1, r.Long(10) == 1, r.Str(11), r.Str(12), r.Str(13), (int)r.Long(14), r.Str(15), r.LongN(16), new(), null, r.Str(17), null, r.Long(18) == 1, r.Str(19), r.Str(20), null, r.Str(21), Agent: r.Str(22) ?? "claude", Uid: r.Str(23), Kind: r.Str(24) ?? DefaultKind, WriteCheckFirst: r.Long(25) == 1);

    /// <summary>İş türü varsayılanı. Tür serbest metindir (kod, yazi, arastirma, tablo, tarayici, dosya-duzeni ...); doğrulama kayıt defterinden gelecek.</summary>
    public const string DefaultKind = "kod";

    /// <summary>Kabul kaydı: olay tablosundaki job_accepted satırından kanal ("arayüz" | "komut satırı"). Kayıt yoksa (veritabanına doğrudan yazılmış) AcceptedUnverified.</summary>
    private static JobDto WithAccepted(SqliteConnection c, JobDto j)
    {
        if (!j.AcceptedByUser) return j;
        string? via = null, proc = null; var found = false;
        try
        {
            var detail = c.Query("SELECT detail FROM events WHERE kind='job_accepted' AND json_extract(detail,'$.jobId')=@j ORDER BY id DESC LIMIT 1", r => r.Str(0), null, ("@j", j.Id)).FirstOrDefault();
            if (detail is not null)
            {
                found = true;
                try { var dn = JsonNode.Parse(detail); via = dn?["via"]?.GetValue<string>(); proc = dn?["process"]?.GetValue<string>(); } catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { }
            }
        }
        catch (SqliteException) { }
        return j with { AcceptedVia = via, AcceptedUnverified = !found, AcceptedProcess = string.IsNullOrEmpty(proc) ? null : proc };
    }

    private static JobDto WithChecks(SqliteConnection c, JobDto j)
        => WithAccepted(c, j) with { Checks = CheckStore.EnabledFor(c, j.ProjectId, j.Id).Select(x => new JobCheckRef(x.Id, x.Label, x.Kind, x.Enabled, x.JobId)).ToList(), Run = Soz.Core.Run.RunStore.InfoFor(c, j.Id),
            PendingApprovals = Soz.Core.Run.RunStore.PendingApprovals(c, j.Id).Select(a => new PendingApprovalDto(a.EventId, a.Summary, a.Detail)).ToList(), Spend = Soz.Core.Run.RunStore.SpendFor(c, j.Id),
            SuggestedChecks = j.WriteCheckFirst ? AgentSuggested(c, j) : null, TestOffer = j.State == JobStates.Passed ? Offer(c, j.Id) : null,
            LastReason = c.Scalar("SELECT reason_code FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", null, ("@j", j.Id)) as string };

    /// <summary>Ajanın bu işte eklediği/düzenlediği test dosyaları için bekleyen "onaylı testlere katayım mı?" teklifi (yoksa ya da cevaplandıysa null).</summary>
    private static TestOfferDto? Offer(SqliteConnection c, long jobId) => OfferFor(c, jobId, false);

    /// <summary>İki ayrı teklif (e3): eklenenler "katayım mı?" diye sorulur, değiştirilenler varsayılan Hayır ve fark özetiyle birlikte. withLines: fark satırları da gelsin (GET test-offer).</summary>
    public static TestOfferDto? OfferFor(SqliteConnection c, long jobId, bool withLines)
    {
        try
        {
            if (EvidenceBase.PendingOffer(c, jobId) is not { } o) return null;
            string? addedS = null, editedS = null;
            if (o.Added > 0)
            {
                var parts = new List<string>();
                if (o.AddedTests > 0) parts.Add($"{o.AddedTests} yeni test");
                if (o.AddedConfigs > 0) parts.Add($"{o.AddedConfigs} test ayarı");
                if (o.AddedHelpers > 0) parts.Add($"{o.AddedHelpers} yardımcı dosya");
                addedS = $"Yardımcı {string.Join(" ve ", parts)} ekledi. {(o.Added == 1 ? "Bunu" : "Bunları")} da onayladığın testlere katayım mı?";
            }
            if (o.Edited > 0)
            {
                var noun = o.EditedConfigs + o.EditedHelpers == 0 ? "testi" : "dosyayı";
                var what = o.EditedFiles.Count == 1 ? o.EditedFiles[0].Sentence
                    : string.Join(", ", o.EditedFiles.Take(3).Select(f => f.Path)) + (o.EditedFiles.Count > 3 ? $" ve {o.EditedFiles.Count - 3} dosya daha" : "");
                editedS = $"Yardımcı onayladığın {o.Edited} {noun} değiştirdi ({what}). Değişikliği görmeden kabul edemezsin.";
            }
            var files = o.Edited == 0 ? null : o.EditedFiles.Select(f => new TestOfferFileDto(f.Path, f.Sentence, withLines ? f.Lines : null)).ToList();
            return new TestOfferDto(o.Added, o.Edited, string.Join(" ", new[] { addedS, editedS }.Where(x => x is not null)), addedS, editedS, o.EditedId, files);
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>"Önce deneme yaz" ile başlayan işte, ajanın yazdığı denemeden Söz'ün bulup önerdiği (kapalı) ölçütler.</summary>
    private static List<SuggestedCheckDto> AgentSuggested(SqliteConnection c, JobDto j)
        => c.Query("SELECT id, label, spec_json FROM checks WHERE project_id=@p AND origin=@o AND enabled=0 AND json_extract(spec_json,'$.suggestedByJob')=@j AND COALESCE(json_extract(spec_json,'$.declined'),0)=0 ORDER BY id",
            r =>
            {
                var spec = CheckStore.ParseSpec(r.Str(2));
                var cmd = spec["cmd"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                var strength = spec["strength"] is JsonValue sv && sv.TryGetValue<string>(out var sk) ? sk : null;
                var note = spec["strengthNote"] is JsonValue nv && nv.TryGetValue<string>(out var nk) ? nk : null;
                var def = spec["defaultAnswer"] is JsonValue dv && dv.TryGetValue<string>(out var dk) ? dk : "evet";
                var rejected = spec["rejected"] is JsonValue rv && rv.TryGetValue<bool>(out var rk) && rk;
                var sentence = rejected ? $"Yardımcı bir deneme yazdı: {cmd}. {note}"
                    : $"Yardımcı bir deneme yazdı: {cmd}. Sen kabul edene kadar ölçülmez. Kabul edersen Söz her işte kendisi çalıştırır.{(note is null ? "" : " " + note)}";
                return new SuggestedCheckDto(r.GetInt64(0), r.Str(1) ?? "", cmd, Confidence.C, Confidence.Label(Confidence.C), sentence, strength, note, def, rejected);
            }, null, ("@p", j.ProjectId), ("@o", CheckStore.AgentTestOrigin), ("@j", j.Id));

    public static JobDto? Get(SqliteConnection c, long id)
    {
        var j = c.Query(Select + " WHERE j.id=@id", Map, null, ("@id", id)).FirstOrDefault();
        return j is null ? null : WithChecks(c, j);
    }

    public static List<JobDto> List(SqliteConnection c, long? projectId, string? state, int limit = 50, int offset = 0)
        => c.Query(Select + " WHERE (@p IS NULL OR j.project_id=@p) AND (@s IS NULL OR j.state=@s) ORDER BY j.id DESC LIMIT @l OFFSET @o", Map, null,
            ("@p", projectId), ("@s", state), ("@l", Math.Clamp(limit, 1, 500)), ("@o", Math.Max(0, offset))).Select(j => WithChecks(c, j)).ToList();

    /// <summary>Aynı süzgeçle toplam iş sayısı (listenin "devamı var" bilgisi için).</summary>
    public static long Count(SqliteConnection c, long? projectId, string? state)
        => c.ScalarLong("SELECT COUNT(*) FROM jobs j WHERE (@p IS NULL OR j.project_id=@p) AND (@s IS NULL OR j.state=@s)", null, ("@p", projectId), ("@s", state));

    /// <summary>İstem metni: (1) ham söz AYNEN, (2) bitti ölçütleri, (3) Defter kuralları (1200), (4) restate cümlesi, son satır iş etiketi.</summary>
    public const string WriteCheckFirstNote = "Önce şunu yap: işe başlamadan önce, bu işin doğru yapıldığını sınayan küçük bir test ya da deneme yaz. Var olan test dosyalarını DEĞİŞTİRME. Denemeyi yeni, ayrı bir dosyaya yaz. Sonunda bu denemeyi hangi komutla çalıştırdığını tek satırla söyle (örnek: npm test).";

    public static string BuildPrompt(SqliteConnection c, long jobId, long projectId, string rawText, bool restate, IReadOnlyList<CheckDto> checks, string language, bool writeCheckFirst = false)
    {
        var sb = new StringBuilder();
        if (writeCheckFirst) sb.Append(WriteCheckFirstNote).Append("\n\n");
        sb.Append(rawText.Trim()).Append("\n\n");
        if (checks.Count > 0)
        {
            sb.Append("Bitti kontrolleri (Söz bu kontrolleri kendisi çalıştıracak, geçmeden iş bitmiş sayılmaz):\n");
            foreach (var ch in checks)
            {
                var detail = MeasurerRegistry.Find(ch.Kind)?.Describe(ch.Spec) ?? "";
                sb.Append("- ").Append(ch.Label).Append(SecretFilter.MaskSecrets(detail)).Append('\n');
            }
            sb.Append('\n');
        }
        else sb.Append("Bitti kontrolü tanımlı değil: Söz bu işi deneyerek doğrulayamayacak.\n\n");

        var packet = PacketBuilder.Build(c, projectId, RulesBudget, language);
        if (packet.Text.Length > 0) sb.Append(packet.Text.Trim()).Append("\n\n");
        if (restate) sb.Append("Başlamadan önce ne anladığını en çok 5 maddeyle yaz ve onayımı bekle.\n\n");
        sb.Append($"(Söz iş #{jobId})");
        return sb.ToString();
    }

    public static JobDto Create(SqliteConnection c, long projectId, string rawText, string source, bool restate, bool night, IReadOnlyList<long> checkIds, string? title, string language, string? agent = null, string? kind = null, bool writeCheckFirst = false)
    {
        using var tx = c.BeginTransaction();
        var now = Time.NowIso();
        var safeRaw = SecretFilter.MaskSecrets(rawText);
        var t = SecretFilter.MaskSecrets(string.IsNullOrWhiteSpace(title) ? TextNormalizer.Truncate(safeRaw, 60) : title.Trim());
        c.Exec("""
            INSERT INTO jobs(project_id, title, raw_text, prompt_text, prompt_hash, source, state, night, restate, created_at, agent, uid, kind, write_check_first)
            VALUES(@p,@t,@r,'','',@s,'taslak',@n,@re,@c,@ag,@u,@kind,@wf)
            """, tx, ("@wf", writeCheckFirst ? 1 : 0), ("@u", Uid.New()), ("@kind", string.IsNullOrWhiteSpace(kind) ? DefaultKind : kind.Trim().ToLowerInvariant()), ("@p", projectId), ("@t", t), ("@r", safeRaw), ("@s", source), ("@n", night ? 1 : 0), ("@re", restate ? 1 : 0), ("@c", now), ("@ag", string.IsNullOrWhiteSpace(agent) ? "claude" : agent.Trim().ToLowerInvariant()));
        var id = c.ScalarLong("SELECT last_insert_rowid()", tx);

        // Seçilen ölçütler: projede kapalı (öneri) olanlar YALNIZ bu iş için açık kopya olarak eklenir; proje varsayılanı değişmez.
        foreach (var cid in checkIds.Distinct())
        {
            var ch = c.Query("SELECT id, project_id, job_id, kind, label, spec_json, timeout_s, enabled, origin, created_at FROM checks WHERE id=@id", CheckStore.Map, tx, ("@id", cid)).FirstOrDefault();
            if (ch is null || ch.ProjectId != projectId) throw new SozException(400, $"Kontrol bu projeye ait değil: {cid}");
            if (ch.Enabled && ch.JobId is null) continue; // zaten her işte geçerli
            CheckStore.Insert(c, tx, projectId, id, ch.Kind, ch.Label, ch.Spec, ch.TimeoutS, enabled: true, origin: "user");
        }

        tx.Commit();

        // PacketBuilder kendi komutlarını işlemsiz çalıştırır; istem işlem bittikten sonra kurulur.
        var checks = CheckStore.EnabledFor(c, projectId, id);
        var prompt = SecretFilter.MaskSecrets(BuildPrompt(c, id, projectId, safeRaw, restate, checks, language, writeCheckFirst));
        c.Exec("UPDATE jobs SET prompt_text=@t, prompt_hash=@h WHERE id=@id", null, ("@t", prompt), ("@h", TextNormalizer.Hash(prompt)), ("@id", id));
        Live.Emit(c, Live.Job, projectId, new { id, state = JobStates.Draft });
        return Get(c, id)!;
    }

    /// <summary>
    /// Henüz başlamamış (taslak) işin istemini güncel ölçütlerle yeniden kurar (taslak sonradan ölçüt aldıysa ajana verilecek metin eski kalmasın).
    /// Taslak dışındaki işe dokunmaz: ajana verilmiş istem ve ölçüt tanımının özeti (checks_hash) değişmez.
    /// </summary>
    public static void RefreshDraftPrompt(SqliteConnection c, long jobId)
    {
        var j = Get(c, jobId);
        if (j is null || j.State != JobStates.Draft) return;
        var prompt = SecretFilter.MaskSecrets(BuildPrompt(c, jobId, j.ProjectId, SecretFilter.MaskSecrets(j.RawText), j.Restate, CheckStore.EnabledFor(c, j.ProjectId, jobId), SozSettings.Load(c).Language, j.WriteCheckFirst));
        c.Exec("UPDATE jobs SET prompt_text=@t, prompt_hash=@h WHERE id=@id AND state='taslak'", null, ("@t", prompt), ("@h", TextNormalizer.Hash(prompt)), ("@id", jobId));
    }

    public static void SetState(SqliteConnection c, long id, string state, long? projectId = null)
    {
        var now = Time.NowIso();
        c.Exec("""
            UPDATE jobs SET state=@s,
              given_at = CASE WHEN @s IN ('verildi','calisiyor') AND given_at IS NULL THEN @n ELSE given_at END,
              finished_at = CASE WHEN @s IN ('gecti','kaldi','iptal','durduruldu','baslatilamadi') THEN @n WHEN @s IN ('verildi','sirada','calisiyor') THEN NULL ELSE finished_at END,
              question = CASE WHEN @s IN ('calisiyor','gecti','kaldi','iptal','sirada') THEN NULL ELSE question END,
              start_error = CASE WHEN @s IN ('calisiyor','sirada','gecti','kaldi') THEN NULL ELSE start_error END,
              accepted_by_user = CASE WHEN @s IN ('calisiyor','sirada','verildi') THEN 0 ELSE accepted_by_user END,
              queued_json = CASE WHEN @s='sirada' THEN queued_json ELSE NULL END,
              seen_at = CASE WHEN @s IN ('verildi','sirada','calisiyor') THEN NULL ELSE seen_at END
            WHERE id=@id
            """, null, ("@s", state), ("@n", now), ("@id", id));
        if (state is JobStates.Given or JobStates.Running) RecordChecksHash(c, id);
        if (state == JobStates.Running) EvidenceBase.TakeForJob(c, id); // kanıt tabanı: işin ilk çalışmaya başladığı an (yalnız bir kez)
        Live.Emit(c, Live.Job, projectId, new { id, state });
        EmitJobState(c, id, state, projectId);
    }

    /// <summary>SSE job.state: arayüz iş durumunu (yeni durumlar dahil) bu olaydan okur. Süreç kaydı varsa en son sürecin durumu da gider.</summary>
    private static void EmitJobState(SqliteConnection c, long id, string state, long? projectId)
    {
        try
        {
            var run = Soz.Core.Run.RunStore.Latest(c, id);
            if (run is not null) { Soz.Core.Run.RunStore.EmitState(c, run.Id); return; }
            Live.Emit(c, Live.JobState, projectId, new { jobId = id, runId = (long?)null, status = (string?)null, jobState = state, lastLine = (string?)null, failReason = (string?)null, exitCode = (int?)null, approval = (object?)null });
        }
        catch (SqliteException) { /* canlı olay kaybı işi bozmaz */ }
    }

    /// <summary>Ajan kullanıcıya soru sordu (Stop kancası ya da süreç sonu): iş cevap bekler, soru metni karta gider.</summary>
    public static void SetAwaitingAnswer(SqliteConnection c, long id, string? question, long? projectId)
    {
        SetState(c, id, JobStates.AwaitingAnswer, projectId);
        c.Exec("UPDATE jobs SET question=@q WHERE id=@id", null, ("@q", string.IsNullOrWhiteSpace(question) ? null : SecretFilter.MaskSecrets(TextNormalizer.Truncate(question.Trim(), 600))), ("@id", id));
    }

    /// <summary>Kullanıcı terminalde cevap yazdı: bu oturumun cevap bekleyen işi çalışıyor durumuna döner.</summary>
    public static void ResumeAnswered(SqliteConnection c, long sessionId, long? projectId)
    {
        foreach (var id in c.Query("SELECT id FROM jobs WHERE session_id=@s AND state='cevap_bekliyor'", r => r.GetInt64(0), null, ("@s", sessionId)))
            SetState(c, id, JobStates.Running, projectId);
    }

    /// <summary>Kullanıcı arayüzde işe baktı (kaldı işi "görülmemiş" sayılmaz).</summary>
    public static void MarkSeen(SqliteConnection c, long id)
        => c.Exec("UPDATE jobs SET seen_at=@n WHERE id=@id", null, ("@n", Time.NowIso()), ("@id", id));

    /// <summary>
    /// İş ajana verilirken (ya da ilk kez çalışmaya başlarken) uygulanacak ölçüt tanımlarının özeti işe yazılır (yalnız bir kez).
    /// Hüküm anında özet farklıysa kapı "geçti" demez (ProofStore.Complete).
    /// </summary>
    private static void RecordChecksHash(SqliteConnection c, long jobId)
    {
        var pid = c.Scalar("SELECT project_id FROM jobs WHERE id=@id AND checks_hash IS NULL", null, ("@id", jobId)) as long?;
        if (pid is null) return;
        c.Exec("UPDATE jobs SET checks_hash=@h WHERE id=@id AND checks_hash IS NULL", null, ("@h", CheckStore.DefinitionHash(c, pid.Value, jobId)), ("@id", jobId));
    }

    /// <summary>
    /// UserPromptSubmit: istem bir işin metniyle (özet ya da "(Söz iş #n)" etiketi) eşleşirse iş o oturuma bağlanır, verildi → calisiyor.
    /// İş yalnız KENDİ projesindeki oturuma bağlanır: oturumun projesi (projectId) işin projesiyle aynı olmalı; proje bilinmiyorsa bağlanmaz.
    /// </summary>
    public static long? LinkOnPrompt(SqliteConnection c, string? prompt, long sessionId, long? projectId)
    {
        if (string.IsNullOrWhiteSpace(prompt) || projectId is null) return null;
        var hash = TextNormalizer.Hash(SecretFilter.MaskSecrets(prompt));
        var rawHash = TextNormalizer.Hash(prompt);
        var id = c.Scalar("SELECT id FROM jobs WHERE prompt_hash IN (@h,@rh) AND project_id=@p AND state IN ('taslak','verildi') ORDER BY id DESC LIMIT 1", null, ("@h", hash), ("@rh", rawHash), ("@p", projectId)) as long?;
        if (id is null)
        {
            var m = Tag.Match(prompt);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var tagged))
                id = c.Scalar("SELECT id FROM jobs WHERE id=@id AND project_id=@p AND state IN ('taslak','verildi','calisiyor','cevap_bekliyor')", null, ("@id", tagged), ("@p", projectId)) as long?;
        }
        if (id is null) return null;
        c.Exec("UPDATE jobs SET session_id=@s WHERE id=@id", null, ("@s", sessionId), ("@id", id));
        SetState(c, id.Value, JobStates.Running, projectId);
        return id;
    }

    /// <summary>
    /// Bu oturumun etkin işi (yalnız kendi istemini alan oturuma bağlanmış iş). Gece işi başka oturuma BAĞLANMAZ:
    /// iş, istemi (özet ya da "(Söz iş #n)" etiketi) UserPromptSubmit'te eşleşen oturuma bağlanır; eşleşme yoksa bağlanmaz.
    /// ProjectId işin projesidir; çağıran oturumun projesiyle karşılaştırır (uyuşmazlık "error" hükmü olur, asla "geçti" değil).
    /// </summary>
    public static (long id, bool night, long projectId, string state)? ActiveFor(SqliteConnection c, long sessionId, long? projectId)
    {
        // Etkin işler önce; yoksa bu oturuma bağlı SON "gecti" iş (K-2): geçtikten sonra aynı oturumda kod bozulursa sonraki Stop yine ölçütleri koşar ve iş "kaldi" olur.
        var j = c.Query("""
            SELECT id, night, project_id, state FROM jobs WHERE session_id=@s AND state IN ('verildi','calisiyor','cevap_bekliyor','onay_bekliyor')
            ORDER BY id DESC LIMIT 1
            """, r => (id: r.GetInt64(0), night: r.Long(1) == 1, projectId: r.Long(2), state: r.Str(3) ?? ""), null, ("@s", sessionId)).FirstOrDefault();
        if (j != default) return j;
        var since = Time.Iso(Time.UtcNow().AddHours(-12));
        j = c.Query("SELECT id, night, project_id, state FROM jobs WHERE session_id=@s AND state='gecti' AND COALESCE(finished_at,'')>=@t ORDER BY id DESC LIMIT 1",
            r => (id: r.GetInt64(0), night: r.Long(1) == 1, projectId: r.Long(2), state: r.Str(3) ?? ""), null, ("@s", sessionId), ("@t", since)).FirstOrDefault();
        return j == default ? null : j;
    }

    /// <summary>Projede bitmemiş gece işi var mı (kapı o iş bitene kadar proje için açık sayılır; iş bu oturuma bağlanmaz).</summary>
    public static bool NightOpen(SqliteConnection c, long? projectId)
        => projectId is not null && c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE project_id=@p AND night=1 AND state IN ('verildi','calisiyor','cevap_bekliyor')", null, ("@p", projectId)) > 0;
}
