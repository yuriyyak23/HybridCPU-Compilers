using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public enum ScalarControlFlowV2LinkStatusV1 : byte
{
    Success = 0,
    InvalidInput = 1,
    BudgetExhausted = 2,
    BackendRejected = 3,
    ObjectRejected = 4,
    LinkRejected = 5,
    ImageRejected = 6
}

public sealed record ScalarControlFlowV2LinkDiagnosticV1(string Code, string Message, string StableIdentity);

public sealed record ScalarControlFlowV2MethodObjectV1(
    string MethodIdentity,
    string ModuleIdentity,
    string AllocationWitnessDigest,
    string CodeSha256,
    int CodeBytes,
    int FrameSizeBytes,
    int SpillCount,
    HybridCpuObjectArtifactV1 ObjectArtifact,
    byte[]? GcInfo = null,
    string? GcInfoDigest = null,
    byte[]? UnwindInfo = null,
    string? UnwindInfoDigest = null,
    byte[]? EhInfo = null,
    string? EhInfoDigest = null,
    byte[]? FinallyInfo = null,
    string? FinallyInfoDigest = null);

public sealed record ManagedRecursionStackEvidenceV1(
    int MaximumDynamicDepth,
    int MaximumStackBytes,
    int RequiredStackBytes,
    string BoundModel,
    string EvidenceDigest);

public sealed record ScalarControlFlowV2LinkedProgramV1(
    ScalarControlFlowV2LinkStatusV1 Status,
    string EntryIdentity,
    IReadOnlyList<ScalarControlFlowV2MethodObjectV1> MethodObjects,
    HybridCpuStaticLinkArtifactV1? LinkedImage,
    HybridCpuRestrictedImageV1? RestrictedImage,
    string OrderedObjectDigest,
    string ProvenanceDigest,
    IReadOnlyList<ScalarControlFlowV2LinkDiagnosticV1> Diagnostics,
    ManagedRecursionStackEvidenceV1? RecursionStackEvidence = null,
    IReadOnlyList<string>? LinkedRuntimeModuleIdentities = null)
{
    public bool HasRuntimeAuthority => false;
    public bool HasExecutionAuthority => false;
    public bool HasPublicationAuthority => false;
}

/// <summary>
/// Phase 04 bridge from the admitted managed body world to the existing HCO/static-link/image path.
/// It emits exactly one canonical HCO object per method and does not own reachability or runtime execution.
/// </summary>
public sealed class ScalarControlFlowV2ObjectLinkerV1
{
    public const string SchemaId = "hybridcpu.scalar-control-flow-v2-object-link/v1";
    private static readonly HybridCpuMiiResourceModelV1 ResourceModel =
        HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    public ScalarControlFlowV2LinkedProgramV1 Link(
        ManagedCallGraphCompilationV1 compilation,
        string entryIdentity)
        => Link(CancellationToken.None, compilation, entryIdentity);

    /// <summary>Cooperative method/backend-stage cancellation; object/link/image internals stay owned by their existing stages.</summary>
    public ScalarControlFlowV2LinkedProgramV1 Link(
        CancellationToken cancellationToken,
        ManagedCallGraphCompilationV1 compilation,
        string entryIdentity)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        cancellationToken.ThrowIfCancellationRequested();
        if (compilation.Status != RestrictedCilImportStatusV1.Success || compilation.Graph is null ||
            string.IsNullOrWhiteSpace(entryIdentity) ||
            !compilation.Graph.RootIdentities.Contains(entryIdentity, StringComparer.Ordinal))
            return Failure(ScalarControlFlowV2LinkStatusV1.InvalidInput, entryIdentity,
                "HCSCF-LINK4001", "A successful admitted graph and exact root entry identity are required.");

        ScalarControlFlowV2Budgets budgets = ScalarControlFlowV2ProfileContractV1.Default.Budgets;
        bool hasNativeEh = compilation.Methods.Any(static method => method.Import.RequiresNativeExceptionTransfer);
        ManagedCompiledMethodV1? incompleteFinally = compilation.Methods.FirstOrDefault(static method =>
            method.Import.ManagedEhAnalysis is { } plan &&
            plan.Clauses.Any(static clause => clause.Kind == HybridCpuManagedEhClauseKindV1.Finally) &&
            (method.Import.ManagedEhLoweringEvidence is not { } evidence ||
             evidence.EndFinallyTransferCount != plan.Operations.Count(static operation =>
                 operation.Kind == ManagedEhOperationKindV1.EndFinally) ||
             method.Import.Program?.Instructions.Any(static instruction =>
                 instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_endfinally") != true));
        if (incompleteFinally is not null)
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4014", "Managed finally requires exact endfinally lowering evidence and the native continuation call in every admitted handler.",
                incompleteFinally.Identity.StableIdentity);
        bool hasStringLiterals = compilation.StringLiteralPlan is { Bindings.Count: > 0 };
        HashSet<string> nativeCallTargets = compilation.Methods.SelectMany(static method =>
                method.Import.Program?.Instructions.Select(static instruction =>
                    instruction.Annotation.BranchTargetSymbolName).Where(static target => target is not null) ?? [])
            .Select(static target => target!).ToHashSet(StringComparer.Ordinal);
        bool ValidDigest(string value) => value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));
        bool ValidCallerStorage(ManagedCompiledMethodV1 caller)
        {
            IReadOnlyList<ManagedReceiverCallerStoragePlanV1>? plans = caller.Import.ReceiverCallerStoragePlans;
            IrProgram? callerProgram = caller.Import.Program;
            if (plans is not { Count: > 0 } || callerProgram is null) return false;
            foreach (ManagedReceiverCallerStoragePlanV1 storage in plans)
            {
                if (storage.LocalIndex < 0 || string.IsNullOrWhiteSpace(storage.ScopedTypeIdentity) ||
                    storage.PayloadSizeBytes is <= 0 or > 16 || storage.PayloadAlignmentBytes is not (1 or 2 or 4 or 8 or 16) ||
                    string.IsNullOrWhiteSpace(storage.FrameSlotIdentity) || !ValidDigest(storage.StorageProofDigest) ||
                    storage.Calls is not { Count: > 0 }) return false;
                ManagedReceiverCallerStorageCallV1 firstCall = storage.Calls.OrderBy(static call => call.CilOffset).First();
                ManagedCompiledMethodV1? initializer = compilation.Methods.SingleOrDefault(method =>
                    method.Identity.StableIdentity == firstCall.CalleeIdentity);
                if (initializer?.Identity.MethodName != ".ctor" || initializer.Import.ReceiverAbi is not { } initializerReceiver ||
                    initializerReceiver.PayloadSizeBytes != storage.PayloadSizeBytes ||
                    !FullyInitializesReceiver(initializer, storage.PayloadSizeBytes)) return false;
                IrInstruction[] addresses = callerProgram.Instructions.Where(instruction =>
                    instruction.Annotation.FixedFrameSlotIdentity == storage.FrameSlotIdentity).ToArray();
                if (addresses.Length == 0 || addresses.Any(static instruction =>
                        instruction.Opcode != HybridCpuOpcode.ADDI ||
                        instruction.CanonicalType.Kind != IrCanonicalValueKind.ManagedByRef)) return false;
                string expectedStorageProof = Hash(string.Join('|', "hybridcpu.receiver-caller-storage/v1",
                    storage.LocalIndex, storage.ScopedTypeIdentity, storage.PayloadSizeBytes,
                    storage.PayloadAlignmentBytes, storage.FrameSlotIdentity, firstCall.CalleePlanDigest,
                    string.Join(',', addresses.Select(static instruction => instruction.SourceSpan?.StartOffset ?? -1)
                        .OrderBy(static offset => offset))));
                if (storage.StorageProofDigest != expectedStorageProof) return false;
                foreach (ManagedReceiverCallerStorageCallV1 call in storage.Calls)
                {
                    ManagedCompiledMethodV1? callee = compilation.Methods.SingleOrDefault(method =>
                        method.Identity.StableIdentity == call.CalleeIdentity);
                    ManagedReceiverAbiPlanV1? receiver = callee?.Import.ReceiverAbi;
                    IrInstruction? transfer = callerProgram.Instructions.SingleOrDefault(instruction =>
                        instruction.SourceSpan?.StartOffset == call.CilOffset &&
                        instruction.Annotation.BranchTargetSymbolName == call.CalleeIdentity &&
                        instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
                    if (receiver is null || receiver.ScopedTypeIdentity != storage.ScopedTypeIdentity ||
                        receiver.PayloadSizeBytes != storage.PayloadSizeBytes ||
                        receiver.PayloadAlignmentBytes != storage.PayloadAlignmentBytes ||
                        receiver.PlanDigest != call.CalleePlanDigest || !receiver.NonEscaping || !receiver.NoSafepoints ||
                        transfer is null || transfer.Annotation.IsManagedGcSafepoint ||
                        !ValidDigest(call.AbiLayoutDigest) || !ValidDigest(call.CallProofDigest) ||
                        call.CallProofDigest != Hash($"receiver-call/v1|{storage.StorageProofDigest}|{call.CilOffset}|" +
                            $"{call.CalleeIdentity}|{call.CalleePlanDigest}|{call.AbiLayoutDigest}")) return false;
                }
            }
            return true;
        }
        static bool FullyInitializesReceiver(ManagedCompiledMethodV1 initializer, int payloadSize)
        {
            IrInstruction[] writes = initializer.Import.Program?.Instructions.Where(static instruction =>
                instruction.SideEffects.Memory.Kind == IrMemoryEffectKind.Write).ToArray() ?? [];
            if (writes.Length != 1) return false;
            int width = writes[0].Opcode switch
            {
                HybridCpuOpcode.SB => 1, HybridCpuOpcode.SH => 2,
                HybridCpuOpcode.SW => 4, HybridCpuOpcode.SD => 8, _ => 0
            };
            return width == payloadSize;
        }
        bool ReceiverUnproven(ManagedCompiledMethodV1 method)
        {
            bool hasByRef = method.Import.Program?.ValueFlow.Values.Any(static value =>
                value.VirtualClass == IrVirtualValueClass.ManagedByRef) == true;
            if (method.Import.ReceiverAbi is { } receiver)
            {
                if (compilation.Graph.RootIdentities.Contains(method.Identity.StableIdentity, StringComparer.Ordinal))
                    return true;
                if (!nativeCallTargets.Contains(method.Identity.StableIdentity)) return false;
                return !compilation.Methods.Any(caller => caller.Import.ReceiverCallerStoragePlans?.Any(storage =>
                    storage.Calls.Any(call => call.CalleeIdentity == method.Identity.StableIdentity &&
                        call.CalleePlanDigest == receiver.PlanDigest)) == true && ValidCallerStorage(caller));
            }
            return hasByRef && !ValidCallerStorage(method);
        }
        ManagedCompiledMethodV1? receiverRejected = compilation.Methods.FirstOrDefault(ReceiverUnproven);
        if (receiverRejected is not null)
        {
            ManagedReceiverAbiPlanV1? receiver = receiverRejected.Import.ReceiverAbi;
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4012", receiver is null
                    ? $"Method '{receiverRejected.Identity.StableIdentity}' retains an unbound managed-byref value without a receiver loan plan."
                    : $"Method '{receiverRejected.Identity.StableIdentity}' requires receiver loan '{receiver.ScopedTypeIdentity}' " +
                      $"({receiver.PayloadSizeBytes} bytes, alignment {receiver.PayloadAlignmentBytes}); caller-side non-null bounded storage and lifetime proof are not linked.",
                receiverRejected.Identity.StableIdentity);
        }
        bool hasInterfaceDispatch = nativeCallTargets.Contains("__hybridcpu_managed_resolve_interface");
        bool hasDoomClockService = nativeCallTargets.Contains(HybridCpuManagedDoomClockEmitterV1.Symbol);
        bool hasFramebufferService = nativeCallTargets.Contains(HybridCpuManagedFramebufferEmitterV1.Symbol);
        bool hasConsoleWriteService = nativeCallTargets.Contains(HybridCpuManagedConsoleWriteEmitterV1.Symbol);
        bool hasDoomWaitService = nativeCallTargets.Contains(HybridCpuManagedDoomWaitEmitterV1.Symbol);
        bool hasBootBlobService = nativeCallTargets.Contains(HybridCpuManagedBootBlobEmitterV1.Symbol);
        bool hasGuestProcessExit = nativeCallTargets.Contains(HybridCpuManagedGuestProcessExitEmitterV1.Symbol);
        bool hasFramebufferPresent = nativeCallTargets.Contains(HybridCpuManagedFramebufferPresentEmitterV1.Symbol);
        bool hasConsoleTitle = nativeCallTargets.Contains(HybridCpuManagedConsoleTitleEmitterV1.Symbol);
        bool hasInputPull = nativeCallTargets.Contains(HybridCpuManagedInputPullEmitterV1.Symbol);
        bool hasPalette = nativeCallTargets.Contains(HybridCpuManagedPaletteEmitterV1.Symbol);
        bool hasGuestService = hasDoomClockService || hasFramebufferService || hasConsoleWriteService || hasDoomWaitService || hasBootBlobService || hasGuestProcessExit || hasFramebufferPresent || hasConsoleTitle || hasInputPull || hasPalette;
        HashSet<string> admittedGuestServiceTargets = nativeCallTargets
            .Where(IsGuestServiceTarget).ToHashSet(StringComparer.Ordinal);
        ManagedDispatchCallPlanV1? unresolvedInterface = compilation.DispatchCallPlans?
            .FirstOrDefault(static plan => plan.Kind == RestrictedCilDispatchKindV1.Interface &&
                (plan.RuntimeExternal || plan.InterfaceTypeId == 0 || plan.Candidates.Count == 0));
        if (hasInterfaceDispatch && unresolvedInterface is not null)
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4011", $"Method '{unresolvedInterface.CallerIdentity}' at IL_{unresolvedInterface.CilOffset:x4} " +
                $"requires '{unresolvedInterface.DeclarationIdentity}' (interface={unresolvedInterface.InterfaceTypeId}, " +
                $"slot={unresolvedInterface.SlotId}, external={unresolvedInterface.RuntimeExternal}); " +
                "no image-owned implementation/guest-service binding is available.", unresolvedInterface.CallerIdentity);
        if (hasInterfaceDispatch && (compilation.DispatchCallPlans is not { Count: > 0 } ||
            compilation.DispatchCallPlans.Any(static plan => plan.Kind == RestrictedCilDispatchKindV1.Interface &&
                (plan.InterfaceTypeId == 0 || plan.Candidates.Count == 0))))
        {
            ManagedClosedInterfacePlanV1? plan = compilation.ClosedInterfacePlans?.FirstOrDefault();
            ManagedCompiledMethodV1 owner = compilation.Methods.First(method => method.Import.Program!.Instructions.Any(
                instruction => instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_resolve_interface"));
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4011", $"Method '{owner.Identity.StableIdentity}' retains interface dispatch; '{plan?.StableIdentity ?? "unknown-interface"}' requires exact implementation slots and image type registrations.", owner.Identity.StableIdentity);
        }
        bool hasVirtualDispatch = nativeCallTargets.Contains(HybridCpuManagedDispatchResolverEmitterV1.VirtualSymbol);
        bool needsEnsureTypeInitialized = nativeCallTargets.Contains(
            HybridCpuManagedEnsureTypeInitializedEmitterV1.Symbol);
        bool needsStaticStoreInt32 = nativeCallTargets.Contains(
            HybridCpuManagedStaticStoreInt32EmitterV1.Symbol);
        bool needsStaticStoreReference = nativeCallTargets.Contains(
            HybridCpuManagedStaticStoreReferenceEmitterV1.Symbol);
        bool needsStaticLoadInt32 = nativeCallTargets.Contains(
            HybridCpuManagedStaticLoadInt32EmitterV1.Symbol);
        bool needsStaticLoadReference = nativeCallTargets.Contains(
            HybridCpuManagedStaticLoadReferenceEmitterV1.Symbol);
        bool needsNullCheck = nativeCallTargets.Contains(HybridCpuManagedNullCheckEmitterV1.Symbol);
        bool needsArrayStoreInt32 = nativeCallTargets.Contains(HybridCpuManagedArrayStoreInt32EmitterV1.Symbol);
        bool needsArrayStoreInt8 = nativeCallTargets.Contains(HybridCpuManagedArrayStoreInt8EmitterV1.Symbol);
        bool needsArrayStoreInt16 = nativeCallTargets.Contains(HybridCpuManagedArrayStoreInt16EmitterV1.Symbol);
        bool needsArrayStoreReference = nativeCallTargets.Contains(HybridCpuManagedArrayStoreReferenceEmitterV1.Symbol);
        bool needsArrayLoadReference = nativeCallTargets.Contains(HybridCpuManagedArrayLoadReferenceEmitterV1.Symbol);
        bool needsArrayLoadInt32 = nativeCallTargets.Contains(HybridCpuManagedArrayLoadInt32EmitterV1.Symbol);
        bool needsArrayLoadInt64 = nativeCallTargets.Contains(HybridCpuManagedArrayLoadInt64EmitterV1.Symbol);
        bool needsArrayLoadUInt8 = nativeCallTargets.Contains(HybridCpuManagedArrayLoadUInt8EmitterV1.Symbol);
        bool needsArrayLoadInt16 = nativeCallTargets.Contains(HybridCpuManagedArrayLoadInt16EmitterV1.Symbol);
        bool needsArrayLoadUInt16 = nativeCallTargets.Contains(HybridCpuManagedArrayLoadUInt16EmitterV1.Symbol);
        bool needsArrayCopyAll = nativeCallTargets.Contains(HybridCpuManagedArrayCopyAllEmitterV1.Symbol);
        bool needsArrayCopy = nativeCallTargets.Contains(HybridCpuManagedArrayCopyEmitterV1.Symbol);
        bool needsIsInstance = nativeCallTargets.Contains(HybridCpuManagedIsInstanceEmitterV1.Symbol);
        bool needsInitializeArray = nativeCallTargets.Contains(HybridCpuManagedInitializeArrayEmitterV1.Symbol);
        bool needsStringFromUtf16Array = nativeCallTargets.Contains(HybridCpuManagedStringFromUtf16ArrayEmitterV1.Symbol);
        bool needsNewArray = nativeCallTargets.Contains(HybridCpuManagedNewArrayEmitterV1.Symbol);
        bool needsAllocateObject = nativeCallTargets.Contains(HybridCpuManagedAllocateObjectEmitterV1.Symbol);
        bool needsDivideUInt32 = nativeCallTargets.Contains(HybridCpuManagedDivideUInt32EmitterV1.Symbol);
        bool needsDivideInt32 = nativeCallTargets.Contains(HybridCpuManagedDivideInt32EmitterV1.Symbol);
        bool needsRemainderInt32 = nativeCallTargets.Contains(HybridCpuManagedRemainderInt32EmitterV1.Symbol);
        bool needsDivideInt64 = nativeCallTargets.Contains(HybridCpuManagedDivideInt64EmitterV1.Symbol);
        bool needsMathAbsInt64 = nativeCallTargets.Contains(HybridCpuManagedMathAbsInt64EmitterV1.Symbol);
        bool needsNullableValue = nativeCallTargets.Contains(HybridCpuManagedNullableValueEmitterV1.Symbol);
        if (needsNullableValue && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
            row.StableIdentity == "System.InvalidOperationException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4047",
                "Nullable.Value requires exact InvalidOperationException metadata and native EH transfer.", entryIdentity);
        bool needsMathMaxInt32 = nativeCallTargets.Contains(HybridCpuManagedMathMaxInt32EmitterV1.Symbol);
        bool needsArrayEmpty = nativeCallTargets.Contains(HybridCpuManagedArrayEmptyEmitterV1.Symbol);
        bool needsArrayLength = nativeCallTargets.Contains(HybridCpuManagedArrayLengthEmitterV1.Symbol);
        bool needsStringCharacter = nativeCallTargets.Contains(HybridCpuManagedStringCharacterEmitterV1.Symbol);
        bool needsStringLength = nativeCallTargets.Contains(HybridCpuManagedStringLengthEmitterV1.Symbol);
        bool needsExceptionCtorMessage = nativeCallTargets.Contains(HybridCpuManagedExceptionCtorMessageEmitterV1.Symbol);
        bool needsArgumentNullCtor = nativeCallTargets.Contains(HybridCpuManagedArgumentNullCtorEmitterV1.Symbol);
        bool needsArgumentOutOfRangeCtor = nativeCallTargets.Contains(HybridCpuManagedArgumentOutOfRangeCtorEmitterV1.Symbol);
        bool needsStringNotEquals = nativeCallTargets.Contains(HybridCpuManagedStringNotEqualsEmitterV1.Symbol);
        bool needsArrayClear = nativeCallTargets.Contains(HybridCpuManagedArrayClearEmitterV1.Symbol);
        bool needsStringConcat2 = nativeCallTargets.Contains(HybridCpuManagedStringConcat2EmitterV1.Symbol);
        bool needsStringConcat3 = nativeCallTargets.Contains(HybridCpuManagedStringConcat3EmitterV1.Symbol);
        if (needsArrayLoadUInt8 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4035", "Managed u1 array load requires exact NullReferenceException and IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayLoadInt16 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4039", "Managed i2 array load requires exact NullReferenceException and IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayLoadUInt16 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4040", "Managed u2 array load requires exact NullReferenceException and IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayCopyAll && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentNullException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentOutOfRangeException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArrayTypeMismatchException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4041",
                "Array.Copy(source,destination,length) requires exact ArgumentNullException, ArgumentOutOfRangeException, ArgumentException and ArrayTypeMismatchException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayCopy && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentNullException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentOutOfRangeException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArrayTypeMismatchException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4046",
                "Array.Copy(source,sourceIndex,destination,destinationIndex,length) requires exact ArgumentNullException, ArgumentOutOfRangeException, ArgumentException and ArrayTypeMismatchException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsStringCharacter && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4031", "Managed string character access requires exact NullReferenceException and IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsStringLength && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4032", "Managed string length requires the exact NullReferenceException image type " +
                "plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsStringConcat2 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4034", "Managed String.Concat(string,string) requires the exact OutOfMemoryException " +
                "image type plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsStringConcat3 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4043", "Managed String.Concat(string,string,string) requires the exact OutOfMemoryException " +
                "image type plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsStringFromUtf16Array && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4045",
                "Managed String(char[]) requires exact NullReferenceException and OutOfMemoryException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArgumentNullCtor && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.String" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentNullException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4047",
                "ArgumentNullException(string) requires exact String, ArgumentNullException and OutOfMemoryException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArgumentOutOfRangeCtor && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.String" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentOutOfRangeException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4048",
                "ArgumentOutOfRangeException(string) requires exact String, ArgumentOutOfRangeException and OutOfMemoryException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsStringNotEquals && compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.String" && row.Descriptor is { Kind: HybridCpuManagedTypeKindV1.String }) != true)
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4049",
                "String inequality requires the exact shape-aware System.String image descriptor.", entryIdentity);
        if (needsArrayClear && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.String" && row.Descriptor is { Kind: HybridCpuManagedTypeKindV1.String }) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.ArgumentNullException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4050",
                "Array.Clear(Array) requires exact String, ArgumentNullException and OutOfMemoryException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayLength && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4029", "Managed array length requires the exact NullReferenceException image type " +
                "plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayEmpty && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4028", "Managed Array.Empty requires the exact OutOfMemoryException image type " +
                "plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsDivideUInt32 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.DivideByZeroException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4026",
                "Checked UInt32 division requires the exact DivideByZeroException image type plus native " +
                "handled/unhandled EH transfer.", entryIdentity);
        if (needsDivideInt32 && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.DivideByZeroException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OverflowException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4036",
                "Checked Int32 division requires exact DivideByZeroException and OverflowException image types " +
                "plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsRemainderInt32 && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.DivideByZeroException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OverflowException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4042",
                "Checked Int32 remainder requires exact DivideByZeroException and OverflowException image types " +
                "plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsDivideInt64 && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.DivideByZeroException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OverflowException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4037",
                "Checked Int64 division requires exact DivideByZeroException and OverflowException image types " +
                "plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsMathAbsInt64 && (!hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OverflowException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4038",
                "Checked Math.Abs(Int64) requires the exact OverflowException image type " +
                "plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsAllocateObject && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.OutOfMemoryException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4024",
                "Managed object allocation requires the exact OutOfMemoryException image type plus native " +
                "handled/unhandled EH transfer.", entryIdentity);
        if (needsNewArray && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.OverflowException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.OutOfMemoryException" &&
                row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4023",
                "Managed newarr requires exact OverflowException and OutOfMemoryException image types plus native " +
                "handled/unhandled EH transfer.", entryIdentity);
        if (needsInitializeArray && (compilation.FieldData is not { Count: > 0 } || !hasNativeEh ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" &&
                row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4022",
                "InitializeArray requires immutable FieldRVA bootstrap registrations, exact NullReferenceException " +
                "metadata and native EH transfer.", entryIdentity);
        if (needsNullCheck && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true))
        {
            ManagedCompiledMethodV1 owner = compilation.Methods.First(method =>
                method.Import.Program?.Instructions.Any(static instruction =>
                    instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check") == true);
            IrInstruction site = owner.Import.Program!.Instructions.First(static instruction =>
                instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check");
            string offset = site.SourceSpan is null ? "unavailable" : $"IL_{site.SourceSpan.StartOffset:x4}";
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4019",
                $"Method '{owner.Identity.StableIdentity}' (assembly={owner.Identity.AssemblyIdentity}) at {offset} " +
                "requires exact NullReferenceException semantics, but its exact image type and native handled/unhandled " +
                "EH transfer are not both available. A native no-op or process-exit substitution is forbidden.",
                owner.Identity.StableIdentity);
        }
        if (needsArrayStoreInt32 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" &&
                row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4020", "Managed i4 array store requires exact NullReferenceException and " +
                "IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayStoreInt8 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4030", "Managed i1 array store requires exact NullReferenceException and IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayStoreInt16 && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true || compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity, "HCSCF-LINK4044", "Managed i2 array store requires exact NullReferenceException and IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if (needsArrayStoreReference && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" &&
                row.Descriptor is not null) != true || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.ArrayTypeMismatchException" && row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4021", "Managed reference array store requires exact NullReferenceException, " +
                "IndexOutOfRangeException and ArrayTypeMismatchException image types plus native EH transfer.", entryIdentity);
        if (needsArrayLoadReference && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" &&
                row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4025", "Managed reference array load requires exact NullReferenceException and " +
                "IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if ((needsArrayLoadInt32 || needsArrayLoadInt64) && (!hasNativeEh || compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.NullReferenceException" && row.Descriptor is not null) != true ||
            compilation.TypeUniverse?.Rows.Any(static row => row.StableIdentity == "System.IndexOutOfRangeException" &&
                row.Descriptor is not null) != true))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4027", "Managed i4 array load requires exact NullReferenceException and " +
                "IndexOutOfRangeException image types plus native handled/unhandled EH transfer.", entryIdentity);
        if(hasVirtualDispatch&&compilation.VirtualSlotPlans is not { Count:>0 })
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected,entryIdentity,"HCSCF-LINK4010",
                "Virtual dispatch requires a non-empty exact closed-world slot plan.",entryIdentity);
        bool needsArgumentExceptionMessage = compilation.VirtualSlotPlans?.SelectMany(static plan => plan.Targets)
            .Any(static target => target.ImplementationIdentity == "System.ArgumentException.get_Message") == true;
        if (compilation.Methods.Count == 0 || compilation.Methods.Count > budgets.MaximumReachableMethods ||
            compilation.Methods.Count > HybridCpuStaticLinkOptionsV1.Production.MaximumInputs)
            return Failure(ScalarControlFlowV2LinkStatusV1.BudgetExhausted, entryIdentity,
                "HCSCF-BACKEND-BUDGET4001", $"Reachable method count {compilation.Methods.Count} exceeds " +
                $"profile limit {budgets.MaximumReachableMethods} or static-link input limit {HybridCpuStaticLinkOptionsV1.Production.MaximumInputs}.");

        Dictionary<string, ManagedCompiledMethodV1> methods = compilation.Methods
            .ToDictionary(static method => method.Identity.StableIdentity, StringComparer.Ordinal);
        if (methods.Count != compilation.Methods.Count || compilation.Graph.CompilationOrder.Count != methods.Count ||
            compilation.Graph.CompilationOrder.Any(identity => !methods.ContainsKey(identity)) ||
            compilation.Graph.CompilationOrder.Distinct(StringComparer.Ordinal).Count() != methods.Count)
            return Failure(ScalarControlFlowV2LinkStatusV1.InvalidInput, entryIdentity,
                "HCSCF-LINK4002", "Managed graph compilation order is incomplete or duplicated.");

        // Keep existing helper, budget and graph rejection precedence.
        // Native EH installs image-owned exception state even for a bare throw/finally.
        // Reject an incomplete closure before runtime-object emission can throw.
        if (hasNativeEh && !(compilation.TypeUniverse?.Rows.Any(static row =>
                row.StableIdentity == "System.Exception" &&
                row.Descriptor is { Kind: HybridCpuManagedTypeKindV1.Class } descriptor &&
                descriptor.TypeId == row.TypeId && descriptor.StableIdentity == row.StableIdentity &&
                (descriptor.BaseTypeId ?? 0) == row.BaseTypeId &&
                descriptor.ArrayShape is null && descriptor.StringShape is null &&
                descriptor.ValueTypeShape is null) ?? false))
            return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                "HCSCF-LINK4061", "Native EH requires the exact image-owned System.Exception descriptor in the admitted metadata closure.",
                entryIdentity);

        List<ScalarControlFlowV2MethodObjectV1> objects = new List<ScalarControlFlowV2MethodObjectV1>(methods.Count);
        int symbolCount = 0;
        int relocationCount = 0;
        long codeBytes = 0;
        for (int ordinal = 0; ordinal < compilation.Graph.CompilationOrder.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string identity = compilation.Graph.CompilationOrder[ordinal];
            ManagedCompiledMethodV1 method = methods[identity];
            try
            {
                ScalarControlFlowV2MethodObjectV1 compiled = CompileMethodWithRuntimeTargets(method, ordinal, admittedGuestServiceTargets, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                objects.Add(compiled);
                symbolCount = checked(symbolCount + compiled.ObjectArtifact.Symbols.Count);
                relocationCount = checked(relocationCount + compiled.ObjectArtifact.Relocations.Count);
                codeBytes = checked(codeBytes + compiled.CodeBytes);
            }
            catch (InvalidOperationException exception)
            {
                return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                    "HCSCF-LINK4003", $"Method '{identity}' (assembly={method.Identity.AssemblyIdentity}): {exception.Message}", identity, objects);
            }

            if (symbolCount > budgets.MaximumSymbols || relocationCount > budgets.MaximumRelocations ||
                codeBytes > budgets.MaximumCodeBytes)
                return Failure(ScalarControlFlowV2LinkStatusV1.BudgetExhausted, entryIdentity,
                    "HCSCF-BACKEND-BUDGET4002", "Symbol, relocation, or code byte budget was exhausted.", identity, objects);
        }

        ScalarControlFlowV2MethodObjectV1? rejectedObject = objects.FirstOrDefault(static item =>
            item.ObjectArtifact.Status != HybridCpuObjectStatusV1.Success);
        if (rejectedObject is not null)
        {
            HybridCpuObjectDiagnosticV1? diagnostic = rejectedObject.ObjectArtifact.Diagnostics.FirstOrDefault();
            string detail = diagnostic is null
                ? rejectedObject.ObjectArtifact.Status.ToString()
                : $"{diagnostic.Code}: {diagnostic.Message}";
            return Failure(ScalarControlFlowV2LinkStatusV1.ObjectRejected, entryIdentity,
                "HCSCF-LINK4004", $"HCO object writer rejected method '{rejectedObject.MethodIdentity}' ({detail}).",
                rejectedObject.MethodIdentity, objects);
        }

        ManagedRecursionStackEvidenceV1? recursionStack = null;
        if (compilation.Graph.HasBoundedRecursion)
        {
            ManagedRecursionProofV1 proof = compilation.Graph.RecursionProofs!.Single();
            HashSet<string> recursiveMethods = proof.MethodIdentities.ToHashSet(StringComparer.Ordinal);
            int recursiveFrame = objects.Where(item => recursiveMethods.Contains(item.MethodIdentity))
                .Select(static item => item.FrameSizeBytes).DefaultIfEmpty().Max();
            int nonRecursiveFrames = objects.Where(item => !recursiveMethods.Contains(item.MethodIdentity))
                .Sum(static item => item.FrameSizeBytes);
            int requiredStack = checked(nonRecursiveFrames + recursiveFrame * proof.MaximumSccInvocations);
            if (requiredStack > compilation.Graph.MaximumRecursionStackBytes)
                return Failure(ScalarControlFlowV2LinkStatusV1.BudgetExhausted, entryIdentity,
                    "HCSCF-RECURSION2004",
                    $"The post-RA recursion frame bound {requiredStack} bytes exceeds the configured V1 stack limit {compilation.Graph.MaximumRecursionStackBytes} bytes.",
                    proof.SccStableId, objects);
            string stackDigest = Hash(string.Join('|', ManagedBoundedRecursionContractV1.Default.ContractDigest,
                compilation.Graph.BoundedRecursionOptionsDigest, proof.ProofDigest,
                compilation.Graph.MaximumDynamicDepth, compilation.Graph.MaximumRecursionStackBytes,
                requiredStack, string.Join(';', objects.Select(static item => $"{item.MethodIdentity}:{item.FrameSizeBytes}"))));
            recursionStack = new(compilation.Graph.MaximumDynamicDepth,
                compilation.Graph.MaximumRecursionStackBytes, requiredStack,
                "sum(all nonrecursive frames)+max(recursive SCC frame)*proven SCC invocation bound",
                stackDigest);
        }

        List<HybridCpuLinkInputV1> inputList = objects.Select(static item =>
            new HybridCpuLinkInputV1(item.ModuleIdentity, item.ObjectArtifact.Bytes)).ToList();
        List<string> runtimeModuleIdentities = new List<string>();
        ManagedStringLiteralObjectArtifactV1? literalObject = null;
        ManagedDispatchTypeObjectArtifactV1? dispatchTypeObject = null;
        bool hasStaticInitializers = compilation.Methods.Any(static method => method.Identity.MethodName == ".cctor");
        bool hasRuntimeIndex = hasNativeEh || hasStringLiterals || hasInterfaceDispatch || hasVirtualDispatch ||
            hasGuestService || needsEnsureTypeInitialized;
        hasRuntimeIndex |= hasStaticInitializers;
        hasRuntimeIndex |= needsStaticStoreInt32;
        hasRuntimeIndex |= needsStaticStoreReference;
        hasRuntimeIndex |= needsStaticLoadInt32;
        hasRuntimeIndex |= needsStaticLoadReference;
        hasRuntimeIndex |= needsNullCheck;
        hasRuntimeIndex |= needsArrayStoreInt32;
        hasRuntimeIndex |= needsArrayStoreInt8;
        hasRuntimeIndex |= needsArrayStoreInt16;
        hasRuntimeIndex |= needsArrayStoreReference;
        hasRuntimeIndex |= needsArrayLoadReference;
        hasRuntimeIndex |= needsArrayLoadInt32 || needsArrayLoadInt64;
        hasRuntimeIndex |= needsArrayLoadUInt8;
        hasRuntimeIndex |= needsArrayLoadInt16;
        hasRuntimeIndex |= needsArrayLoadUInt16;
        hasRuntimeIndex |= needsArrayCopyAll;
        hasRuntimeIndex |= needsArrayCopy;
        hasRuntimeIndex |= needsIsInstance;
        hasRuntimeIndex |= needsInitializeArray;
        hasRuntimeIndex |= needsStringFromUtf16Array;
        hasRuntimeIndex |= needsNewArray;
        hasRuntimeIndex |= needsAllocateObject;
        hasRuntimeIndex |= needsDivideUInt32;
        hasRuntimeIndex |= needsDivideInt32;
        hasRuntimeIndex |= needsRemainderInt32;
        hasRuntimeIndex |= needsDivideInt64;
        hasRuntimeIndex |= needsMathAbsInt64;
        hasRuntimeIndex |= needsNullableValue;
        hasRuntimeIndex |= needsMathMaxInt32;
        hasRuntimeIndex |= needsArrayEmpty;
        hasRuntimeIndex |= needsArrayLength;
        hasRuntimeIndex |= needsStringCharacter;
        hasRuntimeIndex |= needsStringLength;
        hasRuntimeIndex |= needsExceptionCtorMessage;
        hasRuntimeIndex |= needsArgumentNullCtor;
        hasRuntimeIndex |= needsArgumentOutOfRangeCtor;
        hasRuntimeIndex |= needsStringNotEquals;
        hasRuntimeIndex |= needsArrayClear;
        hasRuntimeIndex |= needsStringConcat2;
        hasRuntimeIndex |= needsStringConcat3;
        if (hasRuntimeIndex)
        {
            if (hasNativeEh && compilation.TypeUniverse is null)
                return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                    "HCSCF-LINK4015", "Catch dispatch requires the exact reachable managed type universe.", entryIdentity, objects);
            ManagedEhDispatchTableMethodV1[] dispatchMethods = objects.Select(item => new ManagedEhDispatchTableMethodV1(
                item.MethodIdentity, item.CodeBytes, item.GcInfo?.Length ?? 0, item.UnwindInfo?.Length ?? 0,
                item.EhInfo?.Length ?? 0, item.FinallyInfo?.Length ?? 0,
                HybridCpuManagedUnwindCodecV2.Decode(item.UnwindInfo!))).ToArray();
            ManagedEhDispatchTypeV1[] dispatchTypes = (compilation.TypeUniverse?.Rows ?? []).Select(static row =>
                new ManagedEhDispatchTypeV1(row.TypeHandle, row.TypeId, row.BaseTypeId)).ToArray();
            if (hasNativeEh)
            {
                AddRuntime(HybridCpuManagedEhClauseSelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhClauseSelectorEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedEhCatchSelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhCatchSelectorEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedEhFrameLookupEmitterV1.ModuleIdentity, HybridCpuManagedEhFrameLookupEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedEhUnwindStepEmitterV1.ModuleIdentity, HybridCpuManagedEhUnwindStepEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedExceptionTransferObjectV1.ModuleIdentity, HybridCpuManagedExceptionTransferObjectV1.Emit());
                AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity, HybridCpuManagedProcessExitEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedEhStateObjectV1.ModuleIdentity, HybridCpuManagedEhStateObjectV1.Emit());
                AddRuntime(HybridCpuManagedEhFinallySelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhFinallySelectorEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedEhExceptionalResumeEmitterV1.ModuleIdentity, HybridCpuManagedEhExceptionalResumeEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedFinallyContinuationEmitterV1.ModuleIdentity, HybridCpuManagedFinallyContinuationEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedEhCatchDispatchEmitterV1.ModuleIdentity, HybridCpuManagedEhCatchDispatchEmitterV1.EmitObject());
                AddRuntime(HybridCpuManagedEhScopeEmitterV1.RethrowModuleIdentity, HybridCpuManagedEhScopeEmitterV1.EmitRethrowObject());
                AddRuntime(HybridCpuManagedEhScopeEmitterV1.LeaveModuleIdentity, HybridCpuManagedEhScopeEmitterV1.EmitLeaveCatchObject());
            }
            if (hasGuestService)
            {
                if (hasDoomClockService)
                    AddRuntime(HybridCpuManagedDoomClockEmitterV1.ModuleIdentity, HybridCpuManagedDoomClockEmitterV1.EmitObject());
                if (hasFramebufferService)
                    AddRuntime(HybridCpuManagedFramebufferEmitterV1.ModuleIdentity, HybridCpuManagedFramebufferEmitterV1.EmitObject());
                if (hasConsoleWriteService)
                    AddRuntime(HybridCpuManagedConsoleWriteEmitterV1.ModuleIdentity, HybridCpuManagedConsoleWriteEmitterV1.EmitObject());
                if (hasDoomWaitService)
                    AddRuntime(HybridCpuManagedDoomWaitEmitterV1.ModuleIdentity, HybridCpuManagedDoomWaitEmitterV1.EmitObject());
                if (hasBootBlobService)
                    AddRuntime(HybridCpuManagedBootBlobEmitterV1.ModuleIdentity, HybridCpuManagedBootBlobEmitterV1.EmitObject());
                if (hasGuestProcessExit)
                    AddRuntime(HybridCpuManagedGuestProcessExitEmitterV1.ModuleIdentity, HybridCpuManagedGuestProcessExitEmitterV1.EmitObject());
                if (hasFramebufferPresent)
                    AddRuntime(HybridCpuManagedFramebufferPresentEmitterV1.ModuleIdentity, HybridCpuManagedFramebufferPresentEmitterV1.EmitObject());
                if (hasConsoleTitle)
                    AddRuntime(HybridCpuManagedConsoleTitleEmitterV1.ModuleIdentity, HybridCpuManagedConsoleTitleEmitterV1.EmitObject());
                if (hasInputPull)
                    AddRuntime(HybridCpuManagedInputPullEmitterV1.ModuleIdentity, HybridCpuManagedInputPullEmitterV1.EmitObject());
                if (hasPalette)
                    AddRuntime(HybridCpuManagedPaletteEmitterV1.ModuleIdentity, HybridCpuManagedPaletteEmitterV1.EmitObject());
                if (!hasNativeEh)
                    AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity, HybridCpuManagedProcessExitEmitterV1.EmitObject());
            }
            if (hasStringLiterals)
            {
                literalObject = ManagedStringLiteralObjectV1.Emit(compilation.StringLiteralPlan!);
                AddRuntime(ManagedStringLiteralObjectV1.ModuleIdentity, literalObject.ObjectArtifact);
                AddRuntime(HybridCpuManagedLdstrEmitterV1.ModuleIdentity, HybridCpuManagedLdstrEmitterV1.EmitObject());
            }
            if (needsEnsureTypeInitialized)
            {
                AddRuntime(HybridCpuManagedEnsureTypeInitializedEmitterV1.ModuleIdentity,
                    HybridCpuManagedEnsureTypeInitializedEmitterV1.EmitObject());
                if (!hasNativeEh && !hasGuestService && !needsArgumentExceptionMessage)
                    AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity,
                        HybridCpuManagedProcessExitEmitterV1.EmitObject());
            }
            if (needsStaticStoreInt32)
            {
                AddRuntime(HybridCpuManagedStaticStoreInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedStaticStoreInt32EmitterV1.EmitObject());
                if (!hasNativeEh && !hasGuestService && !needsEnsureTypeInitialized &&
                    !needsArgumentExceptionMessage)
                    AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity,
                        HybridCpuManagedProcessExitEmitterV1.EmitObject());
            }
            if (needsStaticStoreReference)
            {
                AddRuntime(HybridCpuManagedStaticStoreReferenceEmitterV1.ModuleIdentity,
                    HybridCpuManagedStaticStoreReferenceEmitterV1.EmitObject());
                if (!hasNativeEh && !hasGuestService && !needsEnsureTypeInitialized &&
                    !needsArgumentExceptionMessage)
                    AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity,
                        HybridCpuManagedProcessExitEmitterV1.EmitObject());
            }
            if (needsStaticLoadInt32)
            {
                AddRuntime(HybridCpuManagedStaticLoadInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedStaticLoadInt32EmitterV1.EmitObject());
                if (!hasNativeEh && !hasGuestService && !needsEnsureTypeInitialized &&
                    !needsStaticStoreInt32 && !needsArgumentExceptionMessage)
                    AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity,
                        HybridCpuManagedProcessExitEmitterV1.EmitObject());
            }
            if (needsStaticLoadReference)
            {
                AddRuntime(HybridCpuManagedStaticLoadReferenceEmitterV1.ModuleIdentity,
                    HybridCpuManagedStaticLoadReferenceEmitterV1.EmitObject());
                if (!hasNativeEh && !hasGuestService && !needsEnsureTypeInitialized &&
                    !needsStaticStoreInt32 && !needsStaticStoreReference && !needsArgumentExceptionMessage)
                    AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity,
                        HybridCpuManagedProcessExitEmitterV1.EmitObject());
            }
            if (needsNullCheck)
                AddRuntime(HybridCpuManagedNullCheckEmitterV1.ModuleIdentity,
                    HybridCpuManagedNullCheckEmitterV1.EmitObject());
            if (needsArrayStoreInt32)
                AddRuntime(HybridCpuManagedArrayStoreInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject());
            if (needsArrayStoreInt8)
                AddRuntime(HybridCpuManagedArrayStoreInt8EmitterV1.ModuleIdentity, HybridCpuManagedArrayStoreInt8EmitterV1.EmitObject());
            if (needsArrayStoreInt16)
                AddRuntime(HybridCpuManagedArrayStoreInt16EmitterV1.ModuleIdentity, HybridCpuManagedArrayStoreInt16EmitterV1.EmitObject());
            if (needsArrayStoreReference)
                AddRuntime(HybridCpuManagedArrayStoreReferenceEmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayStoreReferenceEmitterV1.EmitObject());
            if (needsArrayLoadReference)
                AddRuntime(HybridCpuManagedArrayLoadReferenceEmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayLoadReferenceEmitterV1.EmitObject());
            if (needsArrayLoadInt64)
                AddRuntime(HybridCpuManagedArrayLoadInt64EmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayLoadInt64EmitterV1.EmitObject());
            if (needsArrayLoadInt32)
                AddRuntime(HybridCpuManagedArrayLoadInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayLoadInt32EmitterV1.EmitObject());
            if (needsArrayLoadUInt8)
                AddRuntime(HybridCpuManagedArrayLoadUInt8EmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayLoadUInt8EmitterV1.EmitObject());
            if (needsArrayLoadInt16)
                AddRuntime(HybridCpuManagedArrayLoadInt16EmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayLoadInt16EmitterV1.EmitObject());
            if (needsArrayLoadUInt16)
                AddRuntime(HybridCpuManagedArrayLoadUInt16EmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayLoadUInt16EmitterV1.EmitObject());
            if (needsArrayCopyAll)
                AddRuntime(HybridCpuManagedArrayCopyAllEmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayCopyAllEmitterV1.EmitObject());
            if (needsArrayCopy)
                AddRuntime(HybridCpuManagedArrayCopyEmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayCopyEmitterV1.EmitObject());
            if (needsIsInstance)
                AddRuntime(HybridCpuManagedIsInstanceEmitterV1.ModuleIdentity,
                    HybridCpuManagedIsInstanceEmitterV1.EmitObject());
            if (needsInitializeArray)
                AddRuntime(HybridCpuManagedInitializeArrayEmitterV1.ModuleIdentity,
                    HybridCpuManagedInitializeArrayEmitterV1.EmitObject());
            if (needsStringFromUtf16Array)
                AddRuntime(HybridCpuManagedStringFromUtf16ArrayEmitterV1.ModuleIdentity,
                    HybridCpuManagedStringFromUtf16ArrayEmitterV1.EmitObject());
            if (needsNewArray)
                AddRuntime(HybridCpuManagedNewArrayEmitterV1.ModuleIdentity,
                    HybridCpuManagedNewArrayEmitterV1.EmitObject());
            if (needsAllocateObject)
                AddRuntime(HybridCpuManagedAllocateObjectEmitterV1.ModuleIdentity,
                    HybridCpuManagedAllocateObjectEmitterV1.EmitObject());
            if (needsDivideUInt32)
                AddRuntime(HybridCpuManagedDivideUInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedDivideUInt32EmitterV1.EmitObject());
            if (needsDivideInt32)
                AddRuntime(HybridCpuManagedDivideInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedDivideInt32EmitterV1.EmitObject());
            if (needsRemainderInt32)
                AddRuntime(HybridCpuManagedRemainderInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedRemainderInt32EmitterV1.EmitObject());
            if (needsDivideInt64)
                AddRuntime(HybridCpuManagedDivideInt64EmitterV1.ModuleIdentity,
                    HybridCpuManagedDivideInt64EmitterV1.EmitObject());
            if (needsMathAbsInt64)
                AddRuntime(HybridCpuManagedMathAbsInt64EmitterV1.ModuleIdentity,
                    HybridCpuManagedMathAbsInt64EmitterV1.EmitObject());
            if (needsNullableValue)
                AddRuntime(HybridCpuManagedNullableValueEmitterV1.ModuleIdentity, HybridCpuManagedNullableValueEmitterV1.EmitObject());
            if (needsMathMaxInt32)
                AddRuntime(HybridCpuManagedMathMaxInt32EmitterV1.ModuleIdentity,
                    HybridCpuManagedMathMaxInt32EmitterV1.EmitObject());
            if (needsArrayEmpty)
                AddRuntime(HybridCpuManagedArrayEmptyEmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayEmptyEmitterV1.EmitObject());
            if (needsArrayLength)
                AddRuntime(HybridCpuManagedArrayLengthEmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayLengthEmitterV1.EmitObject());
            if (needsStringCharacter)
                AddRuntime(HybridCpuManagedStringCharacterEmitterV1.ModuleIdentity, HybridCpuManagedStringCharacterEmitterV1.EmitObject());
            if (needsStringLength)
                AddRuntime(HybridCpuManagedStringLengthEmitterV1.ModuleIdentity, HybridCpuManagedStringLengthEmitterV1.EmitObject());
            if (needsExceptionCtorMessage)
            {
                HybridCpuManagedTypeDescriptorV1? exceptionDescriptor = compilation.TypeUniverse?.Rows
                    .SingleOrDefault(static row => row.StableIdentity == "System.Exception")?.Descriptor;
                HybridCpuManagedFieldLayoutV1? message = exceptionDescriptor?.InstanceFields.SingleOrDefault(
                    static field => field.Identity == "_message" &&
                        field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference &&
                        field.SizeBytes == sizeof(ulong) && field.AlignmentBytes == sizeof(ulong));
                if (message is null)
                    return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                        "HCSCF-LINK4033", "System.Exception::.ctor(string) requires the exact aligned _message reference layout.", entryIdentity, objects);
                AddRuntime(HybridCpuManagedExceptionCtorMessageEmitterV1.ModuleIdentity,
                    HybridCpuManagedExceptionCtorMessageEmitterV1.EmitObject(message.OffsetBytes));
            }
            if (needsArgumentNullCtor)
                AddRuntime(HybridCpuManagedArgumentNullCtorEmitterV1.ModuleIdentity,
                    HybridCpuManagedArgumentNullCtorEmitterV1.EmitObject());
            if (needsArgumentOutOfRangeCtor)
                AddRuntime(HybridCpuManagedArgumentOutOfRangeCtorEmitterV1.ModuleIdentity,
                    HybridCpuManagedArgumentOutOfRangeCtorEmitterV1.EmitObject());
            if (needsStringNotEquals)
                AddRuntime(HybridCpuManagedStringNotEqualsEmitterV1.ModuleIdentity,
                    HybridCpuManagedStringNotEqualsEmitterV1.EmitObject());
            if (needsArrayClear)
                AddRuntime(HybridCpuManagedArrayClearEmitterV1.ModuleIdentity,
                    HybridCpuManagedArrayClearEmitterV1.EmitObject());
            if (needsStringConcat2)
                AddRuntime(HybridCpuManagedStringConcat2EmitterV1.ModuleIdentity,
                    HybridCpuManagedStringConcat2EmitterV1.EmitObject());
            if (needsStringConcat3)
                AddRuntime(HybridCpuManagedStringConcat3EmitterV1.ModuleIdentity,
                    HybridCpuManagedStringConcat3EmitterV1.EmitObject());
            if (hasInterfaceDispatch || hasVirtualDispatch)
            {
                HybridCpuManagedTypeDescriptorV1? exceptionDescriptor = compilation.TypeUniverse?.Rows
                    .SingleOrDefault(static row => row.StableIdentity == "System.Exception")?.Descriptor;
                if (exceptionDescriptor is not null && compilation.VirtualSlotPlans?.SelectMany(static plan => plan.Targets)
                    .Any(static target => target.ImplementationIdentity == "System.Exception.get_Message") == true)
                {
                    HybridCpuManagedFieldLayoutV1? message = exceptionDescriptor.InstanceFields.SingleOrDefault(
                        static field => field.Identity == "_message" &&
                            field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference &&
                            field.SizeBytes == sizeof(ulong) && field.AlignmentBytes == sizeof(ulong));
                    if (message is null)
                        throw new InvalidOperationException("System.Exception::_message lacks its exact aligned reference layout.");
                    AddRuntime(HybridCpuManagedExceptionGetMessageEmitterV1.ModuleIdentity,
                        HybridCpuManagedExceptionGetMessageEmitterV1.EmitObject(message.OffsetBytes));
                }
                if (needsArgumentExceptionMessage)
                {
                    AddRuntime(HybridCpuManagedArgumentExceptionMessageEmitterV1.ModuleIdentity,
                        HybridCpuManagedArgumentExceptionMessageEmitterV1.EmitObject());
                    if (!hasNativeEh && !hasGuestService)
                        AddRuntime(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity,
                            HybridCpuManagedProcessExitEmitterV1.EmitObject());
                }
                string[] requiredRuntimeTypes = [
                    .. (hasNativeEh ? new[] { "System.Exception" } : Array.Empty<string>()),
                    .. (needsNullableValue ? new[] { "System.InvalidOperationException" } : Array.Empty<string>()),
                    .. compilation.Methods.Where(static method => method.Identity.MethodName == ".cctor")
                        .Select(static method => method.Identity.DeclaringType).Distinct(StringComparer.Ordinal),
                    .. (needsNullCheck || needsArrayStoreInt32 || needsArrayStoreInt8 || needsArrayStoreInt16 || needsArrayStoreReference || needsArrayLoadReference || needsArrayLoadInt32 || needsArrayLoadInt64 || needsArrayLoadUInt8 || needsArrayLoadInt16 || needsArrayLoadUInt16 || needsArrayLength || needsStringCharacter || needsStringLength || needsInitializeArray || needsStringFromUtf16Array ? new[] { "System.NullReferenceException" } : Array.Empty<string>()),
                    .. (needsArrayStoreInt32 || needsArrayStoreInt8 || needsArrayStoreInt16 || needsArrayStoreReference || needsArrayLoadReference || needsArrayLoadInt32 || needsArrayLoadInt64 || needsArrayLoadUInt8 || needsArrayLoadInt16 || needsArrayLoadUInt16 || needsStringCharacter ? new[] { "System.IndexOutOfRangeException" } : Array.Empty<string>()),
                    .. (needsArrayStoreReference ? new[] { "System.ArrayTypeMismatchException" } : Array.Empty<string>())];
                requiredRuntimeTypes = [.. requiredRuntimeTypes,
                    .. (needsArrayCopyAll || needsArrayCopy ? new[] { "System.ArgumentNullException", "System.ArgumentOutOfRangeException", "System.ArgumentException", "System.ArrayTypeMismatchException" } : Array.Empty<string>())];
                requiredRuntimeTypes = [.. requiredRuntimeTypes,
                    .. (needsNewArray ? new[] { "System.OverflowException", "System.OutOfMemoryException" } : Array.Empty<string>()),
                    .. ((needsAllocateObject && !needsNewArray) || needsArrayEmpty || needsStringConcat2 || needsStringConcat3 || needsStringFromUtf16Array || needsArgumentNullCtor || needsArgumentOutOfRangeCtor || needsArrayClear ? new[] { "System.OutOfMemoryException" } : Array.Empty<string>()),
                    .. (needsArgumentNullCtor || needsArrayClear ? new[] { "System.ArgumentNullException" } : Array.Empty<string>()),
                    .. (needsArgumentOutOfRangeCtor ? new[] { "System.ArgumentOutOfRangeException" } : Array.Empty<string>())];
                requiredRuntimeTypes = [.. requiredRuntimeTypes,
                    .. (needsDivideUInt32 || needsDivideInt32 || needsRemainderInt32 || needsDivideInt64 ? new[] { "System.DivideByZeroException" } : Array.Empty<string>()),
                    .. ((needsDivideInt32 || needsRemainderInt32 || needsDivideInt64 || needsMathAbsInt64) && !needsNewArray ? new[] { "System.OverflowException" } : Array.Empty<string>())];
                dispatchTypeObject = ManagedDispatchTypeObjectV1.Emit(compilation, requiredRuntimeTypes);
                AddRuntime(ManagedDispatchTypeObjectV1.ModuleIdentity, dispatchTypeObject.ObjectArtifact);
                AddRuntime(ManagedDispatchObjectV1.ModuleIdentity, ManagedDispatchObjectV1.Emit(compilation));
                AddRuntime(HybridCpuManagedDispatchResolverEmitterV1.ModuleIdentity,
                    HybridCpuManagedDispatchResolverEmitterV1.EmitInterfaceObject());
                AddRuntime(HybridCpuManagedDispatchResolverEmitterV1.ModuleIdentity+".virtual",
                    HybridCpuManagedDispatchResolverEmitterV1.EmitVirtualObject());
            }
            if ((hasStaticInitializers || needsNullCheck || needsArrayStoreInt32 || needsArrayStoreInt8 || needsArrayStoreInt16 || needsArrayStoreReference || needsArrayLoadReference || needsArrayLoadInt32 || needsArrayLoadInt64 || needsArrayLoadUInt8 || needsArrayLoadInt16 || needsArrayLoadUInt16 || needsArrayCopyAll || needsArrayCopy || needsIsInstance || needsArgumentNullCtor || needsArgumentOutOfRangeCtor || needsStringNotEquals || needsArrayClear ||
                needsInitializeArray || needsStringFromUtf16Array || needsNewArray || needsAllocateObject || needsDivideUInt32 || needsDivideInt32 || needsRemainderInt32 || needsDivideInt64 || needsMathAbsInt64 || needsNullableValue || needsArrayEmpty || needsArrayLength || needsStringCharacter || needsStringLength || needsStringConcat2 || needsStringConcat3) && dispatchTypeObject is null)
            {
                string[] requiredRuntimeTypes = [
                    .. (hasNativeEh ? new[] { "System.Exception" } : Array.Empty<string>()),
                    .. (needsNullableValue ? new[] { "System.InvalidOperationException" } : Array.Empty<string>()),
                    .. compilation.Methods.Where(static method => method.Identity.MethodName == ".cctor")
                        .Select(static method => method.Identity.DeclaringType).Distinct(StringComparer.Ordinal),
                    .. (needsNullCheck || needsArrayStoreInt32 || needsArrayStoreInt8 || needsArrayStoreInt16 || needsArrayStoreReference || needsArrayLoadReference || needsArrayLoadInt32 || needsArrayLoadInt64 || needsArrayLoadUInt8 || needsArrayLoadInt16 || needsArrayLoadUInt16 || needsArrayCopyAll || needsArrayCopy || needsArrayLength || needsStringCharacter || needsStringLength || needsInitializeArray || needsStringFromUtf16Array
                        ? new[] { "System.NullReferenceException" } : Array.Empty<string>()),
                    .. (needsArrayStoreInt32 || needsArrayStoreInt8 || needsArrayStoreInt16 || needsArrayStoreReference || needsArrayLoadReference || needsArrayLoadInt32 || needsArrayLoadInt64 || needsArrayLoadUInt8 || needsArrayLoadInt16 || needsArrayLoadUInt16 || needsStringCharacter ? new[] { "System.IndexOutOfRangeException" } : Array.Empty<string>()),
                    .. (needsArrayStoreReference ? new[] { "System.ArrayTypeMismatchException" } : Array.Empty<string>()),
                    .. (needsArrayCopyAll || needsArrayCopy ? new[] { "System.ArgumentNullException", "System.ArgumentOutOfRangeException", "System.ArgumentException", "System.ArrayTypeMismatchException" } : Array.Empty<string>()),
                    .. (needsNewArray ? new[] { "System.OverflowException", "System.OutOfMemoryException" } : Array.Empty<string>()),
                    .. ((needsAllocateObject && !needsNewArray) || needsArrayEmpty || needsStringConcat2 || needsStringConcat3 || needsStringFromUtf16Array || needsArgumentNullCtor || needsArgumentOutOfRangeCtor || needsArrayClear ? new[] { "System.OutOfMemoryException" } : Array.Empty<string>()),
                    .. (needsArgumentNullCtor || needsArrayClear ? new[] { "System.ArgumentNullException" } : Array.Empty<string>()),
                    .. (needsArgumentOutOfRangeCtor ? new[] { "System.ArgumentOutOfRangeException" } : Array.Empty<string>()),
                    .. (needsDivideUInt32 || needsDivideInt32 || needsRemainderInt32 || needsDivideInt64 ? new[] { "System.DivideByZeroException" } : Array.Empty<string>()),
                    .. ((needsDivideInt32 || needsRemainderInt32 || needsDivideInt64 || needsMathAbsInt64) && !needsNewArray ? new[] { "System.OverflowException" } : Array.Empty<string>())];
                dispatchTypeObject = ManagedDispatchTypeObjectV1.Emit(compilation, requiredRuntimeTypes);
                AddRuntime(ManagedDispatchTypeObjectV1.ModuleIdentity, dispatchTypeObject.ObjectArtifact);
            }
            if (hasNativeEh && dispatchTypeObject is null)
            {
                // A finally-only image still installs the managed EH root and type system.
                // Bind the existing exact exception base descriptor; missing metadata denies linkage.
                dispatchTypeObject = ManagedDispatchTypeObjectV1.Emit(compilation, ["System.Exception"]);
                AddRuntime(ManagedDispatchTypeObjectV1.ModuleIdentity, dispatchTypeObject.ObjectArtifact);
            }            AddRuntime(ManagedEhDispatchTableObjectV1.ModuleIdentity, ManagedEhDispatchTableObjectV1.Emit(dispatchMethods,
                dispatchTypes, HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
                HybridCpuRestrictedStartupOptionsV1.Production.StackSize,
                bindRuntimeHelpers: hasNativeEh || hasGuestService,
                bindStringLiterals: hasStringLiterals, bindDispatch: hasInterfaceDispatch || hasVirtualDispatch,
                bindProcessExitHelper: needsArgumentExceptionMessage || needsEnsureTypeInitialized ||
                    needsStaticStoreInt32 || needsStaticStoreReference || needsStaticLoadInt32 || needsStaticLoadReference ||
                    needsNullCheck || needsArrayStoreInt32 || needsArrayStoreInt8 || needsArrayStoreInt16 ||
                    needsArrayStoreReference || needsArrayLoadReference || needsArrayLoadInt32 || needsArrayLoadInt64 || needsArrayLoadUInt8 || needsArrayLoadInt16 || needsArrayLoadUInt16 || needsArrayCopyAll || needsArrayCopy || needsInitializeArray || needsStringFromUtf16Array || needsArgumentNullCtor || needsArgumentOutOfRangeCtor || needsStringNotEquals || needsArrayClear || needsNewArray ||
                    needsAllocateObject || needsDivideUInt32 || needsDivideInt32 || needsRemainderInt32 || needsDivideInt64 || needsMathAbsInt64 || needsNullableValue || needsArrayEmpty || needsArrayLength || needsStringCharacter || needsStringLength || needsStringConcat2 || needsStringConcat3));
        }
        HybridCpuLinkInputV1[] inputs = inputList.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        HybridCpuStaticLinkArtifactV1 linked = new HybridCpuStaticLinkerV1().Link(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        if (linked.Status != HybridCpuLinkStatusV1.Success)
        {
            HybridCpuLinkDiagnosticV1? diagnostic = linked.Diagnostics.FirstOrDefault();
            string detail = diagnostic is null ? linked.Status.ToString() :
                $"{diagnostic.Code}: {diagnostic.Message}";
            return Failure(ScalarControlFlowV2LinkStatusV1.LinkRejected, entryIdentity,
                "HCSCF-LINK4005", $"Static linker rejected the managed object world ({detail}).", entryIdentity, objects, linked);
        }
        if (linked.ImageBytes.Length > budgets.MaximumImageBytes)
            return Failure(ScalarControlFlowV2LinkStatusV1.BudgetExhausted, entryIdentity,
                "HCSCF-BACKEND-BUDGET4003", "Linked image exceeds the deterministic profile image budget.",
                entryIdentity, objects, linked);

        HybridCpuImageRuntimeBootstrapDescriptorV1? bootstrap = null;
        if (hasRuntimeIndex)
        {
            List<HybridCpuStaticRootRegistrationV1> staticRoots = new List<HybridCpuStaticRootRegistrationV1>();
            if (hasNativeEh)
            {
                HybridCpuLinkedSymbolV1 root = linked.Symbols.Single(symbol =>
                    string.Equals(symbol.Name, HybridCpuManagedEhStateObjectV1.RootSymbol, StringComparison.Ordinal));
                staticRoots.Add(new(HybridCpuManagedEhStateObjectV1.RootSymbol, root.Address, root.Size));
            }
            if (literalObject is not null)
                foreach (ManagedStringLiteralImageRowV1 row in literalObject.Rows)
                {
                    HybridCpuLinkedSymbolV1 root = linked.Symbols.Single(symbol => symbol.Name == row.RootSymbol);
                    staticRoots.Add(new(row.RootSymbol, root.Address, root.Size));
                }
            string[] helpers = linked.Symbols.Select(static symbol => symbol.Name)
                .Where(name => HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(name) is not null)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            HybridCpuRuntimeHelperImportV1[] helperImports = helpers.Select(name =>
            {
                HybridCpuRuntimeHelperV1 helper = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(name)!;
                return new HybridCPU.Platform.Contracts.HybridCpuRuntimeHelperImportV1(helper.Symbol, helper.Signature, true);
            }).ToArray();
            HybridCpuCodeManagerRegistrationV1[] codeRecords = objects.Select(item =>
            {
                HybridCpuLinkedSymbolV1 symbol = linked.Symbols.Single(candidate => candidate.Name == item.MethodIdentity);
                return new HybridCPU.Platform.Contracts.HybridCpuCodeManagerRegistrationV1(item.MethodIdentity,
                    checked((int)(symbol.Address - linked.ImageBase)), item.CodeBytes, Hash(item.GcInfo!), Hash(item.UnwindInfo!));
            }).ToArray();
            HybridCpuManagedEhMethodRegistrationV1[] ehRecords = objects.Where(static item => item.EhInfo is { Length: > 0 }).Select(item =>
            {
                HybridCpuLinkedSymbolV1 symbol = linked.Symbols.Single(candidate => candidate.Name == item.MethodIdentity);
                return new HybridCPU.Platform.Contracts.HybridCpuManagedEhMethodRegistrationV1(item.MethodIdentity,
                    checked((int)(symbol.Address - linked.ImageBase)), item.CodeBytes, item.EhInfo!, item.UnwindInfo!);
            }).ToArray();
            HybridCpuManagedTypeRegistrationV1[] typeRegistrations = [];
            if (dispatchTypeObject is not null)
                typeRegistrations = ManagedDispatchTypeObjectV1.Registrations(dispatchTypeObject, linked);
            HybridCpuManagedStringLiteralRegistrationV1[] stringRegistrations = [];
            if (literalObject is not null)
            {
                HybridCpuLinkedSymbolV1 metadata = linked.Symbols.Single(symbol => symbol.Name == literalObject.DescriptorSymbol);
                RestrictedCilStringLiteralBindingV1 first = literalObject.Rows[0].Binding;
                typeRegistrations = [.. typeRegistrations, new(first.TypeDescriptor.TypeId, first.TypeDescriptor.StableIdentity,
                    first.TypeDescriptor.DescriptorDigest, checked((int)(metadata.Address - linked.ImageBase)),
                    literalObject.DescriptorSizeBytes, null,
                    compilation.TypeUniverse!.Rows.Single(row => row.TypeId == first.TypeDescriptor.TypeId).TypeHandle)];
                stringRegistrations = literalObject.Rows.Select(row => new HybridCpuManagedStringLiteralRegistrationV1(
                    row.ObjectSymbol, row.Binding.LiteralHandle, row.Binding.TypeDescriptor.TypeId, row.Binding.Literal)).ToArray();
            }
            HybridCpuManagedFieldDataRegistrationV1[] fieldData = (compilation.FieldData ?? [])
                .Select(static row => new HybridCpuManagedFieldDataRegistrationV1(row.DataHandle, row.Data.ToArray()))
                .OrderBy(static row => row.DataHandle).ToArray();
            Dictionary<string, ManagedCompiledMethodV1> compiledByIdentity = compilation.Methods.ToDictionary(
                static method => method.Identity.StableIdentity, StringComparer.Ordinal);
            Dictionary<string, ScalarControlFlowV2MethodObjectV1> objectsByIdentity = objects.ToDictionary(
                static method => method.MethodIdentity, StringComparer.Ordinal);
            Dictionary<string, ManagedTypeUniverseRowV1> typesByIdentity = (compilation.TypeUniverse?.Rows ?? [])
                .ToDictionary(static row => row.StableIdentity, StringComparer.Ordinal);
            List<HybridCpuModuleInitializerRegistrationV1> initializerRegistrations = new List<HybridCpuModuleInitializerRegistrationV1>();
            foreach (string identity in compilation.Graph.CompilationOrder)
            {
                if (!compiledByIdentity.TryGetValue(identity, out ManagedCompiledMethodV1? method) ||
                    method.Identity.MethodName != ".cctor")
                    continue;
                if (!objectsByIdentity.TryGetValue(identity, out ScalarControlFlowV2MethodObjectV1? methodObject) ||
                    !typesByIdentity.TryGetValue(method.Identity.DeclaringType, out ManagedTypeUniverseRowV1? type) ||
                    !typeRegistrations.Any(registration => registration.TypeId == type.TypeId))
                    return Failure(ScalarControlFlowV2LinkStatusV1.BackendRejected, entryIdentity,
                        "HCSCF-LINK4013", $"Static initializer '{identity}' has no exact method object or image-owned TypeId registration.",
                        identity, objects, linked);
                initializerRegistrations.Add(new(methodObject.ModuleIdentity, identity,
                    initializerRegistrations.Count, type.TypeId));
            }
            bootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
                HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entryIdentity, entryIdentity,
                helperImports, codeRecords, staticRoots, managedTypes: typeRegistrations,
                moduleInitializers: initializerRegistrations, stringLiterals: stringRegistrations,
                ehMethods: ehRecords, fieldData: fieldData);
        }
        cancellationToken.ThrowIfCancellationRequested();
        HybridCpuRestrictedImageV1 image = new HybridCpuRestrictedImageBuilderV1().Build(new(
            linked, entryIdentity, HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes,
            RuntimeBootstrap: bootstrap,
            GlobalPointerSymbol: hasRuntimeIndex ? ManagedEhDispatchTableObjectV1.Symbol : null));
        cancellationToken.ThrowIfCancellationRequested();
        if (image.Status != HybridCpuStartupStatusV1.Success)
        {
            string detail = image.Diagnostics.FirstOrDefault()?.Code ?? image.Status.ToString();
            return Failure(ScalarControlFlowV2LinkStatusV1.ImageRejected, entryIdentity,
                "HCSCF-LINK4006", $"Restricted image builder rejected the linked program ({detail}).",
                entryIdentity, objects, linked, image);
        }

        string orderedObjectDigest = DigestObjects(objects);
        string backendPolicyDigest = HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Hash(
            HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Serialize(new
            {
                Schema = "hybridcpu.managed-backend-policy/v1",
                Scheduler = "local-list/v1",
                Bundling = HybridCpuBundlingPolicyV1.Default.Digest,
                Allocation = HybridCpuRegisterAllocationContractV1.Default.ContractDigest,
                AllocationOptions = HybridCpuRegisterAllocationOptionsV1.Qualification.OptionsDigest,
                ResourceModel = ResourceModel.ModelDigest,
                CompilerContract = HybridCpuCompilerContract.Version
            }));
        string provenance = Hash(string.Join('|', SchemaId, ScalarControlFlowV2ProfileContractV1.Default.ContractDigest,
            backendPolicyDigest,
            HybridCpuNativeAbiContractV2.Default.ContractDigest, HybridCpuNativeCallControlContractV1.Default.ContractDigest,
            HybridCpuManagedCallRelocationContractV1.ContractDigest, HybridCpuObjectFormatContractV1.ContractDigest,
            HybridCpuStaticLinkOptionsV1.Production.OptionsDigest, HybridCpuRestrictedStartupOptionsV1.Production.OptionsDigest,
            compilation.Graph.GraphDigest, entryIdentity, orderedObjectDigest, linked.LinkMapDigest, image.PackageSha256,
            recursionStack?.EvidenceDigest ?? string.Empty));
        cancellationToken.ThrowIfCancellationRequested();
        return new(ScalarControlFlowV2LinkStatusV1.Success, entryIdentity, objects.AsReadOnly(), linked, image,
            orderedObjectDigest, provenance, Array.Empty<ScalarControlFlowV2LinkDiagnosticV1>(), recursionStack,
            runtimeModuleIdentities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        void AddRuntime(string moduleIdentity, HybridCpuObjectArtifactV1 artifact)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact.Status != HybridCpuObjectStatusV1.Success)
                throw new InvalidOperationException($"Managed EH runtime object '{moduleIdentity}' was rejected.");
            inputList.Add(new HybridCpuLinkInputV1(moduleIdentity, artifact.Bytes));
            runtimeModuleIdentities.Add(moduleIdentity);
        }
    }

    internal static ScalarControlFlowV2MethodObjectV1 CompileMethod(ManagedCompiledMethodV1 method, int ordinal) =>
        CompileMethodWithRuntimeTargets(method, ordinal, new HashSet<string>(StringComparer.Ordinal));

    private static ScalarControlFlowV2MethodObjectV1 CompileMethodWithRuntimeTargets(
        ManagedCompiledMethodV1 method,
        int ordinal,
        IReadOnlySet<string> admittedRuntimeTargets,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IrProgram program = method.Import.Program ?? throw new InvalidOperationException("Admitted method has no Canonical IR program.");
        (IrProgramSchedule? schedule, IrProgramBundlingResult _, IrRegisterAllocationResultV1? allocationResult) = HybridCpuProgramBundler.CompileWithAllocation(cancellationToken, program,
            input => new HybridCpuLocalListScheduler().ScheduleProgram(cancellationToken, input), new HybridCpuBundleFormer(),
            (inputSchedule, bundles) => new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
                cancellationToken, inputSchedule, bundles, resourceModel: ResourceModel,
                options: HybridCpuRegisterAllocationOptionsV1.Qualification,
                fixedFrameSlots: FixedFrameSlots(method)));
        IrRegisterAllocationResultV1 allocation = allocationResult!;
        if (allocation.Status != IrRegisterAllocationStatusV1.Allocated || allocation.Witness is null)
            throw new InvalidOperationException($"Register allocation rejected method '{method.Identity.StableIdentity}' " +
                $"(assembly={method.Identity.AssemblyIdentity}; IR={program.Instructions.Count}; values={program.ValueFlow.Values.Count}): " +
                $"{allocation.Status}: {allocation.Reason}");

        IReadOnlyList<HybridCpuInstructionBundle> lowered = HybridCpuPostBundleCompiler.Lower(
            cancellationToken, allocation.FinalBundles, resolveControlFlowTargets: true);
        cancellationToken.ThrowIfCancellationRequested();
        HybridCpuObjectRelocationV1[] relocations = CollectManagedCallRelocations(
            allocation.FinalBundles, lowered, method, admittedRuntimeTargets);
        byte[] methodCode = HybridCpuPostBundleCompiler.Serialize(cancellationToken, lowered);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] code = methodCode;
        List<HybridCpuObjectSymbolV1> thunkSymbols = new List<HybridCpuObjectSymbolV1>();
        Dictionary<string, int> ehThunkTargets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [HybridCpuManagedEhCatchDispatchEmitterV1.ThrowSymbol] = HybridCpuManagedEhDispatchIndexV1.CatchDispatchHelperOffset,
            [HybridCpuManagedEhScopeEmitterV1.RethrowSymbol] = HybridCpuManagedEhDispatchIndexV1.RethrowHelperOffset,
            [HybridCpuManagedEhScopeEmitterV1.LeaveCatchSymbol] = HybridCpuManagedEhDispatchIndexV1.LeaveCatchHelperOffset
            ,[HybridCpuManagedLdstrEmitterV1.Symbol] = HybridCpuManagedEhDispatchIndexV1.LdstrHelperOffset
            ,[HybridCpuManagedDispatchResolverEmitterV1.InterfaceSymbol] = HybridCpuManagedEhDispatchIndexV1.ResolveInterfaceHelperOffset
            ,[HybridCpuManagedDispatchResolverEmitterV1.VirtualSymbol] = HybridCpuManagedEhDispatchIndexV1.ResolveVirtualHelperOffset
        };
        foreach (string target in relocations.Select(static relocation => relocation.TargetSymbol)
                     .Where(ehThunkTargets.ContainsKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string thunk = $"__hybridcpu_eh_thunk_{Hash(method.Identity.StableIdentity + "|" + target)[..32]}";
            int offset = code.Length;
            byte[] thunkCode = EmitEhTailThunk(ehThunkTargets[target]);
            code = [.. code, .. thunkCode];
            thunkSymbols.Add(new(thunk, HybridCpuSymbolBinding.Local, HybridCpuSymbolVisibility.Hidden,
                ".text", checked((ulong)offset), checked((ulong)thunkCode.Length), true));
            relocations = relocations.Select(relocation => relocation.TargetSymbol == target
                ? relocation with { TargetSymbol = thunk } : relocation).ToArray();
        }
        List<HybridCpuObjectSymbolV1> symbols = new List<HybridCpuObjectSymbolV1>(method.DirectCalleeIdentities.Count + 1)
        {
            new(method.Identity.StableIdentity, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
                ".text", 0, (ulong)methodCode.Length, IsDefinition: true)
        };
        HashSet<string> thunkedTargets = ehThunkTargets.Keys.ToHashSet(StringComparer.Ordinal);
        thunkedTargets.UnionWith(thunkSymbols.Select(static symbol => symbol.Name));
        symbols.AddRange(BuildUndefinedCallTargets(method.Identity.StableIdentity,
                method.DirectCalleeIdentities, relocations, thunkedTargets).Select(static callee =>
            new HybridCpuObjectSymbolV1(callee, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
                null, 0, 0, IsDefinition: false)));
        symbols.AddRange(thunkSymbols);

        ManagedEhFinalizationArtifactV1? ehMetadata = method.Import.ManagedEhAnalysis is { } ehPlan
            ? new ManagedEhMetadataFinalizerV1().Finalize(cancellationToken, ehPlan, allocation) : null;
        if (ehMetadata is not null && ehMetadata.Status != HybridCpuManagedMetadataStatusV1.Finalized)
            throw new InvalidOperationException($"Managed EH metadata finalization rejected method: {ehMetadata.Reason}");
        byte[] unwindInfo = ehMetadata?.UnwindInfo ?? HybridCpuManagedUnwindCodecV2.Encode(
            ManagedEhMetadataFinalizerV1.CreateFrameUnwind(allocation.Witness.Frame));
        HybridCpuManagedFixedRootRequestV1[] fixedRoots = method.Import.ManagedEhAnalysis?.ControlFlow?.StateHomes
            .Where(static home => home.IsObjectRoot)
            .Select(static home => new HybridCpuManagedFixedRootRequestV1($"eh-root:{home.Slot}",
                $"eh-home:{home.Slot}", HybridCpuGcReferenceKindV1.ObjectReference)).ToArray() ?? [];
        HybridCpuManagedMetadataArtifactV1 managedMetadata = new HybridCpuManagedMetadataFinalizerV1()
            .FinalizeRequiredCallSites(cancellationToken, method.Identity.StableIdentity, 0, allocation,
                HybridCpuManagedMetadataOptionsV1.Qualification, fixedRoots);
        if (managedMetadata.Status != HybridCpuManagedMetadataStatusV1.Finalized ||
            managedMetadata.GcInfoDigest is null)
            throw new InvalidOperationException($"Managed root-map finalization rejected method " +
                $"'{method.Identity.StableIdentity}' from assembly '{method.Identity.AssemblyIdentity}' " +
                $"(IR={schedule.Program.Instructions.Count}, values={schedule.Program.ValueFlow.Values.Count}): " +
                $"{managedMetadata.Status}: {managedMetadata.Reason}");
        TestOnlyGcCorrelationV1.TryWrite(method, allocation, managedMetadata);
        List<HybridCpuObjectSectionV1> objectSections = new List<HybridCpuObjectSectionV1>
        {
             new(".text", HybridCpuObjectSectionKind.Code, HybridCpuManagedCallRelocationContractV1.BundleSizeBytes,
                code, (ulong)code.Length),
             new(".hcgc", HybridCpuObjectSectionKind.ReadOnlyData, 8, managedMetadata.GcInfo,
                (ulong)managedMetadata.GcInfo.Length),
             new(".hcunwind", HybridCpuObjectSectionKind.Unwind, 8, unwindInfo, (ulong)unwindInfo.Length)
        };
        if (ehMetadata is not null)
            objectSections.Add(new(".hceh", HybridCpuObjectSectionKind.ExceptionHandling, 8,
                ehMetadata.EhInfo, (ulong)ehMetadata.EhInfo.Length));
        if (ehMetadata?.FinallyContinuations is { } finallyContinuations)
            objectSections.Add(new(".hcfinally", HybridCpuObjectSectionKind.ReadOnlyData, 8,
                finallyContinuations.Encoding, (ulong)finallyContinuations.Encoding.Length));
        symbols.Add(new(MetadataSymbol(method.Identity.StableIdentity, "gc"), HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Hidden, ".hcgc", 0, (ulong)managedMetadata.GcInfo.Length, IsDefinition: true));
        symbols.Add(new(MetadataSymbol(method.Identity.StableIdentity, "unwind"), HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Hidden, ".hcunwind", 0, (ulong)unwindInfo.Length, IsDefinition: true));
        if (ehMetadata is not null)
            symbols.Add(new(MetadataSymbol(method.Identity.StableIdentity, "eh"), HybridCpuSymbolBinding.Global,
                HybridCpuSymbolVisibility.Hidden, ".hceh", 0, (ulong)ehMetadata.EhInfo.Length, IsDefinition: true));
        if (ehMetadata?.FinallyContinuations is { } finalContinuations)
            symbols.Add(new(MetadataSymbol(method.Identity.StableIdentity, "finally"), HybridCpuSymbolBinding.Global,
                HybridCpuSymbolVisibility.Hidden, ".hcfinally", 0, (ulong)finalContinuations.Encoding.Length, IsDefinition: true));
        HybridCpuObjectArtifactV1 artifact = new HybridCpuObjectWriterV1().Write(new(
            objectSections,
            symbols,
            relocations,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        string moduleIdentity = $"m{ordinal:D4}:{Hash(method.Identity.StableIdentity)[..32]}";
        cancellationToken.ThrowIfCancellationRequested();
        return new(method.Identity.StableIdentity, moduleIdentity, allocation.Witness.WitnessDigest,
            Hash(methodCode), methodCode.Length, allocation.Witness.Frame.FrameSizeBytes, allocation.Witness.Spills.Count, artifact,
            managedMetadata.GcInfo, managedMetadata.GcInfoDigest, unwindInfo, Hash(unwindInfo),
            ehMetadata?.EhInfo, ehMetadata?.ResultDigest,
            ehMetadata?.FinallyContinuations?.Encoding, ehMetadata?.FinallyContinuations?.Digest);
    }

    public static string MetadataSymbol(string methodIdentity, string kind)
    {
        if (string.IsNullOrWhiteSpace(methodIdentity) || kind is not ("gc" or "unwind" or "eh" or "finally"))
            throw new ArgumentException("Managed metadata symbol requires an exact method identity and known kind.");
        return $"__hybridcpu_managed_{kind}_{Hash(methodIdentity)[..32]}";
    }

    private static IReadOnlyList<HybridCpuFrameSlotRequestV2> FixedFrameSlots(ManagedCompiledMethodV1 method)
    {
        IEnumerable<HybridCpuFrameSlotRequestV2> eh = method.Import.ManagedEhAnalysis is { } plan
            ? ManagedEhFrameHomesV1.CreateRequests(plan) : [];
        IEnumerable<HybridCpuFrameSlotRequestV2> receiver = method.Import.ReceiverCallerStoragePlans?
            .Select(static storage => new HybridCpuFrameSlotRequestV2(storage.FrameSlotIdentity,
                storage.PayloadSizeBytes, storage.PayloadAlignmentBytes)) ?? [];
        HybridCpuFrameSlotRequestV2[] slots = eh.Concat(receiver)
            .OrderBy(static slot => slot.Identity, StringComparer.Ordinal).ToArray();
        if (slots.Select(static slot => slot.Identity).Distinct(StringComparer.Ordinal).Count() != slots.Length)
            throw new InvalidOperationException("EH and receiver fixed-frame slot identities overlap.");
        return slots;
    }

    private static byte[] EmitEhTailThunk(int helperOffset)
    {
        HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        };
        HybridCpuInstructionWord[] words =
        [
            Word(HybridCpuOpcode.ADDI, 5, 3, immediate: checked((short)helperOffset)),
            Word(HybridCpuOpcode.LD, 5, 5),
            Word(HybridCpuOpcode.JALR, 0, 5)
        ];
        return new HybridCpuBundleSerializer().SerializeProgram(words.Select(instruction =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, instruction);
            return bundle;
        }).ToArray());
    }

    private static HybridCpuObjectRelocationV1[] CollectManagedCallRelocations(
        IrProgramBundlingResult bundles,
        IReadOnlyList<HybridCpuInstructionBundle> lowered,
        ManagedCompiledMethodV1 method,
        IReadOnlySet<string> admittedRuntimeTargets)
    {
        HashSet<string> admittedTargets = method.DirectCalleeIdentities
            .Concat(admittedRuntimeTargets).ToHashSet(StringComparer.Ordinal);
        List<HybridCpuObjectRelocationV1> relocations = new List<HybridCpuObjectRelocationV1>();
        Dictionary<string, (int BundleIndex, int SlotIndex, IrInstruction Instruction)> placements = new Dictionary<string, (int BundleIndex, int SlotIndex, IrInstruction Instruction)>(StringComparer.Ordinal);
        int placementBundleIndex = 0;
        foreach (IrBasicBlockBundlingResult block in bundles.BlockResults)
        {
            foreach (IrMaterializedBundle bundle in block.Bundles)
            {
                foreach (IrMaterializedBundleSlot slot in bundle.Slots.Where(static slot =>
                             slot.Instruction?.StableIdentity.EndsWith(":long-call-high", StringComparison.Ordinal) == true))
                    placements.Add(slot.Instruction!.StableIdentity,
                        (placementBundleIndex, slot.SlotIndex, slot.Instruction));
                placementBundleIndex++;
            }
        }
        int bundleIndex = 0;
        foreach (IrBasicBlockBundlingResult block in bundles.BlockResults)
            foreach (IrMaterializedBundle bundle in block.Bundles)
            {
                foreach (IrMaterializedBundleSlot slot in bundle.Slots.Where(static slot =>
                    slot.Instruction?.Annotation.ControlFlowKind == IrControlFlowKind.Call))
                {
                    IrInstruction call = slot.Instruction!;
                    string target = call.Annotation.BranchTargetSymbolName ?? string.Empty;
                    HybridCpuInstructionWord carrier = lowered[bundleIndex].GetInstruction(slot.SlotIndex);
                    if ((HybridCpuOpcode)carrier.OpCode == HybridCpuOpcode.JALR)
                    {
                        if (target.Length != 0)
                        {
                            if (!admittedTargets.Contains(target) || carrier.Immediate != 0 ||
                                !HybridCpuInstructionWord.TryUnpackArchRegs(carrier.Word1, out byte longRd,
                                    out byte longRs1, out byte longRs2) ||
                                longRd != HybridCpuNativeAbiContractV2.ReturnAddressRegister || longRs1 != 5 ||
                                longRs2 != HybridCpuInstructionWord.NoArchReg ||
                                !placements.TryGetValue(call.StableIdentity + ":long-call-high", out (int BundleIndex, int SlotIndex, IrInstruction Instruction) highPlacement))
                                throw new InvalidOperationException("Long managed call requires exact AUIPC x5 / JALR x1,x5 structural carriers.");
                            HybridCpuInstructionWord highCarrier = lowered[highPlacement.BundleIndex]
                                .GetInstruction(highPlacement.SlotIndex);
                            if ((HybridCpuOpcode)highCarrier.OpCode != HybridCpuOpcode.AUIPC || highCarrier.Immediate != 0 ||
                                !HybridCpuInstructionWord.TryUnpackArchRegs(highCarrier.Word1, out byte highRd,
                                    out _, out _) || highRd != 5)
                                throw new InvalidOperationException("Long managed call AUIPC carrier or target identity is malformed.");
                            ulong highOffset = checked((ulong)highPlacement.BundleIndex * HybridCpuBundleSerializer.BundleSizeBytes +
                                (ulong)highPlacement.SlotIndex * HybridCpuInstructionWord.EncodedSize +
                                HybridCpuManagedCallRelocationContractV1.ImmediateFieldOffsetBytes);
                            ulong lowOffset = checked((ulong)bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes +
                                (ulong)slot.SlotIndex * HybridCpuInstructionWord.EncodedSize +
                                HybridCpuManagedCallRelocationContractV1.ImmediateFieldOffsetBytes);
                            long lowAddend = checked((long)(bundleIndex - highPlacement.BundleIndex) *
                                HybridCpuBundleSerializer.BundleSizeBytes);
                            relocations.Add(new(".text", highOffset,
                                HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16, target, 0));
                            relocations.Add(new(".text", lowOffset,
                                HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16, target, lowAddend));
                            continue;
                        }
                        if (carrier.Immediate != 0 ||
                            !HybridCpuInstructionWord.TryUnpackArchRegs(carrier.Word1, out byte rd, out byte rs1, out byte rs2) ||
                            rd != HybridCpuNativeAbiContractV2.ReturnAddressRegister || rs1 != 5 ||
                            rs2 != HybridCpuInstructionWord.NoArchReg)
                            throw new InvalidOperationException("Managed indirect call requires an exact relocation-free JALR x1,x5,0 carrier.");
                        continue;
                    }
                    if (!admittedTargets.Contains(target))
                        throw new InvalidOperationException($"Managed call target '{target}' is outside the admitted direct-callee set.");
                    if ((HybridCpuOpcode)carrier.OpCode != HybridCpuOpcode.JAL || carrier.Immediate != 0)
                        throw new InvalidOperationException("Managed call relocation requires an unpatched JAL Immediate carrier.");
                    ulong offset = checked((ulong)bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes +
                        (ulong)slot.SlotIndex * HybridCpuInstructionWord.EncodedSize +
                        HybridCpuManagedCallRelocationContractV1.ImmediateFieldOffsetBytes);
                    relocations.Add(new(".text", offset, HybridCpuRelocationKind.ManagedCallRelativeSigned16,
                        target, HybridCpuManagedCallRelocationContractV1.RequiredAddend));
                }
                bundleIndex++;
            }
        if (bundleIndex != lowered.Count)
            throw new InvalidOperationException("Final bundle layout and lowered call relocation stream disagree.");
        return relocations.OrderBy(static relocation => relocation.Offset)
            .ThenBy(static relocation => relocation.TargetSymbol, StringComparer.Ordinal).ToArray();
    }

    private static bool IsGuestServiceTarget(string target) => target is
        HybridCpuManagedDoomClockEmitterV1.Symbol or
        HybridCpuManagedFramebufferEmitterV1.Symbol or
        HybridCpuManagedConsoleWriteEmitterV1.Symbol or
        HybridCpuManagedDoomWaitEmitterV1.Symbol or
        HybridCpuManagedBootBlobEmitterV1.Symbol or
        HybridCpuManagedGuestProcessExitEmitterV1.Symbol or
        HybridCpuManagedFramebufferPresentEmitterV1.Symbol or
        HybridCpuManagedConsoleTitleEmitterV1.Symbol or
        HybridCpuManagedInputPullEmitterV1.Symbol or
        HybridCpuManagedPaletteEmitterV1.Symbol;

    private static string[] BuildUndefinedCallTargets(
        string methodIdentity,
        IReadOnlyList<string> directCalleeIdentities,
        IReadOnlyList<HybridCpuObjectRelocationV1> relocations,
        IReadOnlySet<string> thunkedTargets) =>
        directCalleeIdentities.Concat(relocations.Select(static relocation => relocation.TargetSymbol))
            .Where(callee => callee != methodIdentity && !thunkedTargets.Contains(callee))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static ScalarControlFlowV2LinkedProgramV1 Failure(
        ScalarControlFlowV2LinkStatusV1 status,
        string entryIdentity,
        string code,
        string message,
        string? stableIdentity = null,
        IReadOnlyList<ScalarControlFlowV2MethodObjectV1>? objects = null,
        HybridCpuStaticLinkArtifactV1? linked = null,
        HybridCpuRestrictedImageV1? image = null) =>
        new(status, entryIdentity ?? string.Empty, objects ?? Array.Empty<ScalarControlFlowV2MethodObjectV1>(), linked, image,
            string.Empty, string.Empty,
            [new ScalarControlFlowV2LinkDiagnosticV1(code, message, stableIdentity ?? entryIdentity ?? string.Empty)]);

    private static string DigestObjects(IEnumerable<ScalarControlFlowV2MethodObjectV1> objects) => Hash(string.Join('|',
        objects.Select(static item => $"{item.MethodIdentity}:{item.ModuleIdentity}:{item.ObjectArtifact.ObjectSha256}")));

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
