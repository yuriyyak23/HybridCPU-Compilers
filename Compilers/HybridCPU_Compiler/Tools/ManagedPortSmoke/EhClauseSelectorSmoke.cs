using System.Buffers.Binary;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Platform.Contracts;

internal static class EhClauseSelectorSmoke
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        const ulong indexAddress = 0x200000, ehAddress = 0x300000, codeAddress = 0x400000;
        byte[] row = new byte[HybridCpuManagedEhDispatchIndexV1.RowSizeBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset), codeAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset), 1024);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(HybridCpuManagedEhDispatchIndexV1.EhAddressOffset), ehAddress);
        byte[] eh = new byte[HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes + 3 * HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(eh, HybridCpuManagedEhSchemaV1.EhMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(HybridCpuManagedEhClauseEncodingV1.CountOffset), 3);
        WriteClause(0, HybridCpuManagedEhClauseKindV1.Catch, 0, 300, 400, 40, 100);
        WriteClause(1, HybridCpuManagedEhClauseKindV1.Catch, 50, 100, 500, 40, 100);
        WriteClause(2, HybridCpuManagedEhClauseKindV1.Finally, 0, 350, 600, 40, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(HybridCpuManagedEhDispatchIndexV1.EhSizeOffset), (ulong)eh.Length);
        byte[] code = HybridCpuManagedEhClauseSelectorEmitterV1.Emit();
        ulong second = ehAddress + (ulong)HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes +
            (ulong)HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes;
        Check(Execute(codeAddress + 75, 100) == second, "selector chooses innermost exact catch");
        Check(Execute(codeAddress + 150, 100) == ehAddress + HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes,
            "selector uses half-open native try ranges");
        Check(Execute(codeAddress + 75, 200) == 0 && Execute(codeAddress + 1024, 100) == 0,
            "selector rejects missing exact type and code-end boundary");
        int reserved = HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes + 1;
        eh[reserved] = 1;
        Check(Execute(codeAddress + 75, 100) == 0, "selector fails closed on reserved clause bytes");
        eh[reserved] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes +
            HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes + HybridCpuManagedEhClauseEncodingV1.OrdinalOffset), 7);
        Check(Execute(codeAddress + 75, 100) == 0, "selector fails closed on noncanonical ordinal");
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes +
            HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes + HybridCpuManagedEhClauseEncodingV1.OrdinalOffset), 1);
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes +
            HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset), -1);
        Check(Execute(codeAddress + 75, 100) == 0, "selector fails closed on wrapped negative handler size");
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes +
            HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset), 40);
        Check(HybridCpuManagedEhClauseSelectorEmitterV1.EmitObject().Status == HybridCpuObjectStatusV1.Success,
            "selector is a linkable HCO definition");
        HybridCpuObjectArtifactV1 ancestry = HybridCpuManagedEhCatchSelectorEmitterV1.EmitObject();
        Check(ancestry.Status == HybridCpuObjectStatusV1.Success && ancestry.Relocations is
            [{ Kind: HybridCpuRelocationKind.ManagedCallRelativeSigned16,
               TargetSymbol: HybridCpuManagedEhClauseSelectorEmitterV1.Symbol }],
            "ancestry selector carries one exact managed-call relocation");
        HybridCpuStaticLinkArtifactV1 linked = new HybridCpuStaticLinkerV1().Link(
            [new(HybridCpuManagedEhCatchSelectorEmitterV1.ModuleIdentity, ancestry.Bytes),
             new(HybridCpuManagedEhClauseSelectorEmitterV1.ModuleIdentity,
                 HybridCpuManagedEhClauseSelectorEmitterV1.EmitObject().Bytes)]);
        Check(linked.Status == HybridCpuLinkStatusV1.Success && linked.Symbols.Any(symbol =>
                symbol.Name == HybridCpuManagedEhCatchSelectorEmitterV1.Symbol) && linked.Symbols.Any(symbol =>
                symbol.Name == HybridCpuManagedEhClauseSelectorEmitterV1.Symbol),
            "ancestry and exact selectors survive production static link with their call resolved");
        ulong ancestryResult = ExecuteLinkedAncestry(linked);
        Check(ancestryResult == second,
            $"linked ancestry selector resolves a base-TypeId catch and restores its ABI frame: 0x{ancestryResult:x} != 0x{second:x}");
        HybridCpuObjectArtifactV1 catchDispatch = HybridCpuManagedEhCatchDispatchEmitterV1.EmitObject();
        string[] catchCalls = HybridCpuManagedEhCatchDispatchEmitterV1.Emit().Calls
            .Select(static call => call.Symbol).ToArray();
        Check(catchDispatch.Status == HybridCpuObjectStatusV1.Success && catchDispatch.Relocations.Count == 0 &&
            catchCalls.SequenceEqual(new[]
            {
                HybridCpuManagedEhFrameLookupEmitterV1.Symbol,
                HybridCpuManagedEhCatchSelectorEmitterV1.Symbol,
                HybridCpuManagedEhUnwindStepEmitterV1.Symbol,
                HybridCpuManagedEhExceptionalResumeEmitterV1.Symbol,
                HybridCpuManagedEhExceptionalResumeEmitterV1.Symbol,
                HybridCpuManagedProcessExitEmitterV1.Symbol
            }, StringComparer.Ordinal) &&
            HybridCpuManagedEhStateObjectV1.Emit().Status == HybridCpuObjectStatusV1.Success,
            "catch frame walker must retain exact index-bound lookup/select/unwind/resume calls and terminal ProcessExit without range-limited relocations");
        HybridCpuStaticLinkArtifactV1 dispatchLink = new HybridCpuStaticLinkerV1().Link(
            [new(HybridCpuManagedEhCatchDispatchEmitterV1.ModuleIdentity, catchDispatch.Bytes),
             new(HybridCpuManagedEhFrameLookupEmitterV1.ModuleIdentity, HybridCpuManagedEhFrameLookupEmitterV1.EmitObject().Bytes),
             new(HybridCpuManagedEhCatchSelectorEmitterV1.ModuleIdentity, ancestry.Bytes),
             new(HybridCpuManagedEhClauseSelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhClauseSelectorEmitterV1.EmitObject().Bytes),
             new(HybridCpuManagedEhFinallySelectorEmitterV1.ModuleIdentity, HybridCpuManagedEhFinallySelectorEmitterV1.EmitObject().Bytes),
             new(HybridCpuManagedEhUnwindStepEmitterV1.ModuleIdentity, HybridCpuManagedEhUnwindStepEmitterV1.EmitObject().Bytes),
             new(HybridCpuManagedEhExceptionalResumeEmitterV1.ModuleIdentity, HybridCpuManagedEhExceptionalResumeEmitterV1.EmitObject().Bytes),
             new(HybridCpuManagedExceptionTransferObjectV1.ModuleIdentity, HybridCpuManagedExceptionTransferObjectV1.Emit().Bytes),
             new(HybridCpuManagedProcessExitEmitterV1.ModuleIdentity, HybridCpuManagedProcessExitEmitterV1.EmitObject().Bytes)]);
        Check(dispatchLink.Status == HybridCpuLinkStatusV1.Success,
            "catch frame walker and every native dependency fit the production linked-call contract: " +
            dispatchLink.Diagnostics.FirstOrDefault()?.Code);
        ExecuteLinkedCatchDispatch(dispatchLink);
        VerifyProcessExit();
        Console.WriteLine("PASS native exact-catch selector validates final .hceh and chooses innermost clause");

        void WriteClause(int ordinal, HybridCpuManagedEhClauseKindV1 kind, int start, int size,
            int handler, int handlerSize, ulong catchType)
        {
            int offset = HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes + ordinal * HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes;
            eh[offset] = (byte)kind;
            BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.TryStartOffset), start);
            BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.TrySizeOffset), size);
            BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset), handler);
            BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset), handlerSize);
            BinaryPrimitives.WriteUInt64LittleEndian(eh.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.CatchTypeIdOffset), catchType);
            BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(offset + HybridCpuManagedEhClauseEncodingV1.OrdinalOffset), ordinal);
        }

        ulong Execute(ulong pc, ulong catchType)
        {
            ulong[] state = new ulong[32];
            state[1] = 0xdead0000; state[10] = indexAddress; state[11] = pc; state[12] = catchType;
            ulong returnPc = state[1] + HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes;
            ulong offset = 0;
            ulong Read(ulong address, int width)
            {
                ReadOnlySpan<byte> source; int sourceOffset;
                if (address >= indexAddress && address + (ulong)width <= indexAddress + (ulong)row.Length)
                { source = row; sourceOffset = checked((int)(address - indexAddress)); }
                else if (address >= ehAddress && address + (ulong)width <= ehAddress + (ulong)eh.Length)
                { source = eh; sourceOffset = checked((int)(address - ehAddress)); }
                else throw new Exception($"selector read outside owned metadata: 0x{address:x}");
                return width == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(source[sourceOffset..]) :
                    unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(source[sourceOffset..]));
            }
            for (int steps = 0; steps < 512 && offset != returnPc; steps++)
            {
                Check(offset < (ulong)code.Length, "selector PC remains in emitted code");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Check(bundle.TryReadBytes(code, checked((int)offset)), "decode selector bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2), "decode selector operands");
                ulong next = offset + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI: state[rd] = unchecked(state[rs1] + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.ADD: state[rd] = unchecked(state[rs1] + state[rs2]); break;
                    case HybridCpuOpcode.SUB: state[rd] = unchecked(state[rs1] - state[rs2]); break;
                    case HybridCpuOpcode.MUL: state[rd] = unchecked(state[rs1] * state[rs2]); break;
                    case HybridCpuOpcode.SLLI: state[rd] = state[rs1] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.ORI: state[rd] = state[rs1] | (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.LD: state[rd] = Read(state[rs1], 8); break;
                    case HybridCpuOpcode.LW: state[rd] = Read(state[rs1], 4); break;
                    case HybridCpuOpcode.BNE: if (state[rs1] != state[rs2]) next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BLTU: if (state[rs1] < state[rs2]) next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JAL: next = unchecked(offset + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JALR: next = state[rs1] + (ulong)(short)instruction.Immediate; break;
                    default: throw new Exception("Unexpected selector opcode: " + instruction.OpCode);
                }
                state[0] = 0; offset = next;
            }
            Check(offset == returnPc, "selector applies the bundled-call return adjustment");
            return state[10];
        }

        ulong ExecuteLinkedAncestry(HybridCpuStaticLinkArtifactV1 image)
        {
            const ulong dispatchAddress = 0x500000, stackBase = 0x700000, stackTop = 0x710000;
            byte[] dispatch = new byte[HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes +
                HybridCpuManagedEhDispatchIndexV1.RowSizeBytes + 2 * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes];
            BinaryPrimitives.WriteInt32LittleEndian(dispatch.AsSpan(HybridCpuManagedEhDispatchIndexV1.CountOffset), 1);
            BinaryPrimitives.WriteInt32LittleEndian(dispatch.AsSpan(HybridCpuManagedEhDispatchIndexV1.TypeCountOffset), 2);
            row.CopyTo(dispatch, HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes);
            int typeBase = HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes + HybridCpuManagedEhDispatchIndexV1.RowSizeBytes;
            WriteType(0, 1, 100, 0); WriteType(1, 2, 200, 1);
            void WriteType(int ordinal, ulong handle, ulong typeId, ulong baseHandle)
            {
                int at = typeBase + ordinal * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes;
                BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(at + HybridCpuManagedEhDispatchIndexV1.TypeHandleOffset), handle);
                BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(at + HybridCpuManagedEhDispatchIndexV1.TypeIdOffset), typeId);
                BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(at + HybridCpuManagedEhDispatchIndexV1.BaseTypeHandleOffset), baseHandle);
            }
            ulong[] state = Enumerable.Range(0, 32).Select(index => 0xabc00000UL + (ulong)index).ToArray();
            state[0] = 0; state[1] = 0x600004; state[2] = stackTop; state[3] = dispatchAddress;
            state[10] = dispatchAddress + HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes;
            state[11] = codeAddress + 75; state[12] = 2;
            ulong[] before = state.ToArray();
            ulong returnPc = state[1] + HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes;
            ulong pc = image.Symbols.Single(symbol => symbol.Name == HybridCpuManagedEhCatchSelectorEmitterV1.Symbol).Address;
            Dictionary<ulong, ulong> stack = new Dictionary<ulong, ulong>();
            ulong Read(ulong address, int width)
            {
                ReadOnlySpan<byte> source; int at;
                if (address >= dispatchAddress && address + (ulong)width <= dispatchAddress + (ulong)dispatch.Length)
                { source = dispatch; at = checked((int)(address - dispatchAddress)); }
                else if (address >= ehAddress && address + (ulong)width <= ehAddress + (ulong)eh.Length)
                { source = eh; at = checked((int)(address - ehAddress)); }
                else if (width == 8 && stack.TryGetValue(address, out ulong value)) return value;
                else throw new Exception($"linked ancestry read outside owned memory: 0x{address:x}");
                return width == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(source[at..]) :
                    unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(source[at..]));
            }
            for (int steps = 0; steps < 4096 && pc != returnPc; steps++)
            {
                Check(pc >= image.ImageBase && pc - image.ImageBase <= (ulong)image.ImageBytes.Length - HybridCpuBundleSerializer.BundleSizeBytes,
                    "linked ancestry PC remains in executable image");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Check(bundle.TryReadBytes(image.ImageBytes, checked((int)(pc - image.ImageBase))), "decode linked ancestry bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2), "decode linked ancestry operands");
                ulong next = pc + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI: state[rd] = unchecked(state[rs1] + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.ADD: state[rd] = unchecked(state[rs1] + state[rs2]); break;
                    case HybridCpuOpcode.SUB: state[rd] = unchecked(state[rs1] - state[rs2]); break;
                    case HybridCpuOpcode.MUL: state[rd] = unchecked(state[rs1] * state[rs2]); break;
                    case HybridCpuOpcode.SLLI: state[rd] = state[rs1] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.ORI: state[rd] = state[rs1] | (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.LD: state[rd] = Read(state[rs1], 8); break;
                    case HybridCpuOpcode.LW: state[rd] = Read(state[rs1], 4); break;
                    case HybridCpuOpcode.SD:
                        Check(state[rs1] >= stackBase && state[rs1] + 8 <= stackTop, "ancestry writes only its bounded ABI stack frame");
                        stack[state[rs1]] = state[rs2]; break;
                    case HybridCpuOpcode.BNE: if (state[rs1] != state[rs2]) next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BLTU: if (state[rs1] < state[rs2]) next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BGEU: if (state[rs1] >= state[rs2]) next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JAL:
                        if (rd != 0) state[rd] = pc + HybridCpuNativeCallControlContractV1.LinkIncrementBytes;
                        next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JALR:
                        if (rd != 0) state[rd] = pc + HybridCpuNativeCallControlContractV1.LinkIncrementBytes;
                        next = state[rs1] + (ulong)(short)instruction.Immediate; break;
                    default: throw new Exception("Unexpected linked ancestry opcode: " + instruction.OpCode);
                }
                state[0] = 0; pc = next;
            }
            Check(pc == returnPc && state[2] == before[2] && HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters.All(register =>
                    state[register] == before[register]), "ancestry helper returns and preserves SP/callee-saved state");
            return state[10];
        }

        void ExecuteLinkedCatchDispatch(HybridCpuStaticLinkArtifactV1 image)
        {
            const ulong tableAddress = 0x500000, objectAddress = 0x600000, stateAddress = 0x610000;
            const ulong stackBase = 0x700000, stackTop = 0x710000;
            const ulong managedCode = 0x800000, throwPc = managedCode + 256, handlerPc = managedCode + 1024;
            byte[] dispatch = new byte[HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes +
                HybridCpuManagedEhDispatchIndexV1.RowSizeBytes + 2 * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(dispatch, HybridCpuManagedEhDispatchIndexV1.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(dispatch.AsSpan(4), HybridCpuManagedEhDispatchIndexV1.Version);
            BinaryPrimitives.WriteInt32LittleEndian(dispatch.AsSpan(HybridCpuManagedEhDispatchIndexV1.CountOffset), 1);
            BinaryPrimitives.WriteInt32LittleEndian(dispatch.AsSpan(HybridCpuManagedEhDispatchIndexV1.TypeCountOffset), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(HybridCpuManagedEhDispatchIndexV1.StackBaseOffset), stackBase);
            BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(HybridCpuManagedEhDispatchIndexV1.StackEndOffset), stackTop);
            Helper(HybridCpuManagedEhDispatchIndexV1.FindFrameHelperOffset, HybridCpuManagedEhFrameLookupEmitterV1.Symbol);
            Helper(HybridCpuManagedEhDispatchIndexV1.CatchSelectorHelperOffset, HybridCpuManagedEhCatchSelectorEmitterV1.Symbol);
            Helper(HybridCpuManagedEhDispatchIndexV1.UnwindStepHelperOffset, HybridCpuManagedEhUnwindStepEmitterV1.Symbol);
            Helper(HybridCpuManagedEhDispatchIndexV1.FinallySelectorHelperOffset, HybridCpuManagedEhFinallySelectorEmitterV1.Symbol);
            Helper(HybridCpuManagedEhDispatchIndexV1.FinallyResumeHelperOffset, HybridCpuManagedEhExceptionalResumeEmitterV1.Symbol);
            Helper(HybridCpuManagedEhDispatchIndexV1.TransferHelperOffset, HybridCpuManagedExceptionTransferObjectV1.Symbol);
            Helper(HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset, HybridCpuManagedProcessExitEmitterV1.Symbol);
            BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset), stateAddress);
            void Helper(int offset, string symbol) => BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(offset),
                image.Symbols.Single(row => row.Name == symbol).Address);
            int method = HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes;
            BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(method + HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset), managedCode);
            BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(method + HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset), 2048);
            const ulong localEhAddress = 0x900000;
            byte[] localEh = new byte[HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes + HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(localEh, HybridCpuManagedEhSchemaV1.EhMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(localEh.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
            BinaryPrimitives.WriteInt32LittleEndian(localEh.AsSpan(HybridCpuManagedEhClauseEncodingV1.CountOffset), 1);
            int clause = HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes;
            BinaryPrimitives.WriteInt32LittleEndian(localEh.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.TrySizeOffset), 1024);
            BinaryPrimitives.WriteInt32LittleEndian(localEh.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset), 1024);
            BinaryPrimitives.WriteInt32LittleEndian(localEh.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset), 256);
            BinaryPrimitives.WriteUInt64LittleEndian(localEh.AsSpan(clause + HybridCpuManagedEhClauseEncodingV1.CatchTypeIdOffset), 100);
            BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(method + HybridCpuManagedEhDispatchIndexV1.EhAddressOffset), localEhAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(method + HybridCpuManagedEhDispatchIndexV1.EhSizeOffset), (ulong)localEh.Length);
            int typesAt = method + HybridCpuManagedEhDispatchIndexV1.RowSizeBytes;
            Type(0, 1, 100, 0); Type(1, 2, 200, 1);
            void Type(int ordinal, ulong handle, ulong typeId, ulong baseHandle)
            {
                int at = typesAt + ordinal * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes;
                BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(at + HybridCpuManagedEhDispatchIndexV1.TypeHandleOffset), handle);
                BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(at + HybridCpuManagedEhDispatchIndexV1.TypeIdOffset), typeId);
                BinaryPrimitives.WriteUInt64LittleEndian(dispatch.AsSpan(at + HybridCpuManagedEhDispatchIndexV1.BaseTypeHandleOffset), baseHandle);
            }
            byte[] managedObject = new byte[HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes];
            byte[] exceptionState = new byte[HybridCpuManagedEhStateObjectV1.SizeBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(managedObject, 2);
            ulong[] state = Enumerable.Range(0, 32).Select(index => 0xdef00000UL + (ulong)index).ToArray();
            state[0] = 0; state[1] = throwPc + HybridCpuNativeCallControlContractV1.LinkIncrementBytes;
            state[2] = stackTop; state[3] = tableAddress; state[10] = objectAddress;
            ulong[] original = state.ToArray();
            ulong pc = image.Symbols.Single(symbol => symbol.Name == HybridCpuManagedEhCatchDispatchEmitterV1.Symbol).Address;
            Dictionary<ulong, ulong> stack = new Dictionary<ulong, ulong>();
            ulong Read(ulong address, int width)
            {
                ReadOnlySpan<byte> source; int at;
                if (address >= tableAddress && address + (ulong)width <= tableAddress + (ulong)dispatch.Length)
                { source = dispatch; at = checked((int)(address - tableAddress)); }
                else if (address >= localEhAddress && address + (ulong)width <= localEhAddress + (ulong)localEh.Length)
                { source = localEh; at = checked((int)(address - localEhAddress)); }
                else if (address >= objectAddress && address + (ulong)width <= objectAddress + (ulong)managedObject.Length)
                { source = managedObject; at = checked((int)(address - objectAddress)); }
                else if (address >= stateAddress && address + (ulong)width <= stateAddress + (ulong)exceptionState.Length)
                { source = exceptionState; at = checked((int)(address - stateAddress)); }
                else if (width == 8 && stack.TryGetValue(address, out ulong value)) return value;
                else throw new Exception($"catch dispatcher read outside owned memory: 0x{address:x}");
                return width == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(source[at..]) :
                    unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(source[at..]));
            }
            for (int steps = 0; steps < 8192 && pc != handlerPc; steps++)
            {
                Check(pc >= image.ImageBase && pc - image.ImageBase <= (ulong)image.ImageBytes.Length - HybridCpuBundleSerializer.BundleSizeBytes,
                    "catch dispatcher PC remains in linked helper image until handler transfer");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                Check(bundle.TryReadBytes(image.ImageBytes, checked((int)(pc - image.ImageBase))), "decode linked catch-dispatch bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2), "decode catch-dispatch operands");
                ulong next = pc + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI: state[rd] = unchecked(state[rs1] + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.ADD: state[rd] = unchecked(state[rs1] + state[rs2]); break;
                    case HybridCpuOpcode.SUB: state[rd] = unchecked(state[rs1] - state[rs2]); break;
                    case HybridCpuOpcode.MUL: state[rd] = unchecked(state[rs1] * state[rs2]); break;
                    case HybridCpuOpcode.SLLI: state[rd] = state[rs1] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.ORI: state[rd] = state[rs1] | (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.ANDI: state[rd] = state[rs1] & (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.LD: state[rd] = Read(state[rs1], 8); break;
                    case HybridCpuOpcode.LW: state[rd] = Read(state[rs1], 4); break;
                    case HybridCpuOpcode.SD:
                        if (state[rs1] >= stackBase && state[rs1] + 8 <= stackTop) stack[state[rs1]] = state[rs2];
                        else if (state[rs1] >= stateAddress && state[rs1] + 8 <= stateAddress + (ulong)exceptionState.Length)
                            BinaryPrimitives.WriteUInt64LittleEndian(exceptionState.AsSpan(checked((int)(state[rs1] - stateAddress))), state[rs2]);
                        else throw new Exception("catch dispatcher writes only bounded HCET or EH state");
                        break;
                    case HybridCpuOpcode.BEQ: if (state[rs1] == state[rs2]) next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BNE: if (state[rs1] != state[rs2]) next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BLTU: if (state[rs1] < state[rs2]) next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.BGEU: if (state[rs1] >= state[rs2]) next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JAL:
                        if (rd != 0) state[rd] = pc + HybridCpuNativeCallControlContractV1.LinkIncrementBytes;
                        next = unchecked(pc + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.JALR:
                        if (rd != 0) state[rd] = pc + HybridCpuNativeCallControlContractV1.LinkIncrementBytes;
                        next = state[rs1] + (ulong)(short)instruction.Immediate; break;
                    default: throw new Exception("Unexpected linked catch-dispatch opcode: " + instruction.OpCode);
                }
                state[0] = 0; pc = next;
            }
            Check(pc == handlerPc && state[2] == original[2] && state[1] == original[1] && state[10] == objectAddress &&
                HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters.All(register => state[register] == original[register]) &&
                BinaryPrimitives.ReadUInt64LittleEndian(exceptionState) == objectAddress &&
                BinaryPrimitives.ReadUInt64LittleEndian(exceptionState.AsSpan(8)) == 2,
                $"handled dispatcher transfers exception with exact SP/RA/callee state; pc=0x{pc:x}/0x{handlerPc:x}, " +
                $"sp=0x{state[2]:x}/0x{original[2]:x}, ra=0x{state[1]:x}/0x{original[1]:x}, " +
                $"a0=0x{state[10]:x}/0x{objectAddress:x}, root=0x{BinaryPrimitives.ReadUInt64LittleEndian(exceptionState):x}, " +
                $"type=0x{BinaryPrimitives.ReadUInt64LittleEndian(exceptionState.AsSpan(8)):x}, changed-callee=" +
                string.Join(',', HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters
                    .Where(register => state[register] != original[register]).Select(register => $"x{register}")));
        }

        void VerifyProcessExit()
        {
            byte[] exitCode = HybridCpuManagedProcessExitEmitterV1.Emit();
            ulong[] state = new ulong[32]; state[10] = 255;
            ulong pc = 0;
            bool ecall = false;
            for (int steps = 0; steps < 32 && !ecall; steps++)
            {
                Check(pc < (ulong)exitCode.Length, "process-exit PC remains in its leaf until ECALL");
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); Check(bundle.TryReadBytes(exitCode, checked((int)pc)), "decode process-exit bundle");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out _), "decode process-exit operands");
                ulong next = pc + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI: state[rd] = unchecked(state[rs1] + (ulong)(long)(short)instruction.Immediate); break;
                    case HybridCpuOpcode.SLLI: state[rd] = state[rs1] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.ORI: state[rd] = state[rs1] | (ulong)(long)(short)instruction.Immediate; break;
                    case HybridCpuOpcode.ECALL: ecall = true; break;
                    default: throw new Exception("Unexpected process-exit opcode: " + instruction.OpCode);
                }
                state[0] = 0; pc = next;
            }
            HybridCpuInstructionBundle terminal = new HybridCpuInstructionBundle();
            Check(ecall && terminal.TryReadBytes(exitCode, checked((int)pc)), "process-exit retains a post-ECALL terminal retry bundle");
            HybridCpuInstructionWord retry = terminal.GetInstruction(0);
            Check(state[16] == 255 && state[17] == HybridCpuExternalServiceEcallContractV1.EcallNumber &&
                state[10] == (ulong)HybridCpuHostServiceV1.ProcessExit && state[11] == 0 && state[12] == 0 &&
                state[13] == 0 && state[14] == (ulong)HybridCpuHostBufferAccessV1.None && state[15] == 1 &&
                state[18] == 0 && state[19] == 0 && (HybridCpuOpcode)retry.OpCode == HybridCpuOpcode.JAL && retry.Immediate == 0 &&
                HybridCpuManagedProcessExitEmitterV1.EmitObject().Status == HybridCpuObjectStatusV1.Success,
                "process-exit must emit the exact external-service ECALL envelope and fail-closed retry if ECALL returns");
        }
    }
}
