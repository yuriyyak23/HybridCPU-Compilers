using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerDeferredScalarAbiClass : byte
{
    ScalarCrcChecksum = 0,
    ScalarMultiPrecision = 1,
    ScalarSelectCarrier = 2
}

/// <summary>
/// Compiler-visible contract for scalar rows that have runtime placeholders but no emission authority.
/// </summary>
public sealed class CompilerDeferredScalarAbiContract
{
    private static readonly string[] CselRequiredPolicyDecisions =
    [
        "FourRegisterCarrierAbi",
        "ConditionRegisterTransport",
        "SelectResultSemantics",
        "RetireOwnedRegisterWriteback",
        "ReplayRollbackEvidence",
        "NoCzeroAliasLowering"
    ];

    private static readonly string[] CrcRequiredPolicyDecisions =
    [
        "Polynomial",
        "InputReflection",
        "OutputReflection",
        "SeedInitialization",
        "FinalXor",
        "EndianIngestionOrder",
        "DataWidth",
        "ResultWidthAndExtension"
    ];

    private static readonly string[] AdcRequiredPolicyDecisions =
    [
        "ExplicitCarryInputTransport",
        "ExplicitCarryOutputTransport",
        "RetireOwnedCarryBorrowPublication",
        "CarryBorrowConsumerAbi",
        "NoImplicitFlags"
    ];

    private static readonly string[] SbcRequiredPolicyDecisions =
    [
        "ExplicitBorrowInputTransport",
        "ExplicitBorrowOutputTransport",
        "RetireOwnedCarryBorrowPublication",
        "CarryBorrowConsumerAbi",
        "NoImplicitFlags"
    ];

    private static readonly string[] AddcRequiredPolicyDecisions =
    [
        "ExplicitCarryOutputTransport",
        "RetireOwnedCarryBorrowPublication",
        "CarryBorrowConsumerAbi",
        "NoImplicitFlags"
    ];

    private static readonly string[] SubcRequiredPolicyDecisions =
    [
        "ExplicitBorrowOutputTransport",
        "RetireOwnedCarryBorrowPublication",
        "CarryBorrowConsumerAbi",
        "NoImplicitFlags"
    ];

    private CompilerDeferredScalarAbiContract(
        string mnemonic,
        CompilerDeferredScalarAbiClass abiClass,
        string extensionName,
        string evidenceBoundary,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        int resultBits,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresPolynomialAbi = false,
        bool requiresReflectionAbi = false,
        bool requiresSeedFinalXorAbi = false,
        bool requiresEndianPolicyAbi = false,
        bool requiresDataWidthAbi = false,
        bool requiresResultSemanticsAbi = false,
        bool rejectImplicitPolynomialSelection = false,
        bool requiresFourRegisterCarrierAbi = false,
        bool requiresExternalCarrierAbi = false,
        bool requiresConditionRegisterAbi = false,
        bool requiresSelectResultAbi = false,
        bool requiresNoCzeroAliasPolicy = false,
        bool fourSourceCarrierDecisionClosed = false,
        bool approvedFourSourceCarrier = false,
        bool currentPackedScalarIrSupportsCarrier = false,
        bool rejectCzeroAliasLowering = false,
        bool rejectHiddenMultiOpSelectLowering = false,
        bool requiresCarryInAbi = false,
        bool requiresCarryOutAbi = false,
        bool requiresBorrowInAbi = false,
        bool requiresBorrowOutAbi = false,
        bool requiresCarryBorrowPublicationAbi = false,
        bool requiresExplicitCarryInputTransportAbi = false,
        bool requiresExplicitCarryOutputTransportAbi = false,
        bool requiresExplicitBorrowInputTransportAbi = false,
        bool requiresExplicitBorrowOutputTransportAbi = false,
        bool requiresRetireOwnedPublicationAbi = false,
        bool noImplicitFlags = false,
        bool rejectHiddenArchitecturalFlags = false)
    {
        Mnemonic = mnemonic;
        AbiClass = abiClass;
        ExtensionName = extensionName;
        EvidenceBoundary = evidenceBoundary;
        AbiDecision = abiDecision;
        OperandShape = operandShape;
        DataSemantics = dataSemantics;
        ResultSemantics = resultSemantics;
        ResultBits = resultBits;
        RequiredPolicyDecisions = requiredPolicyDecisions;
        RequiresPolynomialAbi = requiresPolynomialAbi;
        RequiresReflectionAbi = requiresReflectionAbi;
        RequiresSeedFinalXorAbi = requiresSeedFinalXorAbi;
        RequiresEndianPolicyAbi = requiresEndianPolicyAbi;
        RequiresDataWidthAbi = requiresDataWidthAbi;
        RequiresResultSemanticsAbi = requiresResultSemanticsAbi;
        RejectImplicitPolynomialSelection = rejectImplicitPolynomialSelection;
        RequiresFourRegisterCarrierAbi = requiresFourRegisterCarrierAbi;
        RequiresExternalCarrierAbi = requiresExternalCarrierAbi;
        RequiresConditionRegisterAbi = requiresConditionRegisterAbi;
        RequiresSelectResultAbi = requiresSelectResultAbi;
        RequiresNoCzeroAliasPolicy = requiresNoCzeroAliasPolicy;
        FourSourceCarrierDecisionClosed = fourSourceCarrierDecisionClosed;
        ApprovedFourSourceCarrier = approvedFourSourceCarrier;
        CurrentPackedScalarIrSupportsCarrier = currentPackedScalarIrSupportsCarrier;
        RejectCzeroAliasLowering = rejectCzeroAliasLowering;
        RejectHiddenMultiOpSelectLowering = rejectHiddenMultiOpSelectLowering;
        RequiresCarryInAbi = requiresCarryInAbi;
        RequiresCarryOutAbi = requiresCarryOutAbi;
        RequiresBorrowInAbi = requiresBorrowInAbi;
        RequiresBorrowOutAbi = requiresBorrowOutAbi;
        RequiresCarryBorrowPublicationAbi = requiresCarryBorrowPublicationAbi;
        RequiresExplicitCarryInputTransportAbi = requiresExplicitCarryInputTransportAbi;
        RequiresExplicitCarryOutputTransportAbi = requiresExplicitCarryOutputTransportAbi;
        RequiresExplicitBorrowInputTransportAbi = requiresExplicitBorrowInputTransportAbi;
        RequiresExplicitBorrowOutputTransportAbi = requiresExplicitBorrowOutputTransportAbi;
        RequiresRetireOwnedPublicationAbi = requiresRetireOwnedPublicationAbi;
        NoImplicitFlags = noImplicitFlags;
        RejectHiddenArchitecturalFlags = rejectHiddenArchitecturalFlags;
    }

    public static CompilerDeferredScalarAbiContract Csel { get; } =
        new(
            "CSEL",
            CompilerDeferredScalarAbiClass.ScalarSelectCarrier,
            "ScalarSelectCzero",
            "ScalarSelectAbiDeferredNoEmission",
            "NoAllocationUntilFourRegisterCarrierConditionResultAliasPolicyAbi",
            "rd, rs_true, rs_false, rs_cond",
            "Current packed scalar IR has no approved fourth source carrier for rs_cond; condition transport cannot be inferred from CZERO.* or sideband metadata.",
            "Future rd selects rs_true or rs_false only after four-register carrier, condition, result, retire, replay, and alias-separation policy are explicit.",
            64,
            CselRequiredPolicyDecisions,
            requiresFourRegisterCarrierAbi: true,
            requiresExternalCarrierAbi: true,
            requiresConditionRegisterAbi: true,
            requiresSelectResultAbi: true,
            requiresNoCzeroAliasPolicy: true,
            fourSourceCarrierDecisionClosed: true,
            approvedFourSourceCarrier: false,
            currentPackedScalarIrSupportsCarrier: false,
            rejectCzeroAliasLowering: true,
            rejectHiddenMultiOpSelectLowering: true);

    public static CompilerDeferredScalarAbiContract Crc32 { get; } =
        new(
            "CRC32",
            CompilerDeferredScalarAbiClass.ScalarCrcChecksum,
            "ScalarCrcChecksum",
            "CrcPolynomialAbiDeferredNoEmission",
            "NoAllocationUntilPolynomialReflectionSeedFinalXorEndianDataResultAbi",
            "rd, rs_seed, rs_data",
            "Source data ingestion width, byte order, and chunking are not selected.",
            "Future rd receives a 32-bit CRC result only after result extension semantics are explicit.",
            32,
            CrcRequiredPolicyDecisions,
            requiresPolynomialAbi: true,
            requiresReflectionAbi: true,
            requiresSeedFinalXorAbi: true,
            requiresEndianPolicyAbi: true,
            requiresDataWidthAbi: true,
            requiresResultSemanticsAbi: true,
            rejectImplicitPolynomialSelection: true);

    public static CompilerDeferredScalarAbiContract Crc64 { get; } =
        new(
            "CRC64",
            CompilerDeferredScalarAbiClass.ScalarCrcChecksum,
            "ScalarCrcChecksum",
            "CrcPolynomialAbiDeferredNoEmission",
            "NoAllocationUntilPolynomialReflectionSeedFinalXorEndianDataResultAbi",
            "rd, rs_seed, rs_data",
            "Source data ingestion width, byte order, and chunking are not selected.",
            "Future rd receives a 64-bit CRC result only after result semantics are explicit.",
            64,
            CrcRequiredPolicyDecisions,
            requiresPolynomialAbi: true,
            requiresReflectionAbi: true,
            requiresSeedFinalXorAbi: true,
            requiresEndianPolicyAbi: true,
            requiresDataWidthAbi: true,
            requiresResultSemanticsAbi: true,
            rejectImplicitPolynomialSelection: true);

    public static CompilerDeferredScalarAbiContract Adc { get; } =
        new(
            "ADC",
            CompilerDeferredScalarAbiClass.ScalarMultiPrecision,
            "ScalarMultiPrecision",
            "MultiPrecisionCarryAbiDeferredNoEmission",
            "NoAllocationUntilExplicitCarryBorrowTransportRetirePublicationAbi",
            "rd, rs1, rs2, carry_in",
            "Carry input transport is not selected and must not be inferred from implicit flags.",
            "Future rd receives the XLEN sum and carry-out publishes only through explicit retire-owned ABI.",
            64,
            AdcRequiredPolicyDecisions,
            requiresCarryInAbi: true,
            requiresCarryOutAbi: true,
            requiresCarryBorrowPublicationAbi: true,
            requiresExplicitCarryInputTransportAbi: true,
            requiresExplicitCarryOutputTransportAbi: true,
            requiresRetireOwnedPublicationAbi: true,
            noImplicitFlags: true,
            rejectHiddenArchitecturalFlags: true);

    public static CompilerDeferredScalarAbiContract Sbc { get; } =
        new(
            "SBC",
            CompilerDeferredScalarAbiClass.ScalarMultiPrecision,
            "ScalarMultiPrecision",
            "MultiPrecisionCarryAbiDeferredNoEmission",
            "NoAllocationUntilExplicitCarryBorrowTransportRetirePublicationAbi",
            "rd, rs1, rs2, borrow_in",
            "Borrow input transport is not selected and must not be inferred from implicit flags.",
            "Future rd receives the XLEN difference and borrow-out publishes only through explicit retire-owned ABI.",
            64,
            SbcRequiredPolicyDecisions,
            requiresBorrowInAbi: true,
            requiresBorrowOutAbi: true,
            requiresCarryBorrowPublicationAbi: true,
            requiresExplicitBorrowInputTransportAbi: true,
            requiresExplicitBorrowOutputTransportAbi: true,
            requiresRetireOwnedPublicationAbi: true,
            noImplicitFlags: true,
            rejectHiddenArchitecturalFlags: true);

    public static CompilerDeferredScalarAbiContract Addc { get; } =
        new(
            "ADDC",
            CompilerDeferredScalarAbiClass.ScalarMultiPrecision,
            "ScalarMultiPrecision",
            "MultiPrecisionCarryAbiDeferredNoEmission",
            "NoAllocationUntilExplicitCarryBorrowTransportRetirePublicationAbi",
            "rd, rs1, rs2",
            "Carry-out transport is not selected and must not be inferred from implicit flags.",
            "Future rd receives the XLEN sum and carry-out publishes only through explicit retire-owned ABI.",
            64,
            AddcRequiredPolicyDecisions,
            requiresCarryOutAbi: true,
            requiresCarryBorrowPublicationAbi: true,
            requiresExplicitCarryOutputTransportAbi: true,
            requiresRetireOwnedPublicationAbi: true,
            noImplicitFlags: true,
            rejectHiddenArchitecturalFlags: true);

    public static CompilerDeferredScalarAbiContract Subc { get; } =
        new(
            "SUBC",
            CompilerDeferredScalarAbiClass.ScalarMultiPrecision,
            "ScalarMultiPrecision",
            "MultiPrecisionCarryAbiDeferredNoEmission",
            "NoAllocationUntilExplicitCarryBorrowTransportRetirePublicationAbi",
            "rd, rs1, rs2",
            "Borrow-out transport is not selected and must not be inferred from implicit flags.",
            "Future rd receives the XLEN difference and borrow-out publishes only through explicit retire-owned ABI.",
            64,
            SubcRequiredPolicyDecisions,
            requiresBorrowOutAbi: true,
            requiresCarryBorrowPublicationAbi: true,
            requiresExplicitBorrowOutputTransportAbi: true,
            requiresRetireOwnedPublicationAbi: true,
            noImplicitFlags: true,
            rejectHiddenArchitecturalFlags: true);

    public static IReadOnlyList<CompilerDeferredScalarAbiContract> ScalarSelectCarrierRows { get; } =
    [
        Csel
    ];

    public static IReadOnlyList<CompilerDeferredScalarAbiContract> ScalarCrcChecksumRows { get; } =
    [
        Crc32,
        Crc64
    ];

    public static IReadOnlyList<CompilerDeferredScalarAbiContract> ScalarMultiPrecisionRows { get; } =
    [
        Adc,
        Sbc,
        Addc,
        Subc
    ];

    public static IReadOnlyList<CompilerDeferredScalarAbiContract> AllDeferredScalarRows { get; } =
    [
        Csel,
        Crc32,
        Crc64,
        Adc,
        Sbc,
        Addc,
        Subc
    ];

    public string Mnemonic { get; }
    public CompilerDeferredScalarAbiClass AbiClass { get; }
    public string ExtensionName { get; }
    public string EvidenceBoundary { get; }
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public int ResultBits { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public bool RequiresPolynomialAbi { get; }
    public bool RequiresReflectionAbi { get; }
    public bool RequiresSeedFinalXorAbi { get; }
    public bool RequiresEndianPolicyAbi { get; }
    public bool RequiresDataWidthAbi { get; }
    public bool RequiresResultSemanticsAbi { get; }
    public bool RejectImplicitPolynomialSelection { get; }
    public bool RequiresFourRegisterCarrierAbi { get; }
    public bool RequiresExternalCarrierAbi { get; }
    public bool RequiresConditionRegisterAbi { get; }
    public bool RequiresSelectResultAbi { get; }
    public bool RequiresNoCzeroAliasPolicy { get; }
    public bool FourSourceCarrierDecisionClosed { get; }
    public bool ApprovedFourSourceCarrier { get; }
    public bool CurrentPackedScalarIrSupportsCarrier { get; }
    public bool RejectCzeroAliasLowering { get; }
    public bool RejectHiddenMultiOpSelectLowering { get; }
    public bool RequiresCarryInAbi { get; }
    public bool RequiresCarryOutAbi { get; }
    public bool RequiresBorrowInAbi { get; }
    public bool RequiresBorrowOutAbi { get; }
    public bool RequiresCarryBorrowPublicationAbi { get; }
    public bool RequiresExplicitCarryInputTransportAbi { get; }
    public bool RequiresExplicitCarryOutputTransportAbi { get; }
    public bool RequiresExplicitBorrowInputTransportAbi { get; }
    public bool RequiresExplicitBorrowOutputTransportAbi { get; }
    public bool RequiresRetireOwnedPublicationAbi { get; }
    public bool NoImplicitFlags { get; }
    public bool RejectHiddenArchitecturalFlags { get; }
    public bool HasOpcodeAllocation => false;
    public bool CompilerEmissionAllowed => false;

    public void RequireCompilerEmissionAuthority()
    {
        if (CompilerEmissionAllowed)
        {
            return;
        }

        string requiredDecisions = AbiClass switch
        {
            CompilerDeferredScalarAbiClass.ScalarCrcChecksum =>
                "polynomial, reflection, seed, final-xor, endian, data-width, and result semantics ABI decisions",
            CompilerDeferredScalarAbiClass.ScalarMultiPrecision =>
                "explicit carry/borrow input transport, retire-owned carry/borrow output publication, and no-implicit-flags ABI decisions",
            CompilerDeferredScalarAbiClass.ScalarSelectCarrier =>
                "four-register carrier, condition transport, select result, retire/replay, and no-CZERO-alias ABI decisions",
            _ => "required ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until {requiredDecisions} are explicit.");
    }
}
