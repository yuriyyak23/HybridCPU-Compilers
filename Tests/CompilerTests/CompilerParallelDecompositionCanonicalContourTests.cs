using System.Linq;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using InstructionsEnum = HybridCPU.Compiler.Core.IR.HybridCpuOpcode;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerParallelDecompositionCanonicalContourTests
{
    [Fact]
    public void ParallelForCompiler_SynthesizedWorkerBackEdge_UsesCanonicalUnsignedBranchContour()
    {
        ParallelForCompiler compiler = new ParallelForCompiler();

        ParallelCompilationResult? result = compiler.CompileParallelFor(
            iterationStart: 0,
            iterationEnd: 12,
            iterationStep: 1,
            bodyOpcodes: new[] { InstructionsEnum.ADD },
            inductionReg: 4,
            reduction: null);

        Assert.NotNull(result);

        foreach (IrProgram workerProgram in result!.WorkerPrograms)
        {
            Assert.Contains(workerProgram.Instructions, instruction => instruction.Opcode == InstructionsEnum.BLTU);
        }
    }

    [Fact]
    public void ParallelForCompiler_SynthesizedReductionPrograms_UseCanonicalScalarMemoryContours()
    {
        ParallelForCompiler compiler = new ParallelForCompiler();
        ReductionPlan reduction = new ReductionPlan(
            ReduceOpcode: InstructionsEnum.ADD,
            IdentityElement: 0,
            AccumulatorRegister: 20,
            PartialResultBaseAddress: 0x20000);

        ParallelCompilationResult? result = compiler.CompileParallelFor(
            iterationStart: 0,
            iterationEnd: 12,
            iterationStep: 1,
            bodyOpcodes: new[] { InstructionsEnum.ADD },
            inductionReg: 4,
            reduction: reduction);

        Assert.NotNull(result);

        Assert.Contains(result!.CoordinatorProgram.Instructions, instruction => instruction.Opcode == InstructionsEnum.LD);
        Assert.Contains(result.CoordinatorProgram.Instructions, instruction => instruction.Opcode == InstructionsEnum.SD);

        foreach (IrProgram workerProgram in result.WorkerPrograms)
        {
            Assert.Contains(workerProgram.Instructions, instruction => instruction.Opcode == InstructionsEnum.SD);
        }
    }

    [Fact]
    public void ParallelCompilationResult_OverallAgreement_IncludesWorkersAndCoordinator()
    {
        ParallelForCompiler compiler = new ParallelForCompiler();
        ReductionPlan reduction = new ReductionPlan(
            ReduceOpcode: InstructionsEnum.ADD,
            IdentityElement: 0,
            AccumulatorRegister: 20,
            PartialResultBaseAddress: 0x20000);

        ParallelCompilationResult? result = compiler.CompileParallelFor(
            iterationStart: 0,
            iterationEnd: 12,
            iterationStep: 1,
            bodyOpcodes: new[] { InstructionsEnum.ADD },
            inductionReg: 4,
            reduction: reduction);

        Assert.NotNull(result);

        int expectedTotalBundles =
            result!.WorkerAgreements.Sum(agreement => agreement.TotalBundleCount)
            + result.CoordinatorAgreement.TotalBundleCount;

        Assert.Equal(expectedTotalBundles, result.OverallAgreement.TotalBundleCount);
        Assert.Equal(result.AllTypedSlotFactsValid, result.OverallAgreement.AllTypedSlotFactsValid);
        Assert.Equal(result.AllProgramsAdmissible, result.OverallAgreement.AllBundlesAdmissible);
        Assert.True(result.AllWorkerTypedSlotFactsValid);
    }

    [Fact]
    public void ParallelCompilationResult_AllTypedSlotFactsValid_And_AllProgramsAdmissible_IncludeCoordinatorAgreement()
    {
        ParallelForCompiler compiler = new ParallelForCompiler();
        ParallelCompilationResult? maybeResult = compiler.CompileParallelFor(
            iterationStart: 0,
            iterationEnd: 12,
            iterationStep: 1,
            bodyOpcodes: new[] { InstructionsEnum.ADD },
            inductionReg: 4,
            reduction: null);
        Assert.NotNull(maybeResult);
        ParallelCompilationResult result = maybeResult!;

        IrAdmissibilityAgreement validWorkerAgreement = new(
        [
            new IrBundleAdmissionResult(
                BundleCycle: 0,
                Classification: AdmissibilityClassification.StructurallyAdmissible,
                SafetyMaskResult: default(SafetyMaskCompatibilityResult),
                StealVerdicts: [],
                TypedSlotFacts: default,
                TypedSlotFactsValid: true)
        ]);

        IrAdmissibilityAgreement invalidCoordinatorAgreement = new(
        [
            new IrBundleAdmissionResult(
                BundleCycle: 0,
                Classification: AdmissibilityClassification.TypedSlotFactsInvalid,
                SafetyMaskResult: default(SafetyMaskCompatibilityResult),
                StealVerdicts: [],
                TypedSlotFacts: default,
                TypedSlotFactsValid: false)
        ]);

        ParallelCompilationResult mutated = result with
        {
            WorkerAgreements = [validWorkerAgreement],
            CoordinatorAgreement = invalidCoordinatorAgreement
        };

        Assert.True(mutated.AllWorkersAdmissible);
        Assert.True(mutated.AllWorkerTypedSlotFactsValid);
        Assert.False(mutated.CoordinatorAgreement.AllBundlesAdmissible);
        Assert.False(mutated.CoordinatorAgreement.AllTypedSlotFactsValid);
        Assert.False(mutated.AllProgramsAdmissible);
        Assert.False(mutated.AllTypedSlotFactsValid);
        Assert.Equal(1, mutated.OverallAgreement.StructuralAgreementFailureCount);
        Assert.Equal(1, mutated.OverallAgreement.TypedSlotInvalidBundleCount);
    }

}
