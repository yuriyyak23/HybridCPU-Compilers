using System.Buffers.Binary;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;

internal static class InterfaceResolverSmoke
{
    public static void Run()
    {
        byte[] code = HybridCpuManagedDispatchResolverEmitterV1.EmitInterface();
        byte[] memory = new byte[8192];
        const int index = 512, table = 2048, receiver = 4096;
        void Put(int address, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(memory.AsSpan(address), value);
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(index + HybridCpuManagedEhDispatchIndexV1.CountOffset), 1);
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(index + HybridCpuManagedEhDispatchIndexV1.TypeCountOffset), 1);
        int type = index + HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes + HybridCpuManagedEhDispatchIndexV1.RowSizeBytes;
        Put(type, 1); Put(type + HybridCpuManagedEhDispatchIndexV1.TypeIdOffset, 700);
        Put(receiver, 1); Put(index + HybridCpuManagedEhDispatchIndexV1.DispatchMetadataOffset, table);
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(table + 12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(table + 16), 1);
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(table + 20), 2);
        int first = table + HybridCpuManagedDispatchMetadataEmitterV1.HeaderBytes +
            HybridCpuManagedDispatchMetadataEmitterV1.MethodRowBytes + HybridCpuManagedDispatchMetadataEmitterV1.VirtualRowBytes;
        Put(first, 701); Put(first + 8, 800); Put(first + 16, 900); Put(first + 32, 0xdead);
        int second = first + HybridCpuManagedDispatchMetadataEmitterV1.InterfaceRowBytes;
        Put(second, 700); Put(second + 8, 800); Put(second + 16, 900); Put(second + 32, 0x500000);
        ulong selected = Execute(receiver, 800, 900);
        if (selected != 0x500000) throw new Exception($"Native interface resolver selected wrong implementation address: 0x{selected:x} != 0x500000.");
        if (Execute(receiver, 800, 901) != 0 || Execute(receiver, 801, 900) != 0 || Execute(0, 800, 900) != 0)
            throw new Exception("Native interface resolver failed null/missing-slot checks.");
        Put(receiver, 2);
        if (Execute(receiver, 800, 900) != 0) throw new Exception("Out-of-universe receiver handle accepted.");
        Console.WriteLine("PASS encoded interface resolver exact second-row lookup, misses, receiver bound and callee-save ABI");

        ulong Execute(ulong reference, ulong interfaceId, ulong slot)
        {
            ulong[] state = Enumerable.Range(0, 32).Select(i => 0x900000UL + (ulong)i).ToArray();
            state[0] = 0; state[1] = 0x800004; state[3] = index;
            state[10] = reference; state[11] = interfaceId; state[12] = slot;
            ulong[] before = (ulong[])state.Clone();
            ulong pc = 0;
            for (int steps = 0; steps < 1000; steps++)
            {
                if (pc == before[1] + HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes)
                {
                    foreach (int register in HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters)
                        if (state[register] != before[register]) throw new Exception($"Interface resolver clobbered callee-saved x{register}.");
                    return state[10];
                }
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                if (!bundle.TryReadBytes(code, checked((int)pc))) throw new Exception("Invalid resolver bundle.");
                HybridCpuInstructionWord instruction = bundle.GetInstruction(0);
                HybridCpuInstructionWord.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte a, out byte b);
                long immediate = (short)instruction.Immediate;
                ulong next = pc + HybridCpuBundleSerializer.BundleSizeBytes;
                switch ((HybridCpuOpcode)instruction.OpCode)
                {
                    case HybridCpuOpcode.ADDI: state[rd] = unchecked(state[a] + (ulong)immediate); break;
                    case HybridCpuOpcode.ADD: state[rd] = state[a] + state[b]; break;
                    case HybridCpuOpcode.SLLI: state[rd] = state[a] << (instruction.Immediate & 63); break;
                    case HybridCpuOpcode.LD: state[rd] = BinaryPrimitives.ReadUInt64LittleEndian(memory.AsSpan(checked((int)state[a]))); break;
                    case HybridCpuOpcode.LW: state[rd] = unchecked((ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(memory.AsSpan(checked((int)state[a])))); break;
                    case HybridCpuOpcode.BNE: if (state[a] != state[b]) next = unchecked(pc + (ulong)immediate); break;
                    case HybridCpuOpcode.BLTU: if (state[a] < state[b]) next = unchecked(pc + (ulong)immediate); break;
                    case HybridCpuOpcode.JAL: next = unchecked(pc + (ulong)immediate); if (rd != 0) state[rd] = pc + 4; break;
                    case HybridCpuOpcode.JALR: next = unchecked(state[a] + (ulong)immediate); if (rd != 0) state[rd] = pc + 4; break;
                    default: throw new Exception("Unexpected resolver instruction.");
                }
                state[0] = 0; pc = next;
            }
            throw new Exception("Interface resolver did not return within its instruction budget.");
        }
    }
}
