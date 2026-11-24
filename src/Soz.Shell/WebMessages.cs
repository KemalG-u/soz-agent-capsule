using System.Text.Json;

namespace Soz.Shell;

internal enum WebMessageKind { Invalid, Esc, Paste, Dictate, PickFolder, ThemeChanged, ResetPanelPlace, GetShellPrefs, SetShellPref, Unknown }

/// <summary>Arayüzden gelen ileti. <see cref="Type"/>: nesne iletisinin türü (Unknown durumunda günlük için).</summary>
internal sealed record WebMessage(
    WebMessageKind Kind,
    string? Text = null,
    string? Type = null,
    string? Family = null,
    string? Mode = null,
    string? Key = null,
    bool? Value = null,
    bool Confirmed = false);

/// <summary>Saf ayrıştırma: WebView2 WebMessageAsJson çıktısı. Dize "soz-esc" eski davranışı korur.</summary>
internal static class WebMessages
{
    public const string EscString = "soz-esc";
    public const string NewJobToPage = "soz:new-job";

    /// <summary>Klasör seçici cevabı: sayfaya {"type":"folder-picked","path":"..."} gider (vazgeçilirse path null).</summary>
    public static string FolderPickedJson(string? path)
        => JsonSerializer.Serialize(new Dictionary<string, string?> { ["type"] = "folder-picked", ["path"] = string.IsNullOrWhiteSpace(path) ? null : path });

    public const string PrefNotifications = "bildirimler", PrefDeleteData = "kaldirirkenVeriyiSil", PrefStartWithWindows = "acilistaBasla";

    /// <summary>Kabuk tercihleri cevabı: {"type":"shell-prefs","bildirimler":b,"kaldirirkenVeriyiSil":b,"acilistaBasla":b}. Kabuk yoksa sayfa bu iletiyi hiç almaz (anahtarlar görünmez).</summary>
    public static string ShellPrefsJson(bool bildirimler, bool kaldirirkenVeriyiSil, bool acilistaBasla)
        => JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = "shell-prefs", [PrefNotifications] = bildirimler, [PrefDeleteData] = kaldirirkenVeriyiSil, [PrefStartWithWindows] = acilistaBasla });

    public static WebMessage Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new WebMessage(WebMessageKind.Invalid);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.String:
                    return root.GetString() == EscString ? new WebMessage(WebMessageKind.Esc) : new WebMessage(WebMessageKind.Unknown, Type: "string");
                case JsonValueKind.Object:
                    if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return new WebMessage(WebMessageKind.Unknown, Type: null);
                    var type = t.GetString();
                    switch (type)
                    {
                        case "paste":
                            return new WebMessage(WebMessageKind.Paste,
                                Text: Str(root, "text"), Type: type);
                        case "dictate":
                            return new WebMessage(WebMessageKind.Dictate, Type: type);
                        case "pick-folder":
                            return new WebMessage(WebMessageKind.PickFolder, Type: type);
                        case "theme-changed":
                            return new WebMessage(WebMessageKind.ThemeChanged,
                                Type: type,
                                Family: Str(root, "family") ?? Str(root, "themeFamily"),
                                Mode: Str(root, "mode") ?? Str(root, "theme"));
                        case "reset-panel-place":
                            return new WebMessage(WebMessageKind.ResetPanelPlace, Type: type);
                        case "get-shell-prefs":
                            return new WebMessage(WebMessageKind.GetShellPrefs, Type: type);
                        case "set-shell-pref":
                            // Anahtar adı ve değer (true/false) zorunlu; başka türlü bozuk sayılır ve işlenmez.
                            if (Str(root, "key") is not { Length: > 0 } key || !root.TryGetProperty("value", out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                return new WebMessage(WebMessageKind.Invalid, Type: type);
                            return new WebMessage(WebMessageKind.SetShellPref, Type: type, Key: key, Value: v.GetBoolean(),
                                Confirmed: root.TryGetProperty("confirmed", out var c) && c.ValueKind == JsonValueKind.True);
                        default:
                            return new WebMessage(WebMessageKind.Unknown, Type: type);
                    }
                default:
                    return new WebMessage(WebMessageKind.Unknown, Type: root.ValueKind.ToString());
            }
        }
        catch (JsonException) { return new WebMessage(WebMessageKind.Invalid); }
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Günlüğe yazılacak güvenli tür metni (kullanıcı metni değil, kısaltılmış).</summary>
    public static string SafeType(string? type) => string.IsNullOrEmpty(type) ? "(yok)" : type.Length > 40 ? type[..40] + "…" : type;
}
