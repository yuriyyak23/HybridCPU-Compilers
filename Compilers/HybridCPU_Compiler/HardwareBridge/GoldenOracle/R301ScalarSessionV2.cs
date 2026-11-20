// R3-01 opt-in scalar execution receipt. Does not authenticate Golden source or qualify RTL.
// V1 observation/vector JSON and all existing consumers remain unchanged.
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

namespace HybridCPU.Compiler.HardwareBridge.GoldenOracle;

public sealed record R301ScalarSessionV2(
    string Schema, string Scope, string Status,
    string HostAssemblySha256, string HostAssemblyName,
    string HostModuleVersionId, string HostMethodId, int HostMethodToken,
    GoldenBoundVectorsV1 BoundVectors, string ReplayIdentity)
{
    public const string SchemaId = "hybridcpu.hardwarebridge.r301-scalar-session/v2";
    public const string ScalarScope = "explicit-delegate-observation-only";
    public byte[] ToJsonUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);
}

/// <summary>
/// One-way opt-in capture: the caller must supply a reviewed static delegate; no serialized
/// observation, method name, path, or assembly is ever used to select or load executable code.
/// The captured assembly is the file of the *already loaded* delegate method. Its on-disk
/// bytes and root reflection identity are pinned, not independently tied to the source graph.
/// Runtime observation is unqualified; Core CW-INT-10/11 own all evidence/RTL gates.
/// </summary>
public static class R301ScalarSessionCodecV2
{
    public const int MaxJsonBytes = GoldenBoundVectorsCodecV1.MaxJsonBytes + 65536;
    private static readonly JsonSerializerOptions Strict = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static R301ScalarSessionV2 ObserveAndCapture(
        BridgeSourceSnapshotV1 source,
        IReadOnlyList<ReadOnlyMemory<byte>> originalDocumentBytes,
        ReadOnlyMemory<byte> compiledAssemblyBytes,
        int rootIndex,
        IReadOnlyList<GoldenVectorV1> independentlyRequestedVectors,
        Func<uint, uint, uint> explicitlyReviewedPureMethod,
        string reviewedHostLabel,
        TimeSpan elapsedBudget,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(originalDocumentBytes);
        ArgumentNullException.ThrowIfNull(independentlyRequestedVectors);
        ArgumentNullException.ThrowIfNull(explicitlyReviewedPureMethod);
        // Preflight before copying a caller-owned collection; then bind every call to a
        // frozen snapshot rather than re-reading a mutable vector/source/assembly buffer.
        ValidateInputs(source, originalDocumentBytes, compiledAssemblyBytes,
            rootIndex, independentlyRequestedVectors, reviewedHostLabel, elapsedBudget,
            cancellationToken);
        BridgeSourceSnapshotV1 pinnedSource = FreezeSource(source);
        ReadOnlyMemory<byte>[] pinnedDocuments = originalDocumentBytes
            .Select(d => (ReadOnlyMemory<byte>)d.ToArray()).ToArray();
        byte[] pinnedPe = compiledAssemblyBytes.ToArray();
        GoldenVectorV1[] pinnedVectors = independentlyRequestedVectors.ToArray();
        ValidateInputs(pinnedSource, pinnedDocuments, pinnedPe,
            rootIndex, pinnedVectors, reviewedHostLabel, elapsedBudget,
            cancellationToken);
        HostPin host = InspectLoadedHost(explicitlyReviewedPureMethod, pinnedSource, rootIndex,
            pinnedPe);

        // The only producer path: never accept a caller-supplied observation or after-the-fact cases.
        bool signed = pinnedVectors[0].A.Signed;
        var adapter = TrustedAddWordOracleV1.RegisterExplicitlyTrusted(
            explicitlyReviewedPureMethod, reviewedHostLabel, signed);
        var stopwatch = Stopwatch.StartNew();
        var cases = new GoldenCaseV1[pinnedVectors.Length];
        for (int i = 0; i < cases.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > elapsedBudget)
                throw new TimeoutException("HB-R301: Elapsed budget exhausted before delegate call.");
            GoldenObservationV1 one = adapter.Observe(
                TrustedAddWordOracleV1.AllowlistedAdapterId,
                new[] { pinnedVectors[i] }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Cooperative/post-return budget only; this code cannot terminate a hung delegate.
            if (stopwatch.Elapsed > elapsedBudget)
                throw new TimeoutException("HB-R301: Elapsed budget exhausted after delegate call.");
            if (one.Cases.Count != 1 || one.Cases[0].A != pinnedVectors[i].A ||
                one.Cases[0].B != pinnedVectors[i].B)
                throw new InvalidOperationException("HB-R301: Delegate observation input mismatch.");
            cases[i] = one.Cases[0];
        }
        string status = cases.All(c => c.Status == "Observed") ? "Observed" : "Blocked";
        var observation = new GoldenObservationV1(
            GoldenObservationV1.SchemaId, TrustedAddWordOracleV1.AllowlistedAdapterId,
            reviewedHostLabel, status, Array.AsReadOnly(cases));

        // Detect a changed PE on disk during observation; do not claim memory-loaded PE attestation.
        HostPin after = InspectLoadedHost(explicitlyReviewedPureMethod, pinnedSource, rootIndex,
            pinnedPe);
        if (host != after) throw new InvalidOperationException("HB-R301: Host identity changed.");
        cancellationToken.ThrowIfCancellationRequested();
        var bound = GoldenBoundVectorsCodecV1.Capture(
            pinnedSource, pinnedDocuments, pinnedPe,
            rootIndex, pinnedVectors, observation, cancellationToken);
        var receipt = new R301ScalarSessionV2(
            R301ScalarSessionV2.SchemaId, R301ScalarSessionV2.ScalarScope, status,
            host.AssemblySha256, host.AssemblyName, host.ModuleVersionId,
            host.MethodId, host.MethodToken, bound, "");
        receipt = receipt with { ReplayIdentity = Digest(receipt) };
        if (receipt.ToJsonUtf8().Length > MaxJsonBytes)
            throw new ArgumentException("HB-R301: Session byte budget exceeded.");
        cancellationToken.ThrowIfCancellationRequested();
        return receipt;
    }

    /// <summary>
    /// Strict replay with independently supplied request and an already reviewed/loaded delegate.
    /// Checking the delegate and PE again is not a cryptographic host signature or source proof.
    /// </summary>
    public static R301ScalarSessionV2 ReadAndValidate(
        ReadOnlyMemory<byte> utf8,
        BridgeSourceSnapshotV1 expectedSource,
        ReadOnlyMemory<byte> expectedAssemblyBytes,
        int expectedRootIndex,
        string expectedHostLabel,
        IReadOnlyList<GoldenVectorV1> independentlyRequestedVectors,
        Func<uint, uint, uint> expectedLoadedHostMethod,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expectedLoadedHostMethod);
        ArgumentNullException.ThrowIfNull(independentlyRequestedVectors);
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (utf8.IsEmpty || utf8.Length > MaxJsonBytes || expectedAssemblyBytes.IsEmpty ||
            expectedAssemblyBytes.Length > GoldenBoundVectorsCodecV1.MaxAssemblyBytes ||
            !ValidVectors(independentlyRequestedVectors) || !ValidLabel(expectedHostLabel))
            throw new ArgumentException("HB-R301: Unbounded replay or missing independent inputs.");
        HostPin host = InspectLoadedHost(expectedLoadedHostMethod, expectedSource,
            expectedRootIndex, expectedAssemblyBytes.Span);
        try
        {
            using var json = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicates(json.RootElement);
            R301ScalarSessionV2? receipt =
                JsonSerializer.Deserialize<R301ScalarSessionV2>(utf8.Span, Strict);
            if (receipt is null || receipt.Schema != R301ScalarSessionV2.SchemaId ||
                receipt.Scope != R301ScalarSessionV2.ScalarScope ||
                receipt.Status is not ("Observed" or "Blocked") ||
                receipt.HostAssemblySha256 != host.AssemblySha256 ||
                receipt.HostAssemblyName != host.AssemblyName ||
                receipt.HostModuleVersionId != host.ModuleVersionId ||
                receipt.HostMethodId != host.MethodId ||
                receipt.HostMethodToken != host.MethodToken ||
                receipt.BoundVectors is null ||
                receipt.BoundVectors.Observation is null ||
                receipt.Status != receipt.BoundVectors.Observation.Status ||
                receipt.ReplayIdentity != Digest(receipt) ||
                !utf8.Span.SequenceEqual(receipt.ToJsonUtf8()))
                throw new ArgumentException("HB-R301: Invalid, forged, or noncanonical session.");
            // Strict nested v1 reader additionally binds source/assembly/root/vector order.
            GoldenBoundVectorsCodecV1.ReadAndValidate(
                receipt.BoundVectors.ToJsonUtf8(), expectedSource,
                host.AssemblySha256, expectedRootIndex, expectedHostLabel,
                independentlyRequestedVectors, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return receipt with
            {
                BoundVectors = receipt.BoundVectors with
                {
                    OriginalDocuments = Array.AsReadOnly(receipt.BoundVectors.OriginalDocuments.ToArray()),
                    RequestedVectors = Array.AsReadOnly(receipt.BoundVectors.RequestedVectors.ToArray()),
                    Observation = receipt.BoundVectors.Observation with
                    {
                        Cases = Array.AsReadOnly(receipt.BoundVectors.Observation.Cases.ToArray())
                    }
                }
            };
        }
        catch (JsonException error)
        {
            throw new ArgumentException("HB-R301: Malformed strict session JSON.", nameof(utf8), error);
        }
    }

    private static BridgeSourceSnapshotV1 FreezeSource(BridgeSourceSnapshotV1 source)
    {
        if (!source.HasConsistentDigest())
            throw new ArgumentException("HB-R301: Stale source before freezing.");
        var frozen = source with
        {
            Documents = Array.AsReadOnly(source.Documents.ToArray()),
            References = Array.AsReadOnly(source.References.ToArray()),
            Roots = Array.AsReadOnly(source.Roots.Select(root => root with
            {
                TypeArguments = Array.AsReadOnly(root.TypeArguments.ToArray()),
                MethodArguments = Array.AsReadOnly(root.MethodArguments.ToArray())
            }).ToArray()),
            BodyReferenceIndices = Array.AsReadOnly(source.BodyReferenceIndices.ToArray()),
            MetadataReferenceIndices = Array.AsReadOnly(source.MetadataReferenceIndices.ToArray())
        };
        if (!frozen.HasConsistentDigest())
            throw new ArgumentException("HB-R301: Source changed during freeze.");
        return frozen;
    }

    private static void ValidateInputs(
        BridgeSourceSnapshotV1 source, IReadOnlyList<ReadOnlyMemory<byte>> documents,
        ReadOnlyMemory<byte> assembly, int rootIndex, IReadOnlyList<GoldenVectorV1> vectors,
        string label, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (!source.HasConsistentDigest() || rootIndex < 0 || rootIndex >= source.Roots.Count ||
            documents.Count != source.Documents.Count ||
            assembly.IsEmpty || assembly.Length > GoldenBoundVectorsCodecV1.MaxAssemblyBytes ||
            !ValidVectors(vectors) || !ValidLabel(label) ||
            budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(30))
            throw new ArgumentException("HB-R301: Invalid source/root/vector/host/budget inputs.");
        for (int i = 0; i < documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (documents[i].Length != source.Documents[i].Utf8Bytes ||
                Hash(documents[i].Span) != source.Documents[i].Sha256)
                throw new ArgumentException("HB-R301: Original source bytes mismatch.");
        }
    }

    private static bool ValidVectors(IReadOnlyList<GoldenVectorV1> vectors)
    {
        if (vectors.Count is < 1 or > GoldenBoundVectorsCodecV1.MaxVectors) return false;
        var unique = new HashSet<GoldenVectorV1>();
        foreach (GoldenVectorV1 vector in vectors)
            if (vector is null || vector.A is null || vector.B is null ||
                !vector.A.IsCanonical || !vector.B.IsCanonical ||
                vector.A.Width != 32 || vector.B.Width != 32 ||
                vector.A.Signed != vector.B.Signed || !unique.Add(vector))
                return false;
        return vectors.All(v => v.A.Signed == vectors[0].A.Signed);
    }

    private static HostPin InspectLoadedHost(Func<uint, uint, uint> callback,
        BridgeSourceSnapshotV1 source, int rootIndex, ReadOnlySpan<byte> assemblyBytes)
    {
        if (!source.HasConsistentDigest() || rootIndex < 0 || rootIndex >= source.Roots.Count)
            throw new ArgumentException("HB-R301: Unbound source/root.");
        MethodInfo method = callback.Method;
        BridgeRootSelectorV1 root = source.Roots[rootIndex];
        if (callback.Target is not null || !method.IsStatic || method.IsGenericMethod ||
            root.TypeArguments.Count != 0 || root.MethodArguments.Count != 0 ||
            method.DeclaringType?.FullName != root.TypeName || method.Name != root.MethodName ||
            method.ReturnType != typeof(uint) ||
            !method.GetParameters().Select(p => p.ParameterType)
                .SequenceEqual(new[] { typeof(uint), typeof(uint) }) ||
            (root.MetadataToken.HasValue && method.MetadataToken != root.MetadataToken.Value))
            throw new ArgumentException("HB-R301: Reviewed static AddWord method/root mismatch.");

        Assembly loaded = method.Module.Assembly; // already loaded by the trusted caller
        string name = loaded.GetName().Name ?? "";
        string location = loaded.Location;
        if (name != source.AssemblyName || string.IsNullOrWhiteSpace(location) ||
            !Path.IsPathFullyQualified(location))
            throw new ArgumentException("HB-R301: Host assembly does not match source assembly identity.");
        // A named assembly loaded from a symlink/reparse location is not an accepted pin.
        for (string? segment = Path.GetDirectoryName(location); segment is not null;
             segment = Path.GetDirectoryName(segment))
        {
            if ((File.GetAttributes(segment) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("HB-R301: Reparse/symlink host path refused.");
            string parent = Path.GetDirectoryName(segment) ?? "";
            if (parent == segment || parent.Length == 0) break;
        }
        if ((File.GetAttributes(location) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("HB-R301: Reparse/symlink host file refused.");
        var info = new FileInfo(location);
        if (info.Length is < 1 or > GoldenBoundVectorsCodecV1.MaxAssemblyBytes ||
            info.Length != assemblyBytes.Length)
            throw new ArgumentException("HB-R301: Host file size/pin mismatch.");
        string fileHash = Hash(File.ReadAllBytes(location));
        if (fileHash != Hash(assemblyBytes))
            throw new ArgumentException("HB-R301: Loaded delegate assembly file SHA mismatch.");
        return new HostPin(fileHash, name, method.Module.ModuleVersionId.ToString("D"),
            method.DeclaringType!.FullName + "::" + method.Name, method.MetadataToken);
    }

    private static bool ValidLabel(string? label) =>
        label is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(label) &&
        label.All(c => !char.IsControl(c));

    private static string Digest(R301ScalarSessionV2 receipt) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            receipt.Schema, receipt.Scope, receipt.Status, receipt.HostAssemblySha256,
            receipt.HostAssemblyName, receipt.HostModuleVersionId, receipt.HostMethodId,
            receipt.HostMethodToken, receipt.BoundVectors
        }));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                    throw new ArgumentException("HB-R301: Duplicate JSON member.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray())
                RejectDuplicates(child);
    }

    private sealed record HostPin(
        string AssemblySha256, string AssemblyName, string ModuleVersionId,
        string MethodId, int MethodToken);
}
