using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection;

internal static class Int64ValueChecks
{
    public static void Run()
    {
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(directory, "System.Runtime.dll")), File.ReadAllBytes(Path.Combine(directory, "netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string source = """
            using HybridCPU.Compiler.Profiles;
            public readonly struct WideValue {
                public long Value { get; }
                public WideValue(long value) { Value = value; }
            }
            public static class Fixture {
                [HybridCpuKernelNoHeap] public static long Read(long input) {
                    var value = new WideValue(input); return value.Value;
                }
            }
            """;
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("Int64Value", [new("/src/Wide.cs", source)], references);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedSourceBodyWorldResultV1 profiled = ManagedSourceCompanionV1.CompileBodyWorld(request, importer, [new("Fixture", "Read")], null, [0]);
        ManagedSourceBodyWorldResultV1 unprofiled = ManagedSourceCompanionV1.CompileBodyWorld(request with {
            Documents = [new("/src/Wide.cs", source.Replace("[HybridCpuKernelNoHeap]", ""))] },
            importer, [new("Fixture", "Read")], null, [0]);
        ManagedSourceBodyWorldResultV1 Compile(string text) => ManagedSourceCompanionV1.CompileBodyWorld(request with {
            Documents = [new("/src/Wide.cs", text)] }, importer, [new("Fixture", "Read")], null, [0]);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 value) {
            if (value.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception(string.Join(';', value.Source.Diagnostics) + string.Join(';', value.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 image = new ScalarControlFlowV2ObjectLinkerV1().Link(value.Compilation, value.Compilation.Graph!.RootIdentities.Single());
            if (image.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception(string.Join(';', image.Diagnostics));
            return image;
        }
        static IEnumerable<string> Codes(ScalarControlFlowV2LinkedProgramV1 image) => image.MethodObjects
            .OrderBy(m => m.MethodIdentity[(m.MethodIdentity.IndexOf(':', 5) + 1)..], StringComparer.Ordinal).Select(m => m.CodeSha256);
        ScalarControlFlowV2LinkedProgramV1 linked = Link(profiled); ManagedSourceBodyWorldResultV1 repeated = Compile(source);
        string factorySource = source.Replace("public static class Fixture {", "public static class Fixture { public static WideValue Create(long value) => new WideValue(value);")
            .Replace("new WideValue(input)", "Create(input)");
        ManagedSourceBodyWorldResultV1 factory = Compile(factorySource);
        ScalarControlFlowV2LinkedProgramV1 factoryImage = Link(factory); ManagedSourceBodyWorldResultV1 factoryRepeat = Compile(factorySource);
        ManagedSourceBodyWorldResultV1 scalar = Compile(factorySource.Replace("public static WideValue Create(long value) => new WideValue(value);",
            "public static long Create(long value) => value;").Replace("return value.Value;", "return value;"));
        ScalarControlFlowV2LinkedProgramV1 scalarImage = Link(scalar);
        static string MethodCode(ScalarControlFlowV2LinkedProgramV1 image, string name) => image.MethodObjects
            .Single(m => m.MethodIdentity.Contains(":Fixture." + name + "(", StringComparison.Ordinal)).CodeSha256;
        if (MethodCode(factoryImage, "Read") != MethodCode(scalarImage, "Read") ||
            !Codes(factoryImage).SequenceEqual(Codes(Link(Compile(factorySource.Replace("[HybridCpuKernelNoHeap]", ""))))) ||
            !factory.Source.Pe!.SequenceEqual(factoryRepeat.Source.Pe!) || !factory.Source.Pdb!.SequenceEqual(factoryRepeat.Source.Pdb!) ||
            !factoryImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(factoryRepeat).RestrictedImage!.PackageBytes) ||
            factory.SourceMaps.Count != 4 || factory.SourceMaps.Any(row => row.Value.Rows.All(r => r.Document is null) ||
                factoryRepeat.SourceMaps[row.Key].Digest != row.Value.Digest) ||
            factory.Compilation!.ProfileClosure?.RootProfiles.Contains(ManagedProfileClosureContractV1.KernelNoHeap) != true)
            throw new Exception("Int64 factory scalar caller/encoded/profile/repeat/maps/image oracle failed");
        // Inspect actual nominal signatures before scalar erasure, including the local TypeDef token.
        using (PEReader pe = new PEReader(new MemoryStream(factory.Source.Pe!))) {
            MetadataReader md = pe.GetMetadataReader();
            TypeDefinitionHandle wide = md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "WideValue");
            byte encoded = checked((byte)(MetadataTokens.GetRowNumber(wide) << 2));
            MethodDefinition create = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Create"));
            MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
            StandaloneSignatureHandle local = pe.GetMethodBody(read.RelativeVirtualAddress).LocalSignature;
            if (!md.GetBlobBytes(create.Signature).SequenceEqual(new byte[] { 0, 1, 0x11, encoded, 0x0a }) ||
                !md.GetBlobBytes(md.GetStandaloneSignature(local).Signature).SequenceEqual(new byte[] { 7, 1, 0x11, encoded }))
                throw new Exception("Int64 factory nominal return/local metadata changed");
            Console.WriteLine("SIGNATURE Int64 factory return=" + Convert.ToHexString(md.GetBlobBytes(create.Signature)) +
                "; local=" + Convert.ToHexString(md.GetBlobBytes(md.GetStandaloneSignature(local).Signature)) +
                "; IL=" + Convert.ToHexString(pe.GetMethodBody(create.RelativeVirtualAddress).GetILBytes()!));
        }
        static void ReturnAbi(ScalarControlFlowV2LinkedProgramV1 image, bool requireCopy) {
            byte[] bytes = image.MethodObjects.Single(m => m.MethodIdentity.Contains(":Fixture.Create(", StringComparison.Ordinal))
                .ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            byte previous = 10; int previousBundle = -1, copies = 0; bool returned = false;
            for (int offset = 0; offset < bytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                if (!bundle.TryReadBytes(bytes, offset)) throw new Exception("Invalid Int64 factory serialized bundle");
                for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                    HybridCpuInstructionWord word = bundle.GetInstruction(slot); HybridCpuOpcode opcode = (HybridCpuOpcode)word.OpCode;
                    if (opcode == HybridCpuOpcode.Nope) continue;
                    if (returned || !HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out _))
                        throw new Exception("Invalid Int64 factory physical operands");
                    if (opcode == HybridCpuOpcode.JALR && rd == HybridCpuInstructionWord.NoArchReg &&
                        rs1 == HybridCpuNativeAbiContractV2.ReturnAddressRegister && previous == 10 &&
                        word.Immediate == HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes) { returned = true; continue; }
                    if (opcode != HybridCpuOpcode.ADDI || word.Immediate != 0 || rs1 != previous || rd == 0 || previousBundle == offset)
                        throw new Exception("Int64 factory must preserve the full register through scalar copies");
                    previous = rd; previousBundle = offset; copies++;
                }
            }
            if (!returned || requireCopy && copies == 0) throw new Exception("Missing Int64 native return ABI");
        }
        ReturnAbi(factoryImage, true); ReturnAbi(scalarImage, false);
        ManagedSourceBodyWorldResultV1 nominal = Compile(factorySource.Replace("public static class Fixture {", "public static class Fixture { public static long ScalarCreate(long x) => x; public static OtherValue OtherCreate(long x) => new OtherValue(x);") +
            " public readonly struct OtherValue { public long Value { get; } public OtherValue(long x) { Value = x; } }");
        Link(nominal);
        foreach (string replacement in new[] { "ScalarCreate", "OtherCreate" }) {
            byte[] bytes = nominal.Source.Pe!.ToArray();
            using PEReader pe = new PEReader(new MemoryStream(bytes)); MetadataReader md = pe.GetMetadataReader();
            MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
            int create = MetadataTokens.GetToken(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Create"));
            int other = MetadataTokens.GetToken(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == replacement));
            byte[] il = pe.GetMethodBody(read.RelativeVirtualAddress).GetILBytes()!;
            int call = Enumerable.Range(0, il.Length - 4).Single(i => il[i] == 0x28 && BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(i + 1, 4)) == create);
            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int body = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
            int header = (bytes[body] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2)) >> 12) * 4;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(body + header + call + 1, 4), other);
            ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, bytes, "int64-nominal-negative",
                [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "int64-core")]));
            if (denied.Status != RestrictedCilImportStatusV1.Unsupported || !denied.Diagnostics.Any(d => d.Code == "HCCIL1844"))
                throw new Exception("Primitive/wrong-nominal Int64 store entered scalar projection: " + string.Join(';', denied.Diagnostics));
        }
        foreach (bool wrongNominal in new[] { false, true }) {
            byte[] bytes = nominal.Source.Pe!.ToArray();
            using PEReader pe = new PEReader(new MemoryStream(bytes)); MetadataReader md = pe.GetMetadataReader();
            MethodDefinition create = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Create"));
            TypeDefinitionHandle otherType = md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "OtherValue");
            int constructor = MetadataTokens.GetToken(md.GetTypeDefinition(otherType).GetMethods().Single(h => md.GetString(md.GetMethodDefinition(h).Name) == ".ctor"));
            byte[] il = pe.GetMethodBody(create.RelativeVirtualAddress).GetILBytes()!;
            if (il.Length != 7 || il[0] != 2 || il[1] != 0x73 || il[6] != 0x2a) throw new Exception("Factory mutation fixture changed");
            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => create.RelativeVirtualAddress >= s.VirtualAddress && create.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int body = section.PointerToRawData + create.RelativeVirtualAddress - section.VirtualAddress;
            int header = (bytes[body] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2)) >> 12) * 4;
            if (wrongNominal) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(body + header + 2, 4), constructor);
            else bytes.AsSpan(body + header + 1, 5).Clear(); // ldarg; nop*5; ret: primitive is not WideValue.
            ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, bytes, "int64-return-negative",
                [new("Fixture", "Create")], [], MetadataOnlyModules: [new(references[0], "int64-core")]));
            Console.WriteLine("RETURN-NEGATIVE Int64 wrongNominal=" + wrongNominal + "; status=" + denied.Status + "; diagnostics=" + string.Join(';', denied.Diagnostics));
            if (denied.Status != RestrictedCilImportStatusV1.InvalidInput || !denied.Diagnostics.Any(d => d.Code == "HCCIL0019"))
                throw new Exception("Primitive/wrong-nominal Int64 factory return entered scalar projection");
        }
        Assembly factoryHost = System.Reflection.Assembly.Load(factory.Source.Pe!);
        Assembly scalarHost = System.Reflection.Assembly.Load(scalar.Source.Pe!);
        foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
            if (!Equals(factoryHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), input) ||
                !Equals(scalarHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), input))
                throw new Exception("Independent Int64 CSharp13 host boundary oracle failed");
        foreach (string text in new[] {
            factorySource.Replace("var value = Create(input);", "WideValue value = default; if (input > 0) value = Create(input);"),
            factorySource.Replace("public long Value { get; }", "private readonly long _value; public long Value => _value + 1;").Replace("Value = value;", "_value = value;"),
            factorySource.Replace("public readonly struct WideValue {", "public readonly struct WideValue { static WideValue() { System.GC.Collect(); }") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(text);
            if (denied.Compilation?.Status == RestrictedCilImportStatusV1.Success ||
                (denied.Source.Diagnostics.Count == 0 && (denied.Compilation?.Diagnostics.Count ?? 0) == 0))
                throw new Exception("Int64 factory initialization/nonpure/cctor negative failed");
        }
        Console.WriteLine("IDENTITY Int64 factory artifact=" + factory.Source.ArtifactIdentity!.Digest +
            "; image=" + factoryImage.RestrictedImage!.PackageSha256 + "; read=" + MethodCode(factoryImage, "Read"));
        Console.WriteLine("PASS Int64 factory nominal return/local -> exact pure getter; independent CSharp13 scalar caller bytes, physical full-register copy/return ABI, host boundaries, profile/repeat/maps/image and initialization/effects/primitive/wrong-nominal negatives");
        string parameterSource = factorySource.Replace("public static class Fixture {",
            "public static class Fixture { public static long Consume(WideValue value) => value.Value;")
            .Replace("var value = Create(input); return value.Value;", "return Consume(Create(input));");
        foreach (string expression in new[] { "value.Value", "value.Value + value.Value", "value.Value > 0 ? value.Value : 0" }) {
            string text = parameterSource.Replace("=> value.Value;", "=> " + expression + ";");
            ManagedSourceBodyWorldResultV1 consumed = Compile(text); ScalarControlFlowV2LinkedProgramV1 image = Link(consumed); ManagedSourceBodyWorldResultV1 repeat = Compile(text);
            ManagedSourceBodyWorldResultV1 scalarParameter = Compile(text.Replace("Consume(WideValue value)", "Consume(long value)")
                .Replace("public static WideValue Create(long value) => new WideValue(value);", "public static long Create(long value) => value;")
                .Replace("value.Value", "value"));
            ScalarControlFlowV2LinkedProgramV1 scalarLinked = Link(scalarParameter);
            if (new[] { "Read", "Consume" }.Any(name => MethodCode(image, name) != MethodCode(scalarLinked, name)) ||
                !Codes(image).SequenceEqual(Codes(Link(Compile(text.Replace("[HybridCpuKernelNoHeap]", ""))))) ||
                !consumed.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !consumed.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) ||
                !image.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeat).RestrictedImage!.PackageBytes) ||
                consumed.SourceMaps.Count != 5 || consumed.SourceMaps.Any(row => row.Value.Rows.All(r => r.Document is null) || repeat.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("Int64 parameter scalar caller/helper bytes/profile/artifact/maps/image oracle failed");
            Assembly actualHost = System.Reflection.Assembly.Load(consumed.Source.Pe!);
            Assembly expectedHost = System.Reflection.Assembly.Load(scalarParameter.Source.Pe!);
            foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                if (!Equals(actualHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]),
                    expectedHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                    throw new Exception("Int64 parameter independent emitted-CIL boundary oracle failed");
            if (expression == "value.Value") {
                ReturnAbi(image, true);
                using PEReader pe = new PEReader(new MemoryStream(consumed.Source.Pe!)); MetadataReader md = pe.GetMetadataReader();
                MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Consume"));
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                if (il.Length != 8 || il[0] != 0x0f || il[1] != 0 || il[2] != 0x28 || il[7] != 0x2a)
                    throw new Exception("Int64 parameter ldarga/getter fixture changed");
                Console.WriteLine("SIGNATURE Int64 parameter=" + Convert.ToHexString(md.GetBlobBytes(method.Signature)) + "; IL=" + Convert.ToHexString(il));
                Console.WriteLine("IDENTITY Int64 parameter artifact=" + consumed.Source.ArtifactIdentity!.Digest +
                    "; image=" + image.RestrictedImage!.PackageSha256 + "; consume=" + MethodCode(image, "Consume"));
            }
        }
        ManagedSourceBodyWorldResultV1 nominalParameter = Compile(parameterSource.Replace("public static class Fixture {",
            "public static class Fixture { public static long ScalarCreate(long x) => x; public static OtherValue OtherCreate(long x) => new OtherValue(x);") +
            " public readonly struct OtherValue { public long Value { get; } public OtherValue(long x) { Value = x; } }");
        Link(nominalParameter);
        foreach (string replacement in new[] { "ScalarCreate", "OtherCreate" }) {
            byte[] bytes = nominalParameter.Source.Pe!.ToArray();
            using PEReader pe = new PEReader(new MemoryStream(bytes)); MetadataReader md = pe.GetMetadataReader();
            MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
            int create = MetadataTokens.GetToken(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Create"));
            int other = MetadataTokens.GetToken(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == replacement));
            byte[] il = pe.GetMethodBody(read.RelativeVirtualAddress).GetILBytes()!;
            int call = Enumerable.Range(0, il.Length - 4).Single(i => il[i] == 0x28 && BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(i + 1, 4)) == create);
            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int body = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
            int header = (bytes[body] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2)) >> 12) * 4;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(body + header + call + 1, 4), other);
            ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, bytes, "int64-parameter-negative",
                [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "int64-core")]));
            if (denied.Status != RestrictedCilImportStatusV1.InvalidInput || !denied.Diagnostics.Any(d => d.Code == "HCCIL0017"))
                throw new Exception("Primitive/wrong-nominal Int64 argument entered parameter projection: " + string.Join(';', denied.Diagnostics));
        }
        foreach (string text in new[] {
            parameterSource.Replace("=> value.Value;", "{ value = Create(1); return value.Value; }"),
            parameterSource.Replace("Consume(WideValue value)", "Consume(in WideValue value)"),
            parameterSource.Replace("=> value.Value;", "{ try { return value.Value; } finally { System.GC.Collect(); } }"),
            parameterSource.Replace("public long Value { get; }", "private readonly long _value; public long Value => _value + 1;").Replace("Value = value;", "_value = value;") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(text);
            if (denied.Source.Pe is null || denied.Compilation is null || denied.Compilation.Status == RestrictedCilImportStatusV1.Success || denied.Compilation.Diagnostics.Count == 0)
                throw new Exception("Int64 mutable/in/EH/nonpure parameter getter exceeded qualification");
        }
        Console.WriteLine("PASS immutable exact Int64 parameter/getter -> scalar caller/helper encoded oracle; repeated/conditional/host boundaries/maps/image and mutable/in/EH/nonpure negatives");
        ManagedSourceBodyWorldResultV1 targetTyped = Compile(source.Replace("var value = new WideValue(input)", "WideValue value = new(input)"));
        ManagedSourceBodyWorldResultV1 explicitField = Compile(source.Replace("public long Value { get; }", "private readonly long _value; public long Value => _value;")
            .Replace("Value = value;", "_value = value;"));
        if (!Codes(linked).SequenceEqual(Codes(Link(unprofiled))) ||
            !Codes(linked).SequenceEqual(Codes(Link(targetTyped))) || !Codes(linked).SequenceEqual(Codes(Link(explicitField))) ||
            !profiled.Source.Pe!.SequenceEqual(repeated.Source.Pe!) || !profiled.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!) ||
            !linked.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeated).RestrictedImage!.PackageBytes) ||
            profiled.SourceMaps.Count != 3 || profiled.SourceMaps.Any(row => repeated.SourceMaps[row.Key].Digest != row.Value.Digest))
            throw new Exception("Int64 source/receiver/encoded code/artifact/map/image oracle failed");
        Assembly host = System.Reflection.Assembly.Load(profiled.Source.Pe!);
        foreach (long input in new[] { long.MinValue, int.MinValue, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
            if (!Equals(host.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), input))
                throw new Exception("Int64 emitted-CIL boundary oracle failed");
        foreach (string text in new[] { source.Replace("Value = value;", "System.GC.Collect(); Value = value;"),
            source.Replace("WideValue(long value)", "WideValue(int value)").Replace("new WideValue(input)", "new WideValue(1)"),
            source.Replace("{ get; }", "{ get; } = 7;") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(text);
            if (denied.Source.Pe is not null || denied.Compilation is not null || !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("Int64 effect/mixed-width/initializer source gate failed");
        }
        Console.WriteLine("PASS NoHeap Int64 exact source field-copy constructor/property/target-typed -> existing CIL/receiver/image; unprofiled/explicit-field encoded oracles and host boundaries; repeat/maps/effect/mixed-width/initializer gates; policy v13");
    }
}
