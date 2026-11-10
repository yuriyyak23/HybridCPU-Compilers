using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;

internal static class EhUnwindSmoke
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new("Exception", HybridCpuManagedTypeKindV1.Class, null, [], [])]);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedExceptionRuntimeV1 Runtime(HybridCpuManagedUnwindRecordV2 record) =>
            new(built.TypeSystem!, [new("M", 0, 1024, EhDispatchSmoke.Encode([]), HybridCpuManagedUnwindCodecV2.Encode(record))]);
        HybridCpuManagedExceptionRuntimeV1 runtime = Runtime(new(HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.StackPointer,
            32, null, -8, [new(1, -8), new(8, -16), new(9, -24)]));
        ulong[] registers = Enumerable.Range(0, 32).Select(i => (ulong)i).ToArray();
        registers[2] = 4096;
        HybridCpuManagedUnwindContextV1 context = new HybridCpuManagedUnwindContextV1(256, registers);
        Dictionary<ulong, ulong> memory = new Dictionary<ulong, ulong> { [4120] = 512, [4112] = 8192, [4104] = 999 };
        ulong? Read(ulong address) => memory.TryGetValue(address, out ulong value) ? value : null;
        Check(runtime.TryUnwindFrame("M", context, Read, out HybridCpuManagedUnwindContextV1? caller) && caller!.ProgramCounter == 512 &&
            caller.Registers[2] == 4128 && caller.Registers[1] == 512 && caller.Registers[8] == 8192 &&
            caller.Registers[9] == 999 && caller.Registers[18] == 18, "restore CFA, return PC and saved registers exactly");
        Check(registers[2] == 4096 && registers[8] == 8 && registers[9] == 9, "unwind cannot mutate the interrupted context");
        memory.Remove(4104);
        Check(!runtime.TryUnwindFrame("M", context, Read, out caller) && caller is null,
            "unreadable saved register rejects the entire transfer before exposing partial state");
        memory[4104] = 999; memory[4120] = 513;
        Check(!runtime.TryUnwindFrame("M", context, Read, out caller) && caller is null, "misaligned native return PC rejected");
        memory[4120] = 512;
        registers[2] = ulong.MaxValue - 15;
        Check(!runtime.TryUnwindFrame("M", context, Read, out _), "CFA overflow rejected");
        registers[2] = 4096;
        HybridCpuManagedExceptionRuntimeV1 framePointer = Runtime(new(HybridCpuManagedFrameKindV1.Managed, HybridCpuManagedCfaBaseV1.FramePointer,
            0, 1, null, []));
        registers[8] = 8192; registers[1] = 768;
        Check(framePointer.TryUnwindFrame("M", context, _ => throw new Exception("leaf frame must not read stack"), out caller) &&
            caller!.ProgramCounter == 768 && caller.Registers[2] == 8192, "FP-based CFA and register return PC");
        Check(!framePointer.TryUnwindFrame("Unknown", context, Read, out _) &&
            !framePointer.TryUnwindFrame("M", context with { ProgramCounter = 1024 }, Read, out _), "exact method range required");
        Check(framePointer.TryUnwindFrame("M", context with { ProgramCounter = 1024 }, Read, out caller,
            instructionPointerIsReturnAddress: true) && caller!.ProgramCounter == 768,
            "return PC at method end still identifies the final call bundle without changing resume PC");
        Console.WriteLine("PASS native-frame unwind state restoration, saved registers, PC alignment, unreadable stack and overflow guards");
    }
}
