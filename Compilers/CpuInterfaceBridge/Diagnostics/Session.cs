namespace CpuInterfaceBridge.Diagnostics;

public enum EvidenceState { Available, Unavailable, Unsupported, Contradictory }
public sealed record Evidence<T>(EvidenceState State, T? Value, string Source)
{
    public static Evidence<T> Known(T value, string source) => new(EvidenceState.Available, value, source);
    public static Evidence<T> Missing(string source) => new(EvidenceState.Unavailable, default, source);
}
public enum DigestVerification { NotProvided, ReportedOnly, VerifiedAgainstSuppliedBytes, Mismatch, BytesOnlyUnbound }
public sealed record ImageIdentity(string ReportedPath, string? ReportedSha256, string? SuppliedBytesSha256,
    DigestVerification Verification);
public sealed record CodeRecord(string MethodIdentity, ulong Start, ulong End, string? Assembly = null);
public sealed record RuntimeType(ulong TypeId, ulong TypeHandle, string StableIdentity, System.Text.Json.JsonElement Descriptor);
public sealed record ManagedEcall(ulong Operation, ulong Receiver, ulong Argument1, ulong Argument2,
    int Status, int Error, ulong Value, Evidence<string> ProviderReason)
{
    public EvidenceState StatusSupport => Status is >= 0 and <= 8 ? EvidenceState.Available : EvidenceState.Unsupported;
}
public sealed record DiagnosticSessionV1(
    Guid SessionId, Evidence<string> RunIdentity, string Source, string ReportSha256, ImageIdentity Image,
    Evidence<string> LoaderStatus, Evidence<string> ExecutionOutcome, Evidence<string> Qualification,
    Evidence<ulong> Pc, Evidence<ulong> LastRetiredPc, Evidence<int> Safepoints,
    Evidence<int> RootCount, IReadOnlyList<string> GcResultDigests,
    IReadOnlyList<CodeRecord> CodeRecords, IReadOnlyList<RuntimeType> Types,
    IReadOnlyList<ManagedEcall> Ecalls, string Reason, IReadOnlyList<string> Limitations)
{
    public const string Schema = "hybridcpu.bridge.diagnostic-session/v1";
    public EvidenceState TypeTableState { get; init; } = EvidenceState.Unavailable;
    public EvidenceState EcallTableState { get; init; } = EvidenceState.Unavailable;
    public EvidenceState GcDigestsState { get; init; } = EvidenceState.Unavailable;
}
