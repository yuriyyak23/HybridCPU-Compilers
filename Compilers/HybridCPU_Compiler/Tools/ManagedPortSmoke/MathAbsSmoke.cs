using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;

internal static class MathAbsSmoke
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(MathAbsFixture).Assembly.Location);
        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(pe, new(typeof(MathAbsFixture).FullName!, nameof(MathAbsFixture.Abs)));
        Check(imported.Status == RestrictedCilImportStatusV1.Success && imported.Program!.Instructions.Any(i =>
                i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_math_abs_i8"),
            string.Join(';', imported.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        RestrictedCilImportResultV1 max = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(pe, new(typeof(MathAbsFixture).FullName!, nameof(MathAbsFixture.Max)));
        Check(max.Status == RestrictedCilImportStatusV1.Success && max.Program!.Instructions.Any(i =>
                i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_math_max_i4"),
            string.Join(';', max.Diagnostics.Select(d => d.Code + ":" + d.Message)));

        foreach (long value in new[] { long.MinValue + 1, -1L, 0L, 1L, long.MaxValue })
            Check(HybridCpuManagedMathRuntimeV1.AbsInt64(value) == Math.Abs(value), "CoreCLR Int64 Abs parity");
        try
        {
            HybridCpuManagedMathRuntimeV1.AbsInt64(long.MinValue);
            throw new Exception("Int64 minimum must overflow");
        }
        catch (OverflowException)
        {
        }

        foreach ((int, int) pair in new[] { (int.MinValue, int.MaxValue), (-1, -1), (0, -1), (1, 2), (int.MaxValue, int.MinValue) })
            Check(HybridCpuManagedMathRuntimeV1.MaxInt32(pair.Item1, pair.Item2) == Math.Max(pair.Item1, pair.Item2),
                "CoreCLR Int32 Max parity");

        HybridCpuObjectArtifactV1 absObject = HybridCpuManagedMathAbsInt64EmitterV1.EmitObject();
        HybridCpuObjectArtifactV1 maxObject = HybridCpuManagedMathMaxInt32EmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.SchemaMinor == 63 &&
              HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper("__hybridcpu_managed_math_abs_i8") is
                  { MayThrow: true, GcTransition: HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint } &&
              absObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              absObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedMathAbsInt64EmitterV1.Symbol && symbol.IsDefinition),
            "Math.Abs(Int64) ABI helper contract");
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper("__hybridcpu_managed_math_max_i4") is
                  { MayThrow: false, GcTransition: HybridCpuRuntimeHelperGcTransitionV1.None } &&
              maxObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              maxObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedMathMaxInt32EmitterV1.Symbol && symbol.IsDefinition),
            "Math.Max(Int32,Int32) ABI helper contract");
        RestrictedCilMethodSelectorV1 maxSelector = new RestrictedCilMethodSelectorV1(typeof(MathAbsFixture).FullName!, nameof(MathAbsFixture.Max));
        ManagedCallGraphCompilationV1 maxGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "math-max-smoke", [maxSelector], []));
        ScalarControlFlowV2LinkedProgramV1 maxLinked = new ScalarControlFlowV2ObjectLinkerV1().Link(maxGraph, maxGraph.Graph!.RootIdentities.Single());
        Check(maxGraph.Status == RestrictedCilImportStatusV1.Success &&
              maxLinked.Status == ScalarControlFlowV2LinkStatusV1.Success &&
              !maxLinked.Diagnostics.Any(diagnostic => diagnostic.Code == "HCLINK1003"),
            string.Join(';', maxLinked.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        RestrictedCilMethodSelectorV1 eightSelector = new RestrictedCilMethodSelectorV1(typeof(MathAbsFixture).FullName!, nameof(MathAbsFixture.CallEight));
        ManagedCallGraphCompilationV1 eightGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
                "eight-argument-long-call-smoke", [eightSelector], []));
        ScalarControlFlowV2LinkedProgramV1 eightLinked = new ScalarControlFlowV2ObjectLinkerV1().Link(
            eightGraph, eightGraph.Graph!.RootIdentities.Single());
        IrInstruction eightCall = eightGraph.Methods.Single(method => method.Identity.MethodName == nameof(MathAbsFixture.CallEight))
            .Import.Program!.Instructions.Single(instruction => instruction.Opcode == HybridCpuOpcode.JALR &&
                instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call));
        Check(eightGraph.Status == RestrictedCilImportStatusV1.Success &&
              eightCall.Annotation.Uses.Count(operand => operand.Kind == IrOperandKind.VirtualValue) == 9 &&
              eightLinked.Status == ScalarControlFlowV2LinkStatusV1.Success,
            "Eight ABI arguments plus one exact x5 long-call target must allocate and link: " +
            string.Join(';', eightLinked.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        CheckLongManagedCallRelaxation();
        Check(FakeMathAbs().Diagnostics.Any(d => d.Code == "HCCIL1474"),
            "A same-named non-CoreLib Math.Abs must fail closed");
        Check(FakeMathMax().Diagnostics.Any(d => d.Code == "HCCIL1475"),
            "A same-named non-CoreLib Math.Max must fail closed");
        Console.WriteLine("PASS exact Math.Abs(Int64)/Max(Int32) helpers, CoreCLR boundary parity and CoreLib identity gate");
    }

    private static void CheckLongManagedCallRelaxation()
    {
        static HybridCpuInstructionBundle Bundle(HybridCpuOpcode opcode, byte rd, byte rs1,
            short immediate = 0)
        {
            HybridCpuInstructionWord word = new HybridCpuInstructionWord
            {
                OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64,
                PredicateMask = byte.MaxValue,
                Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, HybridCpuInstructionWord.NoArchReg),
                Immediate = unchecked((ushort)immediate)
            };
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, word);
            return bundle;
        }

        const string target = "far-managed-target";
        byte[] callerCode = new HybridCpuBundleSerializer().SerializeProgram(
            [Bundle(HybridCpuOpcode.JAL, HybridCpuNativeAbiContractV2.ReturnAddressRegister,
                HybridCpuInstructionWord.NoArchReg)]);
        byte[] calleeCode = new HybridCpuBundleSerializer().SerializeProgram(
            [Bundle(HybridCpuOpcode.JALR, 0, HybridCpuNativeAbiContractV2.ReturnAddressRegister,
                HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes)]);
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        HybridCpuObjectArtifactV1 caller = writer.Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                callerCode, (ulong)callerCode.Length)],
            [new("caller", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                    ".text", 0, (ulong)callerCode.Length, true),
                new(target, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                    null, 0, 0, false)],
            [new(".text", HybridCpuManagedCallRelocationContractV1.ImmediateFieldOffsetBytes,
                HybridCpuRelocationKind.ManagedCallRelativeSigned16, target, 0)],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        byte[] fillerCode = new byte[64 * 1024];
        HybridCpuObjectArtifactV1 filler = writer.Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                fillerCode, (ulong)fillerCode.Length)], [], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        HybridCpuObjectArtifactV1 callee = writer.Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                calleeCode, (ulong)calleeCode.Length)],
            [new(target, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".text", 0, (ulong)calleeCode.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        Check(caller.Status == HybridCpuObjectStatusV1.Success &&
              filler.Status == HybridCpuObjectStatusV1.Success && callee.Status == HybridCpuObjectStatusV1.Success,
            "long-call fixture objects");
        HybridCpuStaticLinkArtifactV1 linked = new HybridCpuStaticLinkerV1().Link(
            [new("a-caller", caller.Bytes), new("m-filler", filler.Bytes), new("z-callee", callee.Bytes)]);
        Check(linked.Status == HybridCpuLinkStatusV1.Success,
            string.Join(';', linked.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        HybridCpuAppliedRelocationV1 relocation = linked.AppliedRelocations.Single(row => row.TargetSymbol == target);
        HybridCpuLinkedSymbolV1 thunk = linked.Symbols.Single(symbol =>
            symbol.Name == relocation.ResolvedViaThunkSymbol);
        Check(relocation.ResolvedViaThunkSymbol is not null &&
              unchecked((short)relocation.EncodedValue) == checked((short)(thunk.Address -
                  (relocation.PlaceAddress & ~((ulong)HybridCpuBundleSerializer.BundleSizeBytes - 1)))),
            "far call must resolve through an in-range deterministic thunk");
        int thunkOffset = checked((int)(thunk.Address - linked.ImageBase));
        ulong highWord = BitConverter.ToUInt64(linked.ImageBytes, thunkOffset);
        ulong lowWord = BitConverter.ToUInt64(linked.ImageBytes,
            thunkOffset + HybridCpuBundleSerializer.BundleSizeBytes);
        Check((HybridCpuOpcode)(highWord >> 48) == HybridCpuOpcode.AUIPC &&
              (HybridCpuOpcode)(lowWord >> 48) == HybridCpuOpcode.JALR,
            "long call thunk opcode pair");
        short high = unchecked((short)(highWord & ushort.MaxValue));
        short low = unchecked((short)(lowWord & ushort.MaxValue));
        Check(checked((long)thunk.Address + ((long)high << 12) + low) == (long)relocation.TargetAddress,
            "long call thunk target reconstruction");
    }

    private static RestrictedCilImportResultV1 FakeMathAbs()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("FakeMathAbs"), typeof(object).Assembly);
        ModuleBuilder module = assembly.DefineDynamicModule("FakeMathAbs");
        TypeBuilder type = module.DefineType("System.Math", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder abs = type.DefineMethod("Abs", MethodAttributes.Public | MethodAttributes.Static, typeof(long), [typeof(long)]);
        ILGenerator body = abs.GetILGenerator(); body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Ret); type.CreateType();
        TypeBuilder caller = module.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder call = caller.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(long), [typeof(long)]);
        body = call.GetILGenerator(); body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Call, abs); body.Emit(OpCodes.Ret);
        caller.CreateType();
        using MemoryStream stream = new MemoryStream(); assembly.Save(stream);
        return new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(stream.ToArray(), new("Caller", "Call"));
    }

    private static RestrictedCilImportResultV1 FakeMathMax()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("FakeMathMax"), typeof(object).Assembly);
        ModuleBuilder module = assembly.DefineDynamicModule("FakeMathMax");
        TypeBuilder type = module.DefineType("System.Math", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder max = type.DefineMethod("Max", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int), typeof(int)]);
        ILGenerator body = max.GetILGenerator(); body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Ret); type.CreateType();
        TypeBuilder caller = module.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder call = caller.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int), typeof(int)]);
        body = call.GetILGenerator(); body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Ldarg_1); body.Emit(OpCodes.Call, max); body.Emit(OpCodes.Ret);
        caller.CreateType();
        using MemoryStream stream = new MemoryStream(); assembly.Save(stream);
        return new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(stream.ToArray(), new("Caller", "Call"));
    }
}

public static class MathAbsFixture
{
    public static long Abs(long value) => Math.Abs(value);
    public static int Max(int left, int right) => Math.Max(left, right);
    public static long Eight(long a, long b, long c, long d, long e, long f, long g, long h) =>
        a + b + c + d + e + f + g + h;
    public static long CallEight() => Eight(1, 2, 3, 4, 5, 6, 7, 8);
}
