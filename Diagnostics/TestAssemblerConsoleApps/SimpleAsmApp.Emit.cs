using System;
using HybridCPU.Compiler.Core.IR;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed partial class SimpleAsmApp
{
    private const int SingleThreadSchedulingWindowRounds = 8;
    private const int MultiThreadSchedulingWindowRounds = 2;
    private const int ShowcaseSchedulingWindowRounds = 2;
    private const int MemoryStressSchedulingWindowRounds = 2;

    private void EmitDiagnosticProgram(SimpleAsmAppMode mode, ulong sliceIterations)
    {
        int rounds = checked((int)sliceIterations);

        switch (mode)
        {
            case SimpleAsmAppMode.RefactorShowcase:
                _programVariant = SimpleAsmProgramVariant.RefactorShowcaseComposite;
                EmitRefactorShowcaseProgram(rounds);
                break;
            case SimpleAsmAppMode.WithoutVirtualThreads:
                _programVariant = SimpleAsmProgramVariant.NativeVliwVectorProbe;
                EmitSingleThreadProgram(includeVectorProbe: true, rounds);
                break;
            case SimpleAsmAppMode.WithVirtualThreads:
                _programVariant = SimpleAsmProgramVariant.NativeVliwPackedScalar;
                EmitMultiThreadProgram(includeVectorProbe: false, rounds);
                break;
            case SimpleAsmAppMode.SingleThreadNoVector:
                _programVariant = SimpleAsmProgramVariant.NativeVliwSingleThread;
                EmitSingleThreadProgram(includeVectorProbe: false, rounds);
                break;
            case SimpleAsmAppMode.PackedMixedEnvelope:
                _programVariant = SimpleAsmProgramVariant.NativeVliwPackedMixedEnvelope;
                EmitMultiThreadProgram(includeVectorProbe: true, rounds);
                break;
            case SimpleAsmAppMode.Lk:
                _programVariant = SimpleAsmProgramVariant.NativeVliwLatencyHidingLoadKernel;
                EmitLatencyHidingLoadKernelProgram(rounds);
                break;
            case SimpleAsmAppMode.Bnmcz:
                _programVariant = SimpleAsmProgramVariant.NativeVliwBankNoConflictMixedZoo;
                EmitBankNoConflictMixedZooProgram(rounds);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        _loopBodyInstructionCount = _instructionCount;
    }

    private void EmitSingleThreadProgram(bool includeVectorProbe, int rounds)
    {
        const byte virtualThreadId = 0;
        ulong probeBase = includeVectorProbe ? BnmczProbeBase : LkProbeBase;
        _workloadShape = includeVectorProbe
            ? "spec-like-single-thread-vector"
            : "spec-like-single-thread-int";

        for (int round = 0; round < rounds; round++)
        {
            int destinationRegisterBase = 12 + ((round & 0x3) * 4);
            EmitSingleThreadSpecLikeRound(virtualThreadId, registerBase: 4, destinationRegisterBase, round);
            EmitSingleThreadMemoryRound(virtualThreadId, registerBase: 4, probeBase, round);

            if (ShouldEmitSchedulingWindowFence(round, rounds, SingleThreadSchedulingWindowRounds))
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

    private void EmitMultiThreadProgram(bool includeVectorProbe, int rounds)
    {
        ulong probeBase = includeVectorProbe ? BnmczProbeBase : LkProbeBase;
        int bankStride = includeVectorProbe ? 2 : 3;
        _workloadShape = includeVectorProbe
            ? "spec-like-rate-packed-mixed"
            : "spec-like-rate-packed-scalar";

        for (int round = 0; round < rounds; round++)
        {
            EmitCryptoLikeRound(0, registerBase: 4, destinationRegisterBase: 12 + (((round + 0) & 0x3) * 4), round);
            EmitPolynomialLikeRound(1, registerBase: 4, destinationRegisterBase: 12 + (((round + 1) & 0x3) * 4), round);
            EmitAddressCalculationRound(2, registerBase: 4, destinationRegisterBase: 12 + (((round + 2) & 0x3) * 4), round);
            EmitBaselineScalarRound(3, registerBase: 4, destinationRegisterBase: 12 + (((round + 3) & 0x3) * 4), round);
            EmitPackedSpecLikeMemoryTrafficRound(probeBase, round, bankStride);

            if (ShouldEmitSchedulingWindowFence(round, rounds, MultiThreadSchedulingWindowRounds))
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

    private void EmitRefactorShowcaseProgram(int rounds)
    {
        _workloadShape = "spec-like-showcase-composite";

        for (int round = 0; round < rounds; round++)
        {
            EmitCryptoLikeRound(0, registerBase: 4, destinationRegisterBase: 12 + (((round + 0) & 0x3) * 4), round);
            EmitPolynomialLikeRound(1, registerBase: 4, destinationRegisterBase: 12 + (((round + 1) & 0x3) * 4), round);
            EmitAddressCalculationRound(2, registerBase: 4, destinationRegisterBase: 12 + (((round + 2) & 0x3) * 4), round);
            EmitBaselineScalarRound(3, registerBase: 4, destinationRegisterBase: 12 + (((round + 3) & 0x3) * 4), round);

            ulong probeBase = (round & 0x1) == 0 ? BnmczProbeBase : LkProbeBase;
            int bankStride = (round & 0x1) == 0 ? 2 : 3;
            EmitPackedSpecLikeMemoryTrafficRound(probeBase, round, bankStride);

            if ((round & 0x7) == 0)
            {
                EmitVectorLoadProbe((byte)((round >> 3) & 0x3));
            }

            if ((round & 0xF) == 7)
            {
                EmitStreamWaitRound((byte)((round >> 2) & 0x3));
            }

            if (ShouldEmitSchedulingWindowFence(round, rounds, ShowcaseSchedulingWindowRounds))
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

    private void EmitCryptoLikeRound(byte virtualThreadId, int registerBase, int destinationRegisterBase, int round)
    {
        int rotateRegister = registerBase + 1 + (round & 0x3);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.ADD, destinationRegisterBase, registerBase + 1, registerBase + 2);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.XOR, destinationRegisterBase + 1, registerBase + 3, registerBase + 4);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.SLL, destinationRegisterBase + 2, registerBase + 5, rotateRegister);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.AND, destinationRegisterBase + 3, registerBase + 6, registerBase + 7);
    }

    private void EmitPolynomialLikeRound(byte virtualThreadId, int registerBase, int destinationRegisterBase, int round)
    {
        int rotateRegister = registerBase + 1 + ((round + 1) & 0x3);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.MUL, destinationRegisterBase, registerBase + 1, registerBase + 2);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.ADD, destinationRegisterBase + 1, registerBase + 3, registerBase + 4);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.MUL, destinationRegisterBase + 2, registerBase + 5, registerBase + 6);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.ADD, destinationRegisterBase + 3, registerBase + 7, rotateRegister);
    }

    private void EmitAddressCalculationRound(byte virtualThreadId, int registerBase, int destinationRegisterBase, int round)
    {
        int rotateRegister = registerBase + 1 + (round & 0x3);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.SLL, destinationRegisterBase, rotateRegister, registerBase + 2);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.ADD, destinationRegisterBase + 1, registerBase, registerBase + 3);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.XOR, destinationRegisterBase + 2, registerBase + 4, registerBase + 5);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.OR, destinationRegisterBase + 3, registerBase + 6, registerBase + 7);
    }

    private void EmitBaselineScalarRound(byte virtualThreadId, int registerBase, int destinationRegisterBase, int round)
    {
        int rotateRegister = registerBase + 1 + ((round + 2) & 0x3);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.ADD, destinationRegisterBase, registerBase + 1, registerBase + 2);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.XOR, destinationRegisterBase + 1, registerBase + 3, rotateRegister);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.OR, destinationRegisterBase + 2, registerBase + 5, registerBase + 6);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.MUL, destinationRegisterBase + 3, registerBase + 2, registerBase + 7);
    }

    private void EmitSingleThreadMemoryRound(byte virtualThreadId, int registerBase, ulong probeBase, int round)
    {
        int lineIndex = round & 0x1F;
        int bankId = ((round * 3) + 1) & 0x7;
        int wordOffset = (round >> 1) & 0x1;

        if ((round & 0x1) == 0)
        {
            int loadRegister = 28 + (round & 0x3);
            ulong readAddress = GetBankedAddress(probeBase, bankId, lineIndex, wordOffset);
            EmitTypedLoad(virtualThreadId, loadRegister, registerBase, readAddress);
            return;
        }

        int storeSourceRegister = registerBase + 1 + (round & 0x3);
        ulong writeAddress = GetBankedAddress(probeBase + 0x2000, bankId, lineIndex, wordOffset);
        EmitTypedStore(virtualThreadId, registerBase, storeSourceRegister, writeAddress);
    }

    private void EmitPackedSpecLikeMemoryTrafficRound(ulong probeBase, int round, int bankStride)
    {
        int lineIndex = round & 0x1F;
        byte loadVirtualThreadId = (byte)(round & 0x3);
        byte storeVirtualThreadId = (byte)((round + 2) & 0x3);
        byte secondLoadVirtualThreadId = (byte)((round + 1) & 0x3);
        byte secondStoreVirtualThreadId = (byte)((round + 3) & 0x3);
        ulong loadThreadBase = probeBase + ((ulong)loadVirtualThreadId * 0x8000);
        ulong storeThreadBase = probeBase + ((ulong)storeVirtualThreadId * 0x8000) + 0x2000;
        ulong secondLoadThreadBase = probeBase + ((ulong)secondLoadVirtualThreadId * 0x8000);
        ulong secondStoreThreadBase = probeBase + ((ulong)secondStoreVirtualThreadId * 0x8000) + 0x2000;
        int loadBankId = ((round * bankStride) + loadVirtualThreadId + 1) & 0x7;
        int storeBankId = ((round * bankStride) + storeVirtualThreadId + 4) & 0x7;
        int secondLoadBankId = ((round * bankStride) + secondLoadVirtualThreadId + 2) & 0x7;
        int secondStoreBankId = ((round * bankStride) + secondStoreVirtualThreadId + 6) & 0x7;
        int loadRegister = 28 + (round & 0x3);
        int secondLoadRegister = 28 + ((round + 1) & 0x3);
        int storeSourceRegister = 5 + ((round + storeVirtualThreadId) & 0x3);
        int secondStoreSourceRegister = 5 + ((round + secondStoreVirtualThreadId + 1) & 0x3);
        int firstWordOffset = round & 0x1;
        int secondWordOffset = (round + 1) & 0x1;

        EmitTypedLoad(
            loadVirtualThreadId,
            loadRegister,
            4,
            GetBankedAddress(loadThreadBase, loadBankId, lineIndex, firstWordOffset));
        EmitTypedStore(
            storeVirtualThreadId,
            4,
            storeSourceRegister,
            GetBankedAddress(storeThreadBase, storeBankId, lineIndex, secondWordOffset));
        EmitTypedLoad(
            secondLoadVirtualThreadId,
            secondLoadRegister,
            4,
            GetBankedAddress(secondLoadThreadBase, secondLoadBankId, lineIndex, secondWordOffset));
        EmitTypedStore(
            secondStoreVirtualThreadId,
            4,
            secondStoreSourceRegister,
            GetBankedAddress(secondStoreThreadBase, secondStoreBankId, lineIndex, firstWordOffset));
    }

    private void SetIntRegisterValue(byte virtualThreadId, int registerId, ulong value)
    {
        _runtime.WriteCommittedRegister(virtualThreadId, registerId, value);
    }

    private void EmitLatencyHidingLoadKernelProgram(int rounds)
    {
        _workloadShape = "spec-like-latency-hiding-memory";

        for (int round = 0; round < rounds; round++)
        {
            int lineIndex = round & 0x1F;
            for (byte virtualThreadId = 0; virtualThreadId < 4; virtualThreadId++)
            {
                int scalarDestBase = 12 + (((round + virtualThreadId) & 0x3) * 4);
                EmitIndependentScalarQuad(virtualThreadId, registerBase: 4, scalarDestBase);
            }

            byte loadVirtualThreadId = (byte)(round & 0x3);
            byte storeVirtualThreadId = (byte)((round + 2) & 0x3);
            byte secondLoadVirtualThreadId = (byte)((round + 1) & 0x3);
            byte secondStoreVirtualThreadId = (byte)((round + 3) & 0x3);
            ulong virtualThreadBase = LkProbeBase + ((ulong)loadVirtualThreadId * 0x8000);
            ulong writeThreadBase = LkProbeBase + ((ulong)storeVirtualThreadId * 0x8000) + 0x2000;
            ulong secondLoadThreadBase = LkProbeBase + ((ulong)secondLoadVirtualThreadId * 0x8000);
            ulong secondWriteThreadBase = LkProbeBase + ((ulong)secondStoreVirtualThreadId * 0x8000) + 0x2000;
            int loadBankId = ((round * 3) + loadVirtualThreadId + 1) & 0x7;
            int storeBankId = ((round * 3) + storeVirtualThreadId + 4) & 0x7;
            int secondLoadBankId = ((round * 3) + secondLoadVirtualThreadId + 2) & 0x7;
            int secondStoreBankId = ((round * 3) + secondStoreVirtualThreadId + 6) & 0x7;
            int loadRegister = 28 + (round & 0x3);
            int secondLoadRegister = 28 + ((round + 1) & 0x3);
            int storeSourceRegister = 12 + (((round + storeVirtualThreadId) & 0x3) * 4);
            int secondStoreSourceRegister = 12 + (((round + secondStoreVirtualThreadId) & 0x3) * 4);
            ulong readAddress = GetBankedAddress(virtualThreadBase, loadBankId, lineIndex, wordOffset: 0);
            ulong writeAddress = GetBankedAddress(writeThreadBase, storeBankId, lineIndex, wordOffset: 0);
            ulong secondReadAddress = GetBankedAddress(secondLoadThreadBase, secondLoadBankId, lineIndex, wordOffset: 1);
            ulong secondWriteAddress = GetBankedAddress(secondWriteThreadBase, secondStoreBankId, lineIndex, wordOffset: 1);
            EmitTypedLoad(loadVirtualThreadId, loadRegister, 4, readAddress);
            EmitTypedStore(storeVirtualThreadId, 4, storeSourceRegister, writeAddress);
            EmitTypedLoad(secondLoadVirtualThreadId, secondLoadRegister, 4, secondReadAddress);
            EmitTypedStore(secondStoreVirtualThreadId, 4, secondStoreSourceRegister, secondWriteAddress);

            if (ShouldEmitSchedulingWindowFence(round, rounds, MemoryStressSchedulingWindowRounds))
            {
                EmitFence(0);
            }
        }

        EmitFence(0);
    }

    private void EmitBankNoConflictMixedZooProgram(int rounds)
    {
        _workloadShape = "spec-like-bank-rotated-memory";

        for (int round = 0; round < rounds; round++)
        {
            int lineIndex = round & 0x1F;
            for (byte virtualThreadId = 0; virtualThreadId < 4; virtualThreadId++)
            {
                int scalarDestBase = 12 + (((round + virtualThreadId) & 0x3) * 4);
                EmitIndependentScalarQuad(virtualThreadId, registerBase: 4, scalarDestBase);
            }

            byte loadVirtualThreadId = (byte)(round & 0x3);
            byte storeVirtualThreadId = (byte)((round + 2) & 0x3);
            byte secondLoadVirtualThreadId = (byte)((round + 1) & 0x3);
            byte secondStoreVirtualThreadId = (byte)((round + 3) & 0x3);
            ulong loadThreadBase = BnmczProbeBase + ((ulong)loadVirtualThreadId * 0x8000);
            ulong storeThreadBase = BnmczProbeBase + ((ulong)storeVirtualThreadId * 0x8000) + 0x2000;
            ulong secondLoadThreadBase = BnmczProbeBase + ((ulong)secondLoadVirtualThreadId * 0x8000);
            ulong secondStoreThreadBase = BnmczProbeBase + ((ulong)secondStoreVirtualThreadId * 0x8000) + 0x2000;
            int loadBankId = ((round * 2) + loadVirtualThreadId + 1) & 0x7;
            int storeBankId = ((round * 2) + storeVirtualThreadId + 5) & 0x7;
            int secondLoadBankId = ((round * 2) + secondLoadVirtualThreadId + 3) & 0x7;
            int secondStoreBankId = ((round * 2) + secondStoreVirtualThreadId + 7) & 0x7;
            int loadRegister = 28 + (round & 0x3);
            int secondLoadRegister = 28 + ((round + 2) & 0x3);
            int storeSourceRegister = 12 + (((round + storeVirtualThreadId) & 0x3) * 4);
            int secondStoreSourceRegister = 12 + (((round + secondStoreVirtualThreadId) & 0x3) * 4);
            ulong readAddress = GetBankedAddress(loadThreadBase, loadBankId, lineIndex, wordOffset: 1);
            ulong writeAddress = GetBankedAddress(storeThreadBase, storeBankId, lineIndex, wordOffset: 0);
            ulong secondReadAddress = GetBankedAddress(secondLoadThreadBase, secondLoadBankId, lineIndex, wordOffset: 0);
            ulong secondWriteAddress = GetBankedAddress(secondStoreThreadBase, secondStoreBankId, lineIndex, wordOffset: 1);

            EmitTypedLoad(loadVirtualThreadId, loadRegister, 4, readAddress);
            EmitTypedStore(storeVirtualThreadId, 4, storeSourceRegister, writeAddress);
            EmitTypedLoad(secondLoadVirtualThreadId, secondLoadRegister, 4, secondReadAddress);
            EmitTypedStore(secondStoreVirtualThreadId, 4, secondStoreSourceRegister, secondWriteAddress);

            if (ShouldEmitSchedulingWindowFence(round, rounds, MemoryStressSchedulingWindowRounds))
            {
                EmitFence(0);
            }
        }

        EmitFence(0);
    }

    private void EmitVectorLoadProbe(byte virtualThreadId)
    {
        EmitInstruction(
            virtualThreadId,
            Processor.CPU_Core.InstructionsEnum.VLOAD,
            (byte)DataTypeEnum.INT32,
            0,
            VectorDestinationBase,
            VectorSourceBase,
            8,
            4,
            canBeStolen: false);
    }

    private void EmitStreamWaitRound(byte virtualThreadId)
    {
        EmitInstruction(
            virtualThreadId,
            Processor.CPU_Core.InstructionsEnum.STREAM_WAIT,
            0,
            0,
            0,
            0,
            0,
            0,
            canBeStolen: false);
    }

    private void SeedSyntheticProbeRegisterState(byte virtualThreadId, int registerBase, ulong probeBase)
    {
        ulong virtualThreadBase = probeBase + ((ulong)virtualThreadId * 0x8000);
        SetIntRegisterValue(virtualThreadId, registerBase, virtualThreadBase);
        SetIntRegisterValue(virtualThreadId, registerBase + 1, (ulong)(0x10 + virtualThreadId));
        SetIntRegisterValue(virtualThreadId, registerBase + 2, (ulong)(0x20 + virtualThreadId));
        SetIntRegisterValue(virtualThreadId, registerBase + 3, (ulong)(0x30 + virtualThreadId));
        SetIntRegisterValue(virtualThreadId, registerBase + 4, (ulong)(0x40 + virtualThreadId));
        SetIntRegisterValue(virtualThreadId, registerBase + 5, (ulong)(0x50 + virtualThreadId));
        SetIntRegisterValue(virtualThreadId, registerBase + 6, (ulong)(0x60 + virtualThreadId));
        SetIntRegisterValue(virtualThreadId, registerBase + 7, (ulong)(0x70 + virtualThreadId));
    }

    private void EmitIndependentScalarQuad(byte virtualThreadId, int registerBase, int destinationRegisterBase)
    {
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.ADD, destinationRegisterBase, registerBase + 1, registerBase + 2);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.XOR, destinationRegisterBase + 1, registerBase + 3, registerBase + 4);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.OR, destinationRegisterBase + 2, registerBase + 5, registerBase + 6);
        EmitBinaryScalar(virtualThreadId, Processor.CPU_Core.InstructionsEnum.MUL, destinationRegisterBase + 3, registerBase + 2, registerBase + 7);
    }

    private void EmitFence(byte virtualThreadId)
    {
        EmitInstruction(
            virtualThreadId,
            Processor.CPU_Core.InstructionsEnum.FENCE,
            0,
            0,
            0,
            0,
            0,
            0,
            canBeStolen: false);
    }

    private static bool ShouldEmitSchedulingWindowFence(int round, int totalRounds, int windowRounds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowRounds, 1);
        return round + 1 < totalRounds &&
               ((round + 1) % windowRounds) == 0;
    }

    private void EmitTypedLoad(byte virtualThreadId, int destinationRegisterId, int baseRegisterId, ulong address)
    {
        EmitInstruction(
            virtualThreadId,
            Processor.CPU_Core.InstructionsEnum.LD,
            0,
            0,
            VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegisterId),
                checked((byte)baseRegisterId),
                VLIW_Instruction.NoArchReg),
            address,
            0,
            0,
            canBeStolen: true);
    }

    private void EmitTypedStore(byte virtualThreadId, int baseRegisterId, int sourceRegisterId, ulong address)
    {
        EmitInstruction(
            virtualThreadId,
            Processor.CPU_Core.InstructionsEnum.SD,
            0,
            0,
            VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg,
                checked((byte)baseRegisterId),
                checked((byte)sourceRegisterId)),
            address,
            0,
            0,
            canBeStolen: true);
    }

    private void EmitAddImmediate(byte virtualThreadId, int destinationRegisterId, int sourceRegisterId, short immediate)
    {
        EmitInstruction(
            virtualThreadId,
            Processor.CPU_Core.InstructionsEnum.ADDI,
            0,
            unchecked((ushort)immediate),
            VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegisterId),
                checked((byte)sourceRegisterId),
                VLIW_Instruction.NoArchReg),
            0,
            0,
            0,
            canBeStolen: true);
    }

    private void EmitBinaryScalar(byte virtualThreadId, Processor.CPU_Core.InstructionsEnum instruction, int destinationRegisterId, int firstRegisterId, int secondRegisterId)
    {
        EmitInstruction(
            virtualThreadId,
            instruction,
            0,
            0,
            VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegisterId),
                checked((byte)firstRegisterId),
                checked((byte)secondRegisterId)),
            0,
            0,
            0,
            canBeStolen: true);
    }

    private void EmitInstruction(
        byte virtualThreadId,
        Processor.CPU_Core.InstructionsEnum instruction,
        byte dataType,
        ushort immediate,
        ulong destSrc1,
        ulong src2,
        ulong streamLength,
        ushort stride,
        bool canBeStolen)
    {
        if (_instructionCount >= MaxInstructions)
        {
            throw new InvalidOperationException($"Instruction buffer overflow (max {MaxInstructions})");
        }

        _instructions[_instructionCount] = new VLIW_Instruction
        {
            OpCode = (uint)instruction,
            DataType = dataType,
            PredicateMask = 0,
            Immediate = immediate,
            DestSrc1Pointer = destSrc1,
            Src2Pointer = src2,
            StreamLength = (uint)streamLength,
            Stride = stride
        };
        _instructionSlotMetadata[_instructionCount] = new InstructionSlotMetadata(
            VtId.Create(virtualThreadId),
            canBeStolen ? SlotMetadata.Default : SlotMetadata.NotStealable);
        _instructionCount++;

        _emittedVirtualThreadIds.Add(virtualThreadId);
    }

    private VliwBundleAnnotations GetBundleAnnotations()
    {
        if (_instructionCount == 0)
        {
            return VliwBundleAnnotations.Empty;
        }

        InstructionSlotMetadata[] slotMetadata = new InstructionSlotMetadata[_instructionCount];
        Array.Copy(_instructionSlotMetadata, slotMetadata, _instructionCount);
        return new VliwBundleAnnotations(slotMetadata);
    }
}
