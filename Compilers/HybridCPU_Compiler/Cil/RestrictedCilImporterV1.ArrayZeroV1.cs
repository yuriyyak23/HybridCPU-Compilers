using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private sealed record ArrayZeroProjection(string ElementIdentity, MetadataStorage Storage);

    // Eliminate only a same-token, immediately consumed array-element byref. Branches
    // into initobj (and all EH bodies, at the caller) prevent this transformation.
    private static IReadOnlyList<DecodedInstruction> ProjectArrayZero(MetadataReader metadata, IReadOnlyList<DecodedInstruction> instructions)
    {
        HashSet<int> targets = instructions.SelectMany(ControlFlowTargets).ToHashSet();
        List<DecodedInstruction> result = new List<DecodedInstruction>();
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction address = instructions[index];
            if (address.Encoding == 0x8f && index + 1 < instructions.Count &&
                instructions[index + 1] is { Encoding: 0xfe15 } zero && zero.Token == address.Token &&
                !targets.Contains(zero.Offset) && TryArrayZeroProjection(metadata, address.Token) is { } proof)
            {
                result.Add(address with { Encoding = 0xfe15, Name = "initobj.array-element", Size = address.Size + zero.Size, ArrayZero = proof });
                index++;
            }
            else result.Add(address);
        }
        return result;
    }

    private static ArrayZeroProjection? TryArrayZeroProjection(MetadataReader metadata, int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.IsNil || handle.Kind != HandleKind.TypeDefinition) return null;
        string? identity = AggregateIdentity(metadata, handle);
        MetadataStorage? storage = new InlineStorageResolver([]).Value(metadata, (TypeDefinitionHandle)handle);
        return identity is not null && storage is { Kind: HybridCpuManagedStorageKindV1.BlittableValue }
            ? new(identity, storage) : null;
    }

    private RestrictedCilArrayTypeBindingV1? ResolveArrayZero(DecodedInstruction instruction)
    {
        if (instruction.ArrayZero is not { } proof || !_arrayBindings.TryGetValue(instruction.Token, out RestrictedCilArrayTypeBindingV1? binding)) return null;
        return binding.TypeDescriptor.StableIdentity == proof.ElementIdentity + "[]" && ValidArrayBinding(binding) &&
            ValidEmptyValueElement(binding, instruction.Token, proof.ElementIdentity, proof.Storage) ? binding : null;
    }
}
