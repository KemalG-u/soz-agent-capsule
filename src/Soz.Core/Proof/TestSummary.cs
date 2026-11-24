using System.Text.RegularExpressions;

namespace Soz.Core.Proof;

/// <summary>
/// Komut çıktısındaki bilinen test aracı özetlerini okur (node:test, jest, vitest, pytest, dotnet test).
/// Amaç: çıkış kodu 0 olsa bile "hiç test koşmadı" ya da "hepsi atlandı" durumunu "geçti" saymamak.
/// Özet bulunamazsa hiçbir şey söylemez (Problem null): o komutlarda davranış değişmez.
/// </summary>
public static class TestSummary
{
    private static readonly RegexOptions O = RegexSpeed.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    public const string NoneRan = "hiç test koşmadı";
    public const string AllSkipped = "testler atlandı";

    /// <summary>Bir araç türünün toplamı. Total = koşması gereken test sayısı (atlananlar dahil).</summary>
    public sealed record Counts(string Tool, int Total, int Passed, int Skipped);

    /// <summary>Problem: null = sorun yok ya da özet tanınmadı. Skipped: atlanan test sayısı (özet tanındıysa).</summary>
    public sealed record Evaluation(string? Problem, int Skipped, IReadOnlyList<Counts> Found)
    {
        /// <summary>Okunan toplam test sayısı (araç özetlerinin toplamı); özet tanınmadıysa null.</summary>
        public int? Total => Found.Count == 0 ? null : Found.Sum(f => f.Total);
        public int? Passed => Found.Count == 0 ? null : Found.Sum(f => f.Passed);
        /// <summary>Özet hiçbir araçtan okunamadı (çıkış kodu 0 olsa da test sayısı bilinmiyor).</summary>
        public bool Unrecognized => Found.Count == 0 && Problem is null;
    }

    // ---- "0 test" kaçakları (denetim 2, Ö-2): özet tanınmasa da bu işaretler hiç test koşmadığını söyler ----
    private static readonly Regex MochaZero = new(@"^[ \t]*0[ \t]+passing", O);
    private static readonly Regex MochaAny = new(@"^[ \t]*[1-9]\d*[ \t]+(?:passing|failing)", O);
    private static readonly Regex GoNoTests = new(@"^\?[ \t]+\S+[ \t]+\[no test files\]", O);
    private static readonly Regex GoOk = new(@"^(?:ok|FAIL|---)[ \t]+\S+", O);
    private static readonly Regex CargoRunning = new(@"^[ \t]*running[ \t]+(\d+)[ \t]+tests?", O);
    private static readonly Regex DotnetNone = new(@"No test is available|Kullanılabilir test yok|test (?:bulunamadı|kullanılamıyor)|Hiç test (?:yok|bulunamadı)", O);
    private static readonly Regex VitestNone = new(@"No test files found", O);
    private static readonly Regex BunZero = new(@"^[ \t]*Ran[ \t]+0[ \t]+tests?", O);
    private static readonly Regex TapZero = new(@"^1\.\.0[ \t]*(?:#.*)?$", O);

    /// <summary>Tanınan "hiç test yok" işareti: tür adı döner, yoksa null.</summary>
    public static string? NoTestMarker(string text)
    {
        if (MochaZero.IsMatch(text) && !MochaAny.IsMatch(text)) return "mocha";
        var goNo = GoNoTests.Matches(text).Count;
        if (goNo > 0 && !GoOk.IsMatch(text)) return "go";
        var runs = CargoRunning.Matches(text);
        if (runs.Count > 0 && runs.Cast<Match>().Sum(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0) == 0) return "cargo";
        if (DotnetNone.IsMatch(text)) return "dotnet";
        if (VitestNone.IsMatch(text)) return "vitest";
        if (BunZero.IsMatch(text)) return "bun";
        if (TapZero.IsMatch(text)) return "tap";
        return null;
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexSpeed.Compiled);

    // node:test ("ℹ tests 5", TAP "# tests 5"); simge bozulsa da (ASCII dışı ya da "?") satır biçimi aynıdır.
    private const string NodePre = @"^(?:ℹ|#|(?:[^\s\w]|[^\x00-\x7F]){1,4})[ \t]+";
    private static readonly Regex NodeTests = new(NodePre + @"tests[ \t]+(\d+)[ \t]*$", O);
    private static readonly Regex NodePass = new(NodePre + @"pass[ \t]+(\d+)[ \t]*$", O);
    private static readonly Regex NodeSkip = new(NodePre + @"(?:skipped|todo)[ \t]+(\d+)[ \t]*$", O);

    // jest: "Tests:       1 skipped, 2 passed, 3 total"
    private static readonly Regex JestLine = new(@"^[ \t]*Tests:[ \t]+(?<body>.*\btotal\b.*)$", O);
    // vitest: "      Tests  2 passed | 1 skipped (3)"
    private static readonly Regex VitestLine = new(@"^[ \t]*Tests[ \t]{2,}(?<body>\d+[ \t]+(?:passed|failed|skipped|todo|pending).*)$", O);

    // pytest: "==== 4 passed, 1 skipped in 0.10s ====", "no tests ran in 0.01s"
    private static readonly Regex PyLine = new(@"^[= \t]*(?<body>(?:\d+[ \t]+(?:passed|failed|skipped|deselected|xfailed|xpassed|errors?|warnings?)(?:[ \t]*,[ \t]*)?)+)[ \t]+in[ \t]+[\d.]+[ \t]*s", O);
    private static readonly Regex PyNone = new(@"^[= \t]*no tests ran\b", O);

    // dotnet test: tek satır "Başarılı!  - Başarısız: 0, Başarılı: 5, Atlanan: 0, Toplam: 5" / "Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5"
    private static readonly Regex DotnetOne = new(@"(?:Total|Toplam)[ \t]*:[ \t]*(?<t>\d+)", O);
    private static readonly Regex DotnetPass = new(@"(?:Passed|Başarılı)[ \t]*:[ \t]*(?<n>\d+)", O);
    private static readonly Regex DotnetSkip = new(@"(?:Skipped|Atlandı|Atlanan)[ \t]*:[ \t]*(?<n>\d+)", O);
    // dotnet test çok satırlı: "Total tests: 4" / "     Passed: 3" / "    Skipped: 1"
    private static readonly Regex DotnetTotalMulti = new(@"^[ \t]*(?:Total tests|Toplam test(?: sayısı)?)[ \t]*:[ \t]*(\d+)[ \t]*$", O);
    private static readonly Regex DotnetPassMulti = new(@"^[ \t]*(?:Passed|Başarılı)[ \t]*:[ \t]*(\d+)[ \t]*$", O);
    private static readonly Regex DotnetSkipMulti = new(@"^[ \t]*(?:Skipped|Atlandı|Atlanan)[ \t]*:[ \t]*(\d+)[ \t]*$", O);

    private static int Num(Regex re, string s, string group)
    {
        var m = re.Match(s);
        return m.Success && int.TryParse(m.Groups[group].Value, out var n) ? n : 0;
    }

    private static int SumOf(Regex re, string text)
    {
        var sum = 0;
        foreach (Match m in re.Matches(text)) if (int.TryParse(m.Groups[1].Value, out var n)) sum += n;
        return sum;
    }

    /// <summary>"3 passed", "1 skipped" gibi sayaçlardan birini alır.</summary>
    private static int Word(string body, string word)
    {
        var sum = 0;
        foreach (Match m in Regex.Matches(body, @"(\d+)[ \t]+" + word + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            if (int.TryParse(m.Groups[1].Value, out var n)) sum += n;
        return sum;
    }

    public static IReadOnlyList<Counts> Parse(string? raw)
    {
        var found = new List<Counts>();
        if (string.IsNullOrWhiteSpace(raw)) return found;
        var text = Ansi.Replace(raw, "").Replace("\r\n", "\n");

        // node:test
        if (NodeTests.IsMatch(text))
        {
            var total = SumOf(NodeTests, text);
            found.Add(new Counts("node:test", total, SumOf(NodePass, text), SumOf(NodeSkip, text)));
        }

        // jest
        int jt = 0, jp = 0, js = 0, jf = 0; var jestSeen = false;
        foreach (Match m in JestLine.Matches(text))
        {
            jestSeen = true;
            var b = m.Groups["body"].Value;
            var total = Word(b, "total");
            var skipped = Word(b, "skipped") + Word(b, "todo");
            var failed = Word(b, "failed");
            var passed = Regex.IsMatch(b, @"\d+[ \t]+passed", RegexOptions.IgnoreCase) ? Word(b, "passed") : Math.Max(0, total - skipped - failed);
            jt += total; js += skipped; jp += passed; jf += failed;
        }
        if (jestSeen) found.Add(new Counts("jest", jt, jp, js));

        // vitest
        int vt = 0, vp = 0, vs = 0; var vitestSeen = false;
        foreach (Match m in VitestLine.Matches(text))
        {
            vitestSeen = true;
            var b = m.Groups["body"].Value;
            var paren = Regex.Match(b, @"\((\d+)\)");
            var passed = Word(b, "passed"); var skipped = Word(b, "skipped") + Word(b, "todo") + Word(b, "pending"); var failed = Word(b, "failed");
            vt += paren.Success ? int.Parse(paren.Groups[1].Value) : passed + skipped + failed;
            vp += passed; vs += skipped;
        }
        if (vitestSeen) found.Add(new Counts("vitest", vt, vp, vs));

        // pytest
        int pt = 0, pp = 0, ps = 0; var pySeen = false;
        foreach (Match m in PyLine.Matches(text))
        {
            pySeen = true;
            var b = m.Groups["body"].Value;
            var passed = Word(b, "passed") + Word(b, "xpassed");
            var skipped = Word(b, "skipped");
            pt += passed + skipped + Word(b, "failed") + Word(b, "errors?") + Word(b, "xfailed");
            pp += passed; ps += skipped;
        }
        if (PyNone.IsMatch(text)) pySeen = true;
        if (pySeen) found.Add(new Counts("pytest", pt, pp, ps));

        // dotnet test
        int dt = 0, dp = 0, ds = 0; var dotnetSeen = false;
        foreach (Match m in Regex.Matches(text, @"^.*$", RegexOptions.Multiline))
        {
            var line = m.Value;
            if (DotnetPass.IsMatch(line) && DotnetOne.IsMatch(line) && !DotnetTotalMulti.IsMatch(line))
            {
                dotnetSeen = true;
                dt += Num(DotnetOne, line, "t"); dp += Num(DotnetPass, line, "n"); ds += Num(DotnetSkip, line, "n");
            }
        }
        if (DotnetTotalMulti.IsMatch(text))
        {
            dotnetSeen = true;
            dt += SumOf(DotnetTotalMulti, text); dp += SumOf(DotnetPassMulti, text); ds += SumOf(DotnetSkipMulti, text);
        }
        if (dotnetSeen) found.Add(new Counts("dotnet", dt, dp, ds));

        return found;
    }

    /// <summary>Çıkış kodu beklenen olan (başarılı görünen) bir komutun çıktısını değerlendirir.</summary>
    public static Evaluation Evaluate(string? raw)
    {
        var found = Parse(raw);
        var skipped = found.Sum(f => f.Skipped);
        if (!string.IsNullOrWhiteSpace(raw) && !found.Any(f => f.Passed > 0))
        {
            var text = Ansi.Replace(raw, "").Replace("\r\n", "\n");
            if (NoTestMarker(text) is not null) return new Evaluation(NoneRan, skipped, found);
        }
        foreach (var f in found)
        {
            if (f.Total <= 0) return new Evaluation(NoneRan, skipped, found);
            if (f.Passed <= 0) return new Evaluation(f.Skipped >= f.Total ? AllSkipped : NoneRan, skipped, found);
        }
        return new Evaluation(null, skipped, found);
    }
}
