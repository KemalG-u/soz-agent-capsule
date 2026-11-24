using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Text;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>Komut: projede bir komut koşar, çıkış koduna ve (varsa) çıktıdaki test özetine bakar. Eski ProofRunner.RunCommand, davranışı aynen.</summary>
public sealed class CommandMeasurer : MeasurerBase
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexSpeed.Compiled);

    public override string Kind => CheckKinds.Command;
    public override string Level => Confidence.A;

    public override string? Validate(JsonObject spec) => string.IsNullOrWhiteSpace(S(spec, "cmd")) ? "command kontrolü için spec.cmd gerekli." : null;

    public override string Describe(JsonObject spec) => S(spec, "cmd") is { } cmd ? $" (komut: {cmd})" : "";

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var cmd = S(c.Spec, "cmd");
        if (string.IsNullOrWhiteSpace(cmd)) return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Komut boş.", null, 0);
        var cwd = ctx.UnderRoot(S(c.Spec, "cwd"));
        if (!Directory.Exists(cwd)) return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Çalışma klasörü yok: " + cwd, null, 0);
        var expect = (int)(L(c.Spec, "expectExit") ?? 0);
        var env = new Dictionary<string, string?> { ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "0" };
        if (ctx.DataDir is not null) { env["SOZ_RUN_ID"] = "olcut"; if (ctx.JobId is { } jid) env["SOZ_JOB_ID"] = jid.ToString(); }

        string file, args;
        if (OperatingSystem.IsWindows()) { file = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; args = "/d /s /c \"" + cmd + "\""; }
        else { file = "/bin/sh"; args = "-c \"" + cmd.Replace("\"", "\\\"") + "\""; }

        using var tree = ProcessTree.Start(file, args, cwd, env, ctx.DataDir);
        var finished = tree.WaitForExit(timeout, out var exit);
        var raw = tree.Output;
        var artifact = ctx.SaveEvidence($"cikti-{c.Id}.txt", SecretFilter.MaskSecrets(Ansi.Replace(raw, "")));
        if (!finished)
            return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null,
                ProofRunner.Tail(raw + $"\n[Söz] Zaman aşımı: {timeout.TotalSeconds:0} sn içinde bitmedi. Başlattığım süreç ağacı kapatıldı. Sonuç bilinmiyor."), artifact, 0);

        var reasons = new List<string>();
        if (exit != expect) reasons.Add($"çıkış kodu {exit} (beklenen {expect})");
        if (S(c.Spec, "mustContain") is { Length: > 0 } must && !raw.Contains(must, StringComparison.OrdinalIgnoreCase)) reasons.Add($"çıktıda \"{must}\" yok");
        if (S(c.Spec, "mustNotContain") is { Length: > 0 } mustNot && raw.Contains(mustNot, StringComparison.OrdinalIgnoreCase)) reasons.Add($"çıktıda \"{mustNot}\" var");
        var cmdKey = ProofRunner.CmdKeyOf(cmd, S(c.Spec, "cwd"));
        var evAll = TestSummary.Evaluate(raw);
        if (reasons.Count == 0 && expect == 0)
        {
            // Çıkış kodu 0 yetmez: çıktıda tanınan bir test özeti "hiç test koşmadı" ya da "hepsi atlandı" diyorsa kanıt değeri yoktur.
            var ev = evAll;
            if (ev.Problem is { } problem)
                return new CheckOutcome(c.Id, c.Label, ResultStatus.Unchecked, exit, ProofRunner.Tail(raw + $"\n[Söz] Doğrulanamadı: {problem}. Çıkış kodu 0 olsa da bu bir \"geçti\" değildir."), artifact, 0, ev.Skipped, problem,
                    ev.Total, ev.Passed, false, cmdKey, "no_tests");
            // Test komutu gibi görünüp özeti okunamayan çıktı: geçti kalır ama kartta not düşülür.
            var unknown = ev.Unrecognized && ProofRunner.LooksLikeTestCommand(cmd);
            if (ev.Skipped > 0)
                return new CheckOutcome(c.Id, c.Label, ResultStatus.Passed, exit, ProofRunner.Tail(raw + $"\n[Söz] {ev.Skipped} test atlandı."), artifact, 0, ev.Skipped, null, ev.Total, ev.Passed, false, cmdKey);
            return new CheckOutcome(c.Id, c.Label, ResultStatus.Passed, exit, ProofRunner.Tail(raw), artifact, 0, 0, null, ev.Total, ev.Passed, unknown, cmdKey);
        }
        var tail = ProofRunner.Tail(raw + (reasons.Count > 0 ? "\n[Söz] Geçmedi: " + string.Join(", ", reasons) : ""));
        return new CheckOutcome(c.Id, c.Label, reasons.Count == 0 ? ResultStatus.Passed : ResultStatus.Failed, exit, tail, artifact, 0, evAll.Skipped, null, evAll.Total, evAll.Passed, false, cmdKey);
    }
}
