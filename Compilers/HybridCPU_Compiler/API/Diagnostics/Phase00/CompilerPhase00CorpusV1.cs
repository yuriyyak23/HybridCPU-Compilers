using System;
using System.Collections.Generic;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

public enum CompilerSyntheticCaseKindV1 : byte
{
    SchedulingDag = 0,
    StructuralPlacement = 1,
    Dependence = 2,
    Serialization = 3
}

/// <summary>
/// Frozen Phase 00 synthetic-case descriptor. Opcode recipes describe compiler inputs only;
/// structural masks remain compiler evidence and never assert live machine legality.
/// </summary>
public sealed record CompilerSyntheticCaseV1(
    string Id,
    CompilerSyntheticCaseKindV1 Kind,
    IReadOnlyList<HybridCpuOpcode> OpcodeRecipe,
    IReadOnlyList<IrIssueSlotMask> StructuralSlotMasks,
    bool? ExpectedStructuralPlacement,
    CompilerMetricEvidenceQualityV1 EvidenceQuality,
    string ExpectedObservation);

/// <summary>
/// Frozen adversarial and invariant corpus required by RefPlan3 Phase 00.
/// </summary>
public static class CompilerPhase00SyntheticCorpusV1
{
    public const string Schema = "CompilerPhase00SyntheticCorpusV1";

    public static IReadOnlyList<CompilerSyntheticCaseV1> Cases { get; } = Array.AsReadOnly(
    [
        Case(
            "independent-wide-set",
            CompilerSyntheticCaseKindV1.SchedulingDag,
            [HybridCpuOpcode.ADDI, HybridCpuOpcode.ADDI, HybridCpuOpcode.ADDI, HybridCpuOpcode.ADDI],
            [IrIssueSlotMask.Scalar, IrIssueSlotMask.Scalar, IrIssueSlotMask.Scalar, IrIssueSlotMask.Scalar],
            true,
            CompilerMetricEvidenceQualityV1.Measured,
            "Four independent scalar instructions expose ready-window width without exceeding scalar capacity."),
        Case(
            "raw-chain",
            CompilerSyntheticCaseKindV1.Dependence,
            [HybridCpuOpcode.ADDI, HybridCpuOpcode.ADD],
            [],
            null,
            CompilerMetricEvidenceQualityV1.Measured,
            "The consumer remains ordered after its producer by the existing RAW edge."),
        Case(
            "zero-latency-chain",
            CompilerSyntheticCaseKindV1.SchedulingDag,
            [HybridCpuOpcode.ADDI, HybridCpuOpcode.ADD],
            [],
            null,
            CompilerMetricEvidenceQualityV1.HistoricalUnverified,
            "Same-cycle successor discovery is retained as an explicit corpus case pending a frozen zero-latency opcode recipe."),
        Case(
            "greedy-membership-counterexample",
            CompilerSyntheticCaseKindV1.StructuralPlacement,
            [],
            [IrIssueSlotMask.Slot0 | IrIssueSlotMask.Slot1, IrIssueSlotMask.Slot0, IrIssueSlotMask.Slot1 | IrIssueSlotMask.Slot2],
            true,
            CompilerMetricEvidenceQualityV1.Measured,
            "Existing exact placement succeeds where naive first-fit assignment can fail."),
        Case(
            "w8-fixed-lanes",
            CompilerSyntheticCaseKindV1.StructuralPlacement,
            [],
            [
                IrIssueSlotMask.Slot0,
                IrIssueSlotMask.Slot1,
                IrIssueSlotMask.Slot2,
                IrIssueSlotMask.Slot3,
                IrIssueSlotMask.Slot4,
                IrIssueSlotMask.Slot5,
                IrIssueSlotMask.Slot6,
                IrIssueSlotMask.Slot7
            ],
            true,
            CompilerMetricEvidenceQualityV1.Measured,
            "All eight fixed structural lanes materialize without changing membership."),
        Case(
            "lane6-lane7-choke",
            CompilerSyntheticCaseKindV1.StructuralPlacement,
            [HybridCpuOpcode.LD, HybridCpuOpcode.BEQ],
            [IrIssueSlotMask.Slot6, IrIssueSlotMask.Slot7],
            true,
            CompilerMetricEvidenceQualityV1.Measured,
            "Pinned lane6 and lane7 structural facts remain distinct."),
        Case(
            "system-singleton",
            CompilerSyntheticCaseKindV1.Serialization,
            [HybridCpuOpcode.ECALL],
            [IrIssueSlotMask.Slot7],
            true,
            CompilerMetricEvidenceQualityV1.Measured,
            "SystemSingleton remains isolated on the existing lane7 topology."),
        Case(
            "serialization-boundary",
            CompilerSyntheticCaseKindV1.Serialization,
            [HybridCpuOpcode.FENCE, HybridCpuOpcode.ADDI],
            [],
            null,
            CompilerMetricEvidenceQualityV1.Measured,
            "Existing serialization dependencies preserve the cycle boundary."),
        Case(
            "memory-must-dependence",
            CompilerSyntheticCaseKindV1.Dependence,
            [HybridCpuOpcode.LD, HybridCpuOpcode.SD],
            [],
            null,
            CompilerMetricEvidenceQualityV1.Measured,
            "Same known memory region retains a conservative must-dependence."),
        Case(
            "memory-may-dependence",
            CompilerSyntheticCaseKindV1.Dependence,
            [HybridCpuOpcode.LD, HybridCpuOpcode.SD],
            [],
            null,
            CompilerMetricEvidenceQualityV1.Measured,
            "Unknown overlap retains a conservative may-dependence."),
        Case(
            "register-war",
            CompilerSyntheticCaseKindV1.Dependence,
            [HybridCpuOpcode.ADD, HybridCpuOpcode.ADDI],
            [],
            null,
            CompilerMetricEvidenceQualityV1.Measured,
            "Existing register WAR ordering is preserved."),
        Case(
            "register-waw",
            CompilerSyntheticCaseKindV1.Dependence,
            [HybridCpuOpcode.ADDI, HybridCpuOpcode.ADDI],
            [],
            null,
            CompilerMetricEvidenceQualityV1.Measured,
            "Existing register WAW ordering is preserved."),
        Case(
            "no-placement-duplicate-lane6",
            CompilerSyntheticCaseKindV1.StructuralPlacement,
            [],
            [IrIssueSlotMask.Slot6, IrIssueSlotMask.Slot6],
            false,
            CompilerMetricEvidenceQualityV1.Measured,
            "Two fixed lane6 requirements have no structural assignment.")
    ]);

    private static CompilerSyntheticCaseV1 Case(
        string id,
        CompilerSyntheticCaseKindV1 kind,
        HybridCpuOpcode[] opcodeRecipe,
        IrIssueSlotMask[] structuralSlotMasks,
        bool? expectedStructuralPlacement,
        CompilerMetricEvidenceQualityV1 evidenceQuality,
        string expectedObservation) =>
        new(
            id,
            kind,
            Array.AsReadOnly(opcodeRecipe),
            Array.AsReadOnly(structuralSlotMasks),
            expectedStructuralPlacement,
            evidenceQuality,
            expectedObservation);
}

public enum CompilerBenchmarkProfileScopeV1 : byte
{
    CompilerSchedulingInput = 0,
    RuntimeNegativeControl = 1
}

/// <summary>
/// Frozen representative-profile inventory. Unavailable entries stay explicit and cannot be
/// silently represented by a zero counter or promoted from historical prose.
/// </summary>
public sealed record CompilerBenchmarkProfileV1(
    string Id,
    CompilerBenchmarkProfileScopeV1 Scope,
    CompilerMetricEvidenceQualityV1 EvidenceQuality,
    string EvidenceReason);

public static class CompilerPhase00RepresentativeProfilesV1
{
    public static IReadOnlyList<CompilerBenchmarkProfileV1> Profiles { get; } = Array.AsReadOnly(
    [
        Measured("alu"),
        Measured("novt"),
        Measured("vt"),
        Measured("max"),
        Measured("lk"),
        Measured("bnmcz"),
        MeasuredRuntimeControl("replay"),
        MeasuredRuntimeControl("safety"),
        MeasuredRuntimeControl("stream-vector"),
        MeasuredRuntimeControl("matrix-tile")
    ]);

    private static CompilerBenchmarkProfileV1 Measured(string id) =>
        new(
            id,
            CompilerBenchmarkProfileScopeV1.CompilerSchedulingInput,
            CompilerMetricEvidenceQualityV1.Measured,
            "Frozen compiler input is reproduced in CompilerPhase00BaselineReportV1; runtime repetitions remain separate evidence.");

    private static CompilerBenchmarkProfileV1 MeasuredRuntimeControl(string id) =>
        new(
            id,
            CompilerBenchmarkProfileScopeV1.RuntimeNegativeControl,
            CompilerMetricEvidenceQualityV1.Measured,
            "The frozen local diagnostic recipe and three raw runtime repetitions are archived in CompilerPhase00RuntimeControlReportV1; physical timing remains separate resource telemetry.");
}
