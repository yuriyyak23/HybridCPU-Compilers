using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;

internal static class AggregateFlowSmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("AggregateFlowFixture"), typeof(object).Assembly);
        ModuleBuilder module = assembly.DefineDynamicModule("AggregateFlowFixture");
        Type Value(string name)
        {
            TypeBuilder type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
            type.DefineField("Bits", typeof(int), FieldAttributes.Public);
            return type.CreateType()!;
        }
        Type a = Value("A"), b = Value("B");
        TypeBuilder fixture = module.DefineType("Fixture", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        ILGenerator Method(string name, Type result, params Type[] parameters) =>
            fixture.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, result, parameters).GetILGenerator();
        foreach (bool valid in new[] { true, false })
        {
            ILGenerator il = Method(valid ? "Store" : "BadStore", typeof(void), a.MakeArrayType(), typeof(int), valid ? a : b);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stelem, a); il.Emit(OpCodes.Ret);
            il = Method(valid ? "Join" : "BadJoin", typeof(void), typeof(bool), a, valid ? a : b);
            Label other = il.DefineLabel(); Label join = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brtrue_S, other); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Br_S, join);
            il.MarkLabel(other); il.Emit(OpCodes.Ldarg_2); il.MarkLabel(join); il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ret);
            il = Method(valid ? "Local" : "BadLocal", typeof(void), a, b);
            il.DeclareLocal(a);
            il.Emit(valid ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1); il.Emit(OpCodes.Stloc_0); il.Emit(OpCodes.Ldloc_0); il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ret);
            il = Method(valid ? "Return" : "BadReturn", a, valid ? a : b);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ret);
        }
        ILGenerator arithmetic = Method("BadAdd", typeof(void), a, a);
        arithmetic.Emit(OpCodes.Ldarg_0); arithmetic.Emit(OpCodes.Ldarg_1); arithmetic.Emit(OpCodes.Add); arithmetic.Emit(OpCodes.Pop); arithmetic.Emit(OpCodes.Ret);
        Method("Unused", typeof(void), a).Emit(OpCodes.Ret);
        ILGenerator dup = Method("Dup", a, a);
        dup.Emit(OpCodes.Ldarg_0); dup.Emit(OpCodes.Dup); dup.Emit(OpCodes.Pop); dup.Emit(OpCodes.Ret);
        ILGenerator scalar = Method("ScalarPop", typeof(int), typeof(int));
        scalar.Emit(OpCodes.Ldarg_0); scalar.Emit(OpCodes.Pop); scalar.Emit(OpCodes.Ldc_I4_1); scalar.Emit(OpCodes.Ret);
        ILGenerator badPop = Method("BadPop", typeof(void));
        badPop.Emit(OpCodes.Pop); badPop.Emit(OpCodes.Ret);
        fixture.CreateType();
        using MemoryStream output = new MemoryStream();
        assembly.Save(output);
        byte[] pe = output.ToArray();
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string? name, string? expected) in new[]
        {
            ("Store", "HCCIL1810"), ("Join", "HCCIL1810"), ("Local", "HCCIL1810"), ("Return", "HCCIL1810"),
            ("Unused", "HCCIL1810"), ("Dup", "HCCIL1810"), ("BadPop", "HCCIL1602"), ("BadStore", "HCCIL1813"), ("BadJoin", "HCCIL1104"),
            ("BadLocal", "HCCIL0034"), ("BadReturn", "HCCIL0019"), ("BadAdd", "HCCIL0013")
        })
        {
            RestrictedCilImportResultV1 result = importer.ImportImage(pe, new("Fixture", name), "aggregate-flow-smoke");
            if (!result.Diagnostics.Any(d => d.Code == expected) || result.Status == RestrictedCilImportStatusV1.Success)
                throw new Exception(name + ": expected " + expected + ", got " + string.Join("; ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            if (name == "Return" && !result.Provenance!.MethodIdentity.Contains("[AggregateFlowFixture]A", StringComparison.Ordinal))
                throw new Exception("Method provenance must preserve the scoped aggregate identity");
        }
        RestrictedCilImportResultV1 legacy = new RestrictedCilImporterV1().ImportImage(pe, new("Fixture", "Store"));
        if (importer.ImportImage(pe, new("Fixture", "ScalarPop")).Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Scalar pop must lower without the aggregate gate");
        if (legacy.Status == RestrictedCilImportStatusV1.Success) throw new Exception("Legacy scalar profile must remain closed");
        Console.WriteLine("PASS exact aggregate arguments/locals/phi/ret/stelem, wrong-type CIL rejection and pre-IR ABI gate");
    }
}
