using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerMatrixTileOptionalDisabledAbiClass : byte
{
    LoadBoundary = 0,
    StoreBoundary = 1,
    MaccBoundary = 2,
    TransposeBoundary = 3
}

/// <summary>
/// Compiler-visible no-emission ABI audit for optional-disabled matrix/tile rows.
/// </summary>
public sealed class CompilerMatrixTileOptionalDisabledAbiContract
{
    private static readonly string[] CommonRequiredPolicyDecisions =
    [
        "OptionalDisabledIsaV4Status",
        "SeparateRuntimeIsaWorkRequired",
        "CompilerNoEmissionBoundary",
        "TileExecutionModel",
        "TileDescriptorAbi",
        "TypedTileMicroOp",
        "VectorLegalityMatrixClosure",
        "TileVlmMaterialization",
        "StagedPublicationRetirePolicy",
        "ReplayRollbackGoldenEvidence",
        "RuntimeOwnedLegalityIsFinal",
        "NoCompilerEnumAuthority",
        "NoCompilerOpcodeAuthority",
        "NoRuntimeOpcodeMetadataAsExecution",
        "NoCanonicalDecoderAcceptance",
        "NoRegistryFactory",
        "NoExecutionSemantics",
        "NoTypedFacadeHelperEmission",
        "NoScalarHelperFallback",
        "NoBaseVectorFallback",
        "NoLane6DescriptorFallback",
        "NoLane6StreamFallback",
        "NoLane7AcceleratorFallback",
        "NoExternalBackendFallback",
        "NoVmxSpecificPathFallback",
        "NoExecutableRowAliasPromotion",
        "NoHostOwnedEvidencePublication"
    ];

    private static readonly string[] TileMemoryRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "TileMemoryShapeFaultModel",
        "TileMemoryOrderingFaultReplayPolicy",
        "NoBaseMemoryOpcodeDuplication",
        "NoSegmentMemoryFallback",
        "NoDescriptorStreamFallback"
    ];

    private static readonly string[] LoadRequiredPolicyDecisions =
    [
        .. TileMemoryRequiredPolicyDecisions,
        "RetireStagedPublication"
    ];

    private static readonly string[] StoreRequiredPolicyDecisions =
    [
        .. TileMemoryRequiredPolicyDecisions,
        "RetireStagedCommit"
    ];

    private static readonly string[] MaccRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "AccumulatorTileAbi",
        "AccumulatorPrecisionAbi",
        "AccumulatorResultFootprintAbi",
        "DeterministicOrderingReplayPolicy",
        "RetireStagedPublication",
        "NoScopedVdotWideFallback",
        "NoBaseDotProductFallback",
        "NoWideningFmaFallback",
        "NoDotTileVariantFallback"
    ];

    private static readonly string[] TransposeRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "TransposeTilePolicyAbi",
        "TileShapePermutationPolicy",
        "RetireStagedPublication",
        "NoVectorTransposeFallback",
        "NoStructureMovementFallback",
        "NoSegmentMemoryFallback"
    ];

    private CompilerMatrixTileOptionalDisabledAbiContract(
        string mnemonic,
        CompilerMatrixTileOptionalDisabledAbiClass abiClass,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool isTileMemory = false,
        bool isLoad = false,
        bool isStore = false,
        bool isMacc = false,
        bool isTranspose = false,
        bool requiresTileMemoryShapeFaultModel = false,
        bool requiresTileMemoryOrderingFaultReplayPolicy = false,
        bool requiresRetireStagedPublication = false,
        bool requiresRetireStagedCommit = false,
        bool requiresAccumulatorTileAbi = false,
        bool requiresAccumulatorPrecisionAbi = false,
        bool requiresAccumulatorResultFootprintAbi = false,
        bool requiresDeterministicOrderingReplayPolicy = false,
        bool requiresTransposeTilePolicyAbi = false,
        bool requiresTileShapePermutationPolicy = false,
        bool noBaseMemoryOpcodeDuplication = false,
        bool noSegmentMemoryFallback = false,
        bool noDescriptorStreamFallback = false,
        bool noScopedVdotWideFallback = false,
        bool noBaseDotProductFallback = false,
        bool noWideningFmaFallback = false,
        bool noDotTileVariantFallback = false,
        bool noVectorTransposeFallback = false,
        bool noStructureMovementFallback = false)
    {
        Mnemonic = mnemonic;
        AbiClass = abiClass;
        AbiDecision = abiDecision;
        OperandShape = operandShape;
        DataSemantics = dataSemantics;
        ResultSemantics = resultSemantics;
        RequiredPolicyDecisions = requiredPolicyDecisions;
        IsTileMemory = isTileMemory;
        IsLoad = isLoad;
        IsStore = isStore;
        IsMacc = isMacc;
        IsTranspose = isTranspose;
        RequiresTileMemoryShapeFaultModel = requiresTileMemoryShapeFaultModel;
        RequiresTileMemoryOrderingFaultReplayPolicy = requiresTileMemoryOrderingFaultReplayPolicy;
        RequiresRetireStagedPublication = requiresRetireStagedPublication;
        RequiresRetireStagedCommit = requiresRetireStagedCommit;
        RequiresAccumulatorTileAbi = requiresAccumulatorTileAbi;
        RequiresAccumulatorPrecisionAbi = requiresAccumulatorPrecisionAbi;
        RequiresAccumulatorResultFootprintAbi = requiresAccumulatorResultFootprintAbi;
        RequiresDeterministicOrderingReplayPolicy = requiresDeterministicOrderingReplayPolicy;
        RequiresTransposeTilePolicyAbi = requiresTransposeTilePolicyAbi;
        RequiresTileShapePermutationPolicy = requiresTileShapePermutationPolicy;
        NoBaseMemoryOpcodeDuplication = noBaseMemoryOpcodeDuplication;
        NoSegmentMemoryFallback = noSegmentMemoryFallback;
        NoDescriptorStreamFallback = noDescriptorStreamFallback;
        NoScopedVdotWideFallback = noScopedVdotWideFallback;
        NoBaseDotProductFallback = noBaseDotProductFallback;
        NoWideningFmaFallback = noWideningFmaFallback;
        NoDotTileVariantFallback = noDotTileVariantFallback;
        NoVectorTransposeFallback = noVectorTransposeFallback;
        NoStructureMovementFallback = noStructureMovementFallback;
    }

    public static CompilerMatrixTileOptionalDisabledAbiContract LoadAudit { get; } =
        new(
            "MTILE_LOAD",
            CompilerMatrixTileOptionalDisabledAbiClass.LoadBoundary,
            "NoCompilerSupportUntilRuntimeTileIsaExecutionModelMemoryFaultRetireReplayGoldenEvidence",
            "tile descriptor, memory base/stride/shape, destination tile, and optional mask/tail sideband are not compiler ABI.",
            "Tile memory load shape, ordering, partial-fault, and replay behavior remain runtime/ISA-owned.",
            "Future execution may materialize a tile only after runtime-owned VLM closure and retire-staged publication.",
            LoadRequiredPolicyDecisions,
            isTileMemory: true,
            isLoad: true,
            requiresTileMemoryShapeFaultModel: true,
            requiresTileMemoryOrderingFaultReplayPolicy: true,
            requiresRetireStagedPublication: true,
            noBaseMemoryOpcodeDuplication: true,
            noSegmentMemoryFallback: true,
            noDescriptorStreamFallback: true);

    public static CompilerMatrixTileOptionalDisabledAbiContract StoreAudit { get; } =
        new(
            "MTILE_STORE",
            CompilerMatrixTileOptionalDisabledAbiClass.StoreBoundary,
            "NoCompilerSupportUntilRuntimeTileIsaExecutionModelMemoryFaultCommitReplayGoldenEvidence",
            "tile descriptor, memory base/stride/shape, source tile, and optional mask/tail sideband are not compiler ABI.",
            "Tile memory store shape, ordering, partial-fault, and replay behavior remain runtime/ISA-owned.",
            "Future execution may commit tile memory effects only after runtime-owned VLM closure and retire-staged commit.",
            StoreRequiredPolicyDecisions,
            isTileMemory: true,
            isStore: true,
            requiresTileMemoryShapeFaultModel: true,
            requiresTileMemoryOrderingFaultReplayPolicy: true,
            requiresRetireStagedCommit: true,
            noBaseMemoryOpcodeDuplication: true,
            noSegmentMemoryFallback: true,
            noDescriptorStreamFallback: true);

    public static CompilerMatrixTileOptionalDisabledAbiContract MaccAudit { get; } =
        new(
            "MTILE_MACC",
            CompilerMatrixTileOptionalDisabledAbiClass.MaccBoundary,
            "NoCompilerSupportUntilRuntimeTileIsaExecutionModelAccumulatorTileRetireReplayGoldenEvidence",
            "source tiles, accumulator tile, destination tile, and tile shape sideband are not compiler ABI.",
            "Tile multiply-accumulate precision, accumulator footprint, and ordering remain runtime/ISA-owned.",
            "Future execution may publish accumulator tile results only after deterministic replay and retire-staged publication.",
            MaccRequiredPolicyDecisions,
            isMacc: true,
            requiresRetireStagedPublication: true,
            requiresAccumulatorTileAbi: true,
            requiresAccumulatorPrecisionAbi: true,
            requiresAccumulatorResultFootprintAbi: true,
            requiresDeterministicOrderingReplayPolicy: true,
            noScopedVdotWideFallback: true,
            noBaseDotProductFallback: true,
            noWideningFmaFallback: true,
            noDotTileVariantFallback: true);

    public static CompilerMatrixTileOptionalDisabledAbiContract TransposeAudit { get; } =
        new(
            "MTRANSPOSE",
            CompilerMatrixTileOptionalDisabledAbiClass.TransposeBoundary,
            "NoCompilerSupportUntilRuntimeTileIsaExecutionModelTransposePolicyRetireReplayGoldenEvidence",
            "source tile, destination tile, tile shape, and transpose policy sideband are not compiler ABI.",
            "Tile transpose shape permutation, aliasing, and element-order policy remain runtime/ISA-owned.",
            "Future execution may publish transposed tile results only after runtime-owned VLM closure and retire-staged publication.",
            TransposeRequiredPolicyDecisions,
            isTranspose: true,
            requiresRetireStagedPublication: true,
            requiresTransposeTilePolicyAbi: true,
            requiresTileShapePermutationPolicy: true,
            noSegmentMemoryFallback: true,
            noVectorTransposeFallback: true,
            noStructureMovementFallback: true);

    public static IReadOnlyList<CompilerMatrixTileOptionalDisabledAbiContract> AllOptionalDisabledRows { get; } =
    [
        LoadAudit,
        StoreAudit,
        MaccAudit,
        TransposeAudit
    ];

    public string Mnemonic { get; }
    public CompilerMatrixTileOptionalDisabledAbiClass AbiClass { get; }
    public string ExtensionName => "XMatrix";
    public string EvidenceBoundary => "VectorDotMatrixDeferredNoExecution";
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public bool OptionalDisabledInIsaV4 => true;
    public bool DeclaredOnlyRuntimeEvidence => true;
    public bool HasOptionalDisabledIsaEnumPublication => true;
    public bool HasOptionalDisabledIsaOpcodeValuePublication => true;
    public bool CompilerOwnsEnumAuthority => false;
    public bool CompilerOwnsOpcodeAuthority => false;
    public bool CompilerEmissionAllowed => false;
    public bool CompilerHelperAllowed => false;
    public bool TypedFacadeAllowed => false;
    public bool TypedHelperAllowed => false;
    public bool RuntimeExecutable => false;
    public bool HasRuntimeOpcodeMetadata => false;
    public bool HasCanonicalDecoderAcceptance => false;
    public bool HasRegistryFactory => false;
    public bool HasExecutionSemantics => false;
    public bool RequiresSeparateRuntimeIsaWork => true;
    public bool RequiresTileExecutionModel => true;
    public bool RequiresTileDescriptorAbi => true;
    public bool RequiresTypedTileMicroOp => true;
    public bool RequiresVectorLegalityMatrixClosure => true;
    public bool RequiresVlmMaterializationPolicy => true;
    public bool RequiresStagedPublicationRetirePolicy => true;
    public bool RequiresReplayRollbackGoldenEvidence => true;
    public bool RuntimeOwnedLegalityIsFinal => true;
    public bool NoCompilerEnumAuthority => true;
    public bool NoCompilerOpcodeAuthority => true;
    public bool NoRuntimeOpcodeMetadataAsExecution => true;
    public bool NoTypedFacadeHelperEmission => true;
    public bool NoScalarHelperFallback => true;
    public bool NoBaseVectorFallback => true;
    public bool NoLane6DescriptorFallback => true;
    public bool NoLane6StreamFallback => true;
    public bool NoLane7AcceleratorFallback => true;
    public bool NoExternalBackendFallback => true;
    public bool NoVmxSpecificPathFallback => true;
    public bool NoExecutableRowAliasPromotion => true;
    public bool NoHostOwnedEvidencePublication => true;
    public bool IsTileMemory { get; }
    public bool IsLoad { get; }
    public bool IsStore { get; }
    public bool IsMacc { get; }
    public bool IsTranspose { get; }
    public bool RequiresTileMemoryShapeFaultModel { get; }
    public bool RequiresTileMemoryOrderingFaultReplayPolicy { get; }
    public bool RequiresRetireStagedPublication { get; }
    public bool RequiresRetireStagedCommit { get; }
    public bool RequiresAccumulatorTileAbi { get; }
    public bool RequiresAccumulatorPrecisionAbi { get; }
    public bool RequiresAccumulatorResultFootprintAbi { get; }
    public bool RequiresDeterministicOrderingReplayPolicy { get; }
    public bool RequiresTransposeTilePolicyAbi { get; }
    public bool RequiresTileShapePermutationPolicy { get; }
    public bool NoBaseMemoryOpcodeDuplication { get; }
    public bool NoSegmentMemoryFallback { get; }
    public bool NoDescriptorStreamFallback { get; }
    public bool NoScopedVdotWideFallback { get; }
    public bool NoBaseDotProductFallback { get; }
    public bool NoWideningFmaFallback { get; }
    public bool NoDotTileVariantFallback { get; }
    public bool NoVectorTransposeFallback { get; }
    public bool NoStructureMovementFallback { get; }

    public void RequireCompilerEmissionAuthority()
    {
        string requiredDecisions = AbiClass switch
        {
            CompilerMatrixTileOptionalDisabledAbiClass.LoadBoundary =>
                "runtime-owned tile ISA/execution model, tile memory shape/fault ABI, VLM materialization, retire-staged publication, and replay/golden evidence",
            CompilerMatrixTileOptionalDisabledAbiClass.StoreBoundary =>
                "runtime-owned tile ISA/execution model, tile memory shape/fault ABI, VLM materialization, retire-staged commit, and replay/golden evidence",
            CompilerMatrixTileOptionalDisabledAbiClass.MaccBoundary =>
                "runtime-owned tile ISA/execution model, accumulator tile ABI, deterministic ordering, VLM materialization, retire-staged publication, and replay/golden evidence",
            CompilerMatrixTileOptionalDisabledAbiClass.TransposeBoundary =>
                "runtime-owned tile ISA/execution model, transpose tile policy ABI, VLM materialization, retire-staged publication, and replay/golden evidence",
            _ => "required matrix/tile runtime ISA decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until {requiredDecisions} are explicit.");
    }
}
