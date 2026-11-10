using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // The native encoder takes register operands plus a signed 16-bit immediate.
    // A Constant operand is not a hidden literal pool or a register. Materialize it
    // before RA so both dependencies and all 64 value bits survive final encoding.
    private static List<Emission> MaterializeV2Constants(IReadOnlyList<Emission> source)
    {
        List<Emission> result = new List<Emission>();
        foreach (Emission emission in source)
        {
            HybridCpuOpcode opcode = emission.Opcode;
            IrOperand[] uses = emission.Uses.ToArray();
            bool immediate = opcode is HybridCpuOpcode.ADDI or HybridCpuOpcode.ANDI or HybridCpuOpcode.ORI or
                HybridCpuOpcode.XORI or HybridCpuOpcode.SLTI or HybridCpuOpcode.SLTIU or HybridCpuOpcode.SLLI or
                HybridCpuOpcode.SRAI or HybridCpuOpcode.SRLI;
            int immediateIndex = immediate ? uses.Length - 1 : -1;
            if (immediateIndex >= 0 && uses[immediateIndex].Kind == IrOperandKind.Constant &&
                !ExactNativeConstantPlanV1.FitsSignedImmediate(uses[immediateIndex].Value))
            {
                opcode = opcode switch
                {
                    HybridCpuOpcode.ADDI => HybridCpuOpcode.ADD, HybridCpuOpcode.ANDI => HybridCpuOpcode.AND,
                    HybridCpuOpcode.ORI => HybridCpuOpcode.OR, HybridCpuOpcode.XORI => HybridCpuOpcode.XOR,
                    HybridCpuOpcode.SLTI => HybridCpuOpcode.SLT, HybridCpuOpcode.SLTIU => HybridCpuOpcode.SLTU,
                    HybridCpuOpcode.SLLI => HybridCpuOpcode.SLL,
                    _ => throw new InvalidOperationException("Unknown immediate expansion.")
                };
                immediateIndex = -1;
            }
            for (int index = 0; index < uses.Length; index++)
            {
                IrOperand operand = uses[index];
                if (operand.Kind != IrOperandKind.Constant || index == immediateIndex) continue;
                if (operand.Value == 0)
                {
                    uses[index] = new(IrOperandKind.ArchitecturalRegister, 0, emission.Identity + $":constant-zero:{index}");
                    continue;
                }
                string identity = emission.Identity + $":constant:{index}";
                IrOperand zero = new IrOperand(IrOperandKind.ArchitecturalRegister, 0, identity + ":zero");
                IReadOnlyList<ExactNativeConstantStepV1> plan = ExactNativeConstantPlanV1.Create(operand.Value);
                V2Value? value = null;
                for (int part = 0; part < plan.Count; part++)
                {
                    ExactNativeConstantStepV1 step = plan[part];
                    string stepIdentity = $"{identity}:part:{part}";
                    V2Value next = V2Value.Definition(RestrictedCilTypeV1.NativeUInt, stepIdentity);
                    IrOperand input = part == 0 ? zero : value!.Operand;
                    result.Add(new(emission.CilOffset, step.Opcode, RestrictedCilTypeV1.NativeUInt,
                        [input, new(IrOperandKind.Constant, step.Immediate, stepIdentity + ":immediate")],
                        [next.Operand], -1, stepIdentity));
                    value = next;
                }
                uses[index] = value!.Operand;
            }
            result.Add(emission with { Opcode = opcode, Uses = uses });
        }
        return result;
    }
}
