using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        /// <summary>
        /// Builds the normalized IR program for the current VT-local instruction buffer.
        /// </summary>
        public IrProgram BuildIrProgram()
        {
            HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
            return builder.BuildProgram(
                _virtualThreadId.Value,
                NativeTransportRuntimeAdapter.ToCore(GetCompiledInstructions()),
                GetLabelDeclarations(),
                GetEntryPointDeclarations(),
                bundleAnnotations: NativeTransportRuntimeAdapter.ToCore(GetBundleAnnotations()),
                domainTag: DomainTag,
                controlFlowTargetReferences: GetControlFlowTargetReferences());
        }
    }
}
