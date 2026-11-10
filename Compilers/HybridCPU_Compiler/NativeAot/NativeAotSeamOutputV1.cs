using System.Text.Json;
using System.Threading;

namespace HybridCPU.Compiler.NativeAot;

/// <summary>Owns the existing seam artifact/manifest write sequence and optional post-write sideband.</summary>
internal static class NativeAotSeamOutputV1
{
    // Synchronous existing write sequence, not an atomic filesystem transaction.
    internal static int Write(
        string outputPath, byte[] output, object manifest, SingNextPclEmissionContextV1? pclContext,
        CancellationToken cancellationToken = default)
    {
        // Last cooperative checkpoint. Once writing starts, finish the existing pair/PCL
        // sequence without observing later cancellation; this is not atomic I/O.
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllBytes(outputPath, output);
        File.WriteAllText(outputPath + ".json",
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        if (!SingNextPclSidebandV1.TryEmit(pclContext, out string? diagnostic))
        {
            RejectBestEffort(outputPath);
            Console.Error.WriteLine(diagnostic);
            return 14;
        }
        return 0;
    }

    internal static void RejectBestEffort(string outputPath)
    {
        try { File.Delete(outputPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        try { File.Delete(outputPath + ".json"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
