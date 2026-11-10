namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Temporary compiler-side guard for register dependency cases that current runtime
    /// memory paths still observe conservatively across VT boundaries.
    /// </summary>
    internal static class HybridCpuRegisterDependencyGuard
    {
        public static bool ShouldPreserveCrossVirtualThreadRegisterDependencies(IrInstruction producer, IrInstruction consumer)
        {
            return producer.VirtualThreadId != consumer.VirtualThreadId &&
                   (RequiresCrossVirtualThreadRegisterGuard(producer) ||
                    RequiresCrossVirtualThreadRegisterGuard(consumer));
        }

        public static HybridCpuRegisterDependencyKey GetVirtualThreadLocalKey(IrInstruction instruction, IrOperand operand)
        {
            return new HybridCpuRegisterDependencyKey(instruction.VirtualThreadId, operand.Kind, operand.Value);
        }

        public static bool TryGetCrossVirtualThreadGuardKey(IrInstruction instruction, IrOperand operand, out HybridCpuRegisterDependencyKey key)
        {
            if (RequiresCrossVirtualThreadRegisterGuard(instruction))
            {
                key = new HybridCpuRegisterDependencyKey(byte.MaxValue, operand.Kind, operand.Value);
                return true;
            }

            key = default;
            return false;
        }

        private static bool RequiresCrossVirtualThreadRegisterGuard(IrInstruction instruction)
        {
            return instruction.Annotation.ResourceClass == IrResourceClass.LoadStore;
        }
    }

    internal readonly record struct HybridCpuRegisterDependencyKey(byte VirtualThreadId, IrOperandKind Kind, ulong Value);
}
