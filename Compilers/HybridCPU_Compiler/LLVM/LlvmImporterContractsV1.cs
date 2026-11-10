namespace HybridCPU.Compiler.Llvm;

public enum LlvmImportStatus : byte
{
    Success = 0,
    Unavailable = 1,
    InvalidInput = 2,
    Unsupported = 3,
    BudgetExhausted = 4,
    UnknownSemantics = 5
}

public enum LlvmInputKind : byte
{
    TextIr = 0,
    Bitcode = 1
}

public sealed record LlvmImporterDiagnosticV1(
    LlvmImportStatus Status,
    string Code,
    string Message,
    string? StableSourceIdentity = null);

public sealed record LlvmModuleSummaryV1(
    string SchemaId,
    string InputDigest,
    string ModuleProducer,
    string TargetTriple,
    string DataLayout,
    int FunctionCount,
    int BasicBlockCount,
    int InstructionCount,
    LlvmImportProvenanceV1 Provenance,
    LlvmSemanticModuleSnapshotV1? SemanticSnapshot = null);

public enum LlvmSnapshotOperandKindV1 : byte
{
    Value = 0,
    ConstantInteger = 1,
    BasicBlock = 2,
    Unknown = 255
}

public sealed record LlvmSemanticOperandSnapshotV1(
    LlvmSnapshotOperandKindV1 Kind,
    string StableIdentity,
    int BitWidth,
    long? SignedIntegerValue);

public sealed record LlvmSemanticInstructionSnapshotV1(
    int Index,
    string StableIdentity,
    string Opcode,
    string ResultName,
    string TypeKind,
    int TypeBitWidth,
    bool NoSignedWrap,
    bool NoUnsignedWrap,
    bool IsVolatile,
    uint AlignmentBytes,
    string AtomicOrdering,
    IReadOnlyList<LlvmSemanticOperandSnapshotV1> Operands)
{
    /// <summary>Observed only for OR; null means the flag contract is unavailable, never plain OR.</summary>
    public bool? IsDisjoint { get; init; }
}

public sealed record LlvmSemanticBasicBlockSnapshotV1(
    int Id,
    string StableIdentity,
    IReadOnlyList<LlvmSemanticInstructionSnapshotV1> Instructions);

public sealed record LlvmSemanticFunctionSnapshotV1(
    string Name,
    IReadOnlyList<LlvmSemanticBasicBlockSnapshotV1> Blocks);

public sealed record LlvmSemanticModuleSnapshotV1(
    string SchemaId,
    IReadOnlyList<LlvmSemanticFunctionSnapshotV1> Functions);

public sealed record LlvmImportProvenanceV1(
    string SchemaId,
    string LlvmRelease,
    string LlvmCApiVersion,
    string LlvmSharpPackageVersion,
    string LlvmSharpRepositoryCommit,
    string NativeLibraryIdentity,
    string NativeLibrarySha256,
    string ModuleProducer,
    string InputDigest,
    string OptionsDigest,
    string ToolchainContractDigest,
    string CoreTargetContractDigest,
    string PlatformContractDigest);

public sealed record LlvmImportResultV1(
    LlvmImportStatus Status,
    LlvmModuleSummaryV1? Module,
    IReadOnlyList<LlvmImporterDiagnosticV1> Diagnostics);

public sealed record LlvmRuntimeAvailabilityV1(
    bool IsAvailable,
    int? Major,
    int? Minor,
    int? Patch,
    string? NativeLibrarySha256,
    string DiagnosticCode,
    string Detail);

public sealed record LlvmModuleHeaderV1(string TargetTriple, string DataLayout, string ModuleProducer);
