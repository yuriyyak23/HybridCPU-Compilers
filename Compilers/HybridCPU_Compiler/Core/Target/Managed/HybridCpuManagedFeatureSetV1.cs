using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedWorkstreamSupportV1 : byte
{
    QualifiedDefaultOff = 0,
    ContractOnly = 1,
    Unsupported = 2
}

public enum HybridCpuManagedSafetyLevelV1 : byte
{
    ValueOnlyNoManagedState = 0,
    BasicBlockObjectReferences = 1
}

public sealed record HybridCpuManagedWorkstreamV1(
    string Identity,
    string SchemaId,
    HybridCpuManagedWorkstreamSupportV1 Support,
    bool RuntimeConsumerRequired,
    string Scope,
    string Reason,
    string Digest);

public sealed record HybridCpuManagedFeatureRequirementResultV1(
    bool Supported,
    IReadOnlyList<string> MissingOrUnsupported,
    string Digest);

/// <summary>
/// Closed Phase 25 feature descriptor. It intentionally has no umbrella managed-runtime bit.
/// Compiler metadata never grants runtime execution, GC, publication, commit or retire authority.
/// </summary>
public sealed class HybridCpuManagedFeatureSetV1
{
    public const string SchemaId = "hybridcpu.managed-feature-set/v1";

    public static HybridCpuManagedFeatureSetV1 Default { get; } = new();

    private HybridCpuManagedFeatureSetV1()
    {
        Workstreams = Array.AsReadOnly(new[]
        {
            Workstream("type-layout-object-references", "hybridcpu.managed-type-descriptor/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Runtime-owned deterministic type identity/layout, object-reference CIL flow, explicit null checks and type/static bootstrap registrations.",
                "Heap allocation, GC execution, byrefs, virtual dispatch and default enablement remain unavailable."),
            Workstream("gc-maps-safepoints", "hybridcpu.managed-metadata-finalization/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Exact register/fixed-stack maps at every final call and explicit poll site drive runtime-owned precise STW non-moving mark-sweep after kernel rendezvous.",
                "Interior/byrefs, concurrent and generational collection remain unsupported; multi-context rendezvous qualification is default-off."),
            Workstream("deterministic-allocation", "hybridcpu.managed-heap-allocator/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Runtime-owned deterministic aligned bump/first-fit allocation, non-moving reclamation, collection retry and exact newobj lowering.",
                "No barriers, generations, concurrent or multi-thread allocation, or default enablement is claimed."),
            Workstream("szarray-core", "hybridcpu.managed-szarray/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Exact SZARRAY descriptors, checked allocation/length/int32/reference access and runtime covariance checks through ordinary managed helpers.",
                "Non-zero lower bounds, multidimensional arrays and ldelema/interior references remain unsupported."),
            Workstream("utf16-string-literals", "hybridcpu.managed-string/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Immutable UTF-16 literal materialization, length and character access with deterministic literal identities.",
                "Interning, mutation and broader System.String library behavior are not implied."),
            Workstream("blittable-value-boxing", "hybridcpu.managed-value-type/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Fixed-layout 4/8-byte scalar value types with exact pointer-map-free boxing and unboxing.",
                "Reference-bearing structs and arbitrary aggregate calling conventions remain unsupported."),
            Workstream("static-type-initialization", "hybridcpu.managed-type-initialization/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Ordered single-context exactly-once module/type initialization, static field helpers, recursive observation and sticky failure.",
                "Managed threading and multi-context initialization synchronization remain deferred to Phase 11."),
            Workstream("read-write-barriers", "hybridcpu.managed-runtime-barriers/v1",
                HybridCpuManagedWorkstreamSupportV1.Unsupported, true, "No read/write barrier lowering.",
                "The Phase 03 non-moving no-GC allocator has no proven barrier requirement."),
            Workstream("eh-unwind", "hybridcpu.managed-eh-unwind/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Bounded catch/finally/throw/rethrow admission, final-PC HCEH, final-frame HCW2 and managed-only cross-frame dispatch.",
                "Filters, fault clauses and cross-native transitions remain unsupported; architectural mapping is owned only by the separate fault-trap-integration policy."),
            Workstream("fault-trap-integration", "hybridcpu.trap-abi/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Digest-bound committed-state TrapAbi records, RuntimeKernel classification and runtime-owned precise zero-address read/write mapping to NullReferenceException.",
                "Compiler implicit null checks, execute-fault mapping, illegal/privilege/integrity mapping and any ISE/ISA extension remain unsupported."),
            Workstream("metadata-runtime-lookup", "hybridcpu.managed-code-manager/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "One deterministic HCMM method record per qualified method with HCMG digest and exact code range.",
                "Dynamic generic-context lookup and dynamic registration remain unsupported; exact AOT instances have ordinary distinct method identities."),
            Workstream("bounded-public-reflection", "hybridcpu.managed-reflection/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Explicit roots retain deterministic public Type identities and selected public field/method/property metadata with runtime-owned object caching.",
                "Reflection.Emit, dynamic code, unrestricted loading, non-public retention and arbitrary metadata enumeration remain unsupported."),
            Workstream("async-runtime-libraries", "hybridcpu.async-runtime/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Ordinary compiled CIL state machines use bounded runtime-owned Task state, deterministic cooperative continuations, explicit GC roots and virtualizable monotonic deadlines.",
                "No async-specific opcode/backend, architectural preemption, mutable ExecutionContext flow, unbounded ThreadPool or default enablement is claimed."),
            Workstream("virtual-interface-dispatch", "hybridcpu.managed-dispatch-table/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Exact virtual/interface slot identity, inherited overrides, direct leaf default-interface bodies, interface maps, linked HCO metadata, runtime assignability and generic x5/JALR lowering.",
                "Default enablement, dynamic loading, inherited/reabstracted/ambiguous default-interface methods and variance remain unsupported; only exact closed identities and direct non-generic leaf DIM bodies are admitted."),
            Workstream("delegates-function-pointers", "hybridcpu.managed-function-pointer-table/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Exact managed signatures, single-cast static/closed/open delegate objects, deterministic function-pointer metadata and ordinary x5/JALR invocation thunks.",
                "Multicast, variance, unmanaged function pointers and interop transitions remain unsupported."),
            Workstream("exact-aot-generics", "hybridcpu.exact-aot-generics/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Every statically reachable admitted closed method or constructed reference-type instance has an exact deterministic identity and separate body through Canonical IR/HCO/link/image.",
                "Open forms, constraints, sharing, dictionaries, runtime code generation and arbitrary value-type constructed layouts remain unsupported."),
            Workstream("managed-unmanaged-interop", "hybridcpu.managed-interop/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Exact Cdecl P/Invoke over the blittable V1 subset uses compiler thunks, runtime-owned pins/transition state and the generic kernel host-service boundary.",
                "Reverse P/Invoke, callbacks, reentrancy, complex strings/objects, varargs and cross-native exception unwind remain unsupported."),
            Workstream("tls-thread-runtime-state", "hybridcpu.managed-thread-context/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "ManagedRuntime-owned Thread identity, deterministic TLS layout, lifecycle, all-thread roots and language-neutral RuntimeKernel cooperative rendezvous.",
                "VT remains an execution carrier; preemptive scheduling, host-native transitions and general System.Threading libraries remain unsupported."),
            Workstream("synchronization-memory-model", "hybridcpu.managed-synchronization/v1",
                HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff, true,
                "Normative aligned 32/64-bit volatile and Interlocked mapping, cross-context memory litmus semantics, runtime-owned recursive Monitor and kernel no-lost-wakeup address waits.",
                "No managed synchronization opcode, weak-order API surface, preemptive scheduler or default enablement is claimed."),
            Workstream("debug-source-mapping", "hybridcpu.managed-debug-map/v1",
                HybridCpuManagedWorkstreamSupportV1.Unsupported, false, "No managed source/inline/debug correlation format.",
                "Debug-looking sections are rejected rather than emitted without a tooling contract.")
        }.OrderBy(static item => item.Identity, StringComparer.Ordinal).ToArray());
        ManagedSafetyLevel = HybridCpuManagedSafetyLevelV1.BasicBlockObjectReferences;
        FspAllowed = false;
        VdsaAllowed = false;
        RuntimeAuthority = false;
        PublicationAuthority = false;
        ContractDigest = Hash(string.Join('|', SchemaId, HybridCpuManagedAbiFamilyV1.Default.ContractDigest,
            HybridCpuManagedMetadataContractV1.Default.ContractDigest, ManagedSafetyLevel, FspAllowed, VdsaAllowed,
            RuntimeAuthority, PublicationAuthority,
            string.Join(';', Workstreams.Select(static item => item.Digest))));
    }

    public IReadOnlyList<HybridCpuManagedWorkstreamV1> Workstreams { get; }
    public HybridCpuManagedSafetyLevelV1 ManagedSafetyLevel { get; }
    public bool FspAllowed { get; }
    public bool VdsaAllowed { get; }
    public bool RuntimeAuthority { get; }
    public bool PublicationAuthority { get; }
    public string ContractDigest { get; }

    public HybridCpuManagedFeatureRequirementResultV1 CheckRequirements(IReadOnlyList<string> workstreamIdentities)
    {
        ArgumentNullException.ThrowIfNull(workstreamIdentities);
        string[] malformed = workstreamIdentities.Where(string.IsNullOrWhiteSpace).ToArray();
        string[] requested = workstreamIdentities.Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] missing = requested.Where(identity => Workstreams.FirstOrDefault(item =>
                string.Equals(item.Identity, identity, StringComparison.Ordinal))?.Support !=
            HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff).Concat(malformed)
            .Order(StringComparer.Ordinal).ToArray();
        return new(missing.Length == 0, missing,
            Hash($"requirements|{ContractDigest}|{string.Join(',', requested)}|{string.Join(',', missing)}"));
    }

    private static HybridCpuManagedWorkstreamV1 Workstream(
        string identity,
        string schema,
        HybridCpuManagedWorkstreamSupportV1 support,
        bool runtimeRequired,
        string scope,
        string reason) => new(identity, schema, support, runtimeRequired, scope, reason,
            Hash($"workstream|{identity}|{schema}|{support}|{runtimeRequired}|{scope}|{reason}"));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
