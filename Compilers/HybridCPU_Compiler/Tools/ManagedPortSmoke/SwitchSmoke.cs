using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

internal static class SwitchSmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("RawSwitchFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Switch").DefineType("Switch", TypeAttributes.Public);
        EmitSwitch(type, "Select", typeof(int), validSelector: true);
        EmitSwitch(type, "WrongSelector", typeof(object), validSelector: false);
        ILGenerator unsigned = type.DefineMethod("LessOrEqualUnsigned", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(uint), typeof(uint)]).GetILGenerator();
        Label unsignedTrue = unsigned.DefineLabel();
        unsigned.Emit(OpCodes.Ldarg_0); unsigned.Emit(OpCodes.Ldarg_1); unsigned.Emit(OpCodes.Ble_Un_S, unsignedTrue);
        unsigned.Emit(OpCodes.Ldc_I4_0); unsigned.Emit(OpCodes.Ret);
        unsigned.MarkLabel(unsignedTrue); unsigned.Emit(OpCodes.Ldc_I4_1); unsigned.Emit(OpCodes.Ret);
        EmitMixedUnsignedBranch(type, "UIntBelowConstant", constant: 4096);
        EmitMixedUnsignedBranch(type, "UIntBelowNegativeConstant", constant: -1);
        EmitMixedUnsignedDynamicBranch(type);
        foreach ((string? name, OpCode branch, bool reverse) in new[] {
            ("MixedI8EqualShort", OpCodes.Beq_S, false), ("MixedI8EqualLong", OpCodes.Beq, true),
            ("MixedI8NotEqualShort", OpCodes.Bne_Un_S, true), ("MixedI8NotEqualLong", OpCodes.Bne_Un, false) })
        {
            EmitMixedIntegerEquality(type, name, branch, reverse, typeof(long));
            EmitMixedIntegerEquality(type, name.Replace("I8", "I4"), branch, reverse, typeof(int), typeof(uint));
        }
        EmitMixedIntegerEquality(type, "MixedI8I4", OpCodes.Beq_S, false, typeof(int));
        EmitMixedIntegerEquality(type, "MixedI8Object", OpCodes.Bne_Un_S, false, typeof(object));
        EmitMixedIntegerEquality(type, "MixedI4I8", OpCodes.Beq_S, false, typeof(long), typeof(uint));
        EmitMixedIntegerEquality(type, "MixedI4Object", OpCodes.Bne_Un_S, false, typeof(object), typeof(uint));
        EmitObjectBranch(type, "ObjectNotSame", OpCodes.Bne_Un_S);
        EmitObjectBranch(type, "ObjectRelational", OpCodes.Blt_Un_S);
        EmitFarConditional(type);
        ILGenerator underflow = type.DefineMethod("Underflow", MethodAttributes.Public | MethodAttributes.Static, typeof(int), []).GetILGenerator();
        Label underflowTarget = underflow.DefineLabel();
        underflow.Emit(OpCodes.Switch, [underflowTarget]);
        underflow.Emit(OpCodes.Ldc_I4_0); underflow.Emit(OpCodes.Ret);
        underflow.MarkLabel(underflowTarget); underflow.Emit(OpCodes.Ldc_I4_1); underflow.Emit(OpCodes.Ret);
        type.CreateType();
        using MemoryStream stream = new MemoryStream(); assembly.Save(stream); byte[] pe = stream.ToArray();

        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "raw-switch",
            [new("Switch", "Select")], []));
        if (graph.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("switch: " + string.Join(';', graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCompiledMethodV1 method = graph.Methods.Single();
        if (method.Import.Program!.Instructions.Count(i => i.Opcode == HybridCpuOpcode.BNE) != 3 ||
            method.Import.Program.Instructions.Count(i => i.Opcode == HybridCpuOpcode.AUIPC) != 3)
            throw new Exception("switch must lower to one exact inverted-branch plus AUIPC/JALR transfer per table entry");
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
        if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("switch backend/link failed");
        ManagedCallGraphCompilationV1 unsignedGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "raw-unsigned-branch",
            [new("Switch", "LessOrEqualUnsigned")], []));
        if (unsignedGraph.Status != RestrictedCilImportStatusV1.Success ||
            !unsignedGraph.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.BLTU) ||
            new ScalarControlFlowV2ObjectLinkerV1().Link(unsignedGraph, unsignedGraph.Graph!.RootIdentities.Single()).Status != ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("ble.un.s exact unsigned backend/link lowering failed");
        RestrictedCilImportResultV1 mixedConstant = importer.ImportImage(pe, new("Switch", "UIntBelowConstant"));
        if (mixedConstant.Status != RestrictedCilImportStatusV1.Success ||
            !mixedConstant.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.BGEU))
            throw new Exception("UInt32 relational branch against a non-negative exact I4 constant must lower unsigned");
        foreach (string name in new[] { "UIntBelowNegativeConstant", "UIntBelowDynamicInt32" })
            if (!importer.ImportImage(pe, new("Switch", name)).Diagnostics.Any(d => d.Code == "HCCIL1111"))
                throw new Exception(name + " must preserve the mixed relational-type fail-closed gate");
        foreach (string name in new[] { "MixedI8I4", "MixedI8Object", "MixedI4I8", "MixedI4Object" })
            if (!importer.ImportImage(pe, new("Switch", name)).Diagnostics.Any(d => d.Code == "HCCIL1111"))
                throw new Exception(name + " must preserve equality width/reference rejection");
        Type host = Assembly.Load(pe).GetType("Switch")!;
        foreach (string name in new[] { "MixedI8EqualShort", "MixedI8EqualLong", "MixedI8NotEqualShort", "MixedI8NotEqualLong" }) {
            ManagedCallGraphCompilationV1 equalityGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "raw-mixed-i8-equality", [new("Switch", name)], []));
            if (equalityGraph.Status != RestrictedCilImportStatusV1.Success) throw new Exception(name + ": " + string.Join(';', equalityGraph.Diagnostics));
            ScalarControlFlowV2MethodObjectV1 obj = (ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
                .GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { equalityGraph.Methods.Single(), 0 })!;
            if (obj.ObjectArtifact.Status != HybridCpuObjectStatusV1.Success) throw new Exception("I8 equality HCO failed");
            byte[] code = obj.ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            foreach (ulong left in new ulong[] { 0, 1, 1UL << 32, 1UL << 63, ulong.MaxValue })
                foreach (long right in new long[] { 0, 1, 1L << 32, long.MinValue, -1 }) {
                    int expected = (name.Contains("NotEqual") ? left != unchecked((ulong)right) : left == unchecked((ulong)right)) ? 1 : 0;
                    if (!Equals(host.GetMethod(name)!.Invoke(null, [left, right]), expected) ||
                        NativeConstantSmoke.Evaluate(code, left, unchecked((ulong)right)) != (ulong)expected)
                        throw new Exception(name + " CLI/physical encoded equality differs from scalar bits");
                }
        }
        Console.WriteLine("PASS mixed signed/unsigned I8 equality short/long/reversed branches: scalar/CoreCLR/serialized words; width/reference negatives");
        foreach (string name in new[] { "MixedI4EqualShort", "MixedI4EqualLong", "MixedI4NotEqualShort", "MixedI4NotEqualLong" }) {
            ManagedCallGraphCompilationV1 equalityGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "raw-mixed-i4-equality", [new("Switch", name)], []));
            if (equalityGraph.Status != RestrictedCilImportStatusV1.Success) throw new Exception(name + ": " + string.Join(';', equalityGraph.Diagnostics));
            ScalarControlFlowV2MethodObjectV1 obj = (ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
                .GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { equalityGraph.Methods.Single(), 0 })!;
            if (obj.ObjectArtifact.Status != HybridCpuObjectStatusV1.Success) throw new Exception("I4 equality HCO failed");
            byte[] code = obj.ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            foreach (uint left in new uint[] { 0, 1, int.MaxValue, 1U << 31, uint.MaxValue })
                foreach (int right in new int[] { 0, 1, int.MaxValue, int.MinValue, -1 }) {
                    int expected = (name.Contains("NotEqual") ? left != unchecked((uint)right) : left == unchecked((uint)right)) ? 1 : 0;
                    if (!Equals(host.GetMethod(name)!.Invoke(null, [left, right]), expected) ||
                        NativeConstantSmoke.Evaluate(code, unchecked((ulong)(long)(int)left), unchecked((ulong)(long)right)) != (ulong)expected)
                        throw new Exception(name + " CLI/canonical I4 ABI encoded equality differs from scalar bits");
                }
        }
        Console.WriteLine("PASS mixed signed/unsigned I4 equality short/long/reversed branches: scalar/CoreCLR/serialized words; width/reference negatives");
        ManagedCallGraphCompilationV1 objectEquality = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "raw-object-equality", [new("Switch", "ObjectNotSame")], []));
        if (objectEquality.Status != RestrictedCilImportStatusV1.Success ||
            !objectEquality.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.BEQ))
            throw new Exception("bne.un.s must admit exact object-reference equality and lower to BNE");
        if (!importer.ImportImage(pe, new("Switch", "ObjectRelational")).Diagnostics.Any(d => d.Code == "HCCIL1111"))
            throw new Exception("Ordered object-reference branches must remain fail closed");

        foreach ((string? name, string? diagnostic) in new[] { ("WrongSelector", "HCCIL1864"), ("Underflow", "HCCIL1865") })
            if (!importer.ImportImage(pe, new("Switch", name)).Diagnostics.Any(d => d.Code == diagnostic))
                throw new Exception(name + " must fail closed with " + diagnostic);
        if (new RestrictedCilImporterV1().ImportImage(pe, new("Switch", "Select")).Status == RestrictedCilImportStatusV1.Success)
            throw new Exception("Frozen V1 profile must not acquire switch implicitly");
        ManagedCallGraphCompilationV1 far = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "raw-far-branch", [new("Switch", "FarConditional")], []));
        if (far.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("far branch import failed: " + string.Join(';', far.Diagnostics));
        IrProgram farProgram = far.Methods.Single().Import.Program!;
        bool hasHigh = farProgram.Instructions.Any(instruction => instruction.Opcode == HybridCpuOpcode.AUIPC &&
                instruction.StableIdentity.EndsWith(":long-branch-high", StringComparison.Ordinal)) ||
            false;
        bool hasLow = farProgram.Instructions.Any(instruction => instruction.Opcode == HybridCpuOpcode.JALR &&
                instruction.StableIdentity.EndsWith(":long-branch-low", StringComparison.Ordinal));
        ScalarControlFlowV2LinkedProgramV1 farLinked = new ScalarControlFlowV2ObjectLinkerV1().Link(far, far.Graph!.RootIdentities.Single());
        if (!hasHigh || !hasLow || farLinked.Status != ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception($"far conditional branch must use exact AUIPC/JALR relaxation and survive backend relocation: " +
                $"IR={farProgram.Instructions.Count}; high={hasHigh}; low={hasLow}; link={farLinked.Status}; " +
                $"branches={string.Join(',', farProgram.Instructions.Where(i => i.Annotation.ResolvedBranchTargetInstructionIndex.HasValue).Select(i => $"{i.Index}:{i.StableIdentity}->{i.Annotation.ResolvedBranchTargetInstructionIndex}"))}; " +
                $"diagnostics={string.Join(';', farLinked.Diagnostics)}");
        Console.WriteLine("PASS bounded switch and unsigned relational branch decode/CFG/lowering, backend link, negatives and frozen-v1 isolation");
    }

    private static void EmitMixedIntegerEquality(TypeBuilder type, string name, OpCode branch, bool reverse, Type rightType, Type? leftType = null)
    {
        ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [leftType ?? typeof(ulong), rightType]).GetILGenerator();
        Label taken = il.DefineLabel();
        il.Emit(reverse ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0);
        il.Emit(reverse ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
        il.Emit(branch, taken);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        il.MarkLabel(taken); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
    }

    private static void EmitSwitch(TypeBuilder type, string name, Type selectorType, bool validSelector)
    {
        ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(int), [selectorType]).GetILGenerator();
        Label[] cases = [il.DefineLabel(), il.DefineLabel(), il.DefineLabel()];
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Switch, cases);
        il.Emit(OpCodes.Ldc_I4, 40); il.Emit(OpCodes.Ret);
        for (int index = 0; index < cases.Length; index++)
        {
            il.MarkLabel(cases[index]);
            il.Emit(OpCodes.Ldc_I4, 10 + index);
            il.Emit(OpCodes.Ret);
        }
    }

    private static void EmitObjectBranch(TypeBuilder type, string name, OpCode branch)
    {
        ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(object), typeof(object)]).GetILGenerator();
        Label target = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(branch, target);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        il.MarkLabel(target); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
    }

    private static void EmitMixedUnsignedBranch(TypeBuilder type, string name, int constant)
    {
        ILGenerator il = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(uint)]).GetILGenerator();
        Label target = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, constant); il.Emit(OpCodes.Blt_Un_S, target);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        il.MarkLabel(target); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
    }

    private static void EmitMixedUnsignedDynamicBranch(TypeBuilder type)
    {
        ILGenerator il = type.DefineMethod("UIntBelowDynamicInt32", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(uint), typeof(int)]).GetILGenerator();
        Label target = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Blt_Un_S, target);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        il.MarkLabel(target); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
    }

    private static void EmitFarConditional(TypeBuilder type)
    {
        ILGenerator il = type.DefineMethod("FarConditional", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int)]).GetILGenerator();
        Label target = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, target);
        il.Emit(OpCodes.Ldarg_0);
        for (int index = 0; index < 96; index++)
        {
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Add);
        }
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(target);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }
}
