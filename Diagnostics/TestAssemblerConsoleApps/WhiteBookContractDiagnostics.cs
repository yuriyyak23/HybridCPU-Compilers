using System;
using System.Buffers.Binary;
using HybridCPU.Compiler.Core.IR;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Backends;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record WhiteBookContractDiagnosticsReport(
    string ReportId,
    DateTimeOffset CapturedUtc,
    ulong RequestedIterations,
    WhiteBookContractSummary Summary,
    IReadOnlyList<WhiteBookContractProbeResult> Probes)
{
    public bool Succeeded => Summary.FailedProbeCount == 0;
}

internal sealed record WhiteBookContractSummary(
    int ProbeCount,
    int PassedProbeCount,
    int FailedProbeCount,
    IReadOnlyList<string> CoveredClaimGroups);

internal sealed record WhiteBookContractProbeResult(
    string Id,
    string Claim,
    string ExpectedBoundary,
    string ObservedEvidence,
    bool Passed,
    string? FailureMessage);

internal sealed partial class SimpleAsmApp
{
    private const ulong WhiteBookDsc1IdentityHash = 0xA11CE5EEDUL;
    private const ulong WhiteBookDsc2IdentityHash = 0xD5C2000A11CEUL;
    private const ulong WhiteBookDsc2CapabilityHash = 0xD5C2CA9AUL;
    private const uint WhiteBookDsc2DeviceId = 6;
    private const ulong WhiteBookOwnerDomainTag = 0xD0A11UL;

    private const int Dsc1HeaderSize = 128;
    private const int DscRangeEntrySize = 16;
    private const int Dsc1FlagsOffset = 12;
    private const int Dsc1RangeEncodingOffset = 46;
    private const int Dsc1PartialCompletionPolicyOffset = 56;

    private const int Dsc2HeaderSizeOffset = 8;
    private const int Dsc2ExtensionTableOffsetOffset = 16;
    private const int Dsc2ExtensionCountOffset = 20;
    private const int Dsc2ExtensionTableByteSizeOffset = 24;

    public WhiteBookContractDiagnosticsReport ExecuteWhiteBookContractDiagnostics(ulong iterations)
    {
        List<WhiteBookContractProbeResult> probes = new List<WhiteBookContractProbeResult>
        {
            CaptureWhiteBookProbe(
                "lane6.dsc.fail-closed",
                "lane6 DSC carrier remains descriptor-only and cannot execute through MicroOp.Execute.",
                "DmaStreamComputeMicroOp.Execute throws; WritesRegister=false; ExecutionEnabled=false.",
                ProbeLane6DscFailClosed),
            CaptureWhiteBookProbe(
                "dsc1.strict.current-only",
                "DSC1 accepts only current v1 inline-contiguous all-or-none descriptors through guard-plane evidence.",
                "Valid guarded DSC1 parses; unguarded/reserved/non-inline/partial variants reject.",
                ProbeDsc1StrictCurrentOnly),
            CaptureWhiteBookProbe(
                "dsc2.parser-only",
                "DSC2 is parser-only capability evidence and cannot issue, publish memory, execute, or production-lower.",
                "Parser accepts DSC2 footprint evidence while all execution/publication/lowering gates are false.",
                ProbeDsc2ParserOnly),
            CaptureWhiteBookProbe(
                "dsc.token-progress-fault.model-only",
                "DSC token admission, progress diagnostics, and fault records are helper/model evidence only.",
                "Token/progress/fault APIs cannot publish memory or become full precise architectural execution.",
                ProbeDscTokenProgressFaultModelOnly),
            CaptureWhiteBookProbe(
                "dsc.conflict-addressing-cache.no-current-execution",
                "Conflict, addressing/IOMMU, cache, and prefetch surfaces do not create current DSC/L7 execution authority.",
                "Direct carriers are not wired to conflict/IOMMU/cache fallback; coherent DMA assumptions reject.",
                ProbeConflictAddressingCacheBoundaries),
            CaptureWhiteBookProbe(
                "l7.accel.fail-closed-no-rd",
                "L7 ACCEL_* carriers remain lane7 sideband carriers, not executable production ISA.",
                "All native ACCEL_* carriers fail closed and do not write architectural rd.",
                ProbeL7FailClosedNoRd),
            CaptureWhiteBookProbe(
                "l7.model-fake-backend.boundary",
                "L7 register ABI, token lifecycle, queues, commit, and fake backend evidence remain model/test-only.",
                "FakeMatMul backend is test-only and direct carriers do not wire backend/token/commit APIs.",
                ProbeL7ModelAndFakeBackendBoundary),
            CaptureWhiteBookProbe(
                "compiler.production-lowering.prohibited",
                "Compiler/backend sideband emission is separate from production executable DSC/L7 lowering.",
                "Descriptor/parser/model/fake/coherence/partial-completion assumptions reject production lowering.",
                ProbeCompilerBackendLoweringProhibitions),
            CaptureWhiteBookProbe(
                "phase12.migration-gate",
                "Phase12 remains a conformance/documentation migration gate, not executable feature approval.",
                "Migration requires approval, code, positive/negative tests, compiler conformance, and claim-safety.",
                ProbePhase12MigrationGate),
            CaptureWhiteBookProbe(
                "phase13.dependency-non-inversion",
                "Phase13 dependency graph is planning evidence and downstream surfaces cannot satisfy upstream gates.",
                "Dependency-order docs preserve planning-only and downstream evidence non-inversion language.",
                ProbePhase13DependencyNonInversion)
        };

        WhiteBookContractSummary summary = new(
            ProbeCount: probes.Count,
            PassedProbeCount: probes.Count(static probe => probe.Passed),
            FailedProbeCount: probes.Count(static probe => !probe.Passed),
            CoveredClaimGroups: probes.Select(static probe => probe.Id).ToArray());

        return new WhiteBookContractDiagnosticsReport(
            ReportId: "stream-whitebook-contract",
            CapturedUtc: DateTimeOffset.UtcNow,
            RequestedIterations: iterations,
            Summary: summary,
            Probes: probes);
    }

    private static WhiteBookContractProbeResult CaptureWhiteBookProbe(
        string id,
        string claim,
        string expectedBoundary,
        Func<string> probe)
    {
        try
        {
            string observed = probe();
            return new WhiteBookContractProbeResult(
                id,
                claim,
                expectedBoundary,
                observed,
                Passed: true,
                FailureMessage: null);
        }
        catch (Exception ex)
        {
            return new WhiteBookContractProbeResult(
                id,
                claim,
                expectedBoundary,
                ObservedEvidence: "probe failed before completing boundary evidence",
                Passed: false,
                FailureMessage: ex.Message);
        }
    }

    private static string ProbeLane6DscFailClosed()
    {
        DmaStreamComputeDescriptor descriptor = CreateDsc1Descriptor();
        DmaStreamComputeMicroOp carrier = new DmaStreamComputeMicroOp(descriptor);

        Require(!DmaStreamComputeDescriptorParser.ExecutionEnabled, "DmaStreamComputeDescriptorParser.ExecutionEnabled became true.");
        Require(!carrier.WritesRegister, "DmaStreamComputeMicroOp unexpectedly writes an architectural register.");
        Require(carrier.WriteRegisters.Count == 0, "DmaStreamComputeMicroOp published architectural write registers.");
        Require(carrier.SerializationClass == SerializationClass.MemoryOrdered, "DmaStreamComputeMicroOp serialization changed from MemoryOrdered.");

        Processor.CPU_Core core = new Processor.CPU_Core(
            0,
            CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Emulation));
        DiagnosticShadowFrontend.ConfigureNewCore(core);
        InvalidOperationException exception;
        try
        {
            carrier.Execute(ref core);
            throw new InvalidOperationException("DmaStreamComputeMicroOp.Execute unexpectedly returned successfully.");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        RequireContains(exception.Message, "fail closed", "lane6 execution exception did not state fail-closed behavior.");
        RequireContains(exception.Message, "DmaStreamComputeRuntime", "lane6 execution exception no longer separates runtime helper from MicroOp.Execute.");
        RequireContains(exception.Message, "no StreamEngine or DMAController fallback", "lane6 execution exception no longer rejects fallback routing.");

        return $"ExecutionEnabled={DmaStreamComputeDescriptorParser.ExecutionEnabled}; WritesRegister={carrier.WritesRegister}; WriteRegisters={carrier.WriteRegisters.Count}; exception='{TrimForEvidence(exception.Message)}'";
    }

    private static string ProbeDsc1StrictCurrentOnly()
    {
        byte[] descriptorBytes = BuildDsc1Descriptor();
        DmaStreamComputeValidationResult valid = ParseDsc1(descriptorBytes);
        Require(valid.IsValid, $"Valid DSC1 descriptor rejected: {valid.Fault} {valid.Message}");

        DmaStreamComputeDescriptor descriptor = valid.RequireDescriptorForAdmission();
        Require(descriptor.AbiVersion == DmaStreamComputeDescriptorParser.CurrentAbiVersion, "DSC1 ABI version drifted.");
        Require(descriptor.HeaderSize == DmaStreamComputeDescriptorParser.CurrentHeaderSize, "DSC1 header size drifted.");
        Require(descriptor.RangeEncoding == DmaStreamComputeRangeEncoding.InlineContiguous, "DSC1 range encoding is no longer inline-contiguous.");
        Require(descriptor.PartialCompletionPolicy == DmaStreamComputePartialCompletionPolicy.AllOrNone, "DSC1 no longer requires AllOrNone.");

        DmaStreamComputeValidationResult unguarded = DmaStreamComputeDescriptorParser.Parse(
            descriptorBytes,
            CreateDsc1Reference(descriptorBytes));
        Require(!unguarded.IsValid && unguarded.Fault == DmaStreamComputeValidationFault.OwnerDomainFault, "DSC1 unguarded parse no longer fails owner/domain.");

        DmaStreamComputeValidationResult reserved = ParseDsc1Mutation(static bytes => WriteUInt32(bytes, Dsc1FlagsOffset, 1));
        Require(reserved.Fault == DmaStreamComputeValidationFault.ReservedFieldFault, "DSC1 reserved field mutation did not reject.");

        DmaStreamComputeValidationResult nonInline = ParseDsc1Mutation(static bytes => WriteUInt16(bytes, Dsc1RangeEncodingOffset, 2));
        Require(nonInline.Fault == DmaStreamComputeValidationFault.UnsupportedShape, "DSC1 non-inline range encoding did not reject.");

        DmaStreamComputeValidationResult partial = ParseDsc1Mutation(static bytes => WriteUInt16(bytes, Dsc1PartialCompletionPolicyOffset, 2));
        Require(partial.Fault == DmaStreamComputeValidationFault.ReservedFieldFault, "DSC1 partial-completion policy did not reject.");

        return $"validAbi={descriptor.AbiVersion}; unguarded={unguarded.Fault}; reserved={reserved.Fault}; nonInline={nonInline.Fault}; partial={partial.Fault}; footprint=0x{descriptor.NormalizedFootprintHash:X16}";
    }

    private static string ProbeDsc2ParserOnly()
    {
        byte[] descriptorBytes = BuildDsc2(
            PhysicalAddressSpaceExtension(),
            StridedRangeExtension(DmaStreamComputeDsc2AccessKind.Read, 4, 4, 0x1000, 4),
            StridedRangeExtension(DmaStreamComputeDsc2AccessKind.Write, 4, 4, 0x9000, 4));

        DmaStreamComputeDsc2ValidationResult result = ParseDsc2(descriptorBytes);
        Require(result.IsParserAccepted, $"DSC2 parser-only descriptor rejected: {result.Fault} {result.Message}");
        Require(!result.ExecutionEnabled, "DSC2 validation unexpectedly enabled execution.");
        Require(!result.CanIssueToken, "DSC2 validation unexpectedly enabled token issue.");
        Require(!result.CanPublishMemory, "DSC2 validation unexpectedly enabled memory publication.");
        Require(!result.CanProductionLower, "DSC2 validation unexpectedly enabled production lowering.");

        DmaStreamComputeDsc2Descriptor descriptor = result.RequireParserOnlyDescriptor();
        Require(descriptor.IsParserOnly, "DSC2 descriptor is no longer parser-only.");
        Require(!descriptor.ExecutionEnabled, "DSC2 descriptor unexpectedly enabled execution.");
        Require(!descriptor.CanIssueToken, "DSC2 descriptor unexpectedly enabled token issue.");
        Require(!descriptor.CanPublishMemory, "DSC2 descriptor unexpectedly enabled memory publication.");
        Require(!descriptor.CanProductionLower, "DSC2 descriptor unexpectedly enabled production lowering.");
        Require(descriptor.ExecutionState == DmaStreamComputeDsc2ExecutionState.ParserOnlyExecutionDisabled, "DSC2 execution state drifted.");
        Require(descriptor.NormalizedFootprint.IsExact, "DSC2 footprint is no longer exact for deterministic strided ranges.");
        Require(descriptor.NormalizedFootprint.NormalizedFootprintHash != 0, "DSC2 parser did not produce normalized footprint evidence.");
        Require(!DmaStreamComputeDsc2CapabilitySet.ParserOnlyFoundation.GrantsExecution, "DSC2 ParserOnlyFoundation unexpectedly grants execution.");
        Require(!DmaStreamComputeDsc2CapabilitySet.ParserOnlyFoundation.GrantsCompilerLowering, "DSC2 ParserOnlyFoundation unexpectedly grants compiler lowering.");

        byte[] iommuWithoutExtension = BuildDsc2(
            DmaStreamComputeDsc2AddressSpaceKind.IommuTranslated,
            StridedRangeExtension(DmaStreamComputeDsc2AccessKind.Read, 4, 4, 0x1000, 4));
        DmaStreamComputeDsc2ValidationResult iommuResult = ParseDsc2(iommuWithoutExtension);
        Require(!iommuResult.IsParserAccepted && iommuResult.Fault == DmaStreamComputeValidationFault.AddressSpaceFault, "DSC2 IOMMU selection without explicit address-space evidence did not fail closed.");

        return $"accepted={result.IsParserAccepted}; execution={result.ExecutionEnabled}; issue={result.CanIssueToken}; publish={result.CanPublishMemory}; lower={result.CanProductionLower}; iommuNoExtension={iommuResult.Fault}; footprint=0x{descriptor.NormalizedFootprint.NormalizedFootprintHash:X16}";
    }

    private static string ProbeDscTokenProgressFaultModelOnly()
    {
        DmaStreamComputeValidationResult validation = ParseDsc1(BuildDsc1Descriptor());
        Require(validation.IsValid, $"DSC1 descriptor required for token diagnostics rejected: {validation.Fault} {validation.Message}");

        DmaStreamComputeTokenAdmissionResult admission =
            DmaStreamComputeToken.TryAdmit(validation, tokenId: 0xD51UL);
        Require(admission.IsAccepted, $"Token admission model rejected valid descriptor: {admission.Status} {admission.Message}");
        Require(admission.Token is not null, "Accepted token admission did not carry a token.");
        Require(admission.Token!.State == DmaStreamComputeTokenState.Admitted, "Token was not admitted in model state.");

        DmaStreamComputeProgressDiagnostics progress =
            admission.Token.RecordProgressDiagnostics(
                bytesRead: 16,
                bytesStaged: 16,
                elementOperations: 4,
                modeledLatencyCycles: 12,
                backendStepCount: 1);
        Require(!progress.IsAuthoritative, "Progress diagnostics became authoritative.");
        Require(!progress.CanIssueToken, "Progress diagnostics can issue a token.");
        Require(!progress.CanSetSucceeded, "Progress diagnostics can set success.");
        Require(!progress.CanSetCommitted, "Progress diagnostics can set committed.");
        Require(!progress.CanPublishMemory, "Progress diagnostics can publish memory.");
        Require(!progress.IsRetirePublication, "Progress diagnostics became retire publication.");

        DmaStreamComputeFaultRecord fault = new DmaStreamComputeFaultRecord(
            DmaStreamComputeTokenFaultKind.DmaDeviceFault,
            "WhiteBook diagnostics synthetic model fault.",
            faultAddress: 0x9000,
            isWrite: true,
            virtualThreadId: 0,
            ownerDomainTag: WhiteBookOwnerDomainTag,
            activeDomainCertificate: WhiteBookOwnerDomainTag,
            sourcePhase: DmaStreamComputeFaultSourcePhase.Backend,
            backendExceptionNormalized: true,
            normalizedHostExceptionType: typeof(InvalidOperationException).FullName,
            publicationContract: DmaStreamComputeFaultPublicationContract.FuturePreciseRetireRequiresPublicationMetadata);
        Require(fault.RequiresRetireExceptionPublication, "Fault record no longer requires retire-style exception publication.");
        Require(!fault.IsFullPipelinePreciseArchitecturalException, "Fault record became full-pipeline precise architectural exception.");
        Require(fault.RequiresFuturePrecisePublicationMetadata, "Fault record no longer marks future precise publication metadata.");

        string microOpSource = ReadRepoFile("HybridCPU_ISE/Core/Pipeline/MicroOps/DmaStreamComputeMicroOp.cs");
        RequireNotContains(microOpSource, "DmaStreamComputeTokenStore", "DmaStreamComputeMicroOp wired token store into direct execution.");
        RequireNotContains(microOpSource, "ExecuteToCommitPending", "DmaStreamComputeMicroOp wired runtime helper into direct execution.");

        return $"admission={admission.Status}; tokenState={admission.Token.State}; progressCanPublish={progress.CanPublishMemory}; preciseException={fault.IsFullPipelinePreciseArchitecturalException}; futureMetadata={fault.RequiresFuturePrecisePublicationMetadata}";
    }

    private static string ProbeConflictAddressingCacheBoundaries()
    {
        string dscMicroOpSource = ReadRepoFile("HybridCPU_ISE/Core/Pipeline/MicroOps/DmaStreamComputeMicroOp.cs");
        string l7CarrierSource = ReadRepoFile("HybridCPU_ISE/Core/Pipeline/MicroOps/SystemDeviceCommandMicroOp.cs");
        string dscRuntimeSource = ReadRepoFile("HybridCPU_ISE/Core/Execution/DmaStreamCompute/DmaStreamComputeRuntime.cs");
        string compilerSource = ReadAllSourceText(Path.Combine(FindRepoRoot(), "HybridCPU_Compiler"));

        RequireNotContains(dscMicroOpSource, "GlobalMemoryConflictService", "lane6 carrier wired global conflict authority.");
        RequireNotContains(dscMicroOpSource, "ExternalAcceleratorConflictManager", "lane6 carrier wired L7 conflict manager.");
        RequireNotContains(dscMicroOpSource, "AddressingBackendResolver", "lane6 carrier wired addressing backend resolver.");
        RequireNotContains(dscMicroOpSource, "IommuTranslated", "lane6 carrier wired IOMMU-translated execution.");
        RequireNotContains(dscRuntimeSource, "IommuTranslated", "DSC runtime helper wired current IOMMU-translated execution.");
        RequireNotContains(l7CarrierSource, "ExternalAcceleratorConflictManager", "L7 carrier wired conflict manager into direct execution.");
        RequireNotContains(l7CarrierSource, "MemoryCoherencyObserver", "L7 carrier wired cache coherency observer into direct execution.");
        RequireNotContains(compilerSource, "DmaStreamComputeRuntime.ExecuteToCommitPending", "Compiler wired DSC runtime helper as production lowering.");

        CompilerBackendLoweringDecision dscCoherence = CompilerBackendLoweringContract.EvaluateProductionDscLowering(
            new CompilerBackendLoweringRequest
            {
                Surface = CompilerBackendLoweringSurface.Lane6DmaStreamCompute,
                State = CompilerBackendCapabilityState.ProductionExecutable,
                AvailableRequirements = CompilerBackendLoweringContract.FutureDscRequiredRequirements,
                AssumesHardwareCoherence = true
            });
        Require(!dscCoherence.IsAllowed, "DSC production lowering accepted hardware coherence assumption.");

        CompilerBackendLoweringDecision l7Coherence = CompilerBackendLoweringContract.EvaluateProductionL7Lowering(
            new CompilerBackendLoweringRequest
            {
                Surface = CompilerBackendLoweringSurface.Lane7SystemDeviceCommand,
                State = CompilerBackendCapabilityState.ProductionExecutable,
                AvailableRequirements = CompilerBackendLoweringContract.FutureL7RequiredRequirements,
                AssumesHardwareCoherence = true
            });
        Require(!l7Coherence.IsAllowed, "L7 production lowering accepted hardware coherence assumption.");

        return $"dscCarrierNoConflictIommu=true; l7CarrierNoConflictCache=true; compilerNoRuntimeLowering=true; dscCoherenceAllowed={dscCoherence.IsAllowed}; l7CoherenceAllowed={l7Coherence.IsAllowed}";
    }

    private static string ProbeL7FailClosedNoRd()
    {
        SystemDeviceCommandMicroOp[] carriers =
        [
            new AcceleratorQueryCapsMicroOp(),
            new AcceleratorSubmitMicroOp(),
            new AcceleratorPollMicroOp(),
            new AcceleratorWaitMicroOp(),
            new AcceleratorCancelMicroOp(),
            new AcceleratorFenceMicroOp()
        ];

        foreach (SystemDeviceCommandMicroOp carrier in carriers)
        {
            Require(!carrier.WritesRegister, $"{carrier.GetType().Name} unexpectedly writes rd.");
            Require(carrier.WriteRegisters.Count == 0, $"{carrier.GetType().Name} published write register metadata.");

            Processor.CPU_Core core = new Processor.CPU_Core(
                0,
                CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Emulation));
            DiagnosticShadowFrontend.ConfigureNewCore(core);
            const int observedRegister = 9;
            const ulong sentinel = 0xCAFE_BABE_1020_3040UL;
            core.WriteCommittedArch(0, observedRegister, sentinel);
            ulong before = core.ReadArch(0, observedRegister);

            InvalidOperationException exception;
            try
            {
                carrier.Execute(ref core);
                throw new InvalidOperationException($"{carrier.GetType().Name}.Execute unexpectedly returned successfully.");
            }
            catch (InvalidOperationException ex)
            {
                exception = ex;
            }

            Require(before == sentinel, "Sentinel register setup failed.");
            Require(core.ReadArch(0, observedRegister) == sentinel, $"{carrier.GetType().Name} changed architectural rd despite fail-closed execution.");
            RequireContains(exception.Message, "direct execution is unsupported", $"{carrier.GetType().Name} no longer reports direct execution unsupported.");
            RequireContains(exception.Message, "backend execution", $"{carrier.GetType().Name} no longer separates backend execution.");
            RequireContains(exception.Message, "staged write publication", $"{carrier.GetType().Name} no longer rejects staged write publication.");
            RequireContains(exception.Message, "architectural rd writeback", $"{carrier.GetType().Name} no longer rejects rd writeback.");
        }

        return $"carriers={carriers.Length}; writesRegister=false; writeRegisters=0; directExecuteFailClosed=true";
    }

    private static string ProbeL7ModelAndFakeBackendBoundary()
    {
        FakeMatMulExternalAcceleratorBackend fakeBackend = new FakeMatMulExternalAcceleratorBackend();
        Require(fakeBackend.IsTestOnly, "FakeMatMulExternalAcceleratorBackend is no longer marked test-only.");

        string fakeBackendSource = ReadRepoFile("HybridCPU_ISE/Core/Execution/ExternalAccelerators/Backends/FakeMatMulExternalAcceleratorBackend.cs");
        string carrierSource = ReadRepoFile("HybridCPU_ISE/Core/Pipeline/MicroOps/SystemDeviceCommandMicroOp.cs");

        RequireContains(fakeBackendSource, "IsTestOnly => true", "Fake backend source no longer records test-only evidence.");
        RequireNotContains(carrierSource, nameof(FakeMatMulExternalAcceleratorBackend), "SystemDeviceCommandMicroOp wired fake backend.");
        RequireNotContains(carrierSource, "AcceleratorTokenStore", "SystemDeviceCommandMicroOp wired token store.");
        RequireNotContains(carrierSource, "AcceleratorCommandQueue", "SystemDeviceCommandMicroOp wired command queue.");
        RequireNotContains(carrierSource, "AcceleratorCommitCoordinator", "SystemDeviceCommandMicroOp wired commit coordinator.");
        RequireNotContains(carrierSource, "IExternalAcceleratorBackend", "SystemDeviceCommandMicroOp wired backend interface.");

        return $"fakeBackendIsTestOnly={fakeBackend.IsTestOnly}; carrierBackendWiring=false; carrierTokenStoreWiring=false; carrierCommitWiring=false";
    }

    private static string ProbeCompilerBackendLoweringProhibitions()
    {
        CompilerBackendLoweringDecision dscDescriptorOnly =
            CompilerBackendLoweringContract.EvaluateProductionDscLowering(
                new CompilerBackendLoweringRequest
                {
                    Surface = CompilerBackendLoweringSurface.Lane6DmaStreamCompute,
                    State = CompilerBackendCapabilityState.DescriptorOnly,
                    UsesDescriptorEvidenceOnly = true
                });
        Require(!dscDescriptorOnly.IsAllowed, "Descriptor-only DSC evidence became production lowering.");

        CompilerBackendLoweringDecision dscParserOnly =
            CompilerBackendLoweringContract.EvaluateProductionDscLowering(
                new CompilerBackendLoweringRequest
                {
                    Surface = CompilerBackendLoweringSurface.Lane6DmaStreamCompute,
                    State = CompilerBackendCapabilityState.ProductionExecutable,
                    AvailableRequirements = CompilerBackendLoweringContract.FutureDscRequiredRequirements,
                    UsesParserValidationOnly = true
                });
        Require(!dscParserOnly.IsAllowed, "Parser-only DSC evidence became production lowering.");

        CompilerBackendLoweringDecision dscPartial =
            CompilerBackendLoweringContract.EvaluateProductionDscLowering(
                new CompilerBackendLoweringRequest
                {
                    Surface = CompilerBackendLoweringSurface.Lane6DmaStreamCompute,
                    State = CompilerBackendCapabilityState.ProductionExecutable,
                    AvailableRequirements = CompilerBackendLoweringContract.FutureDscRequiredRequirements,
                    AssumesSuccessfulPartialCompletion = true
                });
        Require(!dscPartial.IsAllowed, "DSC production lowering accepted successful partial completion.");

        CompilerBackendLoweringDecision l7ModelOnly =
            CompilerBackendLoweringContract.EvaluateProductionL7Lowering(
                new CompilerBackendLoweringRequest
                {
                    Surface = CompilerBackendLoweringSurface.Lane7SystemDeviceCommand,
                    State = CompilerBackendCapabilityState.ModelOnly,
                    UsesModelOrTestHelper = true
                });
        Require(!l7ModelOnly.IsAllowed, "Model-only L7 evidence became production lowering.");

        CompilerBackendLoweringDecision l7FakeHelper =
            CompilerBackendLoweringContract.EvaluateProductionL7Lowering(
                new CompilerBackendLoweringRequest
                {
                    Surface = CompilerBackendLoweringSurface.Lane7SystemDeviceCommand,
                    State = CompilerBackendCapabilityState.ProductionExecutable,
                    AvailableRequirements = CompilerBackendLoweringContract.FutureL7RequiredRequirements,
                    UsesModelOrTestHelper = true
                });
        Require(!l7FakeHelper.IsAllowed, "Fake/model helper L7 evidence became production lowering.");

        Require(!CompilerBackendLoweringContract.CanSelectForProductionLowering(CompilerBackendCapabilityState.DescriptorOnly), "DescriptorOnly is production-selectable.");
        Require(!CompilerBackendLoweringContract.CanSelectForProductionLowering(CompilerBackendCapabilityState.ParserOnly), "ParserOnly is production-selectable.");
        Require(!CompilerBackendLoweringContract.CanSelectForProductionLowering(CompilerBackendCapabilityState.ModelOnly), "ModelOnly is production-selectable.");
        Require(!CompilerBackendLoweringContract.CanSelectForProductionLowering(CompilerBackendCapabilityState.ExecutableExperimental), "ExecutableExperimental is production-selectable.");

        return $"dscDescriptorAllowed={dscDescriptorOnly.IsAllowed}; dscParserAllowed={dscParserOnly.IsAllowed}; dscPartialAllowed={dscPartial.IsAllowed}; l7ModelAllowed={l7ModelOnly.IsAllowed}; l7FakeAllowed={l7FakeHelper.IsAllowed}";
    }

    private static string ProbePhase12MigrationGate()
    {
        string phase12 = ReadRepoFile("Documentation/Refactoring/Phases Ex1/12_Testing_Conformance_And_Documentation_Migration.md");
        string adr12 = ReadRepoFile("Documentation/Refactoring/Phases Ex1/ADR_12_Testing_Conformance_And_Documentation_Migration.md");
        string combined = phase12 + Environment.NewLine + adr12;

        RequireContains(combined, "Future Design moves into Current Implemented Contract only after", "Phase12 migration rule is missing.");
        RequireContains(combined, "Architecture approval", "Phase12 no longer requires architecture approval.");
        RequireContains(combined, "Code implementation", "Phase12 no longer requires code implementation.");
        RequireContains(combined, "positive", "Phase12 no longer names positive tests.");
        RequireContains(combined, "negative", "Phase12 no longer names negative tests.");
        RequireContains(combined, "Compiler/backend", "Phase12 no longer gates compiler/backend conformance.");
        RequireContains(combined, "claim-safety", "Phase12 no longer gates documentation claim-safety.");
        RequireContains(combined, "not evidence that executable DSC", "Phase12 no longer rejects current executable inference.");

        return "migrationRule=approval+code+positiveNegativeTests+compilerConformance+claimSafety; executableInferenceRejected=true";
    }

    private static string ProbePhase13DependencyNonInversion()
    {
        string phase13 = ReadRepoFile("Documentation/Refactoring/Phases Ex1/13_Dependency_Graph_And_Execution_Order.md");
        string adr13 = ReadRepoFile("Documentation/Refactoring/Phases Ex1/ADR_13_Dependency_Graph_And_Execution_Order.md");
        string combined = phase13 + Environment.NewLine + adr13;

        RequireContains(combined, "planning", "Phase13 no longer describes planning status.");
        RequireContains(combined, "Dependency", "Phase13 no longer describes dependency ordering.");
        RequireContains(combined, "downstream evidence", "Phase13 no longer describes downstream evidence.");
        RequireContains(combined, "non-inversion", "Phase13 no longer names non-inversion.");
        RequireContains(combined, "must not satisfy upstream executable gates", "Phase13 no longer rejects downstream evidence inversion.");

        return "dependencyOrder=planning-only; downstreamEvidenceNonInversion=true";
    }

    private static DmaStreamComputeDescriptor CreateDsc1Descriptor()
    {
        DmaStreamComputeValidationResult validation = ParseDsc1(BuildDsc1Descriptor());
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Failed to create DSC1 descriptor: {validation.Fault} {validation.Message}");
        }

        return validation.RequireDescriptorForAdmission();
    }

    private static DmaStreamComputeValidationResult ParseDsc1(byte[] descriptorBytes)
    {
        DmaStreamComputeDescriptorReference reference = CreateDsc1Reference(descriptorBytes);
        DmaStreamComputeOwnerGuardDecision guardDecision =
            CreateDsc1GuardDecision(descriptorBytes, reference);
        return DmaStreamComputeDescriptorParser.Parse(
            descriptorBytes,
            guardDecision,
            reference);
    }

    private static DmaStreamComputeValidationResult ParseDsc1Mutation(Action<byte[]> mutate)
    {
        byte[] descriptorBytes = BuildDsc1Descriptor();
        mutate(descriptorBytes);
        return ParseDsc1(descriptorBytes);
    }

    private static DmaStreamComputeDescriptorReference CreateDsc1Reference(byte[] descriptorBytes) =>
        new(
            descriptorAddress: 0x8000,
            descriptorSize: (uint)descriptorBytes.Length,
            descriptorIdentityHash: WhiteBookDsc1IdentityHash);

    private static DmaStreamComputeOwnerGuardDecision CreateDsc1GuardDecision(
        byte[] descriptorBytes,
        DmaStreamComputeDescriptorReference? descriptorReference = null)
    {
        DmaStreamComputeStructuralReadResult structuralRead =
            DmaStreamComputeDescriptorParser.ReadStructuralOwnerBinding(
                descriptorBytes,
                descriptorReference);
        if (!structuralRead.IsValid)
        {
            throw new InvalidOperationException(structuralRead.Message);
        }

        DmaStreamComputeOwnerBinding ownerBinding =
            structuralRead.RequireOwnerBindingForGuard();
        DmaStreamComputeOwnerGuardContext context = new DmaStreamComputeOwnerGuardContext(
            ownerBinding.OwnerVirtualThreadId,
            ownerBinding.OwnerContextId,
            ownerBinding.OwnerCoreId,
            ownerBinding.OwnerPodId,
            ownerBinding.OwnerDomainTag,
            activeDomainCertificate: ownerBinding.OwnerDomainTag,
            deviceId: ownerBinding.DeviceId);

        return new SafetyVerifier().EvaluateDmaStreamComputeOwnerGuard(
            ownerBinding,
            context);
    }

    private static byte[] BuildDsc1Descriptor(
        DmaStreamComputeOperationKind operation = DmaStreamComputeOperationKind.Add,
        DmaStreamComputeElementType elementType = DmaStreamComputeElementType.UInt32,
        DmaStreamComputeShapeKind shape = DmaStreamComputeShapeKind.Contiguous1D)
    {
        ushort sourceRangeCount = operation switch
        {
            DmaStreamComputeOperationKind.Copy => 1,
            DmaStreamComputeOperationKind.Fma => 3,
            _ => 2
        };

        const ushort destinationRangeCount = 1;
        int sourceRangeTableOffset = Dsc1HeaderSize;
        int destinationRangeTableOffset = Dsc1HeaderSize + (sourceRangeCount * DscRangeEntrySize);
        uint totalSize = (uint)(Dsc1HeaderSize + ((sourceRangeCount + destinationRangeCount) * DscRangeEntrySize));
        byte[] bytes = new byte[totalSize];

        WriteUInt32(bytes, 0, DmaStreamComputeDescriptorParser.Magic);
        WriteUInt16(bytes, 4, DmaStreamComputeDescriptorParser.CurrentAbiVersion);
        WriteUInt16(bytes, 6, Dsc1HeaderSize);
        WriteUInt32(bytes, 8, totalSize);
        WriteUInt64(bytes, 24, WhiteBookDsc1IdentityHash);
        WriteUInt64(bytes, 32, 0xC011EC7EUL);
        WriteUInt16(bytes, 40, (ushort)operation);
        WriteUInt16(bytes, 42, (ushort)elementType);
        WriteUInt16(bytes, 44, (ushort)shape);
        WriteUInt16(bytes, Dsc1RangeEncodingOffset, (ushort)DmaStreamComputeRangeEncoding.InlineContiguous);
        WriteUInt16(bytes, 48, sourceRangeCount);
        WriteUInt16(bytes, 50, destinationRangeCount);
        WriteUInt16(bytes, Dsc1PartialCompletionPolicyOffset, (ushort)DmaStreamComputePartialCompletionPolicy.AllOrNone);
        WriteUInt32(bytes, 64, 77);
        WriteUInt32(bytes, 68, 1);
        WriteUInt32(bytes, 72, 2);
        WriteUInt32(bytes, 76, DmaStreamComputeDescriptor.CanonicalLane6DeviceId);
        WriteUInt64(bytes, 80, WhiteBookOwnerDomainTag);
        WriteUInt32(bytes, 96, (uint)sourceRangeTableOffset);
        WriteUInt32(bytes, 100, (uint)destinationRangeTableOffset);

        for (int index = 0; index < sourceRangeCount; index++)
        {
            WriteRange(
                bytes,
                sourceRangeTableOffset + (index * DscRangeEntrySize),
                0x1000UL + ((ulong)index * 0x1000UL),
                16);
        }

        WriteRange(bytes, destinationRangeTableOffset, 0x9000, 16);
        return bytes;
    }

    private static DmaStreamComputeDsc2ValidationResult ParseDsc2(byte[] descriptorBytes)
    {
        DmaStreamComputeDescriptorReference reference = CreateDsc2Reference(descriptorBytes);
        DmaStreamComputeStructuralReadResult structuralRead =
            DmaStreamComputeDescriptorParser.ReadDsc2StructuralOwnerBinding(
                descriptorBytes,
                reference);
        DmaStreamComputeOwnerGuardDecision guardDecision = structuralRead.IsValid
            ? CreateDsc2GuardDecision(descriptorBytes, reference)
            : default;
        return DmaStreamComputeDescriptorParser.ParseDsc2ParserOnly(
            descriptorBytes,
            DmaStreamComputeDsc2CapabilitySet.ParserOnlyFoundation,
            guardDecision,
            reference);
    }

    private static DmaStreamComputeDescriptorReference CreateDsc2Reference(byte[] descriptorBytes) =>
        new(
            descriptorAddress: 0xD52000,
            descriptorSize: (uint)descriptorBytes.Length,
            descriptorIdentityHash: WhiteBookDsc2IdentityHash);

    private static DmaStreamComputeOwnerGuardDecision CreateDsc2GuardDecision(
        byte[] descriptorBytes,
        DmaStreamComputeDescriptorReference? descriptorReference = null)
    {
        DmaStreamComputeStructuralReadResult structuralRead =
            DmaStreamComputeDescriptorParser.ReadDsc2StructuralOwnerBinding(
                descriptorBytes,
                descriptorReference);
        if (!structuralRead.IsValid)
        {
            throw new InvalidOperationException(structuralRead.Message);
        }

        DmaStreamComputeOwnerBinding ownerBinding =
            structuralRead.RequireOwnerBindingForGuard();
        DmaStreamComputeOwnerGuardContext context = new DmaStreamComputeOwnerGuardContext(
            ownerBinding.OwnerVirtualThreadId,
            ownerBinding.OwnerContextId,
            ownerBinding.OwnerCoreId,
            ownerBinding.OwnerPodId,
            ownerBinding.OwnerDomainTag,
            activeDomainCertificate: ownerBinding.OwnerDomainTag,
            deviceId: ownerBinding.DeviceId);
        return new SafetyVerifier().EvaluateDmaStreamComputeOwnerGuard(
            ownerBinding,
            context);
    }

    private static byte[] BuildDsc2(
        params Dsc2ExtensionSpec[] extensions) =>
        BuildDsc2(DmaStreamComputeDsc2AddressSpaceKind.Physical, extensions);

    private static byte[] BuildDsc2(
        DmaStreamComputeDsc2AddressSpaceKind addressSpace,
        params Dsc2ExtensionSpec[] extensions)
    {
        byte[][] extensionBlocks = extensions.Select(BuildDsc2ExtensionBlock).ToArray();
        int extensionTableBytes = extensionBlocks.Sum(static block => block.Length);
        int totalSize = DmaStreamComputeDescriptorParser.Dsc2HeaderSize + extensionTableBytes;
        byte[] bytes = new byte[totalSize];

        WriteUInt32(bytes, 0, DmaStreamComputeDescriptorParser.Dsc2Magic);
        WriteUInt16(bytes, 4, DmaStreamComputeDescriptorParser.Dsc2MajorVersion);
        WriteUInt16(bytes, 6, DmaStreamComputeDescriptorParser.Dsc2MinorVersion);
        WriteUInt16(bytes, Dsc2HeaderSizeOffset, DmaStreamComputeDescriptorParser.Dsc2HeaderSize);
        WriteUInt32(bytes, 12, (uint)totalSize);
        WriteUInt32(bytes, Dsc2ExtensionTableOffsetOffset, (uint)DmaStreamComputeDescriptorParser.Dsc2HeaderSize);
        WriteUInt16(bytes, Dsc2ExtensionCountOffset, (ushort)extensionBlocks.Length);
        WriteUInt32(bytes, Dsc2ExtensionTableByteSizeOffset, (uint)extensionTableBytes);
        WriteUInt64(bytes, 32, WhiteBookDsc2IdentityHash);
        WriteUInt64(bytes, 40, WhiteBookDsc2CapabilityHash);
        WriteUInt16(bytes, 56, 1);
        WriteUInt16(bytes, 58, (ushort)DmaStreamComputeDsc2ParserStatus.ParserOnly);
        WriteUInt32(bytes, 60, 77);
        WriteUInt32(bytes, 64, 1);
        WriteUInt32(bytes, 68, 2);
        WriteUInt32(bytes, 72, WhiteBookDsc2DeviceId);
        WriteUInt16(bytes, 76, (ushort)addressSpace);
        WriteUInt64(bytes, 80, WhiteBookOwnerDomainTag);

        int cursor = DmaStreamComputeDescriptorParser.Dsc2HeaderSize;
        foreach (byte[] extensionBlock in extensionBlocks)
        {
            extensionBlock.CopyTo(bytes.AsSpan(cursor));
            cursor += extensionBlock.Length;
        }

        return bytes;
    }

    private static byte[] BuildDsc2ExtensionBlock(Dsc2ExtensionSpec extension)
    {
        int length = DmaStreamComputeDescriptorParser.Dsc2ExtensionBlockHeaderSize + extension.Payload.Length;
        byte[] bytes = new byte[length];
        WriteUInt16(bytes, 0, extension.RawType);
        WriteUInt16(bytes, 2, extension.Version);
        WriteUInt16(bytes, 4, (ushort)extension.Flags);
        WriteUInt16(bytes, 6, extension.Alignment);
        WriteUInt32(bytes, 8, (uint)length);
        WriteUInt16(bytes, 12, (ushort)extension.CapabilityId);
        WriteUInt16(bytes, 14, DmaStreamComputeDescriptorParser.Dsc2MajorVersion);
        extension.Payload.CopyTo(bytes.AsSpan(DmaStreamComputeDescriptorParser.Dsc2ExtensionBlockHeaderSize));
        return bytes;
    }

    private static Dsc2ExtensionSpec PhysicalAddressSpaceExtension() =>
        AddressSpaceExtension(
            DmaStreamComputeDsc2AddressSpaceKind.Physical,
            WhiteBookDsc2DeviceId,
            WhiteBookOwnerDomainTag,
            mappingEpoch: 0);

    private static Dsc2ExtensionSpec AddressSpaceExtension(
        DmaStreamComputeDsc2AddressSpaceKind addressSpace,
        uint deviceId,
        ulong domainTag,
        ulong mappingEpoch)
    {
        byte[] payload = new byte[32];
        WriteUInt16(payload, 0, (ushort)addressSpace);
        WriteUInt32(payload, 4, deviceId);
        WriteUInt64(payload, 8, domainTag);
        WriteUInt64(payload, 16, mappingEpoch);
        return KnownDsc2Extension(
            DmaStreamComputeDsc2ExtensionType.AddressSpace,
            DmaStreamComputeDsc2CapabilityId.AddressSpace,
            payload);
    }

    private static Dsc2ExtensionSpec StridedRangeExtension(
        DmaStreamComputeDsc2AccessKind accessKind,
        ushort elementSize,
        uint elementCount,
        ulong baseAddress,
        ulong strideBytes)
    {
        byte[] payload = new byte[32];
        WriteUInt16(payload, 0, (ushort)accessKind);
        WriteUInt16(payload, 2, elementSize);
        WriteUInt32(payload, 4, elementCount);
        WriteUInt64(payload, 8, baseAddress);
        WriteUInt64(payload, 16, strideBytes);
        return KnownDsc2Extension(
            DmaStreamComputeDsc2ExtensionType.StridedRange,
            DmaStreamComputeDsc2CapabilityId.StridedRange,
            payload);
    }

    private static Dsc2ExtensionSpec KnownDsc2Extension(
        DmaStreamComputeDsc2ExtensionType extensionType,
        DmaStreamComputeDsc2CapabilityId capabilityId,
        byte[] payload) =>
        new(
            (ushort)extensionType,
            DmaStreamComputeDsc2ExtensionFlags.Required |
            DmaStreamComputeDsc2ExtensionFlags.Semantic,
            capabilityId,
            payload);

    private static void WriteRange(byte[] bytes, int offset, ulong address, ulong length)
    {
        WriteUInt64(bytes, offset, address);
        WriteUInt64(bytes, offset + 8, length);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteUInt64(byte[] bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireContains(string text, string expected, string failureMessage)
    {
        if (text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    private static void RequireNotContains(string text, string forbidden, string failureMessage)
    {
        if (text.IndexOf(forbidden, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    private static string TrimForEvidence(string text)
    {
        const int MaxLength = 180;
        string normalized = text.ReplaceLineEndings(" ");
        return normalized.Length <= MaxLength
            ? normalized
            : normalized[..MaxLength] + "...";
    }

    private static string ReadRepoFile(string relativePath)
    {
        string fullPath = Path.Combine(
            FindRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Missing repository file: {relativePath}", fullPath);
        }

        return File.ReadAllText(fullPath);
    }

    private static string ReadAllSourceText(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Missing source directory: {root}");
        }

        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(static file => !IsGeneratedPath(file))
                .Select(File.ReadAllText));
    }

    private static bool IsGeneratedPath(string filePath) =>
        filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (HasRepoLayout(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (HasRepoLayout(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate HybridCPU ISE repository root.");
    }

    private static bool HasRepoLayout(string path) =>
        Directory.Exists(Path.Combine(path, "Documentation")) &&
        Directory.Exists(Path.Combine(path, "HybridCPU_ISE")) &&
        Directory.Exists(Path.Combine(path, "TestAssemblerConsoleApps"));

    private sealed record Dsc2ExtensionSpec(
        ushort RawType,
        DmaStreamComputeDsc2ExtensionFlags Flags,
        DmaStreamComputeDsc2CapabilityId CapabilityId,
        byte[] Payload,
        ushort Version = 1,
        ushort Alignment = 8);
}
