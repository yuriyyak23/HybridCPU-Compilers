using HybridCPU.Compiler.Core.IR.Contracts;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>Versioned structural projection; no runtime or publication permission.</summary>
public sealed record HybridCpuAgreementSummaryV1(
    int BundleCount, int StructurallyAdmissibleCount, int StructuralFailureCount,
    int TypedSlotValidCount, int TypedSlotInvalidCount, int SafetyMaskConflictCount)
{
    public const string SchemaId = "hybridcpu.agreement-summary/v1";
    public bool RuntimeAuthorityStillRequired => true;
    public string Digest => CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new
    {
        SchemaId, BundleCount, StructurallyAdmissibleCount, StructuralFailureCount,
        TypedSlotValidCount, TypedSlotInvalidCount, SafetyMaskConflictCount,
        RuntimeAuthorityStillRequired
    }));

    public static HybridCpuAgreementSummaryV1 From(IrAdmissibilityAgreement agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        return new(agreement.TotalBundleCount, agreement.AdmissibleBundleCount,
            agreement.StructuralAgreementFailureCount, agreement.TypedSlotValidBundleCount,
            agreement.TypedSlotInvalidBundleCount, agreement.SafetyMaskConflictCount);
    }

    public string FormatDiagnostic() =>
        $"Agreement summary: bundles={BundleCount}, " +
        $"structurally-admissible={StructurallyAdmissibleCount}, " +
        $"agreement failures={StructuralFailureCount}, " +
        $"typed-slot valid={TypedSlotValidCount}/{BundleCount}, " +
        $"typed-slot invalid={TypedSlotInvalidCount}, " +
        $"safety-mask conflicts={SafetyMaskConflictCount}.";
}
