using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Llvm;

public enum LlvmAdapterSupport : byte
{
    Supported = 0,
    Unsupported = 1,
    Unknown = 2
}

public sealed record LlvmCApiSurfaceEntryV1(
    string CApiSymbol,
    string LlvmSharpBinding,
    int MinimumLlvmMajor,
    int MaximumLlvmMajor,
    LlvmAdapterSupport Support,
    string FailClosedBehavior);

public sealed record LlvmImporterLimitsV1(
    long MaximumInputBytes,
    int MaximumFunctions,
    int MaximumBasicBlocks,
    int MaximumInstructions,
    int MaximumOperandsPerInstruction,
    int MaximumTypes,
    int MaximumConstants,
    int MaximumMetadataNodes);

/// <summary>
/// Exact optional frontend contract. It describes only the public LLVM C API surface used by
/// this adapter and grants no scheduling, lowering, backend, publication, or runtime authority.
/// </summary>
public sealed class LlvmToolchainContractV1
{
    public const string SchemaId = "hybridcpu.llvm-toolchain-adapter";
    public const int SchemaMajor = 1;
    public const int SchemaMinor = 0;
    public const int LlvmMajor = 20;
    public const int LlvmMinor = 1;
    public const int LlvmPatch = 2;
    public const string LlvmRelease = "20.1.2";
    public const string LlvmSharpPackageVersion = "20.1.2";
    public const string LlvmSharpRepositoryCommit = "5e4623f5a6cb614cf3597004e0c7896510590b8d";
    public const string NativeLibraryIdentity = "libLLVM.runtime.win-x64/20.1.2:libLLVM.dll";
    public const string NativeLibrarySha256 = "1369ddbef3d965397138e892953d3b34053bd70a95d7c03ddbbda77f33199cf1";
    public const string SupportedProducer = "LLVM/clang 20.1.2";
    public const string HybridCpuLlvmDataLayout = "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64";

    private static readonly LlvmCApiSurfaceEntryV1[] Surface =
    [
        Entry("LLVMGetVersion", "LLVM.GetVersion", "version mismatch -> HCLL0003"),
        Entry("LLVMContextCreate", "LLVMContextRef.Create", "unavailable context -> HCLL0002"),
        Entry("LLVMContextDispose", "LLVMContextRef.Dispose", "always dispose owned context"),
        Entry("LLVMCreateMemoryBufferWithContentsOfFile", "LLVM.CreateMemoryBufferWithContentsOfFile", "read failure -> HCLL0006"),
        Entry("LLVMDisposeMemoryBuffer", "LLVM.DisposeMemoryBuffer", "always dispose owned buffer"),
        Entry("LLVMParseIRInContext", "LLVMContextRef.TryParseIR", "malformed IR/bitcode -> HCLL0007"),
        Entry("LLVMDisposeModule", "LLVMModuleRef.Dispose", "always dispose owned module"),
        Entry("LLVMVerifyModule", "LLVMModuleRef.TryVerify", "verification failure -> HCLL0008"),
        Entry("LLVMGetTarget", "LLVMModuleRef.Target", "missing/mismatch -> HCLL0009/HCLL0010"),
        Entry("LLVMGetDataLayoutStr", "LLVMModuleRef.DataLayout", "missing/mismatch -> HCLL0011/HCLL0012"),
        Entry("LLVMGetFirstNamedMetadata", "LLVM.GetFirstNamedMetadata", "unknown metadata -> HCLL0018"),
        Entry("LLVMGetNextNamedMetadata", "LLVM.GetNextNamedMetadata", "bounded deterministic traversal"),
        Entry("LLVMGetNamedMetadataName", "LLVM.GetNamedMetadataName", "invalid metadata name -> HCLL0018"),
        Entry("LLVMGetNamedMetadataNumOperands", "LLVMModuleRef.GetNamedMetadataOperandsCount", "metadata budget -> HCLL0019"),
        Entry("LLVMGetNamedMetadataOperands", "LLVMModuleRef.GetNamedMetadataOperands", "producer identity validation"),
        Entry("LLVMGetMDNodeNumOperands", "LLVMValueRef.MDNodeOperandsCount", "producer identity validation"),
        Entry("LLVMGetMDNodeOperands", "LLVMValueRef.GetMDNodeOperands", "producer identity validation"),
        Entry("LLVMGetMDString", "LLVMValueRef.GetMDString", "producer identity validation"),
        Entry("LLVMGetFirstFunction", "LLVMModuleRef.FirstFunction", "bounded deterministic traversal"),
        Entry("LLVMGetNextFunction", "LLVMValueRef.NextFunction", "bounded deterministic traversal"),
        Entry("LLVMGetFirstGlobal", "LLVMModuleRef.FirstGlobal", "globals -> HCLL0022"),
        Entry("LLVMGlobalGetValueType", "LLVM.GlobalGetValueType", "function signature validation"),
        Entry("LLVMGetReturnType", "LLVMTypeRef.ReturnType", "non-void return -> HCLL0023"),
        Entry("LLVMGetTypeKind", "LLVMTypeRef.Kind", "non-void return -> HCLL0023"),
        Entry("LLVMCountParams", "LLVMValueRef.ParamsCount", "function parameters -> HCLL0023"),
        Entry("LLVMGetFunctionCallConv", "LLVMValueRef.FunctionCallConv", "non-C calling convention -> HCLL0023"),
        Entry("LLVMIsDeclaration", "LLVMValueRef.IsDeclaration", "non-intrinsic declaration -> HCLL0023"),
        Entry("LLVMGetAttributeCountAtIndex", "LLVMValueRef.GetAttributeCountAtIndex", "function attributes -> HCLL0023"),
        Entry("LLVMCountBasicBlocks", "LLVMValueRef.BasicBlocksCount", "budget overflow -> HCLL0013"),
        Entry("LLVMGetFirstBasicBlock", "LLVMValueRef.FirstBasicBlock", "bounded deterministic traversal"),
        Entry("LLVMGetNextBasicBlock", "LLVMBasicBlockRef.Next", "bounded deterministic traversal"),
        Entry("LLVMGetFirstInstruction", "LLVMBasicBlockRef.FirstInstruction", "bounded deterministic traversal"),
        Entry("LLVMGetNextInstruction", "LLVMValueRef.NextInstruction", "bounded deterministic traversal"),
        Entry("LLVMGetInstructionOpcode", "LLVMValueRef.InstructionOpcode", "unsupported opcode -> HCLL0014"),
        Entry("LLVMGetNumOperands", "LLVMValueRef.OperandCount", "budget overflow -> HCLL0015"),
        Entry("LLVMGetOperand", "LLVMValueRef.GetOperand", "bounded type/constant validation"),
        Entry("LLVMTypeOf", "LLVMValueRef.TypeOf", "bounded type validation"),
        Entry("LLVMGetIntTypeWidth", "LLVMTypeRef.IntWidth", "integer type validation"),
        Entry("LLVMGetPointerAddressSpace", "LLVMTypeRef.PointerAddressSpace", "address-space validation"),
        Entry("LLVMIsConstant", "LLVMValueRef.IsConstant", "constant budget"),
        Entry("LLVMIsAConstantInt", "LLVMValueRef.IsAConstantInt", "integer constant semantic snapshot"),
        Entry("LLVMConstIntGetSExtValue", "LLVMValueRef.ConstIntSExt", "integer constant semantic snapshot"),
        Entry("LLVMGetValueName2", "LLVMValueRef.Name", "stable SSA value identity"),
        Entry("LLVMGetNSW", "LLVM.GetNSW", "signed-wrap poison semantics"),
        Entry("LLVMGetNUW", "LLVM.GetNUW", "unsigned-wrap poison semantics"),
        Entry("LLVMGetIsDisjoint", "LLVM.GetIsDisjoint", "OR disjoint flag observed; unavailable flag contract rejects"),
        Entry("LLVMGetParamTypes", "LLVMTypeRef.GetParamTypes", "function type validation"),
        Entry("LLVMGetCalledValue", "LLVM.GetCalledValue", "call target classification"),
        Entry("LLVMGetIntrinsicID", "LLVMValueRef.IntrinsicID", "unknown intrinsic -> HCLL0016"),
        Entry("LLVMCreatePassBuilderOptions", "LLVMPassBuilderOptionsRef.Create", "Phase 11 deterministic pass options"),
        Entry("LLVMDisposePassBuilderOptions", "LLVMPassBuilderOptionsRef.Dispose", "always dispose pass options"),
        Entry("LLVMPassBuilderOptionsSetVerifyEach", "LLVMPassBuilderOptionsRef.SetVerifyEach", "verify after every selected pass"),
        Entry("LLVMPassBuilderOptionsSetDebugLogging", "LLVMPassBuilderOptionsRef.SetDebugLogging", "debug logging disabled"),
        Entry("LLVMPassBuilderOptionsSetLoopInterleaving", "LLVMPassBuilderOptionsRef.SetLoopInterleaving", "interleaving disabled"),
        Entry("LLVMPassBuilderOptionsSetLoopUnrolling", "LLVMPassBuilderOptionsRef.SetLoopUnrolling", "unrolling disabled"),
        Entry("LLVMPassBuilderOptionsSetLoopVectorization", "LLVMPassBuilderOptionsRef.SetLoopVectorization", "loop vectorization disabled"),
        Entry("LLVMPassBuilderOptionsSetSLPVectorization", "LLVMPassBuilderOptionsRef.SetSLPVectorization", "SLP vectorization disabled"),
        Entry("LLVMPassBuilderOptionsSetMergeFunctions", "LLVMPassBuilderOptionsRef.SetMergeFunctions", "function merging disabled"),
        Entry("LLVMPassBuilderOptionsSetCallGraphProfile", "LLVMPassBuilderOptionsRef.SetCallGraphProfile", "call-graph profiling disabled"),
        Entry("LLVMRunPasses", "LLVM.RunPasses", "pass failure rejects before Canonical IR"),
        Entry("LLVMSetModuleIdentifier", "LLVM.SetModuleIdentifier", "stable optimized bitcode identity"),
        Entry("LLVMSetSourceFileName", "LLVM.SetSourceFileName", "stable optimized source identity"),
        Entry("LLVMWriteBitcodeToFile", "LLVMModuleRef.WriteBitcodeToFile", "deterministic post-pass remap artifact"),
        Entry("LLVMGetErrorMessage", "LLVM.GetErrorMessage", "stable pass failure diagnostic"),
        Entry("LLVMDisposeErrorMessage", "LLVM.DisposeErrorMessage", "always dispose pass diagnostic"),
        Entry("LLVMDisposeMessage", "LLVM.DisposeMessage", "always dispose owned diagnostic")
    ];

    public static LlvmToolchainContractV1 Default { get; } = new();

    private LlvmToolchainContractV1()
    {
        ApiSurface = Array.AsReadOnly(Surface);
        Limits = new(16 * 1024 * 1024, 1024, 8192, 100_000, 256, 4096, 100_000, 16_384);
        CoreTargetContractDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        PlatformContractDigest = HybridCpuTargetPlatformContractV1.Default.ContractDigest;
        OptionsDigest = ComputeOptionsDigest();
        ContractDigest = ComputeDigest();
    }

    public IReadOnlyList<LlvmCApiSurfaceEntryV1> ApiSurface { get; }
    public LlvmImporterLimitsV1 Limits { get; }
    public string CoreTargetContractDigest { get; }
    public string PlatformContractDigest { get; }
    public string OptionsDigest { get; }
    public string ContractDigest { get; }
    public bool HasBackendAuthority => false;
    public bool UsesWallClockBudget => false;

    public LlvmAdapterSupport ValidateProducerVersion(int major, int minor, int patch)
    {
        if (major <= 0 || minor < 0 || patch < 0) return LlvmAdapterSupport.Unknown;
        return major == LlvmMajor && minor == LlvmMinor && patch == LlvmPatch
            ? LlvmAdapterSupport.Supported
            : LlvmAdapterSupport.Unsupported;
    }

    private static LlvmCApiSurfaceEntryV1 Entry(string symbol, string binding, string fallback) =>
        new(symbol, binding, LlvmMajor, LlvmMajor, LlvmAdapterSupport.Supported, fallback);

    private string ComputeDigest()
    {
        StringBuilder text = new StringBuilder();
        text.Append(SchemaId).Append('|').Append(SchemaMajor).Append('.').Append(SchemaMinor)
            .Append('|').Append(LlvmRelease).Append('|').Append(LlvmSharpPackageVersion)
            .Append('|').Append(LlvmSharpRepositoryCommit).Append('|').Append(NativeLibraryIdentity)
            .Append('|').Append(NativeLibrarySha256).Append('|').Append(OptionsDigest)
            .Append('|').Append(SupportedProducer).Append('|').Append(HybridCpuLlvmDataLayout)
            .Append('|').Append(CoreTargetContractDigest)
            .Append('|').Append(PlatformContractDigest).Append('|')
            .Append(Limits.MaximumInputBytes).Append(':').Append(Limits.MaximumFunctions).Append(':')
            .Append(Limits.MaximumBasicBlocks).Append(':').Append(Limits.MaximumInstructions).Append(':')
            .Append(Limits.MaximumOperandsPerInstruction).Append(':').Append(Limits.MaximumTypes).Append(':')
            .Append(Limits.MaximumConstants).Append(':').Append(Limits.MaximumMetadataNodes);
        foreach (LlvmCApiSurfaceEntryV1 entry in Surface.OrderBy(static item => item.CApiSymbol, StringComparer.Ordinal))
            text.Append('|').Append(entry.CApiSymbol).Append(':').Append(entry.LlvmSharpBinding).Append(':')
                .Append(entry.MinimumLlvmMajor).Append(':').Append(entry.MaximumLlvmMajor).Append(':')
                .Append(entry.Support).Append(':').Append(entry.FailClosedBehavior);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private string ComputeOptionsDigest()
    {
        string text = string.Join('|',
            "hybridcpu.llvm-import-options/v1",
            "default=phase09-structural-control-only",
            "phase10-explicit=ret-add-sub-mul-single-bb",
            "phase11-explicit=function(instcombine,dce);verify-each=true;vectorization=false;unrolling=false;profile=absent",
            "metadata=llvm.ident-only",
            "host-defaults=false",
            $"limits={Limits.MaximumInputBytes}:{Limits.MaximumFunctions}:{Limits.MaximumBasicBlocks}:{Limits.MaximumInstructions}:{Limits.MaximumOperandsPerInstruction}:{Limits.MaximumTypes}:{Limits.MaximumConstants}:{Limits.MaximumMetadataNodes}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
