namespace CpuInterfaceBridge.Diagnostics;

public sealed record MethodLocation(ulong Pc, string MethodIdentity, string? Assembly, ulong NativeOffset);
/// <summary>Mapping producer must supply the package digest and exact method/native point. No interpolation.</summary>
public sealed record CilMapPoint(string PackageSha256, string MethodIdentity, ulong NativeOffset, int CilOffset, string Source);
public sealed record FailureCard(Guid SessionId, ImageIdentity Image, Evidence<string> Outcome,
    Evidence<MethodLocation> Location, string ExactReportReason, ManagedEcall? LastEcall);
public sealed record ReportComparison(bool DifferentImportSessions, Evidence<bool> DifferentRuns,
    Evidence<bool> DifferentImages, bool DifferentPaths, bool DifferentReports, bool SameObservedFailure,
    string Assessment);

public static class DiagnosticQueries
{
    public static CodeRecord FromOffset(string method, ulong imageBase, ulong offset, ulong size, string? assembly = null)
    {
        if (string.IsNullOrWhiteSpace(method) || size == 0) throw new ArgumentException("Method and nonempty code required.");
        ulong start = checked(imageBase + offset);
        return new(method, start, checked(start + size), assembly);
    }
    public static Evidence<MethodLocation> Locate(ulong pc, IEnumerable<CodeRecord> records)
    {
        CodeRecord[] rows = records.Distinct().ToArray();
        if (rows.Any(r => r.End <= r.Start || string.IsNullOrWhiteSpace(r.MethodIdentity)))
            return new(EvidenceState.Contradictory, null, "Invalid code range or identity");
        CodeRecord[] matches = rows.Where(r => r.Start <= pc && pc < r.End).ToArray();
        return matches.Length switch
        {
            0 => Evidence<MethodLocation>.Missing("No matching supplied code record; table may be partial"),
            1 => Evidence<MethodLocation>.Known(new(pc, matches[0].MethodIdentity, matches[0].Assembly, pc - matches[0].Start), "Supplied code record"),
            _ => new(EvidenceState.Contradictory, null, "Ambiguous overlapping code records")
        };
    }
    public static Evidence<RuntimeType> FindType(DiagnosticSessionV1 session, ulong typeId)
    {
        RuntimeType[] matches = session.Types.Where(t => t.TypeId == typeId).ToArray();
        return matches.Length switch { 0 => Evidence<RuntimeType>.Missing("Type not supplied"),
            1 => Evidence<RuntimeType>.Known(matches[0], "Runner loader-derived runtime type table"),
            _ => new(EvidenceState.Contradictory, null, "Duplicate TypeId") };
    }
    public static Evidence<bool> CompareHandle(DiagnosticSessionV1 session, ulong typeId, ulong actual)
    {
        Evidence<RuntimeType> type = FindType(session, typeId);
        return type.State == EvidenceState.Available ? Evidence<bool>.Known(type.Value!.TypeHandle == actual,
            $"Runtime TypeId={typeId}: expected=0x{type.Value.TypeHandle:x}, actual=0x{actual:x}") : new(type.State, default, type.Source);
    }
    public static Evidence<int> CilOffset(DiagnosticSessionV1 session, MethodLocation location, IEnumerable<CilMapPoint>? mapping = null)
    {
        if (mapping is null || session.Image.Verification != DigestVerification.VerifiedAgainstSuppliedBytes)
            return Evidence<int>.Missing("No supplied mapping bound to a byte-verified report image");
        CilMapPoint[] points = mapping.Where(p => p.MethodIdentity == location.MethodIdentity && p.NativeOffset == location.NativeOffset).ToArray();
        if (points.Length == 0) return Evidence<int>.Missing("No exact native/CIL mapping point");
        if (points.Any(p => p.CilOffset < 0 || string.IsNullOrWhiteSpace(p.Source) ||
            !string.Equals(p.PackageSha256, session.Image.ReportedSha256, StringComparison.OrdinalIgnoreCase)) || points.Select(p => p.CilOffset).Distinct().Count() != 1)
            return new(EvidenceState.Contradictory, default, "Unbound, invalid or ambiguous CIL mapping");
        return Evidence<int>.Known(points[0].CilOffset, points[0].Source);
    }
    public static FailureCard Failure(DiagnosticSessionV1 session) => new(session.SessionId, session.Image, session.ExecutionOutcome,
        session.Pc.State == EvidenceState.Available ? Locate(session.Pc.Value, session.CodeRecords) : Evidence<MethodLocation>.Missing("PC absent"),
        session.Reason, session.Ecalls.LastOrDefault());
    public static ReportComparison Compare(DiagnosticSessionV1 first, DiagnosticSessionV1 second) => new(
        first.SessionId != second.SessionId,
        first.RunIdentity.State == EvidenceState.Available && second.RunIdentity.State == EvidenceState.Available
            ? Evidence<bool>.Known(first.RunIdentity.Value != second.RunIdentity.Value, "Supplied run identities") : Evidence<bool>.Missing("Run identities absent"),
        first.Image.ReportedSha256 is not null && second.Image.ReportedSha256 is not null &&
        first.Image.Verification != DigestVerification.Mismatch && second.Image.Verification != DigestVerification.Mismatch
            ? Evidence<bool>.Known(first.Image.ReportedSha256 != second.Image.ReportedSha256, "Reported SHA identities; see each verification state") : Evidence<bool>.Missing("Missing or contradictory SHA identity"),
        first.Image.ReportedPath != second.Image.ReportedPath, first.ReportSha256 != second.ReportSha256,
        first.Reason.Length > 0 && first.Reason == second.Reason && first.ExecutionOutcome == second.ExecutionOutcome && first.Pc == second.Pc,
        "Observations only. A missing failure in the second report does not establish reachability or blocker resolution.");
}
