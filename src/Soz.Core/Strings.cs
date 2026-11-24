namespace Soz.Core;

/// <summary>Kullanıcıya görünen metinlerin tek kaynağı (TR birincil, EN ikinci dil).</summary>
public static class Strings
{
    private static readonly Dictionary<string, string> Tr = new()
    {
        ["packet.general"] = "Genel",
        ["packet.header"] = "[Söz defteri · {project}] Kullanıcının daha önce verdiği, hâlâ geçerli kararlar. Bunlara uy. Çelişen istek gelirse önce sor.",
        ["packet.section.rule"] = "Kurallar:",
        ["packet.section.ban"] = "Yasaklar:",
        ["packet.section.decision"] = "Kararlar:",
        ["packet.section.preference"] = "Tercihler:",
        ["hook.repeat"] = "Kullanıcı bu kuralı daha önce {n} kez söyledi: '{text}'. Kalıcı kuraldır, uy.",
        ["explain.disabled"] = "Sade anlat kapalı. Ayarlardan açabilirsin. Kendi Claude aboneliğini kullanır.",
        ["explain.prompt"] = "Şu ajan raporunu kod bilmeyen birine sade Türkçeyle anlat: 1) Sonuç (tek cümle) 2) Gerçek sayılı küçük bir örnek 3) Benden beklenen (yoksa 'bir şey beklenmiyor') 4) Emin olunmayan nokta. Yeni bilgi uydurma.",
        ["explain.nothing"] = "Bu oturumda yardımcının son sözü yok.",
        ["explain.timeout"] = "Sade anlat zaman aşımına uğradı (60 sn).",
        ["explain.failed"] = "Sade anlat çalışmadı: {error}",
        ["export.denied"] = "Bu proje klasörüne yazma kapalı. Yalnız deneme projesine yazılır. Gerçek projeler için Ayarlar'da \"gerçek projelere yazmaya izin ver\" seçeneğini aç.",
        ["export.nopath"] = "Projenin klasör yolu bilinmiyor.",
        ["digest.title"] = "Söz · son {hours} saat",
        ["digest.waiting"] = "Seni bekleyenler",
        ["digest.stalled"] = "Takılmış olabilir",
        ["digest.uncertain"] = "Emin değilim (çalışıyor ya da izin bekliyor olabilir)",
        ["digest.working"] = "Çalışanlar",
        ["digest.idle"] = "Duranlar",
        ["digest.youLast"] = "Sen en son",
        ["digest.agentLast"] = "Yardımcının son sözü",
        ["digest.empty"] = "Bu sürede etkin oturum yok.",
        ["digest.sub"] = "{n} alt iş",
        ["flag.asksQuestion"] = "soru soruyor",
        ["flag.claimsDone"] = "bitti diyor",
        ["flag.noEvidence"] = "bitti dedi, kanıt göstermedi",
        ["flag.showsEvidence"] = "kanıt var",
        ["ago.sec"] = "az önce",
        ["ago.min"] = "{n} dk",
        ["ago.hour"] = "{n} sa",
        ["ago.day"] = "{n} gün",
        ["state.working"] = "çalışıyor",
        ["state.waiting"] = "seni bekliyor",
        ["state.stalled"] = "takılmış olabilir",
        ["state.uncertain"] = "emin değilim",
        ["state.idle"] = "durdu",
        ["state.stale"] = "eski",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["packet.general"] = "General",
        ["packet.header"] = "[Söz ledger · {project}] Decisions the user made earlier that still apply. Follow them. If a request conflicts, ask first.",
        ["packet.section.rule"] = "Rules:",
        ["packet.section.ban"] = "Don'ts:",
        ["packet.section.decision"] = "Decisions:",
        ["packet.section.preference"] = "Preferences:",
        ["hook.repeat"] = "The user has said this rule {n} times before: '{text}'. It is a standing rule. Follow it.",
        ["explain.disabled"] = "Plain explain is off. Turn it on in Settings. It uses your own Claude subscription.",
        ["explain.prompt"] = "Explain this agent report in plain language to someone who does not code: 1) Result (one sentence) 2) A small example with real numbers 3) What is expected from me (or 'nothing expected') 4) What is uncertain. Do not invent new information.",
        ["explain.nothing"] = "The agent has no last message in this session.",
        ["explain.timeout"] = "Plain explain timed out (60 s).",
        ["explain.failed"] = "Plain explain failed: {error}",
        ["export.denied"] = "Writing to this project folder is off. Only the demo project is written. Enable \"allow writing to real projects\" in Settings.",
        ["export.nopath"] = "The project folder path is unknown.",
        ["digest.title"] = "Söz · last {hours} hours",
        ["digest.waiting"] = "Waiting for you",
        ["digest.stalled"] = "May be stuck",
        ["digest.uncertain"] = "Not sure (may be running or waiting for permission)",
        ["digest.working"] = "Working",
        ["digest.idle"] = "Idle",
        ["digest.youLast"] = "You last said",
        ["digest.agentLast"] = "Agent's last word",
        ["digest.empty"] = "No active sessions in this period.",
        ["digest.sub"] = "{n} sub-tasks",
        ["flag.asksQuestion"] = "asks a question",
        ["flag.claimsDone"] = "says done",
        ["flag.noEvidence"] = "said done, showed no evidence",
        ["flag.showsEvidence"] = "has evidence",
        ["ago.sec"] = "just now",
        ["ago.min"] = "{n} min",
        ["ago.hour"] = "{n} h",
        ["ago.day"] = "{n} d",
        ["state.working"] = "working",
        ["state.waiting"] = "waiting for you",
        ["state.stalled"] = "may be stuck",
        ["state.uncertain"] = "not sure",
        ["state.idle"] = "idle",
        ["state.stale"] = "stale",
    };

    public static string Get(string? language, string key)
    {
        var d = language == "en" ? En : Tr;
        return d.TryGetValue(key, out var v) ? v : Tr.TryGetValue(key, out var t) ? t : key;
    }
}
