using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerSystemCounterReplayPolicy : byte
{
    RetireOrderedReplayStable = 0,
    DeferredUntilReplayStableCounterSource = 1,
    DeferredUntilRetireAccountingModel = 2
}

/// <summary>
/// Compiler-visible ABI contract for system counter rows.
/// This surface records compiler evidence and does not allocate runtime/ISA rows.
/// </summary>
public sealed class CompilerSystemCounterAbiContract
{
    private static readonly string[] CycleRequiredPolicyDecisions =
    [
        "CycleCsrAddress",
        "CsrOrderedSerialization",
        "RetireOwnedRegisterWriteback",
        "ReplayRollbackRestoresArchitecturalTruth",
        "NoHostTimeSource"
    ];

    private static readonly string[] TimeRequiredPolicyDecisions =
    [
        "CounterSourceAbi",
        "ReplayStableCounterModel",
        "PrivilegeVirtualizationBoundary",
        "RetireOwnedPublication",
        "NoHostWallClockLeak"
    ];

    private static readonly string[] RetiredInstructionRequiredPolicyDecisions =
    [
        "RetireAccountingModel",
        "ReplayStableCounterModel",
        "PrivilegeVirtualizationBoundary",
        "RetireOwnedPublication",
        "NoSpeculativeRetireCount"
    ];

    private CompilerSystemCounterAbiContract(
        string mnemonic,
        string extensionName,
        string evidenceBoundary,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        CompilerSystemCounterReplayPolicy replayPolicy,
        IReadOnlyList<string> requiredPolicyDecisions,
        ushort? csrAddress,
        bool hasOpcodeAllocation,
        bool isExecutable,
        bool compilerEmissionAllowed,
        bool replayDeterminismPolicyResolved,
        bool retirePublicationPolicyResolved,
        bool requiresCounterSourceAbi = false,
        bool requiresReplayStableCounterModel = false,
        bool requiresRetireAccountingModel = false,
        bool requiresPrivilegeVirtualizationPolicy = false,
        bool requiresRetireOwnedPublication = false,
        bool rejectHostEvidenceLeak = false,
        bool separateFromCycleCounter = false)
    {
        Mnemonic = mnemonic;
        ExtensionName = extensionName;
        EvidenceBoundary = evidenceBoundary;
        AbiDecision = abiDecision;
        OperandShape = operandShape;
        DataSemantics = dataSemantics;
        ResultSemantics = resultSemantics;
        ReplayPolicy = replayPolicy;
        RequiredPolicyDecisions = requiredPolicyDecisions;
        CsrAddress = csrAddress;
        HasOpcodeAllocation = hasOpcodeAllocation;
        IsExecutable = isExecutable;
        CompilerEmissionAllowed = compilerEmissionAllowed;
        ReplayDeterminismPolicyResolved = replayDeterminismPolicyResolved;
        RetirePublicationPolicyResolved = retirePublicationPolicyResolved;
        RequiresCounterSourceAbi = requiresCounterSourceAbi;
        RequiresReplayStableCounterModel = requiresReplayStableCounterModel;
        RequiresRetireAccountingModel = requiresRetireAccountingModel;
        RequiresPrivilegeVirtualizationPolicy = requiresPrivilegeVirtualizationPolicy;
        RequiresRetireOwnedPublication = requiresRetireOwnedPublication;
        RejectHostEvidenceLeak = rejectHostEvidenceLeak;
        SeparateFromCycleCounter = separateFromCycleCounter;
    }

    public static CompilerSystemCounterAbiContract CycleCounter { get; } =
        new(
            "RDCYCLE",
            "ScalarSystemCounter",
            "CycleCounterRetireReplayClosedCompilerEmission",
            "EmitOnlyCycleCsrWithRdOnlyPayloadAndRetireOwnedReplayEvidence",
            "rd",
            "Reads the runtime-owned cycle CSR through the closed lane-7 counter path.",
            "rd receives the retire-owned 64-bit cycle CSR value; x0 writeback remains discarded at retire.",
            CompilerSystemCounterReplayPolicy.RetireOrderedReplayStable,
            CycleRequiredPolicyDecisions,
            0xC00,
            hasOpcodeAllocation: true,
            isExecutable: true,
            compilerEmissionAllowed: true,
            replayDeterminismPolicyResolved: true,
            retirePublicationPolicyResolved: true,
            rejectHostEvidenceLeak: true);

    public static CompilerSystemCounterAbiContract TimeCounter { get; } =
        new(
            "RDTIME",
            "ScalarSystemCounter",
            "Lane7CounterReplayDeferred",
            "NoAllocationUntilReplayStableTimeSourcePrivilegeVirtualizationRetirePublicationAbi",
            "rd",
            "Time source domain, virtualization boundary, and replay-stable source ABI are not selected.",
            "Future rd publication is blocked until retire-owned deterministic time-counter semantics are explicit.",
            CompilerSystemCounterReplayPolicy.DeferredUntilReplayStableCounterSource,
            TimeRequiredPolicyDecisions,
            csrAddress: null,
            hasOpcodeAllocation: false,
            isExecutable: false,
            compilerEmissionAllowed: false,
            replayDeterminismPolicyResolved: false,
            retirePublicationPolicyResolved: false,
            requiresCounterSourceAbi: true,
            requiresReplayStableCounterModel: true,
            requiresPrivilegeVirtualizationPolicy: true,
            requiresRetireOwnedPublication: true,
            rejectHostEvidenceLeak: true,
            separateFromCycleCounter: true);

    public static CompilerSystemCounterAbiContract RetiredInstructionCounter { get; } =
        new(
            "RDINSTRET",
            "ScalarSystemCounter",
            "Lane7CounterReplayDeferred",
            "NoAllocationUntilRetireAccountingReplayRollbackRetirePublicationAbi",
            "rd",
            "Retired-instruction accounting source and replay rollback policy are not selected.",
            "Future rd publication is blocked until retire-owned retired-instruction counter semantics are explicit.",
            CompilerSystemCounterReplayPolicy.DeferredUntilRetireAccountingModel,
            RetiredInstructionRequiredPolicyDecisions,
            csrAddress: null,
            hasOpcodeAllocation: false,
            isExecutable: false,
            compilerEmissionAllowed: false,
            replayDeterminismPolicyResolved: false,
            retirePublicationPolicyResolved: false,
            requiresCounterSourceAbi: true,
            requiresReplayStableCounterModel: true,
            requiresRetireAccountingModel: true,
            requiresPrivilegeVirtualizationPolicy: true,
            requiresRetireOwnedPublication: true,
            rejectHostEvidenceLeak: true,
            separateFromCycleCounter: true);

    public static IReadOnlyList<CompilerSystemCounterAbiContract> AllSystemCounterRows { get; } =
    [
        CycleCounter,
        TimeCounter,
        RetiredInstructionCounter
    ];

    public static IReadOnlyList<CompilerSystemCounterAbiContract> DeferredSystemCounterRows { get; } =
    [
        TimeCounter,
        RetiredInstructionCounter
    ];

    public string Mnemonic { get; }
    public string ExtensionName { get; }
    public string EvidenceBoundary { get; }
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public CompilerSystemCounterReplayPolicy ReplayPolicy { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public ushort? CsrAddress { get; }
    public bool HasOpcodeAllocation { get; }
    public bool IsExecutable { get; }
    public bool CompilerEmissionAllowed { get; }
    public bool ReplayDeterminismPolicyResolved { get; }
    public bool RetirePublicationPolicyResolved { get; }
    public bool RequiresCounterSourceAbi { get; }
    public bool RequiresReplayStableCounterModel { get; }
    public bool RequiresRetireAccountingModel { get; }
    public bool RequiresPrivilegeVirtualizationPolicy { get; }
    public bool RequiresRetireOwnedPublication { get; }
    public bool RejectHostEvidenceLeak { get; }
    public bool SeparateFromCycleCounter { get; }

    public void RequireCompilerEmissionAuthority()
    {
        if (CompilerEmissionAllowed)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until replay-stable counter source, privilege/virtualization, and retire-owned publication ABI decisions are explicit.");
    }
}
