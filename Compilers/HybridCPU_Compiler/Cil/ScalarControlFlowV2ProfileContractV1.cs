using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;

namespace HybridCPU.Compiler.Cil;

public enum ScalarControlFlowV2Disposition : byte
{
    Guaranteed = 0,
    Conditional = 1,
    Missing = 2,
    Rejected = 3
}

public enum ScalarControlFlowV2AssumptionStatus : byte
{
    Verified = 0,
    Unverified = 1,
    Rejected = 2
}

public sealed record ScalarControlFlowV2ScalarRow(
    RestrictedCilTypeV1 CilType,
    int StorageBits,
    int EvaluationStackBits,
    string Interpretation,
    string LoadNormalization,
    string StoreNormalization,
    string CallArgumentNormalization,
    string ReturnNormalization,
    ScalarControlFlowV2Disposition Disposition,
    string EvidenceBoundary);

public sealed record ScalarControlFlowV2FeatureRow(
    string Feature,
    ScalarControlFlowV2Disposition Disposition,
    string Boundary,
    string DiagnosticFamily);

public sealed record ScalarControlFlowV2DiagnosticFamily(
    string Family,
    string CodePrefix,
    string StableOrderingKey,
    string NoArtifactRule);

public sealed record ScalarControlFlowV2Assumption(
    string Identity,
    string Value,
    string AuthorityOwner,
    ScalarControlFlowV2AssumptionStatus Status,
    int ConsumingPhase,
    bool BlocksConsumingPhase);

public sealed record ScalarControlFlowV2Budgets(
    int MaximumPeBytes,
    int MaximumMethodBodyBytes,
    int MaximumIlInstructionsPerMethod,
    int MaximumIlInstructionsProgram,
    int MaximumBasicBlocksPerMethod,
    int MaximumCfgEdgesPerMethod,
    int MaximumLocalsPerMethod,
    int MaximumArgumentsPerMethod,
    int MaximumEvaluationStack,
    int MaximumPhiValuesPerMethod,
    int MaximumParallelCopyTemporariesPerMethod,
    int MaximumLoopsPerMethod,
    int MaximumLoopNestingDepth,
    int MaximumReachableMethods,
    int MaximumOutgoingCallsPerMethod,
    int MaximumCallEdges,
    int MaximumAcyclicCallDepth,
    int MaximumMetadataResolutionSteps,
    int MaximumSpillsPerMethod,
    int MaximumFrameBytes,
    int MaximumSymbols,
    int MaximumRelocations,
    int MaximumCodeBytes,
    int MaximumImageBytes)
{
    public bool IsValid =>
        MaximumPeBytes > 0 && MaximumMethodBodyBytes > 0 &&
        MaximumIlInstructionsPerMethod > 0 && MaximumIlInstructionsProgram >= MaximumIlInstructionsPerMethod &&
        MaximumBasicBlocksPerMethod > 0 && MaximumCfgEdgesPerMethod >= MaximumBasicBlocksPerMethod - 1 &&
        MaximumLocalsPerMethod >= 0 && MaximumArgumentsPerMethod > 0 && MaximumEvaluationStack > 0 &&
        MaximumPhiValuesPerMethod >= 0 && MaximumParallelCopyTemporariesPerMethod >= 0 &&
        MaximumLoopsPerMethod >= 0 && MaximumLoopNestingDepth >= 0 &&
        MaximumReachableMethods > 0 && MaximumOutgoingCallsPerMethod >= 0 && MaximumCallEdges >= 0 &&
        MaximumAcyclicCallDepth > 0 && MaximumMetadataResolutionSteps > 0 &&
        MaximumSpillsPerMethod >= 0 && MaximumFrameBytes > 0 && MaximumSymbols > 0 &&
        MaximumRelocations >= 0 && MaximumCodeBytes > 0 && MaximumImageBytes >= MaximumCodeBytes;
}

/// <summary>
/// Normative, default-off admission contract for RefPlan6 Phase 00. Rows marked
/// Conditional or Missing are never interpreted as language support. Unverified target
/// assumptions block only their declared consuming phase.
/// </summary>
public sealed class ScalarControlFlowV2ProfileContractV1
{
    public const string ProfileId = "HybridCPU.DotNetAot.ScalarControlFlowV2";
    public const int ProfileMajor = 1;
    public const int ProfileMinor = 0;
    public const string SchemaId = "hybridcpu.dotnetaot.scalar-control-flow-v2/v1";
    public const string TargetTriple = HybridCpuTargetMachineContractV1.TargetTriple;
    public const int AddressWidthBits = HybridCpuTargetMachineContractV1.PointerBitWidth;

    private static readonly ScalarControlFlowV2ScalarRow[] ScalarTable =
    [
        new(RestrictedCilTypeV1.Void, 0, 0, "no value", "not applicable", "not applicable", "not applicable", "no return value", ScalarControlFlowV2Disposition.Guaranteed, "RefPlan5 Phase 21/24 void-return evidence"),
        new(RestrictedCilTypeV1.Boolean, 8, 32, "storage is 0 or 1; branch truth is evaluation-stack value != 0", "zero-extend to int32", "truncate to bit 0 and canonicalize to 0/1", "zero-extend to 64-bit ABI register", "truncate and canonicalize to 0/1", ScalarControlFlowV2Disposition.Conditional, "semantics frozen; boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.Int8, 8, 32, "signed two's-complement", "sign-extend to int32", "truncate low 8 bits", "sign-extend to 64-bit ABI register", "truncate low 8 bits", ScalarControlFlowV2Disposition.Conditional, "semantics frozen; boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.UInt8, 8, 32, "unsigned", "zero-extend to int32", "truncate low 8 bits", "zero-extend to 64-bit ABI register", "truncate low 8 bits", ScalarControlFlowV2Disposition.Conditional, "semantics frozen; boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.Int16, 16, 32, "signed two's-complement", "sign-extend to int32", "truncate low 16 bits", "sign-extend to 64-bit ABI register", "truncate low 16 bits", ScalarControlFlowV2Disposition.Conditional, "semantics frozen; boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.UInt16, 16, 32, "unsigned", "zero-extend to int32", "truncate low 16 bits", "zero-extend to 64-bit ABI register", "truncate low 16 bits", ScalarControlFlowV2Disposition.Conditional, "semantics frozen; boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.Int32, 32, 32, "signed two's-complement; arithmetic wraps", "identity", "identity", "sign-extend to 64-bit ABI register", "truncate low 32 bits", ScalarControlFlowV2Disposition.Guaranteed, "RefPlan5 Phase 21/24 arithmetic and ISE execution evidence"),
        new(RestrictedCilTypeV1.UInt32, 32, 32, "unsigned; arithmetic wraps", "identity", "identity", "zero-extend to 64-bit ABI register", "truncate low 32 bits", ScalarControlFlowV2Disposition.Conditional, "unsigned comparison and ABI boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.Int64, 64, 64, "signed two's-complement; arithmetic wraps", "identity", "identity", "identity in 64-bit ABI register", "identity", ScalarControlFlowV2Disposition.Conditional, "64-bit boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.UInt64, 64, 64, "unsigned; arithmetic wraps", "identity", "identity", "identity in 64-bit ABI register", "identity", ScalarControlFlowV2Disposition.Conditional, "unsigned 64-bit boundary execution not yet qualified"),
        new(RestrictedCilTypeV1.NativeInt, 64, 64, "signed pointer-sized integer; never a managed reference", "identity", "identity", "identity in 64-bit ABI register", "identity", ScalarControlFlowV2Disposition.Missing, "not required for V1 source surface"),
        new(RestrictedCilTypeV1.NativeUInt, 64, 64, "unsigned pointer-sized integer; never a managed reference", "identity", "identity", "identity in 64-bit ABI register", "identity", ScalarControlFlowV2Disposition.Missing, "not required for V1 source surface"),
        new(RestrictedCilTypeV1.ObjectReference, 64, 64, "zero-null runtime-owned object identity; ordinary ISA value", "identity", "identity", "pointer carrier in 64-bit ABI register", "identity", ScalarControlFlowV2Disposition.Guaranteed, "RefPlan7 Phase 02 CIL phi/call/RA/spill/final-metadata and linked ISE execution evidence"),
        new(RestrictedCilTypeV1.ManagedByRef, 64, 64, "managed byref requiring provenance/lifetime", "rejected", "rejected", "rejected", "rejected", ScalarControlFlowV2Disposition.Rejected, "Phase 02 bounded policy rejects managed/interior byrefs"),
        new(RestrictedCilTypeV1.UnsupportedManaged, 0, 0, "managed/reference/layout semantics", "forbidden", "forbidden", "forbidden", "forbidden", ScalarControlFlowV2Disposition.Rejected, "fail closed")
    ];

    private static readonly ScalarControlFlowV2FeatureRow[] FeatureTable =
    [
        Guaranteed("static-closed-int32-method", "Existing restricted single-method admission and execution remain available."),
        Guaranteed("forward-branch", "Exact existing br/brtrue/brfalse shapes only."),
        Guaranteed("backward-branch", "Verified CFG/dataflow/SSA and ISE execution evidence."),
        Guaranteed("reducible-for-while-do-while", "Zero/one/many iteration execution through verified reducible loop forests."),
        Guaranteed("loop-carried-values", "Deterministic SSA phis and edge-local parallel copies, including cycles."),
        Guaranteed("nested-if-else", "Nested branch diamonds execute on ISE."),
        Guaranteed("nested-reducible-loops", "Nested loop forest and ISE execution evidence."),
        Guaranteed("break-continue-multiple-loop-exits", "Explicit corpus execution for admitted reducible shapes."),
        Guaranteed("scalar-int32-locals", "Int32 locals, induction variables, accumulators, and loop-carried locals."),
        Guaranteed("signed-int32-arithmetic-comparisons", "Qualified int32 arithmetic and signed comparison corpus."),
        Conditional("unsigned-comparisons", "Lowering infrastructure exists; unsigned scalar boundary corpus is not a V1 guarantee."),
        Guaranteed("direct-static-managed-calls", "Phase 2 graph/SCC and Phase 3 native ABI/frame plus nested ISE execution evidence."),
        Guaranteed("direct-instance-receiver-carrier", "RefPlan7 Phase 02 preserves non-virtual reference-type this as an object-reference semantic kind over the ordinary native ABI carrier."),
        Guaranteed("managed-object-reference-flow", "Object references and null flow through signatures, locals, SSA phi, direct calls, scheduling, RA/spills, final metadata and linked ISE execution."),
        Guaranteed("runtime-bound-instance-fields", "ldfld/stfld require exact runtime TypeDescriptor bindings and explicit non-returning null-check helper lowering."),
        Guaranteed("one-method-hco-link-image", "Phase 4 wires ScalarControlFlowV2 through canonical HCO, static link, restricted image, and profile provenance."),
        Guaranteed("multi-method-symbol-relocation-link", "One canonical HCO object per method; linker-owned managed-call signed16 relocation; existing static linker/image path."),
        Guaranteed("direct-call-chains-shared-callees", "Deterministic acyclic graph with shared callees and bounded depth."),
        Guaranteed("helpers-with-cfg-loops", "Cross-object helper containing branches and a loop executes on ISE."),
        Guaranteed("primitive-int32-arguments-returns-void", "Up to eight admitted register arguments in the native ABI x10..x17, int32 and void returns."),
        Guaranteed("dotnet-publish-hybridcpu", "Explicit opt-in profile only; adapter-presented body world uses the canonical backend without fallback."),
        Rejected("switch", "No V1 switch admission; stable unsupported-opcode diagnostic.", "HCSCF-CIL"),
        Conditional("recursion", "Default-off bounded-recursion V1 capability: exact static Int32(Int32) countdown SCCs, constant non-negative ingress, compile-time depth and post-RA stack proofs only."),
        Guaranteed("bounded-countdown-recursion-capability", "When explicitly configured, admitted recursive SCCs carry deterministic static depth/stack evidence and execute through the existing ABI/object/link/image path without runtime authority."),
        Rejected("managed-references-heap-gc-arrays", "Heap allocation, GC and arrays remain outside this object-reference-only slice.", "HCSCF-TYPE"),
        Rejected("exception-handling", "The ordinary importer still fails closed; Phase 09 uses the explicit managed-eh-plan path.", "HCCIL1011"),
        Guaranteed("managed-eh-plan", "Phase 09 admits bounded catch/finally/throw/rethrow metadata and finalizes HCEH/HCW2 only after scheduling, RA and frame lowering; filters, fault and trap mapping remain rejected."),
        Guaranteed("virtual-interface-calls", "RefPlan7 Phase 06 exact-bound virtual/interface dispatch uses runtime-owned slots and ordinary generic JALR with bounded candidate reachability."),
        Guaranteed("delegates-calli-function-pointers", "Phase 07 exact managed signatures admit single-cast static/closed/open delegates, ldftn/ldvirtftn and managed calli through ordinary x5/JALR; multicast, unmanaged pointers and dynamic binding fail closed."),
        Guaranteed("exact-aot-generics-core", "Phase 08 assigns distinct deterministic identities and compiled bodies to every reachable admitted closed MethodSpec/constructed reference TypeSpec; nested substitution is exact and no sharing, dictionary or runtime code generation exists."),
        Rejected("reflection-dynamic", "Dynamic reflection and runtime code generation remain outside V1; bounded delegate creation is covered by its exact binding contract.", "HCSCF-CIL"),
        Rejected("async-threading-tls", "Outside V1.", "HCSCF-CIL"),
        Guaranteed("pinvoke-interop", "RefPlan7 Phase 13 admits exact static Cdecl declarations over the blittable integer/native-pointer subset and lowers calls to the versioned runtime dispatch helper."),
        Rejected("open-shared-constrained-generics", "Open forms, generic sharing/dictionaries, value-type constructed layouts and constraints remain outside exact AOT generics V1.", "HCCIL1012/HCCIL1702"),
        Rejected("host-jit-native-llvm-fallback", "No fallback is permitted.", "HCSCF-AUTH")
    ];

    private static readonly ScalarControlFlowV2DiagnosticFamily[] DiagnosticTable =
    [
        Diagnostic("unsupported-opcode-shape", "HCSCF-CIL"),
        Diagnostic("unsupported-scalar-reference-type", "HCSCF-TYPE"),
        Diagnostic("malformed-target-or-stack-merge", "HCSCF-CFG"),
        Diagnostic("irreducible-cfg", "HCSCF-IRREDUCIBLE"),
        Diagnostic("profile-budget", "HCSCF-BUDGET"),
        Diagnostic("loop-budget", "HCSCF-LOOP-BUDGET"),
        Diagnostic("ssa-budget", "HCSCF-SSA-BUDGET"),
        Diagnostic("call-frame-image-budget", "HCSCF-BACKEND-BUDGET"),
        Diagnostic("unresolved-disallowed-call", "HCSCF-CALL"),
        Diagnostic("recursive-scc", "HCSCF-RECURSION"),
        Diagnostic("unsupported-abi-signature", "HCSCF-ABI"),
        Diagnostic("abi-frame-stack-mismatch", "HCSCF-FRAME"),
        Diagnostic("missing-duplicate-symbol", "HCSCF-SYMBOL"),
        Diagnostic("unsupported-range-relocation", "HCSCF-RELOC"),
        Diagnostic("nativeaot-body-root-boundary", "HCSCF-BODYWORLD"),
        Diagnostic("compiler-loader-ise-contract", "HCSCF-CONTRACT"),
        Diagnostic("stale-analysis-invariant", "HCSCF-STALE"),
        Diagnostic("nondeterministic-evidence", "HCSCF-DETERMINISM")
    ];

    private static readonly ScalarControlFlowV2Assumption[] AssumptionTable =
    [
        Verified("target-triple", TargetTriple, "HybridCpuTargetMachineContractV1", 1),
        Verified("address-width", "64 bits", "HybridCpuTargetMachineContractV1", 1),
        Verified("endianness", "little", "HybridCpuTargetMachineContractV1", 1),
        Verified("scalar-register-width", "64 bits", "HybridCpuTargetMachineContractV1", 1),
        Verified("cil-branch-truth", "int32/int64 zero=false; nonzero=true", "RestrictedCilSupportMatrixV1", 1),
        Verified("native-abi", "hybridcpu.native-abi/2.0", "HybridCpuNativeAbiContractV2", 3),
        Verified("register-roles", "x0=zero,x1=ra,x2=sp,x8=fp,args=x10..x17,returns=x10..x11", "HybridCpuNativeAbiContractV2", 3),
        Verified("stack-layout", "downward,16-byte alignment,no red-zone,fixed frames", "HybridCpuNativeAbiContractV2", 3),
        Verified("nested-call-execution", "PC+4 link, 256-byte bundle continuation, live-across-call and spill/reload qualified", "HybridCpuNativeCallControlContractV1 + ISE runtime", 3),
        Verified("object-format", $"{HybridCpuObjectFormatContractV1.SchemaId}@{HybridCpuObjectFormatContractV1.SchemaMajor}.{HybridCpuObjectFormatContractV1.SchemaMinor}", "HybridCpuObjectFormatContractV1", 4),
        Verified("function-alignment", $"{HybridCpuManagedCallRelocationContractV1.BundleSizeBytes} bytes", "HybridCpuManagedCallRelocationContractV1", 4),
        Verified("direct-call-relocation", "ManagedCallRelativeSigned16; S-BundleBase(P); zero addend; 16-bit Immediate at 32-byte slot boundary", "HybridCpuManagedCallRelocationContractV1 + HybridCpuStaticLinkerV1", 4),
        Verified("static-link", HybridCpuStaticLinkOptionsV1.Production.SchemaId, "HybridCpuStaticLinkerV1", 4),
        Verified("restricted-image", HybridCpuRestrictedStartupOptionsV1.Production.SchemaId, "HybridCpuRestrictedImageBuilderV1", 5),
        Verified("entry-startup", "global default code symbol; x2 stack top; x1 exact managed-return-biased sentinel", "HybridCpuRestrictedStartupOptionsV1 + HybridCpuNativeCallControlContractV1", 4),
        Verified("compiler-ise-fingerprint", ScalarControlFlowV2CompatibilityFingerprintV1.Default.ContractDigest, "compiler contract + loader/ISE parity qualification", 6)
    ];

    public static ScalarControlFlowV2ProfileContractV1 Default { get; } = new();

    private ScalarControlFlowV2ProfileContractV1()
    {
        Scalars = Array.AsReadOnly(ScalarTable.OrderBy(static row => row.CilType).ToArray());
        Features = Array.AsReadOnly(FeatureTable.OrderBy(static row => row.Feature, StringComparer.Ordinal).ToArray());
        Diagnostics = Array.AsReadOnly(DiagnosticTable.OrderBy(static row => row.Family, StringComparer.Ordinal).ToArray());
        Assumptions = Array.AsReadOnly(AssumptionTable.OrderBy(static row => row.Identity, StringComparer.Ordinal).ToArray());
        Budgets = new(
            16 * 1024 * 1024, 64 * 1024, 16384, 4 * 1024 * 1024, 1024, 4096, 256, 8, 256,
            4096, 1024, 256, 32, 4096, 4096, 65536, 64, 1024 * 1024, 4096, 1024 * 1024,
            65536, 1_000_000, 128 * 1024 * 1024, 256 * 1024 * 1024);
        ContractDigest = Hash(Serialize());
    }

    public IReadOnlyList<ScalarControlFlowV2ScalarRow> Scalars { get; }
    public IReadOnlyList<ScalarControlFlowV2FeatureRow> Features { get; }
    public IReadOnlyList<ScalarControlFlowV2DiagnosticFamily> Diagnostics { get; }
    public IReadOnlyList<ScalarControlFlowV2Assumption> Assumptions { get; }
    public ScalarControlFlowV2Budgets Budgets { get; }
    public string ContractDigest { get; }
    public bool DefaultEnabled => false;
    public bool AllowsHostFallback => false;
    public bool AllowsLlvmFallback => false;
    public bool HasRuntimeAuthority => false;
    public bool HasPhase1Blocker => Assumptions.Any(static row => row.ConsumingPhase <= 1 && row.BlocksConsumingPhase);

    public bool IsGuaranteedScalar(RestrictedCilTypeV1 type) =>
        Scalars.Single(row => row.CilType == type).Disposition == ScalarControlFlowV2Disposition.Guaranteed;

    private string Serialize() => string.Join('|',
        SchemaId, $"{ProfileMajor}.{ProfileMinor}", ProfileId, TargetTriple, AddressWidthBits,
        string.Join(';', Scalars.Select(static row => $"{row.CilType}:{row.StorageBits}:{row.EvaluationStackBits}:{row.Interpretation}:{row.LoadNormalization}:{row.StoreNormalization}:{row.CallArgumentNormalization}:{row.ReturnNormalization}:{row.Disposition}:{row.EvidenceBoundary}")),
        string.Join(';', Features.Select(static row => $"{row.Feature}:{row.Disposition}:{row.Boundary}:{row.DiagnosticFamily}")),
        string.Join(';', Diagnostics.Select(static row => $"{row.Family}:{row.CodePrefix}:{row.StableOrderingKey}:{row.NoArtifactRule}")),
        string.Join(';', Assumptions.Select(static row => $"{row.Identity}:{row.Value}:{row.AuthorityOwner}:{row.Status}:{row.ConsumingPhase}:{row.BlocksConsumingPhase}")),
        Budgets);

    private static ScalarControlFlowV2FeatureRow Guaranteed(string feature, string boundary) =>
        new(feature, ScalarControlFlowV2Disposition.Guaranteed, boundary, string.Empty);

    private static ScalarControlFlowV2FeatureRow Conditional(string feature, string boundary) =>
        new(feature, ScalarControlFlowV2Disposition.Conditional, boundary, "HCSCF-PROFILE");

    private static ScalarControlFlowV2FeatureRow Missing(string feature, string boundary, string diagnostic) =>
        new(feature, ScalarControlFlowV2Disposition.Missing, boundary, diagnostic);

    private static ScalarControlFlowV2FeatureRow Rejected(string feature, string boundary, string diagnostic) =>
        new(feature, ScalarControlFlowV2Disposition.Rejected, boundary, diagnostic);

    private static ScalarControlFlowV2DiagnosticFamily Diagnostic(string family, string prefix) =>
        new(family, prefix, "method-identity,cil-offset,code", "budget/rejection emits no HCO or HCEXE");

    private static ScalarControlFlowV2Assumption Verified(string identity, string value, string owner, int phase) =>
        new(identity, value, owner, ScalarControlFlowV2AssumptionStatus.Verified, phase, false);

    private static ScalarControlFlowV2Assumption Unverified(string identity, string value, string owner, int phase) =>
        new(identity, value, owner, ScalarControlFlowV2AssumptionStatus.Unverified, phase, true);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
