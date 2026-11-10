using System;
using System.Collections.Generic;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

/// <summary>
/// Historical scheduling observation from the untracked diagnostics snapshot. These values are
/// comparison context only until reproduced on an attributable subject.
/// </summary>
public sealed record CompilerHistoricalScheduleBaselineV1(
    int InstructionCount,
    int CycleGroupCount,
    int BundleCount,
    decimal AverageWidth,
    CompilerMetricEvidenceQualityV1 EvidenceQuality,
    string SourceIdentity);

/// <summary>
/// Frozen compiler-input description for one Phase 00 representative workload.
/// Runtime repetition counts and live legality telemetry are deliberately not part of this type.
/// </summary>
public sealed record CompilerRepresentativeInputDescriptorV1(
    string Id,
    string WorkloadShape,
    int ReferenceSliceIterations,
    CompilerHistoricalScheduleBaselineV1 HistoricalBaseline);

/// <summary>
/// Fresh carriers and sideband annotations for one compilation. Arrays are never shared between
/// calls so a benchmark consumer cannot mutate the frozen recipe.
/// </summary>
public sealed record CompilerRepresentativeInputInstanceV1(
    HybridCpuInstructionWord[] Instructions,
    IrBundleAnnotations BundleAnnotations);

/// <summary>
/// Compiler-owned reproduction of the six reference-slice emit recipes from
/// TestAssemblerConsoleApps/SimpleAsmApp.Emit.cs. This corpus performs no runtime execution,
/// publication, admission, FSP, replay, completion, commit or retire operation.
/// </summary>
public static class CompilerPhase00RepresentativeInputCorpusV1
{
    public const string Schema = "CompilerPhase00RepresentativeInputCorpusV1";
    public const string HistoricalSourceIdentity =
        "Documentation/AsmAppTestResults3.md:HistoricalUnverified:2026-08-12";

    private const ulong VectorSourceBase = 0x101000;
    private const ulong VectorDestinationBase = 0x102000;
    private const ulong LkProbeBase = 0x120000;
    private const ulong BnmczProbeBase = 0x140000;

    public static IReadOnlyList<CompilerRepresentativeInputDescriptorV1> Descriptors { get; } =
        Array.AsReadOnly(
        [
            Descriptor("alu", "spec-like-single-thread-int", 36, 185, 60, 3.0833m),
            Descriptor("novt", "spec-like-single-thread-vector", 36, 186, 61, 3.0492m),
            Descriptor("vt", "spec-like-rate-packed-scalar", 8, 164, 36, 4.5556m),
            Descriptor("max", "spec-like-rate-packed-mixed", 8, 165, 37, 4.4595m),
            Descriptor("lk", "spec-like-latency-hiding-memory", 8, 164, 36, 4.5556m),
            Descriptor("bnmcz", "spec-like-bank-rotated-memory", 8, 164, 36, 4.5556m)
        ]);

    public static CompilerRepresentativeInputInstanceV1 Create(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        InputBuilder builder = new InputBuilder();
        switch (profileId)
        {
            case "alu":
                builder.EmitSingleThreadProgram(includeVectorProbe: false, rounds: 36);
                break;
            case "novt":
                builder.EmitSingleThreadProgram(includeVectorProbe: true, rounds: 36);
                break;
            case "vt":
                builder.EmitMultiThreadProgram(includeVectorProbe: false, rounds: 8);
                break;
            case "max":
                builder.EmitMultiThreadProgram(includeVectorProbe: true, rounds: 8);
                break;
            case "lk":
                builder.EmitMemoryStressProgram(bankNoConflict: false, rounds: 8);
                break;
            case "bnmcz":
                builder.EmitMemoryStressProgram(bankNoConflict: true, rounds: 8);
                break;
            default:
                throw new ArgumentException($"Unknown Phase 00 representative profile '{profileId}'.", nameof(profileId));
        }

        return builder.Build();
    }

    private static CompilerRepresentativeInputDescriptorV1 Descriptor(
        string id,
        string workloadShape,
        int referenceSliceIterations,
        int instructionCount,
        int bundleCount,
        decimal averageWidth) =>
        new(
            id,
            workloadShape,
            referenceSliceIterations,
            new CompilerHistoricalScheduleBaselineV1(
                instructionCount,
                bundleCount,
                bundleCount,
                averageWidth,
                CompilerMetricEvidenceQualityV1.HistoricalUnverified,
                HistoricalSourceIdentity));

    private sealed class InputBuilder
    {
        private readonly List<HybridCpuInstructionWord> _instructions = new();
        private readonly List<IrInstructionSlotMetadata> _annotations = new();

        internal CompilerRepresentativeInputInstanceV1 Build() =>
            new(
                _instructions.ToArray(),
                new IrBundleAnnotations(_annotations.ToArray()));

        internal void EmitSingleThreadProgram(bool includeVectorProbe, int rounds)
        {
            const byte virtualThreadId = 0;
            ulong probeBase = includeVectorProbe ? BnmczProbeBase : LkProbeBase;
            for (int round = 0; round < rounds; round++)
            {
                int destinationRegisterBase = 12 + ((round & 0x3) * 4);
                EmitSingleThreadSpecLikeRound(virtualThreadId, 4, destinationRegisterBase, round);
                EmitSingleThreadMemoryRound(virtualThreadId, 4, probeBase, round);
                if (ShouldEmitSchedulingWindowFence(round, rounds, windowRounds: 8))
                {
                    EmitFence(virtualThreadId);
                }
            }

            if (includeVectorProbe)
            {
                EmitVectorLoadProbe(virtualThreadId);
            }

            EmitFence(virtualThreadId);
        }

        internal void EmitMultiThreadProgram(bool includeVectorProbe, int rounds)
        {
            ulong probeBase = includeVectorProbe ? BnmczProbeBase : LkProbeBase;
            int bankStride = includeVectorProbe ? 2 : 3;
            for (int round = 0; round < rounds; round++)
            {
                EmitCryptoLikeRound(0, 4, 12 + ((round & 0x3) * 4), round);
                EmitPolynomialLikeRound(1, 4, 12 + (((round + 1) & 0x3) * 4), round);
                EmitAddressCalculationRound(2, 4, 12 + (((round + 2) & 0x3) * 4), round);
                EmitBaselineScalarRound(3, 4, 12 + (((round + 3) & 0x3) * 4), round);
                EmitPackedSpecLikeMemoryTrafficRound(probeBase, round, bankStride);
                if (ShouldEmitSchedulingWindowFence(round, rounds, windowRounds: 2))
                {
                    EmitFence(0);
                }
            }

            if (includeVectorProbe)
            {
                EmitVectorLoadProbe(2);
            }

            EmitFence(0);
        }

        internal void EmitMemoryStressProgram(bool bankNoConflict, int rounds)
        {
            ulong probeBase = bankNoConflict ? BnmczProbeBase : LkProbeBase;
            int bankStride = bankNoConflict ? 2 : 3;
            for (int round = 0; round < rounds; round++)
            {
                for (byte virtualThreadId = 0; virtualThreadId < 4; virtualThreadId++)
                {
                    int scalarDestBase = 12 + (((round + virtualThreadId) & 0x3) * 4);
                    EmitIndependentScalarQuad(virtualThreadId, 4, scalarDestBase);
                }

                byte loadVt = (byte)(round & 0x3);
                byte storeVt = (byte)((round + 2) & 0x3);
                byte secondLoadVt = (byte)((round + 1) & 0x3);
                byte secondStoreVt = (byte)((round + 3) & 0x3);
                int lineIndex = round & 0x1F;
                int loadBank = ((round * bankStride) + loadVt + 1) & 0x7;
                int storeBank = ((round * bankStride) + storeVt + (bankNoConflict ? 5 : 4)) & 0x7;
                int secondLoadBank = ((round * bankStride) + secondLoadVt + (bankNoConflict ? 3 : 2)) & 0x7;
                int secondStoreBank = ((round * bankStride) + secondStoreVt + (bankNoConflict ? 7 : 6)) & 0x7;
                int secondLoadOffset = bankNoConflict ? 0 : 1;

                EmitTypedLoad(
                    loadVt,
                    28 + (round & 0x3),
                    4,
                    GetBankedAddress(probeBase + ((ulong)loadVt * 0x8000), loadBank, lineIndex, bankNoConflict ? 1 : 0));
                EmitTypedStore(
                    storeVt,
                    4,
                    12 + (((round + storeVt) & 0x3) * 4),
                    GetBankedAddress(probeBase + ((ulong)storeVt * 0x8000) + 0x2000, storeBank, lineIndex, 0));
                EmitTypedLoad(
                    secondLoadVt,
                    28 + ((round + (bankNoConflict ? 2 : 1)) & 0x3),
                    4,
                    GetBankedAddress(probeBase + ((ulong)secondLoadVt * 0x8000), secondLoadBank, lineIndex, secondLoadOffset));
                EmitTypedStore(
                    secondStoreVt,
                    4,
                    12 + (((round + secondStoreVt) & 0x3) * 4),
                    GetBankedAddress(
                        probeBase + ((ulong)secondStoreVt * 0x8000) + 0x2000,
                        secondStoreBank,
                        lineIndex,
                        bankNoConflict ? 1 : 1));

                if (ShouldEmitSchedulingWindowFence(round, rounds, windowRounds: 2))
                {
                    EmitFence(0);
                }
            }

            EmitFence(0);
        }

        private void EmitSingleThreadSpecLikeRound(byte virtualThreadId, int registerBase, int destinationRegisterBase, int round)
        {
            switch (round & 0x3)
            {
                case 0:
                    EmitCryptoLikeRound(virtualThreadId, registerBase, destinationRegisterBase, round);
                    break;
                case 1:
                    EmitPolynomialLikeRound(virtualThreadId, registerBase, destinationRegisterBase, round);
                    break;
                case 2:
                    EmitAddressCalculationRound(virtualThreadId, registerBase, destinationRegisterBase, round);
                    break;
                default:
                    EmitBaselineScalarRound(virtualThreadId, registerBase, destinationRegisterBase, round);
                    break;
            }
        }

        private void EmitCryptoLikeRound(byte vt, int registerBase, int destinationBase, int round)
        {
            int rotateRegister = registerBase + 1 + (round & 0x3);
            EmitBinaryScalar(vt, HybridCpuOpcode.ADD, destinationBase, registerBase + 1, registerBase + 2);
            EmitBinaryScalar(vt, HybridCpuOpcode.XOR, destinationBase + 1, registerBase + 3, registerBase + 4);
            EmitBinaryScalar(vt, HybridCpuOpcode.SLL, destinationBase + 2, registerBase + 5, rotateRegister);
            EmitBinaryScalar(vt, HybridCpuOpcode.AND, destinationBase + 3, registerBase + 6, registerBase + 7);
        }

        private void EmitPolynomialLikeRound(byte vt, int registerBase, int destinationBase, int round)
        {
            int rotateRegister = registerBase + 1 + ((round + 1) & 0x3);
            EmitBinaryScalar(vt, HybridCpuOpcode.MUL, destinationBase, registerBase + 1, registerBase + 2);
            EmitBinaryScalar(vt, HybridCpuOpcode.ADD, destinationBase + 1, registerBase + 3, registerBase + 4);
            EmitBinaryScalar(vt, HybridCpuOpcode.MUL, destinationBase + 2, registerBase + 5, registerBase + 6);
            EmitBinaryScalar(vt, HybridCpuOpcode.ADD, destinationBase + 3, registerBase + 7, rotateRegister);
        }

        private void EmitAddressCalculationRound(byte vt, int registerBase, int destinationBase, int round)
        {
            int rotateRegister = registerBase + 1 + (round & 0x3);
            EmitBinaryScalar(vt, HybridCpuOpcode.SLL, destinationBase, rotateRegister, registerBase + 2);
            EmitBinaryScalar(vt, HybridCpuOpcode.ADD, destinationBase + 1, registerBase, registerBase + 3);
            EmitBinaryScalar(vt, HybridCpuOpcode.XOR, destinationBase + 2, registerBase + 4, registerBase + 5);
            EmitBinaryScalar(vt, HybridCpuOpcode.OR, destinationBase + 3, registerBase + 6, registerBase + 7);
        }

        private void EmitBaselineScalarRound(byte vt, int registerBase, int destinationBase, int round)
        {
            int rotateRegister = registerBase + 1 + ((round + 2) & 0x3);
            EmitBinaryScalar(vt, HybridCpuOpcode.ADD, destinationBase, registerBase + 1, registerBase + 2);
            EmitBinaryScalar(vt, HybridCpuOpcode.XOR, destinationBase + 1, registerBase + 3, rotateRegister);
            EmitBinaryScalar(vt, HybridCpuOpcode.OR, destinationBase + 2, registerBase + 5, registerBase + 6);
            EmitBinaryScalar(vt, HybridCpuOpcode.MUL, destinationBase + 3, registerBase + 2, registerBase + 7);
        }

        private void EmitSingleThreadMemoryRound(byte vt, int registerBase, ulong probeBase, int round)
        {
            int bankId = ((round * 3) + 1) & 0x7;
            int wordOffset = (round >> 1) & 0x1;
            if ((round & 0x1) == 0)
            {
                EmitTypedLoad(vt, 28 + (round & 0x3), registerBase, GetBankedAddress(probeBase, bankId, round & 0x1F, wordOffset));
            }
            else
            {
                EmitTypedStore(
                    vt,
                    registerBase,
                    registerBase + 1 + (round & 0x3),
                    GetBankedAddress(probeBase + 0x2000, bankId, round & 0x1F, wordOffset));
            }
        }

        private void EmitPackedSpecLikeMemoryTrafficRound(ulong probeBase, int round, int bankStride)
        {
            byte loadVt = (byte)(round & 0x3);
            byte storeVt = (byte)((round + 2) & 0x3);
            byte secondLoadVt = (byte)((round + 1) & 0x3);
            byte secondStoreVt = (byte)((round + 3) & 0x3);
            int lineIndex = round & 0x1F;
            int firstOffset = round & 0x1;
            int secondOffset = (round + 1) & 0x1;
            EmitTypedLoad(
                loadVt,
                28 + (round & 0x3),
                4,
                GetBankedAddress(probeBase + ((ulong)loadVt * 0x8000), ((round * bankStride) + loadVt + 1) & 0x7, lineIndex, firstOffset));
            EmitTypedStore(
                storeVt,
                4,
                5 + ((round + storeVt) & 0x3),
                GetBankedAddress(probeBase + ((ulong)storeVt * 0x8000) + 0x2000, ((round * bankStride) + storeVt + 4) & 0x7, lineIndex, secondOffset));
            EmitTypedLoad(
                secondLoadVt,
                28 + ((round + 1) & 0x3),
                4,
                GetBankedAddress(probeBase + ((ulong)secondLoadVt * 0x8000), ((round * bankStride) + secondLoadVt + 2) & 0x7, lineIndex, secondOffset));
            EmitTypedStore(
                secondStoreVt,
                4,
                5 + ((round + secondStoreVt + 1) & 0x3),
                GetBankedAddress(probeBase + ((ulong)secondStoreVt * 0x8000) + 0x2000, ((round * bankStride) + secondStoreVt + 6) & 0x7, lineIndex, firstOffset));
        }

        private void EmitIndependentScalarQuad(byte vt, int registerBase, int destinationBase)
        {
            EmitBinaryScalar(vt, HybridCpuOpcode.ADD, destinationBase, registerBase + 1, registerBase + 2);
            EmitBinaryScalar(vt, HybridCpuOpcode.XOR, destinationBase + 1, registerBase + 3, registerBase + 4);
            EmitBinaryScalar(vt, HybridCpuOpcode.OR, destinationBase + 2, registerBase + 5, registerBase + 6);
            EmitBinaryScalar(vt, HybridCpuOpcode.MUL, destinationBase + 3, registerBase + 2, registerBase + 7);
        }

        private void EmitVectorLoadProbe(byte vt) =>
            Emit(vt, HybridCpuOpcode.VLOAD, (byte)HybridCpuDataType.INT32, 0, VectorDestinationBase, VectorSourceBase, 8, 4, canBeStolen: false);

        private void EmitFence(byte vt) =>
            Emit(vt, HybridCpuOpcode.FENCE, 0, 0, 0, 0, 0, 0, canBeStolen: false);

        private void EmitTypedLoad(byte vt, int destination, int baseRegister, ulong address) =>
            Emit(
                vt,
                HybridCpuOpcode.LD,
                0,
                0,
                HybridCpuInstructionWord.PackArchRegs(checked((byte)destination), checked((byte)baseRegister), HybridCpuInstructionWord.NoArchReg),
                address,
                0,
                0,
                canBeStolen: true);

        private void EmitTypedStore(byte vt, int baseRegister, int source, ulong address) =>
            Emit(
                vt,
                HybridCpuOpcode.SD,
                0,
                0,
                HybridCpuInstructionWord.PackArchRegs(HybridCpuInstructionWord.NoArchReg, checked((byte)baseRegister), checked((byte)source)),
                address,
                0,
                0,
                canBeStolen: true);

        private void EmitBinaryScalar(byte vt, HybridCpuOpcode opcode, int destination, int source1, int source2) =>
            Emit(
                vt,
                opcode,
                0,
                0,
                HybridCpuInstructionWord.PackArchRegs(checked((byte)destination), checked((byte)source1), checked((byte)source2)),
                0,
                0,
                0,
                canBeStolen: true);

        private void Emit(
            byte vt,
            HybridCpuOpcode opcode,
            byte dataType,
            ushort immediate,
            ulong destSrc1,
            ulong src2,
            ulong streamLength,
            ushort stride,
            bool canBeStolen)
        {
            _instructions.Add(new HybridCpuInstructionWord
            {
                OpCode = (uint)opcode,
                DataType = dataType,
                PredicateMask = 0,
                Immediate = immediate,
                DestSrc1Pointer = destSrc1,
                Src2Pointer = src2,
                StreamLength = checked((uint)streamLength),
                Stride = stride
            });
            _annotations.Add(new IrInstructionSlotMetadata(
                vt,
                IrSlotClass.Unclassified,
                IrSlotBindingKind.ClassFlexible,
                0,
                0,
                canBeStolen));
        }

        private static bool ShouldEmitSchedulingWindowFence(int round, int totalRounds, int windowRounds) =>
            round + 1 < totalRounds && ((round + 1) % windowRounds) == 0;

        private static ulong GetBankedAddress(ulong baseAddress, int bankId, int lineIndex, int wordOffset) =>
            baseAddress + ((ulong)lineIndex * 0x400) + ((ulong)(bankId & 0x7) * 0x40) + ((ulong)wordOffset * sizeof(ulong));
    }
}
