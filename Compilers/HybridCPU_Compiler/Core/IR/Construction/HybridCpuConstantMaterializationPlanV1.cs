namespace HybridCPU.Compiler.Core.IR;

public sealed record HybridCpuConstantMaterializationStepV1(HybridCpuOpcode Opcode, ushort Immediate);

/// <summary>Exact register materialization for one native-width bit pattern.</summary>
public static class HybridCpuConstantMaterializationPlanV1
{
    /// <summary>Scoped compiler operations whose constant operands need register materialization; not an ISA legality predicate.</summary>
    public static bool RequiresRegisterConstants(HybridCpuOpcode opcode) =>
        opcode is HybridCpuOpcode.XOR or HybridCpuOpcode.AND or HybridCpuOpcode.OR or
            HybridCpuOpcode.ADD or HybridCpuOpcode.SUB or HybridCpuOpcode.MUL;

    /// <summary>Scoped operand-role classification; shift count remains immediate, shift value requires a register.</summary>
    public static bool RequiresRegisterConstantMaterialization(HybridCpuOpcode opcode, int operandIndex) =>
        RequiresRegisterConstants(opcode) ||
        (operandIndex == 0 && opcode is (HybridCpuOpcode.SLLI or HybridCpuOpcode.SRLI));

    public static IReadOnlyList<HybridCpuConstantMaterializationStepV1> Create(ulong value)
    {
        if (FitsSignedImmediate(value))
            return [new(HybridCpuOpcode.ADDI, unchecked((ushort)(short)(long)value))];

        int first = 7;
        while (first > 0 && (byte)(value >> (8 * first)) == 0) first--;
        List<HybridCpuConstantMaterializationStepV1> result = new List<HybridCpuConstantMaterializationStepV1>
        {
            new(HybridCpuOpcode.ADDI, (byte)(value >> (8 * first)))
        };
        for (int part = first - 1; part >= 0; part--)
        {
            result.Add(new(HybridCpuOpcode.SLLI, 8));
            byte next = (byte)(value >> (8 * part));
            if (next != 0) result.Add(new(HybridCpuOpcode.ORI, next));
        }
        return result;
    }

    public static bool FitsSignedImmediate(ulong value) =>
        unchecked((long)value) is >= short.MinValue and <= short.MaxValue;
}
