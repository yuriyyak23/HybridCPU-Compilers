using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Roslyn;
using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class ValueConstructionChecks
{
    public static void Run()
    {
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(directory, "System.Runtime.dll")), File.ReadAllBytes(Path.Combine(directory, "netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string source = """
            using HybridCPU.Compiler.Profiles;
            public readonly struct ScalarValue {
                public readonly int Value;
                public ScalarValue(int value) { Value = value; }
            }
            public static class Fixture {
                [HybridCpuKernelNoHeap] public static int Read() { var value = new ScalarValue(42); return value.Value; }
            }
            """;
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("ValueConstruction", [new("/src/Value.cs", source)], references);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedSourceBodyWorldResultV1 Compile(string text) => ManagedSourceCompanionV1.CompileBodyWorld(request with {
            Documents = [new("/src/Value.cs", text)] }, importer, [new("Fixture", "Read")], null, [0]);
        ManagedSourceBodyWorldResultV1 profiled = Compile(source);
        ManagedSourceBodyWorldResultV1 unprofiled = ManagedSourceCompanionV1.CompileBodyWorld(request with { Documents = [new("/src/Value.cs",
            source.Replace("[HybridCpuKernelNoHeap]", ""))] }, importer, [new("Fixture", "Read")], null, [0]);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 value) {
            if (value.Compilation?.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception("Value construction graph failed: " + string.Join(';', value.Source.Diagnostics) + "; " + string.Join(';', value.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(value.Compilation, value.Compilation.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception(string.Join(';', linked.Diagnostics));
            return linked;
        }
        ScalarControlFlowV2LinkedProgramV1 native = Link(profiled); ScalarControlFlowV2LinkedProgramV1 oracle = Link(unprofiled); ManagedSourceBodyWorldResultV1 repeat = Compile(source);
        static string Name(string identity) => identity[(identity.IndexOf(':', identity.IndexOf(':') + 1) + 1)..];
        static IEnumerable<(string, string)> Codes(ScalarControlFlowV2LinkedProgramV1 linked) =>
            linked.MethodObjects.Select(m => (Name(m.MethodIdentity), m.CodeSha256)).OrderBy(m => m.Item1, StringComparer.Ordinal);
        if (profiled.Compilation!.ProfileClosure?.RootProfiles.Contains(ManagedProfileClosureContractV1.KernelNoHeap) != true ||
            !Codes(native).SequenceEqual(Codes(oracle)) ||
            !profiled.Compilation.Methods.Single(m => m.Identity.StableIdentity == profiled.Compilation.Graph!.RootIdentities.Single()).Import.Program!.Instructions.Select(i => (i.Opcode, i.DataType, i.Immediate))
                .SequenceEqual(unprofiled.Compilation!.Methods.Single(m => m.Identity.StableIdentity == unprofiled.Compilation.Graph!.RootIdentities.Single()).Import.Program!.Instructions.Select(i => (i.Opcode, i.DataType, i.Immediate))) ||
            !profiled.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !profiled.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) ||
            !native.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeat).RestrictedImage!.PackageBytes) ||
            profiled.SourceMaps.Any(row => repeat.SourceMaps[row.Key].Digest != row.Value.Digest))
            throw new Exception("NoHeap value constructor physical/encoded oracle or artifact/map/image repeat mismatch");
        ManagedSourceBodyWorldResultV1 targetTyped = Compile(source.Replace("var value = new ScalarValue(42)", "ScalarValue value = new(42)"));
        string propertySource = source.Replace("public readonly int Value;", "public readonly int Value; public int ReadValue => Value;")
            .Replace("return value.Value;", "return value.ReadValue;");
        ManagedSourceBodyWorldResultV1 property = Compile(propertySource);
        ScalarControlFlowV2LinkedProgramV1 propertyImage = Link(property);
        string autoSource = source.Replace("public readonly int Value;", "public int Value { get; }");
        ManagedSourceBodyWorldResultV1 auto = Compile(autoSource);
        ManagedSourceBodyWorldResultV1 autoUnprofiled = Compile(autoSource.Replace("[HybridCpuKernelNoHeap]", ""));
        ScalarControlFlowV2LinkedProgramV1 autoImage = Link(auto); ManagedSourceBodyWorldResultV1 autoRepeat = Compile(autoSource);
        string factorySource = autoSource.Replace("public static class Fixture {",
            "public static class Fixture { public static ScalarValue Create(int value) => new ScalarValue(value);")
            .Replace("public static int Read()", "public static int Read(int input)")
            .Replace("new ScalarValue(42)", "Create(input)");
        ManagedSourceBodyWorldResultV1 factory = Compile(factorySource);
        string parameterSource = factorySource.Replace("public static class Fixture {", "public static class Fixture { public static int Consume(ScalarValue value) => value.Value;")
            .Replace("return value.Value;", "return Consume(value);");
        ManagedSourceBodyWorldResultV1 parameter = Compile(parameterSource);
        ScalarControlFlowV2LinkedProgramV1 factoryImage = Link(factory); ManagedSourceBodyWorldResultV1 factoryRepeat = Compile(factorySource);
        ManagedSourceBodyWorldResultV1 scalarFactory = Compile(factorySource.Replace("public static ScalarValue Create(int value) => new ScalarValue(value);",
            "public static int Create(int value) => value;").Replace("return value.Value;", "return value;"));
        ScalarControlFlowV2LinkedProgramV1 scalarImage = Link(scalarFactory);
        string MethodCode(ScalarControlFlowV2LinkedProgramV1 image, string method) =>
            image.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture." + method + "(", StringComparison.Ordinal)).CodeSha256;
        foreach (string expression in new[] { "value.Value", "value.Value + value.Value", "value.Value > 0 ? value.Value : 0" }) {
            string text = parameterSource.Replace("=> value.Value;", "=> " + expression + ";");
            ManagedSourceBodyWorldResultV1 consumed = Compile(text); ScalarControlFlowV2LinkedProgramV1 image = Link(consumed); ManagedSourceBodyWorldResultV1 repeated = Compile(text);
            ManagedSourceBodyWorldResultV1 scalar = Compile(text.Replace("Consume(ScalarValue value)", "Consume(int value)")
                .Replace("public static ScalarValue Create(int value) => new ScalarValue(value);", "public static int Create(int value) => value;")
                .Replace("value.Value", "value"));
            ScalarControlFlowV2LinkedProgramV1 scalarLinked = Link(scalar);
            if (new[] { "Read", "Consume" }.Any(method => MethodCode(image, method) != MethodCode(scalarLinked, method)) ||
                !Codes(image).SequenceEqual(Codes(Link(Compile(text.Replace("[HybridCpuKernelNoHeap]", ""))))) ||
                !consumed.Source.Pe!.SequenceEqual(repeated.Source.Pe!) || !consumed.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!) ||
                !image.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeated).RestrictedImage!.PackageBytes) ||
                consumed.SourceMaps.Count != 5 || consumed.SourceMaps.Any(row => repeated.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("Parameter getter scalar caller/helper/code/maps/repeat oracle failed");
            Assembly host = System.Reflection.Assembly.Load(consumed.Source.Pe!);
            Assembly scalarHost = System.Reflection.Assembly.Load(scalar.Source.Pe!);
            foreach (int input in new[] { int.MinValue, -1, 0, 1, 42, int.MaxValue })
                if (!Equals(host.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]),
                    scalarHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                    throw new Exception("Parameter emitted-CIL semantic oracle failed");
        }
        foreach (string text in new[] {
            parameterSource.Replace("=> value.Value;", "{ value = Create(1); return value.Value; }"),
            parameterSource.Replace("Consume(ScalarValue value)", "Consume(in ScalarValue value)"),
            parameterSource.Replace("=> value.Value;", "{ try { return value.Value; } finally { System.GC.Collect(); } }"),
            parameterSource.Replace("public int Value { get; }", "private readonly int _value; public int Value => _value + 1;")
                .Replace("Value = value;", "_value = value;") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(text);
            if (denied.Source.Pe is null || denied.Compilation is null || denied.Compilation.Status == RestrictedCilImportStatusV1.Success ||
                denied.Compilation.Diagnostics.Count == 0)
                throw new Exception("Mutable/ref/EH/nonpure parameter getter exceeded scalar qualification");
        }
        if (!Codes(factoryImage).SequenceEqual(Codes(Link(Compile(factorySource.Replace("[HybridCpuKernelNoHeap]", ""))))) ||
            MethodCode(factoryImage, "Read") != MethodCode(scalarImage, "Read") ||
            !factory.Source.Pe!.SequenceEqual(factoryRepeat.Source.Pe!) || !factory.Source.Pdb!.SequenceEqual(factoryRepeat.Source.Pdb!) ||
            !factoryImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(factoryRepeat).RestrictedImage!.PackageBytes) ||
            factory.SourceMaps.Count != 4 || factory.SourceMaps.Any(row => row.Value.Rows.All(r => r.Document is null) ||
                factoryRepeat.SourceMaps[row.Key].Digest != row.Value.Digest) ||
            factory.Compilation!.ProfileClosure?.RootProfiles.Contains(ManagedProfileClosureContractV1.KernelNoHeap) != true)
            throw new Exception($"Value factory oracle failed: read={MethodCode(factoryImage, "Read")}/{MethodCode(scalarImage, "Read")}; " +
                $"create={MethodCode(factoryImage, "Create")}/{MethodCode(scalarImage, "Create")}; maps={factory.SourceMaps.Count}; " +
                $"emptyMaps={factory.SourceMaps.Count(row => row.Value.Rows.All(r => r.Document is null))}; " +
                $"profile={string.Join(',', factory.Compilation!.ProfileClosure?.RootProfiles ?? [])}");
        void Copies(ScalarControlFlowV2LinkedProgramV1 image, bool requireCopy) {
            byte[] bytes = image.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Create(", StringComparison.Ordinal))
                .ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            byte previous = 10; int previousBundle = -1, count = 0; bool returned = false;
            for (int offset = 0; offset < bytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                if (!bundle.TryReadBytes(bytes, offset)) throw new Exception("Malformed factory words");
                for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                    HybridCpuInstructionWord word = bundle.GetInstruction(slot); HybridCpuOpcode opcode = (HybridCpuOpcode)word.OpCode;
                    if (opcode == HybridCpuOpcode.Nope) continue;
                    if (returned || !HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out byte rs2))
                        throw new Exception("Invalid factory physical operands");
                    if (opcode == HybridCpuOpcode.JALR && rd == HybridCpuInstructionWord.NoArchReg &&
                        rs1 == HybridCpuNativeAbiContractV2.ReturnAddressRegister &&
                        word.Immediate == HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes && previous == 10)
                    { returned = true; continue; }
                    // ADDI consumes rs1 and its immediate; the encoded rs2 payload is unused.
                    if (opcode != HybridCpuOpcode.ADDI || word.Immediate != 0 || rs1 != previous ||
                        previousBundle == offset || rd == 0)
                        throw new Exception($"Factory scalar copy chain: opcode={opcode}, rd={rd}, rs1={rs1}, rs2={rs2}, imm={word.Immediate}, previous={previous}, bundle={offset}/{previousBundle}");
                    previous = rd; previousBundle = offset; count++;
                }
            }
            if (!returned || requireCopy && count == 0) throw new Exception("Factory copy chain lacks ABI return");
        }
        Copies(factoryImage, true); Copies(scalarImage, false);
        Assembly factoryAssembly = System.Reflection.Assembly.Load(factory.Source.Pe!);
        Assembly scalarAssembly = System.Reflection.Assembly.Load(scalarFactory.Source.Pe!);
        foreach (int input in new[] { int.MinValue, -1, 0, 1, 42, int.MaxValue }) {
            object? actual = factoryAssembly.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]);
            object? expected = scalarAssembly.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]);
            if (!Equals(actual, expected) || !Equals(actual, input)) throw new Exception("Independent emitted-CIL factory semantic oracle failed");
        }
        void FactoryUnsupported(string text) {
            ManagedSourceBodyWorldResultV1 denied = Compile(text);
            if (denied.Source.Pe is null || denied.Compilation?.Status != RestrictedCilImportStatusV1.Unsupported ||
                !denied.Compilation.Diagnostics.Any(d => d.Code == "HCCIL1844"))
                throw new Exception("Factory local receiver projection exceeded its bounded contract");
        }
        foreach (string expression in new[] {
            "if (input > 0) return value.Value; return 0;",
            "if (input > 0) return value.Value + 1; return value.Value - 1;",
            "return input == 0 ? 7 : value.Value;" }) {
            string conditionalSource = factorySource.Replace("return value.Value;", expression);
            ManagedSourceBodyWorldResultV1 conditional = Compile(conditionalSource);
            ManagedSourceBodyWorldResultV1 conditionalRepeat = Compile(conditionalSource);
            ScalarControlFlowV2LinkedProgramV1 conditionalImage = Link(conditional);
            ManagedSourceBodyWorldResultV1 conditionalScalar = Compile(conditionalSource.Replace("public static ScalarValue Create(int value) => new ScalarValue(value);",
                "public static int Create(int value) => value;").Replace("value.Value", "value"));
            if (MethodCode(conditionalImage, "Read") != MethodCode(Link(conditionalScalar), "Read") ||
                !Codes(conditionalImage).SequenceEqual(Codes(Link(Compile(conditionalSource.Replace("[HybridCpuKernelNoHeap]", ""))))) ||
                !conditional.Source.Pe!.SequenceEqual(conditionalRepeat.Source.Pe!) ||
                !conditional.Source.Pdb!.SequenceEqual(conditionalRepeat.Source.Pdb!) ||
                conditional.SourceMaps.Count != 4 || conditional.SourceMaps.Any(row =>
                    row.Value.Rows.All(r => r.Document is null) || conditionalRepeat.SourceMaps[row.Key].Digest != row.Value.Digest) ||
                !conditionalImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(conditionalRepeat).RestrictedImage!.PackageBytes))
                throw new Exception("Conditional value getter scalar/native repeat oracle failed");
            Assembly host = System.Reflection.Assembly.Load(conditional.Source.Pe!);
            Assembly hostScalar = System.Reflection.Assembly.Load(conditionalScalar.Source.Pe!);
            foreach (int input in new[] { int.MinValue, -1, 0, 1, 42, int.MaxValue })
                if (!Equals(host.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]),
                    hostScalar.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                    throw new Exception("Conditional emitted-CIL semantic oracle failed");
        }
        FactoryUnsupported(factorySource.Replace("var value = Create(input); return value.Value;",
            "ScalarValue value = default; if (input > 0) value = Create(input); return value.Value;"));
        string beforeStoreSource = factorySource.Replace("var value = Create(input);",
            "if (input <= 0) input = 1; var value = Create(input); Create(input);")
            .Replace("public static class Fixture {", "public static class Fixture { public static int IntCreate(int value) => value; public static OtherValue OtherCreate(int value) => new OtherValue(value);") +
            " public readonly struct OtherValue { public readonly int Value; public OtherValue(int value) { Value = value; } }";
        ManagedSourceBodyWorldResultV1 beforeStore = Compile(beforeStoreSource);
        Link(beforeStore);
        // Regression-only mutations of actual emitted PE: make the pre-store
        // branch bypass initialization, or enter the consumed getter call.
        // These are not execution/release evidence.
        byte[] Mutation(bool enterCall, int carrierStore = 0) {
            byte[] bytes = beforeStore.Source.Pe!.ToArray();
            using PEReader reader = new PEReader(new MemoryStream(bytes));
            MetadataReader metadata = reader.GetMetadataReader();
            MethodDefinition method = metadata.GetMethodDefinition(metadata.MethodDefinitions.Single(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "Read"));
            int getter = MetadataTokens.GetToken(metadata.MethodDefinitions.Single(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "get_Value"));
            byte[] il = reader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            int address = Enumerable.Range(0, il.Length - 6).Single(index => il[index] == 0x12 && il[index + 2] == 0x28 &&
                BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(index + 3, 4)) == getter);
            if (il[0] != 0x02 || il[1] != 0x16 || il[2] is < 0x2c or > 0x37)
                throw new Exception("Dominance mutation fixture changed");
            SectionHeader section = reader.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress &&
                method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int bodyOffset = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
            int header = (bytes[bodyOffset] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bodyOffset, 2)) >> 12) * 4;
            if (carrierStore != 0) {
                int create = MetadataTokens.GetToken(metadata.MethodDefinitions.Single(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "Create"));
                int replacement = MetadataTokens.GetToken(metadata.MethodDefinitions.Single(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == (carrierStore == 1 ? "IntCreate" : "OtherCreate")));
                int call = Enumerable.Range(0, il.Length - 4).First(index => il[index] == 0x28 &&
                    BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(index + 1, 4)) == create);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bodyOffset + header + call + 1, 4), replacement);
            } else bytes[bodyOffset + header + 3] = unchecked((byte)checked((sbyte)(address + (enterCall ? 2 : 0) - 4)));
            return bytes;
        }
        foreach (int variant in new[] { 0, 1, 2, 3 }) {
            ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, Mutation(variant == 1, variant >= 2 ? variant - 1 : 0),
                "conditional-mutation", [new("Fixture", "Read")], [],
                MetadataOnlyModules: [new(references[0], "conditional-core")]));
            if (denied.Status != RestrictedCilImportStatusV1.Unsupported || !denied.Diagnostics.Any(d => d.Code == "HCCIL1844"))
                throw new Exception("Non-dominating store/targeted call/primitive carrier entered scalar projection: " + string.Join(';', denied.Diagnostics));
        }
        foreach (string statements in new[] {
            "var value = Create(input); int first = value.Value; value = Create(input + 1); return first + value.Value;",
            "ScalarValue value; if (input > 0) value = Create(input + 1); else value = Create(input - 1); return value.Value;",
            "var value = Create(input); if (input > 0) value = Create(input + 1); return value.Value;" }) {
            string text = factorySource.Replace("var value = Create(input); return value.Value;", statements);
            ManagedSourceBodyWorldResultV1 assigned = Compile(text); ManagedSourceBodyWorldResultV1 assignedRepeat = Compile(text); ScalarControlFlowV2LinkedProgramV1 image = Link(assigned);
            ManagedSourceBodyWorldResultV1 scalar = Compile(text.Replace("public static ScalarValue Create(int value) => new ScalarValue(value);",
                "public static int Create(int value) => value;").Replace("ScalarValue value;", "int value;").Replace("value.Value", "value"));
            if (MethodCode(image, "Read") != MethodCode(Link(scalar), "Read") ||
                !Codes(image).SequenceEqual(Codes(Link(Compile(text.Replace("[HybridCpuKernelNoHeap]", ""))))) ||
                !assigned.Source.Pe!.SequenceEqual(assignedRepeat.Source.Pe!) || !assigned.Source.Pdb!.SequenceEqual(assignedRepeat.Source.Pdb!) ||
                !image.RestrictedImage!.PackageBytes.SequenceEqual(Link(assignedRepeat).RestrictedImage!.PackageBytes) ||
                assigned.SourceMaps.Count != 4 || assigned.SourceMaps.Any(row => assignedRepeat.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("Value reassignment scalar SSA/encoded caller/repeat oracle failed");
            Assembly host = System.Reflection.Assembly.Load(assigned.Source.Pe!);
            Assembly scalarHost = System.Reflection.Assembly.Load(scalar.Source.Pe!);
            foreach (int input in new[] { int.MinValue, -1, 0, 1, 42, int.MaxValue })
                if (!Equals(host.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]),
                    scalarHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                    throw new Exception("Reassignment emitted-CIL semantic oracle failed");
        }
        foreach (string statements in new[] {
            "var value = Create(input); var copy = value; value = Create(input + 1); return copy.Value + value.Value;",
            "var value = Create(input); var copy = value; var next = copy; copy = Create(input + 1); return next.Value + copy.Value;",
            "var value = Create(input); ScalarValue copy; if (input > 0) copy = value; else copy = Create(input - 1); return copy.Value;" }) {
            string text = factorySource.Replace("var value = Create(input); return value.Value;", statements);
            ManagedSourceBodyWorldResultV1 copied = Compile(text); ScalarControlFlowV2LinkedProgramV1 image = Link(copied); ManagedSourceBodyWorldResultV1 repeated = Compile(text);
            ManagedSourceBodyWorldResultV1 scalar = Compile(text.Replace("public static ScalarValue Create(int value) => new ScalarValue(value);",
                "public static int Create(int value) => value;").Replace("ScalarValue copy;", "int copy;")
                .Replace("value.Value", "value").Replace("copy.Value", "copy").Replace("next.Value", "next"));
            if (MethodCode(image, "Read") != MethodCode(Link(scalar), "Read") ||
                !Codes(image).SequenceEqual(Codes(Link(Compile(text.Replace("[HybridCpuKernelNoHeap]", ""))))) ||
                !copied.Source.Pe!.SequenceEqual(repeated.Source.Pe!) || !copied.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!) ||
                !image.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeated).RestrictedImage!.PackageBytes) ||
                copied.SourceMaps.Count != 4 || copied.SourceMaps.Any(row => repeated.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("By-value local copy scalar/encoded/repeat oracle failed");
            Assembly host = System.Reflection.Assembly.Load(copied.Source.Pe!);
            Assembly scalarHost = System.Reflection.Assembly.Load(scalar.Source.Pe!);
            foreach (int input in new[] { int.MinValue, -1, 0, 1, 42, int.MaxValue })
                if (!Equals(host.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]),
                    scalarHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                    throw new Exception("Value copy emitted-CIL semantic oracle failed");
        }
        string copySource = factorySource.Replace("return value.Value;", "var copy = value; value = Create(input + 1); return copy.Value + value.Value;");
        foreach (string deniedText in new[] {
            copySource.Replace("public int Value { get; }", "public int Value { get; } public int ExtraValue => Value + 1;")
                .Replace("copy.Value", "copy.ExtraValue"),
            copySource.Replace("public static class Fixture {", "public static class Fixture { public static void Touch(ref ScalarValue value) {}")
                .Replace("var copy = value;", "var copy = value; Touch(ref value);") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(deniedText);
            if (denied.Source.Pe is null || denied.Compilation is null || denied.Compilation.Status == RestrictedCilImportStatusV1.Success ||
                denied.Compilation.Diagnostics.Count == 0)
                throw new Exception("Unsupported source/destination escaped local-copy qualification closure");
        }
        FactoryUnsupported(factorySource.Replace("public int Value { get; }", "private readonly int _value; public int Value => _value + 1;")
            .Replace("Value = value;", "_value = value;"));
        FactoryUnsupported(factorySource.Replace("return value.Value;", "try { return value.Value; } finally { input++; }"));
        FactoryUnsupported(factorySource.Replace("public readonly struct ScalarValue {", "public readonly struct ScalarValue { static ScalarValue() {}"));
        ManagedSourceBodyWorldResultV1 explicitProperty = Compile(source.Replace("public readonly int Value;",
            "private readonly int _value; public int Value => _value;").Replace("Value = value;", "_value = value;"));
        if (!Codes(autoImage).SequenceEqual(Codes(Link(autoUnprofiled))) ||
            !Codes(autoImage).SequenceEqual(Codes(Link(explicitProperty))) ||
            !Codes(autoImage).SequenceEqual(Codes(Link(Compile(autoSource.Replace("var value = new ScalarValue(42)", "ScalarValue value = new(42)"))))) ||
            !auto.Source.Pe!.SequenceEqual(autoRepeat.Source.Pe!) || !auto.Source.Pdb!.SequenceEqual(autoRepeat.Source.Pdb!) ||
            !autoImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(autoRepeat).RestrictedImage!.PackageBytes) ||
            auto.SourceMaps.Count != 3 || auto.SourceMaps.Any(row => row.Value.Rows.All(r => r.Document is null) ||
                autoRepeat.SourceMaps[row.Key].Digest != row.Value.Digest) ||
            auto.Compilation!.ProfileClosure?.RootProfiles.Contains(ManagedProfileClosureContractV1.KernelNoHeap) != true ||
            !auto.Compilation.Graph!.Edges.Any(e => e.CalleeIdentity.Contains(".get_Value", StringComparison.Ordinal)))
            throw new Exception("Auto-property receiver/closure/map/code/image oracle failed");
        ManagedSourceBodyWorldResultV1 effectGetter = Compile(propertySource.Replace("public int ReadValue => Value;",
            "public int ReadValue { get { System.GC.Collect(); return Value; } }"));
        if (effectGetter.Source.Pe is null || effectGetter.Compilation?.Status != RestrictedCilImportStatusV1.Unsupported ||
            !effectGetter.Compilation.Diagnostics.Any(d => d.Code == "HCCIL1841"))
            throw new Exception("Getter effects bypassed existing leaf receiver gate");
        if (!Codes(Link(targetTyped)).SequenceEqual(Codes(native)))
            throw new Exception("Target-typed scalar value construction changed native code");
        void Denied(string text, string id) {
            ManagedSourceBodyWorldResultV1 denied = Compile(text);
            if (denied.Source.Pe is not null || denied.Source.Pdb is not null || denied.Compilation is not null ||
                denied.Source.ArtifactIdentity is not null || denied.SourceMaps.Count != 0 ||
                !denied.Source.Diagnostics.Any(d => d.Id == id && d.Severity == "Error"))
                throw new Exception("Value construction source gate failed: " + id + "; " + string.Join(';', denied.Source.Diagnostics));
        }
        Denied(source.Replace("var value = new ScalarValue(42); return value.Value;", "object value = new ScalarValue(42); return 42;"), "HC0004");
        Denied(source.Replace("readonly struct", "class"), "HC0004");
        Denied(source.Replace("Value = value;", "System.GC.Collect(); Value = value;"), "HC0006");
        Denied(source.Replace("public readonly int Value;", "public readonly int Value; public readonly int Other;")
            .Replace("Value = value;", "Value = value; Other = 1;"), "HC0006");
        Denied(source.Replace("var value = new ScalarValue(42)", "var value = new ScalarValue()"), "HC0006");
        Denied(autoSource.Replace("{ get; }", "{ get; set; }").Replace("readonly struct", "struct"), "HC0006");
        Denied(autoSource.Replace("{ get; }", "{ get; init; }"), "HC0006");
        Denied(autoSource.Replace("{ get; }", "{ get; } = 1;"), "HC0006");
        Denied(autoSource.Replace("public int Value { get; }", "public int Value { get; } public int Other { get; }"), "HC0006");
        bool cancelled = false;
        try { ManagedSourceCompanionV1.CompileBodyWorld(request, importer, [new("Fixture", "Read")], null, [0], new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled || !Compile(source).Source.Pe!.SequenceEqual(profiled.Source.Pe!)) throw new Exception("Value constructor cancel/reuse failed");
        Console.WriteLine("PASS NoHeap pure Int32 value constructor/target-typed source -> existing CIL/physical/encoded/image oracle; PE/PDB/maps/repeat; boxing/reference/effect/shape/default/cancel gates; policy v10");
        Console.WriteLine("PASS NoHeap get-only Int32 auto-property/source receiver/getter closure/maps; explicit-property and unprofiled native-code oracles; target-typed/repeat and setter/init/initializer/shape/leaf-effect gates");
        Console.WriteLine("PASS dynamic Int32 value factory -> scalar return/local pure getter projection; independent scalar factory and unprofiled native-code oracles; maps/image repeat; rewrite/nonpure/EH negatives");
        Console.WriteLine("PASS conditional value getter single-store dominance; early return/diamond/ternary scalar encoded caller and host IL boundary oracles; unprofiled/image repeat; non-dominating initialization and targeted-call PE mutations fail closed");
        Console.WriteLine("PASS exact I4 factory reassignment and conditional store join -> existing scalar SSA/encoded caller oracle; definite initialization, primitive/wrong-struct PE store rejection, host boundary semantics and PE/PDB/maps/image repeat");
        Console.WriteLine("PASS exact initialized I4 by-value local copy/chain/conditional join; dependent-local admission and ref/nonpure-destination denial; independent scalar encoded caller and host boundary oracles; artifact/map/image repeat");
        Console.WriteLine("PASS immutable exact I4 parameter getter -> scalar ldarg/helper/caller encoded oracle; repeat/conditional/host boundary/maps/image; mutable/ref/EH/nonpure negatives");
        Console.WriteLine($"IDENTITY value-factory artifact={factory.Source.ArtifactIdentity!.Digest}; graph={factory.Compilation.Graph!.GraphDigest}; image={factoryImage.RestrictedImage.PackageSha256}; read={MethodCode(factoryImage, "Read")}; create={MethodCode(factoryImage, "Create")}");
        Console.WriteLine($"IDENTITY auto-property artifact={auto.Source.ArtifactIdentity!.Digest}; graph={auto.Compilation.Graph.GraphDigest}; image={autoImage.RestrictedImage.PackageSha256}; code={string.Join(',', Codes(autoImage))}");
        Console.WriteLine($"IDENTITY value-construction artifact={profiled.Source.ArtifactIdentity!.Digest}; graph={profiled.Compilation.Graph!.GraphDigest}; code={string.Join(',', Codes(native))}; image={native.RestrictedImage.PackageSha256}");
        ExternalValueChecks.Run();
        Int64ValueChecks.Run();
    }
}
