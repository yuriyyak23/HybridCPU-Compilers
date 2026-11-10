using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public enum RestrictedCilImportStatusV1 : byte
{
    Success = 0,
    Unsupported = 1,
    InvalidInput = 2,
    UnknownSemantics = 3,
    BudgetExhausted = 4
}

public enum RestrictedCilTypeV1 : byte
{
    Void = 0,
    Boolean = 1,
    Int8 = 2,
    UInt8 = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    UInt32 = 7,
    Int64 = 8,
    UInt64 = 9,
    NativeInt = 10,
    NativeUInt = 11,
    ObjectReference = 12,
    ManagedByRef = 13,
    // Exact identity is carried separately during dataflow; never a scalar ABI carrier.
    Aggregate = 14,
    UnsupportedManaged = 254,
    Invalid = 255
}

public enum RestrictedCilMatrixSupportV1 : byte
{
    Supported = 0,
    Unsupported = 1
}

public sealed record RestrictedCilOpcodeContractV1(
    ushort Encoding,
    string Name,
    string StackRule,
    string TypeRule,
    string CanonicalExpansion,
    RestrictedCilMatrixSupportV1 Support,
    string FailureCode);

public sealed record RestrictedCilFeatureContractV1(
    string Feature,
    RestrictedCilMatrixSupportV1 Support,
    string Policy,
    string FailureCode);

public sealed record RestrictedCilHelperContractV1(
    string StableIdentity,
    IReadOnlyList<RestrictedCilTypeV1> ParameterTypes,
    RestrictedCilTypeV1 ReturnType,
    string CanonicalExpansion,
    string CallingConvention,
    IrMemoryEffectKind MemoryEffects,
    IrArchitecturalEffectKind ArchitecturalEffects);

public sealed record RestrictedCilImportBudgetsV1(
    int MaximumPeBytes,
    int MaximumMethodBodyBytes,
    int MaximumDecodedInstructions,
    int MaximumBasicBlocks,
    int MaximumArguments,
    int MaximumLocals,
    int MaximumEvaluationStack,
    int MaximumCalls)
{
    public static RestrictedCilImportBudgetsV1 Production { get; } = new(
        16 * 1024 * 1024,
        64 * 1024,
        16384,
        1024,
        64,
        4,
        256,
        4096);

    public bool IsValid =>
        MaximumPeBytes > 0 && MaximumMethodBodyBytes > 0 && MaximumDecodedInstructions > 0 &&
        MaximumBasicBlocks > 0 && MaximumArguments > 0 && MaximumLocals >= 0 && MaximumEvaluationStack > 0 && MaximumCalls >= 0;
}

public sealed record RestrictedCilGenericArgumentV1(
    string StableTypeIdentity,
    RestrictedCilTypeV1 CarrierType);

public sealed record RestrictedCilMethodSelectorV1(
    string TypeName,
    string MethodName,
    int? MetadataToken = null,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericTypeArguments = null,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericMethodArguments = null);

public sealed record ManagedMethodIdentityV1(
    string SchemaId,
    string AssemblyIdentity,
    string ModuleName,
    int MetadataToken,
    string DeclaringType,
    string MethodName,
    string CanonicalSignature,
    string StableIdentity,
    string IdentityDigest,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericTypeArguments = null,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericMethodArguments = null)
{
    public bool DependsOnFileSystemPath => false;
    public bool DependsOnMvid => false;
}

public enum ManagedBodyWorldModeV1 : byte
{
    StandaloneRestrictedModule = 0,
    AdapterPresented = 1
}

public sealed record ManagedBodyWorldV1(
    ManagedBodyWorldModeV1 Mode,
    ReadOnlyMemory<byte> PeImage,
    string SourceIdentity,
    IReadOnlyList<RestrictedCilMethodSelectorV1> Roots,
    IReadOnlyList<RestrictedCilMethodSelectorV1> PresentedMethods,
    ManagedBoundedRecursionOptionsV1? BoundedRecursion = null,
    IReadOnlyList<ManagedBodyWorldModuleV1>? DependencyModules = null,
    IReadOnlyList<ManagedBodyWorldModuleV1>? MetadataOnlyModules = null);

public sealed record ManagedBodyWorldModuleV1(
    ReadOnlyMemory<byte> PeImage,
    string SourceIdentity);

public sealed record ManagedCallGraphEdgeV1(
    string StableId,
    string CallerIdentity,
    string CalleeIdentity,
    int CallerIlOffset,
    int MetadataToken,
    bool IsDispatchCandidate = false,
    bool IsFunctionPointerTarget = false,
    bool IsTypeInitializerTarget = false);

public sealed record ManagedCallGraphSccV1(
    string StableId,
    IReadOnlyList<string> MethodIdentities,
    bool IsRecursive);

public sealed record ManagedRecursionProofV1(
    string SccStableId,
    IReadOnlyList<string> MethodIdentities,
    int MaximumSccInvocations,
    int CountdownStep,
    IReadOnlyList<int> ConstantIngressArguments,
    string ProofDigest);

public sealed record ManagedCompiledMethodV1(
    ManagedMethodIdentityV1 Identity,
    RestrictedCilImportResultV1 Import,
    IReadOnlyList<string> DirectCalleeIdentities);

public sealed record ManagedCallGraphEvidenceV1(
    string SchemaId,
    int SchemaVersion,
    ManagedBodyWorldModeV1 BodyWorldMode,
    IReadOnlyList<string> RootIdentities,
    IReadOnlyList<string> MethodIdentities,
    IReadOnlyList<ManagedCallGraphEdgeV1> Edges,
    IReadOnlyList<ManagedCallGraphSccV1> Sccs,
    IReadOnlyList<string> CompilationOrder,
    int MaximumAcyclicDepth,
    string GraphDigest,
    string ContractDigest,
    IReadOnlyList<ManagedRecursionProofV1>? RecursionProofs = null,
    int MaximumDynamicDepth = 0,
    string BoundedRecursionOptionsDigest = "",
    int MaximumRecursionStackBytes = 0,
    int ExactGenericInstantiationCount = 0,
    string ExactAotGenericsContractDigest = "")
{
    public bool HasRuntimeAuthority => false;
    public bool OwnsNativeAotReachability => false;
    public bool HasBoundedRecursion => RecursionProofs is { Count: > 0 };
}

public sealed record ManagedCallGraphCompilationV1(
    RestrictedCilImportStatusV1 Status,
    IReadOnlyList<ManagedCompiledMethodV1> Methods,
    ManagedCallGraphEvidenceV1? Graph,
    IReadOnlyList<IrFrontendDiagnosticV1> Diagnostics,
    IReadOnlyList<ManagedVirtualSlotPlanV1>? VirtualSlotPlans = null,
    IReadOnlyList<ManagedClosedInterfacePlanV1>? ClosedInterfacePlans = null,
    ManagedStringLiteralPlanV1? StringLiteralPlan = null,
    ManagedTypeUniversePlanV1? TypeUniverse = null,
    IReadOnlyList<ManagedDispatchCallPlanV1>? DispatchCallPlans = null,
    IReadOnlyList<RestrictedCilFieldDataBindingV1>? FieldData = null,
    ManagedProfileClosureEvidenceV1? ProfileClosure = null,
    IReadOnlyList<ManagedArrayTypeUseV1>? ArrayTypeUses = null,
    IReadOnlyList<ManagedAllocationTypeUseV1>? AllocationTypeUses = null);

public sealed record ManagedArrayTypeUseV1(string CallerIdentity, int CilOffset,
    ulong TypeId, ulong TypeHandle);

public sealed record ManagedAllocationTypeUseV1(string CallerIdentity, int CilOffset,
    ulong TypeId, ulong TypeHandle);

public sealed record ManagedTypeUniverseRowV1(ulong TypeHandle, ulong TypeId, ulong BaseTypeId,
    string StableIdentity = "", HybridCpuManagedTypeDescriptorV1? Descriptor = null);

public sealed record ManagedTypeUniversePlanV1(
    IReadOnlyList<ManagedTypeUniverseRowV1> Rows,
    string PlanDigest);

public sealed record ManagedStringLiteralPlanV1(
    IReadOnlyList<RestrictedCilStringLiteralBindingV1> Bindings, string PlanDigest);

// Declaration evidence only: never a callable method, implementation address, or runtime vtable.
public sealed record ManagedClosedInterfacePlanV1(string StableIdentity, string DefinitionAssemblySha256,
    int DefinitionToken, IReadOnlyList<string> TypeArguments, IReadOnlyList<string> MethodDeclarations,
    string PlanDigest);

public sealed record ManagedVirtualSlotTargetV1(string RuntimeTypeIdentity, string ImplementationIdentity,
    string? AssemblyName, int? MethodMetadataToken);

public sealed record ManagedVirtualSlotPlanV1(ulong SlotId, string DeclaringTypeIdentity,
    string DeclarationIdentity, string SignatureIdentity, IReadOnlyList<ManagedVirtualSlotTargetV1> Targets,
    string PlanDigest);

public sealed record ManagedDispatchCandidatePlanV1(string RuntimeTypeIdentity, string ImplementationIdentity);
public sealed record ManagedDispatchCallPlanV1(string CallerIdentity, int CilOffset,
    RestrictedCilDispatchKindV1 Kind, ulong SlotId, ulong InterfaceTypeId,
    IReadOnlyList<ManagedDispatchCandidatePlanV1> Candidates, string PlanDigest,
    string DeclarationIdentity = "", bool RuntimeExternal = false);

public sealed record RestrictedCilManagedCallTargetV1(
    int MetadataToken,
    string StableIdentity,
    IReadOnlyList<RestrictedCilTypeV1> ParameterTypes,
    RestrictedCilTypeV1 ReturnType,
    string? AggregateReturn = null,
    IReadOnlyList<string?>? AggregateParameters = null,
    ManagedReceiverAbiPlanV1? Receiver = null);

public enum RestrictedCilDispatchKindV1 : byte
{
    Virtual = 0,
    Interface = 1
}

public sealed record RestrictedCilDispatchBindingV1(
    int MetadataToken,
    string StableIdentity,
    IReadOnlyList<RestrictedCilTypeV1> ParameterTypes,
    RestrictedCilTypeV1 ReturnType,
    RestrictedCilDispatchKindV1 Kind,
    ulong SlotId,
    ulong? InterfaceTypeId = null,
    IReadOnlyList<int>? CandidateMethodMetadataTokens = null,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericTypeArguments = null,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericMethodArguments = null,
    IReadOnlyList<RestrictedCilDispatchCandidateV1>? ExactGenericCandidates = null,
    bool RuntimeExternal = false,
    string? ExactImplementationIdentity = null);

/// <summary>
/// Versioned guest/package policy projected into the generic importer. The importer validates the
/// exact managed signature and only consumes this neutral signature-to-symbol mapping.
/// </summary>
public sealed record RestrictedCilRuntimeExternalBindingV1(
    string StableIdentity,
    IReadOnlyList<RestrictedCilTypeV1> ParameterTypes,
    RestrictedCilTypeV1 ReturnType,
    string ExactImplementationIdentity);

public sealed record RestrictedCilDispatchCandidateV1(
    int MethodMetadataToken,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericTypeArguments = null,
    IReadOnlyList<RestrictedCilGenericArgumentV1>? GenericMethodArguments = null,
    string? AssemblyName = null,
    string? RuntimeTypeIdentity = null);

public sealed record RestrictedCilTypeTestBindingV1(
    int TypeMetadataToken,
    HybridCpuManagedTypeDescriptorV1 TypeDescriptor,
    ulong TypeHandle);

public sealed record RestrictedCilFunctionPointerBindingV1(
    int MethodMetadataToken,
    ulong MethodId,
    ulong SignatureId,
    IReadOnlyList<RestrictedCilTypeV1> ParameterTypes,
    RestrictedCilTypeV1 ReturnType,
    bool IsInstanceMethod,
    int? VirtualSlotMetadataToken = null,
    IReadOnlyList<RestrictedCilTypeV1>? SignatureParameterTypes = null);

public sealed record RestrictedCilCalliBindingV1(
    int SignatureMetadataToken,
    ulong SignatureId,
    IReadOnlyList<RestrictedCilTypeV1> ParameterTypes,
    RestrictedCilTypeV1 ReturnType);

public sealed record RestrictedCilDelegateCreationBindingV1(
    int ConstructorMetadataToken,
    int CilOffset,
    ulong DelegateTypeHandle,
    ulong SignatureId,
    HybridCpuManagedDelegateKindV1 Kind,
    int ContainingMethodMetadataToken);

public sealed record RestrictedCilDelegateInvokeBindingV1(
    int InvokeMetadataToken,
    ulong SignatureId,
    IReadOnlyList<RestrictedCilTypeV1> ParameterTypes,
    RestrictedCilTypeV1 ReturnType);

public sealed record RestrictedCilFieldLayoutBindingV1(
    string DeclaringType,
    string FieldName,
    bool IsStatic,
    RestrictedCilTypeV1 FieldType,
    HybridCpuManagedTypeDescriptorV1 TypeDescriptor,
    string? StaticStorageSymbol = null,
    bool IsScalarValueProjection = false);

public sealed record RestrictedCilAllocationBindingV1(
    int ConstructorMetadataToken,
    HybridCpuManagedTypeDescriptorV1 TypeDescriptor,
    ulong TypeHandle,
    bool IsScalarValueProjection = false,
    RestrictedCilTypeV1 ScalarValueType = RestrictedCilTypeV1.Invalid,
    RestrictedCilTypeV1 ScalarArgumentType = RestrictedCilTypeV1.Invalid);

public sealed record RestrictedCilArrayTypeBindingV1(
    int ElementTypeMetadataToken,
    HybridCpuManagedTypeDescriptorV1 TypeDescriptor,
    ulong TypeHandle,
    HybridCpuManagedTypeDescriptorV1? ElementTypeDescriptor = null);

public sealed record RestrictedCilStringLiteralBindingV1(
    string Literal,
    ulong LiteralHandle,
    HybridCpuManagedTypeDescriptorV1 TypeDescriptor,
    ulong TypeHandle);

public sealed record RestrictedCilFieldDataBindingV1(
    int FieldMetadataToken,
    ulong DataHandle,
    byte[] Data);

public sealed record RestrictedCilValueTypeBindingV1(
    int TypeMetadataToken,
    RestrictedCilTypeV1 ValueType,
    HybridCpuManagedTypeDescriptorV1 TypeDescriptor,
    ulong TypeHandle);

public sealed record RestrictedCilTypeInitializationBindingV1(
    string DeclaringType,
    int InitializerMetadataToken,
    HybridCpuManagedTypeDescriptorV1 TypeDescriptor,
    ulong TypeHandle,
    bool RuntimePreinitialized = false);

public sealed record RestrictedCilProvenanceV1(
    string SourceIdentity,
    string PeSha256,
    string MethodIdentity,
    string CilMethodToken,
    string MatrixDigest,
    string OptionsDigest,
    string MethodLocalIdentity = "")
{
    public string CanonicalMethodLocalIdentity => string.IsNullOrEmpty(MethodLocalIdentity)
        ? MethodIdentity
        : MethodLocalIdentity;

    public string ManagedModuleIdentity => $"mmod:{Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(MethodIdentity))).ToLowerInvariant()}";

    public bool MethodIdentityHasLinkageAuthority => true;
    public bool MethodLocalIdentityHasLinkageAuthority => false;
}

public sealed record RestrictedCilImportResultV1(
    RestrictedCilImportStatusV1 Status,
    IrProgram? Program,
    IReadOnlyList<IrFrontendDiagnosticV1> Diagnostics,
    RestrictedCilProvenanceV1? Provenance,
    ScalarControlFlowV2AnalysisV1? ControlFlowAnalysis = null,
    ManagedReceiverAbiPlanV1? ReceiverAbi = null,
    bool RequiresNativeExceptionTransfer = false)
{
    // PE-revalidated analysis retained even when native EH lowering remains gated.
    // This is not an IR program or runtime/publication authority.
    public ManagedEhMethodPlanV1? ManagedEhAnalysis { get; init; }
    public ManagedEhTypedDataflowV1? ManagedEhTypedDataflow { get; init; }
    public ManagedEhLoweringEvidenceV1? ManagedEhLoweringEvidence { get; init; }
    public IReadOnlyList<ManagedReceiverCallerStoragePlanV1>? ReceiverCallerStoragePlans { get; init; }
    public IReadOnlyList<string>? SourceProfileMarkers { get; init; }
}

public sealed record ManagedEhLoweringEvidenceV1(int HomeAccessInstructionCount,
    int CatchEntryCopyCount, int LeaveTransferCount, int ThrowTransferCount,
    int RethrowTransferCount, int EndFinallyTransferCount, string Digest);

// Callee-side loan requirements, not proof that an arbitrary address is a valid receiver.
public sealed record ManagedReceiverAbiPlanV1(string ScopedTypeIdentity, int PayloadSizeBytes,
    int PayloadAlignmentBytes, int ArgumentIndex, bool RequiresNonNullBoundedCallerStorage,
    bool NonEscaping, bool NoSafepoints, string PlanDigest);

public sealed record ManagedReceiverCallerStorageCallV1(int CilOffset, string CalleeIdentity,
    string CalleePlanDigest, string AbiLayoutDigest, string CallProofDigest);

public sealed record ManagedReceiverCallerStoragePlanV1(int LocalIndex, string ScopedTypeIdentity,
    int PayloadSizeBytes, int PayloadAlignmentBytes, string FrameSlotIdentity,
    IReadOnlyList<ManagedReceiverCallerStorageCallV1> Calls, string StorageProofDigest);

public enum RestrictedCilImportModeV1 : byte
{
    RestrictedScalarV1 = 0,
    ScalarControlFlowV2 = 1
}

public enum ScalarControlFlowV2EdgeKindV1 : byte
{
    Fallthrough = 0,
    Branch = 1,
    Backedge = 2,
    CriticalSplit = 3
}

public sealed record ScalarControlFlowV2BlockV1(
    int Id,
    int StartOffset,
    int EndOffsetExclusive,
    IReadOnlyList<int> PredecessorIds,
    IReadOnlyList<int> SuccessorIds,
    string EntryStateDigest,
    string ExitStateDigest);

public sealed record ScalarControlFlowV2EdgeV1(
    string StableId,
    int SourceBlockId,
    int TargetBlockId,
    ScalarControlFlowV2EdgeKindV1 Kind,
    bool IsCritical);

public sealed record ScalarControlFlowV2PhiIncomingV1(
    int SourceBlockId,
    string ValueId);

public sealed record ScalarControlFlowV2PhiV1(
    string StableId,
    int BlockId,
    string SlotIdentity,
    RestrictedCilTypeV1 Type,
    IReadOnlyList<ScalarControlFlowV2PhiIncomingV1> Incoming);

public sealed record ScalarControlFlowV2LoopV1(
    string StableId,
    int HeaderBlockId,
    IReadOnlyList<int> LatchBlockIds,
    IReadOnlyList<int> BlockIds,
    int? ParentHeaderBlockId,
    bool Reducible);

public sealed record ScalarControlFlowV2ParallelCopyV1(
    string StableId,
    int SourceBlockId,
    int TargetBlockId,
    string SourceValueId,
    string TargetValueId,
    int Sequence,
    bool UsesTemporary,
    bool EdgeWasSplit);

public sealed record ScalarControlFlowV2AnalysisV1(
    string SchemaId,
    int SchemaVersion,
    long Generation,
    string MethodIdentity,
    IReadOnlyList<ScalarControlFlowV2BlockV1> Blocks,
    IReadOnlyList<ScalarControlFlowV2EdgeV1> Edges,
    IReadOnlyList<ScalarControlFlowV2PhiV1> Phis,
    IReadOnlyList<ScalarControlFlowV2LoopV1> Loops,
    IReadOnlyList<ScalarControlFlowV2ParallelCopyV1> ParallelCopies,
    string GraphDigest,
    string StateDigest,
    string SsaDigest,
    string ContractDigest)
{
    public bool HasRuntimeAuthority => false;
    public bool HasTargetLegalityAuthority => false;

    public bool IsCurrentFor(IrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return program.Contract.DerivedFacts.ProgramMutation.Value == Generation;
    }

    public void EnsureCurrentFor(IrProgram program)
    {
        if (!IsCurrentFor(program))
        {
            throw new InvalidOperationException(
                $"ScalarControlFlowV2 analysis generation {Generation} is stale for program mutation generation " +
                $"{program.Contract.DerivedFacts.ProgramMutation.Value}.");
        }
    }

    public void EnsureWellFormed()
    {
        if (Blocks.Select(static block => block.Id).Where((id, index) => id != index).Any())
            throw new InvalidOperationException("ScalarControlFlowV2 block identities must be contiguous and ordered.");
        Dictionary<int, ScalarControlFlowV2BlockV1> blocks = Blocks.ToDictionary(static block => block.Id);
        foreach (ScalarControlFlowV2BlockV1 block in Blocks)
        {
            EnsureSortedDistinct(block.PredecessorIds, $"block {block.Id} predecessor");
            EnsureSortedDistinct(block.SuccessorIds, $"block {block.Id} successor");
            foreach (int predecessor in block.PredecessorIds)
                if (!blocks.TryGetValue(predecessor, out ScalarControlFlowV2BlockV1? source) || !source.SuccessorIds.Contains(block.Id))
                    throw new InvalidOperationException($"ScalarControlFlowV2 predecessor edge b{predecessor}->b{block.Id} is not reciprocal.");
            foreach (int successor in block.SuccessorIds)
                if (!blocks.TryGetValue(successor, out ScalarControlFlowV2BlockV1? target) || !target.PredecessorIds.Contains(block.Id))
                    throw new InvalidOperationException($"ScalarControlFlowV2 successor edge b{block.Id}->b{successor} is not reciprocal.");
        }

        HashSet<(int Id, int Target)> expectedEdges = Blocks.SelectMany(static block => block.SuccessorIds.Select(target => (block.Id, Target: target))).ToHashSet();
        (int SourceBlockId, int TargetBlockId)[] actualEdges = Edges.Select(static edge => (edge.SourceBlockId, edge.TargetBlockId)).ToArray();
        if (actualEdges.Length != actualEdges.Distinct().Count() || !expectedEdges.SetEquals(actualEdges))
            throw new InvalidOperationException("ScalarControlFlowV2 edge records do not exactly cover the CFG.");
        foreach (ScalarControlFlowV2PhiV1 phi in Phis)
        {
            if (!blocks.TryGetValue(phi.BlockId, out ScalarControlFlowV2BlockV1? target))
                throw new InvalidOperationException($"ScalarControlFlowV2 phi '{phi.StableId}' names an absent block.");
            int[] incoming = phi.Incoming.Select(static item => item.SourceBlockId).ToArray();
            if (incoming.Length != incoming.Distinct().Count() || !target.PredecessorIds.SequenceEqual(incoming))
                throw new InvalidOperationException($"ScalarControlFlowV2 phi '{phi.StableId}' must have exactly one ordered incoming value per predecessor.");
        }
        foreach (IGrouping<(int SourceBlockId, int TargetBlockId), ScalarControlFlowV2ParallelCopyV1> group in
                 ParallelCopies.GroupBy(static copy => (copy.SourceBlockId, copy.TargetBlockId)))
        {
            if (!expectedEdges.Contains(group.Key))
                throw new InvalidOperationException("ScalarControlFlowV2 parallel copy names an absent CFG edge.");
            if (!group.OrderBy(static copy => copy.Sequence).Select(static copy => copy.Sequence).SequenceEqual(Enumerable.Range(0, group.Count())))
                throw new InvalidOperationException("ScalarControlFlowV2 parallel-copy sequence must be contiguous.");
        }
    }

    private static void EnsureSortedDistinct(IReadOnlyList<int> values, string identity)
    {
        if (!values.SequenceEqual(values.Order()) || values.Count != values.Distinct().Count())
            throw new InvalidOperationException($"ScalarControlFlowV2 {identity} identities must be sorted and distinct.");
    }
}

public sealed class RestrictedCilSupportMatrixV1
{
    private static readonly RestrictedCilOpcodeContractV1[] OpcodeRows =
    [
        Supported(0x00, "nop", "[] -> []", "none", "no instruction"),
        Supported(0x02, "ldarg.0", "[] -> [arg0]", "qualified integer argument", "virtual value use"),
        Supported(0x03, "ldarg.1", "[] -> [arg1]", "qualified integer argument", "virtual value use"),
        Supported(0x04, "ldarg.2", "[] -> [arg2]", "qualified integer argument", "virtual value use"),
        Supported(0x05, "ldarg.3", "[] -> [arg3]", "qualified integer argument", "virtual value use"),
        Supported(0x06, "ldloc.0", "[] -> [local0]", "assigned qualified primitive local", "virtual value use"),
        Supported(0x07, "ldloc.1", "[] -> [local1]", "assigned qualified primitive local", "virtual value use"),
        Supported(0x08, "ldloc.2", "[] -> [local2]", "assigned qualified primitive local", "virtual value use"),
        Supported(0x09, "ldloc.3", "[] -> [local3]", "assigned qualified primitive local", "virtual value use"),
        Supported(0x0a, "stloc.0", "[T] -> []", "single assignment to exact local type", "frontend SSA binding; no target instruction"),
        Supported(0x0b, "stloc.1", "[T] -> []", "single assignment to exact local type", "frontend SSA binding; no target instruction"),
        Supported(0x0c, "stloc.2", "[T] -> []", "single assignment to exact local type", "frontend SSA binding; no target instruction"),
        Supported(0x0d, "stloc.3", "[T] -> []", "single assignment to exact local type", "frontend SSA binding; no target instruction"),
        Supported(0x14, "ldnull", "[] -> [object-reference]", "zero-null object reference", "constant zero with managed-reference kind"),
        Supported(0x15, "ldc.i4.m1", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x16, "ldc.i4.0", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x17, "ldc.i4.1", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x18, "ldc.i4.2", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x19, "ldc.i4.3", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x1a, "ldc.i4.4", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x1b, "ldc.i4.5", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x1c, "ldc.i4.6", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x1d, "ldc.i4.7", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x1e, "ldc.i4.8", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x1f, "ldc.i4.s", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x20, "ldc.i4", "[] -> [int32]", "int32", "constant operand"),
        Supported(0x21, "ldc.i8", "[] -> [int64]", "int64", "constant operand"),
        Supported(0x25, "dup", "[T] -> [T,T]", "qualified scalar/reference", "SSA value reuse"),
        Supported(0x28, "call", "[receiver?,args] -> [return?]", "allowlisted helper or exact direct managed signature", "versioned helper or managed direct call"),
        Supported(0x29, "calli", "[args,native-uint] -> [return?]", "exact managed call-site signature binding", "signature validation then generic JALR"),
        Supported(0x2a, "ret", "[return?] -> []", "exact method return type", "JALR return"),
        Supported(0x2b, "br.s", "[] -> []", "empty stack at target", "JAL branch"),
        Supported(0x2c, "brfalse.s", "[integer] -> []", "int32/int64 condition", "BEQ zero branch"),
        Supported(0x2d, "brtrue.s", "[integer] -> []", "int32/int64 condition", "BNE zero branch"),
        Supported(0x38, "br", "[] -> []", "empty stack at target", "JAL branch"),
        Supported(0x39, "brfalse", "[integer] -> []", "int32/int64 condition", "BEQ zero branch"),
        Supported(0x3a, "brtrue", "[integer] -> []", "int32/int64 condition", "BNE zero branch"),
        Supported(0x58, "add", "[T,T] -> [T]", "same qualified integer type", "ADD"),
        Supported(0x59, "sub", "[T,T] -> [T]", "same qualified integer type", "SUB"),
        Supported(0x5a, "mul", "[T,T] -> [T]", "same qualified integer type", "MUL"),
        Supported(0x67, "conv.i1", "[integer] -> [int32]", "truncate to signed int8", "SLLI 56 then SRAI 56"),
        Supported(0x68, "conv.i2", "[integer] -> [int32]", "truncate to signed int16", "SLLI 48 then SRAI 48"),
        Supported(0x69, "conv.i4", "[integer] -> [int32]", "int32/native integer", "canonical value copy"),
        Supported(0x6a, "conv.i8", "[integer] -> [int64]", "signed extension of I4; preserve I8/native bits", "ADDIW or canonical copy"),
        Supported(0x6f, "callvirt", "[receiver,args] -> [return?]", "exact virtual/interface dispatch binding", "resolver call then generic JALR"),
        Supported(0x73, "newobj", "[args] -> [object-reference]", "exact runtime TypeDescriptor and direct constructor binding", "allocation helper then ordinary managed constructor call"),
        Supported(0x72, "ldstr", "[] -> [object-reference]", "exact UTF-16 literal binding", "versioned literal materialization helper"),
        Supported(0x74, "castclass", "[object-reference] -> [object-reference]", "exact runtime type-test binding", "versioned cast helper"),
        Supported(0x75, "isinst", "[object-reference] -> [object-reference]", "exact runtime type-test binding", "versioned type-test helper"),
        Supported(0x7b, "ldfld", "[object-reference] -> [T]", "runtime-bound instance field", "explicit null helper, ADDI offset, LOAD"),
        Supported(0x7d, "stfld", "[object-reference,T] -> []", "runtime-bound instance field", "explicit null helper, ADDI offset, STORE"),
        Supported(0x7e, "ldsfld", "[] -> [T]", "runtime-bound static field", "type-init helper then static-load helper"),
        Supported(0x80, "stsfld", "[T] -> []", "runtime-bound static field", "type-init helper then static-store helper"),
        Supported(0x8c, "box", "[value] -> [object-reference]", "exact fixed blittable scalar binding", "versioned boxing helper"),
        Supported(0x8d, "newarr", "[length] -> [object-reference]", "exact SZARRAY descriptor binding", "versioned checked array allocation helper"),
        Supported(0x8e, "ldlen", "[object-reference] -> [native-uint]", "SZARRAY", "versioned checked array length helper"),
        UnsupportedOpcode(0x8f, "ldelema", "[object-reference,index] -> [managed-byref]", "interior reference", "none", "HCCIL1408"),
        Supported(0x92, "ldelem.i2", "[object-reference,index] -> [int32]", "int16 SZARRAY binding", "versioned checked array load helper"),
        Supported(0x93, "ldelem.u2", "[object-reference,index] -> [int32]", "uint16/char SZARRAY binding", "versioned checked array load helper"),
        Supported(0x94, "ldelem.i4", "[object-reference,index] -> [int32]", "int32 SZARRAY binding", "versioned checked array load helper"),
        Supported(0x9a, "ldelem.ref", "[object-reference,index] -> [object-reference]", "reference SZARRAY binding", "versioned checked array load helper"),
        Supported(0x9d, "stelem.i2", "[object-reference,index,value] -> []", "int16/uint16/char SZARRAY binding", "versioned checked array store helper"),
        Supported(0x9e, "stelem.i4", "[object-reference,index,value] -> []", "int32 SZARRAY binding", "versioned checked array store helper"),
        Supported(0xa2, "stelem.ref", "[object-reference,index,value] -> []", "reference SZARRAY binding with store check", "versioned checked array store helper"),
        Supported(0xa5, "unbox.any", "[object-reference] -> [value]", "exact fixed blittable scalar binding", "versioned checked unboxing helper"),
        Supported(0xfe02, "cgt", "[T,T] -> [int32]", "same qualified integer type", "SLT with reversed operands"),
        Supported(0xfe03, "cgt.un", "[T,T] -> [int32]", "same qualified integer type", "SLTU with reversed operands"),
        Supported(0xfe04, "clt", "[T,T] -> [int32]", "same qualified integer type", "SLT"),
        Supported(0xfe05, "clt.un", "[T,T] -> [int32]", "same qualified integer type", "SLTU"),
        Supported(0xfe06, "ldftn", "[] -> [native-uint]", "exact managed method/signature binding", "versioned managed pointer resolver"),
        Supported(0xfe07, "ldvirtftn", "[object-reference] -> [native-uint]", "exact dispatch and managed signature binding", "runtime dispatch resolver plus pointer validation")
    ];

    private static readonly RestrictedCilFeatureContractV1[] FeatureRows =
    [
        SupportedFeature("static-methods", "Static closed managed IL bodies with admitted primitive/reference signatures, including exact AOT generic instances; synchronized, native, unmanaged and internal-call implementations fail closed."),
        SupportedFeature("instance-receiver-carrier", "Reference-type managed IL instance bodies and exact direct calls use an object-reference receiver in the ordinary argument ABI; synchronized, native, unmanaged and internal-call implementations fail closed."),
        SupportedFeature("virtual-interface-dispatch", "Exact callvirt bindings lower a runtime-owned slot resolver followed by an ordinary relocation-free JALR; body-world reachability uses an exact bounded candidate set."),
        SupportedFeature("delegates-managed-function-pointers", "Exact single-cast static/closed/open delegates and exact managed calli signatures use runtime-owned semantics and generic x5 JALR; multicast/native interop pointers fail closed."),
        SupportedFeature("primitive-integers", "Boolean, 8/16/32/64-bit integers and native integers under the Phase 08A target layout."),
        SupportedFeature("structured-branches", "Branch targets are instruction boundaries and evaluation stacks are empty at block boundaries."),
        SupportedFeature("explicit-helper-calls", "Only exact identities in the v1 pure helper allowlist."),
        SupportedFeature("managed-references", "Object references and zero-null flow through signatures, locals, SSA phi, calls, allocation and final root-kind metadata; byrefs remain rejected."),
        SupportedFeature("deterministic-object-allocation", "Exact newobj bindings lower to the managed allocation helper followed by an ordinary direct constructor call."),
        SupportedFeature("szarray-core", "Exact SZARRAY bindings lower allocation, length and int32/reference element access to versioned checked runtime helpers; ldelema fails closed."),
        SupportedFeature("utf16-string-literals", "Exact UTF-16 literal bindings lower to deterministic runtime materialization without an interning guarantee."),
        SupportedFeature("blittable-scalar-boxing", "Exact fixed-layout scalar value bindings lower box/unbox.any; reference-bearing value shapes fail closed."),
        SupportedFeature("static-initialization", "Exact static field and type-initializer bindings lower through the runtime exactly-once state machine."),
        UnsupportedFeature("exception-handling", "General CIL EH body import remains outside the closed ordinary importer; use the explicit bounded managed-eh-plan API.", "HCCIL1011"),
        SupportedFeature("managed-eh-plan", "The explicit bounded API admits catch/finally/throw/rethrow metadata and produces final-PC HCEH plus final-frame HCW2; filters, fault and cross-native unwind fail closed."),
        SupportedFeature("exact-aot-generics-core", "Statically reachable closed generic method and constructed reference-type instances receive exact identities and separate bodies; primary non-root static signed owners admit exact struct flags/core ValueType constraint, including final pure scalar dependency proof and private exact signed Int32/Int64 struct-constrained exact register-bounded scalar method with graph-owned final purity/typed-body admission; sharing, dictionaries, open forms and broader constraints are rejected."),
        SupportedFeature("reflection-bounded", "Explicitly rooted public type/member metadata is retained deterministically and queried through versioned runtime helpers."),
        UnsupportedFeature("reflection-dynamic", "Reflection.Emit, dynamic code and unrestricted assembly loading are not qualified.", "HCCIL1013"),
        SupportedFeature("threading-tls", "Managed Thread/TLS and cooperative scheduling use versioned runtime helpers over the language-neutral kernel context boundary."),
        SupportedFeature("async-state-machines", "Async methods are ordinary admitted CIL state machines; bounded Task/continuation/timer behavior is supplied by runtime libraries without backend specialization."),
        UnsupportedFeature("async-preemption", "Architectural preemption and async-specific opcodes/backend paths are neither required nor qualified.", "HCCIL1015"),
        SupportedFeature("pinvoke-interop", "Exact static Cdecl declarations over the blittable integer/native-pointer subset lower to the versioned managed interop dispatch helper; unsupported signatures fail closed."),
        UnsupportedFeature("runtime-fallback", "CoreCLR, JIT and host-code fallback are forbidden.", "HCCIL1017")
    ];

    private static readonly RestrictedCilHelperContractV1[] HelperRows =
    [
        new(
            "System.Array.Clear(System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_array_clear",
            "runtime-helper",
            IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Array.Copy(System.Object,System.Object,System.Int32):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_array_copy_all",
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Array.Copy(System.Object,System.Int32,System.Object,System.Int32,System.Int32):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32,
             RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_array_copy",
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Math.Abs(System.Int64):System.Int64",
            [RestrictedCilTypeV1.Int64],
            RestrictedCilTypeV1.Int64,
            "__hybridcpu_managed_math_abs_i8",
            "runtime-helper",
            IrMemoryEffectKind.None,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Math.Max(System.Int32,System.Int32):System.Int32",
            [RestrictedCilTypeV1.Int32, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.Int32,
            "__hybridcpu_managed_math_max_i4",
            "runtime-helper",
            IrMemoryEffectKind.None,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Object..ctor(System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void,
            "intrinsic-noop",
            "exact managed intrinsic; receiver consumption only",
            IrMemoryEffectKind.None,
            IrArchitecturalEffectKind.None),
        new(
            "System.String.get_Length(System.Object):System.Int32",
            [RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Int32,
            "__hybridcpu_managed_string_length",
            "runtime-helper",
            IrMemoryEffectKind.Read,
            IrArchitecturalEffectKind.Control),
        new(
            "System.String.get_Chars(System.Object,System.Int32):System.UInt16",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.UInt16,
            "__hybridcpu_managed_string_char",
            "runtime-helper",
            IrMemoryEffectKind.Read,
            IrArchitecturalEffectKind.Control),
        new(
            "System.String.Concat(System.Object,System.Object):System.Object",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.ObjectReference,
            "__hybridcpu_managed_string_concat2",
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.String.Concat(System.Object,System.Object,System.Object):System.Object",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference,
             RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.ObjectReference,
            "__hybridcpu_managed_string_concat3",
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.String.op_Equality(System.Object,System.Object):System.Boolean",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Boolean,
            "__hybridcpu_managed_string_equals",
            "runtime-helper",
            IrMemoryEffectKind.Read,
            IrArchitecturalEffectKind.Control),
        new(
            "System.String.op_Inequality(System.Object,System.Object):System.Boolean",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Boolean,
            "__hybridcpu_managed_string_not_equals",
            "runtime-helper",
            IrMemoryEffectKind.Read,
            IrArchitecturalEffectKind.Control),
        new(
            "System.String..ctor(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_string_from_utf16_array",
            "runtime-factory-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray(System.Object,System.UIntPtr):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.NativeUInt],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_initialize_array",
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Exception..ctor(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_exception_ctor_message",
            "runtime-helper",
            IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.FormatException..ctor(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_exception_ctor_message",
            "runtime-helper",
            IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.ArgumentNullException..ctor(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_argument_null_ctor_param_name",
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.ArgumentOutOfRangeException..ctor(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void,
            "__hybridcpu_managed_argument_out_of_range_ctor_param_name",
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control),
        new(
            "System.Exception.get_Message(System.Object):System.Object",
            [RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.ObjectReference,
            "__hybridcpu_managed_exception_get_message",
            "runtime-helper",
            IrMemoryEffectKind.Read,
            IrArchitecturalEffectKind.Control),
        new(
            "HybridCPU.RestrictedRuntime.Int32Helpers.Identity(System.Int32):System.Int32",
            [RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.Int32,
            "ADDI(value,0)",
            "canonical-inline/no-runtime-call",
            IrMemoryEffectKind.None,
            IrArchitecturalEffectKind.None)
    ];

    public static RestrictedCilSupportMatrixV1 Default { get; } = new();

    private RestrictedCilSupportMatrixV1()
    {
        Opcodes = Array.AsReadOnly(OpcodeRows.OrderBy(static row => row.Encoding).ToArray());
        Features = Array.AsReadOnly(FeatureRows.OrderBy(static row => row.Feature, StringComparer.Ordinal).ToArray());
        Helpers = Array.AsReadOnly(HelperRows.OrderBy(static row => row.StableIdentity, StringComparer.Ordinal).ToArray());
        string text = new StringBuilder("hybridcpu.restricted-cil-matrix/v1")
            .Append('|').Append(HybridCpuTargetPlatformContractV1.Default.ContractDigest)
            .Append('|').Append(HybridCpuNativeAbiContractV2.Default.ContractDigest)
            .Append('|').Append(string.Join('|', Opcodes.Select(static row => $"{row.Encoding:x4}:{row.Name}:{row.StackRule}:{row.TypeRule}:{row.CanonicalExpansion}:{row.Support}:{row.FailureCode}")))
            .Append('|').Append(string.Join('|', Features.Select(static row => $"{row.Feature}:{row.Support}:{row.Policy}:{row.FailureCode}")))
            .Append('|').Append(string.Join('|', Helpers.Select(static row => $"{row.StableIdentity}:{string.Join(',', row.ParameterTypes)}:{row.ReturnType}:{row.CanonicalExpansion}:{row.CallingConvention}:{row.MemoryEffects}:{row.ArchitecturalEffects}")))
            .ToString();
        ContractDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public string SchemaId => "hybridcpu.restricted-cil-matrix/v1";
    public IReadOnlyList<RestrictedCilOpcodeContractV1> Opcodes { get; }
    public IReadOnlyList<RestrictedCilFeatureContractV1> Features { get; }
    public IReadOnlyList<RestrictedCilHelperContractV1> Helpers { get; }
    public string ContractDigest { get; }
    public bool HasRuntimeAuthority => false;
    public bool HasTargetLegalityAuthority => false;
    public bool AllowsHostFallback => false;

    public bool TryGetOpcode(ushort encoding, out RestrictedCilOpcodeContractV1? row)
    {
        row = Opcodes.FirstOrDefault(candidate => candidate.Encoding == encoding);
        return row is not null;
    }

    public bool TryGetHelper(string identity, out RestrictedCilHelperContractV1? helper)
    {
        helper = Helpers.FirstOrDefault(candidate => string.Equals(candidate.StableIdentity, identity, StringComparison.Ordinal));
        return helper is not null;
    }

    public static string OptionsDigest(RestrictedCilImportBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        string text = string.Join('|', "hybridcpu.restricted-cil-options/v2",
            HybridCPU.Compiler.Core.IR.HybridCpuSlotModel.StructuralMaskContract,
            budgets.MaximumPeBytes, budgets.MaximumMethodBodyBytes, budgets.MaximumDecodedInstructions,
            budgets.MaximumBasicBlocks, budgets.MaximumArguments, budgets.MaximumLocals,
            budgets.MaximumEvaluationStack, budgets.MaximumCalls);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static RestrictedCilOpcodeContractV1 Supported(ushort encoding, string name, string stack, string type, string expansion) =>
        new(encoding, name, stack, type, expansion, RestrictedCilMatrixSupportV1.Supported, string.Empty);

    private static RestrictedCilOpcodeContractV1 UnsupportedOpcode(ushort encoding, string name, string stack, string type, string expansion, string code) =>
        new(encoding, name, stack, type, expansion, RestrictedCilMatrixSupportV1.Unsupported, code);

    private static RestrictedCilFeatureContractV1 SupportedFeature(string feature, string policy) =>
        new(feature, RestrictedCilMatrixSupportV1.Supported, policy, string.Empty);

    private static RestrictedCilFeatureContractV1 UnsupportedFeature(string feature, string policy, string code) =>
        new(feature, RestrictedCilMatrixSupportV1.Unsupported, policy, code);
}
