namespace HybridCPU.Platform.Contracts;

/// <summary>Exact synchronous framebuffer service operations used by managed guests.</summary>
public static class HybridCpuFramebufferServiceContractV1
{
    public const string SchemaId = "hybridcpu.framebuffer-service/v1";
    public const ulong InitializeOperation = 0x4642_494e; // FBIN
    public const ulong PresentOperation = 0x4650_5245; // FPRE
    public const ulong UpdatePaletteOperation = 0x4650_414c; // FPAL
    public const int PaletteBytes = 256 * 3;
    public const uint MaximumDimension = 16384;
    public static string ContractDigest { get; } = HybridCpuPlatformContractV1.Hash(
        $"{SchemaId}|init:{InitializeOperation}|present:{PresentOperation}|palette:{UpdatePaletteOperation}|args=packed-u32-width-height|present-buffer=read-u8:exact-width*height|palette-buffer=read-rgb24:{PaletteBytes}|result=void|dimension=1..{MaximumDimension}|sync|no-reentry|gc=deferred-nonmoving");

    public static ulong PackDimensions(int width, int height) =>
        (uint)width | ((ulong)(uint)height << 32);

    public static bool TryUnpackDimensions(ulong packed, out int width, out int height)
    {
        width = unchecked((int)(uint)packed);
        height = unchecked((int)(uint)(packed >> 32));
        return width > 0 && height > 0 && (uint)width <= MaximumDimension && (uint)height <= MaximumDimension;
    }
}
