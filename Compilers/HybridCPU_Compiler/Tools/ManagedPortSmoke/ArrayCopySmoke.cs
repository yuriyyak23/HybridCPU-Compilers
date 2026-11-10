using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

internal static class ArrayCopySmoke
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(ArrayCopyFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilImportResultV1 imported = importer.ImportImage(pe, new(typeof(ArrayCopyFixture).FullName!, nameof(ArrayCopyFixture.Copy)));
        Check(imported.Status == RestrictedCilImportStatusV1.Success && imported.Program!.Instructions.Any(i =>
            i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_array_copy"),
            string.Join(";", imported.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        RestrictedCilImportResultV1 importedAll = importer.ImportImage(pe, new(typeof(ArrayCopyFixture).FullName!, nameof(ArrayCopyFixture.CopyAll)));
        Check(importedAll.Status == RestrictedCilImportStatusV1.Success && importedAll.Program!.Instructions.Any(i =>
            i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_array_copy_all"),
            string.Join(";", importedAll.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        RestrictedCilImportResultV1 importedClear = importer.ImportImage(pe, new(typeof(ArrayCopyFixture).FullName!, nameof(ArrayCopyFixture.Clear)));
        Check(importedClear.Status == RestrictedCilImportStatusV1.Success && importedClear.Program!.Instructions.Any(i =>
            i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_array_clear"),
            string.Join(";", importedClear.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        HybridCpuObjectArtifactV1 copyAllObject = HybridCpuManagedArrayCopyAllEmitterV1.EmitObject();
        HybridCpuObjectArtifactV1 copyObject = HybridCpuManagedArrayCopyEmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.SchemaMinor == 63 &&
            HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper("__hybridcpu_managed_array_copy") is
                { Support: HybridCpuManagedAbiSupportV1.Supported, MemoryEffects: var effects } &&
            effects == (HybridCPU.Compiler.Core.IR.IrMemoryEffectKind.Read | HybridCPU.Compiler.Core.IR.IrMemoryEffectKind.Write) &&
            HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedArrayCopyAllEmitterV1.Symbol) is
                { Support: HybridCpuManagedAbiSupportV1.Supported, MayThrow: true,
                  GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint } &&
            copyAllObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
            copyAllObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayCopyAllEmitterV1.Symbol && symbol.IsDefinition),
            "Array.Copy ABI helper contract");
        Check(HybridCpuManagedRuntimeEcallContractV1.ArrayCopyOperation == 34 &&
              HybridCpuManagedRuntimeEcallContractV1.ArrayCopyArgumentBlockBytes == 40 &&
              copyObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              copyObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayCopyEmitterV1.Symbol && symbol.IsDefinition),
            "five-argument Array.Copy bounded block HCO contract");
        HybridCpuObjectArtifactV1 clearObject = HybridCpuManagedArrayClearEmitterV1.EmitObject();
        Check(HybridCpuManagedRuntimeEcallContractV1.ArrayClearOperation == 38 &&
              HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedArrayClearEmitterV1.Symbol) is
                  { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, MayThrow: true } &&
              clearObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              clearObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayClearEmitterV1.Symbol && symbol.IsDefinition),
            "Array.Clear must bind an exact allocating/throwing ECALL HCO");
        try { Array.Clear(null!); Check(false, "CoreCLR Array.Clear null must throw"); }
        catch (ArgumentNullException exception) { Check(exception.ParamName == "array", "CoreCLR Array.Clear ParamName parity"); }
        HybridCpuRuntimeHelperV1? loadReference = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedArrayLoadReferenceEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 loadReferenceObject = HybridCpuManagedArrayLoadReferenceEmitterV1.EmitObject();
        Check(loadReference is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              loadReferenceObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              loadReferenceObject.Symbols.Any(symbol => symbol.Name ==
                  HybridCpuManagedArrayLoadReferenceEmitterV1.Symbol && symbol.IsDefinition),
            "ldelem.ref must bind an exact allocating/throwing CPU-callable runtime thunk");

        HybridCpuManagedTypeSystemBuilderV1 builder = new HybridCpuManagedTypeSystemBuilderV1();
        HybridCpuManagedTypeDeclarationV1[] classes = new[]
        {
            new HybridCpuManagedTypeDeclarationV1("Root", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new HybridCpuManagedTypeDeclarationV1("Base", HybridCpuManagedTypeKindV1.Class, "Root", [], []),
            new HybridCpuManagedTypeDeclarationV1("Derived", HybridCpuManagedTypeKindV1.Class, "Base", [], []),
            new HybridCpuManagedTypeDeclarationV1("Other", HybridCpuManagedTypeKindV1.Class, "Root", [], [])
        };
        HybridCpuManagedTypeSystemBuildV1 classBuild = builder.Build(classes);
        Check(classBuild.IsSuccess, classBuild.Reason);
        ulong Id(string name) => classBuild.TypeSystem!.Descriptors.Single(t => t.StableIdentity == name).TypeId;
        HybridCpuManagedTypeDeclarationV1 RefArray(string name, string element) =>
            new(name, HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.ObjectReference, Id(element), 8, 8, 16, 24, true, true));
        HybridCpuManagedTypeDeclarationV1 PrimitiveArray(string name, int size) =>
            new(name, HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.Primitive, null, size, size, 16, 24, true, false));
        HybridCpuManagedTypeSystemBuildV1 built = builder.Build([.. classes, RefArray("Root[]", "Root"), RefArray("Derived[]", "Derived"),
            PrimitiveArray("System.Byte[]", 1), PrimitiveArray("System.Boolean[]", 1)]);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        ulong Handle(string name) => types.TypeHandle(types.Descriptors.Single(t => t.StableIdentity == name).TypeId)!.Value;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "boot");
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x40000000, 16384, 4096, -3));
        Check(heap.Initialize().IsSuccess, "heap");
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);

        ulong bytes = arrays.NewArray(Handle("System.Byte[]"), 6).ObjectReference;
        byte[] clr = [1, 2, 3, 4, 5, 6];
        for (int i = 0; i < clr.Length; i++) Check(arrays.StoreInt8(bytes, i, clr[i]).IsSuccess, "byte setup");
        Array.Copy(clr, 0, clr, 2, 4);
        Check(arrays.Copy(bytes, 0, bytes, 2, 4).IsSuccess, "overlap copy");
        Check(Enumerable.Range(0, clr.Length).All(i => arrays.LoadUInt8(bytes, i).ScalarValue == clr[i]),
            "CoreCLR overlap/memmove parity");
        ulong copyAll = arrays.NewArray(Handle("System.Byte[]"), 6).ObjectReference;
        Check(arrays.Copy(bytes, copyAll, 6).IsSuccess && Enumerable.Range(0, 6).All(i =>
            arrays.LoadUInt8(copyAll, i).ScalarValue == arrays.LoadUInt8(bytes, i).ScalarValue), "three-argument overload parity");
        byte[] before = heap.ReadObjectBytes(bytes)!;
        foreach ((int, int, int) range in new[] { (-1, 0, 1), (0, -1, 1), (0, 0, -1), (4, 0, 3), (0, 5, 2) })
            Check(!arrays.Copy(bytes, range.Item1, bytes, range.Item2, range.Item3).IsSuccess, "invalid range");
        Check(before.SequenceEqual(heap.ReadObjectBytes(bytes)!), "range failures are atomic");
        ulong booleans = arrays.NewArray(Handle("System.Boolean[]"), 6).ObjectReference;
        Check(arrays.Copy(bytes, 0, booleans, 0, 1).Status == HybridCpuManagedShapeStatusV1.ArrayTypeMismatch,
            "same-width primitive identities must not alias");

        ulong baseArray = arrays.NewArray(Handle("Root[]"), 2).ObjectReference;
        ulong derivedArray = arrays.NewArray(Handle("Derived[]"), 2).ObjectReference;
        ulong derived = heap.Allocate(Handle("Derived")).ObjectReference;
        ulong other = heap.Allocate(Handle("Other")).ObjectReference;
        HybridCpuManagedShapeResultV1 storeDerived = arrays.StoreReference(baseArray, 0, derived);
        HybridCpuManagedShapeResultV1 storeOther = arrays.StoreReference(baseArray, 1, other);
        Check(storeDerived.IsSuccess && storeOther.IsSuccess,
            $"reference setup: derived={derived:x}:{storeDerived.Status}:{storeDerived.Reason}; other={other:x}:{storeOther.Status}:{storeOther.Reason}");
        byte[] derivedBefore = heap.ReadObjectBytes(derivedArray)!;
        Check(arrays.Copy(baseArray, 0, derivedArray, 0, 2).Status == HybridCpuManagedShapeStatusV1.ArrayTypeMismatch &&
            derivedBefore.SequenceEqual(heap.ReadObjectBytes(derivedArray)!), "covariant store scan is atomic");
        Check(arrays.Copy(baseArray, 0, derivedArray, 0, 1).IsSuccess &&
            unchecked((ulong)arrays.LoadReference(derivedArray, 0).ScalarValue) == derived, "qualified covariant reference copy");
        Check(arrays.LoadReference(0, 0).Status == HybridCpuManagedShapeStatusV1.NullReference &&
              arrays.LoadReference(baseArray, -1).Status == HybridCpuManagedShapeStatusV1.BoundsViolation &&
              arrays.LoadReference(baseArray, 2).Status == HybridCpuManagedShapeStatusV1.BoundsViolation,
            "ldelem.ref exact null and signed bounds outcomes");
        Check(arrays.Copy(0, 0, bytes, 0, 0).Status == HybridCpuManagedShapeStatusV1.NullReference, "null precedence");
        Check(arrays.Clear(bytes).IsSuccess && Enumerable.Range(0, 6).All(i => arrays.LoadUInt8(bytes, i).ScalarValue == 0),
            "primitive full-payload clear");
        Check(arrays.Clear(derivedArray).IsSuccess && arrays.LoadReference(derivedArray, 0).ScalarValue == 0,
            "reference full-payload clear");
        Check(arrays.Clear(0).Status == HybridCpuManagedShapeStatusV1.NullReference,
            "Array.Clear null precedence");
        Console.WriteLine("PASS Array.Copy/Clear exact helpers, overlap parity, primitive identity and atomic reference validation");
    }
}

public static class ArrayCopyFixture
{
    public static void Copy(byte[] source, int sourceIndex, byte[] destination, int destinationIndex, int length) =>
        Array.Copy(source, sourceIndex, destination, destinationIndex, length);
    public static void CopyAll(byte[] source, byte[] destination, int length) => Array.Copy(source, destination, length);
    public static void Clear(byte[] values) => Array.Clear(values);
}
