using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public enum ManagedEhImportStatusV1 : byte
{
    Success = 0,
    Unsupported = 1,
    InvalidInput = 2,
    BudgetExhausted = 3
}

public enum ManagedEhOperationKindV1 : byte
{
    Throw = 0,
    Rethrow = 1,
    Leave = 2,
    EndFinally = 3
}

public sealed record ManagedEhClausePlanV1(
    HybridCpuManagedEhClauseKindV1 Kind,
    int TryOffset,
    int TryLength,
    int HandlerOffset,
    int HandlerLength,
    string? CatchTypeIdentity,
    ulong CatchTypeId,
    int Ordinal);

public sealed record ManagedEhOperationPlanV1(
    ManagedEhOperationKindV1 Kind,
    int IlOffset,
    int LeaveTarget,
    string RuntimeHelperSymbol);

public sealed record ManagedEhHandlerEntryV1(
    int ClauseOrdinal, int IlOffset, IReadOnlyList<RestrictedCilTypeV1> EvaluationStack,
    int? ExceptionReferenceRegister);

public enum ManagedEhLeaveActionKindV1 : byte
{
    InvokeFinally = 0,
    ReleaseCatchScope = 1
}

public sealed record ManagedEhLeaveActionV1(ManagedEhLeaveActionKindV1 Kind, int ClauseOrdinal, int HandlerOffset);
public sealed record ManagedEhLeaveTransferV1(int IlOffset, int TargetOffset,
    IReadOnlyList<ManagedEhLeaveActionV1> Actions)
{
    public bool ClearsEvaluationStack => true;
}

public sealed record ManagedEhFinallyContinuationStepV1(
    int ClauseOrdinal,
    int HandlerOffset,
    int ReleaseCatchScopesBeforeEntry,
    int? NextClauseOrdinal,
    int NextOffset);

public sealed record ManagedEhFinallyContinuationV1(
    int Token,
    int LeaveOffset,
    int TargetOffset,
    IReadOnlyList<ManagedEhFinallyContinuationStepV1> Steps,
    int ReleaseCatchScopesBeforeTarget);

public sealed record ManagedEhFinallyContinuationPlanV1(
    int ExceptionalToken,
    IReadOnlyList<ManagedEhFinallyContinuationV1> Continuations,
    string Digest)
{
    public const int ReservedExceptionalToken = 0;
}

public sealed record ManagedEhMethodPlanV1(
    string MethodIdentity,
    string InstructionIdentityPrefix,
    int MethodBodySize,
    IReadOnlyList<ManagedEhClausePlanV1> Clauses,
    IReadOnlyList<ManagedEhOperationPlanV1> Operations,
    string ContractDigest)
{
    public ManagedEhControlFlowV1? ControlFlow { get; init; }
    // Local/argument homes are preserved by the transfer, never zeroed at handler entry.
    // These are derived views of digest-bound clauses/operations, not caller-supplied authority.
    public IReadOnlyList<ManagedEhHandlerEntryV1> HandlerEntries => Clauses.Select(clause =>
        new ManagedEhHandlerEntryV1(clause.Ordinal, clause.HandlerOffset,
            clause.Kind == HybridCpuManagedEhClauseKindV1.Catch
                ? new[] { RestrictedCilTypeV1.ObjectReference } : Array.Empty<RestrictedCilTypeV1>(),
            clause.Kind == HybridCpuManagedEhClauseKindV1.Catch ? 10 : null)).ToArray();

    public IReadOnlyList<ManagedEhLeaveTransferV1> LeaveTransfers => Operations
        .Where(operation => operation.Kind == ManagedEhOperationKindV1.Leave)
        .Select(operation => new ManagedEhLeaveTransferV1(operation.IlOffset, operation.LeaveTarget,
            Clauses.Select(clause => (Clause: clause,
                    Start: clause.Kind == HybridCpuManagedEhClauseKindV1.Finally ? clause.TryOffset : clause.HandlerOffset,
                    Length: clause.Kind == HybridCpuManagedEhClauseKindV1.Finally ? clause.TryLength : clause.HandlerLength))
                .Where(region => Contains(region.Start, region.Length, operation.IlOffset) &&
                    !Contains(region.Start, region.Length, operation.LeaveTarget))
                .OrderBy(region => region.Length).ThenByDescending(region => region.Start)
                .ThenBy(region => region.Clause.Ordinal)
                .Select(region => new ManagedEhLeaveActionV1(
                    region.Clause.Kind == HybridCpuManagedEhClauseKindV1.Finally
                        ? ManagedEhLeaveActionKindV1.InvokeFinally : ManagedEhLeaveActionKindV1.ReleaseCatchScope,
                    region.Clause.Ordinal, region.Clause.HandlerOffset)).ToArray())).ToArray();

    public ManagedEhFinallyContinuationPlanV1 FinallyContinuations =>
        ManagedEhFinallyContinuationPlannerV1.Create(this);

    private static bool Contains(int start, int length, int offset) =>
        offset >= start && (long)offset < (long)start + length;
}

public static class ManagedEhFinallyContinuationPlannerV1
{
    public static ManagedEhFinallyContinuationPlanV1 Create(ManagedEhMethodPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<ManagedEhFinallyContinuationV1> continuations = new List<ManagedEhFinallyContinuationV1>();
        int token = 1;
        foreach (ManagedEhLeaveTransferV1 leave in plan.LeaveTransfers.OrderBy(static row => row.IlOffset))
        {
            ManagedEhLeaveActionV1[] finallyActions = leave.Actions
                .Where(static action => action.Kind == ManagedEhLeaveActionKindV1.InvokeFinally).ToArray();
            if (finallyActions.Length == 0) continue;
            if (token > HybridCpuManagedEhSchemaV1.MaximumOperationsPerMethod)
                throw new InvalidOperationException("Managed finally continuation-token budget exceeded.");
            ManagedEhFinallyContinuationStepV1[] steps = new ManagedEhFinallyContinuationStepV1[finallyActions.Length];
            int stepIndex = 0;
            int pendingCatchReleases = 0;
            foreach (ManagedEhLeaveActionV1 action in leave.Actions)
            {
                if (action.Kind == ManagedEhLeaveActionKindV1.ReleaseCatchScope)
                {
                    pendingCatchReleases++;
                    continue;
                }
                ManagedEhLeaveActionV1? next = stepIndex + 1 < finallyActions.Length ? finallyActions[stepIndex + 1] : null;
                if (!plan.Clauses.Any(clause => clause.Ordinal == action.ClauseOrdinal &&
                        clause.Kind == HybridCpuManagedEhClauseKindV1.Finally && clause.HandlerOffset == action.HandlerOffset))
                    throw new InvalidOperationException("Managed finally continuation references a non-finally clause.");
                steps[stepIndex++] = new(action.ClauseOrdinal, action.HandlerOffset, pendingCatchReleases,
                    next?.ClauseOrdinal, next?.HandlerOffset ?? leave.TargetOffset);
                pendingCatchReleases = 0;
            }
            continuations.Add(new(token++, leave.IlOffset, leave.TargetOffset, steps, pendingCatchReleases));
        }
        ManagedEhFinallyContinuationV1[] ordered = continuations.OrderBy(static row => row.Token).ToArray();
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-finally-continuation/v1", plan.ContractDigest,
            ManagedEhFinallyContinuationPlanV1.ReservedExceptionalToken,
            string.Join(';', ordered.Select(row =>
                $"{row.Token}:{row.LeaveOffset}:{row.TargetOffset}:" +
                string.Join(',', row.Steps.Select(step =>
                    $"release={step.ReleaseCatchScopesBeforeEntry}:{step.ClauseOrdinal}@{step.HandlerOffset}->{step.NextClauseOrdinal?.ToString() ?? "target"}@{step.NextOffset}")) +
                $":release-target={row.ReleaseCatchScopesBeforeTarget}"))));
        return new(ManagedEhFinallyContinuationPlanV1.ReservedExceptionalToken, ordered, digest);
    }
}

public sealed record ManagedEhImportResultV1(
    ManagedEhImportStatusV1 Status,
    ManagedEhMethodPlanV1? Plan,
    string Code,
    string Reason)
{
    public ManagedEhControlFlowV1? ControlFlow { get; init; }
}

public enum ManagedEhEdgeKindV1 : byte
{
    Normal = 0,
    LeaveContinuation = 1,
    ExceptionDispatchCandidate = 2
}

public sealed record ManagedEhEdgeV1(int SourceOffset, int TargetOffset, ManagedEhEdgeKindV1 Kind,
    int? ClauseOrdinal = null);

public sealed record ManagedEhBasicBlockV1(int Id, int StartOffset, int EndOffsetExclusive,
    IReadOnlyList<int> InstructionOffsets, IReadOnlyList<int> HandlerClauseOrdinals);

// SourceOffset remains explicit: an exceptional edge observes pre-instruction state,
// including when its source is in the middle of the basic block.
public sealed record ManagedEhBlockEdgeV1(int SourceBlock, int TargetBlock, int SourceOffset,
    ManagedEhEdgeKindV1 Kind, int? ClauseOrdinal);

public sealed record ManagedEhStateHomeV1(string Slot, RestrictedCilTypeV1 Type,
    string? AggregateIdentity, bool InitializedAtMethodEntry)
{
    public bool IsObjectRoot => Type == RestrictedCilTypeV1.ObjectReference;
}

public sealed record ManagedEhTypedValueV1(string Identity, RestrictedCilTypeV1 Type, bool Initialized,
    ulong? Constant, string? AggregateIdentity, string? ReceiverIdentity, ulong? FunctionPointerSignatureId);
public sealed record ManagedEhTypedStateV1(int IlOffset, IReadOnlyList<ManagedEhTypedValueV1> Stack,
    IReadOnlyList<ManagedEhTypedValueV1> Locals, IReadOnlyList<ManagedEhTypedValueV1> Arguments);
public sealed record ManagedEhTypedEdgeStateV1(ManagedEhEdgeV1 Edge, ManagedEhTypedStateV1 State);
public sealed record ManagedEhTypedDataflowV1(IReadOnlyList<ManagedEhTypedStateV1> Entries,
    IReadOnlyList<ManagedEhTypedEdgeStateV1> EdgeStates, string Digest);

public enum ManagedEhHomeAccessKindV1 : byte { Initialize = 0, StoreAfterDefinition = 1, ReloadAtHandlerEntry = 2 }
public enum ManagedEhHomeValueKindV1 : byte { ExistingSsaValue = 0, ZeroConstant = 1, NonZeroConstant = 2, ReloadDefinition = 3 }
public sealed record ManagedEhHomeAccessV1(ManagedEhHomeAccessKindV1 Kind, string Slot,
    int IlOffset, RestrictedCilTypeV1 Type, bool IsObjectRoot, string ValueIdentity,
    ManagedEhHomeValueKindV1 ValueKind, ulong? ConstantValue);
public sealed record ManagedEhHomeAccessPlanV1(IReadOnlyList<ManagedEhHomeAccessV1> Accesses, string Digest);
public enum ManagedEhInsertionPlacementV1 : byte { Before = 0, After = 1 }
public sealed record ManagedEhHomeInsertionV1(int AnchorIlOffset, ManagedEhInsertionPlacementV1 Placement,
    ManagedEhHomeAccessV1 Access, IrInstruction Instruction);

/// <summary>Instruction-level EH graph for subsequent typed dataflow. Dispatch candidates
/// are conservative exceptional edges, never ordinary branches or promises that a catch matches.
/// Leave continuation is traversable only after its ordered cleanup actions complete.</summary>
public sealed record ManagedEhControlFlowV1(IReadOnlyList<int> InstructionOffsets,
    IReadOnlyList<ManagedEhEdgeV1> Edges, string Digest)
{
    public IReadOnlyList<string> RequiredStateHomes { get; init; } = [];
    public IReadOnlyList<ManagedEhStateHomeV1> StateHomes { get; init; } = [];
    public IReadOnlyList<ManagedEhBasicBlockV1> Blocks { get; init; } = [];
    public IReadOnlyList<ManagedEhBlockEdgeV1> BlockEdges { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<string>> LiveStateAtEntry { get; init; } =
        new Dictionary<int, IReadOnlyList<string>>();
}

public static class ManagedEhPlanContractV1
{
    public static string ComputeDigest(string methodIdentity, string instructionIdentityPrefix, int methodBodySize,
        IReadOnlyList<ManagedEhClausePlanV1> clauses, IReadOnlyList<ManagedEhOperationPlanV1> operations) =>
        HybridCpuPlatformContractV1.Hash(string.Join('|', HybridCpuManagedEhSchemaV1.SchemaId, methodIdentity,
            instructionIdentityPrefix, methodBodySize,
            string.Join(';', clauses.Select(static row =>
                $"{row.Kind}:{row.TryOffset}:{row.TryLength}:{row.HandlerOffset}:{row.HandlerLength}:{row.CatchTypeIdentity}:{row.CatchTypeId}:{row.Ordinal}")),
            string.Join(';', operations.Select(static row =>
                $"{row.Kind}:{row.IlOffset}:{row.LeaveTarget}:{row.RuntimeHelperSymbol}"))));
}

public sealed record ManagedEhNativeOperationV1(
    ManagedEhOperationKindV1 Kind,
    int NativeOffsetBytes,
    int LeaveTargetOffsetBytes,
    string RuntimeHelperSymbol);

public sealed record ManagedEhNativeFinallyContinuationStepV1(
    int ClauseOrdinal,
    int HandlerOffsetBytes,
    int HandlerEndOffsetBytes,
    int ReleaseCatchScopesBeforeEntry,
    int? NextClauseOrdinal,
    int NextOffsetBytes);

public sealed record ManagedEhNativeFinallyContinuationV1(
    int Token,
    int LeaveOffsetBytes,
    int TargetOffsetBytes,
    IReadOnlyList<ManagedEhNativeFinallyContinuationStepV1> Steps,
    int ReleaseCatchScopesBeforeTarget);

public sealed record ManagedEhNativeFinallyContinuationPlanV1(
    int TokenSlotOffsetFromAdjustedStackPointerBytes,
    IReadOnlyList<ManagedEhNativeFinallyContinuationV1> Continuations,
    byte[] Encoding,
    string Digest);

public sealed record ManagedEhFinalizationArtifactV1(
    HybridCpuManagedMetadataStatusV1 Status,
    string Reason,
    IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> Clauses,
    IReadOnlyList<ManagedEhNativeOperationV1> Operations,
    byte[] EhInfo,
    byte[] UnwindInfo,
    HybridCpuManagedUnwindRecordV2? Unwind,
    IReadOnlyList<HybridCpuObjectSectionV1> ObjectSections,
    string ResultDigest,
    ManagedEhNativeFinallyContinuationPlanV1? FinallyContinuations = null);


