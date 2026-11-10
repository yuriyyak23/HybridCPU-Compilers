using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedFieldAccessKindV1 : byte
{
    Load = 0,
    Store = 1,
    Address = 2
}

public enum HybridCpuManagedFieldLoweringStepKindV1 : byte
{
    ExplicitNullCheckHelperCall = 0,
    AddConstantOffset = 1,
    MaterializeStaticBase = 2,
    Load = 3,
    Store = 4,
    ReturnAddress = 5
}

public enum HybridCpuManagedFieldLoweringStatusV1 : byte
{
    Lowered = 0,
    Unsupported = 1,
    InvalidInput = 2
}

public sealed record HybridCpuManagedFieldLoweringStepV1(
    HybridCpuManagedFieldLoweringStepKindV1 Kind,
    long Immediate,
    string? Symbol,
    int SizeBytes,
    int AlignmentBytes);

public sealed record HybridCpuManagedFieldLoweringPlanV1(
    HybridCpuManagedFieldLoweringStatusV1 Status,
    string Reason,
    string TypeDescriptorDigest,
    string FieldIdentity,
    bool IsStatic,
    HybridCpuManagedStorageKindV1 StorageKind,
    IReadOnlyList<HybridCpuManagedFieldLoweringStepV1> Steps,
    string Digest);

public sealed class HybridCpuManagedFieldLoweringV1
{
    public HybridCpuManagedFieldLoweringPlanV1 Lower(
        HybridCpuManagedTypeDescriptorV1 descriptor,
        string fieldIdentity,
        bool isStatic,
        HybridCpuManagedFieldAccessKindV1 access,
        string? staticStorageSymbol = null)
    {
        if (!ValidDescriptor(descriptor) || string.IsNullOrWhiteSpace(fieldIdentity))
            return Failure(HybridCpuManagedFieldLoweringStatusV1.InvalidInput, descriptor?.DescriptorDigest ?? string.Empty,
                fieldIdentity ?? string.Empty, isStatic, "Type descriptor or field identity is malformed.");
        IReadOnlyList<HybridCpuManagedFieldLayoutV1> fields = isStatic
            ? descriptor.StaticLayout.Fields : descriptor.InstanceFields;
        HybridCpuManagedFieldLayoutV1? field = fields.SingleOrDefault(candidate =>
            string.Equals(candidate.Identity, fieldIdentity, StringComparison.Ordinal));
        if (field is null)
            return Failure(HybridCpuManagedFieldLoweringStatusV1.Unsupported, descriptor.DescriptorDigest,
                fieldIdentity, isStatic, "The runtime-defined layout has no exact field.");
        if (isStatic && string.IsNullOrWhiteSpace(staticStorageSymbol))
            return Failure(HybridCpuManagedFieldLoweringStatusV1.InvalidInput, descriptor.DescriptorDigest,
                fieldIdentity, true, "Static access requires an exact relocatable storage symbol.");

        List<HybridCpuManagedFieldLoweringStepV1> steps = new List<HybridCpuManagedFieldLoweringStepV1>();
        if (isStatic)
            steps.Add(new(HybridCpuManagedFieldLoweringStepKindV1.MaterializeStaticBase, 0,
                staticStorageSymbol, 8, 8));
        else
            steps.Add(new(HybridCpuManagedFieldLoweringStepKindV1.ExplicitNullCheckHelperCall, 0,
                "__hybridcpu_managed_null_check", 8, 8));
        steps.Add(new(HybridCpuManagedFieldLoweringStepKindV1.AddConstantOffset,
            field.OffsetBytes, null, 8, 8));
        steps.Add(new(access switch
        {
            HybridCpuManagedFieldAccessKindV1.Load => HybridCpuManagedFieldLoweringStepKindV1.Load,
            HybridCpuManagedFieldAccessKindV1.Store => HybridCpuManagedFieldLoweringStepKindV1.Store,
            _ => HybridCpuManagedFieldLoweringStepKindV1.ReturnAddress
        }, 0, null, field.SizeBytes, field.AlignmentBytes));
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-field-lowering/v1", HybridCpuManagedAbiFamilyV1.Default.ContractDigest,
            descriptor.DescriptorDigest, fieldIdentity, isStatic, field.StorageKind,
            string.Join(';', steps.Select(static step =>
                $"{step.Kind}:{step.Immediate}:{step.Symbol}:{step.SizeBytes}:{step.AlignmentBytes}"))));
        return new(HybridCpuManagedFieldLoweringStatusV1.Lowered, string.Empty, descriptor.DescriptorDigest,
            fieldIdentity, isStatic, field.StorageKind, steps, digest);
    }

    private static bool ValidDescriptor(HybridCpuManagedTypeDescriptorV1? descriptor) =>
        descriptor is not null && descriptor.SchemaId == HybridCpuManagedTypeDescriptorContractV1.SchemaId &&
        descriptor.SchemaMajor == HybridCpuManagedTypeDescriptorContractV1.SchemaMajor &&
        descriptor.SchemaMinor <= HybridCpuManagedTypeDescriptorContractV1.SchemaMinor &&
        descriptor.DescriptorDigest == HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor);

    private static HybridCpuManagedFieldLoweringPlanV1 Failure(HybridCpuManagedFieldLoweringStatusV1 status,
        string descriptorDigest, string fieldIdentity, bool isStatic, string reason) =>
        new(status, reason, descriptorDigest, fieldIdentity, isStatic, HybridCpuManagedStorageKindV1.Primitive, [],
            HybridCpuPlatformContractV1.Hash($"hybridcpu.managed-field-lowering/failure/v1|{status}|{descriptorDigest}|{fieldIdentity}|{isStatic}|{reason}"));
}
