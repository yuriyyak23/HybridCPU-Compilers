using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Evidence;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Auth;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Tokens;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09CleanupMigrationReadinessTests
{
    [Fact]
    public void LegacyStructuralLegalitySurfaces_AreMarkedObsoleteAndHaveTypedWrappers()
    {
        AssertObsolete(typeof(IrCandidateBundleAnalysis).GetProperty(nameof(IrCandidateBundleAnalysis.IsLegal)));
        AssertObsolete(typeof(IrBundleLegalityResult).GetProperty(nameof(IrBundleLegalityResult.IsLegal)));
        AssertObsolete(typeof(IrBundleLegalityResult).GetProperty(nameof(IrBundleLegalityResult.Legal)));
        AssertObsolete(typeof(IrSlotAssignmentAnalysis).GetProperty(nameof(IrSlotAssignmentAnalysis.HasLegalAssignment)));
        AssertObsolete(typeof(IrSlotAssignmentAnalysis).GetProperty(nameof(IrSlotAssignmentAnalysis.CombinedLegalSlots)));
        AssertObsolete(typeof(IrSlotAssignmentAnalysis).GetProperty(nameof(IrSlotAssignmentAnalysis.InstructionLegalSlots)));

        Assert.NotNull(typeof(IrCandidateBundleAnalysis).GetProperty(nameof(IrCandidateBundleAnalysis.IsStructurallyAdmissible)));
        Assert.NotNull(typeof(IrBundleLegalityResult).GetProperty(nameof(IrBundleLegalityResult.IsStructurallyAdmissible)));
        Assert.NotNull(typeof(IrSlotAssignmentAnalysis).GetProperty(nameof(IrSlotAssignmentAnalysis.HasStructuralPlacement)));
        Assert.NotNull(typeof(IrSlotAssignmentAnalysis).GetProperty(nameof(IrSlotAssignmentAnalysis.StructurallyAllowedSlots)));
        Assert.NotNull(typeof(IrSlotAssignmentAnalysis).GetProperty(nameof(IrSlotAssignmentAnalysis.InstructionStructurallyAllowedSlots)));
        Assert.Null(typeof(CompilerStructuralBundleAdmissionResult).GetProperty("IsLegal"));
        Assert.Null(typeof(CompilerStructuralPlacementReport).GetProperty("HasLegalAssignment"));
        Assert.Null(typeof(CompilerStructuralPlacementReport).GetProperty("LegalSlots"));
        Assert.NotNull(typeof(CompilerStructuralBundleAdmissionResult).GetProperty(nameof(CompilerStructuralBundleAdmissionResult.IsStructurallyAdmissible)));
        Assert.NotNull(typeof(CompilerStructuralPlacementReport).GetProperty(nameof(CompilerStructuralPlacementReport.HasStructuralPlacement)));
        Assert.NotNull(typeof(CompilerStructuralPlacementReport).GetProperty(nameof(CompilerStructuralPlacementReport.StructurallyAllowedSlots)));
    }

    [Fact]
    public void LegacyLegalSlotsSurfaces_AreStructuralSlotFactsAndHaveAliases()
    {
        AssertObsolete(typeof(IrInstructionAnnotation).GetProperty(nameof(IrInstructionAnnotation.LegalSlots)));
        AssertObsolete(typeof(IrOpcodeExecutionProfile).GetProperty(nameof(IrOpcodeExecutionProfile.LegalSlots)));
        AssertObsolete(typeof(IrTypedSlotAdmissionDescriptor).GetProperty(nameof(IrTypedSlotAdmissionDescriptor.LegalSlots)));
        AssertObsolete(typeof(IrMaterializedBundleSlot).GetProperty(nameof(IrMaterializedBundleSlot.InstructionLegalSlots)));
        AssertObsolete(typeof(IrMaterializedBundleSlot).GetProperty(nameof(IrMaterializedBundleSlot.IsLegalPlacement)));
        AssertObsolete(typeof(HybridCpuSlotModel).GetMethod(nameof(HybridCpuSlotModel.GetLegalSlots)));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.HasLegalAssignment));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.AnalyzeAssignment));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.MaterializeAssignment));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.SearchAssignments));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.SearchAdjacentBundleAssignments));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.SearchAdjacentBundleTripletAssignments));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.SearchBasicBlockAssignments));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.SearchGlobalBasicBlockAssignments));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuSlotModel), nameof(HybridCpuSlotModel.SearchProgramAssignments));
        AssertAllPublicOverloadsObsolete(typeof(IrBundlePlacementQuality), nameof(IrBundlePlacementQuality.Create), parameterCount: 3);

        Assert.NotNull(typeof(IrInstructionAnnotation).GetProperty(nameof(IrInstructionAnnotation.StructurallyAllowedSlots)));
        Assert.NotNull(typeof(IrOpcodeExecutionProfile).GetProperty(nameof(IrOpcodeExecutionProfile.StructurallyAllowedSlots)));
        Assert.NotNull(typeof(IrTypedSlotAdmissionDescriptor).GetProperty(nameof(IrTypedSlotAdmissionDescriptor.StructurallyAllowedSlots)));
        Assert.NotNull(typeof(IrMaterializedBundleSlot).GetProperty(nameof(IrMaterializedBundleSlot.InstructionStructurallyAllowedSlots)));
        Assert.NotNull(typeof(IrMaterializedBundleSlot).GetProperty(nameof(IrMaterializedBundleSlot.IsStructuralPlacement)));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(nameof(HybridCpuSlotModel.GetStructurallyAllowedSlots)));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(nameof(HybridCpuSlotModel.HasStructuralPlacement)));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(nameof(HybridCpuSlotModel.AnalyzeStructuralAssignment)));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(
            nameof(HybridCpuSlotModel.MaterializeStructuralAssignment),
            [typeof(IReadOnlyList<IrIssueSlotMask>)]));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(
            nameof(HybridCpuSlotModel.SearchStructuralAssignments),
            [typeof(IReadOnlyList<IrIssueSlotMask>)]));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(
            nameof(HybridCpuSlotModel.SearchAdjacentBundleStructuralAssignments),
            [typeof(IReadOnlyList<IrIssueSlotMask>), typeof(IReadOnlyList<IrIssueSlotMask>)]));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(
            nameof(HybridCpuSlotModel.SearchAdjacentBundleTripletStructuralAssignments),
            [typeof(IReadOnlyList<IrIssueSlotMask>), typeof(IReadOnlyList<IrIssueSlotMask>), typeof(IReadOnlyList<IrIssueSlotMask>)]));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(
            nameof(HybridCpuSlotModel.SearchBasicBlockStructuralAssignments),
            [typeof(IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>)]));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(
            nameof(HybridCpuSlotModel.SearchGlobalBasicBlockStructuralAssignments),
            [typeof(IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>)]));
        Assert.NotNull(typeof(HybridCpuSlotModel).GetMethod(
            nameof(HybridCpuSlotModel.SearchProgramStructuralAssignments),
            [typeof(IReadOnlyList<IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>>)]));
        Assert.NotNull(typeof(IrBundlePlacementQuality).GetMethod(
            nameof(IrBundlePlacementQuality.CreateForStructuralSlotFacts)));

        Assert.Equal(
            IrIssueSlotMask.Vector,
            HybridCpuSlotModel.GetStructurallyAllowedSlots(IrResourceClass.VectorAlu));
    }

    [Fact]
    public void CoreSlotFactReaders_UseStructuralAliasesInsteadOfLegacyLegalSlots()
    {
        string irBuilder = ReadCompilerCoreIrFile(
            Path.Combine("Construction", "HybridCpuIrBuilder.cs"));
        string hazardModel = ReadCompilerCoreIrFile(
            Path.Combine("Hazards", "HybridCpuHazardModel.cs"));
        string instructionLegalityChecker = ReadCompilerCoreIrFile(
            Path.Combine("Hazards", "HybridCpuInstructionLegalityChecker.cs"));
        string dependencyAnalyzer = ReadCompilerCoreIrFile(
            Path.Combine("Hazards", "HybridCpuDependencyAnalyzer.cs"));
        string bundleFormer = ReadCompilerCoreIrFile(
            Path.Combine("Bundling", "HybridCpuBundleFormer.cs"));
        string lateLaneBinder = ReadCompilerCoreIrFile(
            Path.Combine("Bundling", "HybridCpuLateLaneBinder.cs"));
        string localListHeuristics = ReadCompilerCoreIrFile(
            Path.Combine("Scheduling", "HybridCpuLocalListScheduler.Heuristics.cs"));
        string slotMetadata = ReadCompilerCoreIrFile(
            Path.Combine("Model", "IrSlotMetadata.cs"));
        string materializedBundle = ReadCompilerCoreIrFile(
            Path.Combine("Model", "IrMaterializedBundle.cs"));

        string[] migratedSources =
        [
            irBuilder,
            hazardModel,
            instructionLegalityChecker,
            dependencyAnalyzer,
            bundleFormer,
            lateLaneBinder,
            localListHeuristics,
            slotMetadata
        ];

        foreach (string source in migratedSources)
        {
            Assert.DoesNotContain(".Annotation.LegalSlots", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".LegalSlots)", source, StringComparison.Ordinal);
            Assert.Contains("StructurallyAllowed", source, StringComparison.Ordinal);
        }

        Assert.Contains("GetStructurallyAllowedSlots", hazardModel, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLegalSlots(resourceClass", hazardModel, StringComparison.Ordinal);
        Assert.Contains("profile.StructurallyAllowedSlots", slotMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("profile.LegalSlots", slotMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain(".IsLegalPlacement", materializedBundle, StringComparison.Ordinal);
        Assert.Contains(".IsStructuralPlacement", materializedBundle, StringComparison.Ordinal);

        Assert.DoesNotContain("AnalyzeAssignment(", instructionLegalityChecker, StringComparison.Ordinal);
        Assert.Contains("AnalyzeStructuralAssignment(", instructionLegalityChecker, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAdjacentBundleAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAdjacentBundleTripletAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchGlobalBasicBlockAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchProgramAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.Contains("SearchStructuralAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.Contains("SearchAdjacentBundleStructuralAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.Contains("SearchAdjacentBundleTripletStructuralAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.Contains("SearchGlobalBasicBlockStructuralAssignments(", bundleFormer, StringComparison.Ordinal);
        Assert.Contains("SearchProgramStructuralAssignments(", bundleFormer, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyEvaluateLegalityEntrypoints_AreObsoleteAndTypedStructuralEntrypointsExist()
    {
        MethodInfo? legacyCandidate =
            typeof(HybridCpuInstructionLegalityChecker).GetMethod(nameof(HybridCpuInstructionLegalityChecker.EvaluateCandidateBundle));
        MethodInfo? legacyCluster =
            typeof(HybridCpuInstructionLegalityChecker).GetMethod(nameof(HybridCpuInstructionLegalityChecker.EvaluateClusterPreparedLegality));
        MethodInfo? structuralCandidate =
            typeof(HybridCpuInstructionLegalityChecker).GetMethod(nameof(HybridCpuInstructionLegalityChecker.AnalyzeStructuralCandidateBundle));
        MethodInfo? structuralCluster =
            typeof(HybridCpuInstructionLegalityChecker).GetMethod(nameof(HybridCpuInstructionLegalityChecker.AnalyzeClusterPreparedStructuralAdmission));

        AssertObsolete(legacyCandidate);
        AssertObsolete(legacyCluster);
        Assert.NotNull(structuralCandidate);
        Assert.NotNull(structuralCluster);
        Assert.Equal(typeof(CompilerStructuralBundleAdmissionResult), structuralCandidate!.ReturnType);
        Assert.Equal(typeof(CompilerStructuralBundleAdmissionResult), structuralCluster!.ReturnType);

        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker();
        CompilerStructuralBundleAdmissionResult result =
            checker.AnalyzeStructuralCandidateBundle(Array.Empty<IrInstruction>());

        Assert.True(result.IsStructurallyAdmissible);
        Assert.Equal(CompilerAuthorityClass.StructuralAdmissionEvidence, result.Header.AuthorityClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, result.Header.ExecutionClaim);
        Assert.True(result.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(result.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
    }

    [Fact]
    public void LegacyBackendLoweringBoolSurfaces_AreMarkedObsolete()
    {
        AssertObsolete(typeof(CompilerBackendLoweringDecision).GetProperty(nameof(CompilerBackendLoweringDecision.IsAllowed)));

        string[] helperNames =
        [
            nameof(CompilerBackendLoweringContract.CanSelectFeature),
            nameof(CompilerBackendLoweringContract.AllowsDescriptorEvidence),
            nameof(CompilerBackendLoweringContract.AllowsParserValidation),
            nameof(CompilerBackendLoweringContract.AllowsModelOrTestHelper),
            nameof(CompilerBackendLoweringContract.CanSelectForProductionLowering)
        ];

        foreach (string helperName in helperNames)
        {
            MethodInfo? method = typeof(CompilerBackendLoweringContract).GetMethod(
                helperName,
                BindingFlags.Public | BindingFlags.Static);
            AssertObsolete(method);
        }
    }

    [Fact]
    public void LegacyBackendLoweringDecisionAdapter_DoesNotStrengthenAuthority()
    {
        CompilerBackendLoweringDecision legacyDecision =
            CompilerBackendLoweringDecision.Allow("legacy backend contract reports all future gates present");

        CompilerLoweringDecision decision =
            CompilerLoweringDecision.FromLegacyBackendLoweringDecision(
                legacyDecision,
                "CompilerBackendLoweringDecision.IsAllowed",
                SemanticIntentKind.ExternalAcceleratorCommand,
                ExecutionContourKind.L7SdcLane7);

        Assert.Equal(CompilerLoweringDecisionKind.FutureGated, decision.DecisionKind);
        Assert.Equal(CompilerProductionLoweringStatus.FutureGated, decision.ProductionLoweringStatus);
        Assert.Equal(CompilerEmissionClass.EvidenceOnly, decision.EmissionClass);
        Assert.Equal(CompilerAuthorityClass.CompilerEvidenceProduction, decision.AuthorityClass);
        Assert.Equal(CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference, decision.AuthoritySourceKind);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, decision.ExecutionClaim);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, decision.PublicationClass);
        Assert.Contains(CompilerRejectReason.CapabilityObservationOnly, decision.RejectReasons);
        Assert.NotNull(decision.LegacyTranslation);
        Assert.False(decision.LegacyTranslation.StrengthensAuthority);
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimePublicationRequired));
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
    }

    [Fact]
    public void SchedulerAndBundlerCallers_DoNotReadLegacyLegalityOrPlacementAliases()
    {
        string[] migratedFiles =
        [
            Path.Combine("Scheduling", "HybridCpuProgramOrderLocalScheduler.cs"),
            Path.Combine("Scheduling", "HybridCpuLocalListScheduler.cs"),
            Path.Combine("Bundling", "HybridCpuBundleFormer.cs")
        ];

        foreach (string migratedFile in migratedFiles)
        {
            string source = ReadCompilerCoreIrFile(migratedFile);
            Assert.DoesNotContain(".IsLegal", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".HasLegalAssignment", source, StringComparison.Ordinal);
            Assert.Contains("Structurally", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BackendLoweringSelfUse_DoesNotReadLegacyBoolAuthorityAliases()
    {
        string backendContract = ReadCompilerCoreIrFile(
            Path.Combine("Model", "CompilerBackendLoweringContract.cs"));
        string loweringDecision = ReadCompilerCoreIrFile(
            Path.Combine("Lowering", "CompilerLoweringDecision.cs"));

        Assert.DoesNotContain("CanSelectForProductionLowering(request.State)", backendContract, StringComparison.Ordinal);
        Assert.Contains("IsProductionExecutableState(request.State)", backendContract, StringComparison.Ordinal);
        Assert.Contains("internal bool IsAllowedObservation", backendContract, StringComparison.Ordinal);
        Assert.Contains("sourceDecision.IsAllowedObservation", loweringDecision, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceDecision.IsAllowed;", loweringDecision, StringComparison.Ordinal);
    }

    [Fact]
    public void HelperRecoveryDecision_DoesNotStrengthenAuthorityForRecoveredHelperAbi()
    {
        CompilerLoweringDecision decision =
            CompilerLoweringDecision.FromLegacyHelperRecoveryBool(
                sourceValue: true,
                CompilerHelperRecoveryStatus.HelperAbiRecovered,
                "CompilerMatrixTileEmissionLowerer.TryRecoverFromInstruction",
                SemanticIntentKind.MatrixTile,
                ExecutionContourKind.MatrixTileHelperOnly,
                "legacy helper recovery recognized MatrixTile helper ABI");

        Assert.Equal(CompilerLoweringDecisionKind.HelperAbiOnly, decision.DecisionKind);
        Assert.Equal(CompilerProductionLoweringStatus.HelperAbiOnly, decision.ProductionLoweringStatus);
        Assert.Equal(CompilerEmissionClass.EvidenceOnly, decision.EmissionClass);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, decision.PublicationClass);
        Assert.Equal(CompilerExecutionClaim.HelperOnly, decision.ExecutionClaim);
        Assert.Contains(CompilerRejectReason.HelperAbiOnly, decision.RejectReasons);
        Assert.NotNull(decision.LegacyTranslation);
        Assert.False(decision.LegacyTranslation.StrengthensAuthority);
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.False(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.False(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
        Assert.False(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimePublicationRequired));
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
    }

    [Fact]
    public void HelperRecoveryDecision_NotRecognizedIsParserEvidenceOnly()
    {
        CompilerLoweringDecision decision =
            CompilerLoweringDecision.FromLegacyHelperRecoveryBool(
                sourceValue: false,
                CompilerHelperRecoveryStatus.NotRecognized,
                "CompilerVectorTransferEmissionLowerer.TryRecoverFromInstruction",
                SemanticIntentKind.VectorStream,
                ExecutionContourKind.StreamEngineVector,
                "legacy helper recovery did not recognize VectorTransfer helper ABI");

        Assert.Equal(CompilerLoweringDecisionKind.ParserOnly, decision.DecisionKind);
        Assert.Equal(CompilerProductionLoweringStatus.ParserOnly, decision.ProductionLoweringStatus);
        Assert.Equal(CompilerEmissionClass.EvidenceOnly, decision.EmissionClass);
        Assert.Equal(CompilerExecutionClaim.ParserOnly, decision.ExecutionClaim);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, decision.PublicationClass);
        Assert.NotNull(decision.LegacyTranslation);
        Assert.False(decision.LegacyTranslation.StrengthensAuthority);
        Assert.Contains(CompilerRejectReason.ParserOnly, decision.RejectReasons);
    }

    [Fact]
    public void HelperRecoveryCallers_UseTypedRecoveryInsteadOfLegacyTryBool()
    {
        string builder = ReadCompilerCoreIrFile(
            Path.Combine("Construction", "HybridCpuIrBuilder.cs"));
        string matrixTileLowerer = ReadCompilerCoreIrFile(
            Path.Combine("Construction", "CompilerMatrixTileEmissionLowerer.cs"));
        string vectorTransferLowerer = ReadCompilerCoreIrFile(
            Path.Combine("Construction", "CompilerVectorTransferEmissionLowerer.cs"));
        string loweringDecision = ReadCompilerCoreIrFile(
            Path.Combine("Lowering", "CompilerLoweringDecision.cs"));
        string helperRecoveryResult = ReadCompilerCoreIrFile(
            Path.Combine("Lowering", "CompilerHelperRecoveryResult.cs"));

        Assert.DoesNotContain(".TryRecoverFromInstruction(", builder, StringComparison.Ordinal);
        Assert.Contains("CompilerHelperRecoveryResult<CompilerMatrixTileEmissionPlan>", builder, StringComparison.Ordinal);
        Assert.Contains("CompilerHelperRecoveryResult<CompilerVectorTransferEmissionPlan>", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed record CompilerHelperRecoveryResult<TPlan>", loweringDecision, StringComparison.Ordinal);
        Assert.Contains("public sealed record CompilerHelperRecoveryResult<TPlan>", helperRecoveryResult, StringComparison.Ordinal);
        Assert.Contains("namespace HybridCPU.Compiler.Core.IR.Lowering;", helperRecoveryResult, StringComparison.Ordinal);
        Assert.Contains("RecoverFromInstruction", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("RecoverFromInstruction", vectorTransferLowerer, StringComparison.Ordinal);
        Assert.Contains("FromLegacyHelperRecoveryBool", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("FromLegacyHelperRecoveryBool", vectorTransferLowerer, StringComparison.Ordinal);
        Assert.Contains("[Obsolete", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("[Obsolete", vectorTransferLowerer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", matrixTileLowerer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", vectorTransferLowerer, StringComparison.Ordinal);
    }

    [Fact]
    public void PositiveMatrixTileAndVectorTransferEmissionResults_CarryDecisionWithoutRuntimeAuthority()
    {
        HybridCpuThreadCompilerContext matrixContext = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> matrixResult =
            matrixContext.CompileMtileLoadWithDecision(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileDescriptorAbi.Create(2, 2, HybridCpuDataType.INT8),
                CompilerMatrixTileMemoryFaultAbiInputs.Create(0x100));

        Assert.Equal(1, matrixContext.InstructionCount);
        Assert.Equal(matrixResult.Plan.EncodedInstruction.Word0, matrixContext.GetCompiledInstructions()[0].Word0);
        AssertPositiveHelperEmissionDecision(
            matrixResult.Decision,
            SemanticIntentKind.MatrixTile,
            ExecutionContourKind.MatrixTileHelperOnly);

        HybridCpuThreadCompilerContext vectorContext = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan> vectorResult =
            vectorContext.CompileVloadWithDecision(
                CompilerVectorTransferMemoryAddressAbi.Create(0x200),
                CompilerVectorTransferMemoryAddressAbi.Create(0x300),
                CompilerVectorTransferShapeAbi.CreateContiguous(HybridCpuDataType.INT32, 4));

        Assert.Equal(1, vectorContext.InstructionCount);
        Assert.Equal(vectorResult.Plan.EncodedInstruction.Word0, vectorContext.GetCompiledInstructions()[0].Word0);
        AssertPositiveHelperEmissionDecision(
            vectorResult.Decision,
            SemanticIntentKind.VectorStream,
            ExecutionContourKind.StreamEngineVector);

        Assert.Null(matrixResult.Decision.LegacyTranslation);
        Assert.Null(vectorResult.Decision.LegacyTranslation);
        Assert.Contains(CompilerRejectReason.CarrierIsNotPublication, matrixResult.Decision.RejectReasons);
        Assert.Contains(CompilerRejectReason.CarrierIsNotPublication, vectorResult.Decision.RejectReasons);
    }

    [Fact]
    public void PositiveEmissionCallers_UseDecisionBearingWrappersBeforeAppendingCarrier()
    {
        Assert.NotNull(typeof(CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan>)
            .GetProperty(nameof(CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan>.Decision)));
        Assert.NotNull(typeof(CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan>)
            .GetProperty(nameof(CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan>.Plan)));

        string matrixTileLowerer = ReadCompilerCoreIrFile(
            Path.Combine("Construction", "CompilerMatrixTileEmissionLowerer.cs"));
        string vectorTransferLowerer = ReadCompilerCoreIrFile(
            Path.Combine("Construction", "CompilerVectorTransferEmissionLowerer.cs"));
        string matrixTileFacade = ReadCompilerApiFile(
            Path.Combine("Threading", "ThreadCompilerContext.MatrixTile.cs"));
        string vectorTransferFacade = ReadCompilerApiFile(
            Path.Combine("Threading", "ThreadCompilerContext.VectorTransfer.cs"));

        Assert.Contains("LowerWithDecision", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("LowerWithDecision", vectorTransferLowerer, StringComparison.Ordinal);
        Assert.Contains("CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan>", matrixTileFacade, StringComparison.Ordinal);
        Assert.Contains("CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan>", vectorTransferFacade, StringComparison.Ordinal);
        Assert.Contains("CompileMtileLoadWithDecision", matrixTileFacade, StringComparison.Ordinal);
        Assert.Contains("CompileVloadWithDecision", vectorTransferFacade, StringComparison.Ordinal);
        Assert.Contains("FromPositiveHelperEmission", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("FromPositiveHelperEmission", vectorTransferLowerer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", matrixTileLowerer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", vectorTransferLowerer, StringComparison.Ordinal);
    }

    [Fact]
    public void HybridCpuThreadCompilerContext_PublicFacadesAreClassified()
    {
        MethodInfo[] publicMethods = typeof(HybridCpuThreadCompilerContext)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ThenBy(static method => method.GetParameters().Length)
            .ToArray();

        string[] reflectedKeys = publicMethods
            .Select(CreateFacadeMemberKey)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        string[] auditKeys = HybridCpuThreadCompilerFacadeAudit.PublicFacadeRows
            .Select(static row => row.MemberKey)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(reflectedKeys, auditKeys);
        Assert.Equal(auditKeys.Length, auditKeys.Distinct(StringComparer.Ordinal).Count());

        foreach (MethodInfo method in publicMethods)
        {
            string memberKey = CreateFacadeMemberKey(method);
            Assert.True(
                HybridCpuThreadCompilerFacadeAudit.TryGetRow(memberKey, out HybridCpuThreadCompilerFacadeAuditRow? row),
                $"Missing facade audit row for {memberKey}.");
            Assert.False(string.IsNullOrWhiteSpace(row.AuthoritySemantics));

            if (method.ReturnType == typeof(bool))
            {
                Assert.Fail($"Public thread compiler facade exposes raw bool boundary: {memberKey}.");
            }
        }
    }

    [Fact]
    public void HybridCpuThreadCompilerContext_FacadeAuditSeparatesTypedBoundariesFromCompatibilityArtifacts()
    {
        AssertFacadeKind(
            "CompileMtileLoadWithDecision/4",
            HybridCpuThreadCompilerFacadeBoundaryKind.TypedCompilerBoundary);
        AssertFacadeKind(
            "CompileVloadWithDecision/4",
            HybridCpuThreadCompilerFacadeBoundaryKind.TypedCompilerBoundary);
        AssertFacadeKind(
            "CompileAcceleratorSubmit/3",
            HybridCpuThreadCompilerFacadeBoundaryKind.TypedCompilerBoundary);

        string[] rawPlanCompatibilityKeys =
        [
            "CompileMtileLoad/4",
            "CompileMtileStore/4",
            "CompileMtileMacc/6",
            "CompileMtranspose/5",
            "CompileVload/4",
            "CompileVstore/4"
        ];

        foreach (string memberKey in rawPlanCompatibilityKeys)
        {
            AssertFacadeKind(
                memberKey,
                HybridCpuThreadCompilerFacadeBoundaryKind.ObsoleteCompatibilityFacade);
            MethodInfo method = ResolveFacadeMethod(memberKey);
            AssertObsolete(method);
        }

        AssertFacadeKind(
            "CompileInstruction/9",
            HybridCpuThreadCompilerFacadeBoundaryKind.ObsoleteCompatibilityFacade);
        AssertFacadeKind(
            "InsertInstruction/10",
            HybridCpuThreadCompilerFacadeBoundaryKind.ObsoleteCompatibilityFacade);
        AssertFacadeKind(
            "CompileProgram/0",
            HybridCpuThreadCompilerFacadeBoundaryKind.CompilerArtifactObservation);
        AssertFacadeKind(
            "GetBundleAnnotations/0",
            HybridCpuThreadCompilerFacadeBoundaryKind.CompilerArtifactObservation);
        AssertFacadeKind(
            "DeclareLabel/2",
            HybridCpuThreadCompilerFacadeBoundaryKind.CompilerMetadataBoundary);
        AssertFacadeKind(
            "Reset/0",
            HybridCpuThreadCompilerFacadeBoundaryKind.CompilerStateMutation);
    }

    [Fact]
    public void RuntimeOwnedGuardReads_AreWrappedAsCompilerObservations()
    {
        AcceleratorGuardDecision rejectedSubmitGuard =
            AcceleratorGuardDecision.Reject(
                AcceleratorGuardSurface.SubmitAdmission,
                AcceleratorGuardFault.DomainMismatch,
                descriptorOwnerBinding: null,
                evidence: null,
                RejectKind.DomainMismatch,
                "submit guard rejected by runtime owner/domain guard plane");

        CompilerRuntimeGuardObservation observation =
            CompilerRuntimeGuardObservation.FromAcceleratorSubmitGuard(
                rejectedSubmitGuard,
                "test.runtime.guard",
                "runtime guard observation is evidence only");

        Assert.Equal(CompilerRuntimeGuardObservationKind.AcceleratorSubmitGuard, observation.Kind);
        Assert.False(observation.ObservedGuardAllowsProgress);
        Assert.Equal(CompilerAuthorityClass.CompilerEvidenceProduction, observation.Header.AuthorityClass);
        Assert.Equal(CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference, observation.Header.AuthoritySourceKind);
        Assert.Equal(CompilerEvidenceClass.RuntimeContractObservationEvidence, observation.Header.EvidenceClass);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, observation.Header.PublicationClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, observation.Header.ExecutionClaim);
        Assert.True(observation.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(observation.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.True(observation.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));
        Assert.True(observation.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.True(observation.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
        Assert.True(observation.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimePublicationRequired));
    }

    [Fact]
    public void ThreadCompilerContext_DoesNotReadRuntimeGuardIsAllowedDirectly()
    {
        string threadContext = ReadCompilerApiFile(
            Path.Combine("Threading", "HybridCpuThreadCompilerContext.cs"));
        string guardObservation = ReadCompilerApiFile(
            Path.Combine("Threading", "ThreadCompilerContext.RuntimeGuardObservation.cs"));

        Assert.DoesNotContain(".OwnerGuardDecision.IsAllowed", threadContext, StringComparison.Ordinal);
        Assert.DoesNotContain("submitGuard.IsAllowed", threadContext, StringComparison.Ordinal);
        Assert.Contains("CompilerRuntimeGuardObservation", threadContext, StringComparison.Ordinal);
        Assert.Contains("guardDecision.IsAllowed", guardObservation, StringComparison.Ordinal);
        Assert.Contains("RuntimeOwnedPolicyReference", guardObservation, StringComparison.Ordinal);
        Assert.Contains("NoExecutionClaim", guardObservation, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectiveParseResult_SuccessAliasIsParseOnlyAndObsolete()
    {
        AssertObsolete(typeof(HybridCpuCompilerDirectives.DirectiveParseResult).GetProperty(
            nameof(HybridCpuCompilerDirectives.DirectiveParseResult.Success)));
        Assert.NotNull(typeof(HybridCpuCompilerDirectives.DirectiveParseResult).GetProperty(
            nameof(HybridCpuCompilerDirectives.DirectiveParseResult.IsDirectiveParsed)));
        Assert.NotNull(typeof(HybridCpuCompilerDirectives.DirectiveParseResult).GetMethod(
            nameof(HybridCpuCompilerDirectives.DirectiveParseResult.ToParseObservation)));

        HybridCpuCompilerDirectives parser = new HybridCpuCompilerDirectives(new YAKSys_Hybrid_CPU.Processor.CPU_Core(0));
        HybridCpuCompilerDirectives.DirectiveParseResult parsed = parser.ParseDirective(".excmode 1");
        CompilerDirectiveParseObservation parsedObservation =
            parsed.ToParseObservation("HybridCpuCompilerDirectives.ParseDirective");

        Assert.True(parsed.IsDirectiveParsed);
        Assert.True(parsedObservation.IsDirectiveParsed);
        Assert.Equal(CompilerAuthorityClass.CompilerEvidenceProduction, parsedObservation.Header.AuthorityClass);
        Assert.Equal(CompilerAuthoritySourceKind.CompilerAbiValidator, parsedObservation.Header.AuthoritySourceKind);
        Assert.Equal(CompilerEvidenceClass.ParserEvidence, parsedObservation.Header.EvidenceClass);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, parsedObservation.Header.PublicationClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, parsedObservation.Header.ExecutionClaim);
        Assert.Equal(
            CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
            parsedObservation.Header.RuntimeAuthorityDependency);

        HybridCpuCompilerDirectives.DirectiveParseResult rejected = parser.ParseDirective(".excmode 9");
        CompilerDirectiveParseObservation rejectedObservation =
            rejected.ToParseObservation("HybridCpuCompilerDirectives.ParseDirective");

        Assert.False(rejected.IsDirectiveParsed);
        Assert.False(rejectedObservation.IsDirectiveParsed);
        Assert.Equal(CompilerEvidenceClass.NegativeGateEvidence, rejectedObservation.Header.EvidenceClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, rejectedObservation.Header.ExecutionClaim);
    }

    [Fact]
    public void DirectiveParserCallers_DoNotReadLegacySuccessAlias()
    {
        string directivesSource = ReadCompilerApiFile(
            Path.Combine("Frontend", "Directives", "HybridCpuCompilerDirectives.cs"));

        Assert.DoesNotContain(".Success", directivesSource, StringComparison.Ordinal);
        Assert.Contains("IsDirectiveParsed", directivesSource, StringComparison.Ordinal);
        Assert.Contains("CompilerDirectiveParseObservation", directivesSource, StringComparison.Ordinal);
        Assert.Contains("NoExecutionClaim", directivesSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeExecutionRequired", directivesSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", directivesSource, StringComparison.Ordinal);
    }

    [Fact]
    public void VmxPreflightSuccessAlias_IsProjectionPreflightOnlyAndObsolete()
    {
        AssertObsolete(typeof(CompilerVmxPreflightResult).GetProperty("Success"));
        Assert.NotNull(typeof(CompilerVmxPreflightResult).GetProperty(
            nameof(CompilerVmxPreflightResult.ProjectionPreflightPassed)));

        string vmxAuthoritySource = ReadCompilerFile(
            Path.Combine("Legacy", "VMX-2", "Core", "IR", "Model", "VmxCompilerAuthority.cs"));

        Assert.Contains("ProjectionPreflightPassed", vmxAuthoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("return CompilerVmxPreflightResult.Success;", vmxAuthoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", vmxAuthoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeCommitRequired", vmxAuthoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeRetireRequired", vmxAuthoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimePublicationRequired", vmxAuthoritySource, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyValidPredicatesOnEnvelopeValidation_AreMarkedObsoleteAndTypedByAuthorityFields()
    {
        AssertObsolete(typeof(CompilerArtifactValidationResult).GetProperty(nameof(CompilerArtifactValidationResult.IsValid)));
        AssertObsolete(typeof(EvidenceIsolationValidationResult).GetProperty(nameof(EvidenceIsolationValidationResult.IsValid)));
        Assert.NotNull(typeof(CompilerArtifactValidationResult).GetProperty(nameof(CompilerArtifactValidationResult.IsAuthorityScopedValidation)));
        Assert.NotNull(typeof(CompilerArtifactValidationResult).GetProperty(nameof(CompilerArtifactValidationResult.HasValidationDiagnostics)));
        Assert.NotNull(typeof(EvidenceIsolationValidationResult).GetProperty(nameof(EvidenceIsolationValidationResult.IsEvidenceIsolated)));
        Assert.NotNull(typeof(EvidenceIsolationValidationResult).GetProperty(nameof(EvidenceIsolationValidationResult.HasIsolationViolations)));

        ConstructorInfo? artifactCtor = typeof(CompilerArtifactValidationResult).GetConstructors().SingleOrDefault();
        Assert.NotNull(artifactCtor);
        string[] artifactParameterNames = artifactCtor!.GetParameters().Select(p => p.Name ?? string.Empty).ToArray();
        Assert.Contains(artifactParameterNames, name => string.Equals(name, "authorityClass", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(artifactParameterNames, name => string.Equals(name, "evidenceClass", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(artifactParameterNames, name => string.Equals(name, "executionClaim", StringComparison.OrdinalIgnoreCase));

        CompilerArtifactValidationResult helperClaim = new CompilerArtifactValidationResult(
            IsValid: true,
            CompilerArtifactKind.CompilerEvidenceEnvelope,
            CompilerEvidenceClass.ParserEvidence,
            CompilerAuthorityClass.CompilerEvidenceProduction,
            CompilerExecutionClaim.HelperOnly,
            RuntimeLegalityStillRequired: true,
            Array.Empty<string>());
        EvidenceIsolationValidationResult validNoAuthority = new EvidenceIsolationValidationResult(
            IsValid: true,
            Array.Empty<string>());
        EvidenceIsolationValidationResult isolatedViolation = new EvidenceIsolationValidationResult(
            IsValid: false,
            ["host-owned evidence attempted to enter guest/domain state"]);

        Assert.False(helperClaim.IsAuthorityScopedValidation);
        Assert.True(validNoAuthority.IsEvidenceIsolated);
        Assert.True(isolatedViolation.HasIsolationViolations);
        Assert.False(isolatedViolation.IsEvidenceIsolated);
    }

    [Fact]
    public void MatrixTileDomainValidationCallers_UseAuthorityNeutralPredicates()
    {
        Assert.NotNull(typeof(MatrixTileMemoryShapeValidationResult).GetProperty(
            nameof(MatrixTileMemoryShapeValidationResult.IsMemoryShapeAbiAccepted)));
        Assert.NotNull(typeof(MatrixTileNumericPolicyValidationResult).GetProperty(
            nameof(MatrixTileNumericPolicyValidationResult.IsRuntimeOwnedNumericPolicyAccepted)));
        Assert.NotNull(typeof(MatrixTileLayoutPolicyValidationResult).GetProperty(
            nameof(MatrixTileLayoutPolicyValidationResult.IsRuntimeOwnedLayoutPolicyAccepted)));
        Assert.NotNull(typeof(MatrixTileSemanticValidationResult).GetProperty(
            nameof(MatrixTileSemanticValidationResult.IsSemanticAbiAccepted)));

        MatrixTileSemanticValidationResult semanticFault =
            MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.MissingNumericPolicy);
        MatrixTileLayoutPolicyValidationResult layoutFault =
            MatrixTileLayoutPolicyValidationResult.Fault(MatrixTileLayoutPolicyFaultKind.MissingPolicy);

        Assert.False(semanticFault.IsSemanticAbiAccepted);
        Assert.False(layoutFault.IsRuntimeOwnedLayoutPolicyAccepted);

        string matrixTileLowerer = ReadCompilerCoreIrFile(
            Path.Combine("Construction", "CompilerMatrixTileEmissionLowerer.cs"));
        Assert.DoesNotContain(".IsValid", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("IsMemoryShapeAbiAccepted", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("IsRuntimeOwnedNumericPolicyAccepted", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("IsRuntimeOwnedLayoutPolicyAccepted", matrixTileLowerer, StringComparison.Ordinal);
        Assert.Contains("IsSemanticAbiAccepted", matrixTileLowerer, StringComparison.Ordinal);
    }

    [Fact]
    public void DscCompilerFacadeValidation_UsesDescriptorAbiAcceptancePredicate()
    {
        Assert.NotNull(typeof(DmaStreamComputeValidationResult).GetProperty(
            nameof(DmaStreamComputeValidationResult.IsDescriptorAbiAccepted)));

        DmaStreamComputeValidationResult decodeFault =
            DmaStreamComputeValidationResult.Fail(
                DmaStreamComputeValidationFault.DescriptorDecodeFault,
                "malformed DSC descriptor bytes");
        Assert.False(decodeFault.IsDescriptorAbiAccepted);

        string threadContext = ReadCompilerApiFile(
            Path.Combine("Threading", "HybridCpuThreadCompilerContext.cs"));
        Assert.DoesNotContain("validation.IsValid", threadContext, StringComparison.Ordinal);
        Assert.Contains("validation.IsDescriptorAbiAccepted", threadContext, StringComparison.Ordinal);
    }

    [Fact]
    public void DscDescriptorAndStructuralReadTests_UseTypedAcceptancePredicates()
    {
        Assert.NotNull(typeof(DmaStreamComputeValidationResult).GetProperty(
            nameof(DmaStreamComputeValidationResult.IsDescriptorAbiAccepted)));
        Assert.NotNull(typeof(DmaStreamComputeStructuralReadResult).GetProperty(
            nameof(DmaStreamComputeStructuralReadResult.IsStructuralDescriptorReadAccepted)));
        Assert.NotNull(typeof(DmaStreamComputeDsc2ValidationResult).GetProperty(
            nameof(DmaStreamComputeDsc2ValidationResult.IsParserAccepted)));

        string repoRoot = CompatFreezeScanner.FindRepoRoot();
        string testsRoot = Path.Combine(repoRoot, "HybridCPU_ISE.Tests", "tests");
        IEnumerable<string> dscTestFiles =
            Directory.EnumerateFiles(testsRoot, "DmaStreamCompute*.cs", SearchOption.TopDirectoryOnly)
                .Append(Path.Combine(testsRoot, "Phase09Lane6DscContractClosureTests.cs"));

        foreach (string path in dscTestFiles)
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotContain(".IsValid", source, StringComparison.Ordinal);
        }

        string dscCoreRoot = Path.Combine(
            repoRoot,
            "HybridCPU_ISE",
            "CloseToHSL",
            "Core",
            "Execution",
            "DmaStreamCompute");
        string dscCoreSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(dscCoreRoot, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("validationResult.IsValid", dscCoreSource, StringComparison.Ordinal);
        Assert.DoesNotContain("structuralRead.IsValid", dscCoreSource, StringComparison.Ordinal);
        Assert.Contains("IsDescriptorAbiAccepted", dscCoreSource, StringComparison.Ordinal);
        Assert.Contains("IsStructuralDescriptorReadAccepted", dscCoreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveDatatypeValidationInCompilerAbiContracts_UsesEnumShapePredicates()
    {
        Assert.NotNull(typeof(CompilerMatrixTileDescriptorAbi).GetMethod(
            nameof(CompilerMatrixTileDescriptorAbi.IsKnownMatrixTileElementType),
            BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(typeof(CompilerVectorTransferShapeAbi).GetMethod(
            nameof(CompilerVectorTransferShapeAbi.IsKnownVectorElementType),
            BindingFlags.Public | BindingFlags.Static));

        Assert.True(CompilerMatrixTileDescriptorAbi.IsKnownMatrixTileElementType(HybridCpuDataType.INT8));
        Assert.False(CompilerMatrixTileDescriptorAbi.IsKnownMatrixTileElementType((HybridCpuDataType)0xFF));
        Assert.True(CompilerVectorTransferShapeAbi.IsKnownVectorElementType(HybridCpuDataType.FLOAT32));
        Assert.False(CompilerVectorTransferShapeAbi.IsKnownVectorElementType((HybridCpuDataType)0xFF));

        string matrixTileContract = ReadCompilerCoreIrFile(
            Path.Combine("Model", "CompilerMatrixTilePositiveEmissionAbiContract.cs"));
        string vectorTransferContract = ReadCompilerCoreIrFile(
            Path.Combine("Model", "CompilerVectorTransferPositiveEmissionAbiContract.cs"));

        Assert.DoesNotContain("DataTypeUtils.IsValid", matrixTileContract, StringComparison.Ordinal);
        Assert.DoesNotContain("DataTypeUtils.IsValid", vectorTransferContract, StringComparison.Ordinal);
        Assert.Contains("IsKnownMatrixTileElementType", matrixTileContract, StringComparison.Ordinal);
        Assert.Contains("IsKnownVectorElementType", vectorTransferContract, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataCompatibilityValidation_UsesSchemaCompatibilityPredicate()
    {
        AssertObsolete(typeof(ValidationResult).GetProperty(nameof(ValidationResult.IsValid)));
        Assert.NotNull(typeof(ValidationResult).GetProperty(
            nameof(ValidationResult.IsMetadataSchemaCompatible)));

        ValidationResult warning = ValidationResult.Warning("minor schema skew");
        ValidationResult error = ValidationResult.Error("unsupported schema");

        Assert.True(warning.IsMetadataSchemaCompatible);
        Assert.False(error.IsMetadataSchemaCompatible);

        string repoRoot = CompatFreezeScanner.FindRepoRoot();
        string phase05MetadataTests = File.ReadAllText(Path.Combine(
            repoRoot,
            "HybridCPU_ISE.Tests",
            "tests",
            "Phase05MetadataExtractionTests.cs"));
        string phase10MetadataTests = File.ReadAllText(Path.Combine(
            repoRoot,
            "HybridCPU_ISE.Tests",
            "tests",
            "Phase10MetadataModelTests.cs"));

        Assert.DoesNotContain(".IsValid", phase05MetadataTests, StringComparison.Ordinal);
        Assert.DoesNotContain(".IsValid", phase10MetadataTests, StringComparison.Ordinal);
        Assert.Contains("IsMetadataSchemaCompatible", phase05MetadataTests, StringComparison.Ordinal);
        Assert.Contains("IsMetadataSchemaCompatible", phase10MetadataTests, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLocalIdentityAndProjectionValidation_DoNotBecomeCompilerFacingAuthority()
    {
        Assert.NotNull(typeof(VmxDmaDescriptorValidationResult).GetProperty(
            nameof(VmxDmaDescriptorValidationResult.IsValid)));
        Assert.NotNull(typeof(AcceleratorTokenHandle).GetProperty(
            nameof(AcceleratorTokenHandle.IsValid)));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();

        Assert.DoesNotContain("VmxDmaDescriptorValidator", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VmxDmaDescriptorValidationResult", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AcceleratorTokenHandle", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Handle.IsValid", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NestedDomainCapabilityProjection", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VmxRetireEffect", compilerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLocalValidationOnlyIsValidSurfaces_DoNotBecomeCompilerFacingAuthority()
    {
        string dmaWindowDescriptor = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Runtime", "IO", "Dma", "DmaWindowDescriptor.cs"));
        string dmaDomainBinding = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Runtime", "IO", "Dma", "DmaDomainBinding.cs"));
        string domainValidation = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Runtime", "Domains", "Validation", "DomainValidationResult.cs"));
        string laneCompletionRouting = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Runtime", "Completion", "Routing", "LaneCompletionRouting.cs"));
        string eventInjectionDescriptor = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Runtime", "Events", "Injection", "EventInjectionDescriptor.cs"));
        string trapRequest = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Runtime", "Events", "Traps", "TrapRequest.cs"));
        string assistMicroOp = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Pipeline", "MicroOps", "Assist", "AssistMicroOp.cs"));
        string decodedBundleDescriptor = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Decoder", "DecodedBundleDescriptor.cs"));
        string fspRuntime = ReadRuntimeFile(
            Path.Combine("CloseToHSL", "Core", "Pipeline", "Scheduling", "Fsp", "CPU_Core.PipelineExecution.Fsp.cs"));

        Assert.Contains("public bool IsValid", dmaWindowDescriptor, StringComparison.Ordinal);
        Assert.Contains("public bool IsValid", dmaDomainBinding, StringComparison.Ordinal);
        Assert.Contains("public bool IsValid", domainValidation, StringComparison.Ordinal);
        Assert.Contains("public bool IsValid", laneCompletionRouting, StringComparison.Ordinal);
        Assert.Contains("public bool IsValid", eventInjectionDescriptor, StringComparison.Ordinal);
        Assert.Contains("public bool IsValid", trapRequest, StringComparison.Ordinal);
        Assert.Contains("transport.IsValid", assistMicroOp, StringComparison.Ordinal);
        Assert.Contains("public bool IsValid => MicroOp != null", decodedBundleDescriptor, StringComparison.Ordinal);
        Assert.Contains("slotDescriptor.IsValid", fspRuntime, StringComparison.Ordinal);

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] runtimeLocalSurfaceNames =
        [
            "DmaWindowDescriptor",
            "IommuDomainBinding",
            "DomainValidationResult",
            "LaneCompletionDescriptor",
            "LaneCompletionRoutingResult",
            "EventInjectionDescriptor",
            "MemoryTrapRange",
            "TrapRequest",
            "AssistInterCoreTransport",
            "DecodedBundleDescriptor",
            "DomainLegalityService",
            "CompletionRoutingService",
            "EventDeliveryService"
        ];

        foreach (string surfaceName in runtimeLocalSurfaceNames)
        {
            Assert.DoesNotContain(surfaceName, compilerSource, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("slotDescriptor.IsValid", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("transport.IsValid", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Validation.IsValid", compilerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalPublicApiScan_BareAuthorityNamesAreObsoleteCompatibilityShims()
    {
        AssertObsolete(typeof(HybridCpuCompilerDirectives.DirectiveParseResult).GetProperty(
            nameof(HybridCpuCompilerDirectives.DirectiveParseResult.Success)));
        AssertObsolete(typeof(CompilerVmxPreflightResult).GetProperty("Success"));
        AssertObsolete(typeof(IrCandidateBundleAnalysis).GetProperty(nameof(IrCandidateBundleAnalysis.IsLegal)));
        AssertObsolete(typeof(IrBundleLegalityResult).GetProperty(nameof(IrBundleLegalityResult.IsLegal)));
        AssertObsolete(typeof(IrBundleLegalityResult).GetProperty(nameof(IrBundleLegalityResult.Legal)));
        AssertObsolete(typeof(IrMaterializedBundleSlot).GetProperty(nameof(IrMaterializedBundleSlot.IsLegalPlacement)));

        Assert.NotNull(typeof(HybridCpuCompilerDirectives.DirectiveParseResult).GetProperty(
            nameof(HybridCpuCompilerDirectives.DirectiveParseResult.IsDirectiveParsed)));
        Assert.NotNull(typeof(CompilerVmxPreflightResult).GetProperty(
            nameof(CompilerVmxPreflightResult.ProjectionPreflightPassed)));
        Assert.NotNull(typeof(IrCandidateBundleAnalysis).GetProperty(nameof(IrCandidateBundleAnalysis.IsStructurallyAdmissible)));
        Assert.NotNull(typeof(IrBundleLegalityResult).GetProperty(nameof(IrBundleLegalityResult.IsStructurallyAdmissible)));
        Assert.NotNull(typeof(IrMaterializedBundleSlot).GetProperty(nameof(IrMaterializedBundleSlot.IsStructuralPlacement)));

        AssertAllPublicOverloadsObsolete(typeof(HybridCpuThreadCompilerContext), nameof(HybridCpuThreadCompilerContext.CompileMtileLoad));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuThreadCompilerContext), nameof(HybridCpuThreadCompilerContext.CompileMtileStore));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuThreadCompilerContext), nameof(HybridCpuThreadCompilerContext.CompileMtileMacc));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuThreadCompilerContext), nameof(HybridCpuThreadCompilerContext.CompileMtranspose));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuThreadCompilerContext), nameof(HybridCpuThreadCompilerContext.CompileVload));
        AssertAllPublicOverloadsObsolete(typeof(HybridCpuThreadCompilerContext), nameof(HybridCpuThreadCompilerContext.CompileVstore));

        Assert.NotNull(typeof(HybridCpuThreadCompilerContext).GetMethod(nameof(HybridCpuThreadCompilerContext.CompileMtileLoadWithDecision)));
        Assert.NotNull(typeof(HybridCpuThreadCompilerContext).GetMethod(nameof(HybridCpuThreadCompilerContext.CompileMtileStoreWithDecision)));
        Assert.NotNull(typeof(HybridCpuThreadCompilerContext).GetMethod(nameof(HybridCpuThreadCompilerContext.CompileMtileMaccWithDecision)));
        Assert.NotNull(typeof(HybridCpuThreadCompilerContext).GetMethod(nameof(HybridCpuThreadCompilerContext.CompileMtransposeWithDecision)));
        Assert.NotNull(typeof(HybridCpuThreadCompilerContext).GetMethod(nameof(HybridCpuThreadCompilerContext.CompileVloadWithDecision)));
        Assert.NotNull(typeof(HybridCpuThreadCompilerContext).GetMethod(nameof(HybridCpuThreadCompilerContext.CompileVstoreWithDecision)));
    }

    [Fact]
    public void PublicLoweringDecisionTypes_DoNotExposeBareAuthorityBoolNames()
    {
        string[] forbiddenBoolNames =
        [
            "Success",
            "Valid",
            "Accepted",
            "IsLegal",
            "IsValid",
            "IsAccepted",
            "CanExecute"
        ];

        Type[] loweringTypes = typeof(CompilerLoweringDecision).Assembly.GetTypes()
            .Where(t => t.IsPublic &&
                        t.Namespace is not null &&
                        t.Namespace.StartsWith("HybridCPU.Compiler.Core.IR.Lowering", StringComparison.Ordinal))
            .ToArray();

        foreach (Type type in loweringTypes)
        {
            IEnumerable<PropertyInfo> boolProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.PropertyType == typeof(bool));

            foreach (PropertyInfo property in boolProperties)
            {
                Assert.DoesNotContain(property.Name, forbiddenBoolNames);
            }
        }
    }

    private static void AssertObsolete(MemberInfo? member)
    {
        Assert.NotNull(member);
        ObsoleteAttribute? obsolete = member!.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);
        Assert.False(obsolete!.IsError);
    }

    private static void AssertAllPublicOverloadsObsolete(Type type, string methodName, int? parameterCount = null)
    {
        MethodInfo[] methods = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .Where(method => !parameterCount.HasValue || method.GetParameters().Length == parameterCount.Value)
            .ToArray();

        Assert.NotEmpty(methods);
        foreach (MethodInfo method in methods)
        {
            AssertObsolete(method);
        }
    }

    private static void AssertFacadeKind(
        string memberKey,
        HybridCpuThreadCompilerFacadeBoundaryKind expectedKind)
    {
        Assert.True(
            HybridCpuThreadCompilerFacadeAudit.TryGetRow(memberKey, out HybridCpuThreadCompilerFacadeAuditRow? row),
            $"Missing facade audit row for {memberKey}.");
        Assert.Equal(expectedKind, row.BoundaryKind);
        Assert.DoesNotContain("grants runtime authority", row.AuthoritySemantics, StringComparison.OrdinalIgnoreCase);
    }

    private static MethodInfo ResolveFacadeMethod(string memberKey)
    {
        MethodInfo? method = typeof(HybridCpuThreadCompilerContext)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SingleOrDefault(candidate => CreateFacadeMemberKey(candidate) == memberKey);
        Assert.NotNull(method);
        return method!;
    }

    private static string CreateFacadeMemberKey(MethodInfo method) =>
        $"{method.Name}/{method.GetParameters().Length}";

    private static void AssertPositiveHelperEmissionDecision(
        CompilerLoweringDecision decision,
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind)
    {
        Assert.Equal(CompilerLoweringDecisionKind.HelperAbiOnly, decision.DecisionKind);
        Assert.Equal(intentKind, decision.IntentKind);
        Assert.Equal(contourKind, decision.ContourKind);
        Assert.Equal(CompilerEmissionClass.CarrierCandidate, decision.EmissionClass);
        Assert.Equal(CompilerProductionLoweringStatus.HelperAbiOnly, decision.ProductionLoweringStatus);
        Assert.Equal(CompilerAuthorityClass.TransportConstruction, decision.AuthorityClass);
        Assert.Equal(CompilerAuthoritySourceKind.CompilerAbiValidator, decision.AuthoritySourceKind);
        Assert.Equal(CompilerExecutionClaim.HelperOnly, decision.ExecutionClaim);
        Assert.Equal(CompilerPublicationClass.CarrierBytesOnly, decision.PublicationClass);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Contains(CompilerProducedArtifactKind.Carrier, decision.ProducedArtifacts);
        Assert.Contains(CompilerProducedArtifactKind.Evidence, decision.ProducedArtifacts);
        Assert.Contains(CompilerRequiredArtifactKind.RuntimeLegalityA, decision.RequiredArtifacts);
        Assert.Contains(CompilerRequiredArtifactKind.RuntimeLegalityB, decision.RequiredArtifacts);
        Assert.Contains(CompilerRequiredArtifactKind.RuntimeCommit, decision.RequiredArtifacts);
        Assert.Contains(CompilerRequiredArtifactKind.RuntimeRetire, decision.RequiredArtifacts);
        Assert.Contains(CompilerRequiredArtifactKind.RuntimePublication, decision.RequiredArtifacts);
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimePublicationRequired));
        Assert.Contains(CompilerRejectReason.HelperAbiOnly, decision.RejectReasons);
    }

    private static string ReadCompilerCoreIrFile(string coreIrRelativePath)
    {
        string expectedSuffix = Path.Combine("HybridCPU_Compiler", "Core", "IR", coreIrRelativePath);
        string? path = CompilerSourceScanner.EnumerateCompilerSourceFiles()
            .SingleOrDefault(file => file.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(path), $"Compiler source file not found: {expectedSuffix}");
        return File.ReadAllText(path!);
    }

    private static string ReadCompilerApiFile(string apiRelativePath)
    {
        string expectedSuffix = Path.Combine("HybridCPU_Compiler", "API", apiRelativePath);
        string? path = CompilerSourceScanner.EnumerateCompilerSourceFiles()
            .SingleOrDefault(file => file.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(path), $"Compiler API source file not found: {expectedSuffix}");
        return File.ReadAllText(path!);
    }

    private static string ReadCompilerFile(string compilerRelativePath)
    {
        string expectedSuffix = Path.Combine("HybridCPU_Compiler", compilerRelativePath);
        string? path = CompilerSourceScanner.EnumerateCompilerSourceFiles()
            .SingleOrDefault(file => file.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(path), $"Compiler source file not found: {expectedSuffix}");
        return File.ReadAllText(path!);
    }

    private static string ReadRuntimeFile(string runtimeRelativePath)
    {
        string repoRoot = CompatFreezeScanner.FindRepoRoot();
        string path = Path.Combine(repoRoot, "HybridCPU_ISE", runtimeRelativePath);

        Assert.True(File.Exists(path), $"Runtime source file not found: {path}");
        return File.ReadAllText(path);
    }
}
