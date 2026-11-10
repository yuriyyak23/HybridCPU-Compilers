namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Narrows register-dependency analysis to explicit decoded architectural-register operands
    /// without reopening the wider IR operand-kind redesign.
    /// </summary>
    internal static class HybridCpuDependencyOperandClassifier
    {
        public static bool IsRegisterOperand(IrOperand operand)
        {
            return operand.Value != 0 &&
                   (operand.Kind == IrOperandKind.ArchitecturalRegister ||
                    (operand.Kind == IrOperandKind.Pointer &&
                     operand.Name is "rd" or "rs1" or "rs2" or "ctrl0" or "ctrl1" or "ctrl2"));
        }
    }
}
