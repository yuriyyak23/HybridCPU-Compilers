namespace HybridCPU.Compiler.Core.IR.Intent;

public enum SemanticIntentKind
{
    Unknown = 0,
    ScalarAlu,
    LoadStore,
    BranchControl,
    VectorStream,
    MatrixTile,
    DmaStreamCompute,
    ExternalAcceleratorCommand,
    VmxCompatibilityProjection,
    SecureComputeAdmission,
    RuntimeAssist,
    NonExecutable
}

/// <summary>
/// Compiler-side semantic intent. This describes operation meaning only and
/// does not select a lowering provider or emit compiler artifacts.
/// </summary>
public sealed record CompilerSemanticIntent(
    SemanticIntentKind Kind,
    string OpcodeFamily,
    bool RequiresDescriptor,
    bool RequiresSideband,
    bool RequiresToken,
    bool RequiresRuntimeLegality,
    bool IsCompatibilityProjection,
    bool IsPolicyAdmissionOnly,
    bool IsHelperAbiOnly,
    bool IsParserOnly,
    string Reason)
{
    public static CompilerSemanticIntent Unknown(string reason) =>
        new(
            SemanticIntentKind.Unknown,
            "Unknown",
            RequiresDescriptor: false,
            RequiresSideband: false,
            RequiresToken: false,
            RequiresRuntimeLegality: false,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: true,
            reason);
}

public interface ICompilerIntentClassifier
{
    CompilerSemanticIntent ClassifyIntent(IrInstruction instruction);
}
