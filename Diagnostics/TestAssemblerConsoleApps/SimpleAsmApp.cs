using System;
using System.Collections.Generic;
using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed partial class SimpleAsmApp
{
    private const ulong EmissionBaseAddress = 0;
    private const ulong ScalarDataBase = 0x100000;
    private const ulong VectorSourceBase = 0x101000;
    private const ulong VectorDestinationBase = 0x102000;
    private const ulong LkProbeBase = 0x120000;
    private const ulong BnmczProbeBase = 0x140000;
    private const int RegistersPerVirtualThread = 32;
    private const int MaxInstructions = 1024;

    private readonly VLIW_Instruction[] _instructions = new VLIW_Instruction[MaxInstructions];
    private readonly InstructionSlotMetadata[] _instructionSlotMetadata = new InstructionSlotMetadata[MaxInstructions];
    private int _instructionCount;
    private int _coreId;
    private readonly List<byte> _emittedVirtualThreadIds = [];
    private FrontendMode _frontendMode;
    private SimpleAsmProgramVariant _programVariant;
    private readonly DiagnosticRuntimeSession _runtime = new();
    private ulong _requestedWorkloadIterations = DiagnosticRunProfile.DefaultWorkloadIterations;
    private int _loopBodyInstructionCount;
    private ulong _dynamicRetirementTarget;
    private string _workloadShape = "single-pass";
    private ulong _sliceExecutionCount;
    private ulong _referenceSliceIterations;

    private readonly record struct CompilationSummary(
        int BundleCount,
        uint FirstOpcode,
        bool FirstOpcodeRegistered,
        CompilerPackingDiagnostics CompilerPacking);

    private readonly record struct WorkloadSlicePlan(
        ulong ReferenceSliceIterations,
        ulong FullSliceIterations,
        ulong FullSliceExecutions,
        ulong RemainderSliceIterations)
    {
        public ulong TotalSliceExecutions =>
            FullSliceExecutions + (RemainderSliceIterations > 0 ? 1UL : 0UL);
    }

    private readonly record struct CompiledWorkloadSlice(
        ulong SliceIterations,
        int InstructionCount,
        CompilationSummary Compilation);

    private readonly record struct ExecutionSnapshot(
        PipelineExecutionOutcome Outcome,
        Processor.CPU_Core.PipelineControl Pipeline,
        PerformanceReport Performance);

    public void SimpleAsseblerProgram()
    {
        SimpleAsseblerProgram(SimpleAsmAppMode.WithVirtualThreads);
    }

    public void SimpleAsseblerProgram(SimpleAsmAppMode mode)
    {
        SimpleAsseblerProgram(mode, FrontendMode.NativeVLIW);
    }

    public void SimpleAsseblerProgram(SimpleAsmAppMode mode, FrontendMode frontendMode)
    {
        _requestedWorkloadIterations = ValidateWorkloadIterations(_requestedWorkloadIterations);
        _frontendMode = frontendMode;
        PrepareMeasurementSession();
        PublishLifecycleCheckpoint("ProgramInitialization", "Processor state initialized for diagnostic workload.");

        WorkloadSlicePlan plan = CreateWorkloadSlicePlan(mode, _requestedWorkloadIterations);
        CompiledWorkloadSlice slice = BuildCompiledWorkloadSlice(mode, frontendMode, plan.ReferenceSliceIterations, static (_, _) => { });
        _referenceSliceIterations = slice.SliceIterations;
        _sliceExecutionCount = 1;
        _dynamicRetirementTarget = (ulong)slice.InstructionCount;
        _loopBodyInstructionCount = slice.InstructionCount;
        PublishLifecycleCheckpoint("InstructionEmission", "Reference workload slice emitted.");
    }

    public SimpleAsmAppMetrics ExecuteMeasuredProgram()
    {
        return ExecuteMeasuredProgram(
            SimpleAsmAppMode.WithVirtualThreads,
            FrontendMode.NativeVLIW,
            DiagnosticRunProfile.DefaultWorkloadIterations);
    }

    public SimpleAsmAppMetrics ExecuteMeasuredProgram(SimpleAsmAppMode mode)
    {
        return ExecuteMeasuredProgram(
            mode,
            FrontendMode.NativeVLIW,
            DiagnosticRunProfile.DefaultWorkloadIterations);
    }

    public SimpleAsmAppMetrics ExecuteMeasuredProgram(SimpleAsmAppMode mode, FrontendMode frontendMode)
    {
        return ExecuteMeasuredProgram(mode, frontendMode, DiagnosticRunProfile.DefaultWorkloadIterations);
    }

    public SimpleAsmAppMetrics ExecuteMeasuredProgram(
        SimpleAsmAppMode mode,
        FrontendMode frontendMode,
        ulong workloadIterations)
    {
        string compilerStage = "NotStarted";
        string decoderStage = "NotStarted";
        string likelyFailingStage = "Unknown";
        string failureMessage = string.Empty;
        uint firstOpcode = 0;
        bool firstOpcodeRegistered = false;
        int bundleCount = 0;
        bool frontendSupported = true;
        CompilerPackingDiagnostics compilerPacking = CompilerPackingDiagnostics.Empty;
        ShowcaseRuntimeReport showcaseRuntime = ShowcaseRuntimeReport.Empty;
        Processor.CPU_Core.PipelineControl measuredPipeline = default;
        PerformanceReport measuredPerformance = new();
        bool hasMeasuredSnapshot = false;
        void PublishCompilerSubStage(string stage, string detail)
        {
            compilerStage = stage;
            PublishLifecycleCheckpoint(stage, detail);
        }

        try
        {
            _requestedWorkloadIterations = ValidateWorkloadIterations(workloadIterations);
            _frontendMode = frontendMode;
            PrepareMeasurementSession();
            PublishLifecycleCheckpoint("ProgramInitialization", "Processor state initialized for diagnostic workload.");

            WorkloadSlicePlan plan = CreateWorkloadSlicePlan(mode, _requestedWorkloadIterations);
            _referenceSliceIterations = plan.ReferenceSliceIterations;
            _sliceExecutionCount = plan.TotalSliceExecutions;

            CompiledWorkloadSlice? referenceSlice = null;

            if (plan.FullSliceExecutions > 0)
            {
                PublishLifecycleCheckpoint(
                    "InstructionEmission",
                    $"Building reference workload slice for {plan.FullSliceIterations:N0} SPEC-like iterations.");
                CompiledWorkloadSlice fullSlice = BuildCompiledWorkloadSlice(mode, frontendMode, plan.FullSliceIterations, PublishCompilerSubStage);
                referenceSlice = fullSlice;
                _loopBodyInstructionCount = fullSlice.InstructionCount;
                _dynamicRetirementTarget = checked(plan.FullSliceExecutions * (ulong)fullSlice.InstructionCount);

                ExecutionSnapshot execution = ExecuteCompiledSliceRepeatedly(mode, fullSlice, plan.FullSliceExecutions);
                if (!hasMeasuredSnapshot)
                {
                    measuredPipeline = execution.Pipeline;
                    measuredPerformance = execution.Performance;
                    hasMeasuredSnapshot = true;
                }
                else
                {
                    measuredPipeline = AddPipelineControls(measuredPipeline, execution.Pipeline);
                    measuredPerformance = AddPerformanceReports(measuredPerformance, execution.Performance);
                }
            }

            if (plan.RemainderSliceIterations > 0)
            {
                PublishLifecycleCheckpoint(
                    "InstructionEmission",
                    $"Building remainder workload slice for {plan.RemainderSliceIterations:N0} SPEC-like iterations.");
                CompiledWorkloadSlice remainderSlice = BuildCompiledWorkloadSlice(mode, frontendMode, plan.RemainderSliceIterations, PublishCompilerSubStage);
                referenceSlice ??= remainderSlice;
                _dynamicRetirementTarget = checked(_dynamicRetirementTarget + (ulong)remainderSlice.InstructionCount);
                _loopBodyInstructionCount = Math.Max(_loopBodyInstructionCount, remainderSlice.InstructionCount);

                ExecutionSnapshot execution = ExecuteCompiledSliceRepeatedly(mode, remainderSlice, 1);
                if (!hasMeasuredSnapshot)
                {
                    measuredPipeline = execution.Pipeline;
                    measuredPerformance = execution.Performance;
                    hasMeasuredSnapshot = true;
                }
                else
                {
                    measuredPipeline = AddPipelineControls(measuredPipeline, execution.Pipeline);
                    measuredPerformance = AddPerformanceReports(measuredPerformance, execution.Performance);
                }
            }

            if (referenceSlice is null)
            {
                throw new InvalidOperationException("No executable workload slice was produced for the requested iteration count.");
            }

            RestoreReferenceSliceMetadata(mode, referenceSlice.Value.SliceIterations);
            bundleCount = referenceSlice.Value.Compilation.BundleCount;
            compilerPacking = referenceSlice.Value.Compilation.CompilerPacking;
            firstOpcode = referenceSlice.Value.Compilation.FirstOpcode;
            firstOpcodeRegistered = referenceSlice.Value.Compilation.FirstOpcodeRegistered;
            decoderStage = firstOpcodeRegistered ? "InstructionRegistry" : "InstructionRegistryCoverageGap";
            likelyFailingStage = firstOpcodeRegistered ? "NoGrossFailureDetected" : "InstructionRegistryCoverage";
            PublishLifecycleCheckpoint(
                decoderStage,
                firstOpcodeRegistered
                    ? $"First opcode 0x{firstOpcode:X} resolved in registry."
                    : $"First opcode 0x{firstOpcode:X} is missing from registry coverage.");

            likelyFailingStage = "NoGrossFailureDetected";
            if (mode == SimpleAsmAppMode.RefactorShowcase)
            {
                likelyFailingStage = "ShowcaseRuntimeProbes";
                PublishLifecycleCheckpoint(likelyFailingStage, "Runtime-safe showcase probes are starting.");
                showcaseRuntime = RunShowcaseRuntimeProbes();
                decoderStage = "ShowcaseRuntimeProbes";
                PublishLifecycleCheckpoint(decoderStage, "Runtime-safe showcase probes completed.");
                likelyFailingStage = "NoGrossFailureDetected";
            }

            PublishLifecycleCheckpoint("Completed", "ExecuteMeasuredProgram completed successfully.");

            return CreateMetrics(
                hasMeasuredSnapshot ? measuredPipeline : CapturePipelineControl(),
                hasMeasuredSnapshot ? measuredPerformance : CapturePerformanceReport(),
                compilerStage,
                decoderStage,
                likelyFailingStage,
                failureMessage,
                firstOpcode,
                firstOpcodeRegistered,
                bundleCount,
                frontendMode,
                frontendSupported,
                _programVariant,
                compilerPacking,
                showcaseRuntime);
        }
        catch (NotSupportedException ex)
        {
            frontendSupported = false;
            failureMessage = ex.Message;
            likelyFailingStage = compilerStage;
            PublishLifecycleCheckpoint("NotSupported", ex.Message);

            return CreateMetrics(
                CapturePipelineControl(),
                CapturePerformanceReport(),
                compilerStage,
                decoderStage,
                likelyFailingStage,
                failureMessage,
                firstOpcode,
                firstOpcodeRegistered,
                bundleCount,
                frontendMode,
                frontendSupported,
                _programVariant,
                compilerPacking,
                showcaseRuntime);
        }
        catch (InvalidOperationException ex)
        {
            failureMessage = ex.Message;
            likelyFailingStage = decoderStage != "NotStarted" ? decoderStage : compilerStage;
            PublishLifecycleCheckpoint("InvalidOperation", ex.Message);

            return CreateMetrics(
                CapturePipelineControl(),
                CapturePerformanceReport(),
                compilerStage,
                decoderStage,
                likelyFailingStage,
                failureMessage,
                firstOpcode,
                firstOpcodeRegistered,
                bundleCount,
                frontendMode,
                frontendSupported,
                _programVariant,
                compilerPacking,
                showcaseRuntime);
        }
        catch (TypeInitializationException ex) when (IsOpcodeRegistryInitializationFailure(ex))
        {
            failureMessage = BuildOpcodeRegistryInitializationFailureMessage(ex);
            likelyFailingStage = decoderStage != "NotStarted" ? decoderStage : compilerStage;
            PublishLifecycleCheckpoint("OpcodeRegistryInitializationFailure", failureMessage);

            return CreateMetrics(
                CapturePipelineControl(),
                CapturePerformanceReport(),
                compilerStage,
                decoderStage,
                likelyFailingStage,
                failureMessage,
                firstOpcode,
                firstOpcodeRegistered,
                bundleCount,
                frontendMode,
                frontendSupported,
                _programVariant,
                compilerPacking,
                showcaseRuntime);
        }
        finally
        {
            _runtime.ResetProfiling();
        }
    }

    private void PrepareMeasurementSession()
    {
        _coreId = _runtime.CoreId;
        ResetProgramImageState();
        ResetProgressState();
        _loopBodyInstructionCount = 0;
        _dynamicRetirementTarget = 0;
        _workloadShape = "single-pass";
        _sliceExecutionCount = 0;
        _referenceSliceIterations = 0;
        _runtime.BootstrapCompilerRuntime();
    }

    private void ResetProgramImageState()
    {
        _instructionCount = 0;
        _emittedVirtualThreadIds.Clear();
        Array.Clear(_instructionSlotMetadata, 0, _instructionSlotMetadata.Length);
    }

    private static ulong ValidateWorkloadIterations(ulong workloadIterations)
    {
        return workloadIterations == 0
            ? throw new ArgumentOutOfRangeException(nameof(workloadIterations), "Workload iterations must be positive.")
            : workloadIterations;
    }

    private static WorkloadSlicePlan CreateWorkloadSlicePlan(SimpleAsmAppMode mode, ulong requestedIterations)
    {
        ulong sliceCapacity = ComputeSliceCapacity(mode);
        ulong fullSliceExecutions = requestedIterations / sliceCapacity;
        ulong remainderSliceIterations = requestedIterations % sliceCapacity;
        ulong referenceSliceIterations = fullSliceExecutions > 0
            ? sliceCapacity
            : remainderSliceIterations;

        return new WorkloadSlicePlan(
            referenceSliceIterations,
            sliceCapacity,
            fullSliceExecutions,
            remainderSliceIterations);
    }

    private static ulong ComputeSliceCapacity(SimpleAsmAppMode mode)
    {
        return mode switch
        {
            SimpleAsmAppMode.WithoutVirtualThreads => 36UL,
            SimpleAsmAppMode.SingleThreadNoVector => 36UL,
            SimpleAsmAppMode.WithVirtualThreads => 8UL,
            SimpleAsmAppMode.PackedMixedEnvelope => 8UL,
            SimpleAsmAppMode.Lk => 8UL,
            SimpleAsmAppMode.Bnmcz => 8UL,
            SimpleAsmAppMode.RefactorShowcase => 8UL,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private CompiledWorkloadSlice BuildCompiledWorkloadSlice(
        SimpleAsmAppMode mode,
        FrontendMode frontendMode,
        ulong sliceIterations,
        Action<string, string> progressObserver)
    {
        ArgumentNullException.ThrowIfNull(progressObserver);

        ResetProgramImageState();
        EmitDiagnosticProgram(mode, sliceIterations);
        progressObserver("InstructionEmission", $"Program emission completed for a {sliceIterations:N0}-iteration workload slice.");

        CompilationSummary compilation = CompileProgramImage(frontendMode, progressObserver);
        return new CompiledWorkloadSlice(
            sliceIterations,
            _instructionCount,
            compilation);
    }

    private void RestoreReferenceSliceMetadata(SimpleAsmAppMode mode, ulong sliceIterations)
    {
        ResetProgramImageState();
        EmitDiagnosticProgram(mode, sliceIterations);
    }

    private ExecutionSnapshot ExecuteCompiledSliceRepeatedly(
        SimpleAsmAppMode mode,
        CompiledWorkloadSlice slice,
        ulong repetitionCount)
    {
        if (repetitionCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitionCount), repetitionCount, "Repetition count must be positive.");
        }

        ExecutionSnapshot lastExecution = new(
            PipelineExecutionOutcome.RetirementTargetReached,
            default,
            new PerformanceReport());
        PerformanceReport previousPerformanceSnapshot = CapturePerformanceReport();

        for (ulong repetition = 0; repetition < repetitionCount; repetition++)
        {
            PrepareWorkloadState(mode);
            PublishLifecycleCheckpoint(
                "MemorySeeded",
                $"Synthetic probe state initialized for slice execution {repetition + 1:N0}/{repetitionCount:N0}.");

            ExecutionSnapshot sampleExecution = ExecuteCompiledProgram(
                mode,
                slice.Compilation.BundleCount,
                (ulong)slice.InstructionCount);
            PerformanceReport currentPerformanceSnapshot = sampleExecution.Performance;
            PerformanceReport intervalPerformance =
                currentPerformanceSnapshot.CreateAdditiveDiagnosticIntervalSince(previousPerformanceSnapshot);
            previousPerformanceSnapshot = currentPerformanceSnapshot;

            lastExecution = lastExecution with
            {
                Outcome = sampleExecution.Outcome,
                Pipeline = AddPipelineControls(lastExecution.Pipeline, sampleExecution.Pipeline),
                Performance = repetition == 0
                    ? intervalPerformance
                    : AddPerformanceReports(lastExecution.Performance, intervalPerformance)
            };
        }

        return lastExecution;
    }

    private static Processor.CPU_Core.PipelineControl AddPipelineControls(
        Processor.CPU_Core.PipelineControl accumulator,
        Processor.CPU_Core.PipelineControl sample)
    {
        object boxedAccumulator = accumulator;
        object boxedSample = sample;

        foreach (FieldInfo field in typeof(Processor.CPU_Core.PipelineControl).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            object? mergedValue = MergeSnapshotValue(field.FieldType, field.GetValue(boxedAccumulator), field.GetValue(boxedSample));
            field.SetValue(boxedAccumulator, mergedValue);
        }

        foreach (PropertyInfo property in typeof(Processor.CPU_Core.PipelineControl).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? mergedValue = MergeSnapshotValue(
                property.PropertyType,
                property.GetValue(boxedAccumulator),
                property.GetValue(boxedSample));
            property.SetValue(boxedAccumulator, mergedValue);
        }

        return (Processor.CPU_Core.PipelineControl)boxedAccumulator;
    }

    private static PerformanceReport AddPerformanceReports(PerformanceReport accumulator, PerformanceReport sample)
    {
        ArgumentNullException.ThrowIfNull(accumulator);
        ArgumentNullException.ThrowIfNull(sample);

        foreach (PropertyInfo property in typeof(PerformanceReport).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? mergedValue = IsMemoryTelemetryAvailability(property.Name)
                ? (bool)property.GetValue(accumulator)! && (bool)property.GetValue(sample)!
                : IsLastValueDiagnostic(property.Name)
                    ? property.GetValue(sample)
                : MergeSnapshotValue(property.PropertyType, property.GetValue(accumulator), property.GetValue(sample));
            property.SetValue(accumulator, mergedValue);
        }

        return accumulator;
    }

    private static bool IsMemoryTelemetryAvailability(string propertyName)
    {
        return propertyName is nameof(PerformanceReport.MemoryCycleTelemetryAvailable)
            or nameof(PerformanceReport.InstructionFetchReadBytesTelemetryAvailable)
            or nameof(PerformanceReport.InstructionFetchRequestTelemetryAvailable)
            or nameof(PerformanceReport.MemoryBankConflictRejectTelemetryAvailable);
    }

    private static bool IsLastValueDiagnostic(string propertyName)
    {
        return propertyName is nameof(PerformanceReport.LastEligibilityRequestedMask)
            or nameof(PerformanceReport.LastEligibilityNormalizedMask)
            or nameof(PerformanceReport.LastEligibilityReadyPortMask)
            or nameof(PerformanceReport.LastEligibilityVisibleReadyMask)
            or nameof(PerformanceReport.LastEligibilityMaskedReadyMask)
            or nameof(PerformanceReport.LastSmtLegalityRejectKind)
            or nameof(PerformanceReport.LastSmtLegalityAuthoritySource)
            or nameof(PerformanceReport.MemoryTelemetryBaselineOutstandingRequests)
            or nameof(PerformanceReport.MemoryOutstandingRequests);
    }

    private static object? MergeSnapshotValue(Type valueType, object? currentValue, object? sampleValue)
    {
        if (sampleValue is null)
        {
            return currentValue;
        }

        if (valueType == typeof(bool))
        {
            bool current = currentValue is bool boolValue && boolValue;
            return current || (bool)sampleValue;
        }

        if (valueType.IsEnum || valueType == typeof(string))
        {
            return sampleValue;
        }

        if (valueType.IsArray)
        {
            Array sampleArray = (Array)sampleValue;
            Type? elementType = valueType.GetElementType();
            if (elementType is null)
            {
                return sampleValue;
            }

            if (currentValue is not Array currentArray || currentArray.Length != sampleArray.Length)
            {
                return (Array)sampleArray.Clone();
            }

            if (!IsSupportedNumericType(elementType))
            {
                return sampleValue;
            }

            Array mergedArray = (Array)currentArray.Clone();
            for (int index = 0; index < mergedArray.Length; index++)
            {
                object? mergedElement = MergeSnapshotValue(elementType, mergedArray.GetValue(index), sampleArray.GetValue(index));
                mergedArray.SetValue(mergedElement, index);
            }

            return mergedArray;
        }

        if (!IsSupportedNumericType(valueType))
        {
            return currentValue ?? sampleValue;
        }

        return valueType switch
        {
            Type t when t == typeof(byte) => unchecked((byte)((currentValue is byte current ? current : (byte)0) + (byte)sampleValue)),
            Type t when t == typeof(sbyte) => unchecked((sbyte)((currentValue is sbyte current ? current : (sbyte)0) + (sbyte)sampleValue)),
            Type t when t == typeof(short) => unchecked((short)((currentValue is short current ? current : (short)0) + (short)sampleValue)),
            Type t when t == typeof(ushort) => unchecked((ushort)((currentValue is ushort current ? current : (ushort)0) + (ushort)sampleValue)),
            Type t when t == typeof(int) => checked((currentValue is int current ? current : 0) + (int)sampleValue),
            Type t when t == typeof(uint) => checked((currentValue is uint current ? current : 0U) + (uint)sampleValue),
            Type t when t == typeof(long) => checked((currentValue is long current ? current : 0L) + (long)sampleValue),
            Type t when t == typeof(ulong) => checked((currentValue is ulong current ? current : 0UL) + (ulong)sampleValue),
            Type t when t == typeof(float) => (currentValue is float current ? current : 0f) + (float)sampleValue,
            Type t when t == typeof(double) => (currentValue is double current ? current : 0d) + (double)sampleValue,
            _ => sampleValue
        };
    }

    private static bool IsSupportedNumericType(Type valueType)
    {
        return valueType == typeof(byte)
               || valueType == typeof(sbyte)
               || valueType == typeof(short)
               || valueType == typeof(ushort)
               || valueType == typeof(int)
               || valueType == typeof(uint)
               || valueType == typeof(long)
               || valueType == typeof(ulong)
               || valueType == typeof(float)
               || valueType == typeof(double);
    }

    private static bool IsOpcodeRegistryInitializationFailure(TypeInitializationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!string.Equals(exception.TypeName, "YAKSys_Hybrid_CPU.Arch.OpcodeRegistry", StringComparison.Ordinal))
        {
            return false;
        }

        return exception.InnerException is ArgumentNullException argumentNullException
            && string.Equals(argumentNullException.ParamName, "collection", StringComparison.Ordinal);
    }

    private static string BuildOpcodeRegistryInitializationFailureMessage(TypeInitializationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        const string OpcodeRegistryGeneratedCatalogHint =
            "OpcodeRegistry initialization failed while materializing its generated catalog facade. " +
            "The generated ISA catalog is unavailable or invalid. " +
            "Regenerate and validate the manifest-derived catalog before running TestAssemblerConsoleApps diagnostics.";

        string baseMessage = exception.InnerException?.Message ?? exception.Message;
        return $"{OpcodeRegistryGeneratedCatalogHint} Inner failure: {baseMessage}";
    }

    private static VLIW_Instruction ReadInstruction(byte[] programImage, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(programImage);

        VLIW_Instruction instruction = new VLIW_Instruction();
        if (!instruction.TryReadBytes(programImage, slotIndex * 32))
        {
            throw new InvalidOperationException($"Unable to decode instruction at slot {slotIndex} from the serialized program image.");
        }

        return instruction;
    }

    private CompilationSummary CompileProgramImage(
        FrontendMode frontendMode,
        Action<string, string> progressObserver)
    {
        ArgumentNullException.ThrowIfNull(progressObserver);

        progressObserver("CanonicalCompileStarting", "Entering canonical compile.");
        HybridCpuCompiledProgram compiledProgram = NativeTransportRuntimeAdapter.CompileProgram(
            0,
            new ReadOnlySpan<VLIW_Instruction>(_instructions, 0, _instructionCount),
            bundleAnnotations: GetBundleAnnotations(),
            frontendMode: frontendMode,
            progressObserver: progressObserver);
        progressObserver("CanonicalCompile", $"Canonical compile produced {compiledProgram.BundleCount} bundles.");

        progressObserver("BundleEmissionStarting", $"Emitting bundle image for {compiledProgram.BundleCount} bundles.");
        progressObserver("RuntimeEmissionStarting", "Crossing the explicit compiler-to-runtime transport boundary.");
        compiledProgram = NativeTransportRuntimeAdapter.EmitProgram(compiledProgram, EmissionBaseAddress);
        progressObserver("RuntimeEmission", "Compiler-owned bundle image and evidence-only annotations were adapted for runtime transport.");
        progressObserver("BundleEmission", $"Bundle image emitted at 0x{EmissionBaseAddress:X}.");

        progressObserver("BundleAnnotationPublishStarting", $"Publishing bundle annotations for {compiledProgram.BundleCount} emitted bundles.");
        PublishCanonicalBundleAnnotations(compiledProgram, EmissionBaseAddress);
        progressObserver("BundleAnnotationPublish", $"Published bundle annotations for {compiledProgram.BundleCount} emitted bundles.");

        VLIW_Instruction firstInstruction = ReadInstruction(compiledProgram.ProgramImage, 0);
        return new CompilationSummary(
            compiledProgram.BundleCount,
            firstInstruction.OpCode,
            InstructionRegistry.IsRegistered(firstInstruction.OpCode),
            AnalyzeCompilerPacking(compiledProgram));
    }

    private void PublishCanonicalBundleAnnotations(HybridCpuCompiledProgram compiledProgram, ulong baseAddress)
    {
        ArgumentNullException.ThrowIfNull(compiledProgram);

        IReadOnlyList<IrMaterializedBundle> materializedBundles = FlattenMaterializedBundles(compiledProgram);
        if (materializedBundles.Count != compiledProgram.BundleCount)
        {
            throw new InvalidOperationException(
                $"Canonical bundle layout count ({materializedBundles.Count}) does not match lowered bundle count ({compiledProgram.BundleCount}).");
        }

        for (int bundleIndex = 0; bundleIndex < materializedBundles.Count; bundleIndex++)
        {
            ulong bundleAddress = baseAddress + ((ulong)bundleIndex * (ulong)HybridCpuBundleSerializer.BundleSizeBytes);
            _runtime.PublishBundleAnnotations(bundleAddress, BuildBundleAnnotations(materializedBundles[bundleIndex]));
        }
    }

    private static IReadOnlyList<IrMaterializedBundle> FlattenMaterializedBundles(HybridCpuCompiledProgram compiledProgram)
    {
        ArgumentNullException.ThrowIfNull(compiledProgram);

        List<IrMaterializedBundle> materializedBundles = new List<IrMaterializedBundle>(compiledProgram.BundleCount);
        for (int blockIndex = 0; blockIndex < compiledProgram.BundleLayout.BlockResults.Count; blockIndex++)
        {
            IrBasicBlockBundlingResult blockResult = compiledProgram.BundleLayout.BlockResults[blockIndex];
            for (int bundleIndex = 0; bundleIndex < blockResult.Bundles.Count; bundleIndex++)
            {
                materializedBundles.Add(blockResult.Bundles[bundleIndex]);
            }
        }

        return materializedBundles;
    }

    private VliwBundleAnnotations BuildBundleAnnotations(IrMaterializedBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        InstructionSlotMetadata[] slotMetadata = new InstructionSlotMetadata[BundleMetadata.BundleSlotCount];
        bool hasExplicitMetadata = false;

        for (int slotIndex = 0; slotIndex < slotMetadata.Length; slotIndex++)
        {
            slotMetadata[slotIndex] = InstructionSlotMetadata.Default;
        }

        for (int slotIndex = 0; slotIndex < bundle.Slots.Count; slotIndex++)
        {
            IrMaterializedBundleSlot slot = bundle.Slots[slotIndex];
            InstructionSlotMetadata metadata = ResolvePublishedSlotMetadata(slot);
            slotMetadata[slot.SlotIndex] = metadata;
            hasExplicitMetadata |= metadata != InstructionSlotMetadata.Default;
        }

        return hasExplicitMetadata
            ? new VliwBundleAnnotations(slotMetadata)
            : VliwBundleAnnotations.Empty;
    }

    private InstructionSlotMetadata ResolvePublishedSlotMetadata(IrMaterializedBundleSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (slot.Instruction is null)
        {
            return InstructionSlotMetadata.Default;
        }

        int instructionIndex = slot.Instruction.Index;
        if ((uint)instructionIndex < (uint)_instructionCount)
        {
            return _instructionSlotMetadata[instructionIndex];
        }

        return new InstructionSlotMetadata(
            VtId.Create(slot.Instruction.VirtualThreadId),
            SlotMetadata.Default);
    }

    private PipelineExecutionOutcome ExecuteDiagnosticPipeline(ulong targetRetiredInstructions, int bundleCount)
    {
        _runtime.SetPipelineMode(true);

        ulong programEndAddressExclusive = EmissionBaseAddress + ((ulong)Math.Max(1, bundleCount) * (ulong)HybridCpuBundleSerializer.BundleSizeBytes);
        ulong hardCycleLimit = Math.Max(1024UL, targetRetiredInstructions * 32UL + (ulong)Math.Max(1, bundleCount) * 64UL);
        const int ProgramExitConfirmationCycles = 16;
        const ulong ProgramExitDrainBudget = 128;
        const ulong RetirementPlateauBudget = 32;
        int outOfImageCycleCount = 0;
        bool drainingAfterProgramExit = false;
        ulong drainStartCycle = 0;
        ulong lastRetiredCount = 0;
        ulong lastRetireCycle = 0;

        PublishLifecycleCheckpoint(
            "PipelineDecodeLoop",
            "Entered diagnostic pipeline loop.",
            retirementTarget: targetRetiredInstructions,
            hardCycleLimit: hardCycleLimit);

        while (true)
        {
            _runtime.ExecutePipelineCycle();
            PublishLifecycleCheckpoint(
                "PipelineDecodeLoop",
                "Heartbeat from diagnostic pipeline loop.",
                force: false,
                retirementTarget: targetRetiredInstructions,
                hardCycleLimit: hardCycleLimit);

            Processor.CPU_Core.PipelineControl pipeline = _runtime.GetPipelineControl();
            if (pipeline.InstructionsRetired >= targetRetiredInstructions)
            {
                return PipelineExecutionOutcome.RetirementTargetReached;
            }

            ulong activePc = _runtime.ReadActiveLivePc();
            if (activePc >= programEndAddressExclusive)
            {
                outOfImageCycleCount++;
            }
            else
            {
                outOfImageCycleCount = 0;
            }

            if (!drainingAfterProgramExit && outOfImageCycleCount >= ProgramExitConfirmationCycles)
            {
                drainingAfterProgramExit = true;
                drainStartCycle = pipeline.CycleCount;
                lastRetiredCount = pipeline.InstructionsRetired;
                lastRetireCycle = pipeline.CycleCount;

                PublishLifecycleCheckpoint(
                    "PipelineDrainStarting",
                    $"Live PC advanced beyond emitted image end 0x{programEndAddressExclusive:X}; entering bounded drain mode.",
                    retirementTarget: targetRetiredInstructions,
                    hardCycleLimit: hardCycleLimit);
            }

            if (drainingAfterProgramExit)
            {
                if (pipeline.InstructionsRetired > lastRetiredCount)
                {
                    lastRetiredCount = pipeline.InstructionsRetired;
                    lastRetireCycle = pipeline.CycleCount;
                }

                bool drainBudgetReached = pipeline.CycleCount >= drainStartCycle + ProgramExitDrainBudget;
                bool retirementPlateauReached = pipeline.CycleCount >= lastRetireCycle + RetirementPlateauBudget;
                if (drainBudgetReached || retirementPlateauReached)
                {
                    PublishLifecycleCheckpoint(
                        "PipelineDrainCompleted",
                        $"Accepted bounded completion after emitted image fallthrough. Retired={pipeline.InstructionsRetired}, ProgramEnd=0x{programEndAddressExclusive:X}, ActivePC=0x{activePc:X}.",
                        retirementTarget: targetRetiredInstructions,
                        hardCycleLimit: hardCycleLimit);

                    if (pipeline.InstructionsRetired == 0)
                    {
                        throw new InvalidOperationException(
                            "Diagnostic pipeline exited the emitted image without retiring any instruction. " +
                            $"ProgramEnd=0x{programEndAddressExclusive:X}, ActivePC=0x{activePc:X}, Cycles={pipeline.CycleCount}.");
                    }

                    return PipelineExecutionOutcome.DrainedAfterProgramExit;
                }
            }

            if (pipeline.CycleCount >= hardCycleLimit)
            {
                PublishLifecycleCheckpoint(
                    "PipelineHardCycleLimit",
                    "Hard cycle limit reached before retirement target.",
                    retirementTarget: targetRetiredInstructions,
                    hardCycleLimit: hardCycleLimit);
                activePc = _runtime.ReadActiveLivePc();
                int activeVtId = _runtime.ReadActiveVirtualThreadId();
                throw new InvalidOperationException(
                    "Diagnostic pipeline exhausted the hard cycle limit before reaching the retirement target. " +
                    $"Retired={pipeline.InstructionsRetired}, Target={targetRetiredInstructions}, " +
                    $"Cycles={pipeline.CycleCount}, HardCycleLimit={hardCycleLimit}, " +
                    $"ActivePC=0x{activePc:X}, ActiveVT={activeVtId}.");
            }
        }
    }

    private ExecutionSnapshot ExecuteCompiledProgram(
        SimpleAsmAppMode mode,
        int bundleCount,
        ulong absoluteRetirementTarget)
    {
        _runtime.PrepareExecutionStart(EmissionBaseAddress);
        EnableFspForDiagnostics(mode);

        PipelineExecutionOutcome outcome = ExecuteDiagnosticPipeline(absoluteRetirementTarget, bundleCount);
        return new ExecutionSnapshot(
            outcome,
            _runtime.GetPipelineControl(),
            _runtime.GetPerformanceStats());
    }

    private Processor.CPU_Core.PipelineControl CapturePipelineControl()
    {
        return _runtime.CapturePipelineControl();
    }

    private PerformanceReport CapturePerformanceReport()
    {
        return _runtime.CapturePerformanceStats();
    }

    private enum PipelineExecutionOutcome
    {
        RetirementTargetReached,
        DrainedAfterProgramExit
    }
}
