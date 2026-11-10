using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target.Managed;

namespace HybridCPU.Compiler.NativeAot;

/// <summary>
/// Versioned DoomSharp guest-ABI package. Application identities live here rather than in the
/// generic CIL importer; the importer receives only validated signature-to-helper bindings.
/// </summary>
public static class DoomSharpGuestAbiBindingPackageV1
{
    public const string Schema = "hybridcpu.guest-abi-binding/v1";
    public const string PackageIdentity = "doomsharp.hybridcpu.guest-services/v1";
    private const string Prefix =
        "runtime-external-interface:DoomSharp.HybridCpu.Guest.IHybridCpuGuestServices.";

    public static IReadOnlyList<RestrictedCilRuntimeExternalBindingV1> Bindings { get; } =
    [
        Binding("GetMonotonicDoomTics(System.Object):System.Int32", [RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Int32, HybridCpuManagedDoomClockEmitterV1.Symbol),
        Binding("InitializeFramebuffer(System.Object,System.Int32,System.Int32):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.Void, HybridCpuManagedFramebufferEmitterV1.Symbol),
        Binding("ConsoleWrite(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void, HybridCpuManagedConsoleWriteEmitterV1.Symbol),
        Binding("WaitUntilDoomTic(System.Object,System.Int32):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.Void, HybridCpuManagedDoomWaitEmitterV1.Symbol),
        Binding("GetBootBlob(System.Object,System.Int32):System.Object",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.ObjectReference, HybridCpuManagedBootBlobEmitterV1.Symbol),
        Binding("ProcessExit(System.Object,System.Int32):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
            RestrictedCilTypeV1.Void, HybridCpuManagedGuestProcessExitEmitterV1.Symbol),
        Binding("PresentFramebuffer(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void, HybridCpuManagedFramebufferPresentEmitterV1.Symbol),
        Binding("ConsoleSetTitle(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void, HybridCpuManagedConsoleTitleEmitterV1.Symbol),
        Binding("PullInput(System.Object):System.Object", [RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.ObjectReference, HybridCpuManagedInputPullEmitterV1.Symbol),
        Binding("UpdatePalette(System.Object,System.Object):System.Void",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference],
            RestrictedCilTypeV1.Void, HybridCpuManagedPaletteEmitterV1.Symbol)
    ];

    public static string ContractDigest { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', Schema, PackageIdentity, Bindings.OrderBy(static row => row.StableIdentity, StringComparer.Ordinal)
            .Select(static row => $"{row.StableIdentity}:{string.Join(',', row.ParameterTypes)}:{row.ReturnType}:{row.ExactImplementationIdentity}"))))).ToLowerInvariant();

    private static RestrictedCilRuntimeExternalBindingV1 Binding(
        string suffix,
        IReadOnlyList<RestrictedCilTypeV1> parameters,
        RestrictedCilTypeV1 returnType,
        string symbol) => new(Prefix + suffix, parameters, returnType, symbol);
}
