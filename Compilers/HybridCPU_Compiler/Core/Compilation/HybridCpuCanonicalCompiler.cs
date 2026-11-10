using System.Collections.Generic;
using System.Diagnostics;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Contracts;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core
{
    /// <summary>
    /// Stateless canonical compiler: IR construction → scheduling → bundling → lowering → serialization.
    /// This is the single production entry point for all compilation paths.
    /// </summary>
    public static class HybridCpuCanonicalCompiler
    {

        /// <summary>
        /// Compiles a VLIW instruction stream through the full canonical pipeline.
        /// </summary>
        public static HybridCpuCompiledProgram CompileProgram(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            NativeFrontendMode frontendMode = NativeFrontendMode.NativeVLIW,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            Action<string, string>? progressObserver = null,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            return CompileProgramCore(
                virtualThreadId,
                instructions,
                labelDeclarations,
                entryPointDeclarations,
                frontendMode,
                bundleAnnotations,
                domainTag,
                progressObserver,
                controlFlowTargetReferences,
                metricsCollector: null,
                resourceShadowCollector: null);
        }

        /// <summary>
        /// Compiles with caller cancellation at safe stage/scheduler boundaries. No runtime writes
        /// occur and no partial compiled program is returned; individual backend operations remain synchronous.
        /// </summary>
        public static HybridCpuCompiledProgram CompileProgram(
            CancellationToken cancellationToken,
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            NativeFrontendMode frontendMode = NativeFrontendMode.NativeVLIW,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            Action<string, string>? progressObserver = null,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            return CompileProgramCore(virtualThreadId, instructions, labelDeclarations,
                entryPointDeclarations, frontendMode, bundleAnnotations, domainTag, progressObserver,
                controlFlowTargetReferences, metricsCollector: null, resourceShadowCollector: null,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Compiles through the unchanged canonical pipeline and returns optional observational
        /// Phase 00 metrics. Resource samples are telemetry and never code-selection inputs.
        /// </summary>
        public static HybridCpuCompilationMetricsResultV1 CompileProgramWithMetrics(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            CompilerScheduleMetricsRequestV1 metricsRequest,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            NativeFrontendMode frontendMode = NativeFrontendMode.NativeVLIW,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            Action<string, string>? progressObserver = null,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            ArgumentNullException.ThrowIfNull(metricsRequest);
            if (!metricsRequest.Enabled)
            {
                HybridCpuCompiledProgram disabledProgram = CompileProgram(
                    virtualThreadId,
                    instructions,
                    labelDeclarations,
                    entryPointDeclarations,
                    frontendMode,
                    bundleAnnotations,
                    domainTag,
                    progressObserver,
                    controlFlowTargetReferences);
                return new HybridCpuCompilationMetricsResultV1(disabledProgram, null, null);
            }

            CompilerScheduleMetricsCollectorV1 metricsCollector =
                CompilerScheduleMetricsCollectorV1.CreateForInput(
                    virtualThreadId,
                    instructions,
                    metricsRequest);
            long startTimestamp = Stopwatch.GetTimestamp();
            long startManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
            HybridCpuCompiledProgram compiledProgram = CompileProgramCore(
                virtualThreadId,
                instructions,
                labelDeclarations,
                entryPointDeclarations,
                frontendMode,
                bundleAnnotations,
                domainTag,
                progressObserver,
                controlFlowTargetReferences,
                metricsCollector,
                resourceShadowCollector: null);
            CompilerCompileResourceTelemetryV1 resourceTelemetry =
                CompilerCompileResourceTelemetryV1.Measure(startTimestamp, startManagedMemoryBytes);
            return new HybridCpuCompilationMetricsResultV1(
                compiledProgram,
                metricsCollector.Complete(compiledProgram),
                resourceTelemetry);
        }

        /// <summary>
        /// Runs the existing compiler decision path with a default-off Phase 01 resource shadow.
        /// The shadow report is evidence only and cannot select scheduling or placement decisions.
        /// </summary>
        public static HybridCpuCompilationResourceShadowResultV1 CompileProgramWithResourceShadow(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            NativeFrontendMode frontendMode = NativeFrontendMode.NativeVLIW,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            Action<string, string>? progressObserver = null,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            CompilerResourceShadowCollectorV1 collector = new CompilerResourceShadowCollectorV1();
            HybridCpuCompiledProgram compiledProgram = CompileProgramCore(
                virtualThreadId,
                instructions,
                labelDeclarations,
                entryPointDeclarations,
                frontendMode,
                bundleAnnotations,
                domainTag,
                progressObserver,
                controlFlowTargetReferences,
                metricsCollector: null,
                resourceShadowCollector: collector);
            return new HybridCpuCompilationResourceShadowResultV1(
                compiledProgram,
                collector.Complete());
        }

        /// <summary>
        /// Explicit default-off Phase 02 A/B entry point. The search is bounded only by versioned
        /// counters and reuses the existing structural checker and exact placement search.
        /// </summary>
        public static HybridCpuCompiledProgram CompileProgramWithJointCycleComposition(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            HybridCpuCycleSearchOptionsV1? searchOptions = null,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            NativeFrontendMode frontendMode = NativeFrontendMode.NativeVLIW,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            Action<string, string>? progressObserver = null,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            return CompileProgramCore(
                virtualThreadId,
                instructions,
                labelDeclarations,
                entryPointDeclarations,
                frontendMode,
                bundleAnnotations,
                domainTag,
                progressObserver,
                controlFlowTargetReferences,
                metricsCollector: null,
                resourceShadowCollector: null,
                useJointCycleComposition: true,
                jointCycleSearchOptions: searchOptions ?? HybridCpuCycleSearchOptionsV1.Default);
        }

        /// <summary>
        /// Compiles through the exact default path, then observes the immutable Phase 03
        /// topology against the completed schedule. The report cannot select code or alter
        /// emitted artifacts and does not represent runtime legality.
        /// </summary>
        public static HybridCpuCompilationTopologyShadowResultV1 CompileProgramWithTopologyShadow(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            HybridCpuMachineTopologyV1? topology = null,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            NativeFrontendMode frontendMode = NativeFrontendMode.NativeVLIW,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            Action<string, string>? progressObserver = null,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            HybridCpuCompiledProgram compiledProgram = CompileProgram(
                virtualThreadId, instructions, labelDeclarations, entryPointDeclarations,
                frontendMode, bundleAnnotations, domainTag, progressObserver,
                controlFlowTargetReferences);
            HybridCpuTopologyResourceModelV1 model = new HybridCpuTopologyResourceModelV1(topology);
            CompilerTopologyShadowReportV1 report =
                CompilerTopologyShadowEvaluatorV1.Evaluate(compiledProgram.ProgramSchedule, model);
            return new HybridCpuCompilationTopologyShadowResultV1(compiledProgram, report);
        }

        /// <summary>Explicit advisory PGO lane; rejected profiles use the unchanged default pipeline.</summary>
        public static HybridCpuProfileCompilationResultV1 CompileProgramWithProfile(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            HybridCpuProfileCompilationRequestV1 request,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations = null,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations = null,
            NativeFrontendMode frontendMode = NativeFrontendMode.NativeVLIW,
            IrBundleAnnotations? bundleAnnotations = null,
            ulong domainTag = 0,
            Action<string, string>? progressObserver = null,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences = null)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!Enum.IsDefined(request.SelectionPolicy))
                throw new ArgumentOutOfRangeException(nameof(request), "Unknown profile selection policy.");
            string inputIdentity = "";
            string? activeDigest = null;
            string status = "profile-off";
            string basePolicy = HybridCpuCompilationPolicyV1.Default.Digest;
            HybridCpuProfileDecisionTraceCollectorV1? trace = request.CaptureDecisionTrace ? new HybridCpuProfileDecisionTraceCollectorV1() : null;
            HybridCpuCompiledProgram compiled = CompileProgramCore(virtualThreadId, instructions, labelDeclarations,
                entryPointDeclarations, frontendMode, bundleAnnotations, domainTag, progressObserver,
                controlFlowTargetReferences, metricsCollector: null, resourceShadowCollector: null,
                configureProfile: (program, scheduler, fallback) =>
                {
                    scheduler.ProfileDecisionTrace = trace;
                    inputIdentity = CompilerScheduleFingerprintV1.HashIdentity(
                        $"canonical-profile-input/v1:{HybridCpuCompilerContract.Version}:{HybridCpuMachineDescriptionV1.Default.ContractDigest}:{frontendMode}:{basePolicy}:{CompilerScheduleFingerprintV1.HashProgramInput(program)}");
                    if (!request.Enabled) return;
                    TelemetryProfileReader reader = TelemetryProfileReader.CreateVersionedJson(request.ProfileJson ?? "", inputIdentity, out string? digest);
                    if (!reader.HasProfile) { status = "profile-disabled-invalid-or-stale"; return; }
                    if (fallback) { status = "profile-disabled-scheduler-cap"; return; }
                    status = "profile-active";
                    activeDigest = digest;
                    scheduler.ProfileReader = reader;
                    scheduler.UseProfileGuidedScheduling = true;
                    scheduler.UseClassPressureSmoothingTieBreaks = request.SelectionPolicy == HybridCpuProfileSelectionPolicyV1.ClassPressureSmoothing;
                });
            string selector = request.SelectionPolicy == HybridCpuProfileSelectionPolicyV1.ClassPressureSmoothing
                ? "class-pressure-smoothing/v2" : "class-pressure-priority/v1";
            string options = activeDigest is null ? basePolicy : CompilerScheduleFingerprintV1.HashIdentity(
                $"canonical-profile-options/v1:{basePolicy}:{selector}:{activeDigest}");
            return new HybridCpuProfileCompilationResultV1(compiled, inputIdentity, status, activeDigest,
                options, CompilerScheduleFingerprintV1.HashSchedule(compiled.ProgramSchedule))
            { DecisionTrace = trace?.Complete() };
        }

        private static HybridCpuCompiledProgram CompileProgramCore(
            byte virtualThreadId,
            ReadOnlySpan<HybridCpuInstructionWord> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations,
            NativeFrontendMode frontendMode,
            IrBundleAnnotations? bundleAnnotations,
            ulong domainTag,
            Action<string, string>? progressObserver,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences,
            CompilerScheduleMetricsCollectorV1? metricsCollector,
            CompilerResourceShadowCollectorV1? resourceShadowCollector,
            bool useJointCycleComposition = false,
            HybridCpuCycleSearchOptionsV1? jointCycleSearchOptions = null,
            Action<IrProgram, HybridCpuLocalListScheduler, bool>? configureProfile = null,
            CancellationToken cancellationToken = default)
        {
            ValidateFrontendMode(frontendMode);
            cancellationToken.ThrowIfCancellationRequested();
            if (cancellationToken.CanBeCanceled)
            {
                Action<string, string>? callerObserver = progressObserver;
                progressObserver = (stage, message) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    callerObserver?.Invoke(stage, message);
                    cancellationToken.ThrowIfCancellationRequested();
                };
            }

            progressObserver?.Invoke("IrBuildStarting", $"Building IR for {instructions.Length} instruction(s).");
            HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
            HybridCpuSchedulingPolicyV1 policy = HybridCpuSchedulingPolicyV1.Default with
            {
                UseJointCycleComposition = useJointCycleComposition,
                JointCycleSearchOptions = jointCycleSearchOptions ?? HybridCpuCycleSearchOptionsV1.Default
            };
            HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler
            {
                MetricsCollector = metricsCollector,
                ResourceShadowCollector = resourceShadowCollector,
                UseJointCycleComposition = policy.UseJointCycleComposition,
                JointCycleSearchOptions = policy.JointCycleSearchOptions
            };
            HybridCpuProgramOrderLocalScheduler fallbackScheduler = new HybridCpuProgramOrderLocalScheduler
            {
                ResourceShadowCollector = resourceShadowCollector
            };
            HybridCpuBundleFormer bundler = HybridCpuCompilationPolicyV1.Default.Bundling.CreateBundler();
            bundler.MetricsCollector = metricsCollector;
            bundler.ResourceShadowCollector = resourceShadowCollector;

            IrProgram program = builder.BuildProgram(
                virtualThreadId,
                instructions,
                labelDeclarations,
                entryPointDeclarations,
                bundleAnnotations: bundleAnnotations,
                domainTag: domainTag,
                controlFlowTargetReferences: controlFlowTargetReferences);
            IrFrontendAdapterResultV1 frontendBoundary = CanonicalIrFrontendBoundaryV1.Validate(program);
            if (frontendBoundary.Status != IrFrontendAdapterStatus.Success)
            {
                throw new NotSupportedException(frontendBoundary.Diagnostics[0].Message);
            }
            CompilerCapabilityValidationResult capabilityBoundary = CompilerCapabilityValidator.ValidateRequired(
                program.Contract.RequiredCapabilities,
                CompilerCrossLayerSchemaCatalogV1.NativeV1);
            if (!capabilityBoundary.IsSatisfied)
            {
                throw new NotSupportedException(capabilityBoundary.Reason);
            }
            metricsCollector?.CaptureProgramInput(program);
            configureProfile?.Invoke(program, scheduler, policy.RequiresProgramOrderFallback(program));
            progressObserver?.Invoke("IrBuild", $"IR contains {program.Instructions.Count} instruction(s) across {program.BasicBlocks.Count} basic block(s).");

            progressObserver?.Invoke("ScheduleStarting", $"Scheduling {program.BasicBlocks.Count} basic block(s).");
            IrProgramSchedule ScheduleProgram(IrProgram input)
            {
                if (!policy.RequiresProgramOrderFallback(input))
                    return scheduler.ScheduleProgram(cancellationToken, input);
                metricsCollector?.RecordSchedulerCapHit(policy.ProgramOrderReason(program));
                progressObserver?.Invoke(
                    "ScheduleFallback",
                    $"Program-order fallback engaged for {program.Instructions.Count} instruction(s); at least one block exceeds the bounded local-list scheduling budget.");
                return fallbackScheduler.ScheduleProgram(cancellationToken, input);
            }
            HybridCpuCompiledProgram compiled = HybridCpuIrProgramCompiler.Compile(cancellationToken, program, ScheduleProgram, bundler, progressObserver);
            cancellationToken.ThrowIfCancellationRequested();
            return compiled;
        }

        private static void ValidateFrontendMode(NativeFrontendMode frontendMode)
        {
            if (frontendMode != NativeFrontendMode.NativeVLIW)
            {
                throw new ArgumentOutOfRangeException(nameof(frontendMode), frontendMode, "Unknown frontend mode.");
            }
        }



    }
}
