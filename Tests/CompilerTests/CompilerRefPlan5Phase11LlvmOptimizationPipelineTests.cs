using System.Text;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Llvm;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase11LlvmOptimizationPipelineTests
{
    [Fact]
    public void PipelineContract_IsPinnedDefaultOffAndNonAuthoritative()
    {
        LlvmOptimizationPipelineContractV1 contract = LlvmOptimizationPipelineContractV1.Default;
        Assert.Equal("function(instcombine,dce)", LlvmOptimizationPipelineContractV1.ProductionRecipe);
        Assert.Equal("73590940e7df70a185a0c7de76e4515c703212995097942b4e60b980209f9586", contract.ContractDigest);
        Assert.Equal("75d651f8628b4580f00e64020e4e1071f3457444dabf142eb363978b4c5149b6", contract.OptionsDigest);
        Assert.Equal(LlvmToolchainContractV1.Default.ContractDigest, contract.ToolchainContractDigest);
        Assert.Equal(LlvmSemanticMappingContractV1.Default.ContractDigest, contract.SemanticMappingContractDigest);
        Assert.False(contract.DefaultEnabled);
        Assert.False(contract.UsesWallClockBudget);
        Assert.False(contract.HasBackendAuthority);
        Assert.False(contract.HasRuntimeAuthority);
    }

    [Fact]
    public void DefaultOff_PathEqualsDirectSemanticMappingAndDoesNotExecutePipeline()
    {
        string path = WriteModule(SupportedModuleText);
        try
        {
            LlvmSemanticMappingResultV1 direct = new LlvmSemanticMapperV1().MapFile(path, LlvmInputKind.TextIr);
            LlvmOptimizationResultV1 disabled = new LlvmOptimizationPipelineV1().OptimizeAndMap(path, LlvmInputKind.TextIr);
            if (!PinnedRuntimeAvailable())
            {
                Assert.Equal(LlvmSemanticMappingStatusV1.Unavailable, direct.Status);
                Assert.Equal(LlvmSemanticMappingStatusV1.Unavailable, disabled.Status);
                Assert.Equal("HCLL0002", Assert.Single(disabled.Diagnostics).Code);
                return;
            }
            Assert.Equal(LlvmSemanticMappingStatusV1.Success, disabled.Status);
            Assert.Equal(Project(direct.Program!), Project(disabled.Program!));
            LlvmOptimizationProvenanceV1 provenance = Assert.IsType<LlvmOptimizationProvenanceV1>(disabled.Provenance);
            Assert.False(provenance.PipelineExecuted);
            Assert.False(provenance.PreTransformFactsInvalidated);
            Assert.Equal(LlvmOptimizationPipelineContractV1.Default.ContractDigest,
                provenance.PipelineContractDigest);
            Assert.Equal(provenance.InputDigest, provenance.OutputDigest);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProductionPipeline_OptimizesThenReentersPhase10FirewallDeterministically()
    {
        string path = WriteModule(SupportedModuleText);
        try
        {
            LlvmOptimizationPipelineV1 pipeline = new LlvmOptimizationPipelineV1();
            LlvmOptimizationResultV1 first = pipeline.OptimizeAndMap(
                path, LlvmInputKind.TextIr, LlvmOptimizationOptionsV1.Production);
            LlvmOptimizationResultV1 second = pipeline.OptimizeAndMap(
                path, LlvmInputKind.TextIr, LlvmOptimizationOptionsV1.Production);

            if (!PinnedRuntimeAvailable())
            {
                Assert.Equal(LlvmSemanticMappingStatusV1.Unavailable, first.Status);
                Assert.Equal("HCLL0002", Assert.Single(first.Diagnostics).Code);
                Assert.Equal(first.Status, second.Status);
                Assert.Equal(first.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Message)),
                    second.Diagnostics.Select(static diagnostic => (diagnostic.Code, diagnostic.Message)));
                return;
            }

            Assert.Equal(LlvmSemanticMappingStatusV1.Success, first.Status);
            Assert.Equal(Project(first.Program!), Project(second.Program!));
            Assert.Equal(first.Diagnostics, second.Diagnostics);
            LlvmOptimizationProvenanceV1 firstProvenance = Assert.IsType<LlvmOptimizationProvenanceV1>(first.Provenance);
            LlvmOptimizationProvenanceV1 secondProvenance = Assert.IsType<LlvmOptimizationProvenanceV1>(second.Provenance);
            Assert.True(firstProvenance.PipelineExecuted);
            Assert.Equal(LlvmOptimizationPipelineContractV1.Default.ContractDigest,
                firstProvenance.PipelineContractDigest);
            Assert.True(firstProvenance.PreTransformFactsInvalidated);
            // ADD's two constants and MUL's constant have three exact helper definitions.
            Assert.Equal(6, firstProvenance.CanonicalInstructionsBefore);
            Assert.Equal(1, firstProvenance.CanonicalInstructionsAfter);
            Assert.True(firstProvenance.ScheduleCyclesAfter <= firstProvenance.ScheduleCyclesBefore);
            Assert.True(firstProvenance.BundlesAfter <= firstProvenance.BundlesBefore);
            Assert.Equal("UnavailableUntilBackendQualification", firstProvenance.FinalCodeSizeStatus);
            Assert.Equal("UnavailableUntilRuntimeQualification", firstProvenance.RuntimeMetricsStatus);
            Assert.Equal(firstProvenance.OutputDigest, secondProvenance.OutputDigest);
            Assert.Equal(firstProvenance.RecipeDigest, secondProvenance.RecipeDigest);
            Assert.Equal(HybridCpuOpcode.JALR, Assert.Single(first.Program!.Instructions).Opcode);
            Assert.Empty(first.Program.FrontendEvidence.Facts);
            Assert.Equal(IrFrontendAdapterStatus.Success, CanonicalIrFrontendBoundaryV1.Validate(first.Program).Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidRecipeAndProfile_AreRejectedBeforePassExecution()
    {
        string path = WriteModule(SupportedModuleText);
        try
        {
            LlvmOptimizationPipelineV1 pipeline = new LlvmOptimizationPipelineV1();
            LlvmOptimizationResultV1 reordered = pipeline.OptimizeAndMap(path, LlvmInputKind.TextIr,
                new(true, "function(dce,instcombine)", null));
            Assert.Equal("HCLO0001", Assert.Single(reordered.Diagnostics).Code);
            Assert.Null(reordered.Provenance);

            LlvmOptimizationResultV1 profile = pipeline.OptimizeAndMap(path, LlvmInputKind.TextIr,
                new(true, LlvmOptimizationPipelineContractV1.ProductionRecipe, new string('a', 64)));
            Assert.Equal("HCLO0002", Assert.Single(profile.Diagnostics).Code);
            Assert.Null(profile.Provenance);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnsupportedInput_FailsAtPreTransformSemanticFirewall()
    {
        string unsupported = SupportedModuleText.Replace(
            "%sum = add nsw i32 1, 2",
            "%sum = sdiv i32 4, 2",
            StringComparison.Ordinal);
        string path = WriteModule(unsupported);
        try
        {
            LlvmOptimizationResultV1 result = new LlvmOptimizationPipelineV1().OptimizeAndMap(
                path, LlvmInputKind.TextIr, LlvmOptimizationOptionsV1.Production);
            if (!PinnedRuntimeAvailable())
            {
                Assert.Equal(LlvmSemanticMappingStatusV1.Unavailable, result.Status);
                Assert.Equal("HCLL0002", Assert.Single(result.Diagnostics).Code);
                Assert.Null(result.Program);
                Assert.Null(result.Provenance);
                return;
            }
            Assert.Equal(LlvmSemanticMappingStatusV1.Unsupported, result.Status);
            Assert.Equal("HCLL0014", Assert.Single(result.Diagnostics).Code);
            Assert.Null(result.Program);
            Assert.Null(result.Provenance);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OptionalLlvmPipeline_DoesNotChangeNativeProfileOutput()
    {
        HybridCpuInstructionWord[] nativeWords =
        [
            new()
            {
                OpCode = (uint)HybridCpuOpcode.ADD,
                DataTypeValue = HybridCpuDataType.INT32,
                PredicateMask = byte.MaxValue,
                Word1 = HybridCpuInstructionWord.PackArchRegs(1, 2, 3)
            }
        ];
        byte[] before = HybridCpuCanonicalCompiler.CompileProgram(0, nativeWords).ProgramImage;
        string path = WriteModule(SupportedModuleText);
        try
        {
            LlvmOptimizationResultV1 result = new LlvmOptimizationPipelineV1().OptimizeAndMap(
                path, LlvmInputKind.TextIr, LlvmOptimizationOptionsV1.Production);
            if (!PinnedRuntimeAvailable())
            {
                Assert.Equal(LlvmSemanticMappingStatusV1.Unavailable, result.Status);
                Assert.Equal("HCLL0002", Assert.Single(result.Diagnostics).Code);
            }
            else Assert.Equal(LlvmSemanticMappingStatusV1.Success, result.Status);
        }
        finally { File.Delete(path); }
        byte[] after = HybridCpuCanonicalCompiler.CompileProgram(0, nativeWords).ProgramImage;
        Assert.Equal(before, after);
    }

    private static string WriteModule(string module)
    {
        string path = Path.Combine(Path.GetTempPath(), $"hybridcpu-phase11-{Guid.NewGuid():N}.ll");
        File.WriteAllText(path, module, new UTF8Encoding(false));
        return path;
    }

    private static bool PinnedRuntimeAvailable() => new LlvmModuleImporterV1().ProbeRuntime().IsAvailable;

    private static string Project(IrProgram program) => string.Join('|',
        program.Instructions.Select(instruction => $"{instruction.Index}:{instruction.StableIdentity}:{instruction.Opcode}:{instruction.Semantics.IntegerOverflow}")
            .Concat(program.ValueFlow.Values.Select(value => $"v:{value.StableId}:{value.ValueKind.BitWidth}"))
            .Concat(program.ValueFlow.Accesses.Select(access => $"a:{access.ValueId}:{access.InstructionIndex}:{access.Kind}")));

    private const string SupportedModuleText = """
        target datalayout = "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64"
        target triple = "hybridcpuv2-unknown-none"

        define void @kernel() {
        entry:
          %sum = add nsw i32 1, 2
          %product = mul i32 %sum, 4
          ret void
        }

        !llvm.ident = !{!0}
        !0 = !{!"LLVM 20.1.2"}
        """;
}
