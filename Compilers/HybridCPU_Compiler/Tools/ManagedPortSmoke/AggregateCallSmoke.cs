using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;

internal static class AggregateCallSmoke
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        PersistedAssemblyBuilder builder = new PersistedAssemblyBuilder(new AssemblyName("AggregateCallLibrary"), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule("AggregateCallLibrary");
        Type Point(string parentName)
        {
            TypeBuilder parent = module.DefineType(parentName, TypeAttributes.Public);
            TypeBuilder point = parent.DefineNestedType("Point", TypeAttributes.NestedPublic | TypeAttributes.Sealed |
                TypeAttributes.SequentialLayout, typeof(ValueType));
            point.DefineField("X", typeof(int), FieldAttributes.Public);
            point.DefineField("Y", typeof(int), FieldAttributes.Public);
            Type result = point.CreateType()!; parent.CreateType(); return result;
        }
        Type a = Point("First"), b = Point("Second");
        EnumBuilder enumBuilder = module.DefineEnum("Mode", TypeAttributes.Public, typeof(int));
        enumBuilder.DefineLiteral("One", 1); Type mode = enumBuilder.CreateType()!;
        TypeBuilder api = module.DefineType("Api", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        void Probe(string name, Type argument, int value)
        {
            ILGenerator il = api.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(int), [argument]).GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, value); il.Emit(OpCodes.Ret);
        }
        Probe("Probe", a, 101); Probe("Probe", b, 202); Probe("Only", a, 303);
        Probe("EnumOrInt", mode, 404); Probe("EnumOrInt", typeof(int), 505);
        api.CreateType();
        TypeBuilder holder = module.DefineType("Holder", TypeAttributes.Public | TypeAttributes.Sealed);
        FieldBuilder payload = holder.DefineField("Payload", a, FieldAttributes.Public);
        ConstructorBuilder ctor = holder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [a]);
        ILGenerator code = ctor.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0); code.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        code.Emit(OpCodes.Ldarg_0); code.Emit(OpCodes.Ldarg_1); code.Emit(OpCodes.Stfld, payload); code.Emit(OpCodes.Ret);
        foreach (bool valid in new[] { true, false })
        {
            ILGenerator copy = holder.DefineMethod(valid ? "Store" : "BadStore", MethodAttributes.Public | MethodAttributes.Static,
                typeof(void), [holder, valid ? a : b]).GetILGenerator();
            copy.Emit(OpCodes.Ldarg_0); copy.Emit(OpCodes.Ldarg_1); copy.Emit(OpCodes.Stfld, payload); copy.Emit(OpCodes.Ret);
        }
        holder.CreateType();
        TypeBuilder scalar = module.DefineType("ScalarHolder", TypeAttributes.Public | TypeAttributes.Sealed);
        FieldBuilder scalarField = scalar.DefineField("Value", typeof(int), FieldAttributes.Public);
        ConstructorBuilder scalarCtor = scalar.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(int)]);
        ILGenerator scalarCode = scalarCtor.GetILGenerator();
        scalarCode.Emit(OpCodes.Ldarg_0); scalarCode.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        scalarCode.Emit(OpCodes.Ldarg_0); scalarCode.Emit(OpCodes.Ldarg_1); scalarCode.Emit(OpCodes.Stfld, scalarField);
        scalarCode.Emit(OpCodes.Ret); scalar.CreateType();
        using MemoryStream stream = new MemoryStream(); builder.Save(stream); byte[] dependency = stream.ToArray();
        Assembly loaded = Assembly.Load(dependency);
        Type first = loaded.GetType("First+Point")!, second = loaded.GetType("Second+Point")!;
        Type runtimeApi = loaded.GetType("Api")!;
        Type runtimeMode = loaded.GetType("Mode")!;
        object value = Activator.CreateInstance(first)!;
        first.GetField("X")!.SetValue(value, -123); first.GetField("Y")!.SetValue(value, int.MaxValue);
        object instance = Activator.CreateInstance(loaded.GetType("Holder")!, value)!;
        first.GetField("X")!.SetValue(value, 456);
        object saved = loaded.GetType("Holder")!.GetField("Payload")!.GetValue(instance)!;
        Check((int)first.GetField("X")!.GetValue(saved)! == -123 && (int)first.GetField("Y")!.GetValue(saved)! == int.MaxValue,
            "CoreCLR constructor copies the complete Point value, not an alias to the caller's value");
        Check((int)runtimeApi.GetMethod("Probe", [first])!.Invoke(null, [value])! == 101 &&
            (int)runtimeApi.GetMethod("Probe", [second])!.Invoke(null, [Activator.CreateInstance(second)])! == 202,
            "CoreCLR overload reference behavior");

        PersistedAssemblyBuilder consumer = new PersistedAssemblyBuilder(new AssemblyName("AggregateCallConsumer"), typeof(object).Assembly);
        ModuleBuilder cm = consumer.DefineDynamicModule("AggregateCallConsumer");
        TypeBuilder calls = cm.DefineType("Calls", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        ILGenerator make = calls.DefineMethod("MakeScalar", MethodAttributes.Public | MethodAttributes.Static,
            typeof(object), [typeof(int)]).GetILGenerator();
        make.Emit(OpCodes.Ldarg_0);
        make.Emit(OpCodes.Newobj, loaded.GetType("ScalarHolder")!.GetConstructor([typeof(int)])!);
        make.Emit(OpCodes.Ret);
        ILGenerator enumCall = calls.DefineMethod("Enum", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]).GetILGenerator();
        enumCall.Emit(OpCodes.Ldarg_0); enumCall.Emit(OpCodes.Call, runtimeApi.GetMethod("EnumOrInt", [runtimeMode])!); enumCall.Emit(OpCodes.Ret);
        foreach ((string? name, Type? point) in new[] { ("First", first), ("Second", second) })
        {
            ILGenerator il = calls.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(int), [point]).GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, runtimeApi.GetMethod("Probe", [point])!); il.Emit(OpCodes.Ret);
        }
        // Forge a MemberRef with the real assembly/type/name but a different struct
        // signature. A single method with that name is not sufficient binding evidence.
        PersistedAssemblyBuilder spoof = new PersistedAssemblyBuilder(new AssemblyName("AggregateCallLibrary"), typeof(object).Assembly);
        TypeBuilder spoofApi = spoof.DefineDynamicModule("spoof").DefineType("Api", TypeAttributes.Public);
        MethodBuilder wrong = spoofApi.DefineMethod("Only", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [second]);
        wrong.GetILGenerator().Emit(OpCodes.Ldc_I4_0); wrong.GetILGenerator().Emit(OpCodes.Ret);
        MethodInfo wrongMethod = spoofApi.CreateType()!.GetMethod("Only")!;
        ILGenerator wrongCall = calls.DefineMethod("WrongSingle", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [second]).GetILGenerator();
        wrongCall.Emit(OpCodes.Ldarg_0); wrongCall.Emit(OpCodes.Call, wrongMethod); wrongCall.Emit(OpCodes.Ret);
        calls.CreateType(); using MemoryStream consumerStream = new MemoryStream(); consumer.Save(consumerStream);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 constructorGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            consumerStream.ToArray(), "cross-assembly-constructor", [new("Calls", "MakeScalar")], [],
            DependencyModules: [new(dependency, "aggregate-library")]));
        Check(constructorGraph.Status == RestrictedCilImportStatusV1.Success &&
            constructorGraph.Methods.Any(m => m.Identity.DeclaringType == "ScalarHolder" && m.Identity.MethodName == ".ctor"),
            "Exact consumer MemberRef allocation must retain the owner constructor body: " + Describe(constructorGraph));
        ManagedCallGraphCompilationV1 enumGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, consumerStream.ToArray(), "enum-reference",
            [new("Calls", "Enum")], [], DependencyModules: [new(dependency, "aggregate-library")]));
        Check(enumGraph.Status == RestrictedCilImportStatusV1.Success && enumGraph.Methods.Single(m => m.Identity.DeclaringType == "Api").Identity.MetadataToken ==
            runtimeApi.GetMethod("EnumOrInt", [runtimeMode])!.MetadataToken, "Enum TypeRef must resolve to enum overload before scalar ABI selection: " + Describe(enumGraph));
        foreach (string name in new[] { "First", "Second" })
        {
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, consumerStream.ToArray(), "aggregate-consumer",
                [new("Calls", name)], [], DependencyModules: [new(dependency, "aggregate-library")]));
            Check(graph.Diagnostics.Any(d => d.Code == "HCCIL1810" && d.StableSourceIdentity!.Contains("Api.Probe") &&
                d.StableSourceIdentity.Contains("[AggregateCallLibrary]" + name + "+Point")),
                "Cross-assembly aggregate overload must select its exact body before ABI gate: " + Describe(graph));
            Check(graph.Graph is null && graph.Methods.Count == 0, "Aggregate analysis must not publish partial native methods");
        }
        foreach ((string? name, string? expected) in new[] { (".ctor", "HCCIL1810"), ("Store", "HCCIL1810"), ("BadStore", "HCCIL1204") })
        {
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, dependency, "aggregate-holder",
                [new("Holder", name)], []));
            Check(graph.Diagnostics.Any(d => d.Code == expected), name + ": " + Describe(graph));
        }
        ManagedCallGraphCompilationV1 mismatch = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, consumerStream.ToArray(), "spoofed-signature",
            [new("Calls", "WrongSingle")], [], DependencyModules: [new(dependency, "aggregate-library")]));
        Check(mismatch.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD1002"), "Single-name MemberRef signature mismatch must fail closed: " + Describe(mismatch));
        Console.WriteLine("PASS cross-assembly nested aggregate overload identity, constructor/field value flow, wrong-type store and pre-IR ABI gate vs CoreCLR");
    }
    private static string Describe(ManagedCallGraphCompilationV1 graph) =>
        string.Join(';', graph.Diagnostics.Select(d => d.Code + ":" + d.Message + ":" + d.StableSourceIdentity));
}
