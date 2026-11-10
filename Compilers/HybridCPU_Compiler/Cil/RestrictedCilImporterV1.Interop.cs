using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed record RestrictedCilPInvokeAdmissionV1(
    RestrictedCilImportStatusV1 Status,
    string Reason,
    int MetadataToken,
    HybridCpuManagedInteropSignatureV1? Signature,
    string ImportSymbol,
    string DispatchHelper,
    string AdmissionDigest)
{
    public bool IsAdmitted => Status == RestrictedCilImportStatusV1.Success && Signature is not null;
}

public sealed partial class RestrictedCilImporterV1
{
    public RestrictedCilPInvokeAdmissionV1 AdmitPInvokeImage(ReadOnlyMemory<byte> peImage,
        RestrictedCilMethodSelectorV1 selector)
    {
        if (selector is null || string.IsNullOrWhiteSpace(selector.TypeName) ||
            string.IsNullOrWhiteSpace(selector.MethodName) || peImage.IsEmpty)
            return PInvokeFailure(RestrictedCilImportStatusV1.InvalidInput,
                "A managed PE image and exact P/Invoke selector are required.");
        try
        {
            using MemoryStream stream = new MemoryStream(peImage.ToArray(), writable: false);
            using PEReader pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
                return PInvokeFailure(RestrictedCilImportStatusV1.InvalidInput,
                    "The input is not a managed PE image.");
            MetadataReader metadata = pe.GetMetadataReader();
            MethodSelection selected = SelectMethod(metadata, selector);
            if (selected.Failure is not null)
                return PInvokeFailure(selected.Failure.Status,
                    selected.Failure.Diagnostics.FirstOrDefault()?.Message ?? "P/Invoke selection failed.");
            return AdmitPInvokeDeclaration(metadata, selected.Method);
        }
        catch (BadImageFormatException)
        {
            return PInvokeFailure(RestrictedCilImportStatusV1.InvalidInput,
                "The P/Invoke metadata image is malformed.");
        }
    }

    private HelperResolution ResolvePInvokeDeclaration(MetadataReader metadata, MethodDefinitionHandle handle)
    {
        RestrictedCilPInvokeAdmissionV1 admission = AdmitPInvokeDeclaration(metadata, handle);
        if (!admission.IsAdmitted)
            return new(null, Reject(admission.Status, "HCCIL1015", admission.Reason));
        HybridCpuManagedInteropSignatureV1 signature = admission.Signature!;
        MethodSignature method = ParseMethodSignature(metadata, metadata.GetMethodDefinition(handle).Signature);
        RestrictedCilHelperContractV1 contract = new RestrictedCilHelperContractV1(
            $"pinvoke:{signature.Library}!{signature.Symbol}:{HybridCpuManagedInteropContractV1.SignatureDigest(signature)}",
            method.Parameters.Select(StackType).ToArray(), StackType(method.ReturnType),
            HybridCpuManagedInteropLoweringV1.DispatchHelper,
            "runtime-helper",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, IrArchitecturalEffectKind.Control);
        return new(new(contract.StableIdentity, contract, false), null);
    }

    private RestrictedCilPInvokeAdmissionV1 AdmitPInvokeDeclaration(MetadataReader metadata,
        MethodDefinitionHandle handle)
    {
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        int token = MetadataTokens.GetToken(handle);
        if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0 ||
            (method.Attributes & MethodAttributes.Static) == 0 || method.GetGenericParameters().Count != 0)
            return PInvokeFailure(RestrictedCilImportStatusV1.Unsupported,
                "Managed interop V1 requires one static non-generic P/Invoke declaration.", token);
        MethodImport import = method.GetImport();
        ushort attributes = (ushort)import.Attributes;
        if ((attributes & 0x0700) != 0x0200 || (attributes & 0x0040) != 0)
            return PInvokeFailure(RestrictedCilImportStatusV1.Unsupported,
                "Managed interop V1 requires Cdecl and does not admit SetLastError.", token);
        MethodSignature parsed = ParseMethodSignature(metadata, method.Signature);
        if (parsed.Status != SignatureStatus.Success || parsed.HasThis)
            return PInvokeFailure(parsed.Status == SignatureStatus.Invalid
                    ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.Unsupported,
                parsed.Status == SignatureStatus.Success ? "P/Invoke instance signatures are unsupported." : parsed.Message,
                token);
        HybridCpuInteropValueDescriptorV1[] parameters;
        HybridCpuInteropValueDescriptorV1 result;
        try
        {
            parameters = parsed.Parameters.Select(InteropValue).ToArray();
            result = InteropValue(parsed.ReturnType);
        }
        catch (NotSupportedException exception)
        {
            return PInvokeFailure(RestrictedCilImportStatusV1.Unsupported, exception.Message, token);
        }
        string library = metadata.GetString(metadata.GetModuleReference(import.Module).Name);
        string symbol = import.Name.IsNil ? metadata.GetString(method.Name) : metadata.GetString(import.Name);
        HybridCpuManagedInteropSignatureV1 signature = new HybridCpuManagedInteropSignatureV1(library, symbol,
            HybridCpuInteropCallingConventionV1.HybridCpuNativeV2, parameters, result);
        if (!HybridCpuManagedInteropContractV1.TryValidateSignature(signature, out string reason))
            return PInvokeFailure(RestrictedCilImportStatusV1.Unsupported, reason, token);
        string importSymbol = HybridCpuManagedInteropContractV1.ImportSymbol(signature);
        string digest = PInvokeDigest(token, signature.Library, signature.Symbol,
            HybridCpuManagedInteropContractV1.SignatureDigest(signature), importSymbol);
        return new(RestrictedCilImportStatusV1.Success, string.Empty, token, signature, importSymbol,
            HybridCpuManagedInteropLoweringV1.DispatchHelper, digest);
    }

    private static HybridCpuInteropValueDescriptorV1 InteropValue(RestrictedCilTypeV1 type) => type switch
    {
        RestrictedCilTypeV1.Void => new(HybridCpuInteropValueKindV1.Void, 0, 1),
        RestrictedCilTypeV1.Boolean or RestrictedCilTypeV1.UInt8 =>
            new(HybridCpuInteropValueKindV1.UnsignedInteger, 1, 1),
        RestrictedCilTypeV1.Int8 => new(HybridCpuInteropValueKindV1.SignedInteger, 1, 1),
        RestrictedCilTypeV1.UInt16 => new(HybridCpuInteropValueKindV1.UnsignedInteger, 2, 2),
        RestrictedCilTypeV1.Int16 => new(HybridCpuInteropValueKindV1.SignedInteger, 2, 2),
        RestrictedCilTypeV1.UInt32 => new(HybridCpuInteropValueKindV1.UnsignedInteger, 4, 4),
        RestrictedCilTypeV1.Int32 => new(HybridCpuInteropValueKindV1.SignedInteger, 4, 4),
        RestrictedCilTypeV1.UInt64 => new(HybridCpuInteropValueKindV1.UnsignedInteger, 8, 8),
        RestrictedCilTypeV1.Int64 => new(HybridCpuInteropValueKindV1.SignedInteger, 8, 8),
        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt =>
            new(HybridCpuInteropValueKindV1.NativeInteger, 8, 8),
        _ => throw new NotSupportedException(
            "P/Invoke metadata contains a managed reference, byref or unsupported marshalling shape.")
    };

    private static RestrictedCilPInvokeAdmissionV1 PInvokeFailure(RestrictedCilImportStatusV1 status,
        string reason, int token = 0) => new(status, reason, token, null, string.Empty,
            HybridCpuManagedInteropLoweringV1.DispatchHelper,
            PInvokeDigest(token, string.Empty, string.Empty, string.Empty, reason));

    private static string PInvokeDigest(int token, string library, string symbol,
        string signature, string import) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', HybridCpuManagedInteropContractV1.ContractDigest, token, library, symbol,
                signature, import)))).ToLowerInvariant();
}
