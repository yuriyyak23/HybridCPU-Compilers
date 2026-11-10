using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerManagedRuntimeHelperAbiTests
{
    [Fact]
    public void HcsvHelpersThatUseCalleeSavedArgumentRegisters_EmitBalancedNativeAbiFrames()
    {
        AssertPreservesHcsvArgumentRegisters(
            HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(), [18, 19]);
        AssertPreservesHcsvArgumentRegisters(
            HybridCpuManagedStaticLoadInt32EmitterV1.EmitObject(), [18]);
        AssertPreservesHcsvArgumentRegisters(
            HybridCpuManagedStaticLoadReferenceEmitterV1.EmitObject(), [18]);
        AssertPreservesHcsvArgumentRegisters(
            HybridCpuManagedStaticStoreInt32EmitterV1.EmitObject(), [18, 19]);
        AssertPreservesHcsvArgumentRegisters(
            HybridCpuManagedStaticStoreReferenceEmitterV1.EmitObject(), [18, 19]);
    }

    private static void AssertPreservesHcsvArgumentRegisters(
        HybridCpuObjectArtifactV1 artifact,
        int[] registers)
    {
        Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
        byte[] code = Assert.Single(artifact.Sections, static section => section.Name == ".text").Data;
        HybridCpuInstructionWord[] words = Enumerable.Range(0, code.Length / HybridCpuBundleSerializer.BundleSizeBytes)
            .Select(index =>
            {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Assert.True(bundle.TryReadBytes(code, index * HybridCpuBundleSerializer.BundleSizeBytes));
                return bundle.GetInstruction(0);
            })
            .ToArray();

        AssertInstruction(words[0], HybridCpuOpcode.ADDI, 2, 2, HybridCpuInstructionWord.NoArchReg, -16);
        for (int index = 0; index < registers.Length; index++)
            AssertInstruction(words[index + 1], HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg,
                2, (byte)registers[index], checked((short)(index * 8)));

        int ecall = Array.FindIndex(words, static word => word.OpCode == (uint)HybridCpuOpcode.ECALL);
        Assert.True(ecall >= 0);
        for (int index = 0; index < registers.Length; index++)
            AssertInstruction(words[ecall + index + 1], HybridCpuOpcode.LD, (byte)registers[index],
                2, HybridCpuInstructionWord.NoArchReg, checked((short)(index * 8)));
        AssertInstruction(words[ecall + registers.Length + 1], HybridCpuOpcode.ADDI, 2, 2,
            HybridCpuInstructionWord.NoArchReg, 16);
    }

    private static void AssertInstruction(HybridCpuInstructionWord word, HybridCpuOpcode opcode,
        byte rd, byte rs1, byte rs2, short immediate)
    {
        Assert.Equal((uint)opcode, word.OpCode);
        Assert.True(HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte actualRd,
            out byte actualRs1, out byte actualRs2));
        Assert.Equal(rd, actualRd);
        Assert.Equal(rs1, actualRs1);
        Assert.Equal(rs2, actualRs2);
        Assert.Equal(unchecked((ushort)immediate), word.Immediate);
    }
}
