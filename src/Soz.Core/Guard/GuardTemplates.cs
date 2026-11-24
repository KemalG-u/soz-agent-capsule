using System.Text.Json.Nodes;

namespace Soz.Core.Guard;

public sealed record TemplateRule(string Kind, string Label, string Action, JsonObject Spec);
public sealed record GuardTemplate(string Id, string Label, string Description, IReadOnlyList<TemplateRule> Rules);

/// <summary>Hazır şablonlar (sözleşme 4/M4). Kalıp sözdizimi: "komut|takma-ad alt-komut -bayrak|--uzun-bayrak"; kısa bayrak (-r) -rf kümesi içinde de aranır.</summary>
public static class GuardTemplates
{
    private static JsonObject Pats(params string[] p) => new() { ["patterns"] = new JsonArray(p.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
    private static JsonObject Globs(params string[] g) => new() { ["globs"] = new JsonArray(g.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };

    private static readonly TemplateRule ScopeOnly = new(GuardKinds.ScopeOnly, "Proje dışına yazma yasak", "deny", new JsonObject { ["allowTemp"] = true });
    private static readonly TemplateRule BulkDelete = new(GuardKinds.AskBefore, "Toplu silme önce sorulur", "ask", WithRegenerable(Pats(
        "rm -r|--recursive", "rm|ri|remove-item|del|erase|rd|rmdir -recurse|-r", "del|erase|rd|rmdir /s", "find -delete", "git clean -f|--force",
        "rimraf", "robocopy /mir|/purge")));

    /// <summary>Proje içindeki node_modules, bin, obj, dist, .next, target, __pycache__ silinirken sorma.</summary>
    private static JsonObject WithRegenerable(JsonObject o) { o["allowRegenerable"] = true; return o; }
    private static readonly TemplateRule ForcePush = new(GuardKinds.AskBefore, "Zorla gönderme ve sert sıfırlama önce sorulur", "ask", Pats(
        "git push --force|-f|--force-with-lease|--force-if-includes", "git reset --hard",
        // git push origin +main / +HEAD:main: baştaki + zorla göndermedir
        @"re:^git\s(?:.*\s)?push\s(?:.*\s)?\+\S"));
    private static readonly TemplateRule DiscardChanges = new(GuardKinds.AskBefore, "Çalışma kopyasını sıfırlayan git komutları önce sorulur", "ask", Pats(
        "git checkout .", "git restore .", "git reset --hard", "git clean -f|--force"));
    /// <summary>
    /// Söz'ün kendi kayıtları ve ajan ayarları: ajan ölçütü, kapıyı ya da bekçiyi kurcalayıp "geçti" alamasın (aynı kullanıcı hesabında tam engel yoktur;
    /// amaç fark etmek ve göstermek: değişiklikler Söz'de olay olarak da yazılır).
    /// </summary>
    private static readonly TemplateRule SozProtect = new(GuardKinds.PathProtect, "Söz kayıtlarına ve yardımcı ayar dosyalarına yazma yasak", "deny", Globs(
        GuardEngine.SozDataGlob, "soz.db", "soz.db-*", "**/.claude/settings.json", "**/.claude/settings.local.json", "~/.claude/settings.json", "~/.claude/settings.local.json"));
    private static readonly TemplateRule SozCommands = new(GuardKinds.AskBefore, "Söz ayarlarını değiştiren komutlar önce sorulur", "ask", Pats(
        "soz gate off", "soz checks disable|remove|add", "soz guard apply|add|remove|delete|disable|enable",
        @"rx:^sqlite3?\b.*soz\.db", @"rx:^litecli\b.*soz\.db",
        // Söz API'sine (127.0.0.1) doğrudan istek: ölçüt/kapı/bekçi ayarları API ile de değişir
        @"rx:^(?:curl|wget|invoke-webrequest|iwr|invoke-restmethod|irm)\b.*(?:127\.0\.0\.1|localhost|\[::1\])\S*/api/(?:session|checks|guard|jobs|projects|proof|settings|queue|decisions|phone)"));
    /// <summary>Ajan Söz'ü kendi eliyle kullanamaz: soz komutu, veri klasörü, soz.db ve sunucuya giden her loopback biçimi (denetim 3, ilke 2).</summary>
    private static readonly TemplateRule SozSelf = new(GuardKinds.CommandBlock, SozSelfGuard.Label, "deny", Pats(SozSelfGuard.Pattern));
    private static readonly TemplateRule Publish =new(GuardKinds.AskBefore, "Yayın komutları önce sorulur", "ask", Pats(
        "npm|yarn|pnpm publish", "gh release", "vercel --prod|--production", "cargo publish", "twine upload", "docker push"));
    private static readonly TemplateRule KillProc = new(GuardKinds.CommandBlock, "Süreç öldürme yasak", "deny", Pats(
        "taskkill", "stop-process|spps", "pm2 delete|kill", "kill", "pkill", "killall"));
    private static readonly TemplateRule GlobalInstall = new(GuardKinds.AskBefore, "Genel kurulum önce sorulur", "ask", Pats(
        "npm|pnpm i|install|add -g|--global", "yarn global", "winget install|upgrade", "choco install|upgrade", "scoop install", "apt|apt-get|brew install"));
    private static readonly TemplateRule Disk = new(GuardKinds.CommandBlock, "Disk biçimlendirme yasak", "deny", Pats(
        "format", "diskpart", "format-volume", "clear-disk", "mkfs", "fdisk"));
    private static readonly TemplateRule Protected = new(GuardKinds.PathProtect, "Gizli ve sistem dosyalarına yazma yasak", "deny", Globs(
        ".env", ".env.local", ".env.production", ".env.development", "**/.git/**", "*.pem", "id_rsa*", "id_ed25519*",
        "~/.ssh/**", "~/.aws/**", "C:/Windows/**", "C:/Program Files/**", "C:/Program Files (x86)/**"));

    public static readonly IReadOnlyList<GuardTemplate> All = new[]
    {
        new GuardTemplate("temkinli", "Temkinli başlangıç", "Proje dışına yazmayı ve Söz kayıtlarına/yardımcı ayarlarına yazmayı durdurur. Toplu silme, zorla gönderme, git sıfırlama, yayın ve Söz ayarlarını değiştiren komutları önce sorar. Yardımcının Söz'ü kendi eliyle kullanmasını (soz komutu, veri klasörü, sunucuya doğrudan istek) engeller.",
            new[] { ScopeOnly, BulkDelete, ForcePush, DiscardChanges, Publish, SozProtect, SozCommands, SozSelf }),
        new GuardTemplate("proje-disi-yazma", ScopeOnly.Label, "Proje klasörünün dışına yazma ve silme engellenir.", new[] { ScopeOnly }),
        new GuardTemplate("toplu-silme", BulkDelete.Label, "rm -rf, Remove-Item -Recurse, del /s gibi toplu silmeler önce sorulur.", new[] { BulkDelete }),
        new GuardTemplate("zorla-gonderme", ForcePush.Label, "git push --force ve git reset --hard önce sorulur.", new[] { ForcePush }),
        new GuardTemplate("yayin", Publish.Label, "npm publish, gh release, vercel --prod gibi yayın komutları önce sorulur.", new[] { Publish }),
        new GuardTemplate("surec-oldurme", KillProc.Label, "Çalışan programı zorla kapatan komutlar (taskkill, Stop-Process, pm2 ile silme ya da durdurma) engellenir.", new[] { KillProc }),
        new GuardTemplate("genel-kurulum", GlobalInstall.Label, "Bilgisayara program kuran komutlar (npm i -g, winget, choco) önce sorulur.", new[] { GlobalInstall }),
        new GuardTemplate("disk", Disk.Label, "format ve diskpart engellenir.", new[] { Disk }),
        new GuardTemplate("korunan-yollar", Protected.Label, "Gizli bilgi taşıyan ayar dosyalarına, sürüm geçmişi klasörüne (.git), anahtar dosyalarına ve sistem klasörlerine yazma engellenir.", new[] { Protected }),
    };

    public static GuardTemplate? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));
}
