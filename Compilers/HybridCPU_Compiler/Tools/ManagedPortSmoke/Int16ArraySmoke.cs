using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

internal static class Int16ArraySmoke
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }

        byte[] pe = File.ReadAllBytes(typeof(Int16ArrayFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string? method, string? helper) in new[]
        {
            (nameof(Int16ArrayFixture.StoreChar), "store_i2"),
            (nameof(Int16ArrayFixture.StoreShort), "store_i2"),
            (nameof(Int16ArrayFixture.LoadChar), "load_u2"),
            (nameof(Int16ArrayFixture.LoadShort), "load_i2")
        })
        {
            RestrictedCilImportResultV1 imported = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, method));
            Check(imported.Status == RestrictedCilImportStatusV1.Success && imported.Program!.Instructions.Any(i =>
                    i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_array_" + helper),
                method + ": " + string.Join(";", imported.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }
        foreach (string method in new[] { nameof(Int16ArrayFixture.LoadInt32Unsigned), nameof(Int16ArrayFixture.StoreInt32Unsigned) })
        {
            RestrictedCilImportResultV1 imported = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, method));
            Check(imported.Status == RestrictedCilImportStatusV1.Success && imported.Program!.Instructions.Any(i =>
                    i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ADDIW),
                method + " must canonicalize the exact UInt32 I4 index carrier before the checked array helper: " +
                string.Join(";", imported.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }
        RestrictedCilImportResultV1 unsignedValueStore = importer.ImportImage(pe,
            new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.StoreUInt32Value)));
        Check(unsignedValueStore.Status == RestrictedCilImportStatusV1.Success &&
              unsignedValueStore.Program!.Instructions.Any(i =>
                  i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_array_store_i4"),
            "stelem.i4 must preserve an exact UInt32 low-bit carrier");
        ManagedCallGraphCompilationV1 callProjection = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe,
            "uint32-int32-call-projection",
            [new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.CallInt))], []));
        Check(callProjection.Status == RestrictedCilImportStatusV1.Success &&
              callProjection.Methods.Single(m => m.Identity.MethodName == nameof(Int16ArrayFixture.CallInt))
                  .Import.Program!.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ADDIW) &&
              new ScalarControlFlowV2ObjectLinkerV1().Link(callProjection,
                  callProjection.Graph!.RootIdentities.Single()).Status == ScalarControlFlowV2LinkStatusV1.Success,
            "UInt32-to-Int32 managed call argument must normalize the exact low-32-bit carrier");
        foreach (string method in new[] { nameof(Int16ArrayFixture.ConvertUInt16), nameof(Int16ArrayFixture.ConvertUInt16Long) })
        {
            RestrictedCilImportResultV1 converted = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, method));
            Check(converted.Status == RestrictedCilImportStatusV1.Success && converted.Program!.Instructions.Any(i =>
                    i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.AND),
                method + " must materialize the full 0xffff mask and lower to AND");
        }
        foreach (string method in new[] { nameof(Int16ArrayFixture.ConvertInt16), nameof(Int16ArrayFixture.ConvertInt16Long) })
        {
            RestrictedCilImportResultV1 converted = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, method));
            Check(converted.Status == RestrictedCilImportStatusV1.Success &&
                  converted.Program!.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SLLI) &&
                  converted.Program.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SRAI),
                method + " must truncate low 16 bits and sign-extend with paired shifts");
        }
        RestrictedCilImportResultV1 convI8 = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.ConvertInt64)));
        Check(convI8.Status == RestrictedCilImportStatusV1.Success && convI8.Program!.Instructions.Any(i =>
            i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ADDIW), "conv.i8 signed I4 extension");
        RestrictedCilImportResultV1 convI4 = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.ConvertInt32Long)));
        Check(convI4.Status == RestrictedCilImportStatusV1.Success && convI4.Program!.Instructions.Any(i =>
            i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ADDIW), "conv.i4 must truncate I8 to low 32 bits and sign-extend its I4 carrier");
        foreach (long value in new[] { long.MinValue, -4294967297L, -1L, 0L, 0x12345678abcdef01L, long.MaxValue })
            Check(Int16ArrayFixture.ConvertInt32Long(value) == unchecked((int)value), "CoreCLR conv.i4 truncation parity");

        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuObjectArtifactV1 loadInt16Object = HybridCpuManagedArrayLoadInt16EmitterV1.EmitObject();
        HybridCpuObjectArtifactV1 loadUInt16Object = HybridCpuManagedArrayLoadUInt16EmitterV1.EmitObject();
        HybridCpuObjectArtifactV1 storeInt16Object = HybridCpuManagedArrayStoreInt16EmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.SchemaMinor == 63 &&
              abi.ResolveRuntimeHelper("__hybridcpu_managed_array_load_i2") is
                  { Support: HybridCpuManagedAbiSupportV1.Supported, MayThrow: true,
                    GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint } &&
              loadInt16Object.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              loadInt16Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayLoadInt16EmitterV1.Symbol && symbol.IsDefinition) &&
              abi.ResolveRuntimeHelper("__hybridcpu_managed_array_load_u2") is
                  { Support: HybridCpuManagedAbiSupportV1.Supported, MayThrow: true,
                    GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint } &&
              loadUInt16Object.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              loadUInt16Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayLoadUInt16EmitterV1.Symbol && symbol.IsDefinition) &&
              abi.ResolveRuntimeHelper(HybridCpuManagedArrayStoreInt16EmitterV1.Symbol) is
                  { Support: HybridCpuManagedAbiSupportV1.Supported, MayThrow: true,
                    GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint } &&
              storeInt16Object.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              storeInt16Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayStoreInt16EmitterV1.Symbol && symbol.IsDefinition),
            "managed ABI v1.21 must bind every checked 16-bit SZARRAY helper");

        List<HybridCpuManagedTypeDeclarationV1> declarations = new[] { "System.Char[]", "System.Int16[]", "System.Int32[]" }.Select(name =>
            new HybridCpuManagedTypeDeclarationV1(name, HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.Primitive, null, name == "System.Int32[]" ? 4 : 2,
                    name == "System.Int32[]" ? 4 : 2, 16, 24, true, false))).ToList();
        declarations.Add(new("System.String", HybridCpuManagedTypeKindV1.String, null, [], [], null,
            new(16, 20, 2, true)));
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build(declarations);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        ulong Handle(string name) => types.TypeHandle(types.Descriptors.Single(t => t.StableIdentity == name).TypeId)!.Value;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "boot");
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
        Check(heap.Initialize().IsSuccess, "heap");
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
        HybridCpuManagedTypeSystemBuildV1 wideTypes = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new("System.Int64[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.Primitive, null, 8, 8, 16, 24, true, false))]);
        Check(wideTypes.IsSuccess, wideTypes.Reason);
        HybridCpuManagedHeapAllocatorV1 wideHeap = new HybridCpuManagedHeapAllocatorV1(kernel, wideTypes.TypeSystem!,
            HybridCpuManagedHeapOptionsV1.Create(0x50000000, 4096, 4096, -3));
        Check(wideHeap.Initialize().IsSuccess, "wide heap");
        HybridCpuManagedArrayRuntimeV1 wideArrays = new HybridCpuManagedArrayRuntimeV1(wideTypes.TypeSystem!, wideHeap);
        ulong wideHandle = wideTypes.TypeSystem!.TypeHandle(wideTypes.TypeSystem.Descriptors.Single().TypeId)!.Value;
        ulong wide = wideArrays.NewArray(wideHandle, 3).ObjectReference;
        foreach (long value in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)uint.MaxValue + 1, long.MaxValue }) {
            byte[] payload = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload, value);
            Check(wideHeap.WriteObjectBytes(wide, 32, payload).IsSuccess, "wide payload");
            Check(wideArrays.LoadInt64(wide, 1).ScalarValue == value && wideArrays.LoadInt64(wide, 0).ScalarValue == 0 &&
                wideArrays.LoadInt64(wide, 2).ScalarValue == 0, "I8 runtime exact bytes/adjacent elements");
        }
        foreach (int index in new[] { int.MinValue, -1, 3, int.MaxValue })
            Check(wideArrays.LoadInt64(wide, index).Status == HybridCpuManagedShapeStatusV1.BoundsViolation, "I8 bounds");
        Check(wideArrays.LoadInt64(0, 0).Status == HybridCpuManagedShapeStatusV1.NullReference &&
            wideArrays.LoadInt32(wide, 0).Status == HybridCpuManagedShapeStatusV1.InvalidType,
            "I8 null/exact width");
        HybridCpuObjectArtifactV1 wideObject = HybridCpuManagedArrayLoadInt64EmitterV1.EmitObject();
        Check(HybridCpuManagedRuntimeEcallContractV1.ArrayLoadInt64Operation == 39 &&
            wideObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
            wideObject.Symbols.Any(s => s.Name == HybridCpuManagedArrayLoadInt64EmitterV1.Symbol && s.IsDefinition), "I8 ECALL HCO");
        byte[] wideCode = wideObject.Sections.Single(s => s.Name == ".text").Data;
        bool operation39 = false; int ecalls = 0; int saved = 0; int restored = 0;
        for (int offset = 0; offset < wideCode.Length; offset += HybridCPU.Compiler.Core.IR.HybridCpuBundleSerializer.BundleSizeBytes) {
            HybridCpuInstructionBundle bundle = new HybridCPU.Compiler.Core.IR.HybridCpuInstructionBundle();
            Check(bundle.TryReadBytes(wideCode, offset), "I8 helper bundle");
            HybridCpuInstructionWord word = bundle.GetInstruction(0);
            Check(HybridCPU.Compiler.Core.IR.HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out byte rs2), "I8 helper physical operands");
            HybridCpuOpcode opcode = (HybridCPU.Compiler.Core.IR.HybridCpuOpcode)word.OpCode;
            if (opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ADDI && rd == 11 && rs1 == 0 && word.Immediate == 39) operation39 = true;
            if (opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ECALL) ecalls++;
            if (opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SD && rs1 == 2 && rs2 is 18 or 19) saved++;
            if (opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.LD && rs1 == 2 && rd is 18 or 19) restored++;
        }
        Check(operation39 && ecalls == 1 && saved == 2 && restored == 2 && wideCode.SequenceEqual(
            HybridCpuManagedArrayLoadInt64EmitterV1.EmitObject().Sections.Single(s => s.Name == ".text").Data), "I8 serialized service/ABI/repeat");
        Console.WriteLine("PASS Int64 checked array runtime full-width bytes/null/bounds/width and ECALL helper");
        HybridCpuManagedShapeResultV1 emptyFirst = arrays.Empty(Handle("System.Int32[]"));
        HybridCpuManagedShapeResultV1 emptySecond = arrays.Empty(Handle("System.Int32[]"));
        Check(emptyFirst.IsSuccess && emptyFirst.ObjectReference != 0 &&
              emptySecond.ObjectReference == emptyFirst.ObjectReference &&
              arrays.Length(emptyFirst.ObjectReference).ScalarValue == 0,
            "Array.Empty must cache one rooted zero-length array per exact SZARRAY type");
        Check(arrays.Empty(Handle("System.String")).Status == HybridCpuManagedShapeStatusV1.InvalidType,
            "Array.Empty must reject a non-SZARRAY type handle");
        Check(arrays.Length(0).Status == HybridCpuManagedShapeStatusV1.NullReference,
            "array length must preserve the exact null-reference status");
        ulong chars = arrays.NewArray(Handle("System.Char[]"), 3).ObjectReference;
        ulong shorts = arrays.NewArray(Handle("System.Int16[]"), 3).ObjectReference;

        DynamicMethod rawCharStore = new DynamicMethod("RawCharStore", typeof(void), [typeof(char[]), typeof(int), typeof(int)]);
        ILGenerator il = rawCharStore.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stelem_I2); il.Emit(OpCodes.Ret);
        Action<char[], int, int> clrStore = rawCharStore.CreateDelegate<Action<char[], int, int>>();
        DynamicMethod rawCharLoad = new DynamicMethod("RawCharLoad", typeof(int), [typeof(char[]), typeof(int)]);
        il = rawCharLoad.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldelem_U2); il.Emit(OpCodes.Ret);
        Func<char[], int, int> clrLoad = rawCharLoad.CreateDelegate<Func<char[], int, int>>();
        char[] clr = new char[3];

        foreach (int value in new[] { int.MinValue, -65537, -32769, -32768, -1, 0, 1, 0x7fff, 0x8000, 0xffff, 0x10000, int.MaxValue })
        {
            Check(Int16ArrayFixture.ConvertUInt16(value) == unchecked((ushort)value), "CoreCLR conv.u2 I4 parity");
            Check(Int16ArrayFixture.ConvertInt16(value) == unchecked((short)value), "CoreCLR conv.i2 I4 parity");
            clrStore(clr, 1, value);
            Check(arrays.StoreInt16(chars, 1, value).IsSuccess && arrays.StoreInt16(shorts, 1, value).IsSuccess,
                "checked 16-bit stores");
            Check(arrays.LoadUInt16(chars, 1).ScalarValue == clrLoad(clr, 1), "CoreCLR char truncation/zero-extension parity");
            Check(arrays.LoadInt16(shorts, 1).ScalarValue == unchecked((short)value), "signed Int16 extension");
            Check(arrays.LoadUInt16(chars, 0).ScalarValue == 0 && arrays.LoadUInt16(chars, 2).ScalarValue == 0,
                "adjacent UTF-16 elements unchanged");
        }
        foreach (long value in new[] { long.MinValue, -65537L, -1L, 0L, 65535L, 65536L, long.MaxValue })
        {
            Check(Int16ArrayFixture.ConvertUInt16Long(value) == unchecked((ushort)value), "CoreCLR conv.u2 I8 parity");
            Check(Int16ArrayFixture.ConvertInt16Long(value) == unchecked((short)value), "CoreCLR conv.i2 I8 parity");
        }

        PersistedAssemblyBuilder invalidAssembly = new PersistedAssemblyBuilder(new System.Reflection.AssemblyName("ConvU2InvalidFixture"), typeof(object).Assembly);
        TypeBuilder invalidType = invalidAssembly.DefineDynamicModule("Invalid").DefineType("Invalid", System.Reflection.TypeAttributes.Public);
        ILGenerator invalidIl = invalidType.DefineMethod("WrongType", System.Reflection.MethodAttributes.Public |
            System.Reflection.MethodAttributes.Static, typeof(int), [typeof(object)]).GetILGenerator();
        invalidIl.Emit(OpCodes.Ldarg_0); invalidIl.Emit(OpCodes.Conv_U2); invalidIl.Emit(OpCodes.Ret);
        invalidIl = invalidType.DefineMethod("Underflow", System.Reflection.MethodAttributes.Public |
            System.Reflection.MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
        invalidIl.Emit(OpCodes.Conv_U2); invalidIl.Emit(OpCodes.Ret);
        invalidIl = invalidType.DefineMethod("WrongTypeI2", System.Reflection.MethodAttributes.Public |
            System.Reflection.MethodAttributes.Static, typeof(int), [typeof(object)]).GetILGenerator();
        invalidIl.Emit(OpCodes.Ldarg_0); invalidIl.Emit(OpCodes.Conv_I2); invalidIl.Emit(OpCodes.Ret);
        invalidIl = invalidType.DefineMethod("UnderflowI2", System.Reflection.MethodAttributes.Public |
            System.Reflection.MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
        invalidIl.Emit(OpCodes.Conv_I2); invalidIl.Emit(OpCodes.Ret);
        invalidType.CreateType();
        using MemoryStream invalidPe = new MemoryStream();
        invalidAssembly.Save(invalidPe);
        Check(importer.ImportImage(invalidPe.ToArray(), new("Invalid", "WrongType")).Diagnostics.Any(d => d.Code == "HCCIL1874"),
            "conv.u2 must reject non-integer operands");
        Check(importer.ImportImage(invalidPe.ToArray(), new("Invalid", "Underflow")).Diagnostics.Any(d => d.Code == "HCCIL1875"),
            "conv.u2 must reject stack underflow");
        Check(importer.ImportImage(invalidPe.ToArray(), new("Invalid", "WrongTypeI2")).Diagnostics.Any(d => d.Code == "HCCIL1876"),
            "conv.i2 must reject non-integer operands");
        Check(importer.ImportImage(invalidPe.ToArray(), new("Invalid", "UnderflowI2")).Diagnostics.Any(d => d.Code == "HCCIL1877"),
            "conv.i2 must reject stack underflow");

        byte[] before = heap.ReadObjectBytes(chars)!;
        foreach (int index in new[] { -1, 3, int.MinValue, int.MaxValue })
            Check(arrays.StoreInt16(chars, index, 1).Status == HybridCpuManagedShapeStatusV1.BoundsViolation, "bounds");
        Check(before.SequenceEqual(heap.ReadObjectBytes(chars)!), "failed writes are atomic");
        Check(arrays.StoreInt16(0, 0, 1).Status == HybridCpuManagedShapeStatusV1.NullReference, "null");
        ulong ints = arrays.NewArray(Handle("System.Int32[]"), 1).ObjectReference;
        Check(arrays.StoreInt16(ints, 0, 1).Status == HybridCpuManagedShapeStatusV1.InvalidType, "width mismatch");

        ulong stringHandle = Handle("System.String");
        HybridCpuManagedStringRuntimeV1 strings = new HybridCpuManagedStringRuntimeV1(types, heap, stringHandle);
        int[] utf16 = [0, 'A', 0xd800, 0x0436, 0xdfff, 0xffff];
        ulong source = arrays.NewArray(Handle("System.Char[]"), utf16.Length).ObjectReference;
        for (int index = 0; index < utf16.Length; index++)
            Check(arrays.StoreInt16(source, index, utf16[index]).IsSuccess, "UTF-16 source fill");
        HybridCpuManagedShapeResultV1 materialized = strings.FromCharArray(stringHandle, source);
        Check(materialized.IsSuccess && strings.Length(materialized.ObjectReference).ScalarValue == utf16.Length,
            "String(char[]) materialization");
        for (int index = 0; index < utf16.Length; index++)
            Check(strings.Character(materialized.ObjectReference, index).ScalarValue == utf16[index],
                "immutable raw UTF-16 copy");
        Check(strings.Character(0, 0).Status == HybridCpuManagedShapeStatusV1.NullReference &&
              strings.Character(materialized.ObjectReference, -1).Status == HybridCpuManagedShapeStatusV1.BoundsViolation &&
              strings.Character(materialized.ObjectReference, utf16.Length).Status == HybridCpuManagedShapeStatusV1.BoundsViolation,
            "string character null and exact bounds status");
        Check(strings.Length(materialized.ObjectReference).IsSuccess &&
              strings.Length(materialized.ObjectReference).ScalarValue == utf16.Length &&
              strings.Length(0).Status == HybridCpuManagedShapeStatusV1.NullReference,
            "string length success and exact null status");
        Check(!strings.FromCharArray(stringHandle, ints).IsSuccess &&
              strings.FromCharArray(stringHandle, 0).Status == HybridCpuManagedShapeStatusV1.NullReference,
            "String(char[]) exact source contract");
        HybridCpuManagedShapeResultV1 same = strings.MaterializeLiteral(stringHandle, new string(utf16.Select(value => (char)value).ToArray()));
        HybridCpuManagedShapeResultV1 different = strings.MaterializeLiteral(stringHandle, "different");
        HybridCpuManagedShapeResultV1 equalResult = strings.AreEqual(materialized.ObjectReference, same.ObjectReference);
        HybridCpuManagedShapeResultV1 unequalResult = strings.AreNotEqual(materialized.ObjectReference, different.ObjectReference);
        HybridCpuManagedShapeResultV1 nullEqualResult = strings.AreEqual(0, 0);
        HybridCpuManagedShapeResultV1 nullUnequalResult = strings.AreNotEqual(0, same.ObjectReference);
        Check(same.IsSuccess && different.IsSuccess && equalResult.IsSuccess && equalResult.ScalarValue == 1 &&
              unequalResult.IsSuccess && unequalResult.ScalarValue == 1 && nullEqualResult.ScalarValue == 1 &&
              nullUnequalResult.ScalarValue == 1,
            $"String equality/null/CoreCLR content semantics: equal={equalResult.Status}/{equalResult.ScalarValue}/{equalResult.Reason}; " +
            $"unequal={unequalResult.Status}/{unequalResult.ScalarValue}/{unequalResult.Reason}");

        byte[] makeIl = typeof(Int16ArrayFixture).GetMethod(nameof(Int16ArrayFixture.MakeString))!.GetMethodBody()!.GetILAsByteArray()!;
        int ctorOffset = Array.IndexOf(makeIl, (byte)0x73);
        int ctorToken = BitConverter.ToInt32(makeIl, ctorOffset + 1);
        HybridCpuManagedTypeDescriptorV1 stringDescriptor = types.Descriptors.Single(t => t.StableIdentity == "System.String");
        RestrictedCilImporterV1 stringImporter = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            allocationBindings: [new(ctorToken, stringDescriptor, stringHandle)]);
        RestrictedCilImportResultV1 stringImport = stringImporter.ImportImage(pe,
            new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.MakeString)));
        Check(stringImport.Status == RestrictedCilImportStatusV1.Success && stringImport.Program!.Instructions.Any(i =>
                i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_string_from_utf16_array") &&
              !stringImport.Program.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_alloc"),
            "String(char[]) newobj must lower to the variable-size factory without fixed allocation");
        ManagedCallGraphCompilationV1 metadataStringGraph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "metadata-string-factory",
                [new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.MakeString))], [], MetadataOnlyModules:
                [new(File.ReadAllBytes(typeof(object).Assembly.Location), "string-factory-corelib"),
                 new(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                     "string-factory-runtime-facade")]));
        Check(metadataStringGraph.Status == RestrictedCilImportStatusV1.Success &&
              metadataStringGraph.Methods.Single().Import.Program!.Instructions.Any(i =>
                  i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_string_from_utf16_array"),
            "Body-world metadata must bind only exact CoreLib String(char[]) to the immutable UTF-16 factory");
        HybridCpuObjectArtifactV1 stringFactoryObject = HybridCpuManagedStringFromUtf16ArrayEmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedStringFromUtf16ArrayEmitterV1.Symbol) is
                  { Support: HybridCpuManagedAbiSupportV1.Supported, MayThrow: true,
                    GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint } &&
              stringFactoryObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              stringFactoryObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedStringFromUtf16ArrayEmitterV1.Symbol && symbol.IsDefinition),
            "String(char[]) requires exact allocating/throwing ABI and defining HCO");
        RestrictedCilImportResultV1 equality = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.Equal)));
        RestrictedCilImportResultV1 inequality = importer.ImportImage(pe, new(typeof(Int16ArrayFixture).FullName!, nameof(Int16ArrayFixture.NotEqual)));
        Check(equality.Status == RestrictedCilImportStatusV1.Success && equality.Program!.Instructions.Any(i =>
                  i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_string_equals") &&
              inequality.Status == RestrictedCilImportStatusV1.Success && inequality.Program!.Instructions.Any(i =>
                  i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_string_not_equals"),
            "String equality operators must lower to exact runtime helpers");
        HybridCpuObjectArtifactV1 notEqualsObject = HybridCpuManagedStringNotEqualsEmitterV1.EmitObject();
        Check(HybridCpuManagedRuntimeEcallContractV1.StringNotEqualsOperation == 37 &&
              notEqualsObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              notEqualsObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedStringNotEqualsEmitterV1.Symbol && symbol.IsDefinition),
            "String inequality must bind an exact CPU-callable ECALL HCO");

        HybridCpuRuntimeHelperV1? storeI4 = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedArrayStoreInt32EmitterV1.Symbol);
        HybridCpuObjectArtifactV1 storeI4Object = HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject();
        Check(storeI4 is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              storeI4Object.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              storeI4Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayStoreInt32EmitterV1.Symbol &&
                  symbol.IsDefinition),
            "stelem.i4 must bind an exact allocating/throwing CPU-callable runtime thunk");
        HybridCpuRuntimeHelperV1? loadI4 = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedArrayLoadInt32EmitterV1.Symbol);
        HybridCpuObjectArtifactV1 loadI4Object = HybridCpuManagedArrayLoadInt32EmitterV1.EmitObject();
        Check(loadI4 is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              loadI4Object.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              loadI4Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayLoadInt32EmitterV1.Symbol &&
                  symbol.IsDefinition),
            "ldelem.i4 must bind an exact allocating/throwing CPU-callable runtime thunk");
        HybridCpuRuntimeHelperV1? arrayEmpty = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedArrayEmptyEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 arrayEmptyObject = HybridCpuManagedArrayEmptyEmitterV1.EmitObject();
        Check(arrayEmpty is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              arrayEmptyObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              arrayEmptyObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayEmptyEmitterV1.Symbol &&
                  symbol.IsDefinition),
            "Array.Empty must bind an exact allocating/throwing CPU-callable runtime thunk");
        HybridCpuRuntimeHelperV1? arrayLength = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedArrayLengthEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 arrayLengthObject = HybridCpuManagedArrayLengthEmitterV1.EmitObject();
        Check(arrayLength is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              arrayLengthObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              arrayLengthObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayLengthEmitterV1.Symbol &&
                  symbol.IsDefinition),
            "ldlen must bind an exact allocating/throwing CPU-callable runtime thunk");
        HybridCpuObjectArtifactV1 stringCharacterObject = HybridCpuManagedStringCharacterEmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedStringCharacterEmitterV1.Symbol) is
                  { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, MayThrow: true,
                    Support: HybridCpuManagedAbiSupportV1.Supported } &&
              stringCharacterObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              stringCharacterObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedStringCharacterEmitterV1.Symbol && symbol.IsDefinition),
            "string char must bind an exact allocating/throwing runtime thunk");
        HybridCpuObjectArtifactV1 stringLengthObject = HybridCpuManagedStringLengthEmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedStringLengthEmitterV1.Symbol) is
                  { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, MayThrow: true,
                    Support: HybridCpuManagedAbiSupportV1.Supported } &&
              stringLengthObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              stringLengthObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedStringLengthEmitterV1.Symbol && symbol.IsDefinition),
            "string length must bind an exact allocating/throwing runtime thunk");
        HybridCpuRuntimeHelperV1? storeRef = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedArrayStoreReferenceEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 storeRefObject = HybridCpuManagedArrayStoreReferenceEmitterV1.EmitObject();
        Check(storeRef is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              storeRefObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              storeRefObject.Symbols.Any(symbol => symbol.Name ==
                  HybridCpuManagedArrayStoreReferenceEmitterV1.Symbol && symbol.IsDefinition),
            "stelem.ref must bind an exact allocating/throwing CPU-callable runtime thunk");
        HybridCpuRuntimeHelperV1? initializeArray = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedInitializeArrayEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 initializeArrayObject = HybridCpuManagedInitializeArrayEmitterV1.EmitObject();
        Check(initializeArray is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              initializeArrayObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              initializeArrayObject.Symbols.Any(symbol => symbol.Name ==
                  HybridCpuManagedInitializeArrayEmitterV1.Symbol && symbol.IsDefinition),
            "RuntimeHelpers.InitializeArray must bind an exact allocating/throwing CPU-callable runtime thunk");

        byte[] fieldBlob = [1, 2, 3, 4, 5, 6, 7, 8];
        HybridCpuImageRuntimeBootstrapDescriptorV1 fieldBootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(abi.ContractDigest,
            "runtime-entry", "managed-entry", fieldData: [new(7, fieldBlob)]);
        byte[] encodedFieldBootstrap = HybridCPU.Compiler.Core.Target.Runtime.HybridCpuManagedBootstrapEncodingV1.Encode(
            fieldBootstrap);
        HybridCpuImageRuntimeBootstrapDescriptorV1 decodedFieldBootstrap = HybridCPU.Compiler.Core.Target.Runtime.HybridCpuManagedBootstrapEncodingV1.Decode(
            encodedFieldBootstrap);
        Check(decodedFieldBootstrap.DescriptorDigest == fieldBootstrap.DescriptorDigest &&
              decodedFieldBootstrap.FieldData is [{ DataHandle: 7, Data: var decodedBlob }] &&
              decodedBlob.SequenceEqual(fieldBlob),
            "FieldRVA bootstrap encoding must preserve the deterministic handle, exact bytes and descriptor digest");
        bool duplicateFieldDataRejected = false;
        try
        {
            _ = HybridCPU.Compiler.Core.Target.Runtime.HybridCpuManagedBootstrapEncodingV1.Encode(
                HybridCpuImageRuntimeBootstrapContractV1.Create(abi.ContractDigest, "runtime-entry", "managed-entry",
                    fieldData: [new(7, [1]), new(7, [2])]));
        }
        catch (ArgumentException) { duplicateFieldDataRejected = true; }
        Check(duplicateFieldDataRejected, "duplicate FieldRVA handles must fail closed before image publication");
        HybridCpuRuntimeHelperV1? newArray = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            HybridCpuManagedNewArrayEmitterV1.Symbol);
        HybridCpuObjectArtifactV1 newArrayObject = HybridCpuManagedNewArrayEmitterV1.EmitObject();
        Check(newArray is { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
                MayThrow: true, Support: HybridCpuManagedAbiSupportV1.Supported } &&
              newArrayObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              newArrayObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedNewArrayEmitterV1.Symbol &&
                  symbol.IsDefinition),
            "newarr must bind an exact allocating/throwing CPU-callable runtime thunk");
        HybridCpuObjectArtifactV1 allocateObject = HybridCpuManagedAllocateObjectEmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedAllocateObjectEmitterV1.Symbol) is
                  { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, MayThrow: true,
                    Support: HybridCpuManagedAbiSupportV1.Supported } &&
              allocateObject.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              allocateObject.Symbols.Any(symbol => symbol.Name == HybridCpuManagedAllocateObjectEmitterV1.Symbol &&
                  symbol.IsDefinition),
            "newobj allocation must bind an exact allocating/throwing CPU-callable runtime thunk");
        Check(HybridCpuManagedAbiFamilyV1.SchemaMinor == 63, "managed ABI schema");
        Console.WriteLine("PASS int16/char-array and immutable String factory/equality helpers with CoreCLR parity");
    }
}

public static class Int16ArrayFixture
{
    public static void StoreChar(char[] values, int index, char value) => values[index] = value;
    public static void StoreShort(short[] values, int index, short value) => values[index] = value;
    public static int LoadChar(char[] values, int index) => values[index];
    public static int LoadShort(short[] values, int index) => values[index];
    public static int LoadInt32Unsigned(int[] values, uint index) => values[index];
    public static void StoreInt32Unsigned(int[] values, uint index, int value) => values[index] = value;
    public static void StoreUInt32Value(uint[] values, int index, uint value) => values[index] = value;
    public static int CallInt(uint value) => TakeInt(unchecked((int)value));
    private static int TakeInt(int value) => value;
    public static bool Equal(string? first, string? second) => first == second;
    public static bool NotEqual(string? first, string? second) => first != second;
    public static string MakeString(char[] values) => new(values);
    public static int ConvertUInt16(int value) => (ushort)value;
    public static int ConvertUInt16Long(long value) => (ushort)value;
    public static int ConvertInt16(int value) => (short)value;
    public static int ConvertInt16Long(long value) => (short)value;
    public static long ConvertInt64(int value) => value;
    public static int ConvertInt32Long(long value) => (int)value;
}
