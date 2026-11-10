using System;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed partial class SimpleAsmApp
{
    private void InitializeProgramState()
    {
        _coreId = _runtime.CoreId;
        _instructionCount = 0;
        _emittedVirtualThreadIds.Clear();
        ResetProgressState();
        Array.Clear(_instructionSlotMetadata, 0, _instructionSlotMetadata.Length);
        _loopBodyInstructionCount = 0;
        _dynamicRetirementTarget = 0;
        _workloadShape = "single-pass";

        _runtime.BootstrapCompilerRuntime();
    }

    private void PrepareWorkloadState(SimpleAsmAppMode mode)
    {
        SeedMemory();

        switch (mode)
        {
            case SimpleAsmAppMode.WithoutVirtualThreads:
                SeedSyntheticProbeRegisterState(virtualThreadId: 0, registerBase: 4, probeBase: BnmczProbeBase);
                break;
            case SimpleAsmAppMode.SingleThreadNoVector:
                SeedSyntheticProbeRegisterState(virtualThreadId: 0, registerBase: 4, probeBase: LkProbeBase);
                break;
            case SimpleAsmAppMode.WithVirtualThreads:
            case SimpleAsmAppMode.Lk:
                for (byte virtualThreadId = 0; virtualThreadId < 4; virtualThreadId++)
                {
                    SeedSyntheticProbeRegisterState(virtualThreadId, registerBase: 4, probeBase: LkProbeBase);
                }
                break;
            case SimpleAsmAppMode.PackedMixedEnvelope:
            case SimpleAsmAppMode.Bnmcz:
            case SimpleAsmAppMode.RefactorShowcase:
                for (byte virtualThreadId = 0; virtualThreadId < 4; virtualThreadId++)
                {
                    SeedSyntheticProbeRegisterState(virtualThreadId, registerBase: 4, probeBase: BnmczProbeBase);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private void SeedMemory()
    {
        for (int index = 0; index < 32; index++)
        {
            ulong scalarAddress = ScalarDataBase + ((ulong)index * sizeof(ulong));
            ulong vectorSourceAddress = VectorSourceBase + ((ulong)index * sizeof(uint));
            ulong vectorDestinationAddress = VectorDestinationBase + ((ulong)index * sizeof(uint));

            _runtime.WriteMemory(scalarAddress, BitConverter.GetBytes((ulong)(0x100 + index)));
            _runtime.WriteMemory(vectorSourceAddress, BitConverter.GetBytes(index + 1));
            _runtime.WriteMemory(vectorDestinationAddress, BitConverter.GetBytes(0));
        }

        SeedSyntheticProbeMemory(LkProbeBase, linesPerVirtualThread: 32);
        SeedSyntheticProbeMemory(BnmczProbeBase, linesPerVirtualThread: 32);
    }

    /// <summary>
    /// Enable FSP (Formally Safe Packing) and intra-core SMT nomination for
    /// multi-VT diagnostic modes. Activates the runtime injection path so that
    /// <see cref="MicroOpScheduler.PackBundleIntraCoreSmt"/> can fill empty
    /// bundle slots with ready candidates from neighbouring virtual threads.
    /// Single-thread modes leave FSP disabled to preserve baseline comparison.
    /// </summary>
    private void EnableFspForDiagnostics(SimpleAsmAppMode mode)
    {
        bool isMultiVt = mode is SimpleAsmAppMode.WithVirtualThreads
                                or SimpleAsmAppMode.RefactorShowcase
                                or SimpleAsmAppMode.PackedMixedEnvelope
                                or SimpleAsmAppMode.Lk
                                or SimpleAsmAppMode.Bnmcz;

        if (!isMultiVt)
            return;

        // Activate the FSP gate in PipelineStage_Decode
        _runtime.ConfigureFspForDiagnostics();
    }

    private void SeedSyntheticProbeMemory(ulong probeBase, int linesPerVirtualThread)
    {
        for (int vt = 0; vt < 4; vt++)
        {
            ulong virtualThreadBase = probeBase + ((ulong)vt * 0x8000);
            ulong writeBase = virtualThreadBase + 0x2000;

            for (int line = 0; line < linesPerVirtualThread; line++)
            {
                for (int bank = 0; bank < 8; bank++)
                {
                    ulong readAddress = GetBankedAddress(virtualThreadBase, bank, line, wordOffset: 0);
                    ulong readEchoAddress = GetBankedAddress(virtualThreadBase, bank, line, wordOffset: 1);
                    ulong writeAddress = GetBankedAddress(writeBase, bank, line, wordOffset: 0);
                    ulong value = 0x2000UL
                        + ((ulong)vt * 0x400UL)
                        + ((ulong)line * 0x10UL)
                        + (ulong)bank;

                    _runtime.WriteMemory(readAddress, BitConverter.GetBytes(value));
                    _runtime.WriteMemory(readEchoAddress, BitConverter.GetBytes(value ^ 0x55UL));
                    _runtime.WriteMemory(writeAddress, BitConverter.GetBytes(0UL));
                }
            }
        }
    }

    private static ulong GetBankedAddress(ulong baseAddress, int bankId, int lineIndex, int wordOffset)
    {
        return baseAddress
               + ((ulong)lineIndex * 0x400UL)
               + ((ulong)(bankId & 0x7) * 0x40UL)
               + ((ulong)wordOffset * sizeof(ulong));
    }
}
