using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Canonical Stage 5/6 compilation result for one VT-local instruction stream.
    /// </summary>
    public sealed class HybridCpuCompiledProgram
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="HybridCpuCompiledProgram"/> class.
        /// </summary>
        public HybridCpuCompiledProgram(
            IrProgramSchedule programSchedule,
            IrProgramBundlingResult bundleLayout,
            IReadOnlyList<HybridCpuInstructionBundle> loweredBundles,
            byte[] programImage,
            int contractVersion,
            ulong? emissionBaseAddress = null,
            IrAdmissibilityAgreement? admissibilityAgreement = null,
            IReadOnlyList<IrBundleAnnotations>? loweredBundleAnnotations = null,
            IReadOnlyList<IrExternalOperationIntent?>? externalOperationIntents = null,
            IReadOnlyList<IrExternalOperationLoweringMetadata?>? externalOperationMetadata = null)
        {
            ArgumentNullException.ThrowIfNull(programSchedule);
            ArgumentNullException.ThrowIfNull(bundleLayout);
            ArgumentNullException.ThrowIfNull(loweredBundles);
            ArgumentNullException.ThrowIfNull(programImage);

            int expectedImageLength = loweredBundles.Count * HybridCpuBundleSerializer.BundleSizeBytes;
            if (programImage.Length != expectedImageLength)
            {
                throw new ArgumentException($"Serialized image length must match the lowered bundle count ({expectedImageLength} bytes).", nameof(programImage));
            }

            IrAdmissibilityAgreement resolvedAgreement =
                admissibilityAgreement ?? new HybridCpuBundleBuilder().BuildAgreement(bundleLayout);
            if (resolvedAgreement.TotalBundleCount != loweredBundles.Count)
            {
                throw new ArgumentException(
                    $"Admissibility agreement bundle count ({resolvedAgreement.TotalBundleCount}) must match lowered bundle count ({loweredBundles.Count}).",
                    nameof(admissibilityAgreement));
            }

            IReadOnlyList<IrBundleAnnotations> resolvedBundleAnnotations =
                ResolveLoweredBundleAnnotations(
                    loweredBundles.Count,
                    loweredBundleAnnotations);

            ProgramSchedule = programSchedule;
            BundleLayout = bundleLayout;
            LoweredBundles = loweredBundles;
            LoweredBundleAnnotations = resolvedBundleAnnotations;
            ExternalOperationIntents = ResolveExternalOperationIntents(
                bundleLayout.Program.Instructions.Count,
                externalOperationIntents);
            ExternalOperationMetadata = ResolveExternalOperationMetadata(
                bundleLayout.Program.Instructions.Count,
                externalOperationMetadata);
            ProgramImage = programImage;
            AdmissibilityAgreement = resolvedAgreement;
            ContractVersion = contractVersion;
            EmissionBaseAddress = emissionBaseAddress;
        }

        /// <summary>
        /// Gets the Stage 5 schedule consumed by Stage 6.
        /// </summary>
        public IrProgramSchedule ProgramSchedule { get; }

        /// <summary>
        /// Gets the materialized Stage 6 bundle layout.
        /// </summary>
        public IrProgramBundlingResult BundleLayout { get; }

        /// <summary>
        /// Gets the backend-facing lowered bundles in program order.
        /// </summary>
        public IReadOnlyList<HybridCpuInstructionBundle> LoweredBundles { get; }

        /// <summary>
        /// Gets per-lowered-bundle sideband annotations aligned by physical slot.
        /// Descriptor sideband here is transport evidence only and is still revalidated by ISE decode/projector.
        /// </summary>
        public IReadOnlyList<IrBundleAnnotations> LoweredBundleAnnotations { get; }

        /// <summary>Compiler-owned source-indexed semantic intents; never runtime authority or transport metadata.</summary>
        public IReadOnlyList<IrExternalOperationIntent?> ExternalOperationIntents { get; }

        /// <summary>Versioned, compiler-owned semantic correlation for existing external carriers.</summary>
        public IReadOnlyList<IrExternalOperationLoweringMetadata?> ExternalOperationMetadata { get; }

        /// <summary>
        /// Gets the compiled-program-level compiler/runtime agreement summary.
        /// This is diagnostic/build evidence, not runtime legality authority.
        /// </summary>
        public IrAdmissibilityAgreement AdmissibilityAgreement { get; }

        /// <summary>
        /// Gets the contiguous fetch-ready program image.
        /// </summary>
        public byte[] ProgramImage { get; }

        /// <summary>
        /// Gets the compiler/runtime contract version declared by the producer of this artifact.
        /// </summary>
        public int ContractVersion { get; }

        /// <summary>
        /// Gets the emission base address when the canonical compile path also emitted to memory.
        /// </summary>
        public ulong? EmissionBaseAddress { get; }

        /// <summary>
        /// Gets the number of lowered physical bundles.
        /// </summary>
        public int BundleCount => LoweredBundles.Count;

        /// <summary>
        /// Validates this artifact against the active runtime compiler contract.
        /// </summary>
        public void ValidateRuntimeContractCompatibility(string consumerSurface)
        {
            HybridCpuCompilerContract.ThrowIfVersionMismatch(ContractVersion, consumerSurface);
        }

        internal HybridCpuCompiledProgram WithEmissionBaseAddress(ulong emissionBaseAddress)
        {
            return new HybridCpuCompiledProgram(
                ProgramSchedule,
                BundleLayout,
                LoweredBundles,
                ProgramImage,
                ContractVersion,
                emissionBaseAddress,
                AdmissibilityAgreement,
                LoweredBundleAnnotations,
                ExternalOperationIntents,
                ExternalOperationMetadata);
        }

        internal HybridCpuCompiledProgram WithExternalOperationIntents(
            IReadOnlyList<IrExternalOperationIntent?> intents) =>
            new(
                ProgramSchedule,
                BundleLayout,
                LoweredBundles,
                ProgramImage,
                ContractVersion,
                EmissionBaseAddress,
                AdmissibilityAgreement,
                LoweredBundleAnnotations,
                intents,
                ExternalOperationMetadata);

        internal HybridCpuCompiledProgram WithExternalOperationMetadata(
            IReadOnlyList<IrExternalOperationLoweringMetadata?> metadata) =>
            new(
                ProgramSchedule,
                BundleLayout,
                LoweredBundles,
                ProgramImage,
                ContractVersion,
                EmissionBaseAddress,
                AdmissibilityAgreement,
                LoweredBundleAnnotations,
                ResolveIntents(metadata),
                metadata);

        private static IReadOnlyList<IrBundleAnnotations> ResolveLoweredBundleAnnotations(
            int loweredBundleCount,
            IReadOnlyList<IrBundleAnnotations>? loweredBundleAnnotations)
        {
            if (loweredBundleAnnotations is null)
            {
                IrBundleAnnotations[] emptyAnnotations = new IrBundleAnnotations[loweredBundleCount];
                for (int index = 0; index < emptyAnnotations.Length; index++)
                {
                    emptyAnnotations[index] = IrBundleAnnotations.Empty;
                }

                return Array.AsReadOnly(emptyAnnotations);
            }

            if (loweredBundleAnnotations.Count != loweredBundleCount)
            {
                throw new ArgumentException(
                    $"Lowered bundle annotations count ({loweredBundleAnnotations.Count}) must match lowered bundle count ({loweredBundleCount}).",
                    nameof(loweredBundleAnnotations));
            }

            IrBundleAnnotations[] copy = new IrBundleAnnotations[loweredBundleAnnotations.Count];
            for (int index = 0; index < copy.Length; index++)
            {
                copy[index] = loweredBundleAnnotations[index] ?? IrBundleAnnotations.Empty;
            }

            return Array.AsReadOnly(copy);
        }

        private static IReadOnlyList<IrExternalOperationIntent?> ResolveExternalOperationIntents(
            int instructionCount,
            IReadOnlyList<IrExternalOperationIntent?>? intents)
        {
            if (intents is null)
                return Array.AsReadOnly(new IrExternalOperationIntent?[instructionCount]);
            if (intents.Count != instructionCount)
                throw new ArgumentException("External operation intent count must match source instruction count.", nameof(intents));
            IrExternalOperationIntent?[] copy = new IrExternalOperationIntent?[intents.Count];
            for (int index = 0; index < copy.Length; index++) copy[index] = intents[index];
            return Array.AsReadOnly(copy);
        }

        private static IReadOnlyList<IrExternalOperationLoweringMetadata?> ResolveExternalOperationMetadata(
            int instructionCount,
            IReadOnlyList<IrExternalOperationLoweringMetadata?>? metadata)
        {
            if (metadata is null)
                return Array.AsReadOnly(new IrExternalOperationLoweringMetadata?[instructionCount]);
            if (metadata.Count != instructionCount)
                throw new ArgumentException("External operation metadata count must match source instruction count.", nameof(metadata));
            IrExternalOperationLoweringMetadata?[] copy = new IrExternalOperationLoweringMetadata?[metadata.Count];
            for (int index = 0; index < copy.Length; index++)
            {
                IrExternalOperationLoweringMetadata? entry = metadata[index];
                if (entry is null)
                    continue;
                entry.Validate();
                if (entry.SourceInstructionIndex != index)
                    throw new ArgumentException("External operation metadata must be source-index aligned.", nameof(metadata));
                copy[index] = entry;
            }
            return Array.AsReadOnly(copy);
        }

        private static IReadOnlyList<IrExternalOperationIntent?> ResolveIntents(
            IReadOnlyList<IrExternalOperationLoweringMetadata?> metadata)
        {
            IrExternalOperationIntent?[] intents = new IrExternalOperationIntent?[metadata.Count];
            for (int index = 0; index < intents.Length; index++)
                intents[index] = metadata[index]?.Intent;
            return Array.AsReadOnly(intents);
        }
    }
}
