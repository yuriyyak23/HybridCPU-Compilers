using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private static ManagedReceiverAbiPlanV1? ReceiverPlan(MetadataReader metadata, TypeDefinitionHandle owner)
    {
        TypeDefinition type = metadata.GetTypeDefinition(owner);
        if (type.Attributes.HasFlag(TypeAttributes.Interface) || type.BaseType.IsNil ||
            ExactTypeHandleName(metadata, type.BaseType) is not ("System.ValueType" or "System.Enum") ||
            type.GetCustomAttributes().Any(attribute => DescribeMethodReference(metadata,
                MetadataTokens.GetToken(metadata.GetCustomAttribute(attribute).Constructor)) ==
                "System.Runtime.CompilerServices.IsByRefLikeAttribute..ctor")) return null;
        MetadataStorage? payload = new InlineStorageResolver([]).Value(metadata, owner);
        if (payload is null) return null;
        string identity = $"[{metadata.GetString(metadata.GetAssemblyDefinition().Name)}]{NestedTypeName(metadata, owner)}";
        return new(identity, payload.Size, payload.Alignment, 0, true, true, true,
            Hash($"hybridcpu.managed-receiver-loan/v1|{identity}|{payload.Size}|{payload.Alignment}|arg0|caller-nonnull-bounded-storage|required-lifetime|noescape|no-safepoints|reference-free"));
    }

    private static MethodSignature BindValueReceiver(MetadataReader metadata, TypeDefinitionHandle owner, MethodSignature signature)
    {
        if (!signature.HasThis || signature.Status != SignatureStatus.Success || signature.Parameters.Count == 0) return signature;
        TypeDefinition definition = metadata.GetTypeDefinition(owner);
        if (definition.Attributes.HasFlag(TypeAttributes.Interface) || definition.BaseType.IsNil ||
            ExactTypeHandleName(metadata, definition.BaseType) is not ("System.ValueType" or "System.Enum")) return signature;
        string identity = $"[{metadata.GetString(metadata.GetAssemblyDefinition().Name)}]{NestedTypeName(metadata, owner)}";
        RestrictedCilTypeV1[] parameters = signature.Parameters.ToArray();
        parameters[0] = RestrictedCilTypeV1.ManagedByRef;
        string?[] identities = signature.AggregateParameters?.ToArray() ?? new string?[parameters.Length];
        identities[0] = identity + "&";
        return signature with { Parameters = parameters, AggregateParameters = identities, Receiver = ReceiverPlan(metadata, owner) };
    }

    private RestrictedCilImportResultV1? ValidateReceiverBody(MethodSignature signature,
        IReadOnlyList<DecodedInstruction> instructions, RestrictedCilProvenanceV1 provenance)
    {
        if (signature.Receiver is null) return null;
        foreach (DecodedInstruction instruction in instructions)
        {
            ushort op = instruction.Encoding;
            bool leaf = op is <= 0x0e or 0x11 or 0x13 or >= 0x14 and <= 0x23 or 0x25 or 0x26 or
                >= 0x2a and <= 0x45 or >= 0x58 and <= 0x6e or 0x7b or 0x7d or >= 0xfe01 and <= 0xfe05;
            if (!leaf || ControlFlowTargets(instruction).Any(target => target <= instruction.Offset))
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1841",
                    "Receiver loan requires a bounded leaf body without calls, allocation, static/array access, EH or loop safepoints.",
                    OffsetIdentity(provenance, instruction.Offset), provenance);
        }
        return null;
    }

    private FieldResolution ResolveReceiverField(MetadataReader metadata, int token, MethodSignature signature,
        bool store, RestrictedCilProvenanceV1 provenance)
    {
        FieldResolution Fail(string message) => new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1842", message,
            provenance.MethodIdentity, provenance));
        if (signature.Receiver is not { } receiver || MetadataTokens.EntityHandle(token).Kind != HandleKind.FieldDefinition)
            return Fail("Receiver field requires a local exact field definition and typed loan.");
        FieldDefinitionHandle handle = (FieldDefinitionHandle)MetadataTokens.EntityHandle(token);
        FieldDefinition field = metadata.GetFieldDefinition(handle);
        TypeDefinitionHandle owner = field.GetDeclaringType();
        ManagedReceiverAbiPlanV1? expected = ReceiverPlan(metadata, owner);
        if (expected?.PlanDigest != receiver.PlanDigest || (field.Attributes & FieldAttributes.Static) != 0)
            return Fail("Receiver field owner does not match the exact loan identity.");
        if (store && field.Attributes.HasFlag(FieldAttributes.InitOnly) &&
            !provenance.CanonicalMethodLocalIdentity.EndsWith("..ctor", StringComparison.Ordinal))
            return Fail("Readonly payload stores are only admitted in the declaring constructor.");
        FieldResolution resolution = ResolveField(metadata, token, false);
        if (resolution.Failure is not null) return resolution;
        RestrictedCilFieldLayoutBindingV1 binding = resolution.Field!;
        HybridCpuManagedTypeDescriptorV1 descriptor = binding.TypeDescriptor;
        if (!ValidDescriptor(descriptor) || descriptor.Kind != HybridCpuManagedTypeKindV1.ValueType ||
            descriptor.StableIdentity != FullTypeName(metadata, owner) ||
            descriptor.InstanceSizeBytes != receiver.PayloadSizeBytes || descriptor.InstanceAlignmentBytes != receiver.PayloadAlignmentBytes ||
            descriptor.ValueTypeShape is not { ObjectReferenceOffsets.Count: 0 } value ||
            value.PayloadSizeBytes != receiver.PayloadSizeBytes || value.PayloadAlignmentBytes != receiver.PayloadAlignmentBytes)
            return Fail("Receiver descriptor is not the exact reference-free PE payload.");
        InlineStorageResolver resolver = new InlineStorageResolver([]);
        int offset = 0, count = 0;
        foreach (FieldDefinitionHandle candidate in metadata.GetTypeDefinition(owner).GetFields())
        {
            FieldDefinition row = metadata.GetFieldDefinition(candidate);
            if ((row.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) != 0) continue;
            MetadataStorage? storage = resolver.Field(metadata, row.Signature);
            if (storage is null) return Fail("Receiver contains an unqualified payload field.");
            offset = AlignMetadata(offset, storage.Alignment);
            HybridCpuManagedFieldLayoutV1? layout = descriptor.InstanceFields.SingleOrDefault(f => f.Identity == metadata.GetString(row.Name));
            if (layout is null || layout.OffsetBytes != offset || layout.SizeBytes != storage.Size ||
                layout.AlignmentBytes != storage.Alignment || layout.StorageKind != storage.Kind ||
                offset > receiver.PayloadSizeBytes - storage.Size)
                return Fail("Receiver field offset/extent differs from the exact PE layout.");
            offset = checked(offset + storage.Size); count++;
        }
        if (count != descriptor.InstanceFields.Count) return Fail("Receiver descriptor contains extra fields.");
        RestrictedCilTypeV1 carrier = ParseMetadataFieldType(metadata, field.Signature);
        if (!TryStorage(carrier, out HybridCpuManagedStorageKindV1 kind, out _, out _) || kind != HybridCpuManagedStorageKindV1.Primitive)
            return Fail("Receiver field requires scalar primitive load/store lowering.");
        return new(binding with { FieldType = carrier }, null);
    }

    private static bool ValidReceiverUse(V2Value value, MethodSignature signature) =>
        value.Type == RestrictedCilTypeV1.ManagedByRef && signature.Receiver is { } receiver &&
        value.ReceiverIdentity == receiver.ScopedTypeIdentity;

    private IReadOnlyDictionary<int, ManagedReceiverCallerStoragePlanV1>? ReceiverLocalStoragePlans(
        MetadataReader metadata, IReadOnlyList<DecodedInstruction> instructions, LocalSignature locals)
    {
        int[] aggregateLocals = locals.Types.Select((type, index) => (type, index))
            .Where(static item => item.type == RestrictedCilTypeV1.Aggregate)
            .Select(static item => item.index).ToArray();
        if (aggregateLocals.Length == 0) return new Dictionary<int, ManagedReceiverCallerStoragePlanV1>();
        if (aggregateLocals.Length != 1)
            return new Dictionary<int, ManagedReceiverCallerStoragePlanV1>(); // general aggregates retain HCCIL1810
        int local = aggregateLocals[0];
        string? identity = locals.Aggregates?.ElementAtOrDefault(local);
        TypeDefinitionHandle owner = metadata.TypeDefinitions.SingleOrDefault(handle =>
            string.Equals($"[{metadata.GetString(metadata.GetAssemblyDefinition().Name)}]{NestedTypeName(metadata, handle)}",
                identity, StringComparison.Ordinal));
        ManagedReceiverAbiPlanV1? receiver = owner.IsNil ? null : ReceiverPlan(metadata, owner);
        if (receiver is null && identity is not null && _externalScalarValues.TryGetValue(identity, out ScalarValueDeclaration? declaration) &&
            declaration.Carrier is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64) {
            ManagedReceiverAbiPlanV1[] candidates = _managedCallTargets.Values.Select(target => target.Receiver)
                .Where(plan => plan is not null && plan.ScopedTypeIdentity == identity &&
                    plan.PayloadSizeBytes == (declaration.Carrier == RestrictedCilTypeV1.Int64 ? 8 : 4) &&
                    plan.PayloadAlignmentBytes == (declaration.Carrier == RestrictedCilTypeV1.Int64 ? 8 : 4)).Cast<ManagedReceiverAbiPlanV1>()
                .Distinct().ToArray();
            if (candidates.Length == 1) receiver = candidates[0];
        }
        if (receiver is null || receiver.PayloadSizeBytes > 16) return null;

        bool addressed = false;
        bool otherUse = false;
        foreach (DecodedInstruction instruction in instructions)
        {
            int? accessed = instruction.Encoding switch
            {
                >= 0x06 and <= 0x09 => instruction.Encoding - 0x06,
                >= 0x0a and <= 0x0d => instruction.Encoding - 0x0a,
                0x11 or 0x12 or 0x13 => checked((int)instruction.Literal),
                _ => null
            };
            if (accessed != local) continue;
            if (instruction.Encoding == 0x12) addressed = true;
            else otherUse = true;
        }
        if (!addressed) return new Dictionary<int, ManagedReceiverCallerStoragePlanV1>();
        if (otherUse) return null;
        string slot = $"receiver-local:{local}:{Hash(identity!)[..16]}";
        string proof = Hash(string.Join('|', "hybridcpu.receiver-caller-storage/v1", local, identity,
            receiver.PayloadSizeBytes, receiver.PayloadAlignmentBytes, slot, receiver.PlanDigest,
            string.Join(',', instructions.Where(row => row.Encoding == 0x12 && checked((int)row.Literal) == local)
                .Select(static row => row.Offset))));
        return new Dictionary<int, ManagedReceiverCallerStoragePlanV1>
        {
            [local] = new(local, identity!, receiver.PayloadSizeBytes, receiver.PayloadAlignmentBytes,
                slot, [], proof)
        };
    }

    private static HybridCpuManagedCallLayoutV1 ClassifyReceiverCall(
        ManagedReceiverCallerStoragePlanV1 storage, ManagedReceiverAbiPlanV1 receiver,
        RestrictedCilHelperContractV1 contract)
    {
        HybridCpuManagedValueV1 Value(RestrictedCilTypeV1 type, int index)
        {
            if (type == RestrictedCilTypeV1.ManagedByRef)
                return new(receiver.ScopedTypeIdentity + "&", HybridCpuManagedValueKindV1.ManagedByRef, 8, 8);
            if (type == RestrictedCilTypeV1.ObjectReference)
                return new($"arg:{index}", HybridCpuManagedValueKindV1.ObjectReference, 8, 8);
            int size = type switch
            {
                RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.UInt8 => 1,
                RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16 => 2,
                RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 => 4,
                RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                    RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt => 8,
                _ => 0
            };
            return new($"arg:{index}", size == 0 ? HybridCpuManagedValueKindV1.Unknown :
                HybridCpuManagedValueKindV1.PrimitiveInteger, size, size == 0 ? 1 : size);
        }
        HybridCpuManagedValueV1? result = contract.ReturnType == RestrictedCilTypeV1.Void
            ? null : Value(contract.ReturnType, -1) with { Identity = "result" };
        HybridCpuManagedCallSignatureV1 signature = new HybridCpuManagedCallSignatureV1(HybridCpuManagedCallDirectionV1.ManagedToManaged,
            contract.ParameterTypes.Select(Value).ToArray(), result);
        return HybridCpuManagedAbiFamilyV1.Default.ClassifyReceiverLoan(new(signature, receiver.ArgumentIndex,
            receiver.ScopedTypeIdentity, receiver.PayloadSizeBytes, receiver.PayloadAlignmentBytes,
            storage.PayloadSizeBytes, storage.PayloadAlignmentBytes, true, true,
            receiver.NonEscaping, receiver.NoSafepoints, true, receiver.PlanDigest, storage.StorageProofDigest));
    }

    private static void EmitReceiverField(List<Emission> emissions, DecodedInstruction source, string identity,
        RestrictedCilFieldLayoutBindingV1 field, V2Value receiver, V2Value? value, V2Value? result)
    {
        HybridCpuManagedFieldLayoutV1 layout = field.TypeDescriptor.InstanceFields.Single(f => f.Identity == field.FieldName);
        V2Value address = V2Value.Definition(RestrictedCilTypeV1.ManagedByRef, identity + ":receiver-field-address");
        emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.ManagedByRef,
            [receiver.Operand, new(IrOperandKind.Constant, (ulong)layout.OffsetBytes, identity + ":payload-offset")],
            [address.Operand], -1, identity + ":receiver-field-address"));
        HybridCpuOpcode opcode = value is null ? layout.SizeBytes switch
        {
            1 => IsSigned(field.FieldType) ? HybridCpuOpcode.LB : HybridCpuOpcode.LBU,
            2 => IsSigned(field.FieldType) ? HybridCpuOpcode.LH : HybridCpuOpcode.LHU,
            4 => IsSigned(field.FieldType) ? HybridCpuOpcode.LW : HybridCpuOpcode.LWU,
            8 => HybridCpuOpcode.LD,
            _ => throw new InvalidOperationException("Invalid receiver load width.")
        } : layout.SizeBytes switch
        {
            1 => HybridCpuOpcode.SB, 2 => HybridCpuOpcode.SH, 4 => HybridCpuOpcode.SW, 8 => HybridCpuOpcode.SD,
            _ => throw new InvalidOperationException("Invalid receiver store width.")
        };
        if (value?.Operand.Kind == IrOperandKind.Constant)
        {
            V2Value materialized = V2Value.Definition(value.Type, identity + ":receiver-store-value");
            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, value.Type,
                [new(IrOperandKind.ArchitecturalRegister, 0, identity + ":zero"), value.Operand], [materialized.Operand], -1,
                identity + ":receiver-store-value"));
            value = materialized;
        }
        emissions.Add(new(source.Offset, opcode, StackType(field.FieldType), value is null ? [address.Operand] : [address.Operand, value.Operand],
            result is null ? [] : [result.Operand], -1, identity, null, value is null ? IrMemoryEffectKind.Read : IrMemoryEffectKind.Write));
    }
}
