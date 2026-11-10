using System.Buffers.Binary;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Platform.Contracts;

internal static class EhFinallyContinuationSmoke
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        const ulong dispatchAddress = 0x20_0000;
        const ulong metadataAddress = 0x30_0000;
        const ulong methodAddress = 0x40_0000;
        const ulong exceptionStateAddress = 0x50_0000;
        const ulong stackPointer = 0x70_0000;
        const ulong findFrameAddress = 0x80_0000;
        const ulong exceptionalResumeAddress = 0x81_0000;
        const ulong processExitAddress = 0x82_0000;
        const int tokenSlotOffset = 64;
        const int token = 7;

        byte[] code = HybridCpuManagedFinallyContinuationEmitterV1.Emit();
        byte[] dispatch = new byte[HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes +
            HybridCpuManagedEhDispatchIndexV1.RowSizeBytes];
        byte[] metadata = CreateMetadata();
        byte[] exceptionState = new byte[HybridCpuManagedEhStateObjectV1.SizeBytes];
        ulong rowAddress = dispatchAddress + HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes;
        Write64(dispatch, HybridCpuManagedEhDispatchIndexV1.FindFrameHelperOffset, findFrameAddress);
        Write64(dispatch, HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset, processExitAddress);
        Write64(dispatch, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset, exceptionStateAddress);
        Write64(dispatch, HybridCpuManagedEhDispatchIndexV1.FinallyResumeHelperOffset, exceptionalResumeAddress);
        int row = HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes;
        Write64(dispatch, row + HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset, methodAddress);
        Write64(dispatch, row + HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset, 0x1000);
        Write64(dispatch, row + HybridCpuManagedEhDispatchIndexV1.FinallyAddressOffset, metadataAddress);
        Write64(dispatch, row + HybridCpuManagedEhDispatchIndexV1.FinallySizeOffset, (ulong)metadata.Length);

        Dictionary<ulong, ulong> stack = new Dictionary<ulong, ulong> { [stackPointer + tokenSlotOffset] = token };
        Write64(exceptionState, HybridCpuManagedEhStateObjectV1.ExceptionReferenceOffset, 0xabc000);
        Write64(exceptionState, HybridCpuManagedEhStateObjectV1.TypeHandleOffset, 0x1234);

        Execution first = Execute(token, methodAddress + 0x240 +
            HybridCpuNativeCallControlContractV1.LinkIncrementBytes, metadata, exceptionState, stack);
        Check(!first.Exited && first.Target == methodAddress + 0x300 &&
            stack[stackPointer + tokenSlotOffset] == token,
            "first normal endfinally must transfer to the next exact finally without clearing its token");

        Execution second = Execute(token, methodAddress + 0x340 +
            HybridCpuNativeCallControlContractV1.LinkIncrementBytes, metadata, exceptionState, stack);
        Check(!second.Exited && second.Target == methodAddress + 0x500 &&
            stack[stackPointer + tokenSlotOffset] == 0 &&
            Read64(exceptionState, HybridCpuManagedEhStateObjectV1.ExceptionReferenceOffset) == 0 &&
            Read64(exceptionState, HybridCpuManagedEhStateObjectV1.TypeHandleOffset) == 0,
            "final normal endfinally must clear its frame token, release the catch root, and transfer to the leave target");

        Write64(exceptionState, HybridCpuManagedEhStateObjectV1.DispatchActiveOffset,
            HybridCpuManagedEhExceptionalResumeEmitterV1.ActiveValue);
        Write64(exceptionState, HybridCpuManagedEhStateObjectV1.DispatchPhaseOffset,
            HybridCpuManagedEhExceptionalResumeEmitterV1.UnwindPhase);
        Execution exceptional = Execute(ManagedEhFinallyContinuationPlanV1.ReservedExceptionalToken,
            methodAddress + 0x340 + HybridCpuNativeCallControlContractV1.LinkIncrementBytes,
            metadata, exceptionState, stack);
        Check(!exceptional.Exited && exceptional.Target == exceptionalResumeAddress &&
            exceptional.Argument == exceptionStateAddress + HybridCpuManagedEhStateObjectV1.UnwindTransferRecordOffset,
            "exceptional endfinally token zero must tail-enter exact phase-two unwind state with its image-owned HCET");

        Write64(exceptionState, HybridCpuManagedEhStateObjectV1.DispatchActiveOffset, 0);
        Execution inactive = Execute(ManagedEhFinallyContinuationPlanV1.ReservedExceptionalToken,
            methodAddress + 0x340 + HybridCpuNativeCallControlContractV1.LinkIncrementBytes,
            metadata, exceptionState, stack);
        Check(inactive.Exited && inactive.Argument == 255,
            $"exceptional endfinally must fail closed when no phase-two unwind is active; " +
            $"exited={inactive.Exited}, target=0x{inactive.Target:x}, argument={inactive.Argument}");

        byte[] corrupt = metadata.ToArray();
        corrupt[0] ^= 1;
        stack[stackPointer + tokenSlotOffset] = token;
        Execution malformed = Execute(token, methodAddress + 0x240 +
            HybridCpuNativeCallControlContractV1.LinkIncrementBytes, corrupt, exceptionState, stack);
        Check(malformed.Exited && malformed.Argument == 255 && stack[stackPointer + tokenSlotOffset] == token,
            "malformed finally metadata must terminate before mutating the frame-owned continuation token");

        byte[] cyclic = metadata.ToArray();
        int firstStep = HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes +
            HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes;
        Write32(cyclic, firstStep + HybridCpuManagedFinallyContinuationEncodingV1.StepNextClauseOrdinalOffset, 0);
        Execution cycle = Execute(token, methodAddress + 0x240 +
            HybridCpuNativeCallControlContractV1.LinkIncrementBytes, cyclic, exceptionState, stack);
        Check(cycle.Exited && cycle.Argument == 255 && stack[stackPointer + tokenSlotOffset] == token,
            "cyclic/noncanonical finally continuation must terminate before re-entering a handler");

        Console.WriteLine("PASS native endfinally executes exact normal/exceptional continuation and malformed fail-closed paths");

        Execution Execute(ulong continuationToken, ulong linkRegister, byte[] finallyMetadata,
            byte[] stateBytes, IDictionary<ulong, ulong> stackWords)
        {
            ulong[] registers = new ulong[32];
            registers[1] = linkRegister;
            registers[2] = stackPointer;
            registers[3] = dispatchAddress;
            registers[10] = continuationToken;
            ulong pc = 0;
            for (int steps = 0; steps < 4096; steps++)
            {
                Check(pc < (ulong)code.Length && pc % HybridCpuBundleSerializer.BundleSizeBytes == 0,
                    "endfinally PC must remain in whole emitted bundles until a non-returning transfer");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Check(bundle.TryReadBytes(code, checked((int)pc)), "decode endfinally bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1,
                    out byte rd, out byte rs1, out byte rs2), "decode endfinally operands");
                ulong next = pc + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI:
                        registers[rd] = unchecked(registers[rs1] + (ulong)(long)(short)instruction.Immediate);
                        break;
                    case HybridCpuOpcode.ADD:
                        registers[rd] = unchecked(registers[rs1] + registers[rs2]);
                        break;
                    case HybridCpuOpcode.SUB:
                        registers[rd] = unchecked(registers[rs1] - registers[rs2]);
                        break;
                    case HybridCpuOpcode.SLLI:
                        registers[rd] = registers[rs1] << (instruction.Immediate & 63);
                        break;
                    case HybridCpuOpcode.ORI:
                        registers[rd] = registers[rs1] | (ulong)(long)(short)instruction.Immediate;
                        break;
                    case HybridCpuOpcode.ANDI:
                        registers[rd] = registers[rs1] & (ulong)(long)(short)instruction.Immediate;
                        break;
                    case HybridCpuOpcode.AUIPC:
                        registers[rd] = unchecked(pc + (ulong)((long)(short)instruction.Immediate << 12));
                        break;
                    case HybridCpuOpcode.LD:
                        registers[rd] = Read(registers[rs1], 8);
                        break;
                    case HybridCpuOpcode.LW:
                        registers[rd] = Read(registers[rs1], 4);
                        break;
                    case HybridCpuOpcode.SD:
                        Write(registers[rs1], registers[rs2]);
                        break;
                    case HybridCpuOpcode.BEQ:
                        if (registers[rs1] == registers[rs2])
                            next = unchecked(pc + (ulong)(long)(short)instruction.Immediate);
                        break;
                    case HybridCpuOpcode.BNE:
                        if (registers[rs1] != registers[rs2])
                            next = unchecked(pc + (ulong)(long)(short)instruction.Immediate);
                        break;
                    case HybridCpuOpcode.BLTU:
                        if (registers[rs1] < registers[rs2])
                            next = unchecked(pc + (ulong)(long)(short)instruction.Immediate);
                        break;
                    case HybridCpuOpcode.BGEU:
                        if (registers[rs1] >= registers[rs2])
                            next = unchecked(pc + (ulong)(long)(short)instruction.Immediate);
                        break;
                    case HybridCpuOpcode.JAL:
                        if (rd != 0) registers[rd] = pc + HybridCpuNativeCallControlContractV1.LinkIncrementBytes;
                        next = unchecked(pc + (ulong)(long)(short)instruction.Immediate);
                        break;
                    case HybridCpuOpcode.JALR:
                    {
                        ulong target = unchecked(registers[rs1] + (ulong)(short)instruction.Immediate);
                        if (target == findFrameAddress)
                        {
                            Check(rd == 1 && registers[11] >= methodAddress && registers[11] < methodAddress + 0x1000,
                                "endfinally frame lookup must use the exact call-site PC");
                            registers[1] = pc + HybridCpuNativeCallControlContractV1.LinkIncrementBytes;
                            registers[10] = rowAddress;
                            next = registers[1] + HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes;
                            break;
                        }
                        if (target == processExitAddress)
                            return new(true, target, registers[10]);
                        if (rd == 0 && target < (ulong)code.Length &&
                            target % HybridCpuBundleSerializer.BundleSizeBytes == 0)
                        {
                            next = target;
                            break;
                        }
                        return new(false, target, registers[10]);
                    }
                    default:
                        throw new Exception("Unexpected endfinally opcode: " + instruction.OpCode);
                }
                registers[0] = 0;
                pc = next;
            }
            throw new Exception("endfinally exceeded its deterministic instruction bound");

            ulong Read(ulong address, int width)
            {
                if (TryRange(address, width, dispatchAddress, dispatch, out int dispatchOffset))
                    return ReadValue(dispatch, dispatchOffset, width);
                if (TryRange(address, width, metadataAddress, finallyMetadata, out int metadataOffset))
                    return ReadValue(finallyMetadata, metadataOffset, width);
                if (TryRange(address, width, exceptionStateAddress, stateBytes, out int stateOffset))
                    return ReadValue(stateBytes, stateOffset, width);
                if (width == 8 && stackWords.TryGetValue(address, out ulong stackValue)) return stackValue;
                throw new Exception($"endfinally attempted an unowned read at 0x{address:x}");
            }

            void Write(ulong address, ulong value)
            {
                if (address == stackPointer + tokenSlotOffset)
                {
                    stackWords[address] = value;
                    return;
                }
                Check(TryRange(address, 8, exceptionStateAddress, stateBytes, out int stateOffset),
                    "endfinally writes only its frame token or image-owned exception state");
                Write64(stateBytes, stateOffset, value);
            }
        }

        byte[] CreateMetadata()
        {
            int continuationBytes = HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes;
            byte[] bytes = new byte[HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes +
                continuationBytes + 2 * HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedFinallyContinuationEncodingV1.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedFinallyContinuationEncodingV1.Version);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(
                HybridCpuManagedFinallyContinuationEncodingV1.ContinuationCountOffset), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(
                HybridCpuManagedFinallyContinuationEncodingV1.StepCountOffset), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(
                HybridCpuManagedFinallyContinuationEncodingV1.TokenSlotOffset), tokenSlotOffset);
            int continuation = HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes;
            Write32(bytes, continuation + HybridCpuManagedFinallyContinuationEncodingV1.TokenOffset, token);
            Write32(bytes, continuation + HybridCpuManagedFinallyContinuationEncodingV1.LeaveOffset, 0x240);
            Write32(bytes, continuation + HybridCpuManagedFinallyContinuationEncodingV1.TargetOffset, 0x500);
            Write32(bytes, continuation + HybridCpuManagedFinallyContinuationEncodingV1.FirstStepOffset, 0);
            Write32(bytes, continuation + HybridCpuManagedFinallyContinuationEncodingV1.ContinuationStepCountOffset, 2);
            Write32(bytes, continuation + HybridCpuManagedFinallyContinuationEncodingV1.ReleaseBeforeTargetOffset, 1);
            int first = continuation + continuationBytes;
            WriteStep(bytes, first, 0, 0x200, 0, 1, 0x300, 0x280);
            int second = first + HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes;
            WriteStep(bytes, second, 1, 0x300, 0, -1, 0x500, 0x380);
            return bytes;
        }

        static void WriteStep(byte[] bytes, int offset, int ordinal, int handler, int release,
            int nextOrdinal, int nextOffset, int handlerEnd)
        {
            Write32(bytes, offset + HybridCpuManagedFinallyContinuationEncodingV1.StepClauseOrdinalOffset, ordinal);
            Write32(bytes, offset + HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerOffset, handler);
            Write32(bytes, offset + HybridCpuManagedFinallyContinuationEncodingV1.StepReleaseBeforeEntryOffset, release);
            Write32(bytes, offset + HybridCpuManagedFinallyContinuationEncodingV1.StepNextClauseOrdinalOffset, nextOrdinal);
            Write32(bytes, offset + HybridCpuManagedFinallyContinuationEncodingV1.StepNextOffset, nextOffset);
            Write32(bytes, offset + HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerEndOffset, handlerEnd);
        }
    }

    private static bool TryRange(ulong address, int width, ulong basis, byte[] bytes, out int offset)
    {
        offset = 0;
        if (address < basis || address - basis > (ulong)bytes.Length ||
            (ulong)width > (ulong)bytes.Length - (address - basis)) return false;
        offset = checked((int)(address - basis));
        return true;
    }

    private static ulong ReadValue(byte[] bytes, int offset, int width) => width == 8
        ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8))
        : unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));

    private static ulong Read64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));

    private static void Write32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static void Write64(byte[] bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);

    private readonly record struct Execution(bool Exited, ulong Target, ulong Argument);
}
