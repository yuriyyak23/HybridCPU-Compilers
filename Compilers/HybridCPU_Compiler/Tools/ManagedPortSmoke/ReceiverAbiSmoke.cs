using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

internal static class ReceiverAbiSmoke
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string Detail(ManagedCallGraphCompilationV1 graph) => string.Join(';', graph.Diagnostics.Select(d => d.Code + ":" + d.Message));
    public static void Run(string? doomCorePath)
    {
        VerifyLsuTopology();
        byte[] pe = File.ReadAllBytes(typeof(ReceiverPayload).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 Import(string type, string name, byte[]? image = null) => importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, image ?? pe, "receiver-smoke", [new(type, name)], []));
        foreach (string suffix in new[] { "I1", "I2", "I4", "I8" })
        {
            ManagedCallGraphCompilationV1 setter = Import(nameof(ReceiverPayload), "Set" + suffix);
            ManagedCallGraphCompilationV1 getter = Import(nameof(ReceiverPayload), "Get" + suffix);
            Check(setter.Status == RestrictedCilImportStatusV1.Success && getter.Status == RestrictedCilImportStatusV1.Success,
                suffix + " receiver import: " + Detail(setter) + Detail(getter));
            foreach (ManagedCallGraphCompilationV1? graph in new[] { setter, getter })
            {
                ManagedCompiledMethodV1 method = graph.Methods.Single();
                IrProgram program = method.Import.Program!;
                Check(method.Import.ReceiverAbi is { PayloadSizeBytes: 16, PayloadAlignmentBytes: 8, NonEscaping: true, NoSafepoints: true } &&
                    method.Identity.CanonicalSignature.Contains("ReceiverPayload&"), "Receiver identity and extent");
                Check(program.ValueFlow.Values.Where(v => v.StableId.Contains(":arg:0") || v.StableId.Contains(":receiver-field-address"))
                    .All(v => v.VirtualClass == IrVirtualValueClass.ManagedByRef && v.ValueKind.Kind == IrCanonicalValueKind.ManagedByRef && v.ValueKind.BitWidth == 64),
                    "Receiver and derived address must remain managed-byref, not scalar/object aliases");
                Check(program.Instructions.All(i => i.Annotation.ControlFlowKind != IrControlFlowKind.Call),
                    "Payload field access must not invoke object null-check/GC helpers");
                ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
                Check(linked.Diagnostics.Any(d => d.Code == "HCSCF-LINK4012") && linked.RestrictedImage is null,
                    "Callee-only loan must not grant caller/image authority");
                // Exercise the real scheduler/register allocator/encoder/HCO writer, not
                // the image gate. No image or native execution is claimed by this check.
                MethodInfo compile = typeof(ScalarControlFlowV2ObjectLinkerV1).GetMethod("CompileMethod", BindingFlags.NonPublic | BindingFlags.Static)!;
                ScalarControlFlowV2MethodObjectV1 obj = (ScalarControlFlowV2MethodObjectV1)compile.Invoke(null, new object[] { method, 0 })!;
                ScalarControlFlowV2MethodObjectV1 repeated = (ScalarControlFlowV2MethodObjectV1)compile.Invoke(null, new object[] { method, 0 })!;
                Check(obj.ObjectArtifact.Status == HybridCpuObjectStatusV1.Success && obj.CodeBytes > 0 &&
                    obj.CodeSha256 == repeated.CodeSha256, "Receiver backend/HCO and deterministic code");
                VerifyLsuBytes(obj.ObjectArtifact.Sections.Single(s => s.Name == ".text").Data);
                ManagedCallGraphCompilationV1 stripped = graph with { Methods = [method with { Import = method.Import with { ReceiverAbi = null } }] };
                Check(new ScalarControlFlowV2ObjectLinkerV1().Link(stripped, graph.Graph.RootIdentities.Single())
                    .Diagnostics.Any(d => d.Code == "HCSCF-LINK4012"), "Dropping the plan must not erase the IR byref gate");
            }
            int size = int.Parse(suffix[1..]);
            int offset = (int)Marshal.OffsetOf<ReceiverPayload>(suffix);
            foreach (long input in new long[] { 0, 1, -1, 127, 128, 255, 32767, 32768, int.MinValue, int.MaxValue, long.MinValue, long.MaxValue })
            {
                ReceiverPayload reference = new ReceiverPayload();
                long expected;
                switch (suffix)
                {
                    case "I1": reference.SetI1(unchecked((sbyte)input)); expected = reference.GetI1(); break;
                    case "I2": reference.SetI2(unchecked((short)input)); expected = reference.GetI2(); break;
                    case "I4": reference.SetI4(unchecked((int)input)); expected = reference.GetI4(); break;
                    default: reference.SetI8(input); expected = reference.GetI8(); break;
                }
                byte[] memory = Enumerable.Repeat((byte)0xcc, 32).ToArray();
                Evaluate(setter.Methods.Single().Import.Program!, memory, unchecked((ulong)expected));
                ulong actual = Evaluate(getter.Methods.Single().Import.Program!, memory, 0);
                Check(size == 8 ? actual == unchecked((ulong)expected) : (uint)actual == unchecked((uint)expected), "Emitted load differs from CoreCLR " + suffix);
                for (int i = 0; i < memory.Length; i++)
                    Check(memory[i] == (i >= 8 + offset && i < 8 + offset + size
                        ? (byte)(unchecked((ulong)expected) >> ((i - 8 - offset) * 8)) : (byte)0xcc),
                        "Store changed header/neighbor/padding bytes " + suffix);
            }
        }
        ManagedCallGraphCompilationV1 ctor = Import(nameof(ReceiverInt), ".ctor");
        Check(ctor.Status == RestrictedCilImportStatusV1.Success && ctor.Methods.Single().Import.ReceiverAbi!.PayloadSizeBytes == 4,
            "Readonly struct constructor payload: " + Detail(ctor));
        Check(Import(nameof(ReceiverPayload), nameof(ReceiverPayload.Call)).Diagnostics.Any(d => d.Code == "HCCIL1841"), "Receiver must not cross a call/safepoint");
        Check(Import(nameof(ReceiverPayload), nameof(ReceiverPayload.Loop)).Diagnostics.Any(d => d.Code == "HCCIL1841"), "Loop safepoints remain closed");
        Check(Import(nameof(ReceiverWithReference), nameof(ReceiverWithReference.Get)).Diagnostics.Any(d => d.Code == "HCCIL1840"), "GC-containing payload must fail closed");
        Check(Import(nameof(PackedReceiver), nameof(PackedReceiver.Get)).Diagnostics.Any(d => d.Code == "HCCIL1840"), "Packed payload must fail closed");
        Check(Import("A", "Foreign", InvalidFieldFixture()).Diagnostics.Any(d => d.Code == "HCCIL1842"), "Same-size foreign field owner must fail closed");
        Check(Import("A", "ReadonlyStore", InvalidFieldFixture()).Diagnostics.Any(d => d.Code == "HCCIL1842"), "Readonly store outside ctor must fail closed");
        Check(Import("A", "ObjectReceiver", InvalidFieldFixture()).Diagnostics.Any(d => d.Code == "HCCIL1202"), "An object reference is not a value-type loan");
        ManagedCallGraphCompilationV1 callerOwned = Import(nameof(ReceiverCaller), nameof(ReceiverCaller.Roundtrip));
        Check(callerOwned.Status == RestrictedCilImportStatusV1.Success,
            "Caller-owned receiver import: " + Detail(callerOwned));
        ManagedCompiledMethodV1 callerMethod = callerOwned.Methods.Single(method =>
            method.Identity.MethodName == nameof(ReceiverCaller.Roundtrip));
        ManagedReceiverCallerStoragePlanV1 storage = callerMethod.Import.ReceiverCallerStoragePlans?.Single()
            ?? throw new Exception("Caller-owned receiver storage proof is absent");
        Check(storage.PayloadSizeBytes == 4 && storage.PayloadAlignmentBytes == 4 && storage.Calls.Count == 2 &&
            storage.Calls.All(call => call.AbiLayoutDigest.Length == 64 && call.CallProofDigest.Length == 64),
            "Caller-owned storage must bind constructor/getter ABI calls");
        Check(callerMethod.Import.Program!.Instructions.Any(instruction =>
                instruction.Annotation.FixedFrameSlotIdentity == storage.FrameSlotIdentity &&
                instruction.CanonicalType.Kind == IrCanonicalValueKind.ManagedByRef) &&
            callerMethod.Import.Program.Instructions.Where(instruction => storage.Calls.Any(call =>
                call.CilOffset == instruction.SourceSpan.StartOffset && call.CalleeIdentity == instruction.Annotation.BranchTargetSymbolName))
                .All(static instruction => !instruction.Annotation.IsManagedGcSafepoint),
            "Receiver addresses must use one fixed frame slot and only proven leaf calls may suppress safepoints");
        foreach (ManagedReceiverCallerStorageCallV1 call in storage.Calls)
        {
            IrInstruction transfer = callerMethod.Import.Program.Instructions.Single(instruction =>
                instruction.SourceSpan.StartOffset == call.CilOffset &&
                instruction.Annotation.BranchTargetSymbolName == call.CalleeIdentity);
            IrOperand receiverAbi = transfer.Annotation.Uses.Single(use =>
                use.Name.Contains(":call-arg-abi:0", StringComparison.Ordinal));
            Check(HasFrameSlotProvenance(receiverAbi, new HashSet<IrOperand>()),
                "Receiver ABI argument must be an exact managed-byref copy of the proven frame-slot address");
        }
        bool HasFrameSlotProvenance(IrOperand value, HashSet<IrOperand> visited)
        {
            if (!visited.Add(value)) return false;
            IrInstruction? definition = callerMethod.Import.Program.Instructions.SingleOrDefault(instruction =>
                instruction.Annotation.Defs.Contains(value));
            if (definition is null || definition.Opcode != HybridCpuOpcode.ADDI ||
                definition.CanonicalType.Kind != IrCanonicalValueKind.ManagedByRef) return false;
            if (definition.Annotation.FixedFrameSlotIdentity == storage.FrameSlotIdentity) return true;
            return definition.Annotation.Uses.Where(static use => use.Kind == IrOperandKind.VirtualValue)
                .Any(use => HasFrameSlotProvenance(use, visited));
        }
        ScalarControlFlowV2LinkedProgramV1 callerLinked = new ScalarControlFlowV2ObjectLinkerV1().Link(
            callerOwned, callerOwned.Graph!.RootIdentities.Single());
        Check(callerLinked.Status == ScalarControlFlowV2LinkStatusV1.Success && callerLinked.RestrictedImage is not null,
            "Caller/callee receiver proof must survive allocation, metadata, object and image link: " +
            string.Join(';', callerLinked.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        ScalarControlFlowV2MethodObjectV1 linkedCaller = callerLinked.MethodObjects.Single(method =>
            method.MethodIdentity == callerMethod.Identity.StableIdentity);
        foreach (ScalarControlFlowV2MethodObjectV1 method in callerLinked.MethodObjects)
            VerifyLsuBytes(method.ObjectArtifact.Sections.Single(s => s.Name == ".text").Data);
        Check(linkedCaller.GcInfo is { Length: >= 12 } &&
              BinaryPrimitives.ReadUInt32LittleEndian(linkedCaller.GcInfo) == 0x474d4348 &&
              BinaryPrimitives.ReadInt32LittleEndian(linkedCaller.GcInfo.AsSpan(8)) == 0,
            "No-safepoint receiver calls must produce an exact empty HCMG map; managed-byref roots are not qualified");
        ManagedReceiverCallerStoragePlanV1 forgedStorage = storage with { StorageProofDigest = new string('0', 64) };
        ManagedCompiledMethodV1 forgedCaller = callerMethod with
        {
            Import = callerMethod.Import with { ReceiverCallerStoragePlans = [forgedStorage] }
        };
        ManagedCallGraphCompilationV1 forgedGraph = callerOwned with
        {
            Methods = callerOwned.Methods.Select(method => method.Identity.StableIdentity == forgedCaller.Identity.StableIdentity
                ? forgedCaller : method).ToArray()
        };
        Check(new ScalarControlFlowV2ObjectLinkerV1().Link(forgedGraph, forgedGraph.Graph!.RootIdentities.Single())
                .Diagnostics.Any(static diagnostic => diagnostic.Code == "HCSCF-LINK4012"),
            "A mutated caller-storage proof must fail closed at the linker");
        foreach ((string suffix, HybridCpuOpcode load, HybridCpuOpcode store) in new[]
        {
            ("I1", HybridCpuOpcode.LB, HybridCpuOpcode.SB),
            ("I2", HybridCpuOpcode.LH, HybridCpuOpcode.SH),
            ("I4", HybridCpuOpcode.LW, HybridCpuOpcode.SW),
            ("I8", HybridCpuOpcode.LD, HybridCpuOpcode.SD)
        })
        {
            ManagedCallGraphCompilationV1 getter = Import(nameof(ObjectFieldPayload), "Get" + suffix);
            ManagedCallGraphCompilationV1 setter = Import(nameof(ObjectFieldPayload), "Set" + suffix);
            Check(getter.Status == RestrictedCilImportStatusV1.Success && setter.Status == RestrictedCilImportStatusV1.Success,
                suffix + " object field import: " + Detail(getter) + Detail(setter));
            Check(getter.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == load) &&
                setter.Methods.Single().Import.Program!.Instructions.Any(i => i.Opcode == store),
                suffix + " object field must retain its exact signed load and narrow store width");
            Check(new[] { getter, setter }.SelectMany(g => g.Methods.Single().Import.Program!.Instructions)
                .Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check"),
                suffix + " object field must retain the managed null-check boundary");
        }
        if (doomCorePath is not null) VerifyDoomConstructor(File.ReadAllBytes(doomCorePath));
        Console.WriteLine("PASS typed receiver payload offsets/widths, CoreCLR parity, noescape/GC/readonly/type gates and deterministic backend HCO");
    }

    // This interprets the emitted straight-line IR fragment. It is not native execution.
    private static ulong Evaluate(IrProgram program, byte[] memory, ulong input)
    {
        Dictionary<string, ulong> values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (IrVirtualValueV1? value in program.ValueFlow.Values.Where(v => v.StableId.EndsWith(":abi", StringComparison.Ordinal)))
            values[value.StableId] = value.StableId.Contains(":arg:0:") ? 8UL : input;
        ulong Read(IrOperand operand) => operand.Kind == IrOperandKind.Constant ? operand.Value :
            operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 0 ? 0 : values[operand.Name];
        foreach (IrInstruction instruction in program.Instructions)
        {
            IReadOnlyList<IrOperand> operands = instruction.Operands;
            if (instruction.Opcode == HybridCpuOpcode.JALR) return operands.Count == 0 ? 0 : Read(operands[0]);
            ulong result;
            if (instruction.Opcode == HybridCpuOpcode.ADDI) result = unchecked(Read(operands[0]) + Read(operands[1]));
            else
            {
                int size = instruction.Opcode switch
                {
                    HybridCpuOpcode.LB or HybridCpuOpcode.LBU or HybridCpuOpcode.SB => 1,
                    HybridCpuOpcode.LH or HybridCpuOpcode.LHU or HybridCpuOpcode.SH => 2,
                    HybridCpuOpcode.LW or HybridCpuOpcode.LWU or HybridCpuOpcode.SW => 4,
                    HybridCpuOpcode.LD or HybridCpuOpcode.SD => 8,
                    _ => throw new Exception("Unexpected receiver IR operation " + instruction.Opcode)
                };
                int address = checked((int)Read(operands[0]));
                if (instruction.Opcode is HybridCpuOpcode.SB or HybridCpuOpcode.SH or HybridCpuOpcode.SW or HybridCpuOpcode.SD)
                {
                    ulong stored = Read(operands[1]);
                    for (int i = 0; i < size; i++) memory[address + i] = (byte)(stored >> (i * 8));
                    continue;
                }
                result = 0;
                for (int i = 0; i < size; i++) result |= (ulong)memory[address + i] << (i * 8);
                if (instruction.Opcode is HybridCpuOpcode.LB or HybridCpuOpcode.LH or HybridCpuOpcode.LW)
                    result = unchecked((ulong)((long)(result << (64 - size * 8)) >> (64 - size * 8)));
            }
            values[instruction.Annotation.Defs.Single(d => d.Kind == IrOperandKind.VirtualValue).Name] = result;
        }
        throw new Exception("Receiver fragment did not return");
    }

    private static void VerifyLsuTopology()
    {
        Check(HybridCpuMachineDescriptionV1.Default.TryGetSlotClass(IrSlotClass.LsuClass, out HybridCpuSlotClassTopologyV1? lsu) &&
            lsu.PhysicalLaneMask == (byte)IrIssueSlotMask.Lsu && lsu.Capacity == 2,
            "Scalar LSU mask must match the existing typed topology");
        foreach (HybridCpuOpcode opcode in new[] { HybridCpuOpcode.LB, HybridCpuOpcode.LBU, HybridCpuOpcode.LH, HybridCpuOpcode.LHU,
            HybridCpuOpcode.LW, HybridCpuOpcode.LWU, HybridCpuOpcode.LD, HybridCpuOpcode.SB, HybridCpuOpcode.SH, HybridCpuOpcode.SW, HybridCpuOpcode.SD }) {
            IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(opcode);
            Check(profile.LegalSlots == IrIssueSlotMask.Lsu && profile.DerivedSlotClass == IrSlotClass.LsuClass,
                "Scalar memory profile must use physical LSU lanes: " + opcode);
        }
        Check(HybridCpuSlotModel.HasStructuralPlacement([IrIssueSlotMask.Lsu, IrIssueSlotMask.Lsu, IrIssueSlotMask.DmaStream]) &&
            !HybridCpuSlotModel.HasStructuralPlacement([IrIssueSlotMask.Lsu, IrIssueSlotMask.Lsu, IrIssueSlotMask.Lsu]),
            "Two LSU lanes must coexist with lane6 transport, while three LSU instructions overcommit");
        HybridCpuInstructionWord[] words = [new() { OpCode = (uint)HybridCpuOpcode.LD, DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue, Word1 = HybridCpuInstructionWord.PackArchRegs(10, 2, HybridCpuInstructionWord.NoArchReg) },
            new() { OpCode = (uint)HybridCpuOpcode.LD, DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue, Word1 = HybridCpuInstructionWord.PackArchRegs(14, 2, HybridCpuInstructionWord.NoArchReg), Immediate = 8 }];
        HybridCpuCompiledProgram compiled = HybridCpuCanonicalCompiler.CompileProgram(0, words);
        HybridCpuAgreementSummaryV1 summary = HybridCpuAgreementSummaryV1.From(compiled.AdmissibilityAgreement);
        Check(summary.StructuralFailureCount == 0 && summary.TypedSlotInvalidCount == 0 &&
            summary.TypedSlotValidCount == summary.BundleCount && summary.BundleCount > 0,
            "Final scalar LSU bundle agreement must remain valid: " + summary.FormatDiagnostic());
        VerifyLsuBytes(compiled.ProgramImage);
        Check(compiled.ProgramImage.SequenceEqual(HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage),
            "Scalar LSU final image must repeat");
        Console.WriteLine("PASS scalar LSU topology/profile masks, two-lane capacity/lane6 separation, final canonical agreement/encoded lanes and repeat");
    }

    private static void VerifyLsuBytes(byte[] bytes)
    {
        int accesses = 0;
        for (int offset = 0; offset < bytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            Check(bundle.TryReadBytes(bytes, offset), "Receiver encoded bundle is truncated");
            for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                HybridCpuInstructionWord word = bundle.GetInstruction(slot); HybridCpuOpcode op = (HybridCpuOpcode)word.OpCode;
                if (op is not (HybridCpuOpcode.LB or HybridCpuOpcode.LBU or HybridCpuOpcode.LH or HybridCpuOpcode.LHU or
                    HybridCpuOpcode.LW or HybridCpuOpcode.LWU or HybridCpuOpcode.LD or HybridCpuOpcode.SB or
                    HybridCpuOpcode.SH or HybridCpuOpcode.SW or HybridCpuOpcode.SD)) continue;
                Check(slot is 4 or 5, $"Scalar memory {op} must be encoded in LSU lanes4/5, found{slot}");
                Check(HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out _, out byte rs1, out _) && rs1 < 32,
                    "Receiver memory must use physical architectural address operands");
                accesses++;
            }
        }
        Check(accesses > 0, "Receiver byte oracle must observe actual memory instructions");
    }

    private static byte[] InvalidFieldFixture()
    {
        PersistedAssemblyBuilder builder = new PersistedAssemblyBuilder(new AssemblyName("ReceiverInvalidFields"), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule("Invalid");
        TypeBuilder other = module.DefineType("B", TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed, typeof(ValueType));
        FieldBuilder foreign = other.DefineField("Value", typeof(int), FieldAttributes.Public); other.CreateType();
        TypeBuilder owner = module.DefineType("A", TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed, typeof(ValueType));
        FieldBuilder field = owner.DefineField("Value", typeof(int), FieldAttributes.Public | FieldAttributes.InitOnly);
        ILGenerator read = owner.DefineMethod("Foreign", MethodAttributes.Public, typeof(int), Type.EmptyTypes).GetILGenerator();
        read.Emit(OpCodes.Ldarg_0); read.Emit(OpCodes.Ldfld, foreign); read.Emit(OpCodes.Ret);
        ILGenerator write = owner.DefineMethod("ReadonlyStore", MethodAttributes.Public, typeof(void), [typeof(int)]).GetILGenerator();
        write.Emit(OpCodes.Ldarg_0); write.Emit(OpCodes.Ldarg_1); write.Emit(OpCodes.Stfld, field); write.Emit(OpCodes.Ret);
        ILGenerator objectRead = owner.DefineMethod("ObjectReceiver", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(object)]).GetILGenerator();
        objectRead.Emit(OpCodes.Ldarg_0); objectRead.Emit(OpCodes.Ldfld, field); objectRead.Emit(OpCodes.Ret);
        owner.CreateType(); using MemoryStream stream = new MemoryStream(); builder.Save(stream); return stream.ToArray();
    }

    private static void VerifyDoomConstructor(byte[] pe)
    {
        Type importer = typeof(RestrictedCilImporterV1);
        Type context = importer.GetNestedType("ManagedModuleContext", BindingFlags.NonPublic)!;
        List<IDisposable> disposal = new List<IDisposable>();
        Array Wrap(IEnumerable<byte[]> images)
        {
            byte[][] list = images.ToArray(); Array array = Array.CreateInstance(context, list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                IDisposable item = (IDisposable)Activator.CreateInstance(context, new object[] { (ReadOnlyMemory<byte>)list[i], "doom-receiver" })!;
                disposal.Add(item); array.SetValue(item, i);
            }
            return array;
        }
        try
        {
            string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            IEnumerable<byte[]> references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "mscorlib.dll" }.Select(name => File.ReadAllBytes(Path.Combine(runtime, name)));
            object products = importer.GetMethod("BuildMetadataBindings", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { Wrap([pe]), Wrap(references) })!;
            IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1> fields = (IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1>)products.GetType().GetProperty("Fields")!.GetValue(products)!;
            IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1> inits = (IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1>)products.GetType().GetProperty("TypeInitializers")!.GetValue(products)!;
            RestrictedCilImportResultV1 result = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                fieldLayouts: fields.Values.ToArray(), typeInitializationBindings: inits.Values.ToArray())
                .ImportImage(pe, new("DoomSharp.Core.Fixed", ".ctor"));
            Check(result.Status == RestrictedCilImportStatusV1.Success && result.ReceiverAbi is { PayloadSizeBytes: 4 },
                "Real Doom Fixed constructor: " + string.Join(';', result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            byte[] memory = Enumerable.Repeat((byte)0xcc, 32).ToArray();
            Evaluate(result.Program!, memory, 0x12345678);
            Check(BitConverter.ToInt32(memory, 8) == 0x12345678 && memory.Where((_, i) => i < 8 || i >= 12).All(b => b == 0xcc),
                "Real Doom ctor must write exactly four payload bytes");
            Console.WriteLine("PASS actual DoomSharp.Core.Fixed..ctor import and exact emitted payload store (IR, not guest execution)");
        }
        finally { foreach (IDisposable item in disposal) item.Dispose(); }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct ReceiverPayload
{
    public sbyte I1;
    public short I2;
    public int I4;
    public long I8;
    public void SetI1(sbyte value) { I1 = value; }
    public void SetI2(short value) { I2 = value; }
    public void SetI4(int value) { I4 = value; }
    public void SetI8(long value) { I8 = value; }
    public sbyte GetI1() => I1;
    public short GetI2() => I2;
    public int GetI4() => I4;
    public long GetI8() => I8;
    public int Call() => Math.Abs(I4);
    public int Loop(int count) { int i = 0; while (i < count) i++; return I4; }
}
public readonly struct ReceiverInt
{
    public readonly int Value;
    public ReceiverInt(int value) { Value = value; }
}
public struct ReceiverScalar
{
    public int Value;
    public ReceiverScalar(int value) { Value = value; }
    public int Get() => Value;
}
public static class ReceiverCaller
{
    public static int Roundtrip(int value)
    {
        ReceiverScalar receiver = new ReceiverScalar(value);
        return receiver.Get();
    }
}
public struct ReceiverWithReference { public object? Value; public object? Get() => Value; }
[StructLayout(LayoutKind.Sequential)]
public sealed class ObjectFieldPayload
{
    public sbyte I1;
    public short I2;
    public int I4;
    public long I8;
    public void SetI1(sbyte value) => I1 = value;
    public void SetI2(short value) => I2 = value;
    public void SetI4(int value) => I4 = value;
    public void SetI8(long value) => I8 = value;
    public sbyte GetI1() => I1;
    public short GetI2() => I2;
    public int GetI4() => I4;
    public long GetI8() => I8;
}
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedReceiver { public byte First; public int Value; public int Get() => Value; }
