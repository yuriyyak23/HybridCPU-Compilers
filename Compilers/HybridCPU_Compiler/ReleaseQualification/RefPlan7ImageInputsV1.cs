using System.Security.Cryptography;

namespace HybridCPU.Compiler.Release;

// Captures exact image bytes. Clean-build flags remain declarations from the producer;
// hashing files does not establish that either build was clean.
internal static class HybridCpuRefPlan7ImageInputsV1
{
    internal static HybridCpuRefPlan7ImageQualificationV1 Capture(
        string sourceDigest, string toolchainDigest, string optionsDigest,
        string imageAPath, bool declaredCleanBuildA, string imageBPath, bool declaredCleanBuildB)
    {
        string imageA = HashImage(imageAPath);
        string imageB = HashImage(imageBPath);
        return new(sourceDigest, toolchainDigest, optionsDigest,
            declaredCleanBuildA, imageA, declaredCleanBuildB, imageB);
    }

    private static string HashImage(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
