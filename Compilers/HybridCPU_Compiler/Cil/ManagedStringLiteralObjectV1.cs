using System.Buffers.Binary;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed record ManagedStringLiteralImageRowV1(
    RestrictedCilStringLiteralBindingV1 Binding, string ObjectSymbol, string RootSymbol);

public sealed record ManagedStringLiteralObjectArtifactV1(
    HybridCpuObjectArtifactV1 ObjectArtifact,
    IReadOnlyList<ManagedStringLiteralImageRowV1> Rows,
    string DescriptorSymbol,
    int DescriptorSizeBytes);

public static class ManagedStringLiteralObjectV1
{
    public const string ModuleIdentity = "hybridcpu.managed-runtime.string-literals/v1";
    public const string DescriptorSymbol = "__hybridcpu_managed_string_descriptor";

    public static ManagedStringLiteralObjectArtifactV1 Emit(ManagedStringLiteralPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RestrictedCilStringLiteralBindingV1[] bindings = plan.Bindings.OrderBy(static row => row.LiteralHandle).ToArray();
        if (bindings.Length == 0 || bindings.Select(static row => row.LiteralHandle)
                .Where((handle, index) => handle != checked((ulong)index + 1)).Any() ||
            bindings.Select(static row => row.TypeDescriptor.DescriptorDigest).Distinct(StringComparer.Ordinal).Count() != 1 ||
            bindings.Select(static row => row.TypeHandle).Distinct().Count() != 1)
            throw new ArgumentException("String literal plan must have dense handles and one exact String descriptor.", nameof(plan));
        HybridCpuManagedTypeDescriptorV1 descriptor = bindings[0].TypeDescriptor;
        if (descriptor.Kind != HybridCpuManagedTypeKindV1.String || descriptor.StringShape is not { } shape ||
            shape.LengthOffsetBytes != 16 || shape.DataOffsetBytes != 20 || shape.CharacterSizeBytes != 2 ||
            !shape.IsImmutable || descriptor.InstanceSizeBytes != 20)
            throw new ArgumentException("String literal descriptor does not match the exact runtime String layout.", nameof(plan));

        HybridCpuManagedTypeMetadataArtifactV1 encodedDescriptor = new HybridCpuManagedTypeMetadataEncoderV1().Encode([descriptor]);
        if (encodedDescriptor.Status != HybridCpuManagedTypeMetadataStatusV1.Encoded)
            throw new ArgumentException($"String descriptor rejected by shape-aware metadata encoding: {encodedDescriptor.Reason}", nameof(plan));
        byte[] descriptorBytes = encodedDescriptor.Bytes;
        int tableBytes = checked(HybridCpuManagedStringLiteralTableV1.HeaderSizeBytes +
            bindings.Length * HybridCpuManagedStringLiteralTableV1.RowSizeBytes);
        int cursor = Align(descriptorBytes.Length, 8);
        int tableOffset = cursor;
        cursor = Align(checked(cursor + tableBytes), HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes);
        List<ManagedStringLiteralImageRowV1> rows = new List<ManagedStringLiteralImageRowV1>(bindings.Length);
        int[] objectOffsets = new int[bindings.Length];
        for (int index = 0; index < bindings.Length; index++)
        {
            objectOffsets[index] = cursor;
            cursor = Align(checked(cursor + shape.DataOffsetBytes +
                (bindings[index].Literal.Length + 1) * shape.CharacterSizeBytes), HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes);
        }
        byte[] data = new byte[cursor];
        descriptorBytes.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(tableOffset), HybridCpuManagedStringLiteralTableV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(tableOffset + 4), HybridCpuManagedStringLiteralTableV1.Version);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(tableOffset + HybridCpuManagedStringLiteralTableV1.CountOffset), bindings.Length);
        List<HybridCpuObjectSymbolV1> symbols = new List<HybridCpuObjectSymbolV1>
        {
            new(DescriptorSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcstr", 0, (ulong)descriptorBytes.Length, true),
            new(HybridCpuManagedStringLiteralTableV1.Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".hcstr", checked((ulong)tableOffset), checked((ulong)tableBytes), true)
        };
        List<HybridCpuObjectRelocationV1> relocations = new List<HybridCpuObjectRelocationV1>();
        for (int index = 0; index < bindings.Length; index++)
        {
            RestrictedCilStringLiteralBindingV1 binding = bindings[index];
            int rowOffset = tableOffset + HybridCpuManagedStringLiteralTableV1.HeaderSizeBytes +
                index * HybridCpuManagedStringLiteralTableV1.RowSizeBytes;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(rowOffset), binding.LiteralHandle);
            int objectOffset = objectOffsets[index];
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(objectOffset), binding.TypeHandle);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(objectOffset + shape.LengthOffsetBytes), binding.Literal.Length);
            for (int character = 0; character < binding.Literal.Length; character++)
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(objectOffset + shape.DataOffsetBytes + character * 2), binding.Literal[character]);
            string suffix = binding.LiteralHandle.ToString("x16");
            string objectSymbol = "__hybridcpu_managed_string_literal_" + suffix;
            string rootSymbol = "__hybridcpu_managed_string_literal_root_" + suffix;
            int objectSize = Align(shape.DataOffsetBytes +
                (binding.Literal.Length + 1) * shape.CharacterSizeBytes, HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes);
            symbols.Add(new(objectSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcstr",
                checked((ulong)objectOffset), checked((ulong)objectSize), true));
            symbols.Add(new(rootSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".hcstr",
                checked((ulong)(rowOffset + HybridCpuManagedStringLiteralTableV1.ReferenceOffset)), 8, true));
            relocations.Add(new(".hcstr", checked((ulong)(rowOffset + HybridCpuManagedStringLiteralTableV1.ReferenceOffset)),
                HybridCpuRelocationKind.Absolute64, objectSymbol, 0));
            rows.Add(new(binding, objectSymbol, rootSymbol));
        }
        HybridCpuObjectArtifactV1 artifact = new HybridCpuObjectWriterV1().Write(new(
            [new(".hcstr", HybridCpuObjectSectionKind.ReadOnlyData, 8, data, (ulong)data.Length)], symbols, relocations,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        return new(artifact, rows.AsReadOnly(), DescriptorSymbol, descriptorBytes.Length);
    }

    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);
}
