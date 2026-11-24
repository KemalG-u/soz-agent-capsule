using System.Text.Json;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>s1 / B13: telefon yüzeyi bilgisayardaki "Sıra bende" ile aynı tek öncelik tablosunu gösterir.</summary>
public class S1TelefonTests
{
    [Fact]
    public void Telefon_sira_bende_ile_ayni_sirada_ve_ayni_ogeleri_gosterir_ve_sir_maskeler()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NodeSample.Create(env));
        long Job(string text, string state)
        {
            var j = w.CreateJob(pid, text);
            using var c = env.Svc.Db.Open();
            if (state == JobStates.AwaitingAnswer) JobStore.SetAwaitingAnswer(c, j.Id, "Anahtarı sk-ant-api03-TELEFONGIZLI9876543210zyxwvutsrqpon mı kullanayım?", pid);
            else JobStore.SetState(c, j.Id, state, pid);
            return j.Id;
        }
        Job("Bitti işi", JobStates.Passed);
        Job("Kalan iş", JobStates.Failed);
        Job("Karar işi", JobStates.AwaitingDecision);
        Job("Soru işi", JobStates.AwaitingAnswer);

        var bilgisayar = env.Svc.Attention.Snapshot();
        var phone = new PhoneServer(env.Svc, 49556);
        var json = JsonSerializer.Serialize(phone.NowPayload(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

        // Aynı öğeler, aynı sırada, aynı öncelik değerleriyle (telefon kendi sırasını icat etmez).
        Assert.Equal(bilgisayar.Items.Select(i => i.Id), items.Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(bilgisayar.Items.Select(i => i.Priority), items.Select(i => i.GetProperty("priority").GetInt32()));
        Assert.Equal(new[] { "ask", "accept", "blocked", "done" }, items.Select(i => i.GetProperty("kind").GetString()).ToArray());
        Assert.True(items.Select(i => i.GetProperty("priority").GetInt32()).SequenceEqual(items.Select(i => i.GetProperty("priority").GetInt32()).OrderBy(x => x)));
        Assert.Equal(bilgisayar.Sentence, doc.RootElement.GetProperty("sentence").GetString());
        Assert.Equal(bilgisayar.Counts.Total, doc.RootElement.GetProperty("counts").GetProperty("total").GetInt32());
        // Telefona giden metin süzgeçten geçer.
        Assert.DoesNotContain("TELEFONGIZLI", json);
        // Paylaşımlı örnek: /api/attention ile aynı nesne.
        Assert.Same(env.Svc.Attention, env.Svc.Attention);
    }
}
