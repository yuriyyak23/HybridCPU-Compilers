using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedDelegateStatusV1 : byte
{
    Success = 0,
    InvalidMetadata = 1,
    MissingDependency = 2,
    BudgetExhausted = 3,
    NullDelegate = 4,
    InvalidDelegate = 5,
    SignatureMismatch = 6,
    NullTarget = 7,
    MulticastUnsupported = 8
}

public sealed record HybridCpuManagedDelegateCreateRequestV1(
    ulong DelegateTypeHandle,
    ulong SignatureId,
    HybridCpuManagedDelegateKindV1 Kind,
    ulong CodeAddress,
    ulong TargetObject = 0,
    ulong Context = 0,
    int InvocationCount = 1);

public sealed record HybridCpuManagedDelegateResultV1(
    HybridCpuManagedDelegateStatusV1 Status,
    string Reason,
    ulong DelegateReference,
    ulong SignatureId,
    HybridCpuManagedDelegateKindV1 Kind,
    ulong CodeAddress,
    ulong InvocationAddress,
    ulong TargetObject,
    ulong Context,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedDelegateStatusV1.Success;
}

public sealed class HybridCpuManagedDelegateRuntimeV1
{
    public const string SchemaId = "hybridcpu.managed-delegate-runtime/v1";
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly IReadOnlyDictionary<(ulong MethodId, ulong Signature), HybridCpuManagedFunctionPointerEntryV1> _methodsById;
    private readonly IReadOnlyDictionary<(ulong Address, ulong Signature), HybridCpuManagedFunctionPointerEntryV1> _methodsByPointer;

    public HybridCpuManagedDelegateRuntimeV1(
        HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap,
        IEnumerable<HybridCpuManagedFunctionPointerEntryV1> methods)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(heap);
        ArgumentNullException.ThrowIfNull(methods);
        HybridCpuManagedFunctionPointerEntryV1[] ordered = methods.OrderBy(static row => row.MethodId)
            .ThenBy(static row => row.SignatureId).ToArray();
        if (ordered.Length > HybridCpuPlatformContractV1.MaximumManagedFunctionPointers ||
            ordered.Any(static row => row.MethodId == 0 || row.SignatureId == 0 || row.CodeAddress == 0 || row.InvocationThunkAddress == 0) ||
            ordered.Select(static row => (row.MethodId, row.SignatureId)).Distinct().Count() != ordered.Length ||
            ordered.Select(static row => (row.CodeAddress, row.SignatureId)).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Managed function-pointer entries must be bounded, non-zero and uniquely identified.", nameof(methods));
        _types = types;
        _heap = heap;
        _methodsById = ordered.ToDictionary(static row => (row.MethodId, row.SignatureId));
        _methodsByPointer = ordered.ToDictionary(static row => (row.CodeAddress, row.SignatureId));
        ContractDigest = Hash(string.Join('|', SchemaId, HybridCpuPlatformContractV1.ContractDigest,
            string.Join(';', ordered.Select(static row => $"{row.MethodId}:{row.SignatureId}:{row.CodeAddress}:{row.IsInstanceMethod}:{row.InvocationThunkAddress}")),
            "single-cast", "static:closed-instance:open-instance", "multicast=unsupported",
            "native-interop-pointers=unsupported", "runtime-authority=true", "ise-authority=false"));
    }

    public string ContractDigest { get; }
    public bool HasDelegateSemanticAuthority => true;
    public bool HasIseExecutionAuthority => false;

    public static ulong ComputeSignatureId(string canonicalSignature)
    {
        if (string.IsNullOrWhiteSpace(canonicalSignature)) throw new ArgumentException("A canonical signature is required.", nameof(canonicalSignature));
        return HybridCpuPlatformContractV1.ComputeManagedCallSignatureId(canonicalSignature);
    }

    public HybridCpuManagedDelegateResultV1 ResolveFunctionPointer(ulong methodId, ulong expectedSignatureId)
    {
        if (!_methodsById.TryGetValue((methodId, expectedSignatureId), out HybridCpuManagedFunctionPointerEntryV1? method))
            return _methodsById.Keys.Any(key => key.MethodId == methodId)
                ? Failure(HybridCpuManagedDelegateStatusV1.SignatureMismatch, "The managed function-pointer signature is not exact.")
                : Failure(HybridCpuManagedDelegateStatusV1.MissingDependency, "The managed method identity is absent.");
        return Success(0, method.SignatureId, method.IsInstanceMethod
                ? HybridCpuManagedDelegateKindV1.OpenInstance : HybridCpuManagedDelegateKindV1.Static,
                method.CodeAddress, method.CodeAddress, 0, 0);
    }

    public HybridCpuManagedDelegateResultV1 ValidateFunctionPointer(ulong codeAddress, ulong expectedSignatureId)
    {
        if (codeAddress == 0) return Failure(HybridCpuManagedDelegateStatusV1.NullTarget, "A managed function pointer cannot be null.");
        return _methodsByPointer.TryGetValue((codeAddress, expectedSignatureId), out HybridCpuManagedFunctionPointerEntryV1? method)
            ? Success(0, method.SignatureId, method.IsInstanceMethod
                ? HybridCpuManagedDelegateKindV1.OpenInstance : HybridCpuManagedDelegateKindV1.Static,
                method.CodeAddress, method.CodeAddress, 0, 0)
            : Failure(HybridCpuManagedDelegateStatusV1.SignatureMismatch,
                "The code address is not a registered managed pointer with the exact signature; native/interop pointers are not admitted.");
    }

    public HybridCpuManagedDelegateResultV1 Create(HybridCpuManagedDelegateCreateRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InvocationCount != 1)
            return Failure(HybridCpuManagedDelegateStatusV1.MulticastUnsupported, "Multicast delegates are explicitly outside V1.");
        if (!_methodsByPointer.TryGetValue((request.CodeAddress, request.SignatureId), out HybridCpuManagedFunctionPointerEntryV1? method))
            return Failure(HybridCpuManagedDelegateStatusV1.SignatureMismatch, "Delegate target and signature are not an exact managed function-pointer entry.");
        bool validForm = request.Kind switch
        {
            HybridCpuManagedDelegateKindV1.Static => !method.IsInstanceMethod && request.TargetObject == 0,
            HybridCpuManagedDelegateKindV1.ClosedInstance => method.IsInstanceMethod && request.TargetObject != 0,
            HybridCpuManagedDelegateKindV1.OpenInstance => method.IsInstanceMethod && request.TargetObject == 0,
            _ => false
        };
        if (!validForm)
            return Failure(request.Kind == HybridCpuManagedDelegateKindV1.ClosedInstance && request.TargetObject == 0
                ? HybridCpuManagedDelegateStatusV1.NullTarget : HybridCpuManagedDelegateStatusV1.InvalidMetadata,
                "Delegate target form does not match the exact method kind.");
        if (request.TargetObject != 0 && !_heap.ActiveAllocations().Any(row => row.ObjectAddress == request.TargetObject))
            return Failure(HybridCpuManagedDelegateStatusV1.InvalidDelegate, "Closed-instance target is not an active managed object.");
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(request.DelegateTypeHandle);
        HybridCpuManagedDelegateLayoutV1 layout = HybridCpuPlatformContractV1.ManagedDelegateLayout;
        if (!IsDelegateType(type, layout))
            return Failure(HybridCpuManagedDelegateStatusV1.InvalidMetadata,
                "Delegate type must be a class with the exact GC-visible target-object field and minimum layout.");
        HybridCpuManagedHeapResultV1 allocation = _heap.Allocate(request.DelegateTypeHandle);
        if (!allocation.IsSuccess)
            return Failure(HybridCpuManagedDelegateStatusV1.MissingDependency, allocation.Reason);
        byte[] payload = new byte[layout.MinimumObjectSizeBytes - HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes];
        Write(payload, layout.TargetObjectOffsetBytes, request.TargetObject);
        Write(payload, layout.CodePointerOffsetBytes, request.CodeAddress);
        Write(payload, layout.ContextOffsetBytes, request.Context);
        Write(payload, layout.SignatureIdOffsetBytes, request.SignatureId);
        payload[layout.KindOffsetBytes - HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes] = (byte)request.Kind;
        Write(payload, layout.InvocationThunkOffsetBytes, method.InvocationThunkAddress);
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(allocation.ObjectReference,
            HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes, payload);
        return write.IsSuccess
            ? Success(allocation.ObjectReference, request.SignatureId, request.Kind, request.CodeAddress,
                method.InvocationThunkAddress, request.TargetObject, request.Context)
            : Failure(HybridCpuManagedDelegateStatusV1.InvalidDelegate, write.Reason);
    }

    public HybridCpuManagedDelegateResultV1 ResolveInvocation(ulong delegateReference, ulong expectedSignatureId)
    {
        if (delegateReference == 0) return Failure(HybridCpuManagedDelegateStatusV1.NullDelegate, "Delegate invocation requires a non-null delegate object.");
        HybridCpuManagedActiveAllocationV1? allocation = _heap.ActiveAllocations().FirstOrDefault(row =>
            row.ObjectAddress == delegateReference);
        HybridCpuManagedTypeDescriptorV1? delegateType = allocation is null ? null :
            _types.Descriptors.FirstOrDefault(row => row.TypeId == allocation.TypeId);
        byte[]? bytes = _heap.ReadObjectBytes(delegateReference);
        HybridCpuManagedDelegateLayoutV1 layout = HybridCpuPlatformContractV1.ManagedDelegateLayout;
        if (bytes is null || bytes.Length < layout.MinimumObjectSizeBytes || !IsDelegateType(delegateType, layout))
            return Failure(HybridCpuManagedDelegateStatusV1.InvalidDelegate, "Delegate reference is not an active exact-layout object.");
        ulong target = Read(bytes, layout.TargetObjectOffsetBytes);
        ulong code = Read(bytes, layout.CodePointerOffsetBytes);
        ulong context = Read(bytes, layout.ContextOffsetBytes);
        ulong signature = Read(bytes, layout.SignatureIdOffsetBytes);
        HybridCpuManagedDelegateKindV1 kind = (HybridCpuManagedDelegateKindV1)bytes[layout.KindOffsetBytes];
        ulong thunk = Read(bytes, layout.InvocationThunkOffsetBytes);
        if (signature != expectedSignatureId ||
            !_methodsByPointer.TryGetValue((code, signature), out HybridCpuManagedFunctionPointerEntryV1? method))
            return Failure(HybridCpuManagedDelegateStatusV1.SignatureMismatch, "Delegate invocation signature or code target is not exact.");
        bool validForm = kind switch
        {
            HybridCpuManagedDelegateKindV1.Static => !method.IsInstanceMethod && target == 0,
            HybridCpuManagedDelegateKindV1.ClosedInstance => method.IsInstanceMethod && target != 0,
            HybridCpuManagedDelegateKindV1.OpenInstance => method.IsInstanceMethod && target == 0,
            _ => false
        };
        if (!validForm)
            return Failure(HybridCpuManagedDelegateStatusV1.InvalidDelegate,
                "Delegate target form does not match its registered managed method.");
        if (kind == HybridCpuManagedDelegateKindV1.ClosedInstance &&
            (target == 0 || !_heap.ActiveAllocations().Any(row => row.ObjectAddress == target)))
            return Failure(HybridCpuManagedDelegateStatusV1.NullTarget, "Closed-instance delegate target is null or no longer active.");
        if (thunk == 0 || thunk != method.InvocationThunkAddress)
            return Failure(HybridCpuManagedDelegateStatusV1.InvalidDelegate,
                "Delegate invocation thunk is absent or does not match registered metadata.");
        return Success(delegateReference, signature, kind, code, thunk, target, context);
    }

    private static bool IsDelegateType(HybridCpuManagedTypeDescriptorV1? type,
        HybridCpuManagedDelegateLayoutV1 layout) =>
        type is not null && type.Kind == HybridCpuManagedTypeKindV1.Class &&
        type.InstanceSizeBytes == layout.MinimumObjectSizeBytes && type.InstanceFields.Count == 6 &&
        HasField(type, layout.TargetObjectOffsetBytes, HybridCpuManagedStorageKindV1.ObjectReference) &&
        HasField(type, layout.CodePointerOffsetBytes, HybridCpuManagedStorageKindV1.Primitive) &&
        HasField(type, layout.ContextOffsetBytes, HybridCpuManagedStorageKindV1.Primitive) &&
        HasField(type, layout.SignatureIdOffsetBytes, HybridCpuManagedStorageKindV1.Primitive) &&
        HasField(type, layout.KindOffsetBytes, HybridCpuManagedStorageKindV1.Primitive) &&
        HasField(type, layout.InvocationThunkOffsetBytes, HybridCpuManagedStorageKindV1.Primitive);

    private static bool HasField(HybridCpuManagedTypeDescriptorV1 type, int offset,
        HybridCpuManagedStorageKindV1 storage) => type.InstanceFields.Any(field =>
        field.OffsetBytes == offset && field.StorageKind == storage && field.SizeBytes == 8);

    private static void Write(Span<byte> payload, int objectOffset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(payload[(objectOffset - HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes)..], value);
    private static ulong Read(ReadOnlySpan<byte> bytes, int objectOffset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[objectOffset..]);
    private static string Hash(string value) => HybridCpuPlatformContractV1.Hash(value);
    private static HybridCpuManagedDelegateResultV1 Success(ulong reference, ulong signature,
        HybridCpuManagedDelegateKindV1 kind, ulong code, ulong target, ulong context) =>
        Success(reference, signature, kind, code, code, target, context);
    private static HybridCpuManagedDelegateResultV1 Success(ulong reference, ulong signature,
        HybridCpuManagedDelegateKindV1 kind, ulong code, ulong invocation, ulong target, ulong context) =>
        new(HybridCpuManagedDelegateStatusV1.Success, string.Empty, reference, signature, kind, code, invocation, target, context,
            Hash($"success|{reference}|{signature}|{kind}|{code}|{invocation}|{target}|{context}"));
    private static HybridCpuManagedDelegateResultV1 Failure(HybridCpuManagedDelegateStatusV1 status, string reason) =>
        new(status, reason, 0, 0, HybridCpuManagedDelegateKindV1.Static, 0, 0, 0, 0, Hash($"failure|{status}|{reason}"));
}
