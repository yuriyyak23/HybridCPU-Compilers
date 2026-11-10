using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Bridge;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Evidence;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Contracts;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using CompilerTypedSlotFactStaging = HybridCPU.Compiler.Core.IR.Bridge.TypedSlotFactStaging;
using CompilerTypedSlotPolicyMode = HybridCPU.Compiler.Core.IR.IrTypedSlotPolicyMode;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerCoreAuthorityBoundaryNegativeTests
{
    [Fact]
    public void CompilerAuthorityTaxonomy_DoesNotExposeRuntimeAuthorityClaims()
    {
        string[] forbiddenNames =
        [
            "Executable",
            "RuntimeLegal",
            "ExecutionReady",
            "Committed",
            "Retired",
            "PublishedArchitecturalState",
            "CapabilityAuthority",
            "CanExecute"
        ];

        Type[] taxonomyTypes =
        [
            typeof(CompilerAuthorityClass),
            typeof(CompilerAuthoritySourceKind),
            typeof(CompilerRuntimeAuthorityDependency),
            typeof(CompilerEvidenceClass),
            typeof(CompilerPublicationClass),
            typeof(CompilerExecutionClaim)
        ];

        foreach (Type taxonomyType in taxonomyTypes)
        {
            string[] enumNames = Enum.GetNames(taxonomyType);
            foreach (string forbiddenName in forbiddenNames)
            {
                Assert.DoesNotContain(forbiddenName, enumNames);
            }
        }
    }

    [Fact]
    public void CompilerCoreResultHeader_TransportHeaderKeepsRuntimeLegalityDependencyExplicit()
    {
        CompilerCoreResultHeader header =
            CompilerCoreResultHeader.TransportRequiresRuntimeLegality(
                CompilerAuthorityClass.TransportConstruction,
                CompilerAuthoritySourceKind.CompilerCarrierSerializer,
                CompilerEvidenceClass.StructuralEvidence,
                CompilerPublicationClass.CarrierBytesOnly);

        Assert.Equal(CompilerAuthorityClass.TransportConstruction, header.AuthorityClass);
        Assert.Equal(CompilerExecutionClaim.RuntimeExecutionRequired, header.ExecutionClaim);
        Assert.True(header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.False(header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.False(header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
        Assert.Equal(CompilerPublicationClass.CarrierBytesOnly, header.PublicationClass);
    }

    [Fact]
    public void LegacyBundleLegalityWrapper_QuarantinesResultAsStructuralAdmissionOnly()
    {
        CompilerStructuralBundleAdmissionResult wrapper =
            CompilerStructuralAuthorityQuarantine.FromBundleLegalityResult(
                IrBundleLegalityResult.Legal,
                "EvaluateCandidateBundle");

        Assert.True(wrapper.IsStructurallyAdmissible);
        Assert.Equal(0, wrapper.HazardCount);
        Assert.Equal(CompilerAuthorityClass.StructuralAdmissionEvidence, wrapper.Header.AuthorityClass);
        Assert.Equal(CompilerAuthoritySourceKind.CompilerStructuralModel, wrapper.Header.AuthoritySourceKind);
        Assert.Equal(CompilerEvidenceClass.StructuralAdmissionEvidence, wrapper.Header.EvidenceClass);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, wrapper.Header.PublicationClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, wrapper.Header.ExecutionClaim);
        Assert.True(wrapper.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(wrapper.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.False(wrapper.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.False(wrapper.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
        Assert.Null(typeof(CompilerStructuralBundleAdmissionResult).GetProperty("IsLegal"));
        Assert.NotNull(typeof(CompilerStructuralBundleAdmissionResult).GetProperty("IsStructurallyAdmissible"));
    }

    [Fact]
    public void LegacySlotAssignmentWrapper_QuarantinesResultAsStructuralPlacementOnly()
    {
        IrSlotAssignmentAnalysis legacyAnalysis = new IrSlotAssignmentAnalysis(
            CandidateInstructionCount: 1,
            CombinedLegalSlots: IrIssueSlotMask.Scalar,
            DistinctLegalSlotCount: 4,
            HasLegalAssignment: true,
            InstructionLegalSlots: [IrIssueSlotMask.Scalar]);

        CompilerStructuralPlacementReport wrapper =
            CompilerStructuralAuthorityQuarantine.FromSlotAssignmentAnalysis(
                legacyAnalysis,
                "HasLegalAssignment");

        Assert.True(wrapper.HasStructuralPlacement);
        Assert.Equal(IrIssueSlotMask.Scalar, wrapper.StructurallyAllowedSlots);
        Assert.Single(wrapper.InstructionStructurallyAllowedSlots);
        Assert.Equal(CompilerAuthorityClass.StructuralPlacementEvidence, wrapper.Header.AuthorityClass);
        Assert.Equal(CompilerEvidenceClass.StructuralPlacementEvidence, wrapper.Header.EvidenceClass);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, wrapper.Header.PublicationClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, wrapper.Header.ExecutionClaim);
        Assert.True(wrapper.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(wrapper.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.Null(typeof(CompilerStructuralPlacementReport).GetProperty("HasLegalAssignment"));
        Assert.NotNull(typeof(CompilerStructuralPlacementReport).GetProperty("HasStructuralPlacement"));
        Assert.Null(typeof(CompilerStructuralPlacementReport).GetProperty("LegalSlots"));
        Assert.NotNull(typeof(CompilerStructuralPlacementReport).GetProperty("StructurallyAllowedSlots"));
    }

    [Fact]
    public void IntentAndContourTypes_RemainSeparateAndDoNotExposeCombinedClassification()
    {
        Assert.Null(typeof(CompilerSemanticIntent).GetProperty("PreferredContour"));
        Assert.Null(typeof(CompilerSemanticIntent).GetProperty("CompilerMayEmitCarrier"));
        Assert.Null(typeof(CompilerSemanticIntent).GetProperty("CompilerMayEmitDescriptor"));
        Assert.Null(typeof(CompilerSemanticIntent).GetProperty("MayPublishMemory"));
        Assert.Null(typeof(CompilerSemanticIntent).GetProperty("MayPublishRegisterState"));
        Assert.Null(typeof(CompilerExecutionContourSelection).GetProperty("SemanticIntent"));
        Assert.Null(Type.GetType("HybridCPU.Compiler.Core.IR.SemanticIntentClassification, HybridCPU_Compiler"));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        Assert.DoesNotContain("SemanticIntentClassification", compilerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownIntent_SelectsUnknownRejectedAndCannotFallbackToScalar()
    {
        CompilerSemanticIntent intent = CompilerSemanticIntent.Unknown("unrecognized opcode family");
        CompilerExecutionContourSelection selection =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(intent);

        Assert.Equal(ExecutionContourKind.UnknownRejected, selection.Kind);
        Assert.False(selection.IsKnownContour);
        Assert.True(selection.IsEmissionForbidden);
        Assert.True(selection.IsFallbackForbidden);
        Assert.Contains("scalar fallback is forbidden", selection.NoFallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(selection.RequiresRuntimeLegalityA);
        Assert.False(selection.RequiresRuntimeLegalityB);
        Assert.Equal(CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission, selection.RuntimeDependency);
    }

    [Fact]
    public void MatrixTileIntent_SelectsHelperOnlyContourWithNoCrossContourFallback()
    {
        CompilerSemanticIntent intent = new CompilerSemanticIntent(
            SemanticIntentKind.MatrixTile,
            "MatrixTile",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: true,
            IsParserOnly: false,
            "matrix helper ABI");

        CompilerExecutionContourSelection selection =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(intent);

        Assert.Equal(ExecutionContourKind.MatrixTileHelperOnly, selection.Kind);
        Assert.True(selection.IsKnownContour);
        Assert.False(selection.IsEmissionForbidden);
        Assert.True(selection.RequiresSideband);
        Assert.False(selection.RequiresDescriptor);
        Assert.Equal(CompilerSidebandRequirement.PolicySidebandRequired, selection.SidebandRequirement);
        Assert.True(selection.RequiresRuntimeLegalityA);
        Assert.True(selection.RequiresRuntimeLegalityB);
        Assert.True(selection.IsFallbackForbidden);
        Assert.Contains("scalar/vector/Stream fallback is forbidden", selection.SelectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void DscAndL7Intents_SelectDistinctDescriptorContoursWithoutFallback()
    {
        CompilerSemanticIntent dscIntent = new CompilerSemanticIntent(
            SemanticIntentKind.DmaStreamCompute,
            "DmaStreamCompute",
            RequiresDescriptor: true,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "lane6 descriptor contour");

        CompilerSemanticIntent l7Intent = dscIntent with
        {
            Kind = SemanticIntentKind.ExternalAcceleratorCommand,
            OpcodeFamily = "L7Sdc",
            RequiresToken = true,
            Reason = "lane7 descriptor contour"
        };

        CompilerExecutionContourSelection dsc =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(dscIntent);
        CompilerExecutionContourSelection l7 =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(l7Intent);

        Assert.Equal(ExecutionContourKind.DmaStreamComputeLane6, dsc.Kind);
        Assert.Equal(ExecutionContourKind.L7SdcLane7, l7.Kind);
        Assert.True(dsc.RequiresDescriptor);
        Assert.True(l7.RequiresDescriptor);
        Assert.True(dsc.IsFallbackForbidden);
        Assert.True(l7.IsFallbackForbidden);
        Assert.Contains("L7/Stream/scalar fallback is forbidden", dsc.SelectionReason, StringComparison.Ordinal);
        Assert.Contains("descriptorless submit fails closed", l7.SelectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void VmxAndSecureComputeIntents_SelectNoEmissionContoursOnly()
    {
        CompilerSemanticIntent vmxIntent = new CompilerSemanticIntent(
            SemanticIntentKind.VmxCompatibilityProjection,
            "VMX",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: false,
            IsCompatibilityProjection: true,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: true,
            "vmx projection");

        CompilerSemanticIntent secureIntent = new CompilerSemanticIntent(
            SemanticIntentKind.SecureComputeAdmission,
            "SecureCompute",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: false,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: true,
            IsHelperAbiOnly: false,
            IsParserOnly: true,
            "secure admission");

        CompilerExecutionContourSelection vmx =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(vmxIntent);
        CompilerExecutionContourSelection secure =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(secureIntent);

        Assert.Equal(ExecutionContourKind.VmxProjectionOnly, vmx.Kind);
        Assert.Equal(ExecutionContourKind.SecureComputePolicyAdmissionOnly, secure.Kind);
        Assert.True(vmx.IsEmissionForbidden);
        Assert.True(secure.IsEmissionForbidden);
        Assert.False(vmx.RequiresRuntimeLegalityA);
        Assert.False(secure.RequiresRuntimeLegalityA);
        Assert.True(vmx.IsFallbackForbidden);
        Assert.True(secure.IsFallbackForbidden);
        Assert.Contains("VMCS is not compiler-owned state", vmx.SelectionReason, StringComparison.Ordinal);
        Assert.Contains("secure backend execution is forbidden", secure.SelectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void LoweringRejectDecision_CarriesTypedAuthorityAndNoFallbackProof()
    {
        CompilerLoweringDecision decision = CompilerLoweringDecision.Reject(
            SemanticIntentKind.Unknown,
            ExecutionContourKind.UnknownRejected,
            CompilerRejectReason.UnknownContour,
            "unknown contour rejected before lowering");

        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
        Assert.Equal(CompilerProductionLoweringStatus.Rejected, decision.ProductionLoweringStatus);
        Assert.Equal(CompilerAuthorityClass.CompilerEvidenceProduction, decision.AuthorityClass);
        Assert.Equal(CompilerExecutionClaim.ParserOnly, decision.ExecutionClaim);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, decision.PublicationClass);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Contains(CompilerRejectReason.UnknownContour, decision.RejectReasons);
        Assert.Empty(decision.ProducedArtifacts);
        Assert.Equal(CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission, decision.RuntimeAuthorityDependency);
    }

    [Fact]
    public void LegacyApiTranslation_RejectsAuthorityStrengthening()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => LegacyApiTranslation.Create(
                "HasLegalAssignment",
                "attempted runtime legality conversion",
                sourceValue: true,
                strengthensAuthority: true));

        Assert.Contains("cannot be translated into stronger runtime authority", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyStructuralBoolDecision_DoesNotExportRuntimeLegalityOrProductionLowering()
    {
        CompilerLoweringDecision decision = CompilerLoweringDecision.FromLegacyStructuralBool(
            sourceValue: true,
            sourceApi: "IrCandidateBundleAnalysis.IsLegal",
            SemanticIntentKind.ScalarAlu,
            ExecutionContourKind.NativeVliwScalar,
            "legacy structural admission adapter");

        Assert.Equal(CompilerLoweringDecisionKind.StructuralOnly, decision.DecisionKind);
        Assert.Equal(CompilerProductionLoweringStatus.NotProductionLowering, decision.ProductionLoweringStatus);
        Assert.Equal(CompilerEmissionClass.EvidenceOnly, decision.EmissionClass);
        Assert.Equal(CompilerAuthorityClass.StructuralAdmissionEvidence, decision.AuthorityClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, decision.ExecutionClaim);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, decision.PublicationClass);
        Assert.NotNull(decision.LegacyTranslation);
        Assert.False(decision.LegacyTranslation.StrengthensAuthority);
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(decision.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.Contains(CompilerProducedArtifactKind.Evidence, decision.ProducedArtifacts);
        Assert.Contains(CompilerRequiredArtifactKind.RuntimeLegalityA, decision.RequiredArtifacts);
        Assert.True(decision.NoFallbackProof.PolicyKind == FallbackPolicyKind.Forbidden);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
    }

    [Fact]
    public void CompiledProgramEnvelopeAdapter_SeparatesCarrierSidebandAgreementAndBridge()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.Nop();
#pragma warning restore CS0618

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        CompilerEmissionPackage package =
            HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
                compiledProgram,
                CompilerArtifactProjectionOptions.Compatibility(
                    "CompilerCoreAuthorityBoundaryNegativeTests"));

        Assert.NotNull(package.Carrier);
        Assert.NotNull(package.Sideband);
        Assert.NotNull(package.StructuralAgreement);
        Assert.NotNull(package.TypedSlotFacts);
        Assert.NotNull(package.RuntimeBridgeInput);
        Assert.NotNull(package.Descriptor);
        Assert.Equal(compiledProgram.ProgramImage, package.Carrier!.Image.SerializedImage);
        Assert.Equal(compiledProgram.BundleCount, package.Carrier.Image.Bundles.Count);
        Assert.Equal(compiledProgram.ContractVersion, package.Identity.CompilerContractVersion);
        Assert.Equal(compiledProgram.ContractVersion, package.RuntimeBridgeInput!.CompilerContractVersion);
        Assert.True(package.SeparationProof.CarrierSeparatedFromSideband);
        Assert.True(package.SeparationProof.DescriptorSeparatedFromAuthority);
        Assert.True(package.SeparationProof.TypedSlotFactsSeparatedFromLegality);
        Assert.True(package.SeparationProof.BridgeSeparatedFromExecution);
        Assert.Equal(CompilerExecutionClaim.RuntimeExecutionRequired, package.Carrier.Header.ExecutionClaim);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, package.Sideband!.Header.ExecutionClaim);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, package.StructuralAgreement!.Header.ExecutionClaim);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, package.TypedSlotFacts!.Header.ExecutionClaim);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, package.RuntimeBridgeInput.Header.ExecutionClaim);
        Assert.True(package.StructuralAgreement.RuntimeLegalityStillRequired);
        Assert.True(package.TypedSlotFacts.RuntimeLegalityStillRequired);
        Assert.True(package.RuntimeBridgeInput.RuntimeLegalityAStillRequired);
        Assert.True(package.RuntimeBridgeInput.RuntimeLegalityBStillRequired);
        Assert.True(package.RuntimeBridgeInput.RuntimeCommitStillRequired);
        Assert.True(package.RuntimeBridgeInput.RuntimeRetireStillRequired);
        Assert.True(package.RuntimeBridgeInput.RuntimePublicationStillRequired);
        Assert.Equal(CompilerContract.Version, package.RuntimeBridgeInput.RuntimeContractVersionObservedAtBuild);
        Assert.Equal(NativeTransportRuntimeAdapter.ToCore(CompilerContract.CurrentTypedSlotPolicy.Mode), package.RuntimeBridgeInput.RuntimePolicyModeObserved);
        Assert.Equal(NativeTransportRuntimeAdapter.ToCore(CompilerContract.CurrentTypedSlotPolicy.Mode), package.TypedSlotFacts.RuntimePolicyModeObserved);
        Assert.True(package.TypedSlotFacts.StructuralEvidenceOnly);
        Assert.Equal(DescriptorAbiStatus.None, package.Descriptor!.Status);
        Assert.Empty(package.Descriptor.Descriptors);
        Assert.DoesNotContain(
            package.SeparationProof.Notes,
            note => note.Contains("published", StringComparison.OrdinalIgnoreCase));

        CompilerArtifactValidationResult carrierValidation =
            CarrierImageValidator.Instance.Validate(package.Carrier);
        CompilerArtifactValidationResult sidebandValidation =
            SidebandEnvelopeValidator.Instance.Validate(package.Sideband);
        CompilerArtifactValidationResult descriptorValidation =
            DescriptorEnvelopeValidator.Instance.Validate(package.Descriptor);
        CompilerArtifactValidationResult typedSlotValidation =
            TypedSlotFactsEnvelopeValidator.Instance.Validate(package.TypedSlotFacts);
        CompilerArtifactValidationResult separationValidation =
            EmissionPackageSeparationValidator.Instance.Validate(package);

        CompilerArtifactValidationResult[] validationResults =
        [
            carrierValidation,
            sidebandValidation,
            descriptorValidation,
            typedSlotValidation,
            separationValidation
        ];

        foreach (CompilerArtifactValidationResult validation in validationResults)
        {
            Assert.True(validation.IsAuthorityScopedValidation);
            Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, validation.ExecutionClaim);
            Assert.True(validation.RuntimeLegalityStillRequired);
        }

        BridgeAcceptanceReport typedSlotFactsReport =
            CompilerRuntimeBridge.Instance.AcceptTypedSlotFacts(package.TypedSlotFacts);

        Assert.True(
            typedSlotFactsReport.Status is BridgeIngressStatus.BridgeIngressAccepted
                or BridgeIngressStatus.CompatibilityAcceptedMissingFacts
                or BridgeIngressStatus.CompatibilityRecordedWithoutValidation);
        Assert.True(typedSlotFactsReport.RuntimeLegalityAStillRequired);
        Assert.True(typedSlotFactsReport.RuntimeLegalityBStillRequired);
        Assert.True(typedSlotFactsReport.RuntimeCommitStillRequired);
        Assert.True(typedSlotFactsReport.RuntimeRetireStillRequired);
        Assert.True(typedSlotFactsReport.RuntimePublicationStillRequired);
    }

    [Fact]
    public void DescriptorEnvelope_DoesNotExposeExecutionPublicationCommitOrRetireAuthority()
    {
        string[] forbiddenProperties =
        [
            "Executable",
            "CanExecute",
            "IsLegal",
            "RuntimeLegal",
            "Commit",
            "Retire",
            "PublicationAuthority",
            "CapabilityAuthority"
        ];

        string[] propertyNames = typeof(DescriptorEnvelope)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        foreach (string forbiddenProperty in forbiddenProperties)
        {
            Assert.DoesNotContain(forbiddenProperty, propertyNames);
        }

        DescriptorEnvelope descriptor = new(
            ExecutionContourKind.L7SdcLane7,
            DescriptorAbiStatus.ValidTransportDescriptor,
            Array.Empty<object>(),
            SidebandRequirement.RequiredForDescriptorSubmit,
            Array.Empty<string>(),
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.DescriptorAbiConstruction,
                CompilerAuthoritySourceKind.CompilerAbiValidator,
                CompilerEvidenceClass.DescriptorAbiEvidence,
                CompilerPublicationClass.DescriptorOnly,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));

        Assert.Equal(DescriptorAbiStatus.ValidTransportDescriptor, descriptor.Status);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, descriptor.Header.ExecutionClaim);
        Assert.True(descriptor.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(descriptor.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.False(descriptor.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.False(descriptor.Header.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
    }

    [Fact]
    public void BridgeIngressStatus_DoesNotExposeRuntimeLegalityOrExecutionReadyClaims()
    {
        string[] forbiddenNames =
        [
            "RuntimeLegal",
            "ExecutionReady",
            "CanExecute",
            "Committed",
            "Retired",
            "PublishedArchitecturalState"
        ];

        string[] statusNames = Enum.GetNames<BridgeIngressStatus>();
        foreach (string forbiddenName in forbiddenNames)
        {
            Assert.DoesNotContain(forbiddenName, statusNames);
        }
    }

    [Fact]
    public void BridgeAcceptedAndValidatedTypedSlotFacts_StillRequireRuntimeStageAAndB()
    {
        TypedSlotFactsEnvelope factsEnvelope = new TypedSlotFactsEnvelope(
            [new TypedSlotBundleFacts { AluCount = 1, FlexibleOpCount = 1 }],
            NativeTransportRuntimeAdapter.ToCore(CompilerContract.CurrentTypedSlotPolicy.Mode),
            CompilerTypedSlotFactStaging.PresentValidated,
            BundleCount: 1,
            TypedSlotFactBundleCount: 1,
            StructuralEvidenceOnly: true,
            RuntimeLegalityStillRequired: true,
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.TypedSlotFactProduction,
                CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference,
                CompilerEvidenceClass.TypedSlotEvidence,
                CompilerPublicationClass.FactsOnly,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));

        BridgeAcceptanceReport report =
            CompilerRuntimeBridge.Instance.AcceptTypedSlotFacts(factsEnvelope);

        Assert.Equal(BridgeIngressStatus.BridgeIngressAccepted, report.Status);
        Assert.True(report.RuntimeLegalityAStillRequired);
        Assert.True(report.RuntimeLegalityBStillRequired);
        Assert.True(report.RuntimeCommitStillRequired);
        Assert.True(report.RuntimeRetireStillRequired);
        Assert.True(report.RuntimePublicationStillRequired);
        Assert.True(factsEnvelope.StructuralEvidenceOnly);
        Assert.True(factsEnvelope.RuntimeLegalityStillRequired);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, factsEnvelope.Header.ExecutionClaim);
    }

    [Fact]
    public void MissingTypedSlotFactsCompatibility_IsWeakerThanValidatedFacts()
    {
        TypedSlotFactsEnvelope missingFactsEnvelope = new TypedSlotFactsEnvelope(
            Array.Empty<TypedSlotBundleFacts>(),
            NativeTransportRuntimeAdapter.ToCore(CompilerContract.CurrentTypedSlotPolicy.Mode),
            CompilerTypedSlotFactStaging.MissingCompatibility,
            BundleCount: 1,
            TypedSlotFactBundleCount: 0,
            StructuralEvidenceOnly: true,
            RuntimeLegalityStillRequired: true,
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.TypedSlotFactProduction,
                CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference,
                CompilerEvidenceClass.TypedSlotEvidence,
                CompilerPublicationClass.FactsOnly,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));

        BridgeAcceptanceReport report =
            CompilerRuntimeBridge.Instance.AcceptTypedSlotFacts(missingFactsEnvelope);

        Assert.Equal(BridgeIngressStatus.CompatibilityAcceptedMissingFacts, report.Status);
        Assert.True(report.RuntimeLegalityAStillRequired);
        Assert.True(report.RuntimeLegalityBStillRequired);
        Assert.Contains("weaker than validated facts", report.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(missingFactsEnvelope.StructuralEvidenceOnly);
        Assert.True(missingFactsEnvelope.RuntimeLegalityStillRequired);
    }

    [Fact]
    public void FutureRequiredForAdmissionPolicy_RemainsRejectedCompilerSideBridgeStaging()
    {
        TypedSlotFactsEnvelope factsEnvelope = new TypedSlotFactsEnvelope(
            Array.Empty<TypedSlotBundleFacts>(),
            CompilerTypedSlotPolicyMode.RequiredForAdmission,
            CompilerTypedSlotFactStaging.FutureRequiredForAdmission,
            BundleCount: 0,
            TypedSlotFactBundleCount: 0,
            StructuralEvidenceOnly: true,
            RuntimeLegalityStillRequired: true,
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.TypedSlotFactProduction,
                CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference,
                CompilerEvidenceClass.TypedSlotEvidence,
                CompilerPublicationClass.FactsOnly,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission));

        BridgeAcceptanceReport report =
            CompilerRuntimeBridge.Instance.AcceptTypedSlotFacts(factsEnvelope);

        Assert.Equal(BridgeIngressStatus.BridgeIngressRejected, report.Status);
        Assert.True(report.RuntimeLegalityAStillRequired);
        Assert.True(report.RuntimeLegalityBStillRequired);
        Assert.Contains("future runtime-owned policy", report.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContourProviderRegistry_UnknownContourFailsClosedWithoutScalarFallback()
    {
        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.None);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.None);

        CompilerSemanticIntent intent = CompilerSemanticIntent.Unknown("unknown contour test");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("test"),
            "CompilerCoreAuthorityBoundaryNegativeTests");

        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.Equal(ExecutionContourKind.UnknownRejected, analyzer.ContourKind);
        Assert.Equal(ExecutionContourKind.UnknownRejected, provider.ContourKind);
        Assert.Equal(ExecutionContourKind.UnknownRejected, analysis.ContourKind);
        Assert.False(analysis.ProviderAvailable);
        Assert.Contains(CompilerRejectReason.UnknownContour, analysis.RejectReasons);
        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(ExecutionContourKind.UnknownRejected, decision.ContourKind);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.FallbackPolicy.Kind);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
    }

    [Fact]
    public void ContourAnalyzerAndProvider_AreSeparateAndDoNotBypassAnalysis()
    {
        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.MatrixTileHelperOnly);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.MatrixTileHelperOnly);

        CompilerSemanticIntent intent = new CompilerSemanticIntent(
            SemanticIntentKind.MatrixTile,
            "MatrixTile",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: true,
            IsParserOnly: false,
            "matrix helper ABI");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("test"),
            "CompilerCoreAuthorityBoundaryNegativeTests");

        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.Equal(ExecutionContourKind.MatrixTileHelperOnly, analysis.ContourKind);
        Assert.Equal(CompilerCapabilityObservationState.HelperOnly, analysis.CapabilityObservation.State);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, analysis.Evidence.Header.ExecutionClaim);
        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerRejectReason.RuntimeAuthorityOwned, Assert.Single(decision.RejectReasons));
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
        Assert.Contains("does not perform production lowering", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ContourProvider_CrossContourAnalysisRejectsInsteadOfFallback()
    {
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.MatrixTileHelperOnly);
        IContourAnalyzer wrongAnalyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.StreamEngineVector);

        CompilerSemanticIntent intent = new CompilerSemanticIntent(
            SemanticIntentKind.MatrixTile,
            "MatrixTile",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: true,
            IsParserOnly: false,
            "matrix helper ABI");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("test"),
            "CompilerCoreAuthorityBoundaryNegativeTests");

        ContourAnalysisReport wrongAnalysis = wrongAnalyzer.Analyze(intent, context);
        CompilerLoweringDecision decision = provider.Lower(intent, wrongAnalysis, context);

        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Contains(CompilerRejectReason.CrossContourFallbackForbidden, decision.RejectReasons);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Contains("cross-contour fallback is forbidden", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VmxAndSecureComputeProviders_ObserveNoEmissionOnly()
    {
        CompilerTargetProfile target = new CompilerTargetProfile("test");
        CompilerCapabilityObservation vmx =
            DefaultContourLoweringProviderRegistry.Instance
                .ResolveProvider(ExecutionContourKind.VmxProjectionOnly)
                .ObserveCapability(target);
        CompilerCapabilityObservation secure =
            DefaultContourLoweringProviderRegistry.Instance
                .ResolveProvider(ExecutionContourKind.SecureComputePolicyAdmissionOnly)
                .ObserveCapability(target);

        Assert.Equal(CompilerCapabilityObservationState.NoEmission, vmx.State);
        Assert.Equal(CompilerCapabilityObservationState.NoEmission, secure.State);
        Assert.Equal(CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission, vmx.RuntimeAuthorityDependency);
        Assert.Equal(CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission, secure.RuntimeAuthorityDependency);
        Assert.Contains("VMCS is not compiler-owned state", vmx.Reason, StringComparison.Ordinal);
        Assert.Contains("secure backend execution is forbidden", secure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceSnapshotSerializer_EmitsRequiredStructuredAuthorityFields()
    {
        CompilerEvidenceSnapshot snapshot = CreateEvidenceSnapshot(
            EvidenceOwnershipDomain.CompilerHostOwned,
            EvidenceAuthoritySemantics.EvidenceOnly,
            isAuthority: false);

        IReadOnlyDictionary<string, string> serialized =
            CompilerEvidenceSnapshotSerializer.Serialize(snapshot);

        string[] requiredKeys =
        [
            "intent.kind",
            "contour.kind",
            "capability.observation_state",
            "decision.kind",
            "emission.class",
            "production_lowering.status",
            "authority.class",
            "authority.source_kind",
            "evidence.class",
            "evidence.ownership_domain",
            "evidence.authority_semantics",
            "runtime_dependency",
            "runtime_legality_a.required",
            "runtime_legality_b.required",
            "runtime_commit.required",
            "runtime_retire.required",
            "runtime_publication.required",
            "sideband.requirement",
            "descriptor.abi_status",
            "typed_slot.policy_mode",
            "typed_slot.staging",
            "reject.reason",
            "fallback.policy",
            "fallback.proof_id"
        ];

        foreach (string key in requiredKeys)
        {
            Assert.True(serialized.ContainsKey(key), $"Missing telemetry key: {key}");
        }

        Assert.Equal("False", snapshot.DecisionSummary.ProductionLoweringClaimed.ToString());
        Assert.Equal(nameof(EvidenceOwnershipDomain.CompilerHostOwned), serialized["evidence.ownership_domain"]);
        Assert.Equal(nameof(EvidenceAuthoritySemantics.EvidenceOnly), serialized["evidence.authority_semantics"]);
    }

    [Fact]
    public void EvidenceIsolationValidator_BlocksHostEvidenceFromGuestOrDomainState()
    {
        CompilerEvidenceSnapshot guestVisibleSnapshot = CreateEvidenceSnapshot(
            EvidenceOwnershipDomain.GuestVisibleForbidden,
            EvidenceAuthoritySemantics.ForbiddenAsAuthority,
            isAuthority: false);

        EvidenceIsolationValidationResult result =
            EvidenceIsolationValidator.Validate(guestVisibleSnapshot);

        Assert.True(result.HasIsolationViolations);
        Assert.False(result.IsEvidenceIsolated);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("cannot enter guest-visible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EvidenceIsolationValidator_RejectsDiagnosticEvidenceClaimingAuthority()
    {
        CompilerEvidenceSnapshot authoritySnapshot = CreateEvidenceSnapshot(
            EvidenceOwnershipDomain.CompilerHostOwned,
            EvidenceAuthoritySemantics.DiagnosticOnly,
            isAuthority: true);

        EvidenceIsolationValidationResult result =
            EvidenceIsolationValidator.Validate(authoritySnapshot);

        Assert.True(result.HasIsolationViolations);
        Assert.False(result.IsEvidenceIsolated);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("claims authority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaleCompilerContractVersion_RejectsBridgeIngressBeforeAnyRuntimeAuthority()
    {
        ProcessorCompilerBridge bridge = new ProcessorCompilerBridge();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => bridge.DeclareCompilerContractVersion(
                CompilerContract.Version - 1,
                "CompilerCoreAuthorityBoundaryNegativeTests"));

        Assert.Contains("Compiler contract mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(bridge.HasContractHandshake);
        Assert.Equal(CompilerTypedSlotIngressAction.None, bridge.LastTypedSlotIngressAction);
    }

    [Fact]
    public void BridgeIngressDiagnosticActions_DoNotExposeRuntimeLegalityOrExecutionReadyStates()
    {
        string[] forbiddenNames =
        [
            "RuntimeLegal",
            "ExecutionReady",
            "CanExecute",
            "Committed",
            "Retired",
            "PublishedArchitecturalState"
        ];

        string[] actionNames = Enum.GetNames<CompilerTypedSlotIngressAction>();
        foreach (string forbiddenName in forbiddenNames)
        {
            Assert.DoesNotContain(forbiddenName, actionNames);
        }

        ProcessorCompilerBridge bridge = new ProcessorCompilerBridge();
        bridge.DeclareCompilerContractVersion(
            CompilerContract.Version,
            "CompilerCoreAuthorityBoundaryNegativeTests");

        bridge.AcceptTypedSlotFacts(default);

        Assert.Equal(CompilerTypedSlotIngressAction.AcceptedMissingFacts, bridge.LastTypedSlotIngressAction);
        Assert.False(actionNames.Contains("RuntimeLegal", StringComparer.Ordinal));
        Assert.False(actionNames.Contains("ExecutionReady", StringComparer.Ordinal));
    }

    [Fact]
    public void CompilerSource_DoesNotMapStructuralLegalityTermsToRuntimeLegalityDecision()
    {
        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();

        Assert.Contains("HasLegalAssignment", compilerSource, StringComparison.Ordinal);
        Assert.Contains("IrBundleLegalityResult", compilerSource, StringComparison.Ordinal);
        Assert.Contains("IrCandidateBundleAnalysis", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LegalityDecision", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeLegalDecision", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeLegal = true", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeLegal;", compilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionReady", compilerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorlessL7Submit_RemainsFailClosedAtIrConstructionBoundary()
    {
        HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
        VLIW_Instruction descriptorlessSubmit = new()
        {
            OpCode = (uint)InstructionsEnum.ACCEL_SUBMIT,
            DataTypeValue = DataTypeEnum.INT8,
            StreamLength = 0
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => builder.BuildProgram(0, [NativeTransportRuntimeAdapter.ToCore(in descriptorlessSubmit)]));

        Assert.Contains("ACCEL_SUBMIT", exception.Message, StringComparison.Ordinal);
        Assert.Contains("descriptor sideband", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatrixTileRecoverySource_IsHelperAbiRecognitionOnlyAndKeepsFallbackClosed()
    {
        string source = ReadRepoFile(
            "Compilers", "HybridCPU_Compiler",
            "Core",
            "IR",
            "Construction",
            "CompilerMatrixTileEmissionLowerer.cs");

        Assert.Contains("TryRecoverFromInstruction", source, StringComparison.Ordinal);
        Assert.Contains("RequireRuntimeHandoffAuthority", source, StringComparison.Ordinal);
        Assert.Contains("UsesFallbackPath: false", source, StringComparison.Ordinal);
        Assert.Contains("UsesAliasPromotion: false", source, StringComparison.Ordinal);
        Assert.Contains("UsesScalarVectorDotOrBackendFallback: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CanExecute", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorTransferRecoverySource_IsHelperTransportRecognitionOnlyAndKeepsFallbackClosed()
    {
        string source = ReadRepoFile(
            "Compilers", "HybridCPU_Compiler",
            "Core",
            "IR",
            "Construction",
            "CompilerVectorTransferEmissionLowerer.cs");

        Assert.Contains("TryRecoverFromInstruction", source, StringComparison.Ordinal);
        Assert.Contains("RequireRuntimeHandoffAuthority", source, StringComparison.Ordinal);
        Assert.Contains("UsesFallbackPath: false", source, StringComparison.Ordinal);
        Assert.Contains("UsesBaseMemoryFallback: false", source, StringComparison.Ordinal);
        Assert.Contains("UsesBaseVectorFallback: false", source, StringComparison.Ordinal);
        Assert.Contains("UsesScalarHelperFallback: false", source, StringComparison.Ordinal);
        Assert.Contains("UsesVectorTransposeOrSegmentFallback: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionAllowedByExplicitCompilerGate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CanExecute", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorAndHelperSuccessNames_DoNotCreateAuthorityConversionSurface()
    {
        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] forbiddenFragments =
        [
            "ExecutableDescriptor",
            "DescriptorCapability",
            "DescriptorAuthority",
            "DescriptorCommit",
            "DescriptorRetire",
            "CanExecuteDescriptor",
            "DescriptorIsLegal",
            "HelperSuccess",
            "ParserSuccess",
            "ProductionAllowedByExplicitCompilerGate"
        ];

        foreach (string forbiddenFragment in forbiddenFragments)
        {
            Assert.DoesNotContain(forbiddenFragment, compilerSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VmxAndSecureComputeBackendEmissionSurfaces_RemainAbsentFromPublicCompilerApi()
    {
        string[] publicCompilerMethods =
        [
            .. PublicMethodNames(typeof(IAppAsmFacade)),
            .. PublicMethodNames(typeof(AppAsmFacade)),
            .. PublicMethodNames(typeof(IPlatformAsmFacade)),
            .. PublicMethodNames(typeof(PlatformAsmFacade)),
            .. PublicMethodNames(typeof(HybridCpuThreadCompilerContext))
        ];

        foreach (string methodName in publicCompilerMethods)
        {
            Assert.DoesNotContain("Vmx", methodName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Vmcs", methodName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecureCompute", methodName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecureBackend", methodName, StringComparison.OrdinalIgnoreCase);
        }

        string compilerSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();
        Assert.DoesNotContain("VmxBackend", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VmcsOwner", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecureComputeBackend", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecureBackendExecution", compilerSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] PublicMethodNames(Type type)
    {
        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name)
            .ToArray();
    }

    private static string ReadRepoFile(params string[] relativePathParts)
    {
        string repoRoot = relativePathParts.Length > 0 && relativePathParts[0] == "Compilers"
            ? CompatFreezeScanner.FindCompilerRepoRoot()
            : CompatFreezeScanner.FindRepoRoot();
        return File.ReadAllText(Path.Combine([repoRoot, .. relativePathParts]));
    }

    private static CompilerEvidenceSnapshot CreateEvidenceSnapshot(
        EvidenceOwnershipDomain ownershipDomain,
        EvidenceAuthoritySemantics authoritySemantics,
        bool isAuthority)
    {
        CompilerLoweringDecisionSummary decisionSummary = new CompilerLoweringDecisionSummary(
            CompilerLoweringDecisionKind.Rejected,
            CompilerEmissionClass.NoEmission,
            CompilerProductionLoweringStatus.Rejected,
            CompilerRejectReason.UnknownContour,
            CompilerExecutionClaim.NoExecutionClaim,
            CompilerPublicationClass.EvidenceOnly,
            CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
            CarrierEmitted: false,
            SidebandEmitted: false,
            DescriptorEmitted: false,
            TypedSlotFactsEmitted: false,
            StructuralAgreementEmitted: false,
            EvidenceEmitted: true,
            BridgeEnvelopePrepared: false,
            ProductionLoweringClaimed: false);

        return new CompilerEvidenceSnapshot(
            Guid.NewGuid(),
            SemanticIntentKind.Unknown,
            ExecutionContourKind.UnknownRejected,
            CompilerCapabilityObservationState.NoEmission,
            decisionSummary,
            CompilerAuthorityClass.CompilerEvidenceProduction,
            CompilerAuthoritySourceKind.CompilerStructuralModel,
            CompilerEvidenceClass.NegativeGateEvidence,
            ownershipDomain,
            authoritySemantics,
            SidebandRequirement.OptionalCompatibility,
            DescriptorAbiStatus.None,
            CompilerContract.CurrentTypedSlotPolicy.Mode.ToString(),
            CompilerTypedSlotFactStaging.MissingCompatibility,
            BridgeIngressStatus.BridgeIngressRejected,
            FallbackPolicyKind.Forbidden,
            "phase08-test-no-fallback",
            [
                new CompilerEvidenceRecord(
                    CompilerEvidenceClass.NegativeGateEvidence,
                    ownershipDomain,
                    authoritySemantics,
                    "CompilerCoreAuthorityBoundaryNegativeTests",
                    "Negative contour evidence is diagnostic only.",
                    isAuthority,
                    "not runtime authority")
            ],
            ["known contour"],
            RuntimeLegalityAStillRequired: true,
            RuntimeLegalityBStillRequired: true,
            RuntimeCommitStillRequired: true,
            RuntimeRetireStillRequired: true,
            RuntimePublicationStillRequired: true,
            "phase08 evidence snapshot test");
    }
}
