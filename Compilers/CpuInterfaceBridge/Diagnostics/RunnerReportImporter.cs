using System.Security.Cryptography;
using System.Text.Json;

namespace CpuInterfaceBridge.Diagnostics;

/// <summary>Passive, explicit-stream adapter for the current runner's v1 JSON. Never opens image paths.</summary>
public static class RunnerReportImporter
{
    public const string Schema = "hybridcpu.ise-cpu-backed-image-run/v1";
    public static DiagnosticSessionV1 Import(Stream report, Guid sessionId, string source,
        Stream? packageBytes = null, string? expectedImagePath = null, int maximumReportBytes = 4 * 1024 * 1024)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Explicit session and source required.");
        if (maximumReportBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumReportBytes));
        using MemoryStream buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = report.Read(chunk, 0, chunk.Length)) != 0)
        {
            if (buffer.Length + count > maximumReportBytes) throw new InvalidDataException("Report size limit exceeded.");
            buffer.Write(chunk, 0, count);
        }
        using JsonDocument doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = doc.RootElement;
        CheckDuplicates(root);
        if (Text(root, "schema") != Schema) throw new InvalidDataException("Unsupported runner schema.");
        string path = Text(root, "image");
        if (expectedImagePath is not null && !string.Equals(path, expectedImagePath, StringComparison.Ordinal))
            throw new InvalidDataException("Report image identity differs from expected identity (exact comparison).");
        string status = Text(root, "status");
        string reason = Text(root, "Reason", allowEmpty: true);
        List<string> limits = new List<string> { "Run identity unavailable; SessionId identifies this import, not a guest run.",
            "Code records are partial runner selections; absence does not prove no method exists.",
            "Loader status and qualification are not explicit report fields.",
            "ECALL observations have no per-event provider reason or PC; Reason is preserved verbatim at report level.",
            "GC roots and CIL mappings are not published by this report." };
        string? sha = Optional(root, "imageSha256") is { } digest ? digest.GetString() : null;
        if (sha is not null && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) throw new InvalidDataException("Invalid SHA-256.");
        sha = sha?.ToLowerInvariant();
        string? actual = packageBytes is null ? null : Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        DigestVerification verification = (sha, actual) switch
        {
            (null, null) => DigestVerification.NotProvided,
            (null, _) => DigestVerification.BytesOnlyUnbound,
            (_, null) => DigestVerification.ReportedOnly,
            _ => sha == actual ? DigestVerification.VerifiedAgainstSuppliedBytes : DigestVerification.Mismatch
        };
        if (verification == DigestVerification.Mismatch) limits.Add("Contradictory: supplied package SHA differs from report SHA; do not correlate image evidence.");
        List<CodeRecord> records = new List<CodeRecord>();
        foreach (string key in new[] { "nearbyCodeRecords", "managedCallerNearbyCodeRecords", "filteredCodeRecords" })
            foreach (JsonElement row in Array(root, key)) records.Add(ReadCode(row));
        foreach (string key in new[] { "managedCallerCodeRecord", "diagnosticPcCodeRecord" })
            if (Optional(root, key) is { } row)
            {
                CodeRecord code = ReadCode(row);
                if (Optional(row, "nativeOffset") is { } offset && offset.GetUInt64() >= code.End - code.Start)
                    throw new InvalidDataException("Native offset outside code record.");
                if (key == "managedCallerCodeRecord" && Optional(root, "managedCallerPc") is { } pc &&
                    (pc.GetUInt64() < code.Start || pc.GetUInt64() >= code.End ||
                     (Optional(row, "nativeOffset") is { } no && no.GetUInt64() != pc.GetUInt64() - code.Start)))
                    throw new InvalidDataException("Caller PC/code identity contradiction.");
                records.Add(code);
            }
        RuntimeType[] types = Array(root, "managedTypes").Select(row => new RuntimeType(UInt(row, "TypeId"), UInt(row, "TypeHandle"),
            Text(row, "StableIdentity"), row.Clone())).ToArray();
        foreach (RuntimeType? type in types)
        {
            if (Optional(type.Descriptor, "InstanceSizeBytes") is { } size && size.GetInt32() < 0)
                throw new InvalidDataException("Negative type instance size.");
            if (Optional(type.Descriptor, "Kind") is { } kind && kind.GetByte() > 4)
                limits.Add("Unsupported runtime type kind retained in descriptor.");
            foreach (JsonElement field in Array(type.Descriptor, "InstanceFields"))
            {
                int offset = Required(field, "OffsetBytes").GetInt32();
                int length = Required(field, "SizeBytes").GetInt32();
                if (offset < 0 || length <= 0 || offset > int.MaxValue - length)
                    throw new InvalidDataException("Invalid instance field range.");
            }
        }
        if (types.Any(t => t.TypeId == 0 || t.TypeHandle == 0) || types.GroupBy(t => t.TypeId).Any(g => g.Count() > 1) ||
            types.GroupBy(t => t.TypeHandle).Any(g => g.Count() > 1)) throw new InvalidDataException("Contradictory runtime type table.");
        ManagedEcall[] ecalls = Array(root, "managedObservations").Select(row => new ManagedEcall(UInt(row, "Operation"), UInt(row, "Receiver"),
            UInt(row, "Argument1"), UInt(row, "Argument2"), Required(row, "Status").GetByte(), Required(row, "Error").GetInt32(),
            UInt(row, "Value"), Evidence<string>.Missing("Not serialized per observation by runner/v1"))).ToArray();
        foreach (string key in new[] { "RetiredPipelineCycles", "GcSafepointsObserved" })
            if (Optional(root, key) is { } number && number.GetInt32() < 0) throw new InvalidDataException("Negative " + key);
        foreach (string key in new[] { "ProcessExitEcallsObserved", "LastRetireSequence", "managedCallerPc" })
            if (Optional(root, key) is { } number) _ = number.GetUInt64();
        if (Optional(root, "ProcessExitCode") is { } exit) _ = exit.GetInt32();
        Evidence<string> outcome = new Evidence<string>(new[] { "Completed", "InvalidRequest", "CycleBudgetExceeded", "KernelRejected", "ExecutionFault" }.Contains(status)
            ? EvidenceState.Available : EvidenceState.Unsupported, status, "runner.status (reported, not independently executed)");
        return new(sessionId, Evidence<string>.Missing("runner/v1 has no run identity"), source,
            Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant(), new(path, sha, actual, verification),
            Evidence<string>.Missing("runner/v1 has no explicit loader status"), outcome,
            Evidence<string>.Missing("No qualification evidence supplied"), Number(root, "FinalProgramCounter"), Number(root, "LastRetiredBundlePc"),
            Optional(root, "GcSafepointsObserved") is { } sp ? Evidence<int>.Known(sp.GetInt32(), "runner.GcSafepointsObserved") : Evidence<int>.Missing("Absent"),
            Evidence<int>.Missing("Runner does not serialize roots"), Array(root, "gcResultDigests").Select(x => x.GetString() ?? throw new InvalidDataException("Null GC digest")).ToArray(),
            records.Distinct().ToArray(), types, ecalls, reason, limits.AsReadOnly())
        {
            TypeTableState = Optional(root, "managedTypes") is null ? EvidenceState.Unavailable : EvidenceState.Available,
            EcallTableState = Optional(root, "managedObservations") is null ? EvidenceState.Unavailable : EvidenceState.Available,
            GcDigestsState = Optional(root, "gcResultDigests") is null ? EvidenceState.Unavailable : EvidenceState.Available
        };
    }
    private static CodeRecord ReadCode(JsonElement row)
    {
        CodeRecord result = new CodeRecord(Text(row, "MethodIdentity"), UInt(row, "codeStart"), UInt(row, "codeEnd"));
        if (result.End <= result.Start) throw new InvalidDataException("Empty, reversed or wrapped code range.");
        return result;
    }
    private static Evidence<ulong> Number(JsonElement row, string key) => Optional(row, key) is { } value
        ? Evidence<ulong>.Known(value.GetUInt64(), "runner." + key) : Evidence<ulong>.Missing("Absent runner." + key);
    private static JsonElement Required(JsonElement row, string key) => Optional(row, key) ?? throw new InvalidDataException("Missing " + key);
    private static ulong UInt(JsonElement row, string key) => Required(row, key).GetUInt64();
    private static string Text(JsonElement row, string key, bool allowEmpty = false)
    {
        string? value = Required(row, key).GetString();
        return value is not null && (allowEmpty || !string.IsNullOrWhiteSpace(value)) ? value : throw new InvalidDataException("Invalid " + key);
    }
    private static JsonElement? Optional(JsonElement row, string key) => row.TryGetProperty(key, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;
    private static IEnumerable<JsonElement> Array(JsonElement row, string key) => Optional(row, key) is { } value ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name);
                CheckDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (JsonElement child in node.EnumerateArray()) CheckDuplicates(child);
    }
}
