using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Object;

internal static class NativeConstantSmoke
{
    public static void Run()
    {
        foreach (ulong expected in new ulong[]
                 {
                     0, 1, 32767, 32768, unchecked((ulong)-32768L), unchecked((ulong)-32769L),
                     0x0000008000000000, 0x123456789abcdef0, ulong.MaxValue
                 })
        {
            IReadOnlyList<ExactNativeConstantStepV1> facade = ExactNativeConstantPlanV1.Create(expected);
            IReadOnlyList<HybridCpuConstantMaterializationStepV1> core = HybridCpuConstantMaterializationPlanV1.Create(expected);
            if (!facade.Select(static step => (step.Opcode, step.Immediate))
                .SequenceEqual(core.Select(static step => (step.Opcode, step.Immediate))) ||
                ExactNativeConstantPlanV1.FitsSignedImmediate(expected) !=
                HybridCpuConstantMaterializationPlanV1.FitsSignedImmediate(expected))
                throw new Exception("Core/CIL constant-plan compatibility drift");
            ulong actual = 0;
            foreach (ExactNativeConstantStepV1 step in ExactNativeConstantPlanV1.Create(expected))
                actual = step.Opcode switch
                {
                    HybridCpuOpcode.ADDI => unchecked((ulong)(long)(short)step.Immediate),
                    HybridCpuOpcode.SLLI => actual << (short)step.Immediate,
                    HybridCpuOpcode.ORI => actual | unchecked((ulong)(long)(short)step.Immediate),
                    _ => throw new Exception("Unexpected exact constant plan opcode")
                };
            if (actual != expected) throw new Exception($"Exact constant plan produced {actual:x16}, expected {expected:x16}");
        }
        byte[] pe = File.ReadAllBytes(typeof(NativeConstantFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach (MethodInfo method in typeof(NativeConstantFixture).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            byte[] fixturePe = pe;
            bool signedTruncation = method.Name is nameof(NativeConstantFixture.NullableInt16Truncate) or nameof(NativeConstantFixture.NullableInt16DefaultTruncate) or nameof(NativeConstantFixture.NullableSignedEnumTruncate) or nameof(NativeConstantFixture.NullableSignedEnumDefaultTruncate);
            bool booleanTruncation = method.Name is nameof(NativeConstantFixture.NullableBooleanTruncate) or nameof(NativeConstantFixture.NullableBooleanDefaultTruncate);
            bool byteTruncation = booleanTruncation || method.Name is nameof(NativeConstantFixture.NullableUInt8Truncate) or nameof(NativeConstantFixture.NullableUInt8DefaultTruncate) or nameof(NativeConstantFixture.NullableByteEnumTruncate) or nameof(NativeConstantFixture.NullableByteEnumDefaultTruncate);
            bool signedByteTruncation = method.Name is nameof(NativeConstantFixture.NullableInt8Truncate) or nameof(NativeConstantFixture.NullableInt8DefaultTruncate) or nameof(NativeConstantFixture.NullableSByteEnumTruncate) or nameof(NativeConstantFixture.NullableSByteEnumDefaultTruncate);
            if (signedByteTruncation || signedTruncation || byteTruncation || method.Name is nameof(NativeConstantFixture.NullableUInt16Truncate) or nameof(NativeConstantFixture.NullableUInt16DefaultTruncate) or nameof(NativeConstantFixture.NullableCharTruncate) or nameof(NativeConstantFixture.NullableCharDefaultTruncate) or nameof(NativeConstantFixture.NullableNarrowEnumTruncate) or nameof(NativeConstantFixture.NullableNarrowEnumDefaultTruncate)) {
                fixturePe = pe.ToArray();
                using PEReader reader = new PEReader(new MemoryStream(fixturePe));
                MetadataReader md = reader.GetMetadataReader();
                MethodDefinition definition = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0xffffff));
                byte[] il = reader.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes()!;
                int conversion = il.AsSpan().IndexOf(new byte[] { 0x02, signedByteTruncation ? (byte)0x67 : signedTruncation ? (byte)0x68 : byteTruncation ? (byte)0xd2 : (byte)0xd1 }) + 1;
                if (conversion <= 0) throw new Exception("UInt16 CLI argument truncation fixture");
                SectionHeader section = reader.PEHeaders.SectionHeaders.Single(s => definition.RelativeVirtualAddress >= s.VirtualAddress && definition.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                int start = section.PointerToRawData + definition.RelativeVirtualAddress - section.VirtualAddress;
                int header = (fixturePe[start] & 3) == 2 ? 1 : (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(fixturePe.AsSpan(start, 2)) >> 12) * 4;
                fixturePe[start + header + conversion] = 0; // CLI call parameter truncation must suffice without conv.u2.
                if (booleanTruncation) {
                    // Author a byte scalar oracle, then change the exact nullable
                    // TypeSpec and this method's local to Boolean in owned CLI.
                    // The raw Boolean call/result must preserve the whole low byte.
                    if (il[conversion + 1] != 0x28) throw new Exception("Raw Boolean fixture call");
                    int token = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(conversion + 2, 4));
                    if ((uint)token >> 24 != 0x0a) throw new Exception("Raw Boolean fixture member token");
                    MemberReference accessor = md.GetMemberReference(MetadataTokens.MemberReferenceHandle(token & 0xffffff));
                    if (accessor.Parent.Kind != HandleKind.TypeSpecification ||
                        md.GetBlobBytes(md.GetTypeSpecification((TypeSpecificationHandle)accessor.Parent).Signature) is not [0x15, 0x11, .., 1, 5])
                        throw new Exception("Raw Boolean fixture nullable declaration");
                    byte[] nullableByteSignature=md.GetBlobBytes(md.GetTypeSpecification((TypeSpecificationHandle)accessor.Parent).Signature);
                    void BooleanArgument(BlobHandle blob) {
                        byte[] bytes = md.GetBlobBytes(blob);
                        // Debug appends a primitive return local. Locate the exact
                        // emitted Nullable<byte> type, rather than the last byte of
                        // the complete local signature; preserve every other local.
                        int[] matches=Enumerable.Range(0,Math.Max(0,bytes.Length-nullableByteSignature.Length+1))
                            .Where(index=>bytes.AsSpan(index,nullableByteSignature.Length).SequenceEqual(nullableByteSignature)).ToArray();
                        if (bytes.Length >= 128 || matches.Length!=1 || nullableByteSignature[^1]!=5) throw new Exception("Raw Boolean fixture signature");
                        fixturePe[reader.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + matches[0] + nullableByteSignature.Length - 1] = 2;
                    }
                    BooleanArgument(md.GetTypeSpecification((TypeSpecificationHandle)accessor.Parent).Signature);
                    BooleanArgument(md.GetStandaloneSignature(reader.GetMethodBody(definition.RelativeVirtualAddress).LocalSignature).Signature);
                }
            }
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, fixturePe, "native-constant-smoke",
                [new(typeof(NativeConstantFixture).FullName!, method.Name)], []));
            if (graph.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception(method.Name + ": " + string.Join(';', graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            ScalarControlFlowV2MethodObjectV1 obj = (ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
                .GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { graph.Methods.Single(), 0 })!;
            if (obj.ObjectArtifact.Status != HybridCpuObjectStatusV1.Success) throw new Exception("Constant HCO failed");
            byte[] code = obj.ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            MethodInfo? cliHost = ReferenceEquals(fixturePe, pe) ? null : Assembly.Load(fixturePe).GetType(typeof(NativeConstantFixture).FullName!)!.GetMethod(method.Name)!;
            foreach (ulong input in new ulong[] { 0, 1, 2, 0x7f, 0x80, 0x100, 0x101, 0x7fff, 0x8000, 0xd800, 0xdfff, 0xffff, 0x7fffffff, 0x80000000, uint.MaxValue, 0x123456789abcdef0, 0x8000000000000000, 0x7fffffffffffffff, ulong.MaxValue })
            {
                object?[] parameters = method.GetParameters().Length == 0 ? [] :
                    [method.GetParameters()[0].ParameterType == typeof(int) ? (object)unchecked((int)input) :
                     method.GetParameters()[0].ParameterType == typeof(long) ? (object)unchecked((long)input) :
                     method.GetParameters()[0].ParameterType == typeof(bool) ? (object)(input != 0) :
                     method.GetParameters()[0].ParameterType == typeof(char) ? (object)unchecked((char)input) :
                     method.GetParameters()[0].ParameterType == typeof(ushort) ? (object)unchecked((ushort)input) :
                     method.GetParameters()[0].ParameterType == typeof(short) ? (object)unchecked((short)input) :
                     method.GetParameters()[0].ParameterType == typeof(sbyte) ? (object)unchecked((sbyte)input) :
                     method.GetParameters()[0].ParameterType == typeof(byte) ? (object)unchecked((byte)input) :
                     method.GetParameters()[0].ParameterType == typeof(uint) ? (object)(uint)input : input];
                object expected = method.Invoke(null, parameters)!;
                if (cliHost is not null && !Equals(cliHost.Invoke(null, parameters), expected)) throw new Exception("UInt16 CLI argument truncation differs from source oracle");
                // Managed I4 arguments contain exactly their declared 32 bits in
                // canonical carriers. Narrow uint inputs as well: high bits of the
                // ulong test vector must not turn uint zero into a nonzero receiver.
                Type? argumentType = method.GetParameters().FirstOrDefault()?.ParameterType;
                ulong abiInput = argumentType == typeof(int) || argumentType == typeof(uint)
                    ? unchecked((ulong)(long)(int)input) : argumentType == typeof(bool) ? (input != 0 ? 1UL : 0UL) : argumentType == typeof(char) ? (char)input : argumentType == typeof(ushort) ? (ushort)input : argumentType == typeof(short) ? unchecked((ulong)(long)(short)input) : argumentType == typeof(sbyte) ? unchecked((ulong)(long)(sbyte)input) : argumentType == typeof(byte) ? (byte)input : input;
                ulong actual = Evaluate(code, abiInput);
                bool equal = expected switch { int value => (uint)actual == unchecked((uint)value), uint value => (uint)actual == value,
                    bool value => actual == (value ? 1UL : 0UL), long value => actual == unchecked((ulong)value), ulong value => actual == value, _ => false };
                if (!equal) {
                    Console.WriteLine($"NATIVE-FAIL {method.Name} input={input:x16} IL={Convert.ToHexString(method.GetMethodBody()!.GetILAsByteArray()!)}");
                    foreach (IrInstruction instruction in graph.Methods.Single().Import.Program!.Instructions)
                        Console.WriteLine($"IR {instruction.StableIdentity} {instruction.Opcode} uses={string.Join(',', instruction.Operands)} defs={string.Join(',', instruction.Annotation.Defs)}");
                    for (int offset = 0; offset < code.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.TryReadBytes(code, offset);
                        for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                            HybridCpuInstructionWord word = bundle.GetInstruction(slot); if ((HybridCpuOpcode)word.OpCode == HybridCpuOpcode.Nope) continue;
                            HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out byte rs2);
                            Console.WriteLine($"WORD {offset}:{slot} {(HybridCpuOpcode)word.OpCode} x{rd},x{rs1},x{rs2} imm={(short)word.Immediate}");
                        }
                    }
                    throw new Exception($"{method.Name}: encoded words returned {actual:x16}, CoreCLR returned {expected}");
                }
            }
        }
        Console.WriteLine("PASS encoded native constant words: signed I4, full I8, arithmetic/masks and DIVU/DIVUW vs CoreCLR");
    }

    // Evaluate actual HCO .text words, not IR Constant operands. Bundle operands are
    // read from a pre-bundle snapshot. This is an ISA-word semantic test, NOT ISE execution.
    internal static ulong Evaluate(byte[] code, ulong input, ulong input2 = 0, int entryOffset = 0, ulong imageBase = 0,
        IReadOnlyDictionary<int, Func<ulong, ulong, ulong>>? externalCalls = null, Action? onCall = null,
        IReadOnlyList<ulong>? remainingArguments = null)
    {
        ulong[] registers = new ulong[256]; registers[10] = input; registers[11] = input2; registers[2] = 0x100000; registers[1] = ulong.MaxValue;
        // Explicit architectural a2..a7 operands, independent of the compiler's ABI table.
        if (remainingArguments is { Count: > 6 }) throw new ArgumentOutOfRangeException(nameof(remainingArguments));
        if (remainingArguments is not null) for (int index = 0; index < remainingArguments.Count; index++) registers[12 + index] = remainingArguments[index];
        Dictionary<ulong, byte> memory = new Dictionary<ulong, byte>();
        int steps = 0;
        int lastConditionalOffset = -1;
        HybridCpuOpcode lastConditionalOpcode = HybridCpuOpcode.Nope;
        ulong lastConditionalLeft = 0, lastConditionalRight = 0;
        for (int offset = entryOffset; offset < code.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes)
        {
            if (++steps > 10000 || offset < 0 || offset % HybridCpuBundleSerializer.BundleSizeBytes != 0) throw new Exception($"Native word oracle branch bound/alignment offset={offset} steps={steps} ra={registers[1]:x} sp={registers[2]:x} base={imageBase:x}; last={lastConditionalOffset}:{lastConditionalOpcode} left={lastConditionalLeft:x16} right={lastConditionalRight:x16}");
            int nextOffset = offset + HybridCpuBundleSerializer.BundleSizeBytes;
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            if (!bundle.TryReadBytes(code, offset)) throw new Exception("Malformed native bundle");
            ulong[] before = registers.ToArray();
            bool returning = false;
            for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++)
            {
                HybridCpuInstructionWord word = bundle.GetInstruction(slot);
                HybridCpuOpcode opcode = (HybridCpuOpcode)word.OpCode;
                if (opcode == HybridCpuOpcode.Nope) continue;
                if (!HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out byte rs2))
                    throw new Exception("Non-register native operand payload");
                ulong a = before[rs1], b = before[rs2];
                long immediate = (short)word.Immediate;
                if (opcode == HybridCpuOpcode.JALR) {
                    if(rd==1)onCall?.Invoke();
                    if(externalCalls?.TryGetValue(offset,out Func<ulong, ulong, ulong>? call)==true) {
                        if(rd!=1)throw new Exception("External scalar oracle call must write the ABI link register");
                        ulong value=call(before[10],before[11]);
                        registers[1]=imageBase+(ulong)(offset+HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
                        // Exercise preservation across an ABI call, including scratch
                        // and argument registers; the helper result alone is returned.
                        foreach(int clobbered in HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters.Where(register=>register!=1))registers[clobbered]=0xdeadbeefbadc0ffeUL;
                        registers[10]=value;
                        continue;
                    }
                    if (rs1 == 1 && a == ulong.MaxValue) returning = true;
                    else nextOffset = checked((int)(unchecked(a + (ulong)immediate) - imageBase));
                    if (rd is > 0 and < 32) registers[rd] = imageBase + (ulong)(offset + HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
                    continue;
                }
                if (opcode is HybridCpuOpcode.BEQ or HybridCpuOpcode.BNE or HybridCpuOpcode.BLT or HybridCpuOpcode.BGE or HybridCpuOpcode.BLTU or HybridCpuOpcode.BGEU or HybridCpuOpcode.JAL) {
                    if(opcode!=HybridCpuOpcode.JAL){lastConditionalOffset=offset;lastConditionalOpcode=opcode;lastConditionalLeft=a;lastConditionalRight=b;}
                    bool taken = opcode switch {
                        HybridCpuOpcode.BEQ => a == b, HybridCpuOpcode.BNE => a != b,
                        HybridCpuOpcode.BLT => (long)a < (long)b, HybridCpuOpcode.BGE => (long)a >= (long)b,
                        HybridCpuOpcode.BLTU => a < b, HybridCpuOpcode.BGEU => a >= b, _ => true };
                    if (taken) {
                        nextOffset = checked(offset + (int)immediate);
                        if (opcode == HybridCpuOpcode.JAL && rd is > 0 and < 32) registers[rd] = imageBase + (ulong)(offset + HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
                    }
                    continue;
                }
                if (opcode == HybridCpuOpcode.SD)
                {
                    for (int i = 0; i < 8; i++) memory[unchecked(a + (ulong)immediate + (ulong)i)] = (byte)(b >> (8 * i));
                    continue;
                }
                ulong result = opcode switch
                {
                    HybridCpuOpcode.ADDI => unchecked(a + (ulong)immediate),
                    HybridCpuOpcode.AUIPC => unchecked(imageBase + (ulong)(offset + (immediate << 12))),
                    HybridCpuOpcode.ADDIW => unchecked((ulong)(long)(int)(a + (ulong)immediate)),
                    HybridCpuOpcode.ADD => unchecked(a + b),
                    HybridCpuOpcode.ADDW => unchecked((ulong)(long)(int)(a + b)),
                    HybridCpuOpcode.MULW => unchecked((ulong)(long)((int)a * (int)b)),
                    HybridCpuOpcode.MUL => unchecked(a * b),
                    HybridCpuOpcode.SUB => unchecked(a - b),
                    HybridCpuOpcode.SLLI => a << ((int)immediate & 63),
                    HybridCpuOpcode.SRLI => a >> ((int)immediate & 63),
                    HybridCpuOpcode.SRAI => unchecked((ulong)((long)a >> ((int)immediate & 63))),
                    HybridCpuOpcode.SLL => a << ((int)b & 63),
                    HybridCpuOpcode.SLLW => unchecked((ulong)(long)(int)((uint)a << ((int)b & 31))),
                    HybridCpuOpcode.SRAW => unchecked((ulong)(long)((int)a >> ((int)b & 31))),
                    HybridCpuOpcode.SRLW => (uint)a >> ((int)b & 31),
                    HybridCpuOpcode.SRA => unchecked((ulong)((long)a >> ((int)b & 63))),
                    HybridCpuOpcode.SRL => a >> ((int)b & 63),
                    HybridCpuOpcode.ORI => a | unchecked((ulong)immediate),
                    HybridCpuOpcode.OR => a | b,
                    HybridCpuOpcode.AND => a & b,
                    HybridCpuOpcode.ANDI => a & unchecked((ulong)immediate),
                    HybridCpuOpcode.SLTU => a < b ? 1UL : 0UL,
                    HybridCpuOpcode.SLT => unchecked((long)a) < unchecked((long)b) ? 1UL : 0UL,
                    HybridCpuOpcode.SLTIU => a < unchecked((ulong)immediate) ? 1UL : 0UL,
                    HybridCpuOpcode.XORI => a ^ unchecked((ulong)immediate),
                    HybridCpuOpcode.XOR => a ^ b,
                    HybridCpuOpcode.DIVU when b != 0 => a / b,
                    HybridCpuOpcode.DIVUW when (uint)b != 0 => unchecked((ulong)(long)(int)((uint)a / (uint)b)),
                    HybridCpuOpcode.LD => Enumerable.Range(0, 8).Aggregate(0UL, (value, i) => value | (ulong)memory[unchecked(a + (ulong)immediate + (ulong)i)] << (8 * i)),
                    _ => throw new Exception("Unexpected native constant opcode " + opcode)
                };
                if (rd == HybridCpuInstructionWord.NoArchReg) {
                    if (opcode == HybridCpuOpcode.ADDI && rs1 == 0 && immediate == 0) continue; // Existing branch target marker.
                    throw new Exception("Native result has no register: " + opcode + " at " + offset);
                }
                if (rd != 0) registers[rd] = result;
            }
            registers[0] = 0;
            if (returning) return registers[10];
            offset = nextOffset - HybridCpuBundleSerializer.BundleSizeBytes;
        }
        throw new Exception("Native constant fragment did not return");
    }
}

public static class NativeConstantFixture
{
    public enum Choice : int { Zero = 0, One = 1 }
    public enum WideChoice : long { Zero = 0, One = 1 }
    public enum UnsignedChoice : ulong { Zero = 0, One = 1 }
    public enum WordChoice : uint { Zero = 0, One = 1 }
    public enum NarrowChoice : ushort { Zero = 0, One = 1 }
    public enum SignedChoice : short { Zero = 0, One = 1 }
    public enum ByteChoice : byte { Zero = 0, One = 1 }
    public enum SByteChoice : sbyte { Zero = 0, One = 1 }
    public static long NullableWordEnumMax(uint input) { WordChoice? value = input == uint.MaxValue ? null : (WordChoice)input; return (long)value.GetValueOrDefault(unchecked((WordChoice)(-7))); }
    public static long NullableWordEnumHighBit(uint input) { WordChoice? value = input == 0x80000000U ? null : (WordChoice)input; return (long)value.GetValueOrDefault(unchecked((WordChoice)(input + 1))); }
    public static long NullableUnsignedEnumMax(ulong input) { UnsignedChoice? value = input == ulong.MaxValue ? null : (UnsignedChoice)input; return (long)value.GetValueOrDefault(unchecked((UnsignedChoice)(-7))); }
    public static long NullableUnsignedEnumHighBit(ulong input) { UnsignedChoice? value = input == 0x8000000000000000UL ? null : (UnsignedChoice)input; return (long)value.GetValueOrDefault(unchecked((UnsignedChoice)(input + 1))); }
    public static long NullableSByteEnumRoundTrip(sbyte input) { SByteChoice? value = (SByteChoice)input; SByteChoice? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableSByteEnumComputed(sbyte input) { SByteChoice? value = unchecked((SByteChoice)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableSByteEnumConditional(sbyte input) { SByteChoice? value = input < 0 ? null : (SByteChoice)input; return (long)value.GetValueOrDefault((SByteChoice)sbyte.MinValue); }
    public static long NullableSByteEnumTruncate(int input) { SByteChoice? value = unchecked((SByteChoice)input); return (long)value.GetValueOrDefault(); }
    public static long NullableSByteEnumDefaultTruncate(int input) { SByteChoice? value = null; return (long)value.GetValueOrDefault(unchecked((SByteChoice)input)); }
    public static long NullableByteEnumRoundTrip(byte input) { ByteChoice? value = (ByteChoice)input; ByteChoice? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableByteEnumComputed(byte input) { ByteChoice? value = unchecked((ByteChoice)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableByteEnumConditional(byte input) { ByteChoice? value = input == 0 ? null : (ByteChoice)input; return (long)value.GetValueOrDefault((ByteChoice)0x80); }
    public static long NullableByteEnumTruncate(int input) { ByteChoice? value = unchecked((ByteChoice)input); return (long)value.GetValueOrDefault(); }
    public static long NullableByteEnumDefaultTruncate(int input) { ByteChoice? value = null; return (long)value.GetValueOrDefault(unchecked((ByteChoice)input)); }
    public static long NullableSignedEnumRoundTrip(short input) { SignedChoice? value = (SignedChoice)input; SignedChoice? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableSignedEnumComputed(short input) { SignedChoice? value = unchecked((SignedChoice)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableSignedEnumConditional(short input) { SignedChoice? value = input < 0 ? null : (SignedChoice)input; return (long)value.GetValueOrDefault((SignedChoice)short.MinValue); }
    public static long NullableSignedEnumTruncate(int input) { SignedChoice? value = unchecked((SignedChoice)input); return (long)value.GetValueOrDefault(); }
    public static long NullableSignedEnumDefaultTruncate(int input) { SignedChoice? value = null; return (long)value.GetValueOrDefault(unchecked((SignedChoice)input)); }
    public static long NullableNarrowEnumRoundTrip(ushort input) { NarrowChoice? value = (NarrowChoice)input; NarrowChoice? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableNarrowEnumComputed(ushort input) { NarrowChoice? value = unchecked((NarrowChoice)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableNarrowEnumConditional(ushort input) { NarrowChoice? value = input == 0 ? null : (NarrowChoice)input; return (long)value.GetValueOrDefault((NarrowChoice)0x8000); }
    public static long NullableNarrowEnumTruncate(int input) { NarrowChoice? value = unchecked((NarrowChoice)input); return (long)value.GetValueOrDefault(); }
    public static long NullableNarrowEnumDefaultTruncate(int input) { NarrowChoice? value = null; return (long)value.GetValueOrDefault(unchecked((NarrowChoice)input)); }
    public static long NullableWordEnumRoundTrip(uint input) { WordChoice? value = (WordChoice)input; WordChoice? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableWordEnumComputed(uint input) { WordChoice? value = unchecked((WordChoice)(input + 1U)); return (long)value.GetValueOrDefault(); }
    public static long NullableWordEnumConditional(uint input) { WordChoice? value = input == 0 ? null : (WordChoice)input; return (long)value.GetValueOrDefault((WordChoice)0x80000000U); }
    public static long NullableWordEnumDefault(uint input) { WordChoice? value = null; return (long)value.GetValueOrDefault(unchecked((WordChoice)(input + 1U))); }
    public static long NullableWordEnumJoin(uint input) { WordChoice? value = (WordChoice)input; if(input == 0) value = null; return (long)value.GetValueOrDefault((WordChoice)uint.MaxValue); }
    public static ulong NullableUnsignedEnumRoundTrip(ulong input) { UnsignedChoice? value = (UnsignedChoice)input; UnsignedChoice? copy = value; return (ulong)copy.GetValueOrDefault(); }
    public static ulong NullableUnsignedEnumComputed(ulong input) { UnsignedChoice? value = unchecked((UnsignedChoice)(input + 1UL)); return (ulong)value.GetValueOrDefault(); }
    public static ulong NullableUnsignedEnumConditional(ulong input) { UnsignedChoice? value = input == 0 ? null : (UnsignedChoice)input; return (ulong)value.GetValueOrDefault((UnsignedChoice)0x8000000000000000UL); }
    public static ulong NullableUnsignedEnumDefault(ulong input) { UnsignedChoice? value = null; return (ulong)value.GetValueOrDefault(unchecked((UnsignedChoice)(input + 1UL))); }
    public static ulong NullableUnsignedEnumJoin(ulong input) { UnsignedChoice? value = (UnsignedChoice)input; if(input == 0) value = null; return (ulong)value.GetValueOrDefault((UnsignedChoice)ulong.MaxValue); }
    public static long NullableWideEnumRoundTrip(long input) { WideChoice? value = (WideChoice)input; WideChoice? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableWideEnumComputed(long input) { WideChoice? value = unchecked((WideChoice)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableWideEnumConditional(long input) { WideChoice? value = input < 0 ? null : (WideChoice)input; return (long)value.GetValueOrDefault((WideChoice)long.MinValue); }
    public static long NullableWideEnumDefault(long input) { WideChoice? value = null; return (long)value.GetValueOrDefault(unchecked((WideChoice)(input + 1))); }
    public static long NullableWideEnumJoin(long input) { WideChoice? value = (WideChoice)input; if(input == 0) value = null; return (long)value.GetValueOrDefault((WideChoice)(-7)); }
    public static long NullableEnumRoundTrip(int input) { Choice? value = (Choice)input; Choice? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableEnumComputed(int input) { Choice? value = unchecked((Choice)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableEnumConditional(int input) { Choice? value = input < 0 ? null : (Choice)input; return (long)value.GetValueOrDefault((Choice)int.MinValue); }
    public static long NullableEnumDefault(int input) { Choice? value = null; return (long)value.GetValueOrDefault(unchecked((Choice)(input + 1))); }
    public static long NullableEnumJoin(int input) { Choice? value = (Choice)input; if(input == 0) value = null; return (long)value.GetValueOrDefault((Choice)(-7)); }
    public static int MinI4() => int.MinValue;
    public static int MaxI4() => int.MaxValue;
    public static ulong FullI8() => 0xfedcba9876543210UL;
    public static ulong AddWide(ulong value) => value + 0x123456789abcdef0UL;
    public static ulong Mask(ulong value) => value & 0xff00ff00ff00ff00UL;
    public static ulong OrWide(ulong value) => value | 0xff00ff00ff00ff00UL;
    public static ulong XorWide(ulong value) => value ^ 0xff00ff00ff00ff00UL;
    public static int Signed16(ulong value) => (short)value;
    public static ulong DivideWide(ulong value) => value / 0x100000001UL;
    public static uint DivideWord(uint value) => value / 0x80000001U;
    public static uint Shift16(uint value) => value << 16;
    public static int NullableRoundTrip(int input) { int? value = input; int? copy = value; return copy.GetValueOrDefault(); }
    public static long NullableUnsignedRoundTrip(uint input) { uint? value = input; uint? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableUInt16RoundTrip(ushort input) { ushort? value = input; ushort? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableUInt8RoundTrip(byte input) { byte? value = input; byte? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableUInt8Computed(byte input) { byte? value = unchecked((byte)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableUInt8Conditional(byte input) { byte? value = input == 0 ? null : input; return (long)value.GetValueOrDefault(0x80); }
    public static long NullableUInt8Truncate(int input) { byte? value = unchecked((byte)input); return (long)value.GetValueOrDefault(); }
    public static long NullableUInt8DefaultTruncate(int input) { byte? value = null; return (long)value.GetValueOrDefault(unchecked((byte)input)); }
    public static long NullableInt8RoundTrip(sbyte input) { sbyte? value = input; sbyte? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableInt8Computed(sbyte input) { sbyte? value = unchecked((sbyte)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableInt8Conditional(sbyte input) { sbyte? value = input < 0 ? null : input; return (long)value.GetValueOrDefault(sbyte.MinValue); }
    public static long NullableInt8Truncate(int input) { sbyte? value = unchecked((sbyte)input); return (long)value.GetValueOrDefault(); }
    public static long NullableInt8DefaultTruncate(int input) { sbyte? value = null; return (long)value.GetValueOrDefault(unchecked((sbyte)input)); }
    public static long NullableInt16RoundTrip(short input) { short? value = input; short? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableInt16Computed(short input) { short? value = unchecked((short)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableInt16Conditional(short input) { short? value = input < 0 ? null : input; return (long)value.GetValueOrDefault(short.MinValue); }
    public static long NullableInt16Truncate(int input) { short? value = unchecked((short)input); return (long)value.GetValueOrDefault(); }
    public static long NullableInt16DefaultTruncate(int input) { short? value = null; return (long)value.GetValueOrDefault(unchecked((short)input)); }
    public static long NullableBooleanRoundTrip(bool input) { bool? value = input; bool? copy = value; return copy.GetValueOrDefault() ? 1L : 0L; }
    public static long NullableBooleanComputed(bool input) { bool? value = !input; return value.GetValueOrDefault() ? 7L : 3L; }
    public static long NullableBooleanConditional(bool input) { bool? value = input ? null : input; return value.GetValueOrDefault(true) ? 7L : 3L; }
    public static long NullableBooleanTruncate(int input) { byte? value = unchecked((byte)input); return (long)value.GetValueOrDefault(); }
    public static long NullableBooleanDefaultTruncate(int input) { byte? value = null; return (long)value.GetValueOrDefault(unchecked((byte)input)); }
    public static long NullableCharRoundTrip(char input) { char? value = input; char? copy = value; return (long)copy.GetValueOrDefault(); }
    public static long NullableCharComputed(char input) { char? value = unchecked((char)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableCharConditional(char input) { char? value = input == 0 ? null : input; return (long)value.GetValueOrDefault((char)0x8000); }
    public static long NullableCharTruncate(int input) { char? value = unchecked((char)input); return (long)value.GetValueOrDefault(); }
    public static long NullableCharDefaultTruncate(int input) { char? value = null; return (long)value.GetValueOrDefault(unchecked((char)input)); }
    public static long NullableUInt16Computed(ushort input) { ushort? value = unchecked((ushort)(input + 1)); return (long)value.GetValueOrDefault(); }
    public static long NullableUInt16Conditional(ushort input) { ushort? value = input == 0 ? null : input; return (long)value.GetValueOrDefault(0x8000); }
    public static long NullableUInt16Truncate(int input) { ushort? value = unchecked((ushort)input); return (long)value.GetValueOrDefault(); }
    public static long NullableUInt16DefaultTruncate(int input) { ushort? value = null; return (long)value.GetValueOrDefault(unchecked((ushort)input)); }
    public static ulong NullableUnsignedWideRoundTrip(ulong input) { ulong? value = input; ulong? copy = value; return copy.GetValueOrDefault(); }
    public static ulong NullableUnsignedWideComputed(ulong input) { ulong? value = input + 1UL; return value.GetValueOrDefault(); }
    public static ulong NullableUnsignedWideEmpty(ulong input) { ulong? value = null; return value.GetValueOrDefault(input + 1UL); }
    public static ulong NullableUnsignedWideConditional(ulong input) { ulong? value = input == 0 ? null : input; return value.GetValueOrDefault(0x8000000000000000UL); }
    public static ulong NullableUnsignedWideJoin(ulong input) { ulong? value = input; if (input == 0) value = null; return value.GetValueOrDefault(ulong.MaxValue); }
    public static long NullableUnsignedComputed(uint input) { uint? value = input + 1U; return (long)value.GetValueOrDefault(); }
    public static long NullableUnsignedEmpty(uint input) { uint? value = null; return (long)value.GetValueOrDefault(input + 1U); }
    public static long NullableUnsignedConditional(uint input) { uint? value = input == 0 ? null : input; return (long)value.GetValueOrDefault(0x80000000U); }
    public static int NullableComputedPresent(int input) { int? value = input; return value.GetValueOrDefault(input + 1); }
    public static long NullableComputedInt32Initializer(int input) { int? value = input + 1; return (long)value.GetValueOrDefault() + input; }
    public static long NullableConditionalInt32Initializer(int input) { int? value = input < 0 ? input - 7 : input + 9; return (long)value.GetValueOrDefault() + input; }
    public static long NullableEagerInt32Initializer(int input) { int? value = input += 1; return (long)value.GetValueOrDefault() + input; }
    public static int NullableNestedEmpty(int input) { int? value = null; int? other = input; return value.GetValueOrDefault(other.GetValueOrDefault()); }
    public static int NullableNestedComputed(int input) { int? value = null; int? other = input < 0 ? null : input; return value.GetValueOrDefault(other.GetValueOrDefault(input + 1)); }
    public static int NullableDeepComputed(int input) { int? value = null; int? other = input < 0 ? null : input; return value.GetValueOrDefault(other.GetValueOrDefault(value.GetValueOrDefault(input + 1))); }
    public static long NullableWideNestedComputed(long input) { long? value = null; long? other = input < 0 ? null : input; return value.GetValueOrDefault(other.GetValueOrDefault(input + 1)); }
    public static long NullableWideDeepComputed(long input) { long? value = null; long? other = input < 0 ? null : input; return value.GetValueOrDefault(other.GetValueOrDefault(value.GetValueOrDefault(input + 1))); }
    public static int NullableNestedConditional(int input) { int? value = null; int? other = input < 0 ? null : input; return value.GetValueOrDefault(other.HasValue ? other.GetValueOrDefault() + 1 : 7); }
    public static long NullableWideNestedEmpty(long input) { long? value = null; long? other = input; return value.GetValueOrDefault(other.GetValueOrDefault()); }
    public static long NullableWideNestedConditional(long input) { long? value = null; long? other = input < 0 ? null : input; return value.GetValueOrDefault(other.HasValue ? other.GetValueOrDefault() + 1 : 7); }
    public static int NullableComputedEmpty(int input) { int? value = null; return value.GetValueOrDefault(input + 1); }
    public static int NullableComputedConditional(int input) { int? value = input < 0 ? null : input; return value.GetValueOrDefault(input < 0 ? input - 7 : input + 9); }
    public static int NullableComputedEager(int input) { int? value = input; int result = value.GetValueOrDefault(input += 1); return result ^ input; }
    public static long NullableWideComputedPresent(long input) { long? value = input; return value.GetValueOrDefault(input + 1); }
    public static long NullableWideComputedEmpty(long input) { long? value = null; return value.GetValueOrDefault(input + 1); }
    public static long NullableWideComputedConditional(long input) { long? value = input < 0 ? null : input; return value.GetValueOrDefault(input < 0 ? input - 7 : input + 9); }
    public static long NullableWideComputedEager(long input) { long? value = input; long result = value.GetValueOrDefault(input += 1); return result ^ input; }
    public static int NullableDefault(int input) { int? value = input; return value.GetValueOrDefault(-7); }
    public static int NullableEmpty(int input) { int? value = null; return value.GetValueOrDefault(input); }
    public static bool NullablePresent(int input) { int? value = input; return value.HasValue; }
    public static int NullablePackedOracle(ulong input) { long value = unchecked((long)input); long packed = ((value << 32) >>> 31) | 1L; return (int)(packed >>> 1); }
    public static ulong NullableWideRoundTrip(ulong input) { long? value = unchecked((long)input); long? copy = value; return unchecked((ulong)copy.GetValueOrDefault()); }
    public static ulong NullableWideDefault(ulong input) { long? value = unchecked((long)input); return unchecked((ulong)value.GetValueOrDefault(long.MinValue)); }
    public static ulong NullableWideEmpty(ulong input) { long? value = null; return unchecked((ulong)value.GetValueOrDefault(unchecked((long)input))); }
    public static bool NullableWidePresent(ulong input) { long? value = unchecked((long)input); return value.HasValue; }
    public static ulong NullableWideConditional(long input) { long? value = input < 0 ? null : input; return unchecked((ulong)value.GetValueOrDefault(long.MinValue)); }
    public static ulong NullableWideReassign(long input) { long? value = input; if (input < 0) value = null; return unchecked((ulong)(value.HasValue ? value.GetValueOrDefault() : long.MaxValue)); }
    public static ulong NullableWideTwoPresent(long input) { long? value = input < 0 ? input : long.MinValue; return unchecked((ulong)value.GetValueOrDefault()); }
}
