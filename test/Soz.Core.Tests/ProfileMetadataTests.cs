using System.Text.Json;
using Soz.Core.Profile;
using Xunit;

namespace Soz.Core.Tests;

public sealed class ProfileMetadataTests
{
    internal static string Fixture()
    {
        var root = Path.Combine(TestEnv.RepoRoot, "_gecici", "p2", "sentetik", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Claude_OnlyMetadata_RealTurkishCwd_ParentAndBoundedPrefix()
    {
        var root = Fixture();
        var cwd = Path.Combine(root, "Seçim Çalışması");
        var dir = Path.Combine(root, "projects", "E--Se-im", "main-session", "subagents");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "agent-child.jsonl");
        var head = JsonSerializer.Serialize(new { cwd, sessionId = "main-session", agentId = "child", gitBranch = "özellik", timestamp = "2026-11-12T09:00:00Z", message = new { content = "PROFILE_PRIVATE_MARKER" } });
        File.WriteAllText(path, head + "\n" + new string('x', SessionMetadataReader.MaxBytes * 3));
        var m = SessionMetadataReader.Read("claude", path, DateTime.UtcNow)!;
        Assert.Equal(cwd, m.Cwd);
        Assert.Equal("main-session", m.ParentId);
        Assert.Equal("child", m.Id);
        Assert.Equal("özellik", m.GitBranch);
        Assert.DoesNotContain("PROFILE_PRIVATE_MARKER", JsonSerializer.Serialize(m));
        Assert.Equal(File.GetLastWriteTimeUtc(path), m.LastActivity);
    }

    [Fact]
    public void Claude_LongFirstLine_FallbackAndMissingCwd_FirstCwdWins()
    {
        var root = Fixture();
        var p = Path.Combine(root,"long.jsonl");
        File.WriteAllText(p, "{\"message\":\"" + new string('m',300*1024) + "\"}\n" + JsonSerializer.Serialize(new {sessionId="id",cwd=@"E:\Çalışma\Seçim",timestamp="2026-11-12T09:00:00Z"}) + "\n" + JsonSerializer.Serialize(new {cwd=@"E:\Başka"}));
        Assert.Equal(@"E:\Çalışma\Seçim",SessionMetadataReader.Read("claude",p,DateTime.UtcNow)!.Cwd);
        // Klasör alanı kayıtta açıkça var ama boş (null): oturum kalır, klasörü bilinmiyor.
        File.WriteAllText(p, "{\"sessionId\":\"id\",\"cwd\":null,\"message\":{\"content\":\"PRIVATE\"}}\n");
        var unknown=SessionMetadataReader.Read("claude",p,DateTime.UtcNow)!;
        Assert.Equal("id",unknown.Id);
        Assert.Null(unknown.Cwd);
        // Klasör alanı hiç yok ama kayıt oturum gibi duruyor (kimlik var): yardımcı biçimi değiştirmiş olabilir, oturum sayılmaz (a6, D6-13).
        File.WriteAllText(p, "{\"sessionId\":\"id\",\"message\":{\"content\":\"PRIVATE\"}}\n");
        Assert.Equal(ProfileSources.ReasonUnrecognizedFormat,Assert.Throws<ProfileReadException>(()=>SessionMetadataReader.Read("claude",p,DateTime.UtcNow)).Reason);
    }

    [Fact]
    public void Codex_ReorderedPayload_ActualIdAndFirstCwd()
    {
        var p=Path.Combine(Fixture(),"rollout-arbitrary.jsonl");
        File.WriteAllText(p,JsonSerializer.Serialize(new {payload=new {id="real-id",cwd=@"E:\Türkçe\Proje",timestamp="2026-11-12T09:00:00Z"},type="session_meta"})+"\n"+
            JsonSerializer.Serialize(new {type="turn_context",payload=new {cwd=@"E:\Başka"}}));
        var m=SessionMetadataReader.Read("codex",p,DateTime.UtcNow)!;
        Assert.Equal("real-id",m.Id);
        Assert.Equal(@"E:\Türkçe\Proje",m.Cwd);
    }

    [Fact]
    public void Claude_MetadataAfter300KiBContent_OnSameLine_IsReadWithoutAllocatingContent()
    {
        var p=Path.Combine(Fixture(),"long-same-line.jsonl");
        var metadata=JsonSerializer.Serialize(new { sessionId="late",cwd=@"E:\Projeler\Türkçe",timestamp="2026-11-12T09:00:00Z" });
        File.WriteAllText(p,"{\"message\":{\"content\":\""+new string('x',300*1024)+"\"},"+metadata[1..]);
        var row=SessionMetadataReader.Read("claude",p,DateTime.UtcNow)!;
        Assert.Equal(@"E:\Projeler\Türkçe",row.Cwd);Assert.Equal("late",row.Id);
    }
}
