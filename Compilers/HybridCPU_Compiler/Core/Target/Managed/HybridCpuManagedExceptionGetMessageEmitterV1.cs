using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Exact native field getter for the qualified System.Exception::_message slot.</summary>
public static class HybridCpuManagedExceptionGetMessageEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_exception_get_message";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.exception-get-message/v1";

    public static byte[] Emit(int messageOffsetBytes)
    {
        if (messageOffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            messageOffsetBytes > short.MaxValue || messageOffsetBytes % sizeof(ulong) != 0)
            throw new ArgumentOutOfRangeException(nameof(messageOffsetBytes),
                "Exception message offset must be an aligned positive signed16 object-field displacement.");
        HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1, short immediate = 0) => new()
        {
            OpCode = (uint)opcode,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, HybridCpuInstructionWord.NoArchReg),
            Immediate = unchecked((ushort)immediate)
        };
        HybridCpuInstructionWord[] words =
        [
            Word(HybridCpuOpcode.LD, 10, 10, checked((short)messageOffsetBytes)),
            Word(HybridCpuOpcode.JALR, 0, 1, HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes)
        ];
        return new HybridCpuBundleSerializer().SerializeProgram(words.Select(word =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, word);
            return bundle;
        }).ToArray());
    }

    public static HybridCpuObjectArtifactV1 EmitObject(int messageOffsetBytes)
    {
        byte[] code = Emit(messageOffsetBytes);
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".text", 0, (ulong)code.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
