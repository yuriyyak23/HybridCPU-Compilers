using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // Granted only to a primary non-root BodyWorld child, never by metadata discovery.
    private bool _exactStructOwnerIngress;
    // Set only after the graph-owned scalar definition/purity/typed proof succeeds.
    private bool _exactStructScalarMethodIngress;

    private static bool IsExactStructScalarMethod(MetadataReader metadata, MethodDefinition method,
        IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments, IReadOnlyList<RestrictedCilGenericArgumentV1> methodArguments)
    {
        if (!IsExactSignedOwnerContext(metadata, metadata.GetTypeDefinition(method.GetDeclaringType()), typeArguments) ||
            methodArguments.Count != 1 || method.GetGenericParameters().Count != 1 ||
            methodArguments[0] != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32) &&
                methodArguments[0] != new RestrictedCilGenericArgumentV1("System.Int64", RestrictedCilTypeV1.Int64) ||
            (method.Attributes & (MethodAttributes.MemberAccessMask | MethodAttributes.Static)) != (MethodAttributes.Private | MethodAttributes.Static) ||
            !IsExactStructConstraint(metadata, method.GetGenericParameters().Single())) return false;
        // Exact canonical !!0 by-value operands and return within the existing
        // register ABI. This is not primitive substitution, varargs/byref or
        // additional generic arity/constraint solving. Final typed/body proof
        // and the caller's original argument/stack/call budgets remain mandatory.
        ReadOnlySpan<byte> signature = metadata.GetBlobBytes(method.Signature);
        if (signature.Length < 5 || signature[0] != 0x10 || signature[1] != 1 || signature[2] == 0 ||
            signature[2] > HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Count ||
            signature.Length != 5 + signature[2] * 2 || signature[3] != 0x1e || signature[4] != 0) return false;
        for (int index = 5; index < signature.Length; index += 2)
            if (signature[index] != 0x1e || signature[index + 1] != 0) return false;
        return true;
    }

    private static bool IsExactStructConstraint(MetadataReader metadata, GenericParameterHandle handle)
    {
        GenericParameter parameter = metadata.GetGenericParameter(handle);
        if (parameter.Index != 0 || parameter.Attributes != (GenericParameterAttributes.NotNullableValueTypeConstraint |
                GenericParameterAttributes.DefaultConstructorConstraint) || parameter.GetConstraints().Count != 1) return false;
        EntityHandle constraint = metadata.GetGenericParameterConstraint(parameter.GetConstraints().Single()).Type;
        return constraint.Kind == HandleKind.TypeReference && ExactTypeHandleName(metadata, constraint) == "System.ValueType" &&
            ReferencedAssemblyName(metadata, (TypeReferenceHandle)constraint) is "System.Private.CoreLib" or "System.Runtime" or "mscorlib";
    }

    private static bool IsExactSignedOwnerContext(MetadataReader metadata, TypeDefinition owner,
        IReadOnlyList<RestrictedCilGenericArgumentV1> arguments, bool requireStructConstraint = false)
    {
        GenericParameterHandleCollection parameters = owner.GetGenericParameters();
        if (parameters.Count != arguments.Count || arguments.Count > 1 || requireStructConstraint && arguments.Count != 1 ||
            arguments.Any(argument => argument != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32) &&
                argument != new RestrictedCilGenericArgumentV1("System.Int64", RestrictedCilTypeV1.Int64))) return false;
        return parameters.All(handle => IsExactStructConstraint(metadata, handle) || !requireStructConstraint &&
            metadata.GetGenericParameter(handle).Index == 0 && metadata.GetGenericParameter(handle).Attributes == GenericParameterAttributes.None &&
            metadata.GetGenericParameter(handle).GetConstraints().Count == 0);
    }

    private sealed record ManagedMetadataBindingSet(
        IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1> Descriptors,
        IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1> Fields,
        IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1> TypeInitializers,
        IReadOnlyDictionary<string, IReadOnlyList<RestrictedCilAllocationBindingV1>> AllocationsByAssembly,
        IReadOnlyDictionary<string, IReadOnlyList<RestrictedCilArrayTypeBindingV1>> ArraysByAssembly,
        IReadOnlyDictionary<string, IReadOnlyList<RestrictedCilTypeTestBindingV1>> TypeTestsByAssembly,
        IReadOnlyDictionary<string, IReadOnlyList<RestrictedCilFieldDataBindingV1>> FieldDataByAssembly,
        IReadOnlyList<ManagedClosedInterfacePlanV1> ClosedInterfaces);

    private static ManagedMetadataBindingSet BuildMetadataBindings(IReadOnlyList<ManagedModuleContext> modules,
        IReadOnlyList<ManagedModuleContext>? metadataOnly = null)
    {
        ManagedModuleContext[] resolutionModules = [.. modules, .. metadataOnly ?? []];
        HashSet<string> ambiguousOwners = resolutionModules.SelectMany(module => module.Metadata.TypeDefinitions
            .Select(handle => FullTypeName(module.Metadata, handle))).GroupBy(static identity => identity, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1).Select(static group => group.Key).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, HybridCpuManagedTypeDescriptorV1> descriptors = new Dictionary<string, HybridCpuManagedTypeDescriptorV1>(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<(string Name, bool Static, RestrictedCilTypeV1 Carrier, ManagedModuleContext Module, BlobHandle Signature)>> logicalFields = new Dictionary<string, IReadOnlyList<(string Name, bool Static, RestrictedCilTypeV1 Carrier,
            ManagedModuleContext Module, BlobHandle Signature)>>(StringComparer.Ordinal);
        ClosedInterfaceResolver closedInterfaces = new ClosedInterfaceResolver(resolutionModules);
        HashSet<string> building = new HashSet<string>(StringComparer.Ordinal);
        ManagedStorageResolver storageResolver = new ManagedStorageResolver(resolutionModules);

        HybridCpuManagedTypeDescriptorV1? Build(ManagedModuleContext module, TypeDefinitionHandle handle)
        {
            MetadataReader metadata = module.Metadata;
            string identity = FullTypeName(metadata, handle);
            // Legacy field/cctor keys are unscoped. Never bind one ambiguous owner to
            // another module's descriptor; constructed interface/argument IDs are scoped.
            if (ambiguousOwners.Contains(identity)) return null;
            if (descriptors.TryGetValue(identity, out HybridCpuManagedTypeDescriptorV1? existing)) return existing;
            if (!building.Add(identity)) return null;
            try
            {
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                // Open definitions are not runtime types. Constructed interface declarations
                // are created by the separate exact TypeSpec resolver below.
                if (type.GetGenericParameters().Count != 0) return null;
                bool isInterface = type.Attributes.HasFlag(TypeAttributes.Interface);
                string baseIdentity = type.BaseType.IsNil ? string.Empty : ExactTypeHandleName(metadata, type.BaseType);
                bool isEnum = baseIdentity == "System.Enum";
                bool isValueType = isEnum || baseIdentity == "System.ValueType";
                if (isValueType && ((!isEnum && (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout) ||
                    !type.GetLayout().IsDefault || type.GetCustomAttributes().Any(attribute => DescribeMethodReference(metadata,
                        MetadataTokens.GetToken(metadata.GetCustomAttribute(attribute).Constructor)) ==
                        "System.Runtime.CompilerServices.IsByRefLikeAttribute..ctor"))) return null;
                HybridCpuManagedTypeDescriptorV1? baseDescriptor = null;
                if (!string.IsNullOrEmpty(baseIdentity) &&
                    baseIdentity is not ("System.Object" or "System.ValueType" or "System.Enum"))
                {
                    if (!TryResolveType(resolutionModules, module, type.BaseType, out ManagedModuleContext baseModule,
                            out TypeDefinitionHandle baseHandle))
                        return null;
                    baseDescriptor = Build(baseModule, baseHandle);
                    if (baseDescriptor is null) return null;
                }

                List<(string Identity, ManagedFieldStorage Storage, bool Static, int Ordinal)> declarations = new List<(string Identity, ManagedFieldStorage Storage, bool Static, int Ordinal)>();
                List<(string Name, bool Static, RestrictedCilTypeV1 Carrier, ManagedModuleContext Module, BlobHandle Signature)> logical = new List<(string Name, bool Static, RestrictedCilTypeV1 Carrier,
                    ManagedModuleContext Module, BlobHandle Signature)>();
                int ordinal = 0;
                foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                {
                    FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                    if (field.Attributes.HasFlag(FieldAttributes.Literal)) { ordinal++; continue; }
                    ManagedFieldStorage? storage = storageResolver.Field(module, field.Signature);
                    if (storage is null) return null;
                    declarations.Add((metadata.GetString(field.Name), storage,
                        field.Attributes.HasFlag(FieldAttributes.Static), ordinal++));
                    logical.Add((metadata.GetString(field.Name), field.Attributes.HasFlag(FieldAttributes.Static),
                        storage.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference ? RestrictedCilTypeV1.ObjectReference :
                        storage.StorageKind == HybridCpuManagedStorageKindV1.Primitive ? ParseMetadataFieldType(metadata, field.Signature) :
                        RestrictedCilTypeV1.UnsupportedManaged, module, field.Signature));
                }

                int instanceOffset = isValueType ? 0 : baseDescriptor?.InstanceSizeBytes ??
                    HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes;
                int instanceAlignment = isValueType ? 1 : HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes;
                int staticOffset = 0;
                int staticAlignment = 1;
                List<HybridCpuManagedFieldLayoutV1> instanceFields = new List<HybridCpuManagedFieldLayoutV1>();
                List<HybridCpuManagedFieldLayoutV1> staticFields = new List<HybridCpuManagedFieldLayoutV1>();
                foreach ((string Identity, ManagedFieldStorage Storage, bool Static, int Ordinal) declaration in declarations.OrderBy(static row => row.Ordinal))
                {
                    ManagedFieldStorage storage = declaration.Storage;
                    int size = storage.Size, alignment = storage.Alignment;
                    if (declaration.Static)
                    {
                        staticOffset = AlignMetadata(instance: staticOffset, alignment);
                        AppendPhysicalStorage(staticFields, declaration.Identity, storage, staticOffset, declaration.Ordinal);
                        staticOffset = checked(staticOffset + size);
                        staticAlignment = Math.Max(staticAlignment, alignment);
                    }
                    else
                    {
                        instanceOffset = AlignMetadata(instanceOffset, alignment);
                        AppendPhysicalStorage(instanceFields, declaration.Identity, storage, instanceOffset, declaration.Ordinal);
                        instanceOffset = checked(instanceOffset + size);
                        instanceAlignment = Math.Max(instanceAlignment, alignment);
                    }
                }
                if (isInterface && (instanceFields.Count != 0 || staticFields.Count != 0)) return null;
                if (instanceFields.Count + staticFields.Count > HybridCpuPlatformContractV1.MaximumManagedFieldsPerType ||
                    instanceFields.Concat(staticFields).Select(field => field.Identity).Distinct(StringComparer.Ordinal).Count() != instanceFields.Count + staticFields.Count)
                    return null;

                ulong[] interfaceIds = type.GetInterfaceImplementations().Select(implementation =>
                {
                    EntityHandle interfaceHandle = metadata.GetInterfaceImplementation(implementation).Interface;
                    if (interfaceHandle.Kind == HandleKind.TypeSpecification)
                        return closedInterfaces.Resolve(module, interfaceHandle)?.TypeId ?? 0;
                    if (!TryResolveType(resolutionModules, module, interfaceHandle, out ManagedModuleContext interfaceModule,
                            out TypeDefinitionHandle interfaceType)) return 0UL;
                    return Build(interfaceModule, interfaceType)?.TypeId ?? 0;
                }).ToArray();
                if (interfaceIds.Contains(0UL)) return null;

                int finalInstanceSize = isInterface ? 0 : AlignMetadata(isValueType ? Math.Max(1, instanceOffset) : instanceOffset, Math.Max(1, instanceAlignment));
                HybridCpuManagedValueTypeShapeV1? valueShape = isValueType
                    ? new(finalInstanceSize, Math.Max(1, instanceAlignment),
                        instanceFields.Where(static field => field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                            .Select(static field => field.OffsetBytes).ToArray(),
                        HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes)
                    : null;
                ulong typeId = MetadataTypeId(identity);
                HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                    HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                    HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                    HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                    typeId, identity,
                    isInterface ? HybridCpuManagedTypeKindV1.Interface :
                    isValueType ? HybridCpuManagedTypeKindV1.ValueType : HybridCpuManagedTypeKindV1.Class,
                    baseDescriptor?.TypeId, interfaceIds.Order().ToArray(), finalInstanceSize,
                    Math.Max(1, instanceAlignment), instanceFields,
                    new(AlignMetadata(staticOffset, Math.Max(1, staticAlignment)), Math.Max(1, staticAlignment),
                        staticFields,
                        staticFields.Where(static field => field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                            .Select(static field => field.OffsetBytes).ToArray()),
                    [], [], string.Empty, ValueTypeShape: valueShape);
                HybridCpuManagedTypeDescriptorV1 descriptor = draft with
                {
                    DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft)
                };
                descriptors.Add(identity, descriptor);
                logicalFields.Add(identity, logical);
                return descriptor;
            }
            catch (Exception exception) when (exception is BadImageFormatException or ArgumentOutOfRangeException or
                                                   OverflowException)
            {
                return null;
            }
            finally
            {
                building.Remove(identity);
            }
        }

        foreach (ManagedModuleContext module in modules.OrderBy(static row => row.AssemblyName, StringComparer.Ordinal))
            foreach (TypeDefinitionHandle type in module.Metadata.TypeDefinitions)
                Build(module, type);
        ManagedModuleContext? coreLib = resolutionModules.SingleOrDefault(static module => module.AssemblyName == "System.Private.CoreLib");
        if (coreLib is not null)
            foreach (TypeDefinitionHandle type in coreLib.Metadata.TypeDefinitions.Where(handle =>
                          FullTypeName(coreLib.Metadata, handle) is "System.ArgumentNullException" or
                              "System.ArgumentOutOfRangeException" or "System.NullReferenceException" or
                              "System.IndexOutOfRangeException" or "System.OverflowException" or
                              "System.OutOfMemoryException" or "System.DivideByZeroException" or "System.InvalidOperationException"))
                Build(coreLib, type); // Recursively binds the exact CoreLib exception base layout.
        if (coreLib is not null)
            foreach (TypeDefinitionHandle type in coreLib.Metadata.TypeDefinitions.Where(handle =>
                         FullTypeName(coreLib.Metadata, handle) == "System.ArrayTypeMismatchException"))
                Build(coreLib, type); // Recursively binds the exact CoreLib exception base layout.
        TypeDefinitionHandle coreLibStringType = coreLib is null ? default : coreLib.Metadata.TypeDefinitions.SingleOrDefault(handle =>
            FullTypeName(coreLib.Metadata, handle) == "System.String");
        bool exactCoreLibString = coreLib is not null && !coreLibStringType.IsNil;
        bool exactCoreLibStringEmpty = exactCoreLibString && coreLib!.Metadata.GetTypeDefinition(coreLibStringType).GetFields()
            .Where(handle => coreLib.Metadata.GetString(coreLib.Metadata.GetFieldDefinition(handle).Name) == "Empty")
            .Select(handle => coreLib.Metadata.GetFieldDefinition(handle)).SingleOrDefault() is FieldDefinition emptyField &&
            emptyField.Attributes.HasFlag(FieldAttributes.Static) && emptyField.Attributes.HasFlag(FieldAttributes.InitOnly) &&
            coreLib.Metadata.GetBlobBytes(emptyField.Signature).AsSpan().SequenceEqual(new byte[] { 0x06, 0x0e }) &&
            !coreLib.Metadata.GetTypeDefinition(coreLibStringType).GetMethods().Any(handle =>
                coreLib.Metadata.GetString(coreLib.Metadata.GetMethodDefinition(handle).Name) == ".cctor");
        if (exactCoreLibString && !descriptors.ContainsKey("System.String"))
        {
            HybridCpuManagedStaticLayoutV1 staticLayout = exactCoreLibStringEmpty
                ? new(8, 8, [new("Empty", HybridCpuManagedStorageKindV1.ObjectReference, 0, 8, 8)], [0])
                : new(0, 1, [], []);
            HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                MetadataTypeId("System.String"), "System.String", HybridCpuManagedTypeKindV1.String,
                null, [], 20, 8, [], staticLayout, [], [], string.Empty,
                StringShape: new(16, 20, 2, true));
            descriptors.Add("System.String", draft with
            { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft) });
        }
        foreach (HybridCpuManagedTypeDescriptorV1 descriptor in closedInterfaces.Descriptors.Values)
            descriptors.Add(descriptor.StableIdentity, descriptor);

        Dictionary<(string Assembly, int ElementToken), (HybridCpuManagedTypeDescriptorV1 Array, HybridCpuManagedTypeDescriptorV1? Element)> referenceArrays = new Dictionary<(string Assembly, int ElementToken),
            (HybridCpuManagedTypeDescriptorV1 Array, HybridCpuManagedTypeDescriptorV1? Element)>();
        foreach (ManagedModuleContext module in modules.OrderBy(static row => row.AssemblyName, StringComparer.Ordinal))
        {
            foreach (TypeDefinitionHandle typeHandle in module.Metadata.TypeDefinitions)
            {
                string elementIdentity = FullTypeName(module.Metadata, typeHandle);
                if (!descriptors.TryGetValue(elementIdentity, out HybridCpuManagedTypeDescriptorV1? element))
                    continue;
                TypeDefinition definition = module.Metadata.GetTypeDefinition(typeHandle);
                bool scalarEnum = !definition.BaseType.IsNil &&
                    ExactTypeHandleName(module.Metadata, definition.BaseType) == "System.Enum" &&
                    element.Kind == HybridCpuManagedTypeKindV1.ValueType &&
                    element.ValueTypeShape is { ObjectReferenceOffsets.Count: 0 } &&
                    element.InstanceFields is [{ Identity: "value__", OffsetBytes: 0,
                        StorageKind: HybridCpuManagedStorageKindV1.Primitive, SizeBytes: 1 or 2 or 4 or 8 }];
                if (!scalarEnum && element.Kind is not (HybridCpuManagedTypeKindV1.Class or HybridCpuManagedTypeKindV1.Interface))
                    continue;
                // Enums use scalar array helpers but retain a distinct nominal array TypeId.
                HybridCpuManagedArrayShapeV1 shape = scalarEnum
                    ? new HybridCpuManagedArrayShapeV1(HybridCpuManagedStorageKindV1.Primitive, null,
                        element.ValueTypeShape!.PayloadSizeBytes, element.ValueTypeShape.PayloadAlignmentBytes, 16, 24, true, false)
                    : new HybridCpuManagedArrayShapeV1(HybridCpuManagedStorageKindV1.ObjectReference,
                        element.TypeId, 8, 8, 16, 24, true, true);
                string arrayIdentity = elementIdentity + "[]";
                if (!descriptors.TryGetValue(arrayIdentity, out HybridCpuManagedTypeDescriptorV1? array))
                {
                    HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                        HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                        HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                        HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                        MetadataTypeId(arrayIdentity), arrayIdentity, HybridCpuManagedTypeKindV1.SzArray,
                        null, [], HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes,
                        HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes, [], new(0, 1, [], []), [], [], string.Empty,
                        ArrayShape: shape);
                    array = draft with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft) };
                    descriptors.Add(arrayIdentity, array);
                }
                referenceArrays.Add((module.AssemblyName, MetadataTokens.GetToken(typeHandle)), (array, element));
            }
            foreach (TypeReferenceHandle typeHandle in module.Metadata.TypeReferences)
            {
                string identity = TypeReferenceName(module.Metadata, typeHandle);
                if (identity == "System.String" && descriptors.TryGetValue(identity, out HybridCpuManagedTypeDescriptorV1? stringElement) &&
                    TryResolveType(resolutionModules, module, typeHandle, out ManagedModuleContext stringModule,
                        out TypeDefinitionHandle stringType) && stringModule.AssemblyName == "System.Private.CoreLib" &&
                    FullTypeName(stringModule.Metadata, stringType) == identity)
                {
                    string stringArrayIdentity = identity + "[]";
                    if (!descriptors.TryGetValue(stringArrayIdentity, out HybridCpuManagedTypeDescriptorV1? stringArray))
                    {
                        HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                            HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                            HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                            HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                            MetadataTypeId(stringArrayIdentity), stringArrayIdentity, HybridCpuManagedTypeKindV1.SzArray,
                            null, [], HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes,
                            HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes, [], new(0, 1, [], []), [], [], string.Empty,
                            ArrayShape: new(HybridCpuManagedStorageKindV1.ObjectReference, stringElement.TypeId,
                                8, 8, 16, 24, true, true));
                        stringArray = draft with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft) };
                        descriptors.Add(stringArrayIdentity, stringArray);
                    }
                    referenceArrays.Add((module.AssemblyName, MetadataTokens.GetToken(typeHandle)), (stringArray, stringElement));
                    continue;
                }
                (int Size, int Alignment)? primitive = identity switch
                {
                    "System.Boolean" or "System.SByte" or "System.Byte" => (1, 1),
                    "System.Char" or "System.Int16" or "System.UInt16" => (2, 2),
                    "System.Int32" or "System.UInt32" => (4, 4),
                    "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr" => (8, 8),
                    _ => null
                };
                if (primitive is null || !TryResolveType(resolutionModules, module, typeHandle,
                        out ManagedModuleContext primitiveModule, out TypeDefinitionHandle primitiveType) ||
                    primitiveModule.AssemblyName != "System.Private.CoreLib" ||
                    FullTypeName(primitiveModule.Metadata, primitiveType) != identity)
                    continue;
                string arrayIdentity = identity + "[]";
                if (!descriptors.TryGetValue(arrayIdentity, out HybridCpuManagedTypeDescriptorV1? array))
                {
                    HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                        HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                        HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                        HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                        MetadataTypeId(arrayIdentity), arrayIdentity, HybridCpuManagedTypeKindV1.SzArray,
                        null, [], HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes,
                        HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes, [], new(0, 1, [], []), [], [], string.Empty,
                        ArrayShape: new(HybridCpuManagedStorageKindV1.Primitive, null, primitive.Value.Size,
                            primitive.Value.Alignment, 16, 24, true, false));
                    array = draft with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft) };
                    descriptors.Add(arrayIdentity, array);
                }
                referenceArrays.Add((module.AssemblyName, MetadataTokens.GetToken(typeHandle)), (array, null));
            }
            for (int typeRow = 1; typeRow <= module.Metadata.GetTableRowCount(TableIndex.TypeSpec); typeRow++)
            {
                TypeSpecificationHandle typeHandle = MetadataTokens.TypeSpecificationHandle(typeRow);
                byte[] signature = module.Metadata.GetBlobBytes(module.Metadata.GetTypeSpecification(typeHandle).Signature);
                string? primitiveIdentity = signature.AsSpan() switch
                {
                    [0x1d, 0x02] => "System.Boolean",
                    [0x1d, 0x03] => "System.Char",
                    [0x1d, 0x04] => "System.SByte",
                    [0x1d, 0x05] => "System.Byte",
                    [0x1d, 0x06] => "System.Int16",
                    [0x1d, 0x07] => "System.UInt16",
                    [0x1d, 0x08] => "System.Int32",
                    [0x1d, 0x09] => "System.UInt32",
                    [0x1d, 0x0a] => "System.Int64",
                    [0x1d, 0x0b] => "System.UInt64",
                    [0x1d, 0x18] => "System.IntPtr",
                    [0x1d, 0x19] => "System.UIntPtr",
                    _ => null
                };
                HybridCpuManagedTypeDescriptorV1? element = null;
                if (primitiveIdentity is not null)
                    descriptors.TryGetValue(primitiveIdentity + "[]", out element);
                else if (signature.Length >= 3 && signature[0] == 0x1d && signature[1] == 0x12)
                {
                    // SZARRAY CLASS TypeDefOrRef: bind through the scoped metadata token,
                    // never through an unverified display name or an aggregate guess.
                    BlobReader blob = module.Metadata.GetBlobReader(module.Metadata.GetTypeSpecification(typeHandle).Signature);
                    blob.ReadByte(); blob.ReadByte();
                    EntityHandle classHandle = blob.ReadTypeHandle();
                    if (blob.RemainingBytes == 0 &&
                        TryResolveType(resolutionModules, module, classHandle, out ManagedModuleContext? classModule, out TypeDefinitionHandle classType) &&
                        referenceArrays.TryGetValue((classModule.AssemblyName, MetadataTokens.GetToken(classType)), out (HybridCpuManagedTypeDescriptorV1 Array, HybridCpuManagedTypeDescriptorV1? Element) inner) &&
                        inner.Element?.Kind is HybridCpuManagedTypeKindV1.Class or HybridCpuManagedTypeKindV1.Interface)
                        element = inner.Array;
                }
                if (element is null) continue;
                string arrayIdentity = element.StableIdentity + "[]";
                if (!descriptors.TryGetValue(arrayIdentity, out HybridCpuManagedTypeDescriptorV1? array))
                {
                    HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                        HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                        HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                        HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                        MetadataTypeId(arrayIdentity), arrayIdentity, HybridCpuManagedTypeKindV1.SzArray,
                        null, [], HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes,
                        HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes, [], new(0, 1, [], []), [], [], string.Empty,
                        ArrayShape: new(HybridCpuManagedStorageKindV1.ObjectReference, element.TypeId, 8, 8, 16, 24, true, true));
                    array = draft with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft) };
                    descriptors.Add(arrayIdentity, array);
                }
                referenceArrays.Add((module.AssemblyName, MetadataTokens.GetToken(typeHandle)), (array, element));
            }
        }

        HybridCpuManagedTypeDescriptorV1[] ordered = descriptors.Values.OrderBy(static row => row.TypeId).ToArray();
        Dictionary<string, ulong> handles = ordered.Select((descriptor, index) => (descriptor.StableIdentity, Handle: checked((ulong)(index + 1))))
            .ToDictionary(static row => row.StableIdentity, static row => row.Handle, StringComparer.Ordinal);
        Dictionary<string, RestrictedCilFieldLayoutBindingV1> fields = new Dictionary<string, RestrictedCilFieldLayoutBindingV1>(StringComparer.Ordinal);
        Dictionary<string, RestrictedCilTypeInitializationBindingV1> initializers = new Dictionary<string, RestrictedCilTypeInitializationBindingV1>(StringComparer.Ordinal);
        if (exactCoreLibStringEmpty && descriptors.TryGetValue("System.String", out HybridCpuManagedTypeDescriptorV1? stringDescriptor) &&
            handles.TryGetValue("System.String", out ulong stringHandle))
        {
            fields.Add("System.String::Empty", new("System.String", "Empty", true,
                RestrictedCilTypeV1.ObjectReference, stringDescriptor,
                $"__hybridcpu_static_{stringDescriptor.TypeId:x16}"));
            initializers.Add("System.String", new("System.String", 0, stringDescriptor, stringHandle,
                RuntimePreinitialized: true));
        }
        Dictionary<string, IReadOnlyList<RestrictedCilAllocationBindingV1>> allocationsByAssembly = new Dictionary<string, IReadOnlyList<RestrictedCilAllocationBindingV1>>(
            StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<RestrictedCilArrayTypeBindingV1>> arraysByAssembly = new Dictionary<string, IReadOnlyList<RestrictedCilArrayTypeBindingV1>>(
            StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<RestrictedCilTypeTestBindingV1>> typeTestsByAssembly = new Dictionary<string, IReadOnlyList<RestrictedCilTypeTestBindingV1>>(
            StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<RestrictedCilFieldDataBindingV1>> fieldDataByAssembly = new Dictionary<string, IReadOnlyList<RestrictedCilFieldDataBindingV1>>(
            StringComparer.Ordinal);
        ulong nextDataHandle = 1;
        foreach (ManagedModuleContext module in modules.OrderBy(static row => row.AssemblyName, StringComparer.Ordinal))
        {
            MetadataReader metadata = module.Metadata;
            List<RestrictedCilAllocationBindingV1> allocations = new List<RestrictedCilAllocationBindingV1>();
            List<RestrictedCilTypeTestBindingV1> typeTests = new List<RestrictedCilTypeTestBindingV1>();
            RestrictedCilArrayTypeBindingV1[] arrays = referenceArrays.Where(row => row.Key.Assembly == module.AssemblyName)
                .Select(row => new RestrictedCilArrayTypeBindingV1(row.Key.ElementToken, row.Value.Array,
                    handles[row.Value.Array.StableIdentity], row.Value.Element))
                .OrderBy(static row => row.ElementTypeMetadataToken).ToArray();
            List<RestrictedCilFieldDataBindingV1> fieldData = new List<RestrictedCilFieldDataBindingV1>();
            foreach (MemberReferenceHandle memberHandle in metadata.MemberReferences)
            {
                MemberReference member = metadata.GetMemberReference(memberHandle);
                if (metadata.GetString(member.Name) != ".ctor" || member.Parent.Kind != HandleKind.TypeReference ||
                    !TryResolveType(resolutionModules, module, member.Parent, out ManagedModuleContext stringModule,
                        out TypeDefinitionHandle stringType) || stringModule.AssemblyName != "System.Private.CoreLib")
                    continue;
                string targetIdentity = FullTypeName(stringModule.Metadata, stringType);
                byte[] signature = metadata.GetBlobBytes(member.Signature);
                bool exactStringFactory = targetIdentity == "System.String" &&
                    signature.AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x1d, 0x03 });
                bool exactInvariantArgumentException = targetIdentity is "System.ArgumentNullException" or
                    "System.ArgumentOutOfRangeException" &&
                    signature.AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x0e });
                if ((exactStringFactory || exactInvariantArgumentException) &&
                    descriptors.TryGetValue(targetIdentity, out HybridCpuManagedTypeDescriptorV1? targetDescriptor) &&
                    handles.TryGetValue(targetIdentity, out ulong targetHandle))
                    allocations.Add(new(MetadataTokens.GetToken(memberHandle), targetDescriptor, targetHandle));
            }
            foreach (FieldDefinitionHandle fieldHandle in metadata.FieldDefinitions)
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                int rva = field.GetRelativeVirtualAddress();
                if (rva == 0) continue;
                int size = MetadataFieldDataSize(metadata, field.Signature);
                if (size <= 0 || size > 1_048_576 || nextDataHandle > 4096) continue;
                byte[] data = module.Reader.GetSectionData(rva).GetContent(0, size).ToArray();
                int token = MetadataTokens.GetToken(fieldHandle);
                fieldData.Add(new(token, nextDataHandle++, data));
            }
            fieldDataByAssembly.Add(module.AssemblyName, fieldData.OrderBy(static row => row.FieldMetadataToken).ToArray());
            foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
            {
                string identity = FullTypeName(metadata, typeHandle);
                if (!descriptors.TryGetValue(identity, out HybridCpuManagedTypeDescriptorV1? descriptor)) continue;
                if (descriptor.Kind is HybridCpuManagedTypeKindV1.Class or HybridCpuManagedTypeKindV1.Interface or
                    HybridCpuManagedTypeKindV1.String)
                    typeTests.Add(new(MetadataTokens.GetToken(typeHandle), descriptor, handles[identity]));
                if (!logicalFields.TryGetValue(identity, out IReadOnlyList<(string Name, bool Static, RestrictedCilTypeV1 Carrier, ManagedModuleContext Module, BlobHandle Signature)>? logical)) continue;
                TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
                if (descriptor.Kind == HybridCpuManagedTypeKindV1.Class &&
                    !type.Attributes.HasFlag(TypeAttributes.Abstract))
                {
                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        MethodDefinition candidate = metadata.GetMethodDefinition(methodHandle);
                        if (!candidate.Attributes.HasFlag(MethodAttributes.Static) &&
                            string.Equals(metadata.GetString(candidate.Name), ".ctor", StringComparison.Ordinal))
                        {
                            allocations.Add(new(MetadataTokens.GetToken(methodHandle), descriptor, handles[identity]));
                        }
                    }
                }
                else if (!type.BaseType.IsNil && ExactTypeHandleName(metadata, type.BaseType) == "System.ValueType")
                {
                    FieldDefinitionHandle[] instanceFields = type.GetFields()
                        .Where(field => !metadata.GetFieldDefinition(field).Attributes.HasFlag(FieldAttributes.Static))
                        .ToArray();
                    if (instanceFields.Length == 1)
                    {
                        RestrictedCilTypeV1 carrier = ParseMetadataFieldType(metadata,
                            metadata.GetFieldDefinition(instanceFields[0]).Signature);
                        if (carrier is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                            RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                            RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt)
                        {
                            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                            {
                                MethodDefinition candidate = metadata.GetMethodDefinition(methodHandle);
                                if (candidate.Attributes.HasFlag(MethodAttributes.Static) ||
                                    !string.Equals(metadata.GetString(candidate.Name), ".ctor", StringComparison.Ordinal) ||
                                    candidate.RelativeVirtualAddress == 0)
                                    continue;
                                byte[] signature = metadata.GetBlobBytes(candidate.Signature);
                                MethodBodyBlock body = module.Reader.GetMethodBody(candidate.RelativeVirtualAddress);
                                byte[] il = body.GetILBytes() ?? [];
                                RestrictedCilTypeV1 argument = signature.Length == 4 ? signature[3] switch
                                {
                                    0x08 => RestrictedCilTypeV1.Int32,
                                    0x09 => RestrictedCilTypeV1.UInt32,
                                    0x0a => RestrictedCilTypeV1.Int64,
                                    0x0b => RestrictedCilTypeV1.UInt64,
                                    0x18 => RestrictedCilTypeV1.NativeInt,
                                    0x19 => RestrictedCilTypeV1.NativeUInt,
                                    _ => RestrictedCilTypeV1.Invalid
                                } : RestrictedCilTypeV1.Invalid;
                                bool sameWidth = carrier is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32
                                    ? argument is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32
                                    : argument is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                                        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt;
                                int ilStart = il.Length == 9 && il[0] == 0x00 ? 1 : 0;
                                if (signature.Length == 4 && signature[0] == 0x20 && signature[1] == 0x01 &&
                                    signature[2] == 0x01 && sameWidth &&
                                    body.ExceptionRegions.Length == 0 && body.LocalSignature.IsNil &&
                                    il.Length - ilStart == 8 && il[ilStart] == 0x02 && il[ilStart + 1] == 0x03 &&
                                    il[ilStart + 2] == 0x7d && il[ilStart + 7] == 0x2a &&
                                    BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(ilStart + 3, 4)) == MetadataTokens.GetToken(instanceFields[0]))
                                {
                                    allocations.Add(new(MetadataTokens.GetToken(methodHandle), descriptor,
                                        handles[identity], true, carrier, argument));
                                }
                            }
                        }
                    }
                }
                foreach ((string Name, bool Static, RestrictedCilTypeV1 Carrier, ManagedModuleContext Module, BlobHandle Signature) field in logical)
                {
                    RestrictedCilTypeV1 carrier = field.Carrier;
                    RestrictedCilTypeV1 projectedCarrier = RestrictedCilTypeV1.Invalid;
                    bool scalarProjection = carrier == RestrictedCilTypeV1.UnsupportedManaged &&
                        TryStaticScalarValueCarrier(resolutionModules, field.Module, field.Signature, descriptors,
                            out projectedCarrier);
                    if (scalarProjection) carrier = projectedCarrier;
                    fields.TryAdd($"{identity}::{field.Name}", new(identity, field.Name, field.Static,
                        carrier, descriptor, field.Static ? $"__hybridcpu_static_{descriptor.TypeId:x16}" : null,
                        scalarProjection));
                }
                MethodDefinitionHandle[] cctors = metadata.GetTypeDefinition(typeHandle).GetMethods().Where(method =>
                    string.Equals(metadata.GetString(metadata.GetMethodDefinition(method).Name), ".cctor",
                        StringComparison.Ordinal)).ToArray();
                if (cctors.Length == 1)
                    initializers.TryAdd(identity, new(identity, MetadataTokens.GetToken(cctors[0]), descriptor,
                        handles[identity]));
            }
            allocationsByAssembly.Add(module.AssemblyName, allocations
                .OrderBy(static row => row.ConstructorMetadataToken).ToArray());
            arraysByAssembly.Add(module.AssemblyName, arrays);
            typeTestsByAssembly.Add(module.AssemblyName, typeTests.OrderBy(static row => row.TypeMetadataToken).ToArray());
        }
        // MemberRef tokens belong to the consumer module, not the constructor's owner.
        // Reuse only an already qualified definition binding after exact signature resolution.
        foreach (ManagedModuleContext module in modules)
        {
            List<RestrictedCilAllocationBindingV1> allocations = allocationsByAssembly[module.AssemblyName].ToList();
            foreach (MemberReferenceHandle memberHandle in module.Metadata.MemberReferences)
            {
                MemberReference member = module.Metadata.GetMemberReference(memberHandle);
                int token = MetadataTokens.GetToken(memberHandle);
                if (module.Metadata.GetString(member.Name) != ".ctor" ||
                    allocations.Any(binding => binding.ConstructorMetadataToken == token) ||
                    !TryResolveManagedDefinition(modules, module, token, null, null,
                        out ManagedModuleContext owner, out _, out MethodDefinitionHandle constructor,
                        out IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments, out IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments, out _) ||
                    typeArguments.Count != 0 || methodArguments.Count != 0)
                    continue;
                RestrictedCilAllocationBindingV1? definition = allocationsByAssembly[owner.AssemblyName].SingleOrDefault(binding =>
                    binding.ConstructorMetadataToken == MetadataTokens.GetToken(constructor));
                if (definition is not null && (!definition.IsScalarValueProjection ||
                    definition.ScalarValueType == RestrictedCilTypeV1.Int64 &&
                    definition.ScalarArgumentType == RestrictedCilTypeV1.Int64 && IsExactScalarCarrierLayout(definition)))
                    allocations.Add(definition with { ConstructorMetadataToken = token });
            }
            allocationsByAssembly[module.AssemblyName] = allocations.OrderBy(binding =>
                binding.ConstructorMetadataToken).ToArray();
        }
        return new(descriptors, fields, initializers, allocationsByAssembly, arraysByAssembly, typeTestsByAssembly,
            fieldDataByAssembly,
            closedInterfaces.Plans.Values.OrderBy(static plan => plan.StableIdentity, StringComparer.Ordinal).ToArray());
    }

    private static bool TryStaticScalarValueCarrier(IReadOnlyList<ManagedModuleContext> modules,
        ManagedModuleContext module, BlobHandle signature,
        IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1> descriptors,
        out RestrictedCilTypeV1 carrier)
    {
        carrier = RestrictedCilTypeV1.Invalid;
        try
        {
            BlobReader reader = module.Metadata.GetBlobReader(signature);
            if (reader.RemainingBytes < 3 || reader.ReadByte() != 0x06 || reader.ReadByte() != 0x11)
                return false;
            EntityHandle valueHandle = ReadTypeDefOrRefEncoded(ref reader);
            if (reader.RemainingBytes != 0 ||
                !TryResolveType(modules, module, valueHandle, out ManagedModuleContext valueModule,
                    out TypeDefinitionHandle valueTypeHandle))
                return false;
            string valueIdentity = FullTypeName(valueModule.Metadata, valueTypeHandle);
            if (!descriptors.TryGetValue(valueIdentity, out HybridCpuManagedTypeDescriptorV1? valueDescriptor) ||
                valueDescriptor.Kind != HybridCpuManagedTypeKindV1.ValueType ||
                valueDescriptor.ValueTypeShape is not { PayloadSizeBytes: 4, PayloadAlignmentBytes: 4,
                    ObjectReferenceOffsets.Count: 0 } ||
                valueDescriptor.InstanceFields is not [{ OffsetBytes: 0, SizeBytes: 4, AlignmentBytes: 4,
                    StorageKind: HybridCpuManagedStorageKindV1.Primitive } inner])
                return false;
            TypeDefinition valueType = valueModule.Metadata.GetTypeDefinition(valueTypeHandle);
            FieldDefinitionHandle[] instanceFields = valueType.GetFields()
                .Where(handle => !valueModule.Metadata.GetFieldDefinition(handle).Attributes.HasFlag(FieldAttributes.Static))
                .ToArray();
            if (instanceFields.Length != 1 ||
                valueModule.Metadata.GetString(valueModule.Metadata.GetFieldDefinition(instanceFields[0]).Name) != inner.Identity)
                return false;
            carrier = ParseMetadataFieldType(valueModule.Metadata,
                valueModule.Metadata.GetFieldDefinition(instanceFields[0]).Signature);
            return carrier is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32;
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentOutOfRangeException)
        {
            carrier = RestrictedCilTypeV1.Invalid;
            return false;
        }
    }

    private MethodSignature ProjectScalarValueSignature(MetadataReader metadata, MethodSignature signature,
        IReadOnlyList<RestrictedCilAllocationBindingV1>? metadataAllocations = null,
        IReadOnlyDictionary<string, ExternalEnumCarrier>? enumDeclarations = null,
        IReadOnlyDictionary<string, ScalarValueDeclaration>? scalarDeclarations = null)
    {
        if (signature.Status != SignatureStatus.Success || signature.Receiver is not null) return signature;
        string assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        RestrictedCilTypeV1 Project(RestrictedCilTypeV1 carrier, string? identity)
        {
            if (carrier != RestrictedCilTypeV1.Aggregate || identity is null) return carrier;
            if ((enumDeclarations ?? _externalEnumCarriers).TryGetValue(identity, out ExternalEnumCarrier? enumCarrier))
                return enumCarrier.Carrier;
            if ((scalarDeclarations ?? _externalScalarValues).TryGetValue(identity, out ScalarValueDeclaration? declaration)) return declaration.Carrier;
            IEnumerable<RestrictedCilAllocationBindingV1> available = metadataAllocations is null
                ? _allocationBindings.Values
                : metadataAllocations.Concat(_allocationBindings.Values);
            RestrictedCilAllocationBindingV1[] candidates = available
                .Where(binding => binding.IsScalarValueProjection &&
                    identity == $"[{assembly}]{binding.TypeDescriptor.StableIdentity}" &&
                    binding.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.ValueType &&
                    IsExactScalarCarrierLayout(binding))
                .ToArray();
            return candidates.Length != 0 && candidates.Select(static binding => binding.ScalarValueType).Distinct().Count() == 1
                ? candidates[0].ScalarValueType : carrier;
        }
        RestrictedCilTypeV1[] parameters = signature.Parameters.Select((carrier, index) =>
            Project(carrier, signature.AggregateParameters?.ElementAtOrDefault(index))).ToArray();
        RestrictedCilTypeV1 result = Project(signature.ReturnType, signature.AggregateReturn);
        return signature with { Parameters = parameters, ReturnType = result };
    }

    private LocalSignature ProjectScalarValueLocals(MetadataReader metadata, LocalSignature locals,
        IReadOnlyList<DecodedInstruction>? instructions = null)
    {
        if (locals.Status != SignatureStatus.Success || locals.Aggregates is null) return locals;
        HashSet<int> addressedLocals = instructions is null ? [] : instructions
            .Where(static instruction => instruction.Encoding == 0x12)
            .Select(static instruction => checked((int)instruction.Literal)).ToHashSet();
        string assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        RestrictedCilTypeV1[] types = locals.Types.ToArray();
        string?[] aggregates = locals.Aggregates.ToArray();
        for (int index = 0; index < types.Length; index++)
        {
            string? identity = aggregates[index];
            if (types[index] != RestrictedCilTypeV1.Aggregate || identity is null) continue;
            if (addressedLocals.Contains(index)) continue;
            if (_externalScalarValues.TryGetValue(identity, out ScalarValueDeclaration? declaration)) {
                types[index] = declaration.Carrier; aggregates[index] = null; continue;
            }
            if (_externalEnumCarriers.TryGetValue(identity, out ExternalEnumCarrier? enumCarrier))
            {
                types[index] = enumCarrier.Carrier;
                aggregates[index] = null;
                continue;
            }
            RestrictedCilAllocationBindingV1[] candidates = _allocationBindings.Values
                .Where(binding => binding.IsScalarValueProjection &&
                    identity == $"[{assembly}]{binding.TypeDescriptor.StableIdentity}" &&
                    binding.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.ValueType &&
                    IsExactScalarCarrierLayout(binding))
                .ToArray();
            if (candidates.Length == 0 || candidates.Select(static binding => binding.ScalarValueType).Distinct().Count() != 1)
                continue;
            types[index] = candidates[0].ScalarValueType;
            aggregates[index] = null;
        }
        return locals with { Types = types, Aggregates = aggregates };
    }

    private bool IsProvenInt64ScalarIdentity(MetadataReader metadata, string? identity) =>
        identity is not null && (_externalScalarValues.TryGetValue(identity, out ScalarValueDeclaration? declaration) && declaration.Carrier == RestrictedCilTypeV1.Int64 ||
        _allocationBindings.Values.Any(binding => binding.IsScalarValueProjection &&
            binding.ScalarValueType == RestrictedCilTypeV1.Int64 && IsExactScalarCarrierLayout(binding) &&
            identity == $"[{metadata.GetString(metadata.GetAssemblyDefinition().Name)}]{binding.TypeDescriptor.StableIdentity}"));

    private static bool IsExactScalarCarrierLayout(RestrictedCilAllocationBindingV1 binding) =>
        binding.ScalarValueType switch {
            RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 =>
                binding.TypeDescriptor.ValueTypeShape is { PayloadSizeBytes: 4, PayloadAlignmentBytes: 4, ObjectReferenceOffsets.Count: 0 },
            RestrictedCilTypeV1.Int64 =>
                binding.TypeDescriptor.ValueTypeShape is { PayloadSizeBytes: 8, PayloadAlignmentBytes: 8, ObjectReferenceOffsets.Count: 0 },
            _ => false
        };

    private static int MetadataFieldDataSize(MetadataReader metadata, BlobHandle signature)
    {
        try
        {
            BlobReader reader = metadata.GetBlobReader(signature);
            if (reader.RemainingBytes < 2 || reader.ReadByte() != 0x06) return 0;
            byte element = reader.ReadByte();
            if (element == 0x11)
            {
                EntityHandle typeHandle = ReadTypeDefOrRefEncoded(ref reader);
                if (typeHandle.Kind != HandleKind.TypeDefinition || reader.RemainingBytes != 0) return 0;
                TypeLayout layout = metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle).GetLayout();
                return layout.IsDefault ? 0 : layout.Size;
            }
            RestrictedCilTypeV1 carrier = element switch
            {
                0x02 or 0x04 or 0x05 => RestrictedCilTypeV1.UInt8,
                0x03 or 0x06 or 0x07 => RestrictedCilTypeV1.UInt16,
                0x08 or 0x09 => RestrictedCilTypeV1.Int32,
                0x0a or 0x0b => RestrictedCilTypeV1.Int64,
                _ => RestrictedCilTypeV1.UnsupportedManaged
            };
            return reader.RemainingBytes == 0 && TryStorage(carrier, out _, out int size, out _) ? size : 0;
        }
        catch (BadImageFormatException)
        {
            return 0;
        }
    }

    private static RestrictedCilTypeV1 ParseMetadataFieldType(MetadataReader metadata, BlobHandle signature)
    {
        try
        {
            BlobReader reader = metadata.GetBlobReader(signature);
            if (reader.RemainingBytes == 0 || reader.ReadByte() != 0x06) return RestrictedCilTypeV1.Invalid;
            RestrictedCilTypeV1 type = ReadType(metadata, ref reader);
            return reader.RemainingBytes == 0 ? type : RestrictedCilTypeV1.UnsupportedManaged;
        }
        catch (BadImageFormatException)
        {
            return RestrictedCilTypeV1.Invalid;
        }
    }

    private static bool TryStorage(RestrictedCilTypeV1 type, out HybridCpuManagedStorageKindV1 storage,
        out int size, out int alignment)
    {
        storage = type == RestrictedCilTypeV1.ObjectReference
            ? HybridCpuManagedStorageKindV1.ObjectReference : HybridCpuManagedStorageKindV1.Primitive;
        size = type switch
        {
            RestrictedCilTypeV1.Boolean or RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.UInt8 => 1,
            RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16 => 2,
            RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 => 4,
            RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.NativeInt or
                RestrictedCilTypeV1.NativeUInt or RestrictedCilTypeV1.ObjectReference => 8,
            _ => 0
        };
        alignment = size;
        return size != 0;
    }

    private static RestrictedCilTypeV1 Carrier(HybridCpuManagedFieldLayoutV1 field) =>
        field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference ? RestrictedCilTypeV1.ObjectReference :
        field.StorageKind == HybridCpuManagedStorageKindV1.BlittableValue ? RestrictedCilTypeV1.UnsupportedManaged :
        field.SizeBytes switch
        {
            1 => RestrictedCilTypeV1.UInt8,
            2 => RestrictedCilTypeV1.UInt16,
            4 => RestrictedCilTypeV1.Int32,
            8 => RestrictedCilTypeV1.Int64,
            _ => RestrictedCilTypeV1.UnsupportedManaged
        };

    private static int AlignMetadata(int instance, int alignment) =>
        checked((instance + alignment - 1) / alignment * alignment);

    private static ulong MetadataTypeId(string identity)
    {
        ulong result = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hybridcpu.managed-type-id/v1|{identity}")));
        return result == 0 ? 1 : result;
    }

    private static bool TryResolveType(IReadOnlyList<ManagedModuleContext> modules, ManagedModuleContext source,
        EntityHandle handle, out ManagedModuleContext targetModule, out TypeDefinitionHandle targetType)
    {
        targetModule = source;
        targetType = default;
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            targetType = (TypeDefinitionHandle)handle;
            return true;
        }
        if (handle.Kind != HandleKind.TypeReference) return false;
        TypeReferenceHandle reference = (TypeReferenceHandle)handle;
        string identity = TypeReferenceName(source.Metadata, reference);
        string? assembly = ReferencedAssemblyName(source.Metadata, reference);
        if (assembly is null)
        {
            (ManagedModuleContext Module, TypeDefinitionHandle Type)[] matches = modules.SelectMany(module => module.Metadata.TypeDefinitions.Where(type =>
                    FullTypeName(module.Metadata, type) == identity)
                .Select(type => (Module: module, Type: type))).ToArray();
            if (matches.Length != 1) return false;
            targetModule = matches[0].Module; targetType = matches[0].Type; return true;
        }
        HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Count < 16 && visited.Add(assembly))
        {
            ManagedModuleContext[] candidates = modules.Where(module => module.AssemblyName == assembly).ToArray();
            if (candidates.Length != 1) return false;
            targetModule = candidates[0];
            MetadataReader metadata = targetModule.Metadata;
            TypeDefinitionHandle[] definitions = metadata.TypeDefinitions
                .Where(type => FullTypeName(metadata, type) == identity).ToArray();
            if (definitions.Length == 1) { targetType = definitions[0]; return true; }
            if (definitions.Length != 0) return false;
            ExportedType[] exports = metadata.ExportedTypes.Select(metadata.GetExportedType).Where(row =>
                (string.IsNullOrEmpty(metadata.GetString(row.Namespace)) ? "" : metadata.GetString(row.Namespace) + ".") +
                metadata.GetString(row.Name) == identity).ToArray();
            if (exports.Length != 1 || !exports[0].IsForwarder ||
                exports[0].Implementation.Kind != HandleKind.AssemblyReference) return false;
            assembly = metadata.GetString(metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)exports[0].Implementation).Name);
        }
        return false;
    }
}
