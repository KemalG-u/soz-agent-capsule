using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Attention;

/// <summary>
/// Oturum durdu ama dışarıdan bir izin ya da soru işareti yok: yardımcı gerçekten "bitti" mi dedi, yoksa yalnız sırayı mı verdi?
/// Ayrım TEK yerde: <see cref="Classify"/> tek bir bool? alır (true: yardımcının "bitti" dediğine dair güvenilir işaret var; false ya da null: yok ya da bilinmiyor).
/// İşaret güvenilir değilse "bitti" denmez: <c>session_turn_end</c> (tür uncertain). Başka bir kaynak (oturum özeti) ileride yalnız bu bool'u besleyebilir.
/// Bugünkü işaret: oturumun son yardımcı mesajı (Söz'ün kendi veritabanındaki kopyası; metin dışarı taşınmaz, yalnız bool çıkar).
/// </summary>
public static class TurnEnd
{
    public const string DoneReason = "session_done", TurnEndReason = "session_turn_end";

    /// <summary>true ise "bitti" (done / session_done), aksi halde "sırayı sana verdi" (uncertain / session_turn_end).</summary>
    public static (string Kind, string Reason) Classify(bool? claimedDone)
        => claimedDone == true ? (AttentionKinds.Done, DoneReason) : (AttentionKinds.Uncertain, TurnEndReason);

    public static string Sentence(bool done, string title)
        => done ? $"«{title}» yardımcısı işini bitirdi ve seni bekliyor."
            : $"«{title}» durdu ve sırayı sana verdi. Bitirdi mi, senden bir şey mi bekliyor, bilmiyorum.";

    // Bitiş kalıbı: yalnız net "bitirdim / tamamlandı / testler geçti" türü sözler. "hazır", "tamam" gibi gevşek sözler sayılmaz.
    private static readonly Regex DoneRe = new(@"\b(bitti|bitirdim|bitmistir|tamamlandi|tamamladim|tamamlanmistir|is tamam|done|completed|finished|all tests pass(ed)?|tum testler gecti|testler gecti)\b", RegexSpeed.Compiled);
    // Bunlardan biri son paragrafta varsa bitti sayılmaz: soru, onay isteği, plan, ara rapor, sıradaki iş.
    private static readonly Regex NotDoneRe = new(@"(\?|\bonay|\bapprov|\bplan\b|\bplani\b|\bistersen\b|\bister misin\b|\bdevam edeyim\b|\bshould i\b|\bdo you want\b|\bwould you like\b|\blet me know\b|\bbekliyorum\b|\bwaiting for\b|\bkalan\b|\bremaining\b|\bsonraki\b|\bnext step\b|\bsimdilik\b|\bso far\b|\basama\b|\bkismen\b|\bpartial)", RegexSpeed.Compiled);

    /// <summary>Son yardımcı mesaj bir bitiş kalıbı taşıyor ve soru, onay isteği, plan ya da ara rapor işareti taşımıyorsa true. Metin yoksa null (bilinmiyor).</summary>
    public static bool? ClaimsDone(string? lastAssistantText)
    {
        if (string.IsNullOrWhiteSpace(lastAssistantText)) return null;
        var paragraphs = lastAssistantText.Trim().Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var last = TextNormalizer.Fold(paragraphs.Length > 0 ? paragraphs[^1] : lastAssistantText);
        if (last.Length > 600) last = last[^600..];
        return DoneRe.IsMatch(last) && !NotDoneRe.IsMatch(last);
    }

    /// <summary>
    /// Oturumun kendi kaydından işaret: tur kapanmış (turn_ended) ve son söz yardımcıda (kullanıcı ondan sonra yazmamış) ise o son mesaja bakılır.
    /// Aksi halde ya da kayıt yoksa null: bilinmiyor.
    /// </summary>
    public static bool? ClaimFromSession(SqliteConnection c, long sessionId)
    {
        try
        {
            var row = c.Query("SELECT last_assistant_text, last_assistant_ts, last_user_ts, turn_ended FROM sessions WHERE id=@id",
                r => (text: r.Str(0), ats: r.Str(1), uts: r.Str(2), ended: r.Long(3) == 1), null, ("@id", sessionId)).FirstOrDefault();
            if (row.text is null || !row.ended) return null;
            if (row.uts is not null && string.CompareOrdinal(row.ats ?? "", row.uts) < 0) return null; // kullanıcı son sözü söylemiş, bu mesaj eski
            return ClaimsDone(row.text);
        }
        catch (SqliteException) { return null; }
    }
}
