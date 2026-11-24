using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Tests;

/// <summary>
/// Sentetik v10 veritabanı üreticisi (gerçek kişisel veri YOK). Dosya: test\sema-ornekleri\v10-ornek.db.
/// Yeniden üretmek için: ortam değişkeni SOZ_SEMA_URET=1 ile VeriZeminiTests.V10_ornek_yeniden_uretilebilir testi koşulur (dosyanın üstüne yazar).
/// Şema, SozDb.MigrateTo(10) ile kurulur (v1-v10 göç adımları v11 öncesiyle aynıdır); satırlar aşağıdaki SQL ile eklenir. Zaman sabittir, çıktı kararlıdır.
/// </summary>
internal static class V10Ornegi
{
    public static string DosyaYolu => Path.Combine(TestEnv.RepoRoot, "test", "sema-ornekleri", "v10-ornek.db");

    /// <summary><paramref name="dataDir"/>\soz.db olarak sürüm 10 örneği üretir.</summary>
    public static void Uret(string dataDir)
    {
        var onceki = Time.UtcNow;
        Time.UtcNow = () => new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            var db = new SozDb(dataDir);
            db.MigrateTo(10);
            using (var c = db.Open())
            {
                c.Exec(Veri);
                c.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
                c.Exec("PRAGMA journal_mode=DELETE;"); // taahhütlü dosya tek parça kalsın (salt okunur açılışta -wal/-shm doğmasın); göç WAL'a kendisi geçer
            }
        }
        finally
        {
            Time.UtcNow = onceki;
            SqliteConnection.ClearAllPools();
        }
    }

    private const string Veri = """
        INSERT INTO projects(id,key,name,path,created_at,gate_enabled,added) VALUES
          (2,'d:/ornek/site','site','D:\ornek\site','2026-10-01T09:00:00.000Z',1,1),
          (3,'d:/ornek/defter','defter','D:\ornek\defter','2026-10-05T10:30:00.000Z',0,1),
          (4,'d:/ornek/eski',NULL,NULL,NULL,0,0);
        INSERT INTO sessions(id,tool,external_id,project_id,file_path,started_at,last_at,kind,title) VALUES
          (1,'claude','s-001',2,'D:\ornek\a.jsonl','2026-10-01T09:05:00.000Z','2026-10-01T10:00:00.000Z','human','Site isi'),
          (2,'codex','s-002',3,'D:\ornek\b.jsonl','2026-10-05T10:40:00.000Z','2026-10-05T11:00:00.000Z','human','Defter isi');
        INSERT INTO utterances(id,session_id,project_id,ts,text,source_line,hash,source,extracted) VALUES
          (1,1,2,'2026-10-01T09:05:10.000Z','Testleri calistir, gecmeden bitti deme.',1,'h1','transcript',1),
          (2,1,2,'2026-10-01T09:06:10.000Z','Proje disina dosya yazma.',2,'h2','transcript',1),
          (3,2,3,'2026-10-05T10:41:00.000Z','Her degisiklikten sonra defteri guncelle.',1,'h3','transcript',1);
        INSERT INTO clusters(id,project_id,canonical,count,session_count,first_ts,last_ts,max_score) VALUES
          (1,2,'Testler gecmeden bitti deme',2,2,'2026-10-01T09:05:10.000Z','2026-10-03T09:05:10.000Z',0.9);
        INSERT INTO candidates(id,utterance_id,project_id,type,text,score,cluster_id) VALUES
          (1,1,2,'rule','Testler gecmeden bitti deme',0.9,1),
          (2,2,2,'ban','Proje disina dosya yazma',0.8,NULL),
          (3,3,3,'rule','Her degisiklikten sonra defteri guncelle',0.7,NULL);
        INSERT INTO decisions(id,project_id,type,text,status,pinned,origin_candidate_id,cluster_id,repeat_count,created_at,updated_at) VALUES
          (1,2,'rule','Testler gecmeden bitti deme.','approved',1,1,1,2,'2026-10-02T08:00:00.000Z','2026-10-02T08:00:00.000Z'),
          (2,2,'ban','Proje disina dosya yazma.','approved',0,2,NULL,1,'2026-10-02T08:01:00.000Z','2026-10-02T08:01:00.000Z'),
          (3,3,'rule','Her degisiklikten sonra defteri guncelle.','proposed',0,3,NULL,1,'2026-10-06T07:00:00.000Z','2026-10-06T07:00:00.000Z'),
          (4,3,'preference','Kisa yaz.','archived',0,NULL,NULL,1,NULL,NULL);
        INSERT INTO jobs(id,project_id,session_id,title,raw_text,prompt_text,prompt_hash,source,state,night,restate,created_at,given_at,finished_at,agent,accepted_by_user) VALUES
          (1,2,1,'Hatayi duzelt','Hatayi duzelt.','Hatayi duzelt.'||char(10)||char(10)||'(Soz is #1)','ph1','yazi','gecti',0,0,'2026-10-02T08:00:00.000Z','2026-10-02T08:01:00.000Z','2026-10-02T09:00:00.000Z','claude',1),
          (2,2,NULL,'Taslak is','Bir sey yap.','Bir sey yap.','ph2','dikte','taslak',0,1,'2026-10-03T08:00:00.000Z',NULL,NULL,'claude',0),
          (3,3,2,'Defteri duzenle','Defteri duzenle.','Defteri duzenle.','ph3','yazi','kaldi',1,0,'2026-10-06T08:00:00.000Z','2026-10-06T08:05:00.000Z','2026-10-06T09:00:00.000Z','codex',0),
          (4,3,NULL,'Eski is','Eski is.','Eski is.','ph4','yazi','iptal',0,0,NULL,NULL,NULL,'claude',0);
        INSERT INTO checks(id,project_id,job_id,kind,label,spec_json,timeout_s,enabled,origin,created_at) VALUES
          (1,2,NULL,'command','Testler gecsin','{"cmd":"npm test","expectExit":0}',120,1,'user','2026-10-01T09:30:00.000Z'),
          (2,2,NULL,'file','Paket dosyasi var','{"path":"package.json"}',120,0,'auto','2026-10-01T09:31:00.000Z'),
          (3,3,3,'url','Sayfa acilsin','{"url":"http://127.0.0.1:3000"}',60,1,'user','2026-10-06T08:00:00.000Z'),
          (4,3,NULL,'command','Derle','{"cmd":"npm run build"}',300,1,'user','2026-10-05T11:00:00.000Z');
        INSERT INTO proof_runs(id,project_id,job_id,session_id,trigger,attempt,started_at,finished_at,verdict,summary,claims_done,shows_evidence) VALUES
          (1,2,1,1,'stop_hook',1,'2026-10-02T08:30:00.000Z','2026-10-02T08:30:20.000Z','failed','1 olcut kaldi',1,0),
          (2,2,1,1,'stop_hook',2,'2026-10-02T08:50:00.000Z','2026-10-02T08:50:15.000Z','passed','Hepsi gecti',1,1),
          (3,3,3,2,'outer_gate',1,'2026-10-06T08:40:00.000Z','2026-10-06T08:40:30.000Z','failed','Sayfa acilmadi',1,0),
          (4,2,NULL,NULL,'manual',1,'2026-10-07T10:00:00.000Z','2026-10-07T10:00:10.000Z','passed','Elle kosuldu',NULL,NULL),
          (5,3,3,2,'stop_hook',2,'2026-10-06T08:55:00.000Z',NULL,NULL,NULL,1,0);
        INSERT INTO proof_results(id,run_id,check_id,label,status,exit_code,output_tail,duration_ms) VALUES
          (1,1,1,'Testler gecsin','failed',1,'1 test kaldi',4200),
          (2,2,1,'Testler gecsin','passed',0,'hepsi gecti',3900),
          (3,3,3,'Sayfa acilsin','failed',NULL,'baglanti reddedildi',1500);
        INSERT INTO guard_rules(id,project_id,kind,label,spec_json,action,enabled,decision_id,origin,created_at) VALUES
          (1,2,'scope_only','Proje disina yazma yasak','{}','deny',1,2,'defter','2026-10-02T08:02:00.000Z'),
          (2,NULL,'command_block','Tehlikeli silme','{"patterns":["rm -rf"]}','deny',1,NULL,'template','2026-10-01T09:00:00.000Z'),
          (3,3,'ask_before','Paket kur','{"patterns":["npm install"]}','ask',0,NULL,'user','2026-10-05T12:00:00.000Z');
        INSERT INTO guard_events(id,at,project_id,tool_session_id,tool,target_summary,rule_id,action,reason) VALUES
          (1,'2026-10-02T08:10:00.000Z',2,'s-001','Bash','rm -rf build',2,'deny','Tehlikeli silme'),
          (2,'2026-10-02T08:11:00.000Z',2,'s-001','Write','D:\baska\x.txt',1,'deny','Proje disi'),
          (3,'2026-10-06T08:20:00.000Z',3,'s-002','Bash','npm install',3,'ask','Paket kur');
        INSERT INTO runs(id,job_id,session_id,pid,status,started,ended,exit_code,last_line,project_id,kind,agent,input_tokens,output_tokens,cost_usd) VALUES
          (1,1,'cs-1',1111,'done','2026-10-02T08:01:00.000Z','2026-10-02T08:29:00.000Z',0,'Bitti',2,'start','claude',1200,800,0.12),
          (2,1,'cs-1',1112,'done','2026-10-02T08:31:00.000Z','2026-10-02T08:49:00.000Z',0,'Duzeltildi',2,'resume','claude',900,500,0.08),
          (3,3,'cx-1',2221,'failed','2026-10-06T08:05:00.000Z','2026-10-06T08:30:00.000Z',1,'Hata',3,'start','codex',3000,1000,NULL),
          (4,3,'cx-1',2222,'stopped','2026-10-06T08:35:00.000Z','2026-10-06T08:36:00.000Z',NULL,'Durduruldu',3,'resume','codex',NULL,NULL,NULL),
          (5,2,NULL,NULL,'failed',NULL,NULL,NULL,NULL,2,'start','claude',NULL,NULL,NULL);
        INSERT INTO run_lines(id,run_id,job_id,at,kind,text,detail) VALUES
          (1,1,1,'2026-10-02T08:02:00.000Z','progress','Dosyalar okunuyor',NULL),
          (2,1,1,'2026-10-02T08:20:00.000Z','agent','Hatayi buldum',NULL),
          (3,3,3,'2026-10-06T08:10:00.000Z','progress','Defter aciliyor',NULL),
          (4,3,3,'2026-10-06T08:29:00.000Z','error','Baglanti hatasi','{"code":7}');
        INSERT INTO run_approvals(id,run_id,job_id,guard_event_id,tool,summary,input_hash,status,created_at,decided_at,detail) VALUES
          (1,3,3,3,'Bash','npm install','hash-1','allowed','2026-10-06T08:20:00.000Z','2026-10-06T08:21:00.000Z',NULL),
          (2,3,3,NULL,'Write','defter.md','hash-2','pending','2026-10-06T08:22:00.000Z',NULL,'yeni dosya');
        INSERT INTO queue(id,project_id,session_id,text,created_at,delivered_at,origin) VALUES
          (1,2,1,'Bir de README guncelle.','2026-10-07T09:00:00.000Z',NULL,'panel');
        INSERT INTO usage_daily(day,tool,model,is_subagent,messages,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens) VALUES
          ('2026-10-02','claude','ornek-model',0,10,1000,500,200,100),
          ('2026-10-06','codex','ornek-model-2',0,4,400,300,0,0);
        INSERT INTO settings(key,value) VALUES ('language','tr'),('gateBudgetSeconds','300'),('_lastImportAt','2026-10-07T09:00:00.000Z');
        INSERT INTO events(id,ts,kind,project_id,detail) VALUES
          (1,'2026-10-01T09:10:00.000Z','import',NULL,'{"NewUtterances":3,"TotalMs":120}'),
          (2,'2026-10-02T08:00:00.000Z','add',2,'1'),
          (3,'2026-10-02T08:00:30.000Z','job',2,'{"id":1,"state":"taslak"}'),
          (4,'2026-10-02T08:01:00.000Z','job_state',2,'{"jobId":1,"status":"running","jobState":"calisiyor"}'),
          (5,'2026-10-02T08:02:00.000Z','job_progress',2,'{"jobId":1,"runId":1,"lineId":1,"kind":"progress","text":"Dosyalar okunuyor"}'),
          (6,'2026-10-02T08:30:00.000Z','proof',2,'{"runId":1,"projectId":2,"jobId":1,"phase":"started"}'),
          (7,'2026-10-02T08:30:20.000Z','proof',2,'{"runId":1,"projectId":2,"jobId":1,"phase":"done","verdict":"failed"}'),
          (8,'2026-10-02T09:00:00.000Z','job_accepted',2,'{"actor":"api","jobId":1,"via":"arayuz","pid":4242,"process":"soz"}'),
          (9,'2026-10-03T09:00:00.000Z','judge',2,'{"question":"Devam edeyim mi?","engine":"kural","answer":"evet","confidence":0.9,"ms":3}'),
          (10,'2026-10-04T09:00:00.000Z','hook_pulse',2,'{"name":"stop","ms":120,"result":"ok"}'),
          (11,'2026-10-05T09:00:00.000Z','queue',2,'{"id":1}'),
          (12,'2026-10-05T09:30:00.000Z','phone_code_renewed',NULL,NULL),
          (13,'2026-10-06T08:00:00.000Z','approve',3,'3'),
          (14,'2026-10-06T08:50:00.000Z','session_needs_user',3,'{"sessionId":2,"kind":"question"}');
        """;
}
