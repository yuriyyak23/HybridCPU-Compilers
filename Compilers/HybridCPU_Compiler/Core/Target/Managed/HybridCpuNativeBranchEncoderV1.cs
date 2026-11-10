using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Deterministically resolves local native-emitter labels and relaxes an out-of-range
/// JAL or conditional branch into the existing AUIPC/JALR long-transfer form.</summary>
public static class HybridCpuNativeBranchEncoderV1
{
    public static IReadOnlyList<HybridCpuInstructionWord> Resolve(
        IReadOnlyList<HybridCpuInstructionWord> source,
        IReadOnlyDictionary<string, int> labels,
        IReadOnlyList<(int Index, string Label)> branches,
        string owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(branches);
        if (string.IsNullOrWhiteSpace(owner) || branches.Select(static branch => branch.Index).Distinct().Count() != branches.Count ||
            branches.Any(branch => branch.Index < 0 || branch.Index >= source.Count || !labels.ContainsKey(branch.Label)))
            throw new ArgumentException("Local native branch graph is malformed.");
        Dictionary<int, (int Index, string Label)> byIndex = branches.ToDictionary(static branch => branch.Index);
        HashSet<int> expanded = new HashSet<int>();
        int[] positions = new int[source.Count + 1];
        bool changed;
        do
        {
            changed = false;
            int position = 0;
            for (int index = 0; index < source.Count; index++)
            {
                positions[index] = position;
                position += expanded.Contains(index) ? ExpansionLength(source[index]) : 1;
            }
            positions[source.Count] = position;
            foreach ((int index, string label) in branches)
            {
                int displacement = checked((positions[labels[label]] - positions[index]) * HybridCpuBundleSerializer.BundleSizeBytes);
                if (displacement is >= short.MinValue and <= short.MaxValue && displacement != 0 || expanded.Contains(index))
                    continue;
                ValidateBranch(source[index], owner);
                expanded.Add(index);
                changed = true;
            }
        } while (changed);

        List<HybridCpuInstructionWord> result = new List<HybridCpuInstructionWord>(positions[source.Count]);
        for (int index = 0; index < source.Count; index++)
        {
            HybridCpuInstructionWord instruction = source[index];
            if (!byIndex.TryGetValue(index, out (int Index, string Label) branch))
            {
                result.Add(instruction);
                continue;
            }
            int target = positions[labels[branch.Label]];
            int current = positions[index];
            if (!expanded.Contains(index))
            {
                int displacement = checked((target - current) * HybridCpuBundleSerializer.BundleSizeBytes);
                instruction.Immediate = unchecked((ushort)checked((short)displacement));
                result.Add(instruction);
                continue;
            }
            HybridCpuOpcode opcode = (HybridCpuOpcode)instruction.OpCode;
            if (opcode == HybridCpuOpcode.JAL)
            {
                EmitLong(result, target, current, linkRegister: 0);
                continue;
            }
            if (!HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out _, out byte left, out byte right))
                throw new InvalidOperationException($"{owner} branch operands are malformed.");
            result.Add(Word(Invert(opcode), HybridCpuInstructionWord.NoArchReg, left, right,
                checked((short)(3 * HybridCpuBundleSerializer.BundleSizeBytes))));
            EmitLong(result, target, current + 1, linkRegister: 0);
        }
        return result.AsReadOnly();

        static int ExpansionLength(HybridCpuInstructionWord instruction) =>
            (HybridCpuOpcode)instruction.OpCode == HybridCpuOpcode.JAL ? 2 : 3;
    }

    private static void EmitLong(List<HybridCpuInstructionWord> result, int targetPosition,
        int auipcPosition, byte linkRegister)
    {
        long displacement = checked((long)(targetPosition - auipcPosition) * HybridCpuBundleSerializer.BundleSizeBytes);
        long high = (displacement + 0x800L) >> 12;
        long low = displacement - (high << 12);
        if (high is < short.MinValue or > short.MaxValue || low is < short.MinValue or > short.MaxValue)
            throw new InvalidOperationException("Local native long branch exceeds AUIPC/JALR reach.");
        result.Add(Word(HybridCpuOpcode.AUIPC, 5, HybridCpuInstructionWord.NoArchReg,
            HybridCpuInstructionWord.NoArchReg, checked((short)high)));
        result.Add(Word(HybridCpuOpcode.JALR, linkRegister, 5,
            HybridCpuInstructionWord.NoArchReg, checked((short)low)));
    }

    private static void ValidateBranch(HybridCpuInstructionWord instruction, string owner)
    {
        HybridCpuOpcode opcode = (HybridCpuOpcode)instruction.OpCode;
        if (opcode is not (HybridCpuOpcode.JAL or HybridCpuOpcode.BEQ or HybridCpuOpcode.BNE or
            HybridCpuOpcode.BLT or HybridCpuOpcode.BGE or HybridCpuOpcode.BLTU or HybridCpuOpcode.BGEU))
            throw new InvalidOperationException($"{owner} has a non-relaxable local branch opcode '{opcode}'.");
    }

    private static HybridCpuOpcode Invert(HybridCpuOpcode opcode) => opcode switch
    {
        HybridCpuOpcode.BEQ => HybridCpuOpcode.BNE,
        HybridCpuOpcode.BNE => HybridCpuOpcode.BEQ,
        HybridCpuOpcode.BLT => HybridCpuOpcode.BGE,
        HybridCpuOpcode.BGE => HybridCpuOpcode.BLT,
        HybridCpuOpcode.BLTU => HybridCpuOpcode.BGEU,
        HybridCpuOpcode.BGEU => HybridCpuOpcode.BLTU,
        _ => throw new InvalidOperationException($"Opcode '{opcode}' has no conditional inverse.")
    };

    private static HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1,
        byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => new()
    {
        OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
    };
}
