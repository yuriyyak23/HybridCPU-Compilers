using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.ManagedRuntime;
using HybridCPU.Compiler.Core.Target.Object;

internal static class UnsignedDivisionSmoke
{
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(UnsignedDivisionFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string? name, HybridCpuOpcode opcode) in new[] { ("Word", HybridCpuOpcode.DIVUW), ("Wide", HybridCpuOpcode.DIVU),
            ("HighWord", HybridCpuOpcode.DIVUW) })
        {
            RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(UnsignedDivisionFixture).FullName!, name);
            RestrictedCilImportResultV1 result = importer.ImportImage(pe, selector);
            if (result.Status != RestrictedCilImportStatusV1.Success || !result.Program!.Instructions.Any(i => i.Opcode == opcode))
                throw new Exception(name + ": " + string.Join(";", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "division-smoke", [selector], []));
            if (graph.Status != RestrictedCilImportStatusV1.Success) throw new Exception("division graph: " + string.Join(";", graph.Diagnostics.Select(d => d.Message)));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success)
                throw new Exception(name + " backend: " + string.Join(";", linked.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            IrInstruction division = result.Program.Instructions.Single(i => i.Opcode == opcode);
            // Evaluate the emitted arithmetic fragment, including actual materialized constant
            // bits. This is an IR semantic check, not a claim of native guest execution.
            foreach (ulong input in new ulong[] { 0, 1, 17, 18, 19, 0x7fffffff, 0x80000000, uint.MaxValue, 0xa5a5a5a5ffffffff, ulong.MaxValue })
            {
                ulong Eval(IrOperand operand)
                {
                    if (operand.Kind == IrOperandKind.Constant) return operand.Value;
                    if (operand.Kind == IrOperandKind.ArchitecturalRegister) return operand.Value == 0 ? 0 : input;
                    if (operand.Name.EndsWith(":arg:0", StringComparison.Ordinal)) return input;
                    IrInstruction producer = result.Program.Instructions.Single(i => i.Annotation.Defs.Any(d => d.Kind == operand.Kind && d.Name == operand.Name));
                    ulong a = Eval(producer.Operands[0]), b = Eval(producer.Operands[1]);
                    return producer.Opcode switch
                    {
                        HybridCpuOpcode.ADDI or HybridCpuOpcode.ADD => unchecked(a + b),
                        HybridCpuOpcode.SLLI => a << (int)b,
                        HybridCpuOpcode.ORI => a | b,
                        _ => throw new Exception("Unexpected materialization opcode " + producer.Opcode)
                    };
                }
                ulong left = Eval(division.Operands[0]), right = Eval(division.Operands[1]);
                ulong actual = opcode == HybridCpuOpcode.DIVUW ? (uint)left / (uint)right : left / right;
                ulong expected = name == "Wide" ? UnsignedDivisionFixture.Wide(input) : name == "HighWord"
                    ? UnsignedDivisionFixture.HighWord((uint)input) : UnsignedDivisionFixture.Word((uint)input);
                if (actual != expected) throw new Exception(name + " emitted arithmetic differs from CoreCLR");
            }
        }
        RestrictedCilImportResultV1 dynamicResult = importer.ImportImage(pe, new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.Dynamic)));
        if (dynamicResult.Status != RestrictedCilImportStatusV1.Success ||
            !dynamicResult.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_divide_u4_checked") ||
            dynamicResult.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.DIVU or HybridCpuOpcode.DIVUW))
            throw new Exception("Dynamic divisor must use only the exact managed checked helper");
        foreach ((string? wideMethod, HybridCpuOpcode wideOpcode) in new[] { (nameof(UnsignedDivisionFixture.DynamicWide), HybridCpuOpcode.DIVU),
            (nameof(UnsignedDivisionFixture.DynamicUnsignedRemainderWide), HybridCpuOpcode.REMU) }) {
            RestrictedCilImportResultV1 wideDynamic = importer.ImportImage(pe, new(typeof(UnsignedDivisionFixture).FullName!, wideMethod));
        if (wideDynamic.Status != RestrictedCilImportStatusV1.Success || !wideDynamic.RequiresNativeExceptionTransfer)
            throw new Exception("Unsigned I8 dynamic import/transfer");
            IrInstruction wideDivision = wideDynamic.Program!.Instructions.Single(i => i.Opcode == wideOpcode);
            IrInstruction guardedDivisor = wideDynamic.Program.Instructions.Single(i => i.Opcode == HybridCpuOpcode.ADD &&
            i.Annotation.Defs.Any(d => d.Name == wideDivision.Operands[1].Name));
            IrInstruction probeCopy = wideDynamic.Program.Instructions.Single(i => i.Annotation.Defs.Any(d => d.Name == guardedDivisor.Operands[1].Name));
        if (!wideDynamic.Program.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == HybridCpuManagedDivideInt64EmitterV1.Symbol &&
                i.Annotation.Defs.Any(d => d.Name == probeCopy.Operands[0].Name)) ||
            !wideDynamic.Program.Instructions.Any(i => i.StableIdentity.EndsWith(":u8-zero-probe:arg-copy:0", StringComparison.Ordinal) &&
                i.Opcode == HybridCpuOpcode.ADDI && i.Operands[0].Kind == IrOperandKind.ArchitecturalRegister && i.Operands[0].Value == 0 && i.Immediate == 0) ||
            wideDynamic.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.DIV or HybridCpuOpcode.DIVUW or HybridCpuOpcode.REMW or HybridCpuOpcode.REMUW))
            throw new Exception("Unsigned I8 quotient must depend on checked zero probe through exact I8 add");
        foreach (ulong n in new[] { 0UL, 1UL, 17UL, (ulong)uint.MaxValue + 1, 1UL << 63, ulong.MaxValue })
            foreach (ulong d in new[] { 0UL, 1UL, 3UL, (ulong)uint.MaxValue + 1, 1UL << 63, ulong.MaxValue }) {
                if (d == 0) {
                    try { ManagedCheckedArithmeticRuntimeV1.DivideInt64(0, unchecked((long)d)); throw new Exception("missing zero fault"); }
                    catch (DivideByZeroException) { }
                } else {
                    long probe = ManagedCheckedArithmeticRuntimeV1.DivideInt64(0, unchecked((long)d));
                    if (probe != 0 || (wideOpcode == HybridCpuOpcode.DIVU
                        ? n / unchecked(d + (ulong)probe) != UnsignedDivisionFixture.DynamicWide(n, d)
                        : n % unchecked(d + (ulong)probe) != UnsignedDivisionFixture.DynamicUnsignedRemainderWide(n, d)))
                        throw new Exception("Unsigned I8 independent probe/numeric boundary oracle");
                }
            }
        }
        PersistedAssemblyBuilder zeroAssembly = new PersistedAssemblyBuilder(new AssemblyName("DivisionZeroFixture"), typeof(object).Assembly);
        TypeBuilder zeroType = zeroAssembly.DefineDynamicModule("Zero").DefineType("Zero", TypeAttributes.Public);
        ILGenerator zero = zeroType.DefineMethod("Divide", MethodAttributes.Public | MethodAttributes.Static, typeof(uint), [typeof(uint)]).GetILGenerator();
        zero.Emit(OpCodes.Ldarg_0); zero.Emit(OpCodes.Ldc_I4_0); zero.Emit(OpCodes.Div_Un); zero.Emit(OpCodes.Ret);
        zeroType.CreateType();
        using MemoryStream zeroPe = new MemoryStream(); zeroAssembly.Save(zeroPe);
        RestrictedCilImportResultV1 zeroResult = importer.ImportImage(zeroPe.ToArray(), new("Zero", "Divide"));
        if (zeroResult.Status != RestrictedCilImportStatusV1.Success ||
            !zeroResult.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_divide_u4_checked") ||
            zeroResult.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.DIVU or HybridCpuOpcode.DIVUW))
            throw new Exception("A literal zero must lower to the managed checked helper, never native DIVU/DIVUW");
        // Reference values exercise unsigned boundaries; native ISA operand width is checked
        // above by the actual emitted opcode, not by substituting signed division.
        foreach (uint value in new uint[] { 0, 1, 17, 18, 19, 0x7fffffff, 0x80000000, uint.MaxValue })
        {
            if (UnsignedDivisionFixture.Word(value) != (uint)((ulong)value / 18) ||
                UnsignedDivisionFixture.HighWord(value) != (uint)((ulong)value / 0x80000001)) throw new Exception("CoreCLR I4 parity");
        }
        foreach (ulong value in new ulong[] { 0, 1, 18, 0x7fffffffffffffff, 0x8000000000000000, ulong.MaxValue })
            if (UnsignedDivisionFixture.Wide(value) != value / 0x100000001UL) throw new Exception("CoreCLR I8 parity");
        try { UnsignedDivisionFixture.Dynamic(1, 0); throw new Exception("CoreCLR zero division must throw"); }
        catch (DivideByZeroException) { }
        if (ManagedCheckedArithmeticRuntimeV1.DivideUInt32(uint.MaxValue, 3) != uint.MaxValue / 3)
            throw new Exception("Managed checked division runtime result mismatch");
        try { ManagedCheckedArithmeticRuntimeV1.DivideUInt32(1, 0); throw new Exception("Runtime helper must throw DivideByZeroException"); }
        catch (DivideByZeroException) { }
        HybridCpuRuntimeHelperV1? divideAbi = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedDivideUInt32EmitterV1.Symbol);
        HybridCpuObjectArtifactV1 divideObject = HybridCpuManagedDivideUInt32EmitterV1.EmitObject();
        if (divideAbi is not { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } ||
            divideObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !divideObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedDivideUInt32EmitterV1.Symbol &&
                symbol.IsDefinition))
            throw new Exception("Checked UInt32 division must bind an exact conditionally-allocating CPU thunk");
        TestSigned(importer, pe);
        TestRemainder(importer, pe);
        Console.WriteLine("PASS signed/unsigned division width, exception proof gates, CoreCLR boundaries and backend/link");
    }

    private static void TestSigned(RestrictedCilImporterV1 importer, byte[] pe)
    {
        foreach ((string? name, HybridCpuOpcode opcode) in new[]
        {
            (nameof(UnsignedDivisionFixture.SignedWord), HybridCpuOpcode.DIVW),
            (nameof(UnsignedDivisionFixture.SignedNegativeDivisor), HybridCpuOpcode.DIVW),
            (nameof(UnsignedDivisionFixture.SignedWide), HybridCpuOpcode.DIV)
        })
        {
            RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(UnsignedDivisionFixture).FullName!, name);
            RestrictedCilImportResultV1 result = importer.ImportImage(pe, selector);
            if (result.Status != RestrictedCilImportStatusV1.Success || !result.Program!.Instructions.Any(i => i.Opcode == opcode))
                throw new Exception(name + ": " + string.Join(";", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "signed-division-smoke", [selector], []));
            if (graph.Status != RestrictedCilImportStatusV1.Success) throw new Exception(name + " graph: " + string.Join(";", graph.Diagnostics.Select(d => d.Message)));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success)
                throw new Exception(name + " backend: " + string.Join(";", linked.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }

        RestrictedCilImportResultV1 dynamicSigned = importer.ImportImage(pe,
            new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.DynamicSigned)));
        if (dynamicSigned.Status != RestrictedCilImportStatusV1.Success ||
            !dynamicSigned.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_divide_i4_checked") ||
            dynamicSigned.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.DIV or HybridCpuOpcode.DIVW))
            throw new Exception("Dynamic signed divisor must use the managed zero/overflow helper");

        RestrictedCilImportResultV1 dynamicSignedWide = importer.ImportImage(pe,
            new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.DynamicSignedWide)));
        if (dynamicSignedWide.Status != RestrictedCilImportStatusV1.Success ||
            !dynamicSignedWide.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_divide_i8_checked") ||
            dynamicSignedWide.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.DIV or HybridCpuOpcode.DIVW))
            throw new Exception("Dynamic signed I8 divisor must use the managed zero/overflow helper");

        PersistedAssemblyBuilder unsafeAssembly = new PersistedAssemblyBuilder(new AssemblyName("SignedDivisionUnsafeFixture"), typeof(object).Assembly);
        TypeBuilder unsafeType = unsafeAssembly.DefineDynamicModule("Unsafe").DefineType("Unsafe", TypeAttributes.Public);
        ILGenerator minusOne = unsafeType.DefineMethod("MinusOne", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]).GetILGenerator();
        minusOne.Emit(OpCodes.Ldarg_0); minusOne.Emit(OpCodes.Ldc_I4_M1); minusOne.Emit(OpCodes.Div); minusOne.Emit(OpCodes.Ret);
        ILGenerator zero = unsafeType.DefineMethod("Zero", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]).GetILGenerator();
        zero.Emit(OpCodes.Ldarg_0); zero.Emit(OpCodes.Ldc_I4_0); zero.Emit(OpCodes.Div); zero.Emit(OpCodes.Ret);
        unsafeType.CreateType();
        using MemoryStream unsafePe = new MemoryStream(); unsafeAssembly.Save(unsafePe);
        foreach (string method in new[] { "MinusOne", "Zero" })
            if (importer.ImportImage(unsafePe.ToArray(), new("Unsafe", method)).Program?.Instructions.Any(i =>
                    i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_divide_i4_checked") != true)
                throw new Exception(method + " must retain managed signed-division exception semantics");

        foreach (int value in new[] { int.MinValue, -1025, -17, -1, 0, 1, 17, 1025, int.MaxValue })
        {
            if (UnsignedDivisionFixture.SignedWord(value) != value / 16 ||
                UnsignedDivisionFixture.SignedNegativeDivisor(value) != value / -2)
                throw new Exception("CoreCLR signed I4 parity");
        }
        foreach (long value in new[] { long.MinValue, -1025L, -17L, -1L, 0L, 1L, 17L, 1025L, long.MaxValue })
            if (UnsignedDivisionFixture.SignedWide(value) != value / 0x100000001L) throw new Exception("CoreCLR signed I8 parity");
        try { UnsignedDivisionFixture.DynamicSigned(1, 0); throw new Exception("CoreCLR signed zero division must throw"); }
        catch (DivideByZeroException) { }
        try { UnsignedDivisionFixture.DynamicSigned(int.MinValue, -1); throw new Exception("CoreCLR signed overflow must throw"); }
        catch (OverflowException) { }
        if (ManagedCheckedArithmeticRuntimeV1.DivideInt32(int.MaxValue, 3) != int.MaxValue / 3)
            throw new Exception("Signed checked helper result mismatch");
        try { ManagedCheckedArithmeticRuntimeV1.DivideInt32(1, 0); throw new Exception("signed helper zero"); }
        catch (DivideByZeroException) { }
        try { ManagedCheckedArithmeticRuntimeV1.DivideInt32(int.MinValue, -1); throw new Exception("signed helper overflow"); }
        catch (OverflowException) { }
        HybridCpuObjectArtifactV1 divideInt32Object = HybridCpuManagedDivideInt32EmitterV1.EmitObject();
        if (HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedDivideInt32EmitterV1.Symbol) is not
                { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, MayThrow: true,
                  Support: HybridCpuManagedAbiSupportV1.Supported } ||
            divideInt32Object.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !divideInt32Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedDivideInt32EmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("checked signed I4 division exact ABI/HCO");
        if (ManagedCheckedArithmeticRuntimeV1.DivideInt64(long.MaxValue, 3) != long.MaxValue / 3)
            throw new Exception("Signed checked I8 helper result mismatch");
        try { ManagedCheckedArithmeticRuntimeV1.DivideInt64(1, 0); throw new Exception("signed I8 helper zero"); }
        catch (DivideByZeroException) { }
        try { ManagedCheckedArithmeticRuntimeV1.DivideInt64(long.MinValue, -1); throw new Exception("signed I8 helper overflow"); }
        catch (OverflowException) { }
        HybridCpuObjectArtifactV1 divideInt64Object = HybridCpuManagedDivideInt64EmitterV1.EmitObject();
        if (HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedDivideInt64EmitterV1.Symbol) is not
                { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, MayThrow: true,
                  Support: HybridCpuManagedAbiSupportV1.Supported } ||
            divideInt64Object.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !divideInt64Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedDivideInt64EmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("checked signed I8 division exact ABI/HCO");
    }

    private static void TestRemainder(RestrictedCilImporterV1 importer, byte[] pe)
    {
        foreach ((string? name, HybridCpuOpcode opcode) in new[]
        {
            (nameof(UnsignedDivisionFixture.SignedRemainderWord), HybridCpuOpcode.REMW),
            (nameof(UnsignedDivisionFixture.UnsignedRemainderWord), HybridCpuOpcode.REMUW),
            (nameof(UnsignedDivisionFixture.SignedRemainderWide), HybridCpuOpcode.REM),
            (nameof(UnsignedDivisionFixture.SignedRemainderWideTen), HybridCpuOpcode.REM),
            (nameof(UnsignedDivisionFixture.UnsignedRemainderWide), HybridCpuOpcode.REMU)
        })
        {
            RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(UnsignedDivisionFixture).FullName!, name);
            RestrictedCilImportResultV1 result = importer.ImportImage(pe, selector);
            if (result.Status != RestrictedCilImportStatusV1.Success || !result.Program!.Instructions.Any(i => i.Opcode == opcode))
                throw new Exception(name + ": " + string.Join(";", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "remainder-smoke", [selector], []));
            ScalarControlFlowV2LinkedProgramV1? linked = graph.Status == RestrictedCilImportStatusV1.Success
                ? new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single()) : null;
            if (linked?.Status != ScalarControlFlowV2LinkStatusV1.Success)
                throw new Exception(name + " remainder backend/link failed");
        }
        RestrictedCilImportResultV1 dynamicRemainder = importer.ImportImage(pe,
            new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.DynamicRemainder)));
        if (dynamicRemainder.Status != RestrictedCilImportStatusV1.Success ||
            !dynamicRemainder.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_remainder_i4_checked") ||
            dynamicRemainder.Program.Instructions.Any(i => i.Opcode == HybridCpuOpcode.REMW))
            throw new Exception("dynamic remainder needs managed zero/overflow paths");
        RestrictedCilImportResultV1 unsignedWord = importer.ImportImage(pe, new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.DynamicUnsignedRemainder)));
        if (unsignedWord.Status != RestrictedCilImportStatusV1.Success || !unsignedWord.RequiresNativeExceptionTransfer)
            throw new Exception("unsigned I4 remainder import/transfer");
        IrInstruction wordRem = unsignedWord.Program!.Instructions.Single(i => i.Opcode == HybridCpuOpcode.REMUW);
        IrInstruction wordGuard = unsignedWord.Program.Instructions.Single(i => i.Opcode == HybridCpuOpcode.ADDW && i.Annotation.Defs.Any(d => d.Name == wordRem.Operands[1].Name));
        IrInstruction wordProbeCopy = unsignedWord.Program.Instructions.Single(i => i.Annotation.Defs.Any(d => d.Name == wordGuard.Operands[1].Name));
        if (!unsignedWord.Program.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == HybridCpuManagedDivideUInt32EmitterV1.Symbol &&
                i.Annotation.Defs.Any(d => d.Name == wordProbeCopy.Operands[0].Name)) ||
            !unsignedWord.Program.Instructions.Any(i => i.StableIdentity.EndsWith(":u4-zero-probe:arg-copy:0", StringComparison.Ordinal) &&
                i.Operands[0].Kind == IrOperandKind.ArchitecturalRegister && i.Operands[0].Value == 0 && i.Immediate == 0) ||
            unsignedWord.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.REM or HybridCpuOpcode.REMU or HybridCpuOpcode.REMW))
            throw new Exception("unsigned I4 remainder requires exact zero probe and dependent word divisor");
        foreach (uint n in new[] { 0U, 1U, 17U, 0x80000000U, uint.MaxValue })
            foreach (uint d in new[] { 0U, 1U, 3U, 0x80000000U, uint.MaxValue }) {
                if (d == 0) {
                    try { ManagedCheckedArithmeticRuntimeV1.DivideUInt32(0, d); throw new Exception("missing U4 zero fault"); }
                    catch (DivideByZeroException) { }
                } else {
                    uint probe = ManagedCheckedArithmeticRuntimeV1.DivideUInt32(0, d);
                    if (probe != 0 || n % unchecked(d + probe) != UnsignedDivisionFixture.DynamicUnsignedRemainder(n, d))
                        throw new Exception("unsigned I4 remainder independent probe boundary oracle");
                }
            }
        RestrictedCilMethodSelectorV1 wideSelector = new RestrictedCilMethodSelectorV1(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.DynamicRemainderWide));
        RestrictedCilImportResultV1 wide = importer.ImportImage(pe, wideSelector);
        if (wide.Status != RestrictedCilImportStatusV1.Success || !wide.RequiresNativeExceptionTransfer ||
            !wide.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == HybridCpuManagedDivideInt64EmitterV1.Symbol) ||
            !wide.Program.Instructions.Any(i => i.Opcode == HybridCpuOpcode.MUL) ||
            !wide.Program.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SUB) ||
            wide.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.REM or HybridCpuOpcode.DIV))
            throw new Exception("dynamic I8 remainder needs checked quotient followed by MUL/SUB");
        MethodInfo compileMethod = typeof(ScalarControlFlowV2ObjectLinkerV1).GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!;
        ScalarControlFlowV2MethodObjectV1 WideObject(RestrictedCilMethodSelectorV1 selector) {
            ManagedCallGraphCompilationV1 body = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "wide-remainder", [selector], []));
            if (body.Status != RestrictedCilImportStatusV1.Success) throw new Exception("wide remainder body graph");
            return (ScalarControlFlowV2MethodObjectV1)compileMethod.Invoke(null, [body.Methods.Single(), 0])!;
        }
        ScalarControlFlowV2MethodObjectV1 wideObject = WideObject(wideSelector);
        ScalarControlFlowV2MethodObjectV1 quotientObject = WideObject(new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.DynamicRemainderWideViaQuotient)));
        foreach(string name in new[]{nameof(UnsignedDivisionFixture.DynamicRemainderWide),nameof(UnsignedDivisionFixture.DynamicRemainderWideViaQuotient)})
            Console.WriteLine("WIDE-REM-CIL "+name+" "+Convert.ToHexString(typeof(UnsignedDivisionFixture).GetMethod(name)!.GetMethodBody()!.GetILAsByteArray()!));
        // Debug C# retains the explicit quotient local and return-local/branch;
        // its physical code has an extra copy and epilogue edge. Release still
        // requires exact byte equality. Both modes execute the serialized ABI
        // operands below, with the checked division helper as the only call seam.
#if !DEBUG
        if (wideObject.CodeSha256 != quotientObject.CodeSha256)
            throw new Exception("Release wide remainder physical/encoded bytes differ from independent C# quotient/MUL/SUB oracle");
#endif
        if (!wideObject.ObjectArtifact.Relocations.Any(r =>
            r.TargetSymbol == HybridCpuManagedDivideInt64EmitterV1.Symbol))
            throw new Exception("wide remainder lost checked division relocation");
        long EvaluateWide(ScalarControlFlowV2MethodObjectV1 obj,long numerator,long divisor) {
            byte[] code=obj.ObjectArtifact.Sections.Single(s=>s.Name==".text").Data;
            HybridCpuObjectRelocationV1 low =obj.ObjectArtifact.Relocations.Single(r=>r.Kind==HybridCPU.Compiler.Core.Target.HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16 && r.TargetSymbol==HybridCpuManagedDivideInt64EmitterV1.Symbol);
            int offset=checked((int)low.Offset)/HybridCpuBundleSerializer.BundleSizeBytes*HybridCpuBundleSerializer.BundleSizeBytes;
            return unchecked((long)NativeConstantSmoke.Evaluate(code,unchecked((ulong)numerator),unchecked((ulong)divisor),
                externalCalls:new Dictionary<int,Func<ulong,ulong,ulong>>{[offset]=(n,d)=>unchecked((ulong)ManagedCheckedArithmeticRuntimeV1.DivideInt64(unchecked((long)n),unchecked((long)d)))}));
        }
        static object WideOutcome(Func<long> action) { try { return action(); } catch (Exception fault) { return fault.GetType(); } }
        foreach (long numerator in new[] { long.MinValue, (long)int.MinValue - 1, -17L, -1L, 0L, 1L, 17L, long.MaxValue })
            foreach (long divisor in new[] { long.MinValue, -3L, -1L, 0L, 1L, 3L, (long)uint.MaxValue + 1, long.MaxValue })
                if (!Equals(WideOutcome(() => UnsignedDivisionFixture.DynamicRemainderWide(numerator, divisor)),
                    WideOutcome(() => UnsignedDivisionFixture.DynamicRemainderWideViaQuotient(numerator, divisor))) ||
                    !Equals(WideOutcome(() => EvaluateWide(wideObject,numerator,divisor)),WideOutcome(() => UnsignedDivisionFixture.DynamicRemainderWide(numerator,divisor))) ||
                    !Equals(WideOutcome(() => EvaluateWide(quotientObject,numerator,divisor)),WideOutcome(() => UnsignedDivisionFixture.DynamicRemainderWide(numerator,divisor))))
                    throw new Exception("wide remainder independent quotient result/fault oracle");
        Console.WriteLine("PASS wide remainder serialized ABI operands/checked quotient/MUL/SUB, caller-clobber preservation and independent result/fault boundaries; bytes-equal="+(wideObject.CodeSha256==quotientObject.CodeSha256));
        HybridCpuObjectArtifactV1 remainderObject = HybridCpuManagedRemainderInt32EmitterV1.EmitObject();
        if (HybridCpuManagedAbiFamilyV1.SchemaMinor != 63 ||
            HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedRemainderInt32EmitterV1.Symbol) is not
                { Support: HybridCpuManagedAbiSupportV1.Supported, MayThrow: true,
                  GcTransition: HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint } ||
            remainderObject.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !remainderObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedRemainderInt32EmitterV1.Symbol && symbol.IsDefinition))
            throw new Exception("checked signed remainder helper ABI contract");
        ManagedCallGraphCompilationV1 dynamicRemainderGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "dynamic-remainder-smoke", [new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.DynamicRemainder))], []));
        if (dynamicRemainderGraph.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("checked signed remainder graph failed");
        ScalarControlFlowV2MethodObjectV1 dynamicRemainderObject = (ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
            .GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [dynamicRemainderGraph.Methods.Single(), 0])!;
        if (dynamicRemainderObject.ObjectArtifact.Status != HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success ||
            !dynamicRemainderObject.ObjectArtifact.Relocations.Any(r =>
                r.TargetSymbol == "__hybridcpu_managed_remainder_i4_checked"))
            throw new Exception("checked signed remainder HCO/relocation failed");
        if (UnsignedDivisionFixture.SignedRemainderWord(int.MinValue) != int.MinValue % 35 ||
            UnsignedDivisionFixture.SignedRemainderWide(long.MinValue) != long.MinValue % 0x100000001L)
            throw new Exception("signed remainder boundary parity");
        try { UnsignedDivisionFixture.DynamicRemainder(int.MinValue, -1); throw new Exception("CoreCLR remainder overflow must throw"); }
        catch (OverflowException) { }
        if (ManagedCheckedArithmeticRuntimeV1.RemainderInt32(-17, 5) != -17 % 5)
            throw new Exception("Signed checked remainder helper result mismatch");
        try { ManagedCheckedArithmeticRuntimeV1.RemainderInt32(1, 0); throw new Exception("remainder helper zero"); }
        catch (DivideByZeroException) { }
        try { ManagedCheckedArithmeticRuntimeV1.RemainderInt32(int.MinValue, -1); throw new Exception("remainder helper overflow"); }
        catch (OverflowException) { }
    }
}

public static class UnsignedDivisionFixture
{
    public static uint Word(uint numerator) => numerator / 18u;
    public static uint HighWord(uint numerator) => numerator / 0x80000001u;
    public static ulong Wide(ulong numerator) => numerator / 0x100000001UL;
    public static uint Dynamic(uint numerator, uint denominator) => numerator / denominator;
    public static int SignedWord(int numerator) => numerator / 16;
    public static int SignedNegativeDivisor(int numerator) => numerator / -2;
    public static long SignedWide(long numerator) => numerator / 0x100000001L;
    public static int DynamicSigned(int numerator, int denominator) => numerator / denominator;
    public static long DynamicSignedWide(long numerator, long denominator) => numerator / denominator;
    public static ulong DynamicWide(ulong numerator, ulong denominator) => numerator / denominator;
    public static ulong DynamicUnsignedRemainderWide(ulong numerator, ulong denominator) => numerator % denominator;
    public static long DynamicRemainderWideViaQuotient(long numerator, long denominator) {
        long quotient = numerator / denominator; return numerator - quotient * denominator;
    }
    public static int SignedRemainderWord(int numerator) => numerator % 35;
    public static uint UnsignedRemainderWord(uint numerator) => numerator % 35u;
    public static long SignedRemainderWide(long numerator) => numerator % 0x100000001L;
    public static long SignedRemainderWideTen(long numerator) => numerator % 10;
    public static ulong UnsignedRemainderWide(ulong numerator) => numerator % 0x100000001UL;
    public static int DynamicRemainder(int numerator, int denominator) => numerator % denominator;
    public static uint DynamicUnsignedRemainder(uint numerator, uint denominator) => numerator % denominator;
    public static long DynamicRemainderWide(long numerator, long denominator) => numerator % denominator;
}
