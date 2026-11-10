using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using LLVMSharp.Interop;

namespace HybridCPU.Compiler.Llvm;

public sealed record LlvmOptimizationOptionsV1(
    bool Enabled,
    string Recipe,
    string? ProfileSha256)
{
    public static LlvmOptimizationOptionsV1 Disabled { get; } = new(false, string.Empty, null);
    public static LlvmOptimizationOptionsV1 Production { get; } =
        new(true, LlvmOptimizationPipelineContractV1.ProductionRecipe, null);
}

public sealed record LlvmOptimizationProvenanceV1(
    string SchemaId,
    string PipelineContractDigest,
    bool PipelineExecuted,
    string Recipe,
    string RecipeDigest,
    string OptionsDigest,
    string ProfileIdentity,
    string InputDigest,
    string OutputDigest,
    int CanonicalInstructionsBefore,
    int CanonicalInstructionsAfter,
    int ScheduleCyclesBefore,
    int ScheduleCyclesAfter,
    int BundlesBefore,
    int BundlesAfter,
    string FinalCodeSizeStatus,
    string RuntimeMetricsStatus,
    bool PreTransformFactsInvalidated,
    string ToolchainContractDigest,
    string SemanticMappingContractDigest);

public sealed record LlvmOptimizationResultV1(
    LlvmSemanticMappingStatusV1 Status,
    IrProgram? Program,
    IReadOnlyList<IrFrontendDiagnosticV1> Diagnostics,
    LlvmOptimizationProvenanceV1? Provenance);

public sealed class LlvmOptimizationPipelineContractV1
{
    public const string SchemaId = "hybridcpu.llvm-optimization-pipeline/v1";
    public const string ProductionRecipe = "function(instcombine,dce)";

    public static LlvmOptimizationPipelineContractV1 Default { get; } = new();

    private LlvmOptimizationPipelineContractV1()
    {
        ToolchainContractDigest = LlvmToolchainContractV1.Default.ContractDigest;
        SemanticMappingContractDigest = LlvmSemanticMappingContractV1.Default.ContractDigest;
        RecipeDigest = Hash(ProductionRecipe);
        OptionsDigest = Hash(string.Join('|',
            SchemaId,
            "verify-each=true",
            "debug-logging=false",
            "loop-interleaving=false",
            "loop-unrolling=false",
            "loop-vectorization=false",
            "slp-vectorization=false",
            "merge-functions=false",
            "call-graph-profile=false",
            "profile=absent",
            "wall-clock=false"));
        ContractDigest = Hash(string.Join('|', SchemaId, ToolchainContractDigest,
            SemanticMappingContractDigest, ProductionRecipe, RecipeDigest, OptionsDigest));
    }

    public string ToolchainContractDigest { get; }
    public string SemanticMappingContractDigest { get; }
    public string RecipeDigest { get; }
    public string OptionsDigest { get; }
    public string ContractDigest { get; }
    public bool DefaultEnabled => false;
    public bool UsesWallClockBudget => false;
    public bool HasBackendAuthority => false;
    public bool HasRuntimeAuthority => false;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed unsafe class LlvmOptimizationPipelineV1
{
    public LlvmOptimizationResultV1 OptimizeAndMap(
        string path,
        LlvmInputKind kind,
        LlvmOptimizationOptionsV1? options = null)
    {
        options ??= LlvmOptimizationOptionsV1.Disabled;
        LlvmOptimizationPipelineContractV1 contract = LlvmOptimizationPipelineContractV1.Default;
        if (!options.Enabled)
        {
            LlvmSemanticMappingResultV1 direct = new LlvmSemanticMapperV1().MapFile(path, kind);
            (int Cycles, int Bundles) metrics = default;
            if (direct.Program is not null && !TryMeasure(direct.Program, out metrics))
                return Reject("HCLO0011", "HybridCPU downstream metric evaluation failed closed.");
            return new(direct.Status, direct.Program, direct.Diagnostics,
                direct.ImportProvenance is null ? null : new(
                    "hybridcpu.llvm-optimization-provenance/v1",
                    contract.ContractDigest,
                    PipelineExecuted: false,
                    Recipe: string.Empty,
                    contract.RecipeDigest,
                    contract.OptionsDigest,
                    "absent",
                    direct.ImportProvenance.InputDigest,
                    direct.ImportProvenance.InputDigest,
                    direct.Program?.Instructions.Count ?? 0,
                    direct.Program?.Instructions.Count ?? 0,
                    metrics.Cycles,
                    metrics.Cycles,
                    metrics.Bundles,
                    metrics.Bundles,
                    "UnavailableUntilBackendQualification",
                    "UnavailableUntilRuntimeQualification",
                    PreTransformFactsInvalidated: false,
                    contract.ToolchainContractDigest,
                    contract.SemanticMappingContractDigest));
        }

        if (!string.Equals(options.Recipe, LlvmOptimizationPipelineContractV1.ProductionRecipe,
                StringComparison.Ordinal))
            return Reject("HCLO0001", "Optimization recipe is not the exact production recipe.");
        if (options.ProfileSha256 is not null)
            return Reject("HCLO0002", "Profile-guided LLVM optimization is not qualified in Phase 11.");

        LlvmSemanticMappingResultV1 before = new LlvmSemanticMapperV1().MapFile(path, kind);
        if (before.Status != LlvmSemanticMappingStatusV1.Success || before.Program is null || before.ImportProvenance is null)
            return new(before.Status, null, before.Diagnostics, null);

        string optimizedPath = Path.Combine(Path.GetTempPath(), $"hybridcpu-phase11-{Guid.NewGuid():N}.bc");
        LlvmOptimizationResultV1 transformed = Transform(path, optimizedPath, contract, before);
        try { return transformed; }
        finally { if (File.Exists(optimizedPath)) File.Delete(optimizedPath); }
    }

    private static LlvmOptimizationResultV1 Transform(
        string inputPath,
        string optimizedPath,
        LlvmOptimizationPipelineContractV1 contract,
        LlvmSemanticMappingResultV1 before)
    {
        string fullPath = Path.GetFullPath(inputPath);
        byte[] pathBytes = Encoding.UTF8.GetBytes(fullPath + '\0');
        LLVMMemoryBufferRef buffer = default;
        LLVMContextRef context = default;
        LLVMModuleRef module = default;
        LLVMPassBuilderOptionsRef passOptions = default;
        sbyte* message = null;
        try
        {
            fixed (byte* pathPointer = pathBytes)
            {
                LLVMOpaqueMemoryBuffer* rawBuffer = null;
                if (LLVM.CreateMemoryBufferWithContentsOfFile((sbyte*)pathPointer, &rawBuffer, &message) != 0)
                    return Reject("HCLO0003", TakeMessage(ref message, "LLVM could not read optimization input."));
                buffer = rawBuffer;
            }
            context = LLVMContextRef.Create();
            bool parsed = context.TryParseIR(buffer, out module, out string parseMessage);
            buffer = default;
            if (!parsed) return Reject("HCLO0004", StableMessage(parseMessage, "LLVM rejected optimization input."));
            if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out string preVerify))
                return Reject("HCLO0005", StableMessage(preVerify, "Pre-optimization verification failed."));

            passOptions = LLVMPassBuilderOptionsRef.Create();
            passOptions.SetVerifyEach(true);
            passOptions.SetDebugLogging(false);
            passOptions.SetLoopInterleaving(false);
            passOptions.SetLoopUnrolling(false);
            passOptions.SetLoopVectorization(false);
            passOptions.SetSLPVectorization(false);
            passOptions.SetMergeFunctions(false);
            passOptions.SetCallGraphProfile(false);
            byte[] recipeBytes = Encoding.UTF8.GetBytes(LlvmOptimizationPipelineContractV1.ProductionRecipe + '\0');
            LLVMErrorRef error;
            fixed (byte* recipePointer = recipeBytes)
                error = LLVM.RunPasses(module, (sbyte*)recipePointer, (LLVMOpaqueTargetMachine*)null, passOptions);
            if (error != default)
                return Reject("HCLO0006", TakeErrorMessage(error, "LLVM pass pipeline failed."));
            if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out string postVerify))
                return Reject("HCLO0007", StableMessage(postVerify, "Post-optimization verification failed."));

            SetStableModuleIdentity(module);
            if (module.WriteBitcodeToFile(optimizedPath) != 0)
                return Reject("HCLO0008", "LLVM could not write the optimized bitcode remap artifact.");
            LlvmSemanticMappingResultV1 after = new LlvmSemanticMapperV1().MapFile(optimizedPath, LlvmInputKind.Bitcode);
            if (after.Status != LlvmSemanticMappingStatusV1.Success || after.Program is null || after.ImportProvenance is null)
                return new(after.Status, null, after.Diagnostics.Select(diagnostic =>
                    diagnostic with { StableSourceIdentity = fullPath }).ToArray(), null);
            if (after.Program.Instructions.Count > before.Program!.Instructions.Count)
                return Reject("HCLO0009", "Production optimization exceeded the Canonical IR non-regression threshold.");
            if (!TryMeasure(before.Program, out (int Cycles, int Bundles) beforeMetrics) ||
                !TryMeasure(after.Program, out (int Cycles, int Bundles) afterMetrics))
                return Reject("HCLO0011", "HybridCPU downstream metric evaluation failed closed.");
            if (afterMetrics.Cycles > beforeMetrics.Cycles || afterMetrics.Bundles > beforeMetrics.Bundles)
                return Reject("HCLO0009", "Production optimization exceeded a downstream schedule or bundle non-regression threshold.");

            return new(LlvmSemanticMappingStatusV1.Success, after.Program, Array.Empty<IrFrontendDiagnosticV1>(), new(
                "hybridcpu.llvm-optimization-provenance/v1",
                contract.ContractDigest,
                PipelineExecuted: true,
                LlvmOptimizationPipelineContractV1.ProductionRecipe,
                contract.RecipeDigest,
                contract.OptionsDigest,
                "absent",
                before.ImportProvenance!.InputDigest,
                after.ImportProvenance.InputDigest,
                before.Program.Instructions.Count,
                after.Program.Instructions.Count,
                beforeMetrics.Cycles,
                afterMetrics.Cycles,
                beforeMetrics.Bundles,
                afterMetrics.Bundles,
                "UnavailableUntilBackendQualification",
                "UnavailableUntilRuntimeQualification",
                PreTransformFactsInvalidated: true,
                contract.ToolchainContractDigest,
                contract.SemanticMappingContractDigest));
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or
            EntryPointNotFoundException or TypeInitializationException)
        {
            return Reject("HCLO0010", $"Pinned LLVM runtime became unavailable: {exception.GetType().Name}.");
        }
        finally
        {
            if (message != null) LLVM.DisposeMessage(message);
            if (passOptions != default) passOptions.Dispose();
            if (module != default) module.Dispose();
            if (context != default) context.Dispose();
            if (buffer != default) LLVM.DisposeMemoryBuffer(buffer);
        }
    }

    private static void SetStableModuleIdentity(LLVMModuleRef module)
    {
        byte[] identity = Encoding.UTF8.GetBytes("hybridcpu-optimized-v1");
        fixed (byte* pointer = identity)
        {
            LLVM.SetModuleIdentifier(module, (sbyte*)pointer, (nuint)identity.Length);
            LLVM.SetSourceFileName(module, (sbyte*)pointer, (nuint)identity.Length);
        }
    }

    private static (int Cycles, int Bundles) Measure(IrProgram program)
    {
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        return (
            schedule.BlockSchedules.Sum(static block => block.ScheduleLength),
            bundles.BlockResults.Sum(static block => block.Bundles.Count));
    }

    private static bool TryMeasure(IrProgram program, out (int Cycles, int Bundles) metrics)
    {
        try
        {
            metrics = Measure(program);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            metrics = default;
            return false;
        }
    }

    private static string TakeErrorMessage(LLVMErrorRef error, string fallback)
    {
        sbyte* pointer = LLVM.GetErrorMessage(error);
        if (pointer == null) return fallback;
        try { return StableMessage(new string(pointer), fallback); }
        finally { LLVM.DisposeErrorMessage(pointer); }
    }

    private static string TakeMessage(ref sbyte* message, string fallback)
    {
        if (message == null) return fallback;
        string result = StableMessage(new string(message), fallback);
        LLVM.DisposeMessage(message);
        message = null;
        return result;
    }

    private static string StableMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static LlvmOptimizationResultV1 Reject(string code, string message) =>
        new(LlvmSemanticMappingStatusV1.Unsupported, null, [new(code, message)], null);
}
