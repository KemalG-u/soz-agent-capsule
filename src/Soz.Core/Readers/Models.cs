namespace Soz.Core.Readers;

public static class SessionKinds
{
    public const string Human = "human";
    public const string Automation = "automation";
    public const string Subagent = "subagent";
}

public static class EventKinds
{
    public const string User = "user";
    public const string AssistantText = "assistant_text";
    public const string Tool = "tool";
    public const string Other = "other";
}

/// <summary>Bir okuyucunun bir kök için raporu ("okuyamadım" görünür olsun).</summary>
public sealed class ReadReport
{
    public string Tool { get; set; } = "";
    public string Root { get; set; } = "";
    public bool Found { get; set; }
    public int Files { get; set; }
    public int FilesRead { get; set; }
    public int FilesSkippedUnchanged { get; set; }
    public int SubagentFiles { get; set; }
    public long BytesRead { get; set; }
    public int NewUtterances { get; set; }
    public int NewCompactions { get; set; }
    public int BadLines { get; set; }
    public int Unrecognized { get; set; }
    public int FileErrors { get; set; }
    public long ElapsedMs { get; set; }
    /// <summary>Okunamayan ya da kısmen okunan kaynak için sade Türkçe neden (ör. "tanımadığım sürüm"). Sorun yoksa null.</summary>
    public string? Note { get; set; }
}

public sealed class ReadOptions
{
    /// <summary>Bu zamandan eski dosyalar ve mesajlar atlanır (null = hepsi).</summary>
    public DateTime? SinceUtc { get; set; }
    /// <summary>Durum alanları (son mesajlar) için dosya sonundan okunan pencere.</summary>
    public long StateTailBytes { get; set; } = 4L << 20;
    public int MaxAssistantChars { get; set; } = 8000;
}

public sealed record ParsedUtterance(string Ts, string Text, long Line);

/// <summary>Bir dosyanın bir okumada ürettiği değişiklikler; IngestStore tek işlemde yazar.</summary>
public sealed class FileBatch
{
    public required string Path { get; init; }
    public required string Tool { get; init; }
    public long NewOffset { get; set; }
    public long NewLineNo { get; set; }
    public long Size { get; set; }
    public string? Mtime { get; set; }
    public SessionUpdate? Session { get; set; }
    public List<ParsedUtterance> Utterances { get; } = new();
    public List<string> CompactionTs { get; } = new();
}

/// <summary>Oturum alanları; null olan alan değişmez.</summary>
public sealed class SessionUpdate
{
    public required string Tool { get; init; }
    public required string ExternalId { get; init; }
    public string? Cwd { get; set; }
    public string? Kind { get; set; }
    public bool SawAutomationInput { get; set; }
    public bool SawHumanInput { get; set; }
    public string? ParentExternalId { get; set; }
    public string? Title { get; set; }
    public string? StartedAt { get; set; }
    public string? LastAt { get; set; }
    public string? LastUserText { get; set; }
    public string? LastUserTs { get; set; }
    public string? LastAssistantText { get; set; }
    public string? LastAssistantTs { get; set; }
    public string? LastEventTs { get; set; }
    public string? LastEventKind { get; set; }
    public bool? TurnEnded { get; set; }

    public void Touch(string? ts)
    {
        if (ts is null) return;
        if (StartedAt is null || string.CompareOrdinal(ts, StartedAt) < 0) StartedAt = ts;
        if (LastAt is null || string.CompareOrdinal(ts, LastAt) > 0) LastAt = ts;
    }

    public void Event(string kind, string? ts, bool? turnEnded)
    {
        LastEventKind = kind;
        if (ts is not null) LastEventTs = ts;
        if (turnEnded.HasValue) TurnEnded = turnEnded;
        Touch(ts);
    }
}

public interface ITranscriptReader
{
    string Tool { get; }
    ReadReport Read(IngestStore store, string root, ReadOptions options);
}
