using System.Buffers.Binary;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

internal static class EhNativeTransferSmoke
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        ulong[] restored = Enumerable.Range(0, 32).Select(i => 0xfedcba9800000000UL + (ulong)i).ToArray();
        restored[0] = 0; restored[2] = 0x800000; restored[1] = 0x60000;
        const ulong handler = 0x40000, exception = 0xabc000, recordAddress = 0x100000;
        byte[] record = HybridCpuManagedExceptionTransferV1.Encode(handler, restored, exception);
        byte[] code = HybridCpuManagedExceptionTransferEmitterV1.Emit();
        Check(code.SequenceEqual(HybridCpuManagedExceptionTransferEmitterV1.Emit()), "native transfer encoding deterministic");
        ulong[] registers = Enumerable.Repeat(0x12345678UL, 32).ToArray();
        registers[0] = 0; registers[10] = recordAddress;
        ulong pc = 0;
        for (int offset = 0; offset < code.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes)
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            Check(bundle.TryReadBytes(code, offset), "decode transfer bundle");
            Check(Enumerable.Range(1, 7).All(slot => bundle.GetInstruction(slot).OpCode == (uint)HybridCpuOpcode.Nope),
                "transfer fragment must not rely on same-bundle register forwarding");
            HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
            Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2) &&
                rs2 == HybridCpuInstructionWord.NoArchReg && instruction.PredicateMask == byte.MaxValue, "exact scalar operand encoding");
            ulong source = registers[rs1];
            switch ((HybridCpuOpcode)instruction.OpCode)
            {
                case HybridCpuOpcode.ADDI: registers[rd] = unchecked(source + (ulong)(long)(short)instruction.Immediate); break;
                case HybridCpuOpcode.LD:
                    Check(source >= recordAddress && source - recordAddress <= (ulong)record.Length - 8, "transfer reads only owned record");
                    registers[rd] = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(checked((int)(source - recordAddress))));
                    break;
                case HybridCpuOpcode.JALR:
                    Check(rd == 0 && instruction.Immediate == 0 && offset == code.Length - HybridCpuBundleSerializer.BundleSizeBytes,
                        "last instruction transfers without writing a return link");
                    pc = source;
                    break;
                default: throw new Exception("Unexpected transfer instruction: " + instruction.OpCode);
            }
            registers[0] = 0;
        }
        Check(pc == handler && registers[2] == restored[2] && registers[1] == restored[1] && registers[10] == exception,
            "native transfer restores handler PC/SP/RA and exception argument");
        foreach (int register in HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters)
            Check(registers[register] == restored[register], "restored callee-saved x" + register);
        Check(registers[3] == 0x12345678 && registers[4] == 0x12345678, "process/thread pointers retained");
        bool rejected = false;
        try { HybridCpuManagedExceptionTransferV1.Encode(handler + 1, restored, exception); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "misaligned handler must not produce a transfer record");
        HybridCpuObjectArtifactV1 transferObject = HybridCpuManagedExceptionTransferObjectV1.Emit();
        Check(transferObject.Status == HybridCpuObjectStatusV1.Success &&
            transferObject.Sections is [{ Kind: HybridCpuObjectSectionKind.Code }] &&
            transferObject.Symbols is [{ Name: HybridCpuManagedExceptionTransferObjectV1.Symbol,
                IsDefinition: true }], "native transfer must be an exact linkable HCO definition");
        HybridCpuStaticLinkArtifactV1 linked = new HybridCpuStaticLinkerV1().Link(
            [new(HybridCpuManagedExceptionTransferObjectV1.ModuleIdentity, transferObject.Bytes)]);
        Check(linked.Status == HybridCpuLinkStatusV1.Success && linked.Symbols is [{ Name:
                HybridCpuManagedExceptionTransferObjectV1.Symbol }] && linked.ImageBytes.SequenceEqual(code),
            "native transfer HCO must survive the production static linker byte-exactly");
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
                HybridCpuManagedExceptionTransferObjectV1.Symbol) is { Support: HybridCpuManagedAbiSupportV1.Supported,
                GcTransition: HybridCpuRuntimeHelperGcTransitionV1.None },
            "native transfer symbol must be bound to the exact managed ABI helper contract");
        const ulong stackBase = 0x700000, stackEnd = 0x800000;
        byte[] index = new byte[HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes +
            2 * HybridCpuManagedEhDispatchIndexV1.RowSizeBytes + 3 * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(index, HybridCpuManagedEhDispatchIndexV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(4), HybridCpuManagedEhDispatchIndexV1.Version);
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(HybridCpuManagedEhDispatchIndexV1.CountOffset), 2);
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(HybridCpuManagedEhDispatchIndexV1.TypeCountOffset), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(HybridCpuManagedEhDispatchIndexV1.StackBaseOffset), stackBase);
        BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(HybridCpuManagedEhDispatchIndexV1.StackEndOffset), stackEnd);
        const ulong indexAddress = 0x200000;
        WriteRow(0, 0x4000, 0x300);
        WriteRow(1, 0x8000, 0x500);
        WriteType(0, 1, 100, 0, 0);
        WriteType(1, 2, 200, 100, 1);
        WriteType(2, 3, 300, 200, 2);
        byte[] lookupCode = HybridCpuManagedEhFrameLookupEmitterV1.Emit();
        Check(RunLookup(0x8123) == indexAddress + (ulong)HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes +
                HybridCpuManagedEhDispatchIndexV1.RowSizeBytes &&
            RunLookup(0x8500) == 0 && RunLookup(0x1000) == 0,
            "native frame lookup must return exactly one containing half-open code range or zero");
        index[0] ^= 1;
        Check(RunLookup(0x8123) == 0, "native frame lookup must fail closed on a corrupt index header");
        index[0] ^= 1;
        HybridCpuObjectArtifactV1 lookupObject = HybridCpuManagedEhFrameLookupEmitterV1.EmitObject();
        Check(lookupObject.Status == HybridCpuObjectStatusV1.Success && lookupObject.Symbols is
            [{ Name: HybridCpuManagedEhFrameLookupEmitterV1.Symbol, IsDefinition: true }],
            "native frame lookup must be a linkable global HCO definition");
        byte[] typeMatchCode = HybridCpuManagedEhTypeMatchEmitterV1.Emit();
        Check(RunTypeMatch(3, 100) == 1 && RunTypeMatch(3, 200) == 1 &&
            RunTypeMatch(3, 999) == 0 && RunTypeMatch(0, 100) == 0 && RunTypeMatch(4, 100) == 0,
            "native type match must follow the closed ordinal-handle base chain and reject unknown handles/types");
        Check(HybridCpuManagedEhTypeMatchEmitterV1.EmitObject().Status == HybridCpuObjectStatusV1.Success,
            "native type match must be a linkable HCO definition");
        const ulong transferAddress = 0x300000;
        const ulong frameSp = 0x7fff00, cfa = frameSp + 128;
        ulong[] snapshotRegisters = Enumerable.Range(0, 32).Select(static value => (ulong)(0x1000 + value)).ToArray();
        snapshotRegisters[0] = 0;
        snapshotRegisters[2] = frameSp;
        snapshotRegisters[8] = 0x8888;
        byte[] snapshot = HybridCpuManagedExceptionTransferV1.Encode(0x40000, snapshotRegisters, exception);
        byte[] unwindRow = new byte[HybridCpuManagedEhDispatchIndexV1.RowSizeBytes];
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.CfaBaseOffset),
            (int)HybridCpuManagedCfaBaseV1.StackPointer);
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.CfaOffsetOffset), 128);
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.ReturnRegisterOffset), -1);
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.ReturnStackOffset), -8);
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.SavedCountOffset), 1);
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.SavedRowsOffset), 8);
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.SavedRowsOffset + 4), -16);
        unwindRow.CopyTo(index, HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes);
        Dictionary<ulong, ulong> stackWords = new Dictionary<ulong, ulong> { [cfa - 8] = 0x50000, [cfa - 16] = 0x81818181 };
        byte[] unwindCode = HybridCpuManagedEhUnwindStepEmitterV1.Emit();
        Check(RunUnwind() == 1 &&
            BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(HybridCpuManagedExceptionTransferV1.ProgramCounterOffset)) == 0x50000 &&
            BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(HybridCpuManagedExceptionTransferV1.RegisterOffset(2))) == cfa &&
            BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(HybridCpuManagedExceptionTransferV1.RegisterOffset(8))) == 0x81818181,
            "native unwind step must restore stack RA/saved x8 and advance SP to the exact CFA");
        byte[] beforeInvalid = snapshot.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(snapshot.AsSpan(HybridCpuManagedExceptionTransferV1.RegisterOffset(2)), frameSp);
        BinaryPrimitives.WriteInt32LittleEndian(unwindRow.AsSpan(HybridCpuManagedEhDispatchIndexV1.ReturnStackOffset), 256);
        unwindRow.CopyTo(index, HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes);
        beforeInvalid = snapshot.ToArray();
        Check(RunUnwind() == 0 && snapshot.SequenceEqual(beforeInvalid),
            "native unwind must fail closed before mutation when return-PC storage exceeds stackEnd");
        HybridCpuObjectArtifactV1 unwindObject = HybridCpuManagedEhUnwindStepEmitterV1.EmitObject();
        Check(unwindObject.Status == HybridCpuObjectStatusV1.Success && unwindObject.Symbols is
            [{ Name: HybridCpuManagedEhUnwindStepEmitterV1.Symbol, IsDefinition: true }],
            "native unwind step must be a linkable global HCO definition");
        byte[] finallySelector = HybridCpuManagedEhFinallySelectorEmitterV1.Emit();
        byte[] exceptionalResume = HybridCpuManagedEhExceptionalResumeEmitterV1.Emit();
        byte[] endFinally = HybridCpuManagedFinallyContinuationEmitterV1.Emit();
        Check(finallySelector.Length != 0 && exceptionalResume.Length != 0 && endFinally.Length != 0 &&
            finallySelector.Length % HybridCpuBundleSerializer.BundleSizeBytes == 0 &&
            exceptionalResume.Length % HybridCpuBundleSerializer.BundleSizeBytes == 0 &&
            endFinally.Length % HybridCpuBundleSerializer.BundleSizeBytes == 0 &&
            finallySelector.SequenceEqual(HybridCpuManagedEhFinallySelectorEmitterV1.Emit()) &&
            exceptionalResume.SequenceEqual(HybridCpuManagedEhExceptionalResumeEmitterV1.Emit()) &&
            endFinally.SequenceEqual(HybridCpuManagedFinallyContinuationEmitterV1.Emit()),
            "finally selector, exceptional resume, and endfinally continuation must be deterministic whole-bundle native code");
        Check(HybridCpuManagedEhFinallySelectorEmitterV1.EmitObject().Status == HybridCpuObjectStatusV1.Success &&
            HybridCpuManagedEhExceptionalResumeEmitterV1.EmitObject().Status == HybridCpuObjectStatusV1.Success &&
            HybridCpuManagedFinallyContinuationEmitterV1.EmitObject().Status == HybridCpuObjectStatusV1.Success &&
            HybridCpuManagedEhStateObjectV1.SizeBytes % 16 == 0 &&
            HybridCpuManagedEhStateObjectV1.UnwindTransferRecordOffset ==
                HybridCpuManagedEhStateObjectV1.SearchTransferRecordOffset + HybridCpuManagedExceptionTransferV1.SizeBytes &&
            HybridCpuManagedEhDispatchIndexV1.FinallyAddressOffset + 8 == HybridCpuManagedEhDispatchIndexV1.FinallySizeOffset &&
            HybridCpuManagedEhDispatchIndexV1.FinallySizeOffset + 8 == HybridCpuManagedEhDispatchIndexV1.RowSizeBytes,
            "exceptional finally runtime objects and their image-owned state/index layout must remain exact and linkable");
        Console.WriteLine("PASS encoded EH transfer restores ABI state and survives HCO/static-link as a non-returning native helper");

        void WriteRow(int row, ulong start, ulong size)
        {
            int offset = HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes +
                row * HybridCpuManagedEhDispatchIndexV1.RowSizeBytes;
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(offset), start);
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(offset + 8), size);
        }

        void WriteType(int row, ulong handle, ulong typeId, ulong baseTypeId, ulong baseHandle)
        {
            int offset = HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes +
                2 * HybridCpuManagedEhDispatchIndexV1.RowSizeBytes + row * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes;
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.TypeHandleOffset), handle);
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.TypeIdOffset), typeId);
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.BaseTypeIdOffset), baseTypeId);
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.BaseTypeHandleOffset), baseHandle);
        }

        ulong RunLookup(ulong lookupPc)
        {
            ulong[] state = new ulong[32];
            state[1] = 0xdead0000;
            ulong returnPc = state[1] + HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes;
            state[3] = indexAddress;
            state[11] = lookupPc;
            ulong lookupOffset = 0;
            for (int steps = 0; steps < 128 && lookupOffset != returnPc; steps++)
            {
                Check(lookupOffset < (ulong)lookupCode.Length && lookupOffset % HybridCpuBundleSerializer.BundleSizeBytes == 0,
                    "native lookup PC remains inside whole emitted bundles");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Check(bundle.TryReadBytes(lookupCode, checked((int)lookupOffset)), "decode native lookup bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2),
                    "decode native lookup operands");
                ulong next = lookupOffset + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI:
                        state[rd] = unchecked(state[rs1] + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.ADD:
                        state[rd] = unchecked(state[rs1] + state[rs2]); break;
                    case HybridCpuOpcode.SLLI:
                        state[rd] = state[rs1] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.ORI:
                        state[rd] = state[rs1] | (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.ANDI:
                        state[rd] = state[rs1] & (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.LD:
                        Check(state[rs1] >= indexAddress && state[rs1] - indexAddress <= (ulong)index.Length - 8,
                            "native lookup reads only the dispatch index");
                        state[rd] = BinaryPrimitives.ReadUInt64LittleEndian(index.AsSpan(
                            checked((int)(state[rs1] - indexAddress)), 8)); break;
                    case HybridCpuOpcode.LW:
                        Check(state[rs1] >= indexAddress && state[rs1] - indexAddress <= (ulong)index.Length - 4,
                            "native lookup reads only the dispatch index");
                        state[rd] = unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(
                            checked((int)(state[rs1] - indexAddress)), 4))); break;
                    case HybridCpuOpcode.BNE:
                        if (state[rs1] != state[rs2]) next = unchecked(lookupOffset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BLTU:
                        if (state[rs1] < state[rs2]) next = unchecked(lookupOffset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JAL:
                        if (rd != 0) state[rd] = next;
                        next = unchecked(lookupOffset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JALR:
                        if (rd != 0) state[rd] = next;
                        next = state[rs1] + (ulong)(short)instruction.Immediate; break;
                    default: throw new Exception("Unexpected native lookup opcode: " + instruction.OpCode);
                }
                state[0] = 0;
                lookupOffset = next;
            }
            Check(lookupOffset == returnPc, "native lookup must apply the bundled-call return adjustment");
            return state[10];
        }

        ulong RunUnwind()
        {
            ulong[] state = new ulong[32];
            state[1] = 0xbeef0000;
            ulong returnPc = state[1] + HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes;
            state[3] = indexAddress;
            state[10] = indexAddress + (ulong)HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes;
            state[13] = transferAddress;
            ulong offset = 0;
            ulong Read(ulong address, int width)
            {
                if (address >= indexAddress && address + (ulong)width <= indexAddress + (ulong)index.Length)
                    return width == 8
                        ? BinaryPrimitives.ReadUInt64LittleEndian(index.AsSpan(checked((int)(address - indexAddress)), 8))
                        : unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(checked((int)(address - indexAddress)), 4)));
                ulong rowAddress = indexAddress + (ulong)HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes;
                if (address >= rowAddress && address + (ulong)width <= rowAddress + (ulong)unwindRow.Length)
                    return width == 8
                        ? BinaryPrimitives.ReadUInt64LittleEndian(unwindRow.AsSpan(checked((int)(address - rowAddress)), 8))
                        : unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(unwindRow.AsSpan(checked((int)(address - rowAddress)), 4)));
                if (address >= transferAddress && address + (ulong)width <= transferAddress + (ulong)snapshot.Length)
                    return width == 8
                        ? BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(checked((int)(address - transferAddress)), 8))
                        : unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(snapshot.AsSpan(checked((int)(address - transferAddress)), 4)));
                if (width == 8 && stackWords.TryGetValue(address, out ulong word)) return word;
                throw new Exception($"native unwind attempted an unowned read at 0x{address:x}");
            }
            void Write(ulong address, ulong value)
            {
                Check(address >= transferAddress && address + 8 <= transferAddress + (ulong)snapshot.Length,
                    "native unwind writes only its HCET snapshot");
                BinaryPrimitives.WriteUInt64LittleEndian(snapshot.AsSpan(checked((int)(address - transferAddress)), 8), value);
            }
            for (int steps = 0; steps < 256 && offset != returnPc; steps++)
            {
                Check(offset < (ulong)unwindCode.Length && offset % HybridCpuBundleSerializer.BundleSizeBytes == 0,
                    "native unwind PC remains inside emitted bundles");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Check(bundle.TryReadBytes(unwindCode, checked((int)offset)), "decode native unwind bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2),
                    "decode native unwind operands");
                ulong next = offset + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI: state[rd] = unchecked(state[rs1] + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.ADD: state[rd] = unchecked(state[rs1] + state[rs2]); break;
                    case HybridCpuOpcode.SLLI: state[rd] = state[rs1] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.ANDI: state[rd] = state[rs1] & (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.LD: state[rd] = Read(state[rs1], 8); break;
                    case HybridCpuOpcode.LW: state[rd] = Read(state[rs1], 4); break;
                    case HybridCpuOpcode.SD: Write(state[rs1], state[rs2]); break;
                    case HybridCpuOpcode.BNE:
                        if (state[rs1] != state[rs2]) next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BLTU:
                        if (state[rs1] < state[rs2]) next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JAL: next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JALR: next = state[rs1] + (ulong)(short)instruction.Immediate; break;
                    default: throw new Exception("Unexpected native unwind opcode: " + instruction.OpCode);
                }
                state[0] = 0;
                offset = next;
            }
            Check(offset == returnPc, "native unwind must apply the bundled-call return adjustment");
            return state[10];
        }

        ulong RunTypeMatch(ulong handle, ulong targetTypeId)
        {
            ulong[] state = new ulong[32];
            state[1] = 0xcafe0000;
            ulong returnPc = state[1] + HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes;
            state[3] = indexAddress;
            state[10] = handle;
            state[11] = targetTypeId;
            ulong offset = 0;
            for (int steps = 0; steps < 128 && offset != returnPc; steps++)
            {
                Check(offset < (ulong)typeMatchCode.Length, "native type-match PC remains inside emitted code");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Check(bundle.TryReadBytes(typeMatchCode, checked((int)offset)), "decode native type-match bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2),
                    "decode native type-match operands");
                ulong next = offset + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI: state[rd] = unchecked(state[rs1] + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.ADD: state[rd] = unchecked(state[rs1] + state[rs2]); break;
                    case HybridCpuOpcode.MUL: state[rd] = unchecked(state[rs1] * state[rs2]); break;
                    case HybridCpuOpcode.SLLI: state[rd] = state[rs1] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.LD:
                        state[rd] = BinaryPrimitives.ReadUInt64LittleEndian(index.AsSpan(checked((int)(state[rs1] - indexAddress)), 8)); break;
                    case HybridCpuOpcode.LW:
                        state[rd] = unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(
                            checked((int)(state[rs1] - indexAddress)), 4))); break;
                    case HybridCpuOpcode.BNE:
                        if (state[rs1] != state[rs2]) next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BLTU:
                        if (state[rs1] < state[rs2]) next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JAL: next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JALR: next = state[rs1] + (ulong)(short)instruction.Immediate; break;
                    default: throw new Exception("Unexpected native type-match opcode: " + instruction.OpCode);
                }
                state[0] = 0;
                offset = next;
            }
            Check(offset == returnPc, "native type match applies the bundled-call return adjustment");
            return state[10];
        }
    }
}
