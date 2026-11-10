namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record TelemetryMetric(
    string Availability,
    ulong? Value,
    string ProducerBoundary)
{
    internal static TelemetryMetric Available(ulong value, string producerBoundary) =>
        new("Available", value, producerBoundary);

    internal static TelemetryMetric Unavailable(string producerBoundary) =>
        new("Unavailable", null, producerBoundary);
}

/// <summary>
/// Versioned consumer projection of producer-owned timing and memory telemetry.
/// Existing v2 fields remain additive compatibility fields; metrics without a
/// truthful runtime producer carry Availability=Unavailable and Value=null.
/// </summary>
internal sealed record TimingMemoryReport(
    string SchemaVersion,
    string ArtifactName,
    string[] CompatibleSchemaVersions,
    string ProducerSchemaVersion,
    string FunctionalBaseline,
    string TimingBaseline,
    string TelemetryAvailabilityPolicy,
    string TimingComparisonPolicy,
    string SchedulerDiagnosticComparisonPolicy,
    string EligibilityMaskComparisonPolicy,
    ulong TotalCycles,
    ulong PipelineStallCycles,
    ulong MemoryStallCycles,
    ulong NonMemoryStallCycles,
    TelemetryMetric NonMemoryStallCyclesMetric,
    ulong InstructionsRetired,
    double RawCycleIpc,
    double RetireNormalizedIpc,
    string FineGrainedCycleBreakdownAvailability,
    string MemoryTelemetryDisposition,
    ulong LegacyTotalBursts,
    ulong LegacyBytesTransferred,
    bool MemoryWaitObserved,
    string MemoryTelemetryMessage,
    TelemetryMetric MemoryControllerCycles,
    TelemetryMetric MemoryReadServiceCycles,
    TelemetryMetric MemoryStoreReadinessServiceCycles,
    TelemetryMetric MemoryCompletionPublicationCycles,
    TelemetryMetric AcceptedMemoryRequests,
    TelemetryMetric CompletedMemoryRequests,
    TelemetryMetric DataReadAcceptedRequests,
    TelemetryMetric DataReadCompletedRequests,
    TelemetryMetric DataWriteAcceptedRequests,
    TelemetryMetric DataWriteCompletedRequests,
    TelemetryMetric DataReadBytes,
    TelemetryMetric CommittedDataWriteBytes,
    TelemetryMetric InstructionFetchAcceptedRequests,
    TelemetryMetric InstructionFetchCompletedRequests,
    TelemetryMetric InstructionFetchReadBytes,
    TelemetryMetric QueueFullRejects,
    TelemetryMetric BankConflictRejects,
    string[] CompatibleProducerSchemaVersions,
    TelemetryMetric TelemetryBaselineOutstandingRequests,
    TelemetryMetric CanceledMemoryRequests,
    TelemetryMetric ConsumedMemoryCompletions,
    TelemetryMetric OutstandingMemoryRequests,
    string RequestIdentityBalanceDisposition)
{
    internal const string SchemaVersionValue = "timing-memory-report/v3";
    internal const string LegacySchemaVersionValue = "post-ref1-timing-memory-v2";
    internal const string ArtifactFileName = "timing_memory_report.json";
    internal const string LegacyArtifactFileName = "post_ref1_timing_memory_report.json";
    internal const string ManifestKey = "timing_memory";
    internal const string LegacyManifestKey = "post_ref1_timing_memory";

    internal static TimingMemoryReport Create(SimpleAsmAppMetrics metrics)
    {
        bool stallPartitionConsistent = metrics.MemoryStalls <= metrics.StallCycles;
        ulong nonMemoryStallCycles = stallPartitionConsistent
            ? metrics.StallCycles - metrics.MemoryStalls
            : 0;
        bool memoryWaitObserved = metrics.MemoryStalls > 0;
        bool legacyTransferCountersEmpty = metrics.TotalBursts == 0 && metrics.BytesTransferred == 0;
        string memoryDisposition = metrics.MemoryCycleTelemetryAvailable
            ? "ProducerTelemetryAvailable"
            : memoryWaitObserved && legacyTransferCountersEmpty
                ? "UnavailableWhilePipelineReportsMemoryWait"
                : legacyTransferCountersEmpty
                    ? "NoLegacyTransferActivityObserved"
                    : "LegacyTransferCountersReported";
        string memoryMessage = metrics.MemoryCycleTelemetryAvailable
            ? "Controller request/completion telemetry is producer-owned. Legacy burst counters remain a separate compatibility surface and may be zero during controller-native activity."
            : memoryDisposition switch
            {
                "UnavailableWhilePipelineReportsMemoryWait" =>
                    "Pipeline memory waits are non-zero while legacy burst/byte counters are zero. Do not read zero as no memory activity; producer telemetry was unavailable.",
                "NoLegacyTransferActivityObserved" =>
                    "Legacy burst/byte counters are zero and this run did not report pipeline memory waits; producer telemetry was unavailable.",
                _ => "Legacy burst/byte counters were observed, but producer telemetry was unavailable."
            };

        TelemetryMetric ProducerMetric(ulong value, string boundary) =>
            metrics.MemoryCycleTelemetryAvailable
                ? TelemetryMetric.Available(value, boundary)
                : TelemetryMetric.Unavailable(boundary);

        return new TimingMemoryReport(
            SchemaVersion: SchemaVersionValue,
            ArtifactName: nameof(TimingMemoryReport),
            CompatibleSchemaVersions: [LegacySchemaVersionValue],
            ProducerSchemaVersion: metrics.MemoryCycleTelemetryAvailable
                ? metrics.MemoryCycleTelemetrySchemaVersion
                : "Unavailable",
            FunctionalBaseline: "Current ISE functional observation; prior results remain historical evidence and no parity claim is inferred from this artifact.",
            TimingBaseline: "Current post-RF10 timing observation; any baseline must be established independently from historical pre-RF10 total cycles.",
            TelemetryAvailabilityPolicy: "Available zero means measured zero. Unavailable means the runtime has no truthful producer for that metric.",
            TimingComparisonPolicy: "Pre-RF10 and post-RF10 total-cycle values are not comparable until MemoryCycleController equivalence is demonstrated.",
            SchedulerDiagnosticComparisonPolicy: "LastSmtLegalityRejectKind is a current-observation field, not a stable last-rejection history across Ref1.",
            EligibilityMaskComparisonPolicy: "Eligibility masks use the current candidate-bit layout; compare pre-Ref1 values only with an explicit bit mapping.",
            TotalCycles: metrics.CycleCount,
            PipelineStallCycles: metrics.StallCycles,
            MemoryStallCycles: metrics.MemoryStalls,
            NonMemoryStallCycles: nonMemoryStallCycles,
            NonMemoryStallCyclesMetric: stallPartitionConsistent
                ? TelemetryMetric.Available(
                    nonMemoryStallCycles,
                    "PipelineControl.StallCycles inclusive stalled-cycle owner minus its nested CountMemoryStall cycles")
                : TelemetryMetric.Unavailable(
                    "Pipeline stall partition invariant failed because MemoryStalls exceeded StallCycles"),
            InstructionsRetired: metrics.InstructionsRetired,
            RawCycleIpc: metrics.Ipc,
            RetireNormalizedIpc: metrics.RetireIpc,
            FineGrainedCycleBreakdownAvailability:
                "Unavailable below the exact memory/non-memory top-level partition: fetch-wait, decode/admission-wait, memory-admission-wait, memory-completion-wait, execute-wait, writeback-wait, retire and hazard early-return do not have exact producer owners. WAW is an event counter, not a general cycle bucket. Controller edge/service/publication-cycle boundaries are exposed separately and may overlap.",
            MemoryTelemetryDisposition: memoryDisposition,
            LegacyTotalBursts: metrics.TotalBursts,
            LegacyBytesTransferred: metrics.BytesTransferred,
            MemoryWaitObserved: memoryWaitObserved,
            MemoryTelemetryMessage: memoryMessage,
            MemoryControllerCycles: ProducerMetric(metrics.MemoryControllerCycles, "MemoryCycleController observed edges since telemetry reset"),
            MemoryReadServiceCycles: ProducerMetric(metrics.MemoryReadServiceCycles, "MemoryCycleController cycles selecting one controller-native read"),
            MemoryStoreReadinessServiceCycles: ProducerMetric(metrics.MemoryStoreReadinessServiceCycles, "MemoryCycleController cycles selecting one scalar-store readiness request"),
            MemoryCompletionPublicationCycles: ProducerMetric(metrics.MemoryCompletionPublicationCycles, "MemoryCycleController cycles publishing one or more prior-latched completions"),
            AcceptedMemoryRequests: ProducerMetric(metrics.MemoryAcceptedRequests, "MemoryCycleController unique accepted request identities"),
            CompletedMemoryRequests: ProducerMetric(metrics.MemoryCompletedRequests, "MemoryCycleController published completions"),
            DataReadAcceptedRequests: ProducerMetric(metrics.DataReadAcceptedRequests, "MemoryCycleController requests with a data-read property"),
            DataReadCompletedRequests: ProducerMetric(metrics.DataReadCompletedRequests, "MemoryCycleController published data-read completions"),
            DataWriteAcceptedRequests: ProducerMetric(metrics.DataWriteAcceptedRequests, "MemoryCycleController requests with a future data-write property"),
            DataWriteCompletedRequests: ProducerMetric(metrics.DataWriteCompletedRequests, "MemoryCycleController completed requests with a future data-write property; not byte publication"),
            DataReadBytes: ProducerMetric(metrics.DataReadBytes, "Successful MemoryCycleController data-read completions"),
            CommittedDataWriteBytes: ProducerMetric(metrics.CommittedDataWriteBytes, "Selected-retire physical publication owner"),
            InstructionFetchAcceptedRequests: TelemetryMetric.Unavailable(
                "Fetch is synchronous cache/main-memory access and has no controller request admission"),
            InstructionFetchCompletedRequests: TelemetryMetric.Unavailable(
                "Fetch is synchronous cache/main-memory access and has no controller request completion"),
            InstructionFetchReadBytes: metrics.InstructionFetchReadBytesTelemetryAvailable
                ? TelemetryMetric.Available(metrics.InstructionFetchReadBytes, "Instruction cache physical 256-byte materialization owner")
                : TelemetryMetric.Unavailable("Instruction-fetch physical-read producer"),
            QueueFullRejects: ProducerMetric(metrics.MemoryQueueFullRejects, "MemoryCycleController capacity backpressure attempts"),
            BankConflictRejects: metrics.MemoryBankConflictRejectTelemetryAvailable
                ? TelemetryMetric.Available(metrics.MemoryBankConflictRejects, "MemoryCycleController bank-conflict admission")
                : TelemetryMetric.Unavailable("Controller-native admission has no bank-conflict reject result; scheduler and legacy conflict counters are distinct"),
            CompatibleProducerSchemaVersions: ["memory-cycle-telemetry-v1"],
            TelemetryBaselineOutstandingRequests: ProducerMetric(
                metrics.MemoryTelemetryBaselineOutstandingRequests,
                "MemoryCycleController live request identities captured at telemetry reset"),
            CanceledMemoryRequests: ProducerMetric(
                metrics.MemoryCanceledRequests,
                "MemoryCycleController successful terminal TryCancel operations"),
            ConsumedMemoryCompletions: ProducerMetric(
                metrics.MemoryConsumedCompletions,
                "MemoryCycleController completions successfully consumed by request identity"),
            OutstandingMemoryRequests: ProducerMetric(
                metrics.MemoryOutstandingRequests,
                "MemoryCycleController current live identities, including pending, latched, or published completion state"),
            RequestIdentityBalanceDisposition: metrics.MemoryCycleTelemetryAvailable
                ? metrics.MemoryRequestIdentityBalanced ? "Balanced" : "Unbalanced"
                : "Unavailable");
    }

    internal TimingMemoryReport AsLegacyCompatibilityProjection() => this with
    {
        SchemaVersion = LegacySchemaVersionValue
    };
}
