using System;
using System.Collections.Generic;
using System.Threading;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Serializes lowered backend `HybridCpuInstructionBundle` instances into fetch-ready byte images.
    /// </summary>
    public sealed class HybridCpuBundleSerializer
    {
        /// <summary>
        /// Number of bytes in one physical HybridCPU bundle.
        /// </summary>
        public const int BundleSizeBytes = 256;

        /// <summary>
        /// Serializes one backend `HybridCpuInstructionBundle` into a 256-byte image.
        /// </summary>
        public byte[] SerializeBundle(HybridCpuInstructionBundle bundle)
        {
            byte[] serializedBundle = new byte[BundleSizeBytes];
            if (!bundle.TryWriteBytes(serializedBundle))
            {
                throw new InvalidOperationException("Backend bundle serialization failed because the destination buffer was smaller than one physical bundle.");
            }

            return serializedBundle;
        }

        /// <summary>
        /// Serializes a program-order bundle stream into one contiguous fetch-ready image.
        /// </summary>
        public byte[] SerializeProgram(IReadOnlyList<HybridCpuInstructionBundle> bundles)
            => SerializeProgram(CancellationToken.None, bundles);

        public byte[] SerializeProgram(CancellationToken cancellationToken, IReadOnlyList<HybridCpuInstructionBundle> bundles)
        {
            ArgumentNullException.ThrowIfNull(bundles);
            cancellationToken.ThrowIfCancellationRequested();
            if (bundles.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Array.Empty<byte>();
            }

            int imageLength = bundles.Count * BundleSizeBytes;
            cancellationToken.ThrowIfCancellationRequested();
            byte[] programImage = new byte[imageLength];
            for (int bundleIndex = 0; bundleIndex < bundles.Count; bundleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HybridCpuInstructionBundle bundle = bundles[bundleIndex];
                cancellationToken.ThrowIfCancellationRequested();
                if (!bundle.TryWriteBytes(programImage.AsSpan(bundleIndex * BundleSizeBytes, BundleSizeBytes)))
                {
                    throw new InvalidOperationException($"Backend bundle serialization failed for bundle index {bundleIndex}.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return programImage;
        }
    }
}

