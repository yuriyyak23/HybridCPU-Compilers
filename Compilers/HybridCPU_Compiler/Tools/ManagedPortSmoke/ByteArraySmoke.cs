using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;

internal static class ByteArraySmoke
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        byte[] pe = File.ReadAllBytes(typeof(ByteArrayFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string? method, string? helper) in new[] { ("Store", "store_i1"), ("StoreBool", "store_i1"),
            ("Load", "load_u1"), ("LoadSigned", "load_u1"), ("LoadUInt32", "load_i4") })
        {
            RestrictedCilImportResultV1 imported = importer.ImportImage(pe, new(typeof(ByteArrayFixture).FullName!, method));
            Check(imported.Status == RestrictedCilImportStatusV1.Success && imported.Program!.Instructions.Any(i =>
                i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_array_" + helper),
                method + ": " + string.Join(";", imported.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }
        RestrictedCilImportResultV1 signedLoad = importer.ImportImage(pe, new(typeof(ByteArrayFixture).FullName!, nameof(ByteArrayFixture.LoadSigned)));
        Check(signedLoad.RequiresNativeExceptionTransfer && signedLoad.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SLLI && i.Immediate == 56) &&
            signedLoad.Program.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SRAI && i.Immediate == 56), "I1 load must use checked U1 plus exact signed extension");
        byte[] castIl = typeof(ByteArrayFixture).GetMethod(nameof(ByteArrayFixture.LoadSignedCast))!.GetMethodBody()!.GetILAsByteArray()!;
        Check(castIl.Contains((byte)0x91) && castIl.Contains((byte)0x67), "Independent signed-byte oracle must emit ldelem.u1/conv.i1");
        string Code(string method) {
            ManagedCallGraphCompilationV1 world = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "signed-byte-oracle",
                [new(typeof(ByteArrayFixture).FullName!, method)], [], MetadataOnlyModules: [new(File.ReadAllBytes(typeof(object).Assembly.Location), "core") ]));
            Check(world.Status == RestrictedCilImportStatusV1.Success, "I1 body world: " + string.Join(';', world.Diagnostics));
            if (method.Contains("Conditional"))
                Check(world.Methods.Single(m => m.Identity.MethodName == method).Import.ControlFlowAnalysis!.Phis.Count > 0, "I1 oracle must exercise a real I4 phi");
            ScalarControlFlowV2LinkedProgramV1 image = new ScalarControlFlowV2ObjectLinkerV1().Link(world, world.Graph!.RootIdentities.Single());
            Check(image.Status == ScalarControlFlowV2LinkStatusV1.Success, "I1 image: " + string.Join(';', image.Diagnostics));
            return image.MethodObjects.Single(m => m.MethodIdentity == world.Graph.RootIdentities.Single()).CodeSha256;
        }
        Check(Code(nameof(ByteArrayFixture.LoadSigned)) == Code(nameof(ByteArrayFixture.LoadSignedCast)), "I1 encoded bytes must equal independent U1/conv.i1 scalar path");
        Check(Code(nameof(ByteArrayFixture.LoadSignedConditional)) == Code(nameof(ByteArrayFixture.LoadSignedCastConditional)),
            "I1 conditional phi producer must equal independent U1/conv.i1 encoded path");
        Console.WriteLine("PASS signed byte array checked U1/sign-extension path reaches image with independent U1/conv.i1 encoded oracle");
        RestrictedCilImportResultV1 conversion = importer.ImportImage(pe, new(typeof(ByteArrayFixture).FullName!, nameof(ByteArrayFixture.ConvertByte)));
        Check(conversion.Status == RestrictedCilImportStatusV1.Success && conversion.Program!.Instructions.Any(i =>
            i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ANDI && i.Operands.Any(o => o.Value == 0xff)),
            "conv.u1 must lower to an exact byte mask");
        RestrictedCilImportResultV1 signedConversion = importer.ImportImage(pe, new(typeof(ByteArrayFixture).FullName!, nameof(ByteArrayFixture.ConvertSByte)));
        foreach (string method in new[] { nameof(ByteArrayFixture.StringByte), nameof(ByteArrayFixture.StringChar) })
        {
            RestrictedCilImportResultV1 character = importer.ImportImage(pe, new(typeof(ByteArrayFixture).FullName!, method));
            Check(character.Status == RestrictedCilImportStatusV1.Success,
                method + ": " + string.Join(";", character.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            Check(character.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_string_char") &&
                character.Program.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.AND),
                "string char helper keeps its ABI and zero-extends the return to an I4 stack value: " +
                string.Join(";", character.Program.Instructions.Select(i => i.Opcode + ":" + i.Annotation.BranchTargetSymbolName + ":" + string.Join(",", i.Operands.Select(o => o.Value)))));
            if (method == nameof(ByteArrayFixture.StringByte))
                Check(character.Program.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.ANDI && i.Operands.Any(o => o.Value == 255)),
                    "string char to byte must retain exact truncation");
        }
        Check(signedConversion.Status == RestrictedCilImportStatusV1.Success &&
            signedConversion.Program!.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SLLI && i.Operands.Any(o => o.Value == 56)) &&
            signedConversion.Program.Instructions.Any(i => i.Opcode == HybridCPU.Compiler.Core.IR.HybridCpuOpcode.SRAI && i.Operands.Any(o => o.Value == 56)),
            "conv.i1 must lower to exact 8-bit truncate/sign-extension shifts");
        PersistedAssemblyBuilder invalidAssembly = new PersistedAssemblyBuilder(new AssemblyName("ConvU1InvalidFixture"), typeof(object).Assembly);
        foreach ((OpCode opcode, Type? resultType, string? typeCode, string? stackCode) in new[] {
            (OpCodes.Conv_U4, typeof(int), "HCCIL1441", "HCCIL1440"),
            (OpCodes.Conv_U8, typeof(long), "HCCIL1880", "HCCIL1881") }) {
            PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("UnsignedConversion" + opcode.Value), typeof(object).Assembly);
            TypeBuilder type = assembly.DefineDynamicModule("Conversions").DefineType("Conversions", TypeAttributes.Public);
            ILGenerator valid = type.DefineMethod("Valid", MethodAttributes.Public | MethodAttributes.Static, resultType,
                [opcode == OpCodes.Conv_U4 ? typeof(long) : typeof(int)]).GetILGenerator();
            valid.Emit(OpCodes.Ldarg_0); valid.Emit(opcode); valid.Emit(OpCodes.Ret);
            ILGenerator wrong = type.DefineMethod("Wrong", MethodAttributes.Public | MethodAttributes.Static, resultType, [typeof(object)]).GetILGenerator();
            wrong.Emit(OpCodes.Ldarg_0); wrong.Emit(opcode); wrong.Emit(OpCodes.Ret);
            ILGenerator empty = type.DefineMethod("Empty", MethodAttributes.Public | MethodAttributes.Static, resultType, Type.EmptyTypes).GetILGenerator();
            empty.Emit(opcode); empty.Emit(OpCodes.Ret);
            type.CreateType(); using MemoryStream image = new MemoryStream(); assembly.Save(image); byte[] conversionPe = image.ToArray();
            RestrictedCilImportResultV1 result = importer.ImportImage(conversionPe, new("Conversions", "Valid"));
            Check(result.Status == RestrictedCilImportStatusV1.Success, opcode + " valid integer conversion");
            Check(opcode == OpCodes.Conv_U4
                ? result.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.ADDIW)
                : result.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SLLI && i.Immediate == 32 && i.Operands.Any(o => o.Value == 32)) &&
                  result.Program.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SRLI && i.Immediate == 32 && i.Operands.Any(o => o.Value == 32)),
                opcode + " exact truncation/zero-extension operands");
            ManagedCallGraphCompilationV1 world = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, conversionPe,
                "unsigned-conversion", [new("Conversions", "Valid")], []));
            Check(world.Status == RestrictedCilImportStatusV1.Success, opcode + " body world");
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(world, world.Graph!.RootIdentities.Single());
            Check(linked.Status == ScalarControlFlowV2LinkStatusV1.Success, opcode + " object/link/image");
            byte[] encoded = linked.MethodObjects.Single().ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            List<HybridCpuOpcode> conversionWords = new List<HybridCpuOpcode>();
            for (int offset = 0; offset < encoded.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); Check(bundle.TryReadBytes(encoded, offset), "conversion bundle bytes");
                for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                    HybridCpuInstructionWord word = bundle.GetInstruction(slot); HybridCpuOpcode operation = (HybridCpuOpcode)word.OpCode;
                    if (operation is not (HybridCpuOpcode.ADDIW or HybridCpuOpcode.SLLI or HybridCpuOpcode.SRLI)) continue;
                    Check(slot < 4 && HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out _) &&
                        rd is > 0 and < 32 && rs1 is > 0 and < 32 && word.Immediate == (operation == HybridCpuOpcode.ADDIW ? 0 : 32),
                        opcode + " encoded width/immediate/physical registers/ALU lane");
                    conversionWords.Add(operation);
                }
            }
            Check(opcode == OpCodes.Conv_U4 ? conversionWords.Contains(HybridCpuOpcode.ADDIW) :
                conversionWords.Contains(HybridCpuOpcode.SLLI) && conversionWords.Contains(HybridCpuOpcode.SRLI), opcode + " serialized operations");
            Check(importer.ImportImage(conversionPe, new("Conversions", "Wrong")).Diagnostics.Any(d => d.Code == typeCode), opcode + " rejects object");
            Check(importer.ImportImage(conversionPe, new("Conversions", "Empty")).Diagnostics.Any(d => d.Code == stackCode), opcode + " rejects underflow");
            Check(new RestrictedCilImporterV1().ImportImage(conversionPe, new("Conversions", "Valid")).Diagnostics.Any(d => d.Code == "HCCIL1001"),
                opcode + " frozen V1 remains closed");
        }
        TypeBuilder invalidType = invalidAssembly.DefineDynamicModule("Invalid").DefineType("Invalid", TypeAttributes.Public);
        ILGenerator wrongType = invalidType.DefineMethod("WrongType", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(object)]).GetILGenerator();
        wrongType.Emit(OpCodes.Ldarg_0); wrongType.Emit(OpCodes.Conv_U1); wrongType.Emit(OpCodes.Ret);
        ILGenerator underflow = invalidType.DefineMethod("Underflow", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
        underflow.Emit(OpCodes.Conv_U1); underflow.Emit(OpCodes.Ret);
        invalidType.CreateType();
        using MemoryStream invalidPe = new MemoryStream(); invalidAssembly.Save(invalidPe);
        Check(importer.ImportImage(invalidPe.ToArray(), new("Invalid", "WrongType")).Diagnostics.Any(d => d.Code == "HCCIL1870"),
            "conv.u1 must reject non-integer operands");
        Check(importer.ImportImage(invalidPe.ToArray(), new("Invalid", "Underflow")).Diagnostics.Any(d => d.Code == "HCCIL1871"),
            "conv.u1 must reject stack underflow");
        HybridCpuManagedTypeSystemBuilderV1 builder = new HybridCpuManagedTypeSystemBuilderV1();
        HybridCpuManagedTypeSystemBuildV1 built = builder.Build(new[] { "System.Byte[]", "System.Boolean[]", "System.Int32[]", "System.UInt32[]" }.Select(name =>
            new HybridCpuManagedTypeDeclarationV1(name, HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.Primitive, null, name is "System.Int32[]" or "System.UInt32[]" ? 4 : 1,
                    name is "System.Int32[]" or "System.UInt32[]" ? 4 : 1, 16, 24, true, false))));
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        ulong Handle(string name) => types.TypeHandle(types.Descriptors.Single(t => t.StableIdentity == name).TypeId)!.Value;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "boot");
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types, HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
        Check(heap.Initialize().IsSuccess, "heap");
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
        ulong bytes = arrays.NewArray(Handle("System.Byte[]"), 3).ObjectReference;
        ulong booleans = arrays.NewArray(Handle("System.Boolean[]"), 3).ObjectReference;
        DynamicMethod write = new DynamicMethod("RawBoolStore", typeof(void), [typeof(bool[]), typeof(int), typeof(int)]);
        ILGenerator il = write.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stelem_I1); il.Emit(OpCodes.Ret);
        Action<bool[], int, int> store = write.CreateDelegate<Action<bool[], int, int>>();
        DynamicMethod read = new DynamicMethod("RawBoolLoad", typeof(int), [typeof(bool[]), typeof(int)]);
        il = read.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldelem_U1); il.Emit(OpCodes.Ret);
        Func<bool[], int, int> load = read.CreateDelegate<Func<bool[], int, int>>();
        bool[] clr = new bool[3];
        foreach (int value in new[] { int.MinValue, -257, -256, -129, -128, -1, 0, 1, 2, 127, 128, 255, 256, 257, int.MaxValue })
        {
            Check(ByteArrayFixture.ConvertByte(value) == unchecked((byte)value), "CoreCLR conv.u1 parity");
            Check(ByteArrayFixture.ConvertSByte(value) == unchecked((sbyte)value), "CoreCLR conv.i1 parity");
            store(clr, 1, value);
            Check(arrays.StoreInt8(bytes, 1, value).IsSuccess && arrays.StoreInt8(booleans, 1, value).IsSuccess, "store byte/bool");
            Check(arrays.LoadUInt8(bytes, 1).ScalarValue == unchecked((byte)value), "byte truncation/zero extension");
            Check(arrays.LoadInt8(bytes, 1).ScalarValue == unchecked((sbyte)value), "signed extension");
            Check(arrays.LoadUInt8(booleans, 1).ScalarValue == load(clr, 1), "CoreCLR raw bool stelem.i1/ldelem.u1 parity");
            Check(arrays.LoadUInt8(bytes, 0).ScalarValue == 0 && arrays.LoadUInt8(bytes, 2).ScalarValue == 0, "adjacent bytes unchanged");
        }
        byte[] before = heap.ReadObjectBytes(bytes)!;
        foreach (int index in new[] { -1, 3, int.MinValue, int.MaxValue })
            Check(arrays.StoreInt8(bytes, index, 1).Status == HybridCpuManagedShapeStatusV1.BoundsViolation, "bounds");
        Check(before.SequenceEqual(heap.ReadObjectBytes(bytes)!), "failed writes are atomic");
        Check(arrays.StoreInt8(0, 0, 1).Status == HybridCpuManagedShapeStatusV1.NullReference, "null");
        Check(arrays.LoadUInt8(0, 0).Status == HybridCpuManagedShapeStatusV1.NullReference &&
              arrays.LoadUInt8(bytes, -1).Status == HybridCpuManagedShapeStatusV1.BoundsViolation &&
              arrays.LoadUInt8(bytes, 3).Status == HybridCpuManagedShapeStatusV1.BoundsViolation,
            "ldelem.u1 exact null and signed bounds status");
        HybridCpuObjectArtifactV1 storeI1Object = HybridCpuManagedArrayStoreInt8EmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedArrayStoreInt8EmitterV1.Symbol) is
                  { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, MayThrow: true,
                    Support: HybridCpuManagedAbiSupportV1.Supported } &&
              storeI1Object.Status == HybridCPU.Compiler.Core.Target.Object.HybridCpuObjectStatusV1.Success &&
              storeI1Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayStoreInt8EmitterV1.Symbol && symbol.IsDefinition),
            "stelem.i1 exact allocating/throwing runtime thunk");
        ulong ints = arrays.NewArray(Handle("System.Int32[]"), 1).ObjectReference;
        Check(arrays.StoreInt8(ints, 0, 1).Status == HybridCpuManagedShapeStatusV1.InvalidType, "width mismatch");
        Check(arrays.LoadUInt8(ints, 0).Status == HybridCpuManagedShapeStatusV1.InvalidType,
            "ldelem.u1 width mismatch");
        HybridCpuObjectArtifactV1 loadU1Object = HybridCpuManagedArrayLoadUInt8EmitterV1.EmitObject();
        Check(HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(HybridCpuManagedArrayLoadUInt8EmitterV1.Symbol) is
                  { GcTransition: HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, MayThrow: true,
                    Support: HybridCpuManagedAbiSupportV1.Supported } &&
              loadU1Object.Status == HybridCpuObjectStatusV1.Success &&
              loadU1Object.Symbols.Any(symbol => symbol.Name == HybridCpuManagedArrayLoadUInt8EmitterV1.Symbol && symbol.IsDefinition),
            "ldelem.u1 exact allocating/throwing runtime thunk");
        ulong uints = arrays.NewArray(Handle("System.UInt32[]"), 1).ObjectReference;
        Check(arrays.StoreInt32(uints, 0, unchecked((int)0xfedcba98u)).IsSuccess &&
            unchecked((uint)arrays.LoadInt32(uints, 0).ScalarValue) == 0xfedcba98u,
            "ldelem.u4 runtime bits must match CoreCLR UInt32 semantics");
        Check(arrays.LoadUInt8(arrays.Empty(Handle("System.Byte[]")).ObjectReference, 0).Status == HybridCpuManagedShapeStatusV1.BoundsViolation, "empty");
        ulong bootBlob=arrays.NewArray(Handle("System.Byte[]"),3).ObjectReference;
        Check(arrays.StoreInt8(bootBlob,0,0x49).IsSuccess&&arrays.StoreInt8(bootBlob,1,0x57).IsSuccess&&
              arrays.StoreInt8(bootBlob,2,0x41).IsSuccess,"boot blob payload");
        HybridCpuManagedGcRootRegistryV1 roots =new HybridCpuManagedGcRootRegistryV1();
        HybridCpuManagedBootBlobRegistryV1 registry =new HybridCpuManagedBootBlobRegistryV1(types,heap,roots);
        Check(registry.Register(1,bootBlob)&&registry.Register(1,bootBlob)&&!registry.Register(0,bootBlob)&&
              roots.Snapshot() is [{ Source:HybridCpuManagedGcRootSourceV1.Handle,ObjectReference:var rooted }]&&rooted==bootBlob,
            "boot blob exact registration and process root");
        DeterministicRuntimeKernelV1 blobKernel =new DeterministicRuntimeKernelV1(null,registry);
        Check(blobKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,new string('a',64),
            0x100000,4096,0x100000,0x200000,4096,0,1000)).IsSuccess,"blob kernel boot");
        HybridCpuExternalServiceRequestV1 unsignedBlob =new HybridCpuExternalServiceRequestV1(blobKernel.CurrentContext()!.ContextId,
            HybridCpuHostServiceV1.File,HybridCpuBootBlobServiceContractV1.GetOperation,HybridCpuPrivilegeModeV1.User,
            0,0,HybridCpuHostBufferAccessV1.None,[1],string.Empty);
        HybridCpuExternalServiceRequestV1 signedBlob =unsignedBlob with { TransitionDigest=HybridCpuManagedInteropContractV1.ComputeTransitionDigest(unsignedBlob) };
        HybridCpuExternalServiceResultV1 resolvedBlob=blobKernel.ExternalServiceTransition(signedBlob);
        Check(resolvedBlob.IsSuccess&&resolvedBlob.ReturnValue==bootBlob,"trusted boot blob service reference");
        HybridCpuExternalServiceRequestV1 missingBlob =unsignedBlob with { Arguments=[2UL],TransitionDigest=string.Empty };
        missingBlob=missingBlob with { TransitionDigest=HybridCpuManagedInteropContractV1.ComputeTransitionDigest(missingBlob) };
        Check(!blobKernel.ExternalServiceTransition(missingBlob).IsSuccess,"missing boot blob fail closed");
        byte[] bootThunk=HybridCpuManagedBootBlobEmitterV1.Emit();
        Check(Enumerable.Range(0,bootThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes).Count(index=>
                  (BitConverter.ToUInt64(bootThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)==(uint)HybridCpuOpcode.ECALL)==1&&
              HybridCpuManagedBootBlobEmitterV1.EmitObject().Status==HybridCpuObjectStatusV1.Success,
            "boot blob exact native thunk");
        byte[] exitThunk=HybridCpuManagedGuestProcessExitEmitterV1.Emit();
        Check(Enumerable.Range(0,exitThunk.Length/HybridCpuBundleSerializer.BundleSizeBytes).All(index=>
              (HybridCpuOpcode)(BitConverter.ToUInt64(exitThunk,index*HybridCpuBundleSerializer.BundleSizeBytes)>>48)
                  is not (HybridCpuOpcode.ECALL or HybridCpuOpcode.EBREAK))&&
              HybridCpuManagedGuestProcessExitEmitterV1.EmitObject().Status==HybridCpuObjectStatusV1.Success,
            "guest process exit must use the restricted non-trap boundary");
        Console.WriteLine("PASS byte-array CIL helpers, truncation/sign extension, CoreCLR raw-bool parity and fail-closed bounds");
    }
}

public static class ByteArrayFixture
{
    public static int LoadSignedCast(byte[] values, int index) => unchecked((sbyte)values[index]);
    public static int LoadSignedConditional(sbyte[] values, int index, bool first) => (first ? values[index] : values[index + 1]) + 7;
    public static int LoadSignedCastConditional(byte[] values, int index, bool first) => (first ? unchecked((sbyte)values[index]) : unchecked((sbyte)values[index + 1])) + 7;
    public static void Store(byte[] values, int index, byte value) => values[index] = value;
    public static void StoreBool(bool[] values, int index, bool value) => values[index] = value;
    public static int Load(byte[] values, int index) => values[index];
    public static int LoadSigned(sbyte[] values, int index) => values[index];
    public static uint LoadUInt32(uint[] values, int index) => values[index];
    public static int ConvertByte(int value) => (byte)value;
    public static int ConvertSByte(int value) => (sbyte)value;
    public static int StringByte(string value, int index) => (byte)value[index];
    public static int StringChar(string value, int index) => value[index];
}
