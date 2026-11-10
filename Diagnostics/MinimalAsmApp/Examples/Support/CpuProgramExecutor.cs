using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace MinimalAsmApp.Examples.Support;

public static class CpuProgramExecutor
{
    private const int CoreId = 0;
    private const byte VirtualThreadId = 0;

    public static CpuProgramExecution Run(
        IReadOnlyList<VLIW_Instruction> program,
        CpuProgramRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Count == 0)
        {
            throw new ArgumentException("Program must contain at least one instruction.", nameof(program));
        }

        options ??= new CpuProgramRunOptions();
        BootstrapRuntime();
        WriteInitialMemory(options.InitialMemory);

        VLIW_Instruction[] programArray = program.ToArray();
        HybridCpuCompiledProgram compiledProgram = NativeTransportRuntimeAdapter.CompileProgram(
            VirtualThreadId,
            programArray,
            bundleAnnotations: CreateInputAnnotations(programArray.Length),
            frontendMode: FrontendMode.NativeVLIW);
        compiledProgram = NativeTransportRuntimeAdapter.EmitProgram(
            compiledProgram,
            options.EmissionBaseAddress);

        Processor.CurrentProcessorMode = ProcessorMode.Emulation;
        Processor.CPU_Cores[CoreId].PrepareExecutionStart(options.EmissionBaseAddress);

        ulong target = options.RetirementTarget ?? (ulong)programArray.Length;
        List<string> trace = new List<string>();
        Processor.CPU_Core.PipelineControl control = RunUntilRetired(target, options, trace);
        DrainPipeline(options.DrainCycles);
        control = Processor.CPU_Cores[CoreId].GetPipelineControl();

        return new CpuProgramExecution(
            compiledProgram.BundleCount,
            control.InstructionsRetired,
            control.CycleCount,
            ReadRegisters(options.RegisterDump),
            ReadMemory(options.MemoryDump),
            trace);
    }

    private static void BootstrapRuntime()
    {
#pragma warning disable CS0618
        _ = new Processor(ProcessorMode.Compiler);
#pragma warning restore CS0618
        Processor.ResetPerformanceCounters();
    }

    private static Processor.CPU_Core.PipelineControl RunUntilRetired(
        ulong retirementTarget,
        CpuProgramRunOptions options,
        List<string> trace)
    {
        while (true)
        {
            Processor.CPU_Core.PipelineControl control = Processor.CPU_Cores[CoreId].GetPipelineControl();
            if (control.InstructionsRetired >= retirementTarget)
            {
                return control;
            }

            if (control.CycleCount >= options.HardCycleLimit)
            {
                throw new InvalidOperationException(
                    $"Pipeline did not retire the program within {options.HardCycleLimit} cycles. " +
                    $"Retired={control.InstructionsRetired}, target={retirementTarget}, " +
                    $"activePC=0x{Processor.CPU_Cores[CoreId].ReadActiveLivePc():X}.");
            }

            CaptureTraceLine(options, trace, control);
            Processor.CPU_Cores[CoreId].ExecutePipelineCycle();
        }
    }

    private static void DrainPipeline(int cycles)
    {
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            Processor.CPU_Cores[CoreId].ExecutePipelineCycle();
        }
    }

    private static void CaptureTraceLine(
        CpuProgramRunOptions options,
        List<string> trace,
        Processor.CPU_Core.PipelineControl control)
    {
        if (!options.CaptureTrace || trace.Count >= options.MaxTraceLines)
        {
            return;
        }

        string registers = options.TraceRegisters.Count == 0
            ? string.Empty
            : " " + string.Join(
                ", ",
                options.TraceRegisters.Select(reg =>
                    $"{CpuProgramExecution.FormatRegister(reg)}={Processor.CPU_Cores[CoreId].ReadArch(VirtualThreadId, reg)}"));

        trace.Add(
            $"cycle={control.CycleCount}, retired={control.InstructionsRetired}, " +
            $"pc=0x{Processor.CPU_Cores[CoreId].ReadActiveLivePc():X}{registers}");
    }

    private static Dictionary<string, ulong> ReadRegisters(IReadOnlyList<int> registerIds)
    {
        Dictionary<string, ulong> registers = new Dictionary<string, ulong>(registerIds.Count);
        foreach (int registerId in registerIds)
        {
            registers[CpuProgramExecution.FormatRegister(registerId)] =
                Processor.CPU_Cores[CoreId].ReadArch(VirtualThreadId, registerId);
        }

        return registers;
    }

    private static Dictionary<string, ulong> ReadMemory(IReadOnlyList<ulong> addresses)
    {
        Dictionary<string, ulong> memory = new Dictionary<string, ulong>(addresses.Count);
        foreach (ulong address in addresses)
        {
            memory[CpuProgramExecution.FormatMemory(address)] = ReadUInt64(address);
        }

        return memory;
    }

    private static void WriteInitialMemory(IReadOnlyDictionary<ulong, ulong> values)
    {
        foreach (KeyValuePair<ulong, ulong> item in values)
        {
            WriteUInt64(item.Key, item.Value);
        }
    }

    private static ulong ReadUInt64(ulong address)
    {
        byte[] bytes = MainMemory.ReadFromPosition(new byte[sizeof(ulong)], address, sizeof(ulong));
        return BitConverter.ToUInt64(bytes, 0);
    }

    private static void WriteUInt64(ulong address, ulong value)
    {
        MainMemory.WriteToPosition(BitConverter.GetBytes(value), address);
    }

    private static Processor.MainMemoryArea MainMemory =>
        Processor.MainMemory ?? throw new InvalidOperationException("Processor main memory is not initialized.");

    private static VliwBundleAnnotations CreateInputAnnotations(int instructionCount)
    {
        InstructionSlotMetadata[] slotMetadata = new InstructionSlotMetadata[instructionCount];
        for (int instructionIndex = 0; instructionIndex < slotMetadata.Length; instructionIndex++)
        {
            slotMetadata[instructionIndex] = new InstructionSlotMetadata(
                VtId.Create(VirtualThreadId),
                SlotMetadata.NotStealable);
        }

        return new VliwBundleAnnotations(slotMetadata);
    }

}
