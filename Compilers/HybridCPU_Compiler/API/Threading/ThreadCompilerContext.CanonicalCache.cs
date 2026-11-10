using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        private HybridCpuCompiledProgram? _canonicalCompiledProgram;

        private HybridCpuCompiledProgram GetOrCompileCanonicalProgram()
        {
            _canonicalCompiledProgram ??= HybridCpuCanonicalCompiler.CompileProgram(
                _virtualThreadId.Value,
                NativeTransportRuntimeAdapter.ToCore(GetCompiledInstructions()),
                GetLabelDeclarations(),
                GetEntryPointDeclarations(),
                (HybridCPU.Compiler.Core.IR.NativeFrontendMode)(byte)FrontendMode,
                bundleAnnotations: NativeTransportRuntimeAdapter.ToCore(GetBundleAnnotations()),
                domainTag: DomainTag,
                controlFlowTargetReferences: GetControlFlowTargetReferences())
                .WithExternalOperationMetadata(GetExternalOperationLoweringMetadata());
            return _canonicalCompiledProgram;
        }

        private HybridCpuCompiledProgram EmitCanonicalProgram(ulong baseAddress)
        {
            HybridCpuCompiledProgram emittedProgram = NativeTransportRuntimeAdapter.EmitProgram(
                GetOrCompileCanonicalProgram(), baseAddress);
            _canonicalCompiledProgram = emittedProgram;
            return emittedProgram;
        }

        private void InvalidateCanonicalCompileCache()
        {
            _canonicalCompiledProgram = null;
        }
    }
}
