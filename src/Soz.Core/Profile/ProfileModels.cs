using System.Text.Json.Serialization;

namespace Soz.Core.Profile;

public sealed record ProfileEvidence(string Tool, string FileType, string? Path, DateTime ObservedAt, string Kind = "gozlem");
public sealed record ProfileFact(string Field, object? Value, ProfileEvidence Source, DateTime ObservedAt, string Kind);
public sealed record DiskProfile(string Letter, long? SizeBytes, long? FreeBytes, ProfileEvidence Source, DateTime ObservedAt, string Kind = "gozlem");
public sealed record MachineProfile(long? MemoryBytes, string? CpuName, int? PhysicalCores, int? LogicalCores,
    string? GpuName, IReadOnlyList<DiskProfile> Disks, IReadOnlyList<ProfileFact> Facts);
public sealed record ProfileSource(string Tool, int Okunan, int Okunamayan, int Sessions, int FilesRead, int FilesUnchanged,
    ProfileEvidence Source, DateTime ObservedAt, IReadOnlyDictionary<string,int> Reasons, string Kind = "gozlem")
{
    [JsonIgnore] public bool Ok => Okunamayan == 0;
    [JsonIgnore] public int Unreadable => Okunamayan;
}
public sealed record ProfileTool(string Tool, int Sessions, ProfileEvidence Source, DateTime ObservedAt, string Kind = "gozlem");
public sealed record ProjectArea(string Path, int Sessions, int RootSessions, int SubagentSessions, bool HasGit);
public sealed record ProjectProfile(string Name, string Path, string DisplayHint, string Drive, bool MissingFolder,
    IReadOnlyDictionary<string, int> SessionsByTool, DateTime? FirstActivity, DateTime? LastActivity,
    int Sessions7Days, int Sessions30Days, double Score, bool HasGit, bool HasTests,
    int RootSessions, int SubagentSessions, IReadOnlyList<ProjectArea> Areas, bool Soluk,
    IReadOnlyList<ProfileEvidence> Sources, DateTime ObservedAt, string Kind = "cikarim",
    string Status = "belirsiz", int ActiveDays = 0, int? LastQuietDays = null);
public sealed record LiveRelation(string Tool, string SessionId, string OtherTool, string OtherSessionId, string Relation,
    ProfileEvidence Source, DateTime ObservedAt, string Kind);
public sealed record LiveSession(string Tool, string Id, DateTime? LastActivity, string? Level, bool AcikAdayi, string Kind = "cikarim", string? Title = null,
    int? Pid = null, long? Hwnd = null, bool CanFocus = false, string? StatusLabel = null, string Evidence = LiveRules.EvidenceNone,
    string State = LiveRules.StateUnknown, string? WaitingFor = null);
public sealed record LiveProject(string? ProjectPath, IReadOnlyList<LiveSession> Sessions, IReadOnlyList<LiveRelation> Relations,
    DateTime ObservedAt, string Kind = "cikarim");
public sealed record DefaultWeight(string Drive, string Parent, double Score);
public sealed record ProfileDefaults(string? ProjeSurucusu, string? YeniProjeUstKlasoru,
    IReadOnlyList<ProfileEvidence> Sources, DateTime ObservedAt, string Explanation = "Yeterli proje gözlemi yok.",
    IReadOnlyList<DefaultWeight>? Weights = null, string Kind = "cikarim");
public sealed record SessionBreakdown(int Count, int RootSessions, int SubagentSessions, int Sessions7Days, int Sessions30Days);
public sealed record UnassignedProfile(int Count, IReadOnlyDictionary<string, int> Reasons, DateTime ObservedAt, string Kind = "cikarim",
    IReadOnlyList<ProfileEvidence>? Sources = null, IReadOnlyDictionary<string,SessionBreakdown>? ByTool = null,
    IReadOnlyDictionary<string,SessionBreakdown>? ByReason = null, int Sessions7Days = 0, int Sessions30Days = 0);
public sealed record ProfileCounts(int Sessions, int Projects, int Facts);
public sealed record ProfileSnapshot(MachineProfile Machine, IReadOnlyList<ProfileTool> Tools,
    IReadOnlyList<ProjectProfile> Projects, UnassignedProfile Unassigned, IReadOnlyList<LiveProject> Live,
    ProfileDefaults Defaults, ProfileCounts Counts, long ScanMs, IReadOnlyList<ProfileSource> Sources,
    ProcessInventory? Processes = null, DateTime? ScannedAt = null, IReadOnlyList<string>? Warnings = null,
    DateTime? LiveObservedAt = null, string? LiveSource = null, string? LiveNote = null);
