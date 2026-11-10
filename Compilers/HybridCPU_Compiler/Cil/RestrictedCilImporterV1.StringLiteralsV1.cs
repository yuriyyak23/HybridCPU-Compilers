using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private static ManagedStringLiteralPlanV1 CreateStringLiteralPlan(IEnumerable<string> literals,
        ManagedMetadataBindingSet metadata)
    {
        string[] ordered = literals.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        HybridCpuManagedTypeDescriptorV1 descriptor;
        if (!metadata.Descriptors.TryGetValue("System.String", out descriptor!))
        {
            HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                MetadataTypeId("System.String"), "System.String", HybridCpuManagedTypeKindV1.String,
                null, [], 20, 8, [], new(0, 1, [], []), [], [], string.Empty,
                StringShape: new(16, 20, 2, true));
            descriptor = draft with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft) };
        }
        // Preserve the existing metadata handle namespace. Final image registration must
        // reconcile this runtime-owned String shape before these handles can execute.
        HybridCpuManagedTypeDescriptorV1[] types = metadata.Descriptors.Values
            .Append(descriptor)
            .DistinctBy(static type => type.StableIdentity, StringComparer.Ordinal)
            .OrderBy(static type => type.TypeId).ToArray();
        int index = Array.FindIndex(types, static type => type.StableIdentity == "System.String");
        ulong typeHandle = checked((ulong)(index + 1));
        RestrictedCilStringLiteralBindingV1[] bindings = ordered.Select((literal, ordinal) =>
            new RestrictedCilStringLiteralBindingV1(literal, checked((ulong)ordinal + 1), descriptor, typeHandle)).ToArray();
        string ExactUtf16(string value)
        {
            // Encoding.Unicode replaces unpaired surrogates; CIL literals preserve code units.
            byte[] bytes = new byte[checked(value.Length * 2)];
            for (int i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), value[i]);
            return Convert.ToHexString(bytes);
        }
        string digest = Hash("hybridcpu.managed-string-literal-plan/v1|" + descriptor.DescriptorDigest + "|" +
            typeHandle + "|" + string.Join(';', bindings.Select(binding => binding.LiteralHandle + ":" + ExactUtf16(binding.Literal))));
        return new(bindings, digest);
    }
}
