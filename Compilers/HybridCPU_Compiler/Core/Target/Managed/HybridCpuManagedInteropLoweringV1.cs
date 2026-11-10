using System.Security.Cryptography;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public sealed record HybridCpuManagedInteropThunkPlanV1(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    HybridCpuManagedInteropSignatureV1 Signature,
    string ImportSymbol,
    string DispatchHelper,
    HybridCpuAbiLayoutV2? NativeLayout,
    IReadOnlyList<string> Steps,
    string PlanDigest)
{
    public bool IsSupported => Status == HybridCpuPlatformFactStatus.Supported;
}

/// <summary>Compiler-owned closed lowering from an admitted managed interop signature to native ABI v2.</summary>
public static class HybridCpuManagedInteropLoweringV1
{
    public const string DispatchHelper = "__hybridcpu_managed_pinvoke_dispatch";

    public static HybridCpuManagedInteropThunkPlanV1 Plan(HybridCpuManagedInteropSignatureV1 signature)
    {
        if (!HybridCpuManagedInteropContractV1.TryValidateSignature(signature, out string reason))
            return Failure(signature, reason);
        HybridCpuAbiValueV2[] parameters = signature.Parameters.Select((value, index) =>
            AbiValue($"arg{index}", value)).ToArray();
        HybridCpuAbiValueV2? returnValue = signature.ReturnValue.Kind == HybridCpuInteropValueKindV1.Void
            ? null : AbiValue("return", signature.ReturnValue);
        HybridCpuAbiLayoutV2 layout = HybridCpuNativeAbiContractV2.Default.Classify(
            new(parameters, returnValue));
        if (layout.Status != HybridCpuPlatformFactStatus.Supported)
            return Failure(signature, layout.Reason);
        string import = HybridCpuManagedInteropContractV1.ImportSymbol(signature);
        string[] steps =
        [
            "validate-exact-signature-and-symbol",
            "pin-explicit-managed-object-arguments",
            "publish-native-call-roots",
            "enter-native-transition-state",
            $"marshal-blittable-to:{layout.Digest}",
            $"call:{DispatchHelper}:{import}",
            "contain-native-boundary-exception",
            "leave-native-transition-state",
            "release-pins",
            "return-bit-exact-result"
        ];
        return new(HybridCpuPlatformFactStatus.Supported, string.Empty, signature, import, DispatchHelper,
            layout, steps, Digest(signature, import, layout.Digest, steps));
    }

    private static HybridCpuAbiValueV2 AbiValue(string identity, HybridCpuInteropValueDescriptorV1 value) =>
        new(identity, value.Kind switch
        {
            HybridCpuInteropValueKindV1.RawPointer or HybridCpuInteropValueKindV1.BufferPointer =>
                HybridCpuAbiValueKindV2.Pointer,
            HybridCpuInteropValueKindV1.FixedLayoutStruct => HybridCpuAbiValueKindV2.Aggregate,
            _ => HybridCpuAbiValueKindV2.Integer
        }, value.SizeBytes, value.AlignmentBytes);

    private static HybridCpuManagedInteropThunkPlanV1 Failure(HybridCpuManagedInteropSignatureV1 signature,
        string reason) => new(HybridCpuPlatformFactStatus.Unsupported, reason, signature, string.Empty,
            DispatchHelper, null, [], Digest(signature, string.Empty, string.Empty, [reason]));

    private static string Digest(HybridCpuManagedInteropSignatureV1 signature, string import,
        string layout, IReadOnlyList<string> steps) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', HybridCpuManagedInteropContractV1.ContractDigest, signature?.Library,
                signature?.Symbol, import, layout, string.Join(';', steps))))).ToLowerInvariant();
}
