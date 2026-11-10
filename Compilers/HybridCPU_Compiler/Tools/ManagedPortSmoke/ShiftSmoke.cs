using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

internal static class ShiftSmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("RawShiftFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Shift").DefineType("Shift", TypeAttributes.Public);
        (string, Type, Type, OpCode, HybridCpuOpcode)[] valid = new[]
        {
            ("LeftWord", typeof(int), typeof(int), OpCodes.Shl, HybridCpuOpcode.SLLW),
            ("LeftNative", typeof(nint), typeof(nint), OpCodes.Shl, HybridCpuOpcode.SLL),
            ("SignedWord", typeof(int), typeof(int), OpCodes.Shr, HybridCpuOpcode.SRAW),
            ("UnsignedWord", typeof(uint), typeof(int), OpCodes.Shr_Un, HybridCpuOpcode.SRLW),
            ("SignedWide", typeof(long), typeof(int), OpCodes.Shr, HybridCpuOpcode.SRA),
            ("UnsignedWide", typeof(ulong), typeof(int), OpCodes.Shr_Un, HybridCpuOpcode.SRL)
        };
        foreach ((string? name, Type? value, Type? count, OpCode operation, HybridCpuOpcode _) in valid)
        {
            ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, value, [value, count]).GetILGenerator();
            // Raw CIL: there is deliberately no C#-emitted AND of the count.
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(operation); il.Emit(OpCodes.Ret);
        }
        ILGenerator localProjection = type.DefineMethod("UnsignedWordToSignedLocal", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(uint)]).GetILGenerator();
        localProjection.DeclareLocal(typeof(int));
        localProjection.Emit(OpCodes.Ldarg_0); localProjection.Emit(OpCodes.Ldc_I4_S, 19);
        localProjection.Emit(OpCodes.Shr_Un); localProjection.Emit(OpCodes.Stloc_0);
        localProjection.Emit(OpCodes.Ldloc_0); localProjection.Emit(OpCodes.Ret);
        ILGenerator badLocalProjection = type.DefineMethod("UnsignedWordToWideLocal", MethodAttributes.Public | MethodAttributes.Static,
            typeof(long), [typeof(uint)]).GetILGenerator();
        badLocalProjection.DeclareLocal(typeof(long));
        badLocalProjection.Emit(OpCodes.Ldarg_0); badLocalProjection.Emit(OpCodes.Ldc_I4_1);
        badLocalProjection.Emit(OpCodes.Shr_Un); badLocalProjection.Emit(OpCodes.Stloc_0);
        badLocalProjection.Emit(OpCodes.Ldloc_0); badLocalProjection.Emit(OpCodes.Ret);
        foreach ((string? name, Type? value, Type? count) in new[] { ("BadCount", typeof(int), typeof(long)), ("BadValue", typeof(object), typeof(int)) })
        {
            ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, value, [value, count]).GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Ret);
        }
        ILGenerator underflow = type.DefineMethod("Underflow", MethodAttributes.Public | MethodAttributes.Static, typeof(void), []).GetILGenerator();
        underflow.Emit(OpCodes.Shl); underflow.Emit(OpCodes.Ret);
        type.CreateType(); using MemoryStream stream = new MemoryStream(); assembly.Save(stream); byte[] pe = stream.ToArray();
        Type reference = Assembly.Load(pe).GetType("Shift")!;
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string? name, Type? valueType, Type? countType, OpCode operation, HybridCpuOpcode opcode) in valid)
        {
            RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1("Shift", name);
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "raw-shift", [selector], []));
            if (graph.Status != RestrictedCilImportStatusV1.Success) throw new Exception(name + ": " + string.Join(';', graph.Diagnostics));
            if (!graph.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == opcode)) throw new Exception("Wrong shift native width");
            ScalarControlFlowV2MethodObjectV1 Compile() => (ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
                .GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [graph.Methods.Single(), 0])!;
            ScalarControlFlowV2MethodObjectV1 native = Compile();
            if (native.ObjectArtifact.Status != HybridCpuObjectStatusV1.Success) throw new Exception("shl native object failed");
            byte[] code = native.ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            if (!code.SequenceEqual(Compile().ObjectArtifact.Sections.Single(s => s.Name == ".text").Data)) throw new Exception("shl HCO nondeterminism");
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception("shl image/link gate failed");
            foreach (ulong bits in new ulong[] { 0, 1, 0x7fffffff, 0x80000000, uint.MaxValue, 0x123456789abcdef0, 0x8000000000000000, ulong.MaxValue })
            foreach (int count in new[] { int.MinValue, -129, -65, -33, -1, 0, 1, 16, 31, 32, 33, 63, 64, 65, 127, int.MaxValue })
            {
                object value = valueType == typeof(int) ? (object)unchecked((int)bits) :
                    valueType == typeof(uint) ? unchecked((uint)bits) :
                    valueType == typeof(long) ? unchecked((long)bits) :
                    valueType == typeof(ulong) ? bits : (nint)unchecked((long)bits);
                object amount = countType == typeof(nint) ? (object)(nint)count : count;
                object expected = reference.GetMethod(name)!.Invoke(null, [value, amount])!;
                ulong expectedBits = expected switch
                {
                    int word => unchecked((ulong)(long)word), uint word => word,
                    long wide => unchecked((ulong)wide), ulong wide => wide,
                    _ => unchecked((ulong)(long)(nint)expected)
                };
                ulong actual = NativeConstantSmoke.Evaluate(code, bits, unchecked((ulong)(long)count));
                if (actual != expectedBits) throw new Exception($"{name} {bits:x16} {operation.Name} {count}: native {actual:x16}, CLR {expectedBits:x16}");
            }
        }
        foreach ((string? name, string? diagnostic) in new[] { ("BadCount", "HCCIL1850"), ("BadValue", "HCCIL1850"), ("Underflow", "HCCIL1851") })
            if (!importer.ImportImage(pe, new("Shift", name)).Diagnostics.Any(d => d.Code == diagnostic)) throw new Exception(name + " must fail closed");
        if (new RestrictedCilImporterV1().ImportImage(pe, new("Shift", "SignedWord")).Status == RestrictedCilImportStatusV1.Success)
            throw new Exception("Frozen V1 profile must not acquire shifts implicitly");
        ManagedCallGraphCompilationV1 projected = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "raw-shift-local",
            [new("Shift", "UnsignedWordToSignedLocal")], []));
        if (projected.Status != RestrictedCilImportStatusV1.Success ||
            !projected.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.ADDIW))
            throw new Exception("UInt32-to-Int32 local projection must use exact I4 normalization");
        if (new ScalarControlFlowV2ObjectLinkerV1().Link(projected, projected.Graph!.RootIdentities.Single()).Status != ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("UInt32-to-Int32 local projection backend/link failed");
        if (!importer.ImportImage(pe, new("Shift", "UnsignedWordToWideLocal")).Diagnostics.Any(d => d.Code == "HCCIL0034"))
            throw new Exception("UInt32-to-Int64 local projection must remain fail-closed");
        Console.WriteLine("PASS raw CIL shl/shr/shr.un: encoded word/wide signedness, count masking, backend determinism vs CoreCLR");
    }
}
