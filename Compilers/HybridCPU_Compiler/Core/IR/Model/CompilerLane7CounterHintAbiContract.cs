using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerLane7CounterHintAbiClass : byte
{
    ReplayStableTimeCounter = 0,
    RetireAccountingCounter = 1,
    SchedulingHint = 2
}

/// <summary>
/// Compiler-visible no-emission ABI audit for Lane7 counter and hint rows.
/// </summary>
public sealed class CompilerLane7CounterHintAbiContract
{
    private static readonly string[] TimeRequiredPolicyDecisions =
    [
        "CounterSourceAbi",
        "ReplayStableCounterModel",
        "PrivilegeVirtualizationBoundary",
        "RetireOwnedPublication",
        "NoHostWallClockLeak",
        "SeparateFromCycleCounter"
    ];

    private static readonly string[] RetiredInstructionRequiredPolicyDecisions =
    [
        "RetireAccountingModel",
        "ReplayStableCounterModel",
        "PrivilegeVirtualizationBoundary",
        "RetireOwnedPublication",
        "NoSpeculativeRetireCount",
        "SeparateFromCycleCounter"
    ];

    private static readonly string[] PauseRequiredPolicyDecisions =
    [
        "HintEncodingAbi",
        "SchedulerFairnessPolicy",
        "ProgressGuaranteePolicy",
        "ReplayRollbackPolicy",
        "NoArchitecturalStateLeakage",
        "NoSynchronizationPrimitiveSemantics",
        "NoArchitecturalProgressGuarantee"
    ];

    private CompilerLane7CounterHintAbiContract(
        string mnemonic,
        CompilerLane7CounterHintAbiClass abiClass,
        string extensionName,
        string evidenceBoundary,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresCounterSourceAbi = false,
        bool requiresReplayStableCounterModel = false,
        bool requiresRetireAccountingModel = false,
        bool requiresPrivilegeVirtualizationPolicy = false,
        bool requiresRetireOwnedPublication = false,
        bool rejectHostEvidenceLeak = false,
        bool separateFromCycleCounter = false,
        bool requiresHintEncodingAbi = false,
        bool requiresProgressFairnessPolicy = false,
        bool noArchitecturalProgressGuarantee = false,
        bool requiresNoArchitecturalStateLeakage = false,
        bool rejectSynchronizationPrimitiveSemantics = false,
        bool requiresReplayRollbackEvidence = false,
        bool noGenericSystemOpFallback = false,
        bool noHiddenScalarLowering = false,
        bool noMultiOpEmission = false)
    {
        Mnemonic = mnemonic;
        AbiClass = abiClass;
        ExtensionName = extensionName;
        EvidenceBoundary = evidenceBoundary;
        AbiDecision = abiDecision;
        OperandShape = operandShape;
        DataSemantics = dataSemantics;
        ResultSemantics = resultSemantics;
        RequiredPolicyDecisions = requiredPolicyDecisions;
        RequiresCounterSourceAbi = requiresCounterSourceAbi;
        RequiresReplayStableCounterModel = requiresReplayStableCounterModel;
        RequiresRetireAccountingModel = requiresRetireAccountingModel;
        RequiresPrivilegeVirtualizationPolicy = requiresPrivilegeVirtualizationPolicy;
        RequiresRetireOwnedPublication = requiresRetireOwnedPublication;
        RejectHostEvidenceLeak = rejectHostEvidenceLeak;
        SeparateFromCycleCounter = separateFromCycleCounter;
        RequiresHintEncodingAbi = requiresHintEncodingAbi;
        RequiresProgressFairnessPolicy = requiresProgressFairnessPolicy;
        NoArchitecturalProgressGuarantee = noArchitecturalProgressGuarantee;
        RequiresNoArchitecturalStateLeakage = requiresNoArchitecturalStateLeakage;
        RejectSynchronizationPrimitiveSemantics = rejectSynchronizationPrimitiveSemantics;
        RequiresReplayRollbackEvidence = requiresReplayRollbackEvidence;
        NoGenericSystemOpFallback = noGenericSystemOpFallback;
        NoHiddenScalarLowering = noHiddenScalarLowering;
        NoMultiOpEmission = noMultiOpEmission;
    }

    public static CompilerLane7CounterHintAbiContract TimeCounter { get; } =
        new(
            "RDTIME",
            CompilerLane7CounterHintAbiClass.ReplayStableTimeCounter,
            "ScalarSystemCounter",
            "Lane7CounterReplayDeferred",
            "NoAllocationUntilReplayStableTimeSourcePrivilegeVirtualizationRetirePublicationAbi",
            "rd",
            "Time source domain, virtualization boundary, and replay-stable source ABI are not selected.",
            "Future rd publication is blocked until retire-owned deterministic time-counter semantics are explicit.",
            TimeRequiredPolicyDecisions,
            requiresCounterSourceAbi: true,
            requiresReplayStableCounterModel: true,
            requiresPrivilegeVirtualizationPolicy: true,
            requiresRetireOwnedPublication: true,
            rejectHostEvidenceLeak: true,
            separateFromCycleCounter: true,
            noGenericSystemOpFallback: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public static CompilerLane7CounterHintAbiContract RetiredInstructionCounter { get; } =
        new(
            "RDINSTRET",
            CompilerLane7CounterHintAbiClass.RetireAccountingCounter,
            "ScalarSystemCounter",
            "Lane7CounterReplayDeferred",
            "NoAllocationUntilRetireAccountingReplayRollbackRetirePublicationAbi",
            "rd",
            "Retired-instruction accounting source and replay rollback policy are not selected.",
            "Future rd publication is blocked until retire-owned retired-instruction counter semantics are explicit.",
            RetiredInstructionRequiredPolicyDecisions,
            requiresCounterSourceAbi: true,
            requiresReplayStableCounterModel: true,
            requiresRetireAccountingModel: true,
            requiresPrivilegeVirtualizationPolicy: true,
            requiresRetireOwnedPublication: true,
            rejectHostEvidenceLeak: true,
            separateFromCycleCounter: true,
            noGenericSystemOpFallback: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public static CompilerLane7CounterHintAbiContract PauseHint { get; } =
        new(
            "PAUSE",
            CompilerLane7CounterHintAbiClass.SchedulingHint,
            "ScalarSystemCounter",
            "Lane7HintNoExecutionGuarantee",
            "NoAllocationUntilHintEncodingProgressFairnessNoStateAbi",
            "no operands or approved hint immediate",
            "Scheduling-hint encoding and fairness semantics are not selected.",
            "No architectural state, no register writeback, and no progress guarantee may be inferred.",
            PauseRequiredPolicyDecisions,
            requiresHintEncodingAbi: true,
            requiresProgressFairnessPolicy: true,
            noArchitecturalProgressGuarantee: true,
            requiresNoArchitecturalStateLeakage: true,
            rejectSynchronizationPrimitiveSemantics: true,
            requiresReplayRollbackEvidence: true,
            noGenericSystemOpFallback: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public static IReadOnlyList<CompilerLane7CounterHintAbiContract> AllCounterHintRows { get; } =
    [
        TimeCounter,
        RetiredInstructionCounter,
        PauseHint
    ];

    public string Mnemonic { get; }
    public CompilerLane7CounterHintAbiClass AbiClass { get; }
    public string ExtensionName { get; }
    public string EvidenceBoundary { get; }
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public bool RequiresCounterSourceAbi { get; }
    public bool RequiresReplayStableCounterModel { get; }
    public bool RequiresRetireAccountingModel { get; }
    public bool RequiresPrivilegeVirtualizationPolicy { get; }
    public bool RequiresRetireOwnedPublication { get; }
    public bool RejectHostEvidenceLeak { get; }
    public bool SeparateFromCycleCounter { get; }
    public bool RequiresHintEncodingAbi { get; }
    public bool RequiresProgressFairnessPolicy { get; }
    public bool NoArchitecturalProgressGuarantee { get; }
    public bool RequiresNoArchitecturalStateLeakage { get; }
    public bool RejectSynchronizationPrimitiveSemantics { get; }
    public bool RequiresReplayRollbackEvidence { get; }
    public bool NoGenericSystemOpFallback { get; }
    public bool NoHiddenScalarLowering { get; }
    public bool NoMultiOpEmission { get; }
    public bool HasOpcodeAllocation => false;
    public bool IsExecutable => false;
    public bool CompilerEmissionAllowed => false;
    public bool CompilerHelperAllowed => false;

    public void RequireCompilerEmissionAuthority()
    {
        string requiredDecisions = AbiClass switch
        {
            CompilerLane7CounterHintAbiClass.ReplayStableTimeCounter =>
                "replay-stable time source, privilege/virtualization, retire-owned publication, and no-host-time-source ABI decisions",
            CompilerLane7CounterHintAbiClass.RetireAccountingCounter =>
                "retire accounting, replay-stable counter source, privilege/virtualization, and retire-owned publication ABI decisions",
            CompilerLane7CounterHintAbiClass.SchedulingHint =>
                "hint encoding, progress/fairness, replay/rollback, no-state-leakage, and no-synchronization-semantics ABI decisions",
            _ => "required Lane7 counter/hint ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until {requiredDecisions} are explicit.");
    }
}
