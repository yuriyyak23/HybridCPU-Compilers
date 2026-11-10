using HybridCPU.Compiler.Core.IR;
using YAKSys_Hybrid_CPU;

namespace HybridCPU.Compiler.Core.Runtime;

public static partial class NativeTransportRuntimeAdapter
{
    public static HybridCpuCompiledProgram EmitProgram(HybridCpuCompiledProgram program, ulong baseAddress)
    {
        ArgumentNullException.ThrowIfNull(program);
        program.ValidateRuntimeContractCompatibility(
            $"{nameof(NativeTransportRuntimeAdapter)}.{nameof(EmitProgram)}");
        if ((baseAddress % 256) != 0) throw new ArgumentException("Emission base address must be 256-byte aligned.", nameof(baseAddress));
        Processor.MainMemory.WriteToPosition(program.ProgramImage, baseAddress);
        for (int bundleIndex = 0; bundleIndex < program.LoweredBundleAnnotations.Count; bundleIndex++)
        {
            IrBundleAnnotations annotations = program.LoweredBundleAnnotations[bundleIndex];
            if (annotations.Count != 0)
            {
                Processor.MainMemory.PublishVliwBundleAnnotations(
                    baseAddress + (ulong)bundleIndex * 256,
                    ToRuntime(annotations));
            }
        }
        for (int bundleIndex = 0; bundleIndex < program.BundleCount; bundleIndex++)
        {
            ulong address = baseAddress + (ulong)bundleIndex * 256;
            for (int coreIndex = 0; coreIndex < Processor.CPU_Cores.Length; coreIndex++)
                Processor.CPU_Cores[coreIndex].InvalidateVliwFetchState(address);
        }
        return program.WithEmissionBaseAddress(baseAddress);
    }
}
