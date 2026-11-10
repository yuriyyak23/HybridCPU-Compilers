using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;

internal static class NegationSmoke
{
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(NegationFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach (string method in new[] { nameof(NegationFixture.Word), nameof(NegationFixture.UnsignedWord), nameof(NegationFixture.Wide),
            nameof(NegationFixture.WordNot), nameof(NegationFixture.UnsignedWordNot), nameof(NegationFixture.WideNot) })
        {
            RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(NegationFixture).FullName!, method);
            RestrictedCilImportResultV1 imported = importer.ImportImage(pe, selector);
            bool complement = method.EndsWith("Not", StringComparison.Ordinal);
            if (imported.Status != RestrictedCilImportStatusV1.Success || !imported.Program!.Instructions.Any(i =>
                    complement ? i.Opcode == HybridCpuOpcode.XORI && i.Operands[1].Value == ulong.MaxValue :
                    i.Opcode == HybridCpuOpcode.SUB && i.Operands[0].Kind == IrOperandKind.ArchitecturalRegister && i.Operands[0].Value == 0))
                throw new Exception(method + ": " + string.Join(';', imported.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "negation-smoke", [selector], []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
            if (graph.Status != RestrictedCilImportStatusV1.Success || linked.Status != ScalarControlFlowV2LinkStatusV1.Success)
                throw new Exception(method + " backend/link failed");
            if (complement) {
                byte[] bytes = linked.MethodObjects.Single().ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
                int masks = 0;
                for (int offset = 0; offset < bytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                    HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                    if (!bundle.TryReadBytes(bytes, offset)) throw new Exception("Invalid complement bundle");
                    for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                        HybridCpuInstructionWord word = bundle.GetInstruction(slot);
                        if ((HybridCpuOpcode)word.OpCode != HybridCpuOpcode.XORI) continue;
                        if (slot >= 4 || word.Immediate != ushort.MaxValue || !HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out _) || rd is 0 or >= 32 || rs1 is 0 or >= 32)
                            throw new Exception("Complement must encode XORI -1 with physical ALU operands");
                        masks++;
                    }
                }
                if (masks != 1 || new RestrictedCilImporterV1().ImportImage(pe, selector).Status != RestrictedCilImportStatusV1.Unsupported)
                    throw new Exception("Complement encoded oracle/frozen-v1 isolation failed");
            }
        }

        foreach (int value in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
            if (NegationFixture.Word(value) != unchecked(-value)) throw new Exception("I4 neg parity");
        foreach (uint value in new[] { uint.MinValue, 1u, 0x7fffffffu, 0x80000000u, uint.MaxValue })
            if (NegationFixture.UnsignedWord(value) != unchecked(-(int)value)) throw new Exception("UInt32 I4 carrier neg parity");
        foreach (long value in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue })
            if (NegationFixture.Wide(value) != unchecked(-value)) throw new Exception("I8 neg parity");
        foreach (long value in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0L, 1L, (long)int.MaxValue + 1, long.MaxValue }) {
            if (NegationFixture.WideNot(value) != unchecked(-value - 1) ||
                NegationFixture.WordNot((int)value) != unchecked(-(int)value - 1) ||
                NegationFixture.UnsignedWordNot((uint)value) != unchecked(-(int)(uint)value - 1))
                throw new Exception("Integer complement independent arithmetic host boundary oracle");
        }

        PersistedAssemblyBuilder invalidAssembly = new PersistedAssemblyBuilder(new AssemblyName("NegationInvalidFixture"), typeof(object).Assembly);
        TypeBuilder invalidType = invalidAssembly.DefineDynamicModule("Invalid").DefineType("Invalid", TypeAttributes.Public);
        ILGenerator wrongType = invalidType.DefineMethod("WrongType", MethodAttributes.Public | MethodAttributes.Static, typeof(object), [typeof(object)]).GetILGenerator();
        wrongType.Emit(OpCodes.Ldarg_0); wrongType.Emit(OpCodes.Neg); wrongType.Emit(OpCodes.Ret);
        ILGenerator underflow = invalidType.DefineMethod("Underflow", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
        underflow.Emit(OpCodes.Neg); underflow.Emit(OpCodes.Ret);
        ILGenerator wrongNot = invalidType.DefineMethod("WrongNot", MethodAttributes.Public | MethodAttributes.Static, typeof(object), [typeof(object)]).GetILGenerator();
        wrongNot.Emit(OpCodes.Ldarg_0); wrongNot.Emit(OpCodes.Not); wrongNot.Emit(OpCodes.Ret);
        ILGenerator emptyNot = invalidType.DefineMethod("EmptyNot", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
        emptyNot.Emit(OpCodes.Not); emptyNot.Emit(OpCodes.Ret);
        invalidType.CreateType();
        using MemoryStream invalidPe = new MemoryStream(); invalidAssembly.Save(invalidPe);
        if (!importer.ImportImage(invalidPe.ToArray(), new("Invalid", "WrongType")).Diagnostics.Any(d => d.Code == "HCCIL1872") ||
            !importer.ImportImage(invalidPe.ToArray(), new("Invalid", "Underflow")).Diagnostics.Any(d => d.Code == "HCCIL1873") ||
            !importer.ImportImage(invalidPe.ToArray(), new("Invalid", "WrongNot")).Diagnostics.Any(d => d.Code == "HCCIL1872") ||
            !importer.ImportImage(invalidPe.ToArray(), new("Invalid", "EmptyNot")).Diagnostics.Any(d => d.Code == "HCCIL1873"))
            throw new Exception("neg fail-closed diagnostics");

        Console.WriteLine("PASS integer negation wrapping boundaries, exact type negatives and backend/link");
        Console.WriteLine("PASS integer complement I4/U4/I8 -> encoded XORI -1/ALU physical operands, independent arithmetic host boundaries, type/underflow negatives and frozen-v1 isolation");
    }
}

public static class NegationFixture
{
    public static int Word(int value) => -value;
    public static int UnsignedWord(uint value) => unchecked(-(int)value);
    public static long Wide(long value) => -value;
    public static int WordNot(int value) => ~value;
    public static int UnsignedWordNot(uint value) => unchecked((int)~value);
    public static long WideNot(long value) => ~value;
}
