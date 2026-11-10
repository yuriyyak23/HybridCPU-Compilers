using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public sealed record HybridCpuManagedExceptionObjectLayoutV1(
    ulong ExceptionTypeId,
    string MessageFieldIdentity);

/// <summary>
/// Bounded CoreLib exception-object operations. The compiler/runtime contract supplies one exact
/// System.Exception message field identity; no host Exception object or side table participates.
/// </summary>
public sealed class HybridCpuManagedExceptionObjectRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly HybridCpuManagedExceptionObjectLayoutV1 _layout;
    private readonly int _messageOffset;

    public HybridCpuManagedExceptionObjectRuntimeV1(
        HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap,
        HybridCpuManagedExceptionObjectLayoutV1 layout)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        if (layout.ExceptionTypeId == 0 || string.IsNullOrWhiteSpace(layout.MessageFieldIdentity))
            throw new ArgumentException("Exception message layout requires an exact type and field identity.", nameof(layout));
        HybridCpuManagedTypeDescriptorV1? exception = types.Resolve(layout.ExceptionTypeId);
        HybridCpuManagedFieldLayoutV1? message = exception?.InstanceFields.SingleOrDefault(field =>
            string.Equals(field.Identity, layout.MessageFieldIdentity, StringComparison.Ordinal));
        if (message is null || message.StorageKind != HybridCpuManagedStorageKindV1.ObjectReference ||
            message.SizeBytes != sizeof(ulong) || message.AlignmentBytes != sizeof(ulong) ||
            message.OffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes)
            throw new ArgumentException("Exception message field is absent or is not one exact aligned object reference.", nameof(layout));
        _messageOffset = message.OffsetBytes;
    }

    public HybridCpuManagedShapeResultV1 SetMessage(ulong exceptionReference, ulong messageReference)
    {
        HybridCpuManagedShapeResultV1 validation = ValidateException(exceptionReference);
        if (!validation.IsSuccess) return validation;
        if (messageReference != 0 && _heap.ReadObjectBytes(messageReference) is null)
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "Exception message is not a runtime-owned managed reference.");
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, messageReference);
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(exceptionReference, _messageOffset, bytes);
        return write.IsSuccess
            ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, exceptionReference)
            : new(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
    }

    public HybridCpuManagedShapeResultV1 GetMessage(ulong exceptionReference)
    {
        HybridCpuManagedShapeResultV1 validation = ValidateException(exceptionReference);
        if (!validation.IsSuccess) return validation;
        byte[]? bytes = _heap.ReadObjectPayload(exceptionReference, _messageOffset, sizeof(ulong));
        if (bytes is null)
            return new(HybridCpuManagedShapeStatusV1.HeapFailure, "Exception message field is outside the runtime-owned allocation.");
        ulong messageReference = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (messageReference != 0 && _heap.ReadObjectBytes(messageReference) is null)
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "Exception message field contains an invalid managed reference.");
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, messageReference);
    }

    private HybridCpuManagedShapeResultV1 ValidateException(ulong exceptionReference)
    {
        if (exceptionReference == 0)
            return new(HybridCpuManagedShapeStatusV1.NullReference, "Exception reference is null.");
        byte[]? bytes = _heap.ReadObjectBytes(exceptionReference);
        if (bytes is null || bytes.Length < sizeof(ulong))
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "Exception reference is not runtime-owned.");
        HybridCpuManagedTypeDescriptorV1? actual = _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        return actual is not null && _types.IsAssignable(actual.TypeId, _layout.ExceptionTypeId)
            ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, exceptionReference)
            : new(HybridCpuManagedShapeStatusV1.InvalidType, "Object is not assignable to the exact System.Exception contract.");
    }
}

public enum HybridCpuManagedExceptionStatusV1 : byte
{
    Handled = 0,
    UnhandledTermination = 1,
    InvalidMetadata = 2,
    InvalidException = 3,
    BudgetExhausted = 4,
    GcRejected = 5
}

public sealed record HybridCpuManagedFinallyOutcomeV1(
    bool Completed,
    ulong ReplacementExceptionReference = 0,
    ulong ReplacementExceptionTypeId = 0);

public sealed record HybridCpuManagedExceptionDispatchResultV1(
    HybridCpuManagedExceptionStatusV1 Status,
    ulong ExceptionReference,
    ulong ExceptionTypeId,
    string? HandlerMethodIdentity,
    int HandlerInstructionPointer,
    ulong HandlerStackPointer,
    int UnwoundFrames,
    int FinallyInvocations,
    IReadOnlyList<string> Trace,
    string ResultDigest)
{
    public bool UsedArchitecturalTrap => false;
    public bool HasIseAuthority => false;
}

public sealed record HybridCpuManagedExceptionProcessResultV1(
    HybridCpuManagedExceptionDispatchResultV1 Dispatch,
    HybridCpuKernelResultV1? ProcessExit)
{
    public bool CanEnterHandler => Dispatch.Status == HybridCpuManagedExceptionStatusV1.Handled && ProcessExit is null;
    public bool ProcessTerminated => Dispatch.Status == HybridCpuManagedExceptionStatusV1.UnhandledTermination &&
        ProcessExit is { IsSuccess: true };
}

public sealed record HybridCpuManagedUnwindContextV1(ulong ProgramCounter, IReadOnlyList<ulong> Registers);
public sealed record HybridCpuManagedExceptionNativeResultV1(
    HybridCpuManagedExceptionDispatchResultV1 Dispatch, byte[]? TransferRecord);

public sealed class HybridCpuManagedExceptionRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly IReadOnlyDictionary<string, MethodRegistration> _methods;

    public HybridCpuManagedExceptionRuntimeV1(HybridCpuManagedTypeSystemV1 types,
        IReadOnlyList<HybridCpuManagedEhMethodRegistrationV1> methods)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Count > HybridCpuPlatformContractV1.MaximumCodeManagerRecords ||
            methods.Any(static row => row is null || string.IsNullOrWhiteSpace(row.MethodIdentity)) ||
            methods.Select(static row => row.MethodIdentity).Distinct(StringComparer.Ordinal).Count() != methods.Count)
            throw new ArgumentException("Managed EH registrations are malformed or exceed deterministic budgets.", nameof(methods));
        _methods = methods.OrderBy(static row => row.MethodIdentity, StringComparer.Ordinal)
            .ToDictionary(static row => row.MethodIdentity, Parse, StringComparer.Ordinal);
        MethodRegistration[] ranges = _methods.Values.OrderBy(static row => row.CodeStart).ToArray();
        if (ranges.Zip(ranges.Skip(1), static (left, right) =>
                (long)left.CodeStart + left.CodeSize > right.CodeStart).Any(static overlap => overlap) ||
            _methods.Values.SelectMany(static row => row.Clauses).Any(clause =>
                clause.Kind == HybridCpuManagedEhClauseKindV1.Catch && _types.Resolve(clause.CatchTypeId) is null))
            throw new ArgumentException("Managed EH code ranges overlap or a catch type is absent from the exact type system.",
                nameof(methods));
    }

    /// <summary>Restore one native frame from its exact unwind record. All memory reads and
    /// arithmetic are validated before a new context is returned; the input is never mutated.
    /// This computes transfer state; it does not itself write CPU registers or branch.</summary>
    public bool TryUnwindFrame(string methodIdentity, HybridCpuManagedUnwindContextV1 context,
        Func<ulong, ulong?> readStackWord, out HybridCpuManagedUnwindContextV1? caller,
        bool instructionPointerIsReturnAddress = false)
    {
        caller = null;
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(readStackWord);
        if (instructionPointerIsReturnAddress && (context.ProgramCounter < 256 || context.ProgramCounter % 256 != 0)) return false;
        ulong lookupPc = context.ProgramCounter - (instructionPointerIsReturnAddress ? 256UL : 0UL);
        if (!_methods.TryGetValue(methodIdentity, out MethodRegistration? method) || context.Registers is not { Count: 32 } ||
            context.Registers[0] != 0 || lookupPc < (ulong)method.CodeStart ||
            lookupPc >= (ulong)method.CodeStart + (ulong)method.CodeSize) return false;
        ulong[] current = context.Registers.ToArray();
        DecodedUnwind unwind = method.Unwind;
        ulong basis = current[unwind.CfaBase == 0 ? 2 : 8];
        if (basis == 0 || basis > ulong.MaxValue - (ulong)unwind.CfaOffset) return false;
        ulong cfa = basis + (ulong)unwind.CfaOffset;
        if (cfa % 16 != 0) return false;
        bool Read(int offset, out ulong value)
        {
            value = 0;
            long displacement = offset;
            if (displacement < 0 && cfa < (ulong)-displacement ||
                displacement >= 0 && cfa > ulong.MaxValue - (ulong)displacement) return false;
            ulong address = displacement < 0 ? cfa - (ulong)-displacement : cfa + (ulong)displacement;
            if (address % 8 != 0 || address > ulong.MaxValue - 7) return false;
            ulong? word = readStackWord(address);
            if (!word.HasValue) return false;
            value = word.Value;
            return true;
        }
        ulong returnPc;
        if (unwind.ReturnRegister >= 1) returnPc = current[unwind.ReturnRegister];
        else if (!Read(unwind.ReturnStackOffset, out returnPc)) return false;
        if (returnPc == 0 || returnPc % 256 != 0) return false;
        ulong[] restored = current.ToArray();
        foreach ((int Register, int Offset) saved in unwind.SavedRegisters)
        {
            if (!Read(saved.Offset, out ulong value)) return false;
            restored[saved.Register] = value;
        }
        restored[2] = cfa;
        caller = new(returnPc, Array.AsReadOnly(restored));
        return true;
    }

    /// <summary>Validate native snapshots before any managed callback, dispatch with roots, then
    /// encode the restored handler context. The caller owns record storage and the final native jump.</summary>
    public HybridCpuManagedExceptionNativeResultV1 PrepareNativeHandlerTransfer(
        HybridCpuManagedExceptionStateV1 state, ulong token,
        IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1> frames,
        HybridCpuManagedUnwindContextV1 current, Func<ulong, ulong?> readStackWord,
        Func<ulong, IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1>, bool> gcSafepoint,
        Func<string, int, ulong, HybridCpuManagedFinallyOutcomeV1>? executeFinally = null,
        bool rethrow = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(current);
        HybridCpuManagedExceptionNativeResultV1 Invalid(string reason) => new(
            Result(HybridCpuManagedExceptionStatusV1.InvalidMetadata, 0, 0, null, -1, 0, 0, [reason]), null);
        if (frames.Count is 0 or > HybridCpuManagedEhSchemaV1.MaximumFramesPerDispatch || current.Registers is not { Count: 32 })
            return Invalid("native-frame-budget-or-register-bank");
        // Do not let callbacks mutate the caller-supplied frame list after validation.
        HybridCpuManagedEhFrameSnapshotV1[] snapshots = frames.ToArray();
        for (int index = 1; index < snapshots.Length; index++)
            if (snapshots[index] is not null) snapshots[index] = snapshots[index] with { InstructionPointerIsReturnAddress = true };
        List<HybridCpuManagedUnwindContextV1> contexts = new List<HybridCpuManagedUnwindContextV1>();
        HybridCpuManagedUnwindContextV1 restored = new HybridCpuManagedUnwindContextV1(current.ProgramCounter, Array.AsReadOnly(current.Registers.ToArray()));
        for (int index = 0; index < snapshots.Length; index++)
        {
            HybridCpuManagedEhFrameSnapshotV1? frame = snapshots[index];
            if (frame is null || !_methods.TryGetValue(frame.MethodIdentity, out MethodRegistration? method) ||
                restored.ProgramCounter != (ulong)frame.InstructionPointer || restored.ProgramCounter % 256 != 0 ||
                restored.Registers[0] != 0 || restored.Registers[2] != frame.StackPointer || frame.StackPointer == 0 ||
                frame.StackPointer % 16 != 0 ||
                (method.Unwind.CfaBase == 1 || frame.FramePointer != 0) && restored.Registers[8] != frame.FramePointer ||
                method.Clauses.Any(clause => method.CodeStart + clause.HandlerStartOffsetBytes == 0 ||
                    (method.CodeStart + clause.HandlerStartOffsetBytes) % 256 != 0))
                return Invalid("native-frame-context-mismatch");
            contexts.Add(restored);
            if (index + 1 < snapshots.Length)
            {
                if (!TryUnwindFrame(frame.MethodIdentity, restored, readStackWord, out HybridCpuManagedUnwindContextV1? caller, frame.InstructionPointerIsReturnAddress) ||
                    caller!.ProgramCounter != frame.ReturnProgramCounter)
                    return Invalid("native-frame-unwind-failed");
                restored = caller;
            }
        }
        HybridCpuManagedExceptionDispatchResultV1 dispatch = DispatchRooted(state, token, snapshots, gcSafepoint, executeFinally, rethrow);
        if (dispatch.Status != HybridCpuManagedExceptionStatusV1.Handled) return new(dispatch, null);
        HybridCpuManagedUnwindContextV1 handler = contexts[dispatch.UnwoundFrames];
        if (handler.Registers[2] != dispatch.HandlerStackPointer || state.CurrentToken != token ||
            state.CurrentReference != dispatch.ExceptionReference)
            return Invalid("native-handler-state-mismatch");
        return new(dispatch, HybridCpuManagedExceptionTransferV1.Encode(
            checked((ulong)dispatch.HandlerInstructionPointer), handler.Registers, dispatch.ExceptionReference));
    }

    /// <summary>Runtime process boundary: only a verified unhandled dispatch may request exit.
    /// The exception scope remains rooted throughout dispatch and the kernel transition.
    /// This result is not permission to resume the throwing instruction.</summary>
    public HybridCpuManagedExceptionProcessResultV1 DispatchForProcess(
        HybridCpuManagedExceptionStateV1 state, ulong token,
        IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1> frames,
        Func<ulong, IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1>, bool> gcSafepoint,
        IHybridCpuRuntimeKernelV1 kernel, int unhandledExitCode,
        Func<string, int, ulong, HybridCpuManagedFinallyOutcomeV1>? executeFinally = null,
        bool rethrow = false)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        HybridCpuManagedExceptionDispatchResultV1 dispatch = DispatchRooted(state, token, frames, gcSafepoint, executeFinally, rethrow);
        return new(dispatch, dispatch.Status == HybridCpuManagedExceptionStatusV1.UnhandledTermination
            ? kernel.ProcessExit(unhandledExitCode) : null);
    }

    /// <summary>Dispatch using an already retained exception scope. The caller keeps the token
    /// alive through catch execution or unhandled process termination and pops it on leave/unwind.
    /// The required checkpoint must collect with this state in the GC request.</summary>
    public HybridCpuManagedExceptionDispatchResultV1 DispatchRooted(
        HybridCpuManagedExceptionStateV1 state, ulong token,
        IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1> frames,
        Func<ulong, IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1>, bool> gcSafepoint,
        Func<string, int, ulong, HybridCpuManagedFinallyOutcomeV1>? executeFinally = null,
        bool rethrow = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(gcSafepoint);
        if (!state.UsesTypes(_types) || token == 0 || state.CurrentToken != token || state.TypeOf(state.CurrentReference) == 0)
            return Result(HybridCpuManagedExceptionStatusV1.InvalidException, 0, 0, null, -1, 0, 0, ["invalid-exception-scope"]);
        return Dispatch(state.CurrentReference, state.TypeOf(state.CurrentReference), frames,
            executeFinally is null ? null : (method, pc, reference) =>
            {
                global::HybridCPU.ManagedRuntime.HybridCpuManagedFinallyOutcomeV1 outcome = executeFinally(method, pc, reference);
                if (outcome.ReplacementExceptionReference != 0 &&
                    state.TypeOf(outcome.ReplacementExceptionReference) != outcome.ReplacementExceptionTypeId)
                    return new(false);
                return outcome;
            },
            (reference, liveFrames) => state.TryReplace(token, reference) && gcSafepoint(reference, liveFrames), rethrow);
    }

    public HybridCpuManagedExceptionDispatchResultV1 Dispatch(
        ulong exceptionReference,
        ulong exceptionTypeId,
        IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1> frames,
        Func<string, int, ulong, HybridCpuManagedFinallyOutcomeV1>? executeFinally = null,
        Func<ulong, IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1>, bool>? gcSafepoint = null,
        bool rethrow = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (exceptionReference == 0 || _types.Resolve(exceptionTypeId) is null)
            return Result(HybridCpuManagedExceptionStatusV1.InvalidException, exceptionReference, exceptionTypeId,
                null, -1, 0, 0, ["invalid-exception"]);
        if (frames.Count is 0 || frames.Count > HybridCpuManagedEhSchemaV1.MaximumFramesPerDispatch)
            return Result(HybridCpuManagedExceptionStatusV1.BudgetExhausted, exceptionReference, exceptionTypeId,
                null, -1, 0, 0, ["frame-budget"]);
        for (int index = 0; index < frames.Count; index++)
        {
            HybridCpuManagedEhFrameSnapshotV1 frame = frames[index];
            if (frame is null || !_methods.TryGetValue(frame.MethodIdentity, out MethodRegistration? method) ||
                LookupPc(frame) < method.CodeStart ||
                LookupPc(frame) >= (long)method.CodeStart + method.CodeSize ||
                frame.InstructionPointerIsReturnAddress && frame.InstructionPointer % 256 != 0)
                return Result(HybridCpuManagedExceptionStatusV1.InvalidMetadata, exceptionReference, exceptionTypeId,
                    null, -1, 0, 0, [$"invalid-frame:{index}"]);
        }
        for (int index = 0; index < frames.Count - 1; index++)
        {
            HybridCpuManagedEhFrameSnapshotV1 frame = frames[index];
            HybridCpuManagedEhFrameSnapshotV1 caller = frames[index + 1];
            MethodRegistration method = _methods[frame.MethodIdentity];
            ulong cfaBase = method.Unwind.CfaBase == 0 ? frame.StackPointer : frame.FramePointer;
            if (cfaBase == 0 && method.Unwind.CfaBase != 0 ||
                cfaBase > ulong.MaxValue - checked((ulong)method.Unwind.CfaOffset) ||
                cfaBase + checked((ulong)method.Unwind.CfaOffset) != caller.StackPointer ||
                frame.ReturnProgramCounter != checked((ulong)caller.InstructionPointer))
                return Result(HybridCpuManagedExceptionStatusV1.InvalidMetadata, exceptionReference, exceptionTypeId,
                    null, -1, 0, 0, [$"invalid-unwind-chain:{index}"]);
        }
        if (rethrow)
        {
            HybridCpuManagedEhFrameSnapshotV1 current = frames[0];
            MethodRegistration method = _methods[current.MethodIdentity];
            long pc = LookupPc(current) - method.CodeStart;
            if (!method.Clauses.Any(clause => clause.Kind == HybridCpuManagedEhClauseKindV1.Catch &&
                    pc >= clause.HandlerStartOffsetBytes && pc < clause.HandlerStartOffsetBytes + clause.HandlerSizeBytes))
                return Result(HybridCpuManagedExceptionStatusV1.InvalidMetadata, exceptionReference, exceptionTypeId,
                    null, -1, 0, 0, ["rethrow-outside-catch"]);
        }
        if (gcSafepoint is not null && !gcSafepoint(exceptionReference, frames))
            return Result(HybridCpuManagedExceptionStatusV1.GcRejected, exceptionReference, exceptionTypeId,
                null, -1, 0, 0, ["gc-rejected"]);

        List<string> trace = new List<string> { rethrow ? "rethrow" : "throw" };
        int finallyCount = 0;
        // The current PC is the throw/rethrow site, including when it is in a catch.
        // Enclosing try regions in the same frame still participate in the search.
        (int Frame, HybridCpuManagedEhClauseRegistrationV1 Clause)? target = FindCatch(exceptionTypeId, frames, 0);
        int unwindThrough = target is null ? frames.Count : target.Value.Frame + 1;
        for (int frameIndex = 0; frameIndex < unwindThrough; frameIndex++)
        {
            HybridCpuManagedEhFrameSnapshotV1 frame = frames[frameIndex];
            MethodRegistration method = _methods[frame.MethodIdentity];
            long relativePc = LookupPc(frame) - method.CodeStart;
            foreach (HybridCpuManagedEhClauseRegistrationV1 clause in method.Clauses
                         .Where(row => row.Kind == HybridCpuManagedEhClauseKindV1.Finally &&
                             relativePc >= row.TryStartOffsetBytes && relativePc < row.TryStartOffsetBytes + row.TrySizeBytes &&
                             !(target is { } selected && selected.Frame == frameIndex &&
                               selected.Clause.HandlerStartOffsetBytes >= row.TryStartOffsetBytes &&
                               selected.Clause.HandlerStartOffsetBytes < row.TryStartOffsetBytes + row.TrySizeBytes))
                         .OrderBy(static row => row.TrySizeBytes).ThenBy(static row => row.Ordinal))
            {
                if (++finallyCount > HybridCpuManagedEhSchemaV1.MaximumFinallyInvocations)
                    return Result(HybridCpuManagedExceptionStatusV1.BudgetExhausted, exceptionReference, exceptionTypeId,
                        null, -1, frameIndex, finallyCount, trace.Append("finally-budget").ToArray());
                trace.Add($"finally:{frame.MethodIdentity}:{clause.Ordinal}");
                HybridCpuManagedFinallyOutcomeV1 outcome = executeFinally?.Invoke(frame.MethodIdentity,
                    method.CodeStart + clause.HandlerStartOffsetBytes, exceptionReference) ?? new(false);
                if (!outcome.Completed)
                    return Result(HybridCpuManagedExceptionStatusV1.InvalidMetadata, exceptionReference, exceptionTypeId,
                        null, -1, frameIndex, finallyCount, trace.Append("finally-incomplete").ToArray());
                if (outcome.ReplacementExceptionReference != 0)
                {
                    if (outcome.ReplacementExceptionTypeId == 0 || _types.Resolve(outcome.ReplacementExceptionTypeId) is null)
                        return Result(HybridCpuManagedExceptionStatusV1.InvalidException,
                            outcome.ReplacementExceptionReference, outcome.ReplacementExceptionTypeId,
                            null, -1, frameIndex, finallyCount, trace.Append("invalid-finally-throw").ToArray());
                    exceptionReference = outcome.ReplacementExceptionReference;
                    exceptionTypeId = outcome.ReplacementExceptionTypeId;
                    trace.Add("throw-in-finally");
                    // A finally can throw into an enclosing handler in this very frame.
                    // Restart at its handler PC so completed inner finally regions are not replayed.
                    HybridCpuManagedEhFrameSnapshotV1[] remaining = frames.ToArray();
                    remaining[frameIndex] = frame with
                    {
                        InstructionPointer = checked(method.CodeStart + clause.HandlerStartOffsetBytes),
                        InstructionPointerIsReturnAddress = false
                    };
                    frames = remaining;
                    if (gcSafepoint is not null && !gcSafepoint(exceptionReference, remaining.Skip(frameIndex).ToArray()))
                        return Result(HybridCpuManagedExceptionStatusV1.GcRejected, exceptionReference, exceptionTypeId,
                            null, -1, frameIndex, finallyCount, trace.Append("gc-rejected").ToArray());
                    target = FindCatch(exceptionTypeId, frames, frameIndex);
                    unwindThrough = target is null ? frames.Count : target.Value.Frame + 1;
                    frameIndex--;
                    break;
                }
            }
        }

        if (target is null)
        {
            trace.Add("unhandled-terminate");
            return Result(HybridCpuManagedExceptionStatusV1.UnhandledTermination, exceptionReference, exceptionTypeId,
                null, -1, frames.Count, finallyCount, trace);
        }
        HybridCpuManagedEhFrameSnapshotV1 handlerFrame = frames[target.Value.Frame];
        MethodRegistration handlerMethod = _methods[handlerFrame.MethodIdentity];
        int handlerPc = checked(handlerMethod.CodeStart + target.Value.Clause.HandlerStartOffsetBytes);
        trace.Add($"catch:{handlerFrame.MethodIdentity}:{target.Value.Clause.Ordinal}");
        return Result(HybridCpuManagedExceptionStatusV1.Handled, exceptionReference, exceptionTypeId,
            handlerFrame.MethodIdentity, handlerPc, target.Value.Frame, finallyCount, trace, handlerFrame.StackPointer);
    }

    private (int Frame, HybridCpuManagedEhClauseRegistrationV1 Clause)? FindCatch(ulong exceptionTypeId,
        IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1> frames, int startFrame)
    {
        for (int index = startFrame; index < frames.Count; index++)
        {
            HybridCpuManagedEhFrameSnapshotV1 frame = frames[index];
            MethodRegistration method = _methods[frame.MethodIdentity];
            long relativePc = LookupPc(frame) - method.CodeStart;
            HybridCpuManagedEhClauseRegistrationV1? clause = method.Clauses
                .Where(row => row.Kind == HybridCpuManagedEhClauseKindV1.Catch &&
                    relativePc >= row.TryStartOffsetBytes && relativePc < row.TryStartOffsetBytes + row.TrySizeBytes &&
                    _types.IsAssignable(exceptionTypeId, row.CatchTypeId))
                .OrderBy(static row => row.TrySizeBytes).ThenBy(static row => row.Ordinal).FirstOrDefault();
            if (clause is not null) return (index, clause);
        }
        return null;
    }

    private static long LookupPc(HybridCpuManagedEhFrameSnapshotV1 frame) =>
        (long)frame.InstructionPointer - (frame.InstructionPointerIsReturnAddress ? 256 : 0);

    private static MethodRegistration Parse(HybridCpuManagedEhMethodRegistrationV1 row)
    {
        if (row.CodeStartOffsetBytes < 0 || row.CodeSizeBytes <= 0 ||
            row.CodeStartOffsetBytes > int.MaxValue - row.CodeSizeBytes || row.EhInfo is null || row.UnwindInfo is null)
            throw new ArgumentException("Managed EH registration range or payload is invalid.");
        byte[] bytes = row.EhInfo;
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != HybridCpuManagedEhSchemaV1.EhMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != HybridCpuManagedEhSchemaV1.SchemaVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != 0)
            throw new ArgumentException("Managed EH header is invalid.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (count < 0 || count > HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod || bytes.Length != 12 + count * 40)
            throw new ArgumentException("Managed EH payload exceeds the deterministic schema.");
        HybridCpuManagedEhClauseRegistrationV1[] clauses = new HybridCpuManagedEhClauseRegistrationV1[count];
        for (int index = 0, offset = 12; index < count; index++, offset += 40)
        {
            HybridCpuManagedEhClauseKindV1 kind = (HybridCpuManagedEhClauseKindV1)bytes[offset];
            int tryStart = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
            int trySize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 8));
            int handlerStart = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12));
            int handlerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 16));
            ulong catchType = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 20));
            int ordinal = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 28));
            if (!Enum.IsDefined(kind) || tryStart < 0 || trySize <= 0 || handlerStart < 0 || handlerSize <= 0 ||
                tryStart > row.CodeSizeBytes - trySize || handlerStart > row.CodeSizeBytes - handlerSize ||
                kind == HybridCpuManagedEhClauseKindV1.Catch && catchType == 0 ||
                kind == HybridCpuManagedEhClauseKindV1.Finally && catchType != 0 ||
                bytes.AsSpan(offset + 1, 3).ToArray().Any(static value => value != 0) ||
                bytes.AsSpan(offset + 32, 8).ToArray().Any(static value => value != 0))
                throw new ArgumentException("Managed EH clause is corrupt or outside its final code range.");
            clauses[index] = new(kind, tryStart, trySize, handlerStart, handlerSize, catchType, ordinal);
        }
        if (!clauses.Select(static item => item.Ordinal).SequenceEqual(Enumerable.Range(0, clauses.Length)))
            throw new ArgumentException("Managed EH clause ordinals are not canonical.");
        DecodedUnwind unwind = ParseUnwind(row.UnwindInfo);
        return new(row.CodeStartOffsetBytes, row.CodeSizeBytes, clauses, unwind);
    }

    private static DecodedUnwind ParseUnwind(byte[] bytes)
    {
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != HybridCpuManagedEhSchemaV1.UnwindMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != HybridCpuManagedEhSchemaV1.SchemaVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)) != bytes.Length)
            throw new ArgumentException("Managed unwind header is invalid.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        int cfa = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        int returnRegister = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        int returnStack = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
        if (count > 32 || bytes.Length != 28 + count * 8 || bytes[8] > 1 || bytes[9] > 1 ||
            cfa < 0 || cfa % 8 != 0 ||
            !((returnRegister is >= 1 and <= 31 && returnStack == int.MinValue) ||
              (returnRegister == -1 && returnStack != int.MinValue && returnStack % 8 == 0)))
            throw new ArgumentException("Managed unwind record is malformed.");
        int prior = 0;
        List<(int Register, int Offset)> saved = new List<(int Register, int Offset)>();
        for (int index = 0, offset = 28; index < count; index++, offset += 8)
        {
            int register = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            int relative = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
            if (register is < 1 or > 31 || register <= prior || relative == int.MinValue || relative % 8 != 0)
                throw new ArgumentException("Managed unwind saved-register row is non-canonical.");
            prior = register;
            saved.Add((register, relative));
        }
        return new(bytes[9], cfa, returnRegister, returnStack, saved.AsReadOnly());
    }

    private static HybridCpuManagedExceptionDispatchResultV1 Result(
        HybridCpuManagedExceptionStatusV1 status, ulong reference, ulong typeId, string? method, int handlerIp,
        int unwound, int finallyCount, IReadOnlyList<string> trace, ulong handlerStackPointer = 0)
    {
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', HybridCpuManagedEhSchemaV1.SchemaId,
            status, reference, typeId, method, handlerIp, handlerStackPointer, unwound, finallyCount, string.Join(';', trace)));
        return new(status, reference, typeId, method, handlerIp, handlerStackPointer, unwound, finallyCount, trace, digest);
    }

    private sealed record MethodRegistration(int CodeStart, int CodeSize,
        IReadOnlyList<HybridCpuManagedEhClauseRegistrationV1> Clauses, DecodedUnwind Unwind);
    private sealed record DecodedUnwind(byte CfaBase, int CfaOffset, int ReturnRegister, int ReturnStackOffset,
        IReadOnlyList<(int Register, int Offset)> SavedRegisters);
}
