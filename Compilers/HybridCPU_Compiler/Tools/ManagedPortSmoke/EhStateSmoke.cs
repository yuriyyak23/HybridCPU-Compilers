using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

internal static class EhStateSmoke
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new("ExceptionBase", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new("Unrelated", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        ulong exceptionType = types.Descriptors.Single(t => t.StableIdentity == "ExceptionBase").TypeId;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "kernel boot");
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
        Check(heap.Initialize().IsSuccess, "heap initialization");
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest, abi.TargetContractDigest,
            abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedExceptionStateV1 state = new HybridCpuManagedExceptionStateV1(types, heap, exceptionType);
        ulong Allocate() => heap.Allocate(types.TypeHandle(exceptionType)!.Value).ObjectReference;
        HybridCpuManagedNonMovingGcResultV1 Collect() => gc.Collect(new([], [], [], Exceptions: state),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        ulong first = Allocate(), second = Allocate();
        ulong unrelated = heap.Allocate(types.TypeHandle(types.Descriptors.Single(t => t.StableIdentity == "Unrelated").TypeId)!.Value).ObjectReference;
        Check(!state.TryPush(0, out _) && !state.TryPush(unrelated, out _) && !state.TryPush(0x123, out _),
            "null, foreign addresses and non-exceptions cannot become exception state");
        Check(state.TryPush(first, out ulong outer) && outer != 0, "outer exception root");
        Check(state.TryPush(second, out ulong inner) && inner > outer, "nested exception root");
        Check(!state.TryPop(outer) && !state.TryReplace(outer, second), "out-of-order state mutations fail closed");
        HybridCpuManagedNonMovingGcResultV1 result = Collect();
        Check(result.IsSuccess && result.Roots.Contains(first) && result.Roots.Contains(second) &&
            result.ReclaimedObjects.Contains(unrelated), "GC retains both active catches, not unrelated objects");
        ulong replacement = Allocate();
        Check(!state.TryReplace(inner, 0) && state.CurrentReference == second, "invalid replacement leaves state intact");
        Check(state.TryReplace(inner, replacement), "replace current exception without releasing outer catch root");
        result = Collect();
        Check(result.IsSuccess && result.ReclaimedObjects.Contains(second) && result.Roots.Contains(first) &&
            result.Roots.Contains(replacement), "GC sees replacement root and retains enclosing exception");
        Check(state.TryPop(inner) && state.CurrentReference == first && !state.TryPop(inner), "leave restores outer state; stale token rejected");
        result = Collect();
        Check(result.IsSuccess && result.ReclaimedObjects.Contains(replacement) && result.Roots.Contains(first), "left catch releases only its own exception");
        Check(state.TryPop(outer) && state.Depth == 0, "last catch exit");
        result = Collect();
        Check(result.IsSuccess && result.ReclaimedObjects.Contains(first), "last released exception is reclaimable");
        ulong thrown = Allocate();
        Check(state.TryPush(thrown, out ulong dispatchToken), "dispatch root");
        HybridCpuManagedExceptionRuntimeV1 runtime = new HybridCpuManagedExceptionRuntimeV1(types, [new("M", 0, 200,
            EhDispatchSmoke.Encode([
                new(HybridCpuManagedEhClauseKindV1.Finally, 0, 50, 60, 10, 0, 0),
                new(HybridCpuManagedEhClauseKindV1.Catch, 0, 100, 120, 10, exceptionType, 1)]),
            HybridCpuManagedUnwindCodecV2.Encode(new(HybridCpuManagedFrameKindV1.Managed,
                HybridCpuManagedCfaBaseV1.StackPointer, 0, 1, null, [])))]);
        int checkpoints = 0;
        ulong finalReference = 0;
        HybridCpuManagedExceptionDispatchResultV1 dispatch = runtime.DispatchRooted(state, dispatchToken, [new("M", 20, 4096, 0)],
            (reference, _) =>
            {
                checkpoints++;
                HybridCpuManagedNonMovingGcResultV1 collected = Collect();
                return collected.IsSuccess && collected.Roots.Contains(reference) && state.CurrentReference == reference;
            }, (_, _, _) => { finalReference = Allocate(); return new(true, finalReference, exceptionType); });
        Check(dispatch.Status == HybridCpuManagedExceptionStatusV1.Handled && checkpoints == 2 &&
            dispatch.ExceptionReference == finalReference && state.CurrentReference == finalReference,
            "rooted dispatcher retains the replacement across real collection and handler transfer");
        Check(Collect().Roots.Contains(finalReference), "handler owns exception root after dispatcher returns");
        Check(runtime.DispatchRooted(state, outer, [new("M", 20, 4096, 0)], (_, _) => true).Status ==
            HybridCpuManagedExceptionStatusV1.InvalidException, "stale dispatch token must be rejected");
        Check(state.TryPop(dispatchToken), "handler leave");
        Check(Collect().ReclaimedObjects.Contains(finalReference), "handler leave releases dispatch exception");
        ulong fatal = Allocate();
        Check(state.TryPush(fatal, out ulong fatalToken), "process-boundary exception root");
        HybridCpuManagedExceptionRuntimeV1 nativeRuntime = new HybridCpuManagedExceptionRuntimeV1(types, [
            new("Leaf", 0, 1024, EhDispatchSmoke.Encode([]), HybridCpuManagedUnwindCodecV2.Encode(
                new(HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.StackPointer, 16, null, -8, [new(1, -8)]))),
            new("Parent", 1024, 1024, EhDispatchSmoke.Encode([
                new(HybridCpuManagedEhClauseKindV1.Catch, 0, 256, 512, 256, exceptionType, 0)]),
                HybridCpuManagedUnwindCodecV2.Encode(new(HybridCpuManagedFrameKindV1.Managed,
                    HybridCpuManagedCfaBaseV1.StackPointer, 0, 1, null, [])))]);
        ulong[] bank = new ulong[32]; bank[2] = 4096;
        HybridCpuManagedEhFrameSnapshotV1[] nativeFrames = new HybridCpuManagedEhFrameSnapshotV1[] { new("Leaf", 256, 4096, 1280), new("Parent", 1280, 4112, 0) };
        int nativeCheckpoints = 0;
        HybridCpuManagedExceptionNativeResultV1 prepared = nativeRuntime.PrepareNativeHandlerTransfer(state, fatalToken, nativeFrames,
            new(256, bank), address => address == 4104 ? 1280UL : null,
            (_, _) => { nativeCheckpoints++; return Collect().IsSuccess; });
        Check(prepared.Dispatch.Status == HybridCpuManagedExceptionStatusV1.Handled && prepared.TransferRecord is not null &&
            nativeCheckpoints == 1 && System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                prepared.TransferRecord.AsSpan(HybridCpuManagedExceptionTransferV1.ProgramCounterOffset)) == 1536 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prepared.TransferRecord.AsSpan(
                HybridCpuManagedExceptionTransferV1.RegisterOffset(2))) == 4112 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prepared.TransferRecord.AsSpan(
                HybridCpuManagedExceptionTransferV1.RegisterOffset(10))) == fatal,
            "native preparation catches the last try-bundle call even when return PC equals try end");
        nativeCheckpoints = 0;
        prepared = nativeRuntime.PrepareNativeHandlerTransfer(state, fatalToken, nativeFrames,
            new(256, bank), _ => null, (_, _) => { nativeCheckpoints++; return true; });
        Check(prepared.TransferRecord is null && prepared.Dispatch.Status == HybridCpuManagedExceptionStatusV1.InvalidMetadata &&
            nativeCheckpoints == 0 && state.CurrentReference == fatal, "bad native stack must fail before callbacks or transfer publication");
        HybridCpuManagedExceptionProcessResultV1 process = runtime.DispatchForProcess(state, fatalToken, [new("M", 80, 4096, 0)],
            (_, _) => Collect().IsSuccess, kernel, 255);
        Check(process.CanEnterHandler && !process.ProcessTerminated && process.ProcessExit is null,
            "handled exceptions must not terminate the process");
        process = runtime.DispatchForProcess(state, fatalToken, [new("M", 150, 4096, 0)],
            (_, _) => false, kernel, 255);
        Check(process.Dispatch.Status == HybridCpuManagedExceptionStatusV1.GcRejected && process.ProcessExit is null,
            "failed GC checkpoint must not be converted into managed process exit");
        process = runtime.DispatchForProcess(state, fatalToken, [new("M", 150, 4096, 0)],
            (_, _) => Collect().IsSuccess, kernel, 255);
        Check(process.ProcessTerminated && !process.CanEnterHandler && process.ProcessExit!.Reason == "process-exit:255" &&
            state.CurrentReference == fatal && !process.Dispatch.UsedArchitecturalTrap,
            "unhandled exception executes exact kernel process_exit while retaining its root");
        Check(!kernel.ProcessExit(0).IsSuccess, "kernel must no longer have a live process after unhandled exit");
        process = runtime.DispatchForProcess(state, fatalToken, [new("M", 150, 4096, 0)], (_, _) => true, kernel, 255);
        Check(!process.ProcessTerminated && !process.CanEnterHandler && process.ProcessExit is { IsSuccess: false },
            "kernel exit failure is explicit and cannot permit resumption");
        Console.WriteLine("PASS runtime-owned nested exception roots, replacement, leave and real GC retention/reclamation (native transfer pending)");
    }
}
