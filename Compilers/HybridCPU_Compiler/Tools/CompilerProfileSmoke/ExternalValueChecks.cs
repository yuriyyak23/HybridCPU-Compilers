using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.IR.Telemetry.Contracts;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Reflection;

internal static class ExternalValueChecks
{
    public static void RunNullableEnum()
    {
        RunNullableEnum(wide: false);
        RunNullableEnum(wide: true);
        RunNullableEnum(wide: true, unsigned: true);
        RunNullableEnum(wide: false, unsigned: true);
        RunNullableEnum(wide: false, unsigned: true, narrowBits: 16);
        RunNullableEnum(wide: false, narrowBits: 16);
        RunNullableEnum(wide: false, unsigned: true, narrowBits: 8);
        RunNullableEnum(wide: false, narrowBits: 8);
    }

    private static void RunNullableEnum(bool wide, bool unsigned = false, int narrowBits = 0)
    {
        ReadOnlyMemory<byte>[] core = [File.ReadAllBytes(typeof(object).Assembly.Location)];
        string scalarType = narrowBits == 8 ? unsigned ? "byte" : "sbyte" : narrowBits == 16 ? unsigned ? "ushort" : "short" : unsigned ? wide ? "ulong" : "uint" : wide ? "long" : "int";
        string declarations = "public enum Choice:"+scalarType+" { Zero=0, One=1 } public enum Other:"+scalarType+" { Zero=0 } public static class Library { public static int Read()=>1; }";
        ManagedSourceBodyWorldResultV1 library = ManagedSourceCompanionV1.CompileBodyWorld(new("EnumLibrary", [new("/src/Library.cs", declarations)], core),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Library", "Read")], null, [0]);
        if (library.Source.Pe is null) throw new Exception("External nullable enum library emit");
        foreach (string template in new[] {
            "Choice? n=(Choice)input; Choice? copy=n; return (long)copy.GetValueOrDefault();",
            "Choice? n=unchecked((Choice)(input+1)); return (long)n.GetValueOrDefault();",
            "Choice? n=input<0?null:(Choice)input; return (long)n.GetValueOrDefault((Choice)(-7));",
            "Choice? n=null; if(input!=0)n=(Choice)input; return (long)n.GetValueOrDefault((Choice)(input+1));",
            "Choice? n=input<0?null:(Choice)input; return (long)n.Value;" }) {
            string body = unsigned ? template.Replace("input<0", "input=="+scalarType+".MaxValue").Replace("(Choice)(-7)", "unchecked((Choice)(-7))") : template;
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("ExternalNullableEnum", [new("/src/Primary.cs", "public static class Fixture { private static Other Identity(Other value)=>value; public static long Read("+scalarType+" input) { "+body+" } }")], [..core, library.Source.Pe]);
            ManagedSourceBodyWorldResultV1 Compile(int[] bodies, int[] metadata, int calls=100) => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2, budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls=calls }),
                [new("Fixture", "Read")], bodies, metadata);
            ManagedSourceBodyWorldResultV1 actual =Compile([1],[0]); ManagedSourceBodyWorldResultV1 again =Compile([1],[0]); ManagedSourceBodyWorldResultV1 metadataOnly =Compile([],[0,1]);
            if(actual.Compilation?.Status!=RestrictedCilImportStatusV1.Success || metadataOnly.Compilation?.Status!=RestrictedCilImportStatusV1.Success)
                throw new Exception("External nullable enum import: "+body+"; "+string.Join(';',actual.Compilation?.Diagnostics??[]));
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 value) => new ScalarControlFlowV2ObjectLinkerV1().Link(value.Compilation!,value.Compilation!.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 image =Link(actual); ScalarControlFlowV2LinkedProgramV1 repeat =Link(again); ScalarControlFlowV2LinkedProgramV1 declared =Link(metadataOnly);
            if(image.Status!=ScalarControlFlowV2LinkStatusV1.Success || !image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes) ||
                !image.MethodObjects.Select(m=>m.CodeSha256).SequenceEqual(declared.MethodObjects.Select(m=>m.CodeSha256)) ||
                !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) || actual.SourceMaps.Any(p=>again.SourceMaps[p.Key].Digest!=p.Value.Digest))
                throw new Exception("External nullable enum repeat/roles/image");
            ManagedSourceBodyWorldResultV1 single =ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalNullableEnum", [new("/src/Primary.cs",request.Documents[0].Text+declarations)],core),
                new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],null,[0]);
            if(single.Compilation?.Status!=RestrictedCilImportStatusV1.Success || !image.MethodObjects.Select(m=>m.CodeSha256).SequenceEqual(Link(single).MethodObjects.Select(m=>m.CodeSha256)))
                throw new Exception("External nullable enum single-module bytes");
            AssemblyLoadContext context =new System.Runtime.Loader.AssemblyLoadContext("external-nullable-enum",isCollectible:true);
            try {
                context.LoadFromStream(new MemoryStream(library.Source.Pe));
                MethodInfo host =context.LoadFromStream(new MemoryStream(actual.Source.Pe!)).GetType("Fixture")!.GetMethod("Read")!;
                IEnumerable<object> inputs = narrowBits == 8 ? unsigned ? new byte[]{0,1,127,128,byte.MaxValue}.Cast<object>() :
                    new sbyte[]{sbyte.MinValue,-127,-1,0,1,126,sbyte.MaxValue}.Cast<object>() : narrowBits == 16 ? unsigned ? new ushort[]{0,1,32767,32768,ushort.MaxValue}.Cast<object>() :
                    new short[]{short.MinValue,-32767,-1,0,1,32766,short.MaxValue}.Cast<object>() : unsigned ? wide ? new ulong[]{0,1,uint.MaxValue,1UL<<32,(1UL<<63)-1,1UL<<63,ulong.MaxValue}.Cast<object>() :
                    new uint[]{0,1,int.MaxValue,1U<<31,uint.MaxValue}.Cast<object>() :
                    (wide?new[]{long.MinValue,(long)int.MinValue,-1,0,1,(long)int.MaxValue,long.MaxValue}:new long[]{int.MinValue,-1,0,1,int.MaxValue}).Select(input=>wide?(object)input:(int)input);
                foreach(object argument in inputs) {
                    long input = argument is ulong bits ? unchecked((long)bits) : Convert.ToInt64(argument);
                    bool empty = narrowBits == 8 && unsigned ? (byte)argument==byte.MaxValue : narrowBits == 16 && unsigned ? (ushort)argument==ushort.MaxValue : unsigned ? wide ? (ulong)argument==ulong.MaxValue : (uint)argument==uint.MaxValue : input<0;
                    bool increment = body.StartsWith("Choice? n=unchecked", StringComparison.Ordinal);
                    object? outcome;try{outcome=host.Invoke(null,[argument]);}catch(System.Reflection.TargetInvocationException fault){outcome=fault.InnerException!.GetType();}
                    object expected=body.Contains("n.Value")&&empty?typeof(InvalidOperationException):increment?unchecked(input+1):
                        body.Contains("(-7)")&&empty?unsigned&&!wide?(long)unchecked((uint)-7):-7L:body.Contains("if(input!=0)")&&input==0?1L:input;
                    if(!wide&&increment)expected=unsigned?(long)unchecked((uint)input+1U):(long)unchecked((int)input+1);
                    if(narrowBits==16 && increment)expected=unsigned?(long)unchecked((ushort)(input+1)):(long)unchecked((short)(input+1));
                    if(narrowBits==16 && unsigned && body.Contains("(-7)") && empty)expected=(long)unchecked((ushort)-7);
                    if(narrowBits==8 && increment)expected=unsigned?(long)unchecked((byte)(input+1)):(long)unchecked((sbyte)(input+1));
                    if(narrowBits==8 && unsigned && body.Contains("(-7)") && empty)expected=(long)unchecked((byte)-7);
                    if(!Equals(outcome,expected))throw new Exception("External nullable enum scalar host oracle");
                }
            }finally{context.Unload();}
            ManagedSourceBodyWorldResultV1 omitted =Compile([],[0]);
            if(omitted.Compilation?.Status==RestrictedCilImportStatusV1.Success || (omitted.Compilation?.Diagnostics.Count??0)==0)throw new Exception("External enum omitted declaration authority");
            ManagedSourceCompanionResultV1 standalone =ManagedSourceCompanionV1.Compile(request,new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),new("Fixture","Read"));
            if(standalone.Imported?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Standalone gained enum declaration authority");
            if(body.Contains("copy=n"))foreach(int calls in new[]{0,1,2}) {
                ManagedSourceBodyWorldResultV1 budgeted =Compile([1],[0],calls);
                if(budgeted.Compilation?.Status!=(calls==2?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("External enum original calls");
            }
            if(body.Contains("copy=n")) {
                byte[] malformed=library.Source.Pe.ToArray();
                using(PEReader pe =new PEReader(new MemoryStream(malformed))) {
                    MetadataReader md =pe.GetMetadataReader();
                    TypeDefinitionHandle handle =md.TypeDefinitions.Single(h=>md.GetString(md.GetTypeDefinition(h).Name)=="Choice");
                    int offset=pe.PEHeaders.MetadataStartOffset+md.GetTableMetadataOffset(TableIndex.TypeDef)+
                        (MetadataTokens.GetRowNumber(handle)-1)*md.GetTableRowSize(TableIndex.TypeDef);
                    uint flags=BinaryPrimitives.ReadUInt32LittleEndian(malformed.AsSpan(offset,4));
                    BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(offset,4),flags&~(uint)System.Reflection.TypeAttributes.Sealed);
                }
                ManagedCallGraphCompilationV1 invalid =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule,actual.Source.Pe!,"external-enum-invalid",[new("Fixture","Read")],[],
                    MetadataOnlyModules:[new(core[0],"core"),new(malformed,"enum")]));
                if(invalid.Status==RestrictedCilImportStatusV1.Success||invalid.Diagnostics.Count==0)throw new Exception("External enum exact declaration gate");
                byte[] wrongNominal=actual.Source.Pe!.ToArray();
                using(PEReader pe =new PEReader(new MemoryStream(wrongNominal))) {
                    MetadataReader md =pe.GetMetadataReader();
                    MemberReference getter =md.GetMemberReference(md.MemberReferences.Single(h=>md.GetString(md.GetMemberReference(h).Name)=="GetValueOrDefault"));
                    int other=MetadataTokens.GetRowNumber(md.TypeReferences.Single(h=>md.GetString(md.GetTypeReference(h).Name)=="Other"));
                    byte[] signature=md.GetBlobBytes(getter.Signature);
                    if(signature.Length!=4||signature[2]!=0x13||signature[3]!=0)throw new Exception("External enum nominal mutation fixture changed");
                    int offset=pe.PEHeaders.MetadataStartOffset+md.GetHeapMetadataOffset(HeapIndex.Blob)+MetadataTokens.GetHeapOffset(getter.Signature)+1;
                    wrongNominal[offset+2]=0x11;
                    wrongNominal[offset+3]=checked((byte)((other<<2)|1));
                }
                ManagedCallGraphCompilationV1 nominalDenied =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule,wrongNominal,"external-enum-wrong-nominal",[new("Fixture","Read")],[],
                    MetadataOnlyModules:[new(core[0],"core"),new(library.Source.Pe,"enum")]));
                if(nominalDenied.Status==RestrictedCilImportStatusV1.Success||nominalDenied.Diagnostics.Count==0)throw new Exception("External enum nominal getter gate");
                CompanionSourceRequestV1 bodyRequest =request with { Documents=[new("/src/Primary.cs","public static class Fixture { public static long Read(int input)=>Library.Read(); }")] };
                ManagedSourceBodyWorldResultV1 deniedBody =ManagedSourceCompanionV1.CompileBodyWorld(bodyRequest,new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0,1]);
                if(deniedBody.Compilation?.Status==RestrictedCilImportStatusV1.Success||(deniedBody.Compilation?.Diagnostics.Count??0)==0)throw new Exception("Enum declaration granted body authority");
            }
        }
        Console.WriteLine("PASS external nullable enum "+(narrowBits==8?unsigned?"UInt8":"Int8":narrowBits==16?unsigned?"UInt16":"Int16":unsigned?wide?"UInt64":"UInt32":wide?"Int64":"Int32")+" exact declarations/body-metadata parity/standalone-omitted budgets/scalar host/single-module/repeat/maps/image");
    }

    public static void Run()
    {
        RunNullableEnum();
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(directory, "System.Runtime.dll")), File.ReadAllBytes(Path.Combine(directory, "netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string library = """
            public readonly struct ScalarValue {
                public int Value { get; }
                public ScalarValue(int value) { Value = value; }
            }
            public static class LibraryFactory {
                public static ScalarValue Create(int value) => new ScalarValue(value);
            }
            """;
        ManagedSourceBodyWorldResultV1 libraryResult = ManagedSourceCompanionV1.CompileBodyWorld(new("ValueLibrary", [new("/src/Library.cs", library)], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("LibraryFactory", "Create")], null, [0]);
        if (libraryResult.Source.Pe is null || libraryResult.Compilation?.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Value library source failed: " + string.Join(';', libraryResult.Source.Diagnostics) + "; " + string.Join(';', libraryResult.Compilation?.Diagnostics ?? []));
        const string primary = """
            using HybridCPU.Compiler.Profiles;
            public static class Fixture {
                [HybridCpuKernelNoHeap] public static int Read(int input) {
                    var value = LibraryFactory.Create(input); return value.Value;
                }
            }
            """;
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("ExternalValue", [new("/src/Primary.cs", primary)], [..references, libraryResult.Source.Pe]);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 input, int[]? bodies = null, int[]? metadata = null) =>
            ManagedSourceCompanionV1.CompileBodyWorld(input, importer, [new("Fixture", "Read")], bodies ?? [4], metadata ?? [0]);
        ManagedSourceBodyWorldResultV1 result = Compile(request); ManagedSourceBodyWorldResultV1 repeated = Compile(request);
        CompanionSourceRequestV1 constructionRequest = request with { Documents = [new("/src/Primary.cs",
            primary.Replace("LibraryFactory.Create(input)", "new ScalarValue(input)"))] };
        ManagedSourceBodyWorldResultV1 construction = Compile(constructionRequest);
        ManagedSourceCompanionResultV1 singleMethod = ManagedSourceCompanionV1.Compile(constructionRequest, importer, new("Fixture", "Read"));
        ManagedSourceCompanionResultV1 singleMethodUnprofiled = ManagedSourceCompanionV1.Compile(constructionRequest with { Documents = [new("/src/Primary.cs",
            constructionRequest.Documents[0].Text.Replace("[HybridCpuKernelNoHeap]", ""))] }, importer, new("Fixture", "Read"));
        if (singleMethod.Pe is not null || !singleMethod.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error") ||
            singleMethodUnprofiled.Imported?.Status != RestrictedCilImportStatusV1.Unsupported)
            throw new Exception("Single-method constructor body authority contract changed");
        ManagedSourceBodyWorldResultV1 constructionUnprofiled = Compile(constructionRequest with { Documents = [new("/src/Primary.cs",
            constructionRequest.Documents[0].Text.Replace("[HybridCpuKernelNoHeap]", ""))] });
        if (construction.Source.Pe is null || construction.Compilation?.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Owned external source constructor admission failed: " + string.Join(';', construction.Source.Diagnostics));
        CompanionSourceRequestV1 parameterRequest = request with { Documents = [new("/src/Primary.cs",
            primary.Replace("public static class Fixture {", "public static class Fixture { private static int Consume(ScalarValue value) => value.Value;")
                .Replace("return value.Value;", "return Consume(value);"))] };
        ManagedSourceBodyWorldResultV1 parameter = Compile(parameterRequest);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 value) {
            if (value.Compilation?.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception("External value graph failed: " + string.Join(';', value.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 image = new ScalarControlFlowV2ObjectLinkerV1().Link(value.Compilation, value.Compilation.Graph!.RootIdentities.Single());
            if (image.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception(string.Join(';', image.Diagnostics));
            return image;
        }
        ScalarControlFlowV2LinkedProgramV1 linked = Link(result);
        ScalarControlFlowV2LinkedProgramV1 constructionImage = Link(constructionUnprofiled);
        string fixture = Path.Combine(AppContext.BaseDirectory, "CliExternalConstructionFixture.txt");
        string cliRoot = Path.Combine(Path.GetTempPath(), "external-constructor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cliRoot);
        string libraryPath = Path.Combine(cliRoot, "ValueLibrary.dll");
        File.WriteAllBytes(libraryPath, libraryResult.Source.Pe);
        string[] referencePaths = [typeof(object).Assembly.Location, Path.Combine(directory, "System.Runtime.dll"),
            Path.Combine(directory, "netstandard.dll"), typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location];
        CompanionSourceRequestV1 cliRequest = constructionRequest with { Documents = [new(Path.GetFullPath(fixture).Replace('\\', '/'), File.ReadAllText(fixture))] };
        ManagedSourceBodyWorldResultV1 cliApi = Compile(cliRequest); ScalarControlFlowV2LinkedProgramV1 cliImage = Link(cliApi);
        string imagePath = Path.Combine(cliRoot, "source.hcexe"), pePath = Path.Combine(cliRoot, "source.dll"), pdbPath = Path.Combine(cliRoot, "source.pdb");
        using StringWriter output = new StringWriter(); using StringWriter error = new StringWriter();
        int cliExit = ManagedSourceCommandLineV1.Run(["compile-source-image", "--source", fixture,
            "--metadata-reference", referencePaths[0], ..referencePaths.Skip(1).SelectMany(path => new[] { "--reference", path }),
            "--body-reference", libraryPath, "--assembly-name", cliRequest.AssemblyName, "--type", "Fixture", "--method", "Read",
            "--out", imagePath, "--emit-pe", pePath, "--emit-pdb", pdbPath], output, error);
        if (cliExit != 0 || !File.ReadAllBytes(imagePath).SequenceEqual(cliImage.RestrictedImage!.PackageBytes) ||
            !File.ReadAllBytes(pePath).SequenceEqual(cliApi.Source.Pe!) || !File.ReadAllBytes(pdbPath).SequenceEqual(cliApi.Source.Pdb!))
            throw new Exception("External constructor CLI/API exact artifact oracle failed: " + error);
        string deniedPath = Path.Combine(cliRoot, "denied.hcexe");
        int deniedExit = ManagedSourceCommandLineV1.Run(["compile-source-image", "--source", fixture,
            "--metadata-reference", referencePaths[0], ..referencePaths.Skip(1).SelectMany(path => new[] { "--reference", path }),
            "--reference", libraryPath, "--assembly-name", cliRequest.AssemblyName, "--type", "Fixture", "--method", "Read",
            "--out", deniedPath], output, error);
        if (deniedExit != 3 || File.Exists(deniedPath) || !error.ToString().Contains("HC0006", StringComparison.Ordinal))
            throw new Exception("CLI omitted body role bypassed source constructor admission");
        ManagedSourceBodyWorldResultV1 oracle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalValue", [new("/src/Primary.cs", primary + library)], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        static string Name(string id) => id[(id.IndexOf(':', id.IndexOf(':') + 1) + 1)..];
        static IEnumerable<(string, string)> Codes(ScalarControlFlowV2LinkedProgramV1 image) =>
            image.MethodObjects.Select(m => (Name(m.MethodIdentity).Replace("[ValueLibrary]", "[ExternalValue]"), m.CodeSha256))
                .OrderBy(m => m.Item1, StringComparer.Ordinal);
        CompanionSourceRequestV1 unprofiledRequest = constructionRequest with { Documents = [new("/src/Primary.cs",
            constructionRequest.Documents[0].Text.Replace("[HybridCpuKernelNoHeap]", ""))] };
        ManagedSourceBodyWorldResultV1 constructionRepeat = Compile(unprofiledRequest);
        ManagedSourceBodyWorldResultV1 constructionOracle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalValue",
            [new("/src/Primary.cs", unprofiledRequest.Documents[0].Text + library)], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        if (!Codes(constructionImage).SequenceEqual(Codes(Link(constructionOracle))) ||
            !Codes(constructionImage).SequenceEqual(Codes(Link(construction))) ||
            !constructionUnprofiled.Source.Pe!.SequenceEqual(constructionRepeat.Source.Pe!) ||
            !constructionUnprofiled.Source.Pdb!.SequenceEqual(constructionRepeat.Source.Pdb!) ||
            !constructionImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(constructionRepeat).RestrictedImage!.PackageBytes) ||
            constructionUnprofiled.SourceMaps.Count != 1)
            throw new Exception("External receiver storage single-module encoded/repeat/map oracle failed");
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(unprofiledRequest, []), Compile(unprofiledRequest, [], [0, 4]) })
            if (denied.Compilation?.Status == RestrictedCilImportStatusV1.Success)
                throw new Exception("External receiver acquired omitted/metadata-only constructor authority");
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(constructionRequest, []), Compile(constructionRequest, [], [0, 4]) })
            if (denied.Source.Pe is not null || !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("Unselected constructor obtained source admission");
        foreach (string expression in new[] { "value.Value", "value.Value + value.Value", "value.Value > 0 ? value.Value : 0" }) {
            CompanionSourceRequestV1 input = parameterRequest with { Documents = [new("/src/Primary.cs",
                parameterRequest.Documents[0].Text.Replace("=> value.Value;", "=> " + expression + ";"))] };
            ManagedSourceBodyWorldResultV1 consumed = Compile(input); ManagedSourceBodyWorldResultV1 consumedRepeat = Compile(input); ScalarControlFlowV2LinkedProgramV1 consumedImage = Link(consumed);
            ManagedSourceBodyWorldResultV1 single = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalValue",
                [new("/src/Primary.cs", input.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            if (!Codes(consumedImage).SequenceEqual(Codes(Link(single))) ||
                !consumed.Source.Pe!.SequenceEqual(consumedRepeat.Source.Pe!) || !consumed.Source.Pdb!.SequenceEqual(consumedRepeat.Source.Pdb!) ||
                !consumedImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(consumedRepeat).RestrictedImage!.PackageBytes) ||
                consumed.SourceMaps.Count != 2 || consumed.SourceMaps.Any(row => consumedRepeat.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("External parameter single-module encoded/map/image oracle failed");
        }
        foreach (int limit in new[] { 0, 1, 2, 3 }) {
            ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(parameterRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with { MaximumMetadataResolutionSteps = limit }),
                [new("Fixture", "Consume")], [4], [0]);
            bool accepted = limit == 0
                ? budgeted.Compilation?.Status == RestrictedCilImportStatusV1.InvalidInput &&
                    budgeted.Compilation.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD0004")
                : limit < 3 ? budgeted.Compilation?.Status == RestrictedCilImportStatusV1.BudgetExhausted &&
                    budgeted.Compilation.Diagnostics.Any(d => d.Code == "HCSCF-BUDGET2002")
                : budgeted.Compilation?.Status == RestrictedCilImportStatusV1.Success;
            if (!accepted)
                throw new Exception("External getter proof resolution budget boundary failed: " + limit);
        }
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(parameterRequest, []), Compile(parameterRequest, [], [0, 4]),
            Compile(parameterRequest with { Documents = [new("/src/Primary.cs",
                parameterRequest.Documents[0].Text.Replace("=> value.Value;", "{ value = LibraryFactory.Create(1); return value.Value; }"))] }) })
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success)
                throw new Exception("External parameter omitted/metadata/mutation control failed");
        if (!Codes(linked).SequenceEqual(Codes(Link(oracle))) ||
            !result.Source.Pe!.SequenceEqual(repeated.Source.Pe!) || !result.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!) ||
            !linked.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeated).RestrictedImage!.PackageBytes) ||
            result.SourceMaps.Count != 1 || result.SourceMaps.Any(row => repeated.SourceMaps[row.Key].Digest != row.Value.Digest))
            throw new Exception("External value single-module code/artifact/map/image oracle failed");
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(request, []), Compile(request, [], [0, 4]) })
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success)
                throw new Exception("Unselected/metadata-only reference gained value body authority");
        byte[] Changed(string text) {
            ManagedSourceBodyWorldResultV1 emitted = ManagedSourceCompanionV1.CompileBodyWorld(new("ValueLibrary", [new("/src/Library.cs", text)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("LibraryFactory", "Create")], null, [0]);
            return emitted.Source.Pe ?? throw new Exception("Changed library source failed");
        }
        foreach (string text in new[] { library.Replace("Value = value;", "Value = value + 1;"),
            library.Replace("public int Value { get; }", "private readonly int _value; public int Value => _value + 1;").Replace("Value = value;", "_value = value;") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(request with { ReferenceImages = [..references, Changed(text)] });
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success)
                throw new Exception("Unqualified external constructor/getter entered scalar projection");
        }
        foreach (string text in new[] { library.Replace("Value = value;", "Value = value + 1;"),
            library.Replace("Value = value;", "System.GC.Collect(); Value = value;"),
            library.Replace("public readonly struct ScalarValue {", "public readonly struct ScalarValue { static ScalarValue() {}") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(constructionRequest with { ReferenceImages = [..references, Changed(text)] });
            if (denied.Source.Pe is not null || denied.Source.ArtifactIdentity is not null || denied.Compilation is not null ||
                !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("Unqualified owned metadata constructor passed pre-emit admission");
        }
        ManagedSourceBodyWorldResultV1 changed = Compile(request with { ReferenceImages = [..references, Changed(library + " public static class Unused { public static int Read() => 9; }")] });
        if (changed.Source.SourceInputDigest == result.Source.SourceInputDigest ||
            changed.Compilation?.Status != RestrictedCilImportStatusV1.Success ||
            changed.Compilation.Methods.Single(m => m.Identity.MethodName == "Read").Import.Provenance!.OptionsDigest ==
                result.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import.Provenance!.OptionsDigest ||
            !Codes(Link(changed)).SequenceEqual(Codes(linked)))
            throw new Exception("External definition image/options identity or code control failed");
        Console.WriteLine("PASS explicit CSharp value library -> TypeRef/MemberRef scalar carrier/getter/shared image; single-module code and repeat oracle; omitted/metadata/effect/definition identity controls; primary PDB filtering");
        Console.WriteLine("PASS primary helper external I4 parameter/getter raw graph proof -> caller/helper encoded single-module oracle; repeated/conditional/maps/image; resolution budgets0/1/2/3 and authority/mutation negatives");
        Console.WriteLine("PASS owned exact external constructor NoHeap pre-emit admission v11 -> receiver/shared image; single-module/unprofiled code oracle, omitted/metadata/effect/cctor source negatives");
        Console.WriteLine("PASS external constructor CLI/API exact PE/PDB/image oracle; omitted body role source exit3/no output and single-method no-authority controls");
        Console.WriteLine($"IDENTITY external-value artifact={result.Source.ArtifactIdentity!.Digest}; graph={result.Compilation!.Graph!.GraphDigest}; image={linked.RestrictedImage.PackageSha256}; options={result.Compilation.Methods.Single(m => m.Identity.MethodName == "Read").Import.Provenance!.OptionsDigest}");
        CheckLsuProfileFallback();
        RunInt64(references);
    }

    private static void CheckLsuProfileFallback()
    {
        HybridCpuInstructionWord[] words = [new() { OpCode = (uint)HybridCpuOpcode.LD, DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue, Word1 = HybridCpuInstructionWord.PackArchRegs(10, 2, HybridCpuInstructionWord.NoArchReg) }];
        HybridCpuCompiledProgram baseline = HybridCPU.Compiler.Core.HybridCpuCanonicalCompiler.CompileProgram(0, words);
        HybridCpuProfileCompilationResultV1 off = HybridCPU.Compiler.Core.HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new(false, "corrupt"));
        TypedSlotTelemetryProfile profile = new TypedSlotTelemetryProfile(off.ProfileInputIdentity + "-stale", new Dictionary<IrSlotClass, long>(),
            new Dictionary<IrSlotClass, long>(), new Dictionary<IrTypedSlotRejectReason, long>(), 0, 0, 0, 0, 0, 0, 0, 0,
            new Dictionary<int, long>(), null);
        string stale = System.Text.Json.JsonSerializer.Serialize(new { Schema = TelemetryProfileReader.ProfileSchemaV1, Profile = profile });
        foreach (string json in new[] { "corrupt", stale, stale.Replace(TelemetryProfileReader.ProfileSchemaV1, "wrong-version") }) {
            HybridCpuProfileCompilationResultV1 rejected = HybridCPU.Compiler.Core.HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new(true, json));
            if (rejected.ActiveProfileDigest is not null || rejected.ProfileStatus != "profile-disabled-invalid-or-stale" ||
                rejected.EffectiveOptionsDigest != off.EffectiveOptionsDigest ||
                !rejected.CompiledProgram.ProgramImage.SequenceEqual(baseline.ProgramImage) ||
                HybridCpuAgreementSummaryV1.From(rejected.CompiledProgram.AdmissibilityAgreement).Digest !=
                    HybridCpuAgreementSummaryV1.From(baseline.AdmissibilityAgreement).Digest)
                throw new Exception("Scalar LSU stale/corrupt/version profile fallback changed bytes/agreement/options");
        }
        if (!off.CompiledProgram.ProgramImage.SequenceEqual(baseline.ProgramImage)) throw new Exception("Scalar LSU profile-off changed bytes");
        Console.WriteLine("PASS scalar LSU profile-off and stale/corrupt/version fallback exact image/agreement/effective-options equality");
    }

    public static void RunNullableRetained()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        RunInt64([File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),
            File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)],nullableParameterOnly:true);
    }

    private static void RunInt64(ReadOnlyMemory<byte>[] references, bool nullableParameterOnly = false)
    {
        const string library = """
            public readonly struct WideValue {
                public long Value { get; }
                public WideValue(long value) { Value = value; }
            }
            public static class WideFactory {
                public static WideValue Create(long value) => new WideValue(value);
            }
            """;
        const string primary = """
            using HybridCPU.Compiler.Profiles;
            public static class Fixture {
                [HybridCpuKernelNoHeap] public static long Read(long input) {
                    var value = WideFactory.Create(input); return value.Value;
                }
            }
            """;
        ManagedSourceBodyWorldResultV1 Library(string text) => ManagedSourceCompanionV1.CompileBodyWorld(
            new("WideLibrary", [new("/src/WideLibrary.cs", text)], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("WideFactory", "Create")], null, [0]);
        ManagedSourceBodyWorldResultV1 definition = Library(library);
        if (definition.Source.Pe is null || definition.Compilation?.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Int64 source-owned library failed");
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("ExternalWide", [new("/src/Primary.cs", primary)], [..references, definition.Source.Pe]);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 input, int[]? bodies = null, int[]? metadata = null) =>
            ManagedSourceCompanionV1.CompileBodyWorld(input, importer, [new("Fixture", "Read")], bodies ?? [4], metadata ?? [0]);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 value) {
            if (value.Compilation?.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception("External Int64 graph failed: emitted=" + (value.Source.Pe is not null) + "; " + string.Join(';', value.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 image = new ScalarControlFlowV2ObjectLinkerV1().Link(value.Compilation, value.Compilation.Graph!.RootIdentities.Single());
            if (image.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception(string.Join(';', image.Diagnostics));
            return image;
        }
        static string Name(string id) => id[(id.IndexOf(':', id.IndexOf(':') + 1) + 1)..];
        static IEnumerable<(string, string)> Codes(ScalarControlFlowV2LinkedProgramV1 image) => image.MethodObjects
            .Select(m => (Name(m.MethodIdentity).Replace("[WideLibrary]", "[ExternalWide]"), m.CodeSha256)).OrderBy(m => m.Item1, StringComparer.Ordinal);
        ManagedSourceBodyWorldResultV1 result = Compile(request); ScalarControlFlowV2LinkedProgramV1 image = Link(result); ManagedSourceBodyWorldResultV1 repeat = Compile(request);
        ManagedSourceBodyWorldResultV1 single = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", primary + library)], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        ManagedSourceBodyWorldResultV1 scalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs",
            primary.Replace("return value.Value;", "return value;") + library.Replace("public static WideValue Create(long value) => new WideValue(value);", "public static long Create(long value) => value;"))], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        ScalarControlFlowV2LinkedProgramV1 scalarImage = Link(scalar);
        if (!Codes(image).SequenceEqual(Codes(Link(single))) ||
            image.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 !=
                scalarImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
            !result.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !result.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) ||
            !image.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeat).RestrictedImage!.PackageBytes) ||
            result.SourceMaps.Count != 1 || result.SourceMaps.Any(row => row.Value.Rows.All(r => r.Document is null) || repeat.SourceMaps[row.Key].Digest != row.Value.Digest))
            throw new Exception("External Int64 single-module/scalar caller/artifact/maps/image oracle failed");
        Assembly host = System.Reflection.Assembly.Load(single.Source.Pe!); Assembly scalarHost = System.Reflection.Assembly.Load(scalar.Source.Pe!);
        AssemblyLoadContext context = new System.Runtime.Loader.AssemblyLoadContext("external-wide-host", isCollectible: true);
        try {
            context.LoadFromStream(new MemoryStream(definition.Source.Pe!));
            Assembly externalHost = context.LoadFromStream(new MemoryStream(result.Source.Pe!));
            foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                if (!Equals(host.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), input) ||
                    !Equals(externalHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), input) ||
                    !Equals(scalarHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), input))
                    throw new Exception("External Int64 independent host boundary oracle failed");
        } finally { context.Unload(); }
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(request, []), Compile(request, [], [0, 4]) })
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("External Int64 gained omitted/metadata-only body authority");
        CompanionSourceRequestV1 constructorRequest = request with { Documents = [new("/src/Primary.cs", primary.Replace("WideFactory.Create(input)", "new WideValue(input)"))] };
        if(nullableParameterOnly){CheckNullableParameterConsumers();return;}
        ManagedSourceBodyWorldResultV1 construction = Compile(constructorRequest); ScalarControlFlowV2LinkedProgramV1 constructionImage = Link(construction);
        ManagedSourceBodyWorldResultV1 constructionRepeat = Compile(constructorRequest);
        ManagedSourceBodyWorldResultV1 constructionUnprofiled = Compile(constructorRequest with { Documents = [new("/src/Primary.cs",
            constructorRequest.Documents[0].Text.Replace("[HybridCpuKernelNoHeap]", ""))] });
        ManagedSourceBodyWorldResultV1 constructionSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide",
            [new("/src/Primary.cs", constructorRequest.Documents[0].Text + library)], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        ManagedSourceBodyWorldResultV1 targetTyped = Compile(constructorRequest with { Documents = [new("/src/Primary.cs",
            constructorRequest.Documents[0].Text.Replace("var value = new WideValue(input)", "WideValue value = new(input)"))] });
        if (!Codes(constructionImage).SequenceEqual(Codes(Link(constructionSingle))) ||
            !Codes(constructionImage).SequenceEqual(Codes(Link(constructionUnprofiled))) ||
            !Codes(constructionImage).SequenceEqual(Codes(Link(targetTyped))) ||
            !construction.Source.Pe!.SequenceEqual(constructionRepeat.Source.Pe!) || !construction.Source.Pdb!.SequenceEqual(constructionRepeat.Source.Pdb!) ||
            !constructionImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(constructionRepeat).RestrictedImage!.PackageBytes) ||
            construction.SourceMaps.Count != 1 || construction.SourceMaps.Any(row => row.Value.Rows.All(r => r.Document is null) ||
                constructionRepeat.SourceMaps[row.Key].Digest != row.Value.Digest) ||
            construction.Compilation!.ProfileClosure?.RootProfiles.Contains(ManagedProfileClosureContractV1.KernelNoHeap) != true)
            throw new Exception("External Int64 constructor single-module/unprofiled/target-typed/PE/PDB/maps/image oracle failed");
        AssemblyLoadContext constructorContext = new System.Runtime.Loader.AssemblyLoadContext("external-wide-constructor-host", isCollectible: true);
        try {
            constructorContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
            Assembly actual = constructorContext.LoadFromStream(new MemoryStream(construction.Source.Pe!));
            foreach (long value in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                if (!Equals(actual.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [value]),
                    scalarHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [value])))
                    throw new Exception("External Int64 constructor independent scalar host boundary oracle failed");
        } finally { constructorContext.Unload(); }
        ManagedReceiverCallerStoragePlanV1? storage = construction.Compilation.Methods.Single(m => m.Identity.MethodName == "Read").Import.ReceiverCallerStoragePlans?.Single();
        if (storage is not { PayloadSizeBytes: 8, PayloadAlignmentBytes: 8 } || storage.Calls.Count != 2)
            throw new Exception("External Int64 constructor exact receiver storage/calls lost");
        foreach ((string? name, HybridCpuOpcode opcode) in new[] { (".ctor", HybridCpuOpcode.SD), ("get_Value", HybridCpuOpcode.LD) }) {
            ManagedCompiledMethodV1 method = construction.Compilation.Methods.Single(m => m.Identity.MethodName == name);
            if (method.Import.ReceiverAbi is not { PayloadSizeBytes: 8, PayloadAlignmentBytes: 8, NonEscaping: true, NoSafepoints: true })
                throw new Exception("External Int64 receiver ABI changed");
            byte[] bytes = constructionImage.MethodObjects.Single(m => m.MethodIdentity == method.Identity.StableIdentity)
                .ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            int memoryWords = 0;
            for (int offset = 0; offset < bytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                if (!bundle.TryReadBytes(bytes, offset)) throw new Exception("Invalid external Int64 receiver bundle");
                for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                    HybridCpuInstructionWord word = bundle.GetInstruction(slot); HybridCpuOpcode op = (HybridCpuOpcode)word.OpCode;
                    if (op is not (HybridCpuOpcode.SD or HybridCpuOpcode.LD or HybridCpuOpcode.SW or HybridCpuOpcode.LW)) continue;
                    bool packed = HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out byte rs2);
                    if (op != opcode || slot is not (4 or 5) || word.Immediate != 0 ||
                        !packed ||
                        rs1 is 0 or >= 32 || (op == HybridCpuOpcode.LD ? rd is 0 or >= 32 : rs2 is 0 or >= 32))
                        throw new Exception($"External Int64 receiver encoded width/physical operands/LSU lane changed: {name} {op} slot={slot} imm={word.Immediate} regs={rd}/{rs1}/{rs2} packed={packed}");
                    Console.WriteLine($"ENCODED external-Int64-receiver method={name}; op={op}; slot={slot}; regs={rd}/{rs1}/{rs2}");
                    memoryWords++;
                }
            }
            if (memoryWords != 1) throw new Exception("External Int64 receiver requires one exact 8-byte memory access");
        }
        ManagedSourceBodyWorldResultV1 explicitDefinition = Library(library.Replace("public long Value { get; }", "private readonly long _value; public long Value => _value;")
            .Replace("Value = value;", "_value = value;"));
        if (!Codes(constructionImage).SequenceEqual(Codes(Link(Compile(constructorRequest with { ReferenceImages = [..references, explicitDefinition.Source.Pe!] })))))
            throw new Exception("External Int64 constructor explicit-field encoded oracle failed");

        string cliRoot = Path.Combine(Path.GetTempPath(), "external-wide-constructor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cliRoot);
        string fixturePath = Path.Combine(cliRoot, "Primary.cs"), libraryPath = Path.Combine(cliRoot, "WideLibrary.dll");
        File.WriteAllText(fixturePath, constructorRequest.Documents[0].Text); File.WriteAllBytes(libraryPath, definition.Source.Pe!);
        ManagedSourceBodyWorldResultV1 cliApi = Compile(constructorRequest with { Documents = [new(fixturePath.Replace('\\', '/'), constructorRequest.Documents[0].Text)] });
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string[] referencePaths = [typeof(object).Assembly.Location, Path.Combine(runtimeDirectory, "System.Runtime.dll"),
            Path.Combine(runtimeDirectory, "netstandard.dll"), typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location];
        string imagePath = Path.Combine(cliRoot, "app.hcexe"), pePath = Path.Combine(cliRoot, "app.dll"),
            pdbPath = Path.Combine(cliRoot, "app.pdb"), mapPath = Path.Combine(cliRoot, "app.map.json");
        using StringWriter cliOutput = new StringWriter(); using StringWriter cliError = new StringWriter();
        string[] cliArgs = ["compile-source-image", "--source", fixturePath, "--assembly-name", "ExternalWide", "--type", "Fixture", "--method", "Read",
            "--metadata-reference", referencePaths[0], ..referencePaths.Skip(1).SelectMany(path => new[] { "--reference", path }),
            "--body-reference", libraryPath, "--out", imagePath, "--emit-pe", pePath, "--emit-pdb", pdbPath, "--emit-map", mapPath];
        if (ManagedSourceCommandLineV1.Run(cliArgs, cliOutput, cliError) != 0 ||
            !File.ReadAllBytes(imagePath).SequenceEqual(Link(cliApi).RestrictedImage!.PackageBytes) ||
            !File.ReadAllBytes(pePath).SequenceEqual(cliApi.Source.Pe!) || !File.ReadAllBytes(pdbPath).SequenceEqual(cliApi.Source.Pdb!))
            throw new Exception("External Int64 constructor CLI/API artifact oracle failed: " + cliError);
        byte[] cliMap = File.ReadAllBytes(mapPath);
        if (ManagedSourceCommandLineV1.Run(cliArgs, cliOutput, cliError) != 0 || !cliMap.SequenceEqual(File.ReadAllBytes(mapPath)))
            throw new Exception("External Int64 constructor CLI map repeat failed");
        string deniedPath = Path.Combine(cliRoot, "denied.hcexe");
        if (ManagedSourceCommandLineV1.Run(["compile-source-image", "--source", fixturePath, "--assembly-name", "ExternalWide", "--type", "Fixture", "--method", "Read",
            "--metadata-reference", referencePaths[0], ..referencePaths.Skip(1).SelectMany(path => new[] { "--reference", path }),
            "--metadata-reference", libraryPath, "--out", deniedPath], cliOutput, cliError) != 3 || File.Exists(deniedPath) ||
            !cliError.ToString().Contains("HC0006", StringComparison.Ordinal))
            throw new Exception("External Int64 constructor CLI metadata-only admission bypassed");
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(constructorRequest, []), Compile(constructorRequest, [], [0, 4]) })
            if (denied.Source.Pe is not null || denied.Source.ArtifactIdentity is not null || denied.Compilation is not null ||
                !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("External Int64 constructor acquired omitted/metadata-only admission");
        ManagedSourceCompanionResultV1 standalone = ManagedSourceCompanionV1.Compile(constructorRequest, importer, new("Fixture", "Read"));
        if (standalone.Pe is not null || !standalone.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
            throw new Exception("Standalone Compile acquired external Int64 constructor authority");
        foreach (string text in new[] {
            library.Replace("Value = value;", "Value = value + 1;"),
            library.Replace("Value = value;", "System.GC.Collect(); Value = value;"),
            library.Replace("public readonly struct WideValue {", "public readonly struct WideValue { static WideValue() {}"),
            library.Replace("WideValue(long value)", "WideValue(int value)").Replace("new WideValue(value)", "new WideValue((int)value)") }) {
            ManagedSourceBodyWorldResultV1 badLibrary = Library(text);
            if (badLibrary.Source.Pe is null) throw new Exception("External Int64 constructor negative failed to emit");
            ManagedSourceBodyWorldResultV1 denied = Compile(constructorRequest with { ReferenceImages = [..references, badLibrary.Source.Pe!], Documents = [new("/src/Primary.cs",
                constructorRequest.Documents[0].Text.Replace("new WideValue(input)", "new WideValue(1)"))] });
            if (denied.Source.Pe is not null || denied.Compilation is not null || !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("External Int64 constructor admitted effect/cctor/mixed-width definition");
        }
        // A pure long overload must not grant admission to an effectful int overload on the same nominal type.
        ManagedSourceBodyWorldResultV1 overloaded = Library(library.Replace("public WideValue(long value)", "public WideValue(int value) { System.GC.Collect(); Value = value; } public WideValue(long value)"));
        Link(Compile(constructorRequest with { ReferenceImages = [..references, overloaded.Source.Pe!] }));
        ManagedSourceBodyWorldResultV1 wrongOverload = Compile(constructorRequest with { ReferenceImages = [..references, overloaded.Source.Pe!], Documents = [new("/src/Primary.cs",
            constructorRequest.Documents[0].Text.Replace("new WideValue(input)", "new WideValue(1)"))] });
        if (wrongOverload.Source.Pe is not null || !wrongOverload.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
            throw new Exception("External Int64 qualified type name authorized another constructor overload");
        Console.WriteLine("PASS selected external Int64 exact constructor -> NoHeap/receiver/shared image; single-module/explicit-field/target-typed/unprofiled bytes, physical SD/LD, scalar host boundaries, repeat PE/PDB/maps/image; role/effect/cctor/mixed-width/overload and standalone negatives");
        Console.WriteLine("PASS external Int64 constructor CLI/API PE/PDB/image and map repeat; metadata-only role exit3/no output");
        Console.WriteLine("IDENTITY external-Int64-constructor artifact=" + construction.Source.ArtifactIdentity!.Digest + "; image=" + constructionImage.RestrictedImage!.PackageSha256);
        ManagedSourceBodyWorldResultV1 constructedCopy = Compile(constructorRequest with { Documents = [new("/src/Primary.cs",
            constructorRequest.Documents[0].Text.Replace("return value.Value;", "var alias = value; return alias.Value;"))] });
        using (PEReader copyPe = new PEReader(new MemoryStream(constructedCopy.Source.Pe!))) {
            MetadataReader copyMetadata = copyPe.GetMetadataReader();
            MethodDefinition copyMethod = copyMetadata.GetMethodDefinition(copyMetadata.MethodDefinitions.Single(h => copyMetadata.GetString(copyMetadata.GetMethodDefinition(h).Name) == "Read"));
            Console.WriteLine("IL constructed external Int64 copy=" + Convert.ToHexString(copyPe.GetMethodBody(copyMethod.RelativeVirtualAddress).GetILBytes()!));
        }
        Link(constructedCopy);
        foreach (string body in new[] { "var alias = value; return alias.Value;",
            "var alias = value; return value.Value + alias.Value;",
            "var first = value; var alias = first; return value.Value + first.Value + alias.Value;",
            "WideValue alias; if (input > 0) alias = value; else alias = value; return value.Value + alias.Value;" }) {
            CompanionSourceRequestV1 copyRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text.Replace("return value.Value;", body))] };
            ManagedSourceBodyWorldResultV1 copied = Compile(copyRequest); ScalarControlFlowV2LinkedProgramV1 copiedImage = Link(copied); ManagedSourceBodyWorldResultV1 copiedRepeat = Compile(copyRequest);
            using (PEReader copyPe = new PEReader(new MemoryStream(copied.Source.Pe!))) {
                MetadataReader md = copyPe.GetMetadataReader();
                MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                MethodBodyBlock methodBody = copyPe.GetMethodBody(method.RelativeVirtualAddress);
                byte[] localSignature = md.GetBlobBytes(md.GetStandaloneSignature(methodBody.LocalSignature).Signature);
                int expectedLocals = body.Contains("first") ? 3 : body == "var alias = value; return alias.Value;" ? 1 : 2;
                if (localSignature[0] != 7 || localSignature[1] != expectedLocals)
                    throw new Exception("Constructed copy fixture no longer retains expected nominal locals");
                Console.WriteLine("COPY-IL locals=" + expectedLocals + "; signature=" + Convert.ToHexString(localSignature) +
                    "; IL=" + Convert.ToHexString(methodBody.GetILBytes()!));
            }
            ManagedSourceBodyWorldResultV1 singleCopy = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", copyRequest.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 scalarCopy = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", copyRequest.Documents[0].Text
                .Replace("new WideValue(input)", "input").Replace("WideValue alias;", "long alias;")
                .Replace("value.Value", "value").Replace("first.Value", "first").Replace("alias.Value", "alias"))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ScalarControlFlowV2LinkedProgramV1 scalarCopyImage = Link(scalarCopy);
            if (!Codes(copiedImage).SequenceEqual(Codes(Link(singleCopy))) ||
                copiedImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 !=
                    scalarCopyImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
                !copied.Source.Pe!.SequenceEqual(copiedRepeat.Source.Pe!) || !copied.Source.Pdb!.SequenceEqual(copiedRepeat.Source.Pdb!) ||
                !copiedImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(copiedRepeat).RestrictedImage!.PackageBytes) ||
                copied.SourceMaps.Count != 1 || copied.SourceMaps.Any(row => copiedRepeat.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("Constructed Int64 local copy oracle failed: body=" + body +
                    "; single=" + Codes(copiedImage).SequenceEqual(Codes(Link(singleCopy))) + "; actual=" + string.Join(';', Codes(copiedImage)) +
                    "; scalar=" + string.Join(';', Codes(scalarCopyImage)) + "; maps=" + copied.SourceMaps.Count);
            AssemblyLoadContext copyContext = new System.Runtime.Loader.AssemblyLoadContext("constructed-copy-host", isCollectible: true);
            try {
                copyContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                Assembly actual = copyContext.LoadFromStream(new MemoryStream(copied.Source.Pe!));
                Assembly expected = System.Reflection.Assembly.Load(scalarCopy.Source.Pe!);
                foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                    if (!Equals(actual.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), expected.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                        throw new Exception("Constructed Int64 local copy independent host boundary oracle failed");
            } finally { copyContext.Unload(); }
        }
        CompanionSourceRequestV1 nominalCopyRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
            .Replace("return value.Value;", "var alias = value; return alias.Value;")
            .Replace("public static class Fixture {", "public static class Fixture { public static long Primitive(long value) => value;") +
            " public readonly struct OtherValue { public long Value { get; } public OtherValue(long value) { Value = value; } }")] };
        ManagedSourceBodyWorldResultV1 nominalCopy = Compile(nominalCopyRequest); Link(nominalCopy);
        foreach (bool primitive in new[] { false, true }) {
            byte[] bytes = nominalCopy.Source.Pe!.ToArray();
            using PEReader pe = new PEReader(new MemoryStream(bytes)); MetadataReader md = pe.GetMetadataReader();
            MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
            TypeDefinitionHandle other = md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "OtherValue");
            int replacement = MetadataTokens.GetToken(primitive
                ? md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Primitive")
                : md.GetTypeDefinition(other).GetMethods().Single(h => md.GetString(md.GetMethodDefinition(h).Name) == ".ctor"));
            byte[] il = pe.GetMethodBody(read.RelativeVirtualAddress).GetILBytes()!;
            if (il.Length != 15 || il[0] != 2 || il[1] != 0x73 || il[6] != 0x0a) throw new Exception("Constructed copy nominal fixture IL changed");
            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int start = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
            int header = (bytes[start] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start, 2)) >> 12) * 4;
            bytes[start + header + 1] = primitive ? (byte)0x28 : (byte)0x73;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(start + header + 2, 4), replacement);
            ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, bytes, "constructed-copy-nominal-negative",
                [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
            if (denied.Status is not (RestrictedCilImportStatusV1.InvalidInput or RestrictedCilImportStatusV1.Unsupported) || denied.Diagnostics.Count == 0)
                throw new Exception("Constructed Int64 copy accepted primitive/wrong-nominal producer");
            Console.WriteLine("COPY-NEGATIVE primitive=" + primitive + "; " + string.Join(';', denied.Diagnostics));
        }
        foreach (string text in new[] {
            library.Replace("Value = value;", "Value = value + 1;"),
            library.Replace("public long Value { get; }", "private readonly long _value; public long Value => _value + 1;").Replace("Value = value;", "_value = value;"),
            library.Replace("public readonly struct WideValue {", "public readonly struct WideValue { static WideValue() {}") }) {
            ManagedSourceBodyWorldResultV1 badDefinition = Library(text);
            ManagedSourceBodyWorldResultV1 denied = Compile(nominalCopyRequest with { ReferenceImages = [..references, badDefinition.Source.Pe!] });
            if (denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || denied.Source.Pe is not null && (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("Constructed Int64 copy admitted effects/nonpure getter/cctor");
        }
        ManagedSourceBodyWorldResultV1 escapedCopy = Compile(nominalCopyRequest with { Documents = [new("/src/Primary.cs", nominalCopyRequest.Documents[0].Text
            .Replace("return alias.Value;", "return Escape(ref alias);")
            .Replace("public static class Fixture {", "public static class Fixture { private static long Escape(ref WideValue value) => value.Value;"))] });
        if (escapedCopy.Source.Pe is null || escapedCopy.Compilation?.Status == RestrictedCilImportStatusV1.Success || (escapedCopy.Compilation?.Diagnostics.Count ?? 0) == 0)
            throw new Exception("Constructed copy address escaped its pure getter boundary");
        ManagedSourceBodyWorldResultV1 ehCopy = Compile(nominalCopyRequest with { Documents = [new("/src/Primary.cs", nominalCopyRequest.Documents[0].Text
            .Replace("[HybridCpuKernelNoHeap]", "").Replace("var value = new WideValue(input); var alias = value; return alias.Value;",
                "try { var value = new WideValue(input); var alias = value; return alias.Value; } finally { System.GC.Collect(); }"))] });
        if (ehCopy.Source.Pe is null || ehCopy.Compilation?.Status == RestrictedCilImportStatusV1.Success || (ehCopy.Compilation?.Diagnostics.Count ?? 0) == 0)
            throw new Exception("Constructed copy exceeded non-EH projection scope");
        foreach (int limit in new[] { 0, 1, 2, 3 }) {
            ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(nominalCopyRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with { MaximumMetadataResolutionSteps = limit }),
                [new("Fixture", "Read")], [4], [0]);
            if (limit == 3 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                budgeted.Compilation?.Status != (limit == 0 ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.BudgetExhausted) ||
                !budgeted.Compilation.Diagnostics.Any(d => d.Code == (limit == 0 ? "HCSCF-BODYWORLD0004" : "HCSCF-BUDGET2002")))
                throw new Exception("Constructed copy resolution budget boundary changed: " + limit + "; " + string.Join(';', budgeted.Compilation?.Diagnostics ?? []));
        }
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text.Replace("return value.Value;",
            "var alias = value; return value.Value + alias.Value;"))] }, []),
            Compile(constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text.Replace("return value.Value;",
            "var alias = value; return value.Value + alias.Value;"))] }, [], [0, 4]) })
            if (denied.Source.Pe is not null || !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("Constructed Int64 copy gained omitted/metadata-only source authority");
        Console.WriteLine("PASS constructed Int64 newobj/local/pure-getter and retained local copy chains/conditional joins -> source-owned/external scalar caller bytes; host boundaries, repeat PE/PDB/maps/image; nominal/effect/ref/EH/role/budget0/1/2/3 controls");
        const string safeMathHelpers = """
            private static long SafeDiv(long x) => x / 3;
            private static long SafeRem(long x) => x % 3;
            private static long SafeDivNegative(long x) => x / -3;
            private static long SafeUDiv(long x) => (long)((ulong)x / 3UL);
            private static long SafeURem(long x) => (long)((ulong)x % 3UL);
            private static long SafeWordDiv(long x) => (long)((int)x / 3);
            private static long SafeWordRem(long x) => (long)((int)x % 3);
            private static long SafeUWordDiv(long x) => (long)((uint)x / 3U);
            private static long SafeUWordRem(long x) => (long)((uint)x % 3U);
            private static int SafeI4Leaf(int x) => x / 3;
            private static int SafeRemI4Leaf(int x) => x % 3;
            private static long SafeConditional(long x) => x < 0 ? SafeDiv(x) : SafeRem(x);
            private static long SafeMathChain(long x) => SafeConditional(x) + SafeUWordRem(x);
            private static long Updated(long x) { x += 1; return x; }
            private static int UpdatedI4(int x) { x += 1; return x; }
            private static long UpdatedConditional(long x) { if (x < 0) x = -x; else x += 1; return x; }
            """;
        foreach (string expression in new[] { "input + 1", "unchecked(-input)", "(input * 3) ^ (input >> 5)", "(long)(int)input + 1", "~input & 0x100000001L", "(input << 3) | 7L",
            "SafeDiv(input)", "SafeRem(input)", "SafeDivNegative(input)", "SafeUDiv(input)", "SafeURem(input)",
            "SafeWordDiv(input)", "SafeWordRem(input)", "SafeUWordDiv(input)", "SafeUWordRem(input)",
            "(long)SafeI4Leaf((int)input)", "(long)SafeRemI4Leaf((int)input)", "SafeConditional(input)", "SafeMathChain(input)",
            "Updated(input)", "(long)UpdatedI4((int)input)", "UpdatedConditional(input)",
            "input < 0 ? -input : input", "input == 0 ? 7L : input < 0 ? input + 1 : input - 1", "input > 0 && input < 100 ? input * 3 : ~input",
            "Identity(input)", "Transform(input) + 1", "input < 0 ? Transform(input) : Identity(input)", "(long)Narrow((int)input)", "Chain(input)", "Chain(input) + Chain(input)",
            "input / 3", "input % 3", "input / -3", "input % -3", "(long)((ulong)input / 3UL)", "(long)((ulong)input % 3UL)",
            "(long)((int)input / 3)", "(long)((int)input % 3)", "(long)((uint)input / 3U)", "(long)((uint)input % 3U)",
            "input < 0 ? input / 3 : input % 3", "input / Identity(input)", "input / Identity(0)", "input / -1", "input / Identity(-1)",
            "input % Identity(input)", "input % Identity(0)", "input % -1", "input % Identity(-1)", "input % Identity(3)", "input % Identity(-3)",
            "(long)((ulong)input / (ulong)Identity(input))", "(long)((ulong)input / (ulong)Identity(0))",
            "(long)((ulong)input / (ulong)Identity(-1))", "(long)((ulong)input / (ulong)Identity(3))",
            "(long)((ulong)input % (ulong)Identity(input))", "(long)((ulong)input % (ulong)Identity(0))",
            "(long)((ulong)input % (ulong)Identity(-1))", "(long)((ulong)input % (ulong)Identity(3))",
            "(long)((int)input / (int)Identity(input))", "(long)((int)input / (int)Identity(0))", "(long)((int)input / -1)", "(long)((int)input / (int)Identity(3))",
            "(long)((int)input % (int)Identity(input))", "(long)((int)input % (int)Identity(0))", "(long)((int)input % -1)", "(long)((int)input % (int)Identity(3))",
            "(long)((uint)input / (uint)Identity(input))", "(long)((uint)input / (uint)Identity(0))", "(long)((uint)input / (uint)Identity(1))", "(long)((uint)input / (uint)Identity(3))",
            "(long)((uint)input % (uint)Identity(input))", "(long)((uint)input % (uint)Identity(0))", "(long)((uint)input % (uint)Identity(-1))", "(long)((uint)input % (uint)Identity(3))" }) {
            CompanionSourceRequestV1 expressionRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("new WideValue(input)", "new WideValue(" + expression + ")").Replace("return value.Value;", "var alias = value; return value.Value + alias.Value;")
                .Replace("public static class Fixture {", "public static class Fixture { " + safeMathHelpers + " private static long Chain(long x) => Mid(x) + Identity(x); private static long Mid(long x) => Transform(x); private static long Identity(long x) => x; private static long Transform(long x) => x < 0 ? ~x : x * 3; private static int Narrow(int x) => x + 1;"))] };
            ManagedSourceBodyWorldResultV1 actual = Compile(expressionRequest); ScalarControlFlowV2LinkedProgramV1 actualImage = Link(actual); ManagedSourceBodyWorldResultV1 again = Compile(expressionRequest);
            if (expression.Contains("(int)Identity(") || expression.Contains("(uint)Identity(") ||
                expression is "(long)((int)input / -1)" or "(long)((int)input % -1)") {
                bool unsignedWord = expression.Contains("(uint)"); bool remWord = expression.Contains('%');
                string symbol = unsignedWord ? "__hybridcpu_managed_divide_u4_checked" : remWord ?
                    "__hybridcpu_managed_remainder_i4_checked" : "__hybridcpu_managed_divide_i4_checked";
                string module = "hybridcpu.managed-runtime." + (unsignedWord ? "divide-u4" : remWord ? "remainder-i4" : "divide-i4") + "-checked/v1";
                RestrictedCilImportResultV1 readWord = actual.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import;
                List<IrInstruction> instructionsWord = readWord.Program!.Instructions.ToList();
                int callWord = instructionsWord.FindIndex(i => i.Annotation.BranchTargetSymbolName == symbol && i.Annotation.IsManagedGcSafepoint);
                if (!readWord.RequiresNativeExceptionTransfer || readWord.ReceiverCallerStoragePlans?.Count > 0 || callWord < 0 ||
                    !actualImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).ObjectArtifact.Relocations.Any(r => r.TargetSymbol == symbol) ||
                    actualImage.LinkedRuntimeModuleIdentities?.Contains(module) != true ||
                    instructionsWord.Any(i => i.Opcode is HybridCpuOpcode.DIVW or HybridCpuOpcode.DIVUW or HybridCpuOpcode.REMW))
                    throw new Exception("Checked I4 constructor lost helper width/relocation/safepoint/lifetime");
                if (unsignedWord ? !instructionsWord.Skip(callWord + 1).Any(i => i.Opcode == HybridCpuOpcode.SLLI && i.Immediate == 32) ||
                    !instructionsWord.Skip(callWord + 1).Any(i => i.Opcode == HybridCpuOpcode.SRLI && i.Immediate == 32) :
                    !instructionsWord.Skip(callWord + 1).Any(i => i.Opcode == HybridCpuOpcode.ADDIW))
                    throw new Exception("Checked I4 result lost source-aware I8 extension");
                if (unsignedWord && remWord) {
                    byte[] wordBytes = actualImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read("))
                        .ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
                    int wordRemainders = 0;
                    for (int offset = 0; offset < wordBytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); if (!bundle.TryReadBytes(wordBytes, offset)) throw new Exception("U4 remainder bundle");
                        for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                            HybridCpuInstructionWord word = bundle.GetInstruction(slot);
                            if ((HybridCpuOpcode)word.OpCode != HybridCpuOpcode.REMUW) continue;
                            if (slot >= 4 || !HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out byte rs2) ||
                                rd is 0 or >= 32 || rs1 is 0 or >= 32 || rs2 is 0 or >= 32) throw new Exception("U4 remainder encoded lane/registers");
                            wordRemainders++;
                        }
                    }
                    if (wordRemainders != 1) throw new Exception("U4 remainder encoded word width/count");
                }
                ScalarControlFlowV2LinkedProgramV1 missing = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation with { TypeUniverse = null }, actual.Compilation.Graph!.RootIdentities.Single());
                string code = unsignedWord ? "HCSCF-LINK4026" : remWord ? "HCSCF-LINK4042" : "HCSCF-LINK4036";
                if (missing.Status != ScalarControlFlowV2LinkStatusV1.BackendRejected || !missing.Diagnostics.Any(d => d.Code == code))
                    throw new Exception("Checked I4 exception image gate changed");
            }
            if (expression.Contains(" / Identity(") || expression == "input / -1" || expression.Contains(" % Identity(") || expression == "input % -1" || expression.Contains("(ulong)Identity(")) {
                RestrictedCilImportResultV1 read = actual.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import;
                if (expression.Contains("(ulong)Identity(")) {
                    byte[] code = actualImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read("))
                        .ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
                    int divisions = 0;
                    for (int offset = 0; offset < code.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); if (!bundle.TryReadBytes(code, offset)) throw new Exception("Unsigned divide bundle");
                        for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                            HybridCpuInstructionWord word = bundle.GetInstruction(slot);
                            if ((HybridCpuOpcode)word.OpCode is HybridCpuOpcode.DIVUW or HybridCpuOpcode.REMUW) throw new Exception("Unsigned I8 narrowed to word");
                            if ((HybridCpuOpcode)word.OpCode != (expression.Contains('%') ? HybridCpuOpcode.REMU : HybridCpuOpcode.DIVU)) continue;
                            if (slot >= 4 || !HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs1, out byte rs2) ||
                                rd is 0 or >= 32 || rs1 is 0 or >= 32 || rs2 is 0 or >= 32)
                                throw new Exception("Unsigned I8 encoded ALU lane/physical registers");
                            divisions++;
                        }
                    }
                    if (divisions != 1) throw new Exception("Unsigned I8 requires one exact encoded DIVU/REMU");
                }
                if (!read.RequiresNativeExceptionTransfer || read.ReceiverCallerStoragePlans?.Count > 0 ||
                    !read.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_divide_i8_checked" && i.Annotation.IsManagedGcSafepoint) ||
                    read.Program.Instructions.Any(i => i.Opcode is HybridCpuOpcode.DIV or HybridCpuOpcode.DIVW or HybridCpuOpcode.REM or HybridCpuOpcode.REMW) ||
                    !actualImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).ObjectArtifact.Relocations
                        .Any(r => r.TargetSymbol == "__hybridcpu_managed_divide_i8_checked") ||
                    actualImage.LinkedRuntimeModuleIdentities?.Contains(HybridCPU.Compiler.Core.Target.Managed.HybridCpuManagedDivideInt64EmitterV1.ModuleIdentity) != true)
                    throw new Exception("Checked constructor division lost exact helper/transfer/safepoint or retained receiver storage");
                if (expression == "input / Identity(input)") {
                    ScalarControlFlowV2LinkedProgramV1 missingTypes = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation with { TypeUniverse = null },
                        actual.Compilation.Graph!.RootIdentities.Single());
                    if (missingTypes.Status != ScalarControlFlowV2LinkStatusV1.BackendRejected ||
                        !missingTypes.Diagnostics.Any(d => d.Code == "HCSCF-LINK4037") ||
                        !ManagedWorkstreamDiscoveryContractV1.Discover(actual.Compilation, actualImage).RequiredWorkstreams.Contains("eh-unwind"))
                        throw new Exception("Checked constructor division lost exact exception-type gate or EH workstream");
                }
            }
            ManagedSourceBodyWorldResultV1 singleExpression = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", expressionRequest.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 scalarExpression = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", expressionRequest.Documents[0].Text
                .Replace("new WideValue(" + expression + ")", "(" + expression + ")").Replace("value.Value", "value").Replace("alias.Value", "alias"))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            if (!Codes(actualImage).SequenceEqual(Codes(Link(singleExpression))) ||
                actualImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 !=
                    Link(scalarExpression).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
                !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !actualImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(again).RestrictedImage!.PackageBytes) ||
                actual.SourceMaps.Count != scalarExpression.SourceMaps.Count || actual.SourceMaps.Any(row => again.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("In-place constructor expression oracle failed: " + expression + "; single=" + Codes(actualImage).SequenceEqual(Codes(Link(singleExpression))) +
                    "; actual=" + string.Join(';', Codes(actualImage)) + "; scalar=" + string.Join(';', Codes(Link(scalarExpression))));
            AssemblyLoadContext expressionContext = new System.Runtime.Loader.AssemblyLoadContext("constructor-expression-host", isCollectible: true);
            try {
                expressionContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                Assembly expressionHost = expressionContext.LoadFromStream(new MemoryStream(actual.Source.Pe!)); Assembly expected = System.Reflection.Assembly.Load(scalarExpression.Source.Pe!);
                static object? Outcome(System.Reflection.MethodInfo method, long input) {
                    try { return method.Invoke(null, [input]); }
                    catch (System.Reflection.TargetInvocationException fault) { return fault.InnerException!.GetType(); }
                }
                if (expression.Contains('/') || expression.Contains('%'))
                    Console.WriteLine("DIVISION-IL expression=" + expression + "; IL=" + Convert.ToHexString(expressionHost.GetType("Fixture")!.GetMethod("Read")!.GetMethodBody()!.GetILAsByteArray()!));
                foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                    if (!Equals(Outcome(expressionHost.GetType("Fixture")!.GetMethod("Read")!, input), Outcome(expected.GetType("Fixture")!.GetMethod("Read")!, input)))
                        throw new Exception("In-place constructor expression scalar host boundary oracle failed");
            } finally { expressionContext.Unload(); }
            if (expression == "input < 0 ? -input : input") {
                foreach (bool backward in new[] { false, true }) {
                    byte[] escaped = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(escaped))) {
                        MetadataReader md = pe.GetMetadataReader(); MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        byte[] il = pe.GetMethodBody(read.RelativeVirtualAddress).GetILBytes()!;
                        int ctorToken = MetadataTokens.GetToken(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == ".ctor" &&
                            md.GetString(md.GetTypeReference((TypeReferenceHandle)md.GetMemberReference(h).Parent).Name) == "WideValue"));
                        int ctor = Enumerable.Range(0, il.Length - 4).Single(i => il[i] == 0x28 && BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(i + 1, 4)) == ctorToken);
                        int branch = Enumerable.Range(2, ctor - 2).First(i => il[i] is >= 0x2b and <= 0x37);
                        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                        int start = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
                        int header = (BinaryPrimitives.ReadUInt16LittleEndian(escaped.AsSpan(start, 2)) >> 12) * 4;
                        escaped[start + header + branch + 1] = unchecked((byte)(sbyte)((backward ? 2 : ctor + 5) - branch - 2));
                        Console.WriteLine("CONDITIONAL-IL maxStack=" + pe.GetMethodBody(read.RelativeVirtualAddress).MaxStack + "; IL=" + Convert.ToHexString(il));
                    }
                    ManagedCallGraphCompilationV1 rejected = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, escaped, "conditional-argument-region-negative",
                        [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (rejected.Status == RestrictedCilImportStatusV1.Success || rejected.Diagnostics.Count == 0)
                        throw new Exception("Conditional argument escaped region or admitted a backedge");
                }
            }
            if (expression == "input + 1") {
                byte[] understated = actual.Source.Pe!.ToArray();
                using (PEReader pe = new PEReader(new MemoryStream(understated))) {
                    MetadataReader md = pe.GetMetadataReader(); MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                    MethodBodyBlock body = pe.GetMethodBody(read.RelativeVirtualAddress); byte[] il = body.GetILBytes()!;
                    if (body.MaxStack != 3 || il[0] != 0x12) throw new Exception("In-place expression stack fixture changed");
                    SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                    int start = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
                    if ((understated[start] & 3) != 3) throw new Exception("Expression fixture must use a fat header");
                    BinaryPrimitives.WriteUInt16LittleEndian(understated.AsSpan(start + 2, 2), 2);
                    Console.WriteLine("EXPRESSION-IL maxStack=" + body.MaxStack + "; IL=" + Convert.ToHexString(il));
                }
                ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, understated, "constructor-stack-negative",
                    [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                if (denied.Status != RestrictedCilImportStatusV1.InvalidInput || !denied.Diagnostics.Any(d => d.Code == "HCCIL0009"))
                    throw new Exception("Erased receiver bypassed original declared MaxStack: " + string.Join(';', denied.Diagnostics));
                foreach (byte operation in new byte[] { 0x25, 0x26 }) {
                    byte[] consumedReceiver = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(consumedReceiver))) {
                        MetadataReader md = pe.GetMetadataReader(); MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                        int start = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
                        int header = (BinaryPrimitives.ReadUInt16LittleEndian(consumedReceiver.AsSpan(start, 2)) >> 12) * 4;
                        if (consumedReceiver[start + header + 2] != 2) throw new Exception("Expression ingress IL changed");
                        consumedReceiver[start + header + 2] = operation;
                    }
                    ManagedCallGraphCompilationV1 rejected = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, consumedReceiver, "constructor-receiver-stack-negative",
                        [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (rejected.Status == RestrictedCilImportStatusV1.Success || rejected.Diagnostics.Count == 0)
                        throw new Exception("Expression consumed/duplicated its receiver underneath the isolated argument stack");
                }
                foreach (int stackBudget in new[] { 1, 2, 3 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(expressionRequest, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumEvaluationStack = stackBudget }), [new("Fixture", "Read")], [4], [0]);
                    if (stackBudget == 3 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success : budgeted.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted ||
                        !budgeted.Compilation.Diagnostics.Any(d => d.Code == "HCCIL2103"))
                        throw new Exception("Erased receiver bypassed original stack budget: " + stackBudget + "; " + string.Join(';', budgeted.Compilation?.Diagnostics ?? []));
                }
            }
        }
        CompanionSourceRequestV1 entryRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
            .Replace("var value = new WideValue(input); return value.Value;", "if (input < 0) return 0; var value = new WideValue(input + 1); var alias = value; return value.Value + alias.Value;"))] };
        ManagedSourceBodyWorldResultV1 entryPositive = Compile(entryRequest); Link(entryPositive);
        byte[] enteredArgument = entryPositive.Source.Pe!.ToArray();
        using (PEReader pe = new PEReader(new MemoryStream(enteredArgument))) {
            MetadataReader md = pe.GetMetadataReader(); MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
            byte[] il = pe.GetMethodBody(read.RelativeVirtualAddress).GetILBytes()!;
            int address = Array.IndexOf(il, (byte)0x12);
            int branch = Enumerable.Range(0, address - 1).Single(i => il[i] is >= 0x2b and <= 0x37 && i + 2 + (sbyte)il[i + 1] == address);
            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int start = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
            int header = (BinaryPrimitives.ReadUInt16LittleEndian(enteredArgument.AsSpan(start, 2)) >> 12) * 4;
            enteredArgument[start + header + branch + 1] = checked((byte)(address + 2 - branch - 2));
        }
        ManagedCallGraphCompilationV1 entered = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, enteredArgument, "constructor-incoming-target-negative",
            [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
        if (entered.Status == RestrictedCilImportStatusV1.Success || entered.Diagnostics.Count == 0)
            throw new Exception("External branch entered an erased constructor argument region");
        CompanionSourceRequestV1 calledRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
            .Replace("public static class Fixture {", "public static class Fixture { private static long Identity(long x) => x;")
            .Replace("new WideValue(input)", "new WideValue(Identity(input))").Replace("return value.Value;", "var alias = value; return value.Value + alias.Value;"))] };
        Link(Compile(calledRequest));
        CompanionSourceRequestV1 localGenericRequest=calledRequest with { Documents=[new("/src/Primary.cs",calledRequest.Documents[0].Text
            .Replace("long Identity(long x)","long Identity<T>(long x)").Replace("new WideValue(Identity(input))","new WideValue(Identity<long>(input))"))] };
        ManagedSourceBodyWorldResultV1 localGeneric=Compile(localGenericRequest), localGenericAgain=Compile(localGenericRequest);
        ScalarControlFlowV2LinkedProgramV1 localGenericImage=Link(localGeneric), localPlainImage=Link(Compile(calledRequest));
        static string HelperCode(ScalarControlFlowV2LinkedProgramV1 value,string method)=>value.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).CodeSha256;
        if(new[]{"Read","Identity"}.Any(name=>HelperCode(localGenericImage,name)!=HelperCode(localPlainImage,name)) ||
            !localGeneric.Source.Pe!.SequenceEqual(localGenericAgain.Source.Pe!) || !localGeneric.Source.Pdb!.SequenceEqual(localGenericAgain.Source.Pdb!) ||
            localGeneric.SourceMaps.Count!=localGenericAgain.SourceMaps.Count || localGeneric.SourceMaps.Any(row=>localGenericAgain.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
            !localGenericImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(localGenericAgain).RestrictedImage!.PackageBytes) ||
            localGeneric.Compilation!.Methods.Single(m=>m.Identity.MethodName=="Identity").Identity.GenericMethodArguments?.Single()!=new RestrictedCilGenericArgumentV1("System.Int64",RestrictedCilTypeV1.Int64))
            throw new Exception("Local Int64 generic constructor helper exact code/context/repeat/maps/image");
        CompanionSourceRequestV1 doubleCallRequest = calledRequest with { Documents = [new("/src/Primary.cs", calledRequest.Documents[0].Text.Replace("Identity(input))", "Identity(input) + Identity(input))"))] };
        foreach (int callBudget in new[] { 1, 2 }) {
            ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(doubleCallRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = callBudget }), [new("Fixture", "Read")], [4], [0]);
            if (callBudget == 2 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                budgeted.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted || !budgeted.Compilation.Diagnostics.Any(d => d.Code == "HCCIL2007"))
                throw new Exception("Constructor helper call-budget precedence changed: " + callBudget + "; " + string.Join(';', budgeted.Compilation?.Diagnostics ?? []));
        }
        foreach (string helper in new[] { "{ System.GC.Collect(); return x; }", "{ try { return x; } finally { System.GC.Collect(); } }",
            "{ return Identity(x); }", "{ while (x > 0) x--; return x; }", "{ return State + x; }" }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(calledRequest with { Documents = [new("/src/Primary.cs", calledRequest.Documents[0].Text
                .Replace("=> x;", helper).Replace("public static class Fixture {", "public static class Fixture { private static long State; private static long Other(long x) => x;"))] });
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("Constructor argument helper admitted effects/EH/transitive call/loop/memory");
        }
        foreach (string text in new[] {
            calledRequest.Documents[0].Text.Replace("public static class Fixture {", "public static class Fixture { static Fixture() {}"),
            calledRequest.Documents[0].Text.Replace("long Identity(long x)", "long Identity<T>(long x)").Replace("new WideValue(Identity(input))", "new WideValue(Identity<ulong>(input))"),
            calledRequest.Documents[0].Text.Replace("long Identity(long x)", "long Identity(in long x)") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(calledRequest with { Documents = [new("/src/Primary.cs", text)] });
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("Constructor argument helper exceeded cctor/generic/byref scope");
        }
        Console.WriteLine("PASS local static primitive leaf helpers in Int64 constructor arguments -> exact body typed proof, call ABI/shared image/scalar bytes; effects/EH/transitive-call/loop/memory negatives");
        ManagedSourceBodyWorldResultV1 externalHelperLibrary = Library(library.Replace("public static class WideFactory {", "public static class WideFactory { public static long Identity(long x) => x;"));
        ManagedSourceBodyWorldResultV1 externalHelper = Compile(calledRequest with { ReferenceImages = [..references, externalHelperLibrary.Source.Pe!], Documents = [new("/src/Primary.cs",
            calledRequest.Documents[0].Text.Replace("new WideValue(Identity(input))", "new WideValue(WideFactory.Identity(input))"))] });
        ScalarControlFlowV2LinkedProgramV1 externalHelperImage = Link(externalHelper);
        CompanionSourceRequestV1 externalHelperRequest = calledRequest with { ReferenceImages = [..references, externalHelperLibrary.Source.Pe!], Documents = [new("/src/Primary.cs",
            calledRequest.Documents[0].Text.Replace("new WideValue(Identity(input))", "new WideValue(WideFactory.Identity(input))"))] };
        ManagedSourceBodyWorldResultV1 externalGenericLibrary=Library(library.Replace("public static class WideFactory {","public static class WideFactory { public static long Identity<T>(long x)=>x;"));
        CompanionSourceRequestV1 externalGenericRequest=externalHelperRequest with {ReferenceImages=[..references,externalGenericLibrary.Source.Pe!],Documents=[new("/src/Primary.cs",externalHelperRequest.Documents[0].Text.Replace("WideFactory.Identity(input)","WideFactory.Identity<long>(input)"))]};
        ManagedSourceBodyWorldResultV1 externalGeneric=Compile(externalGenericRequest), externalGenericAgain=Compile(externalGenericRequest);
        ScalarControlFlowV2LinkedProgramV1 externalGenericImage=Link(externalGeneric);
        if(new[]{"Read","Identity"}.Any(name=>HelperCode(externalGenericImage,name)!=HelperCode(externalHelperImage,name)) ||
            !externalGeneric.Source.Pe!.SequenceEqual(externalGenericAgain.Source.Pe!) || !externalGeneric.Source.Pdb!.SequenceEqual(externalGenericAgain.Source.Pdb!) ||
            externalGeneric.SourceMaps.Count!=externalHelper.SourceMaps.Count || externalGeneric.SourceMaps.Any(row=>externalGenericAgain.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
            !externalGenericImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(externalGenericAgain).RestrictedImage!.PackageBytes))
            throw new Exception("External Int64 generic constructor helper exact code/repeat/maps/image");
        AssemblyLoadContext genericConstructorContext=new("generic-constructor",isCollectible:true);
        try {
            genericConstructorContext.LoadFromStream(new MemoryStream(externalGenericLibrary.Source.Pe!));
            MethodInfo genericRead=genericConstructorContext.LoadFromStream(new MemoryStream(externalGeneric.Source.Pe!)).GetType("Fixture")!.GetMethod("Read")!;
            foreach(long input in new[]{long.MinValue,long.MaxValue,-1L,0L,1L,1L<<40})
                if(!Equals(genericRead.Invoke(null,[input]),unchecked(input+input)))throw new Exception("Generic constructor host high-bit/overflow boundary");
        } finally {genericConstructorContext.Unload();}
        Console.WriteLine("PASS exact Int64 generic local/external constructor helper contexts, scalar helper/caller bytes, host high-bit/overflow, repeat PE/PDB/maps/image; unsigned context stays closed");
        ManagedSourceBodyWorldResultV1 externalAgain = Compile(externalHelperRequest);
        ManagedSourceBodyWorldResultV1 externalSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", externalHelperRequest.Documents[0].Text +
            library.Replace("public static class WideFactory {", "public static class WideFactory { public static long Identity(long x) => x;"))], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        ManagedSourceBodyWorldResultV1 externalScalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", externalHelperRequest.Documents[0].Text
            .Replace("new WideValue(WideFactory.Identity(input))", "WideFactory.Identity(input)").Replace("value.Value", "value").Replace("alias.Value", "alias") +
            " public static class WideFactory { public static long Identity(long x) => x; }")], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        if (!Codes(externalHelperImage).SequenceEqual(Codes(Link(externalSingle))) ||
            externalHelperImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 !=
                Link(externalScalar).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
            !externalHelper.Source.Pe!.SequenceEqual(externalAgain.Source.Pe!) || !externalHelper.Source.Pdb!.SequenceEqual(externalAgain.Source.Pdb!) ||
            !externalHelperImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(externalAgain).RestrictedImage!.PackageBytes) ||
            externalHelper.SourceMaps.Count != 1 || externalHelper.SourceMaps.Any(row => externalAgain.SourceMaps[row.Key].Digest != row.Value.Digest))
            throw new Exception("External helper constructor scalar/single-module/repeat/maps oracle failed");
        AssemblyLoadContext externalContext = new System.Runtime.Loader.AssemblyLoadContext("external-constructor-helper-host", isCollectible: true);
        try {
            externalContext.LoadFromStream(new MemoryStream(externalHelperLibrary.Source.Pe!));
            Assembly actualHost = externalContext.LoadFromStream(new MemoryStream(externalHelper.Source.Pe!));
            Assembly expectedHost = System.Reflection.Assembly.Load(externalScalar.Source.Pe!);
            foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                if (!Equals(actualHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), expectedHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                    throw new Exception("External constructor helper scalar host boundary oracle failed");
        } finally { externalContext.Unload(); }
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] { Compile(externalHelperRequest, []), Compile(externalHelperRequest, [], [0, 4]) })
            if (denied.Source.Pe is not null || !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("External helper constructor gained omitted/metadata-only source admission");
        foreach (string helper in new[] { "{ System.GC.Collect(); return x; }", "{ return x + State; }", "{ return Identity(x); }",
            "{ while (x > 0) x--; return x; }", "{ try { return x; } finally { System.GC.Collect(); } }" }) {
            ManagedSourceBodyWorldResultV1 badLibrary = Library(library.Replace("public static class WideFactory {", "public static class WideFactory { private static long State; private static long Other(long x) => x; public static long Identity(long x) " + helper));
            ManagedSourceBodyWorldResultV1 denied = Compile(externalHelperRequest with { ReferenceImages = [..references, badLibrary.Source.Pe!] });
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("External helper purity proof admitted effects/memory/transitive call/loop/EH");
        }
        foreach (int limit in new[] { 0, 1, 2, 3, 4, 5, 6 }) {
            ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(externalHelperRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with { MaximumMetadataResolutionSteps = limit }), [new("Fixture", "Read")], [4], [0]);
            if (limit == 6 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                budgeted.Compilation?.Status != (limit == 0 ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.BudgetExhausted) ||
                !budgeted.Compilation.Diagnostics.Any(d => d.Code == (limit == 0 ? "HCSCF-BODYWORLD0004" : "HCSCF-BUDGET2002")))
                throw new Exception("External helper proof-resolution budget boundary: " + limit + "; " + string.Join(';', budgeted.Compilation?.Diagnostics ?? []));
        }
        ManagedSourceBodyWorldResultV1 changedHelperLibrary = Library(library.Replace("public static class WideFactory {", "public static class WideFactory { public static long Identity(long x) => x; public static long Unused() => 9;"));
        ManagedSourceBodyWorldResultV1 changedHelper = Compile(externalHelperRequest with { ReferenceImages = [..references, changedHelperLibrary.Source.Pe!] });
        if (changedHelper.Source.SourceInputDigest == externalHelper.Source.SourceInputDigest ||
            changedHelper.Compilation?.Graph?.GraphDigest == externalHelper.Compilation!.Graph!.GraphDigest ||
            !Codes(Link(changedHelper)).SequenceEqual(Codes(externalHelperImage)))
            throw new Exception("External helper full-definition/reference identity control failed");
        foreach (string declaration in new[] { "static WideFactory() {} public static long Identity(long x) => x;",
            "public static long Identity<T>(long x) => x;", "public static long Identity(in long x) => x;" }) {
            ManagedSourceBodyWorldResultV1 deniedLibrary = Library(library.Replace("public static class WideFactory {", "public static class WideFactory { " + declaration));
            string text = externalHelperRequest.Documents[0].Text;
            if (declaration.Contains("<T>")) text = text.Replace("WideFactory.Identity(input)", "WideFactory.Identity<ulong>(input)");
            ManagedSourceBodyWorldResultV1 denied = Compile(externalHelperRequest with { ReferenceImages = [..references, deniedLibrary.Source.Pe!], Documents = [new("/src/Primary.cs", text)] });
            if (denied.Compilation?.Status == RestrictedCilImportStatusV1.Success ||
                denied.Source.Pe is null && !denied.Source.Diagnostics.Any(d => d.Id == "HC0006" && d.Severity == "Error"))
                throw new Exception("External helper exceeded cctor/generic/byref proof scope");
        }
        Console.WriteLine("PASS selected external constructor helper exact budgeted definition/pure-leaf proof -> scalar/single-module bytes, split-host boundaries, repeat PE/PDB/maps/image; role/effect/memory/EH/loop/transitive-call/budget controls");
        const string chainHelpers = "public static long Identity(long x) => Mid(x) + Leaf(x); private static long Mid(long x) => Leaf(x); private static long Leaf(long x) => x;";
        string chainLibraryText = library.Replace("public static class WideFactory {", "public static class WideFactory { " + chainHelpers);
        ManagedSourceBodyWorldResultV1 chainLibrary = Library(chainLibraryText);
        CompanionSourceRequestV1 chainRequest = externalHelperRequest with { ReferenceImages = [..references, chainLibrary.Source.Pe!] };
        ManagedSourceBodyWorldResultV1 chain = Compile(chainRequest); ScalarControlFlowV2LinkedProgramV1 chainImage = Link(chain);
        ManagedSourceBodyWorldResultV1 chainSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", chainRequest.Documents[0].Text + chainLibraryText)], references),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
        if (!Codes(chainImage).SequenceEqual(Codes(Link(chainSingle))) ||
            !chainImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(Compile(chainRequest)).RestrictedImage!.PackageBytes))
            throw new Exception("External transitive helper chain single-module/image repeat oracle failed");
        foreach (string leaf in new[] { "{ System.GC.Collect(); return x; }", "{ return Identity(x); }", "{ return x + State; }" }) {
            ManagedSourceBodyWorldResultV1 deniedLibrary = Library(chainLibraryText.Replace("private static long Leaf(long x) => x;", "private static long State; private static long Leaf(long x) " + leaf));
            ManagedSourceBodyWorldResultV1 denied = Compile(chainRequest with { ReferenceImages = [..references, deniedLibrary.Source.Pe!] });
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("Transitive helper proof ignored descendant effect/cycle/memory");
        }
        Console.WriteLine("PASS bounded transitive local/external helper chains and shared descendants -> scalar/single-module bytes, repeat/maps/host; descendant effects/cycles/memory fail closed");
        foreach (int depth in new[] { 64, 65 }) {
            string helpers = string.Join(' ', Enumerable.Range(0, depth).Select(i => "private static long H" + i + "(long x) => " +
                (i + 1 == depth ? "x;" : "H" + (i + 1) + "(x);")));
            CompanionSourceRequestV1 deepRequest = calledRequest with { Documents = [new("/src/Primary.cs", calledRequest.Documents[0].Text
                .Replace("public static class Fixture {", "public static class Fixture { " + helpers)
                .Replace("new WideValue(Identity(input))", "new WideValue(H0(input))"))] };
            ManagedSourceBodyWorldResultV1 deep = ManagedSourceCompanionV1.CompileBodyWorld(deepRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with { MaximumAcyclicCallDepth = 128 }),
                [new("Fixture", "Read")], [4], [0]);
            if (depth == 64) Link(deep);
            else if (deep.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted ||
                !deep.Compilation.Diagnostics.Any(d => d.Code == "HCSCF-BUDGET2002"))
                throw new Exception("Helper proof depth64/65 boundary failed: " + string.Join(';', deep.Compilation?.Diagnostics ?? []));
        }
        foreach (string checkedArgument in new[] { "input / Identity(input)", "input % Identity(input)", "(long)((ulong)input / (ulong)Identity(input))", "(long)((ulong)input % (ulong)Identity(input))",
            "(long)((int)input / (int)Identity(input))", "(long)((int)input % (int)Identity(input))", "(long)((uint)input / (uint)Identity(input))", "(long)((uint)input % (uint)Identity(input))" })
        foreach (int callBudget in new[] { 1, 2 }) {
                CompanionSourceRequestV1 checkedRequest = calledRequest with { Documents = [new("/src/Primary.cs", calledRequest.Documents[0].Text
                .Replace("new WideValue(Identity(input))", "new WideValue(" + checkedArgument + ")"))] };
                ManagedSourceBodyWorldResultV1 checkedResult = ManagedSourceCompanionV1.CompileBodyWorld(checkedRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = callBudget }), [new("Fixture", "Read")], [4], [0]);
            if (callBudget == 2 ? checkedResult.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                checkedResult.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted ||
                !checkedResult.Compilation.Diagnostics.Any(d => d.Code == "HCCIL2007"))
                throw new Exception("Checked constructor argument call budget precedence changed: " + callBudget + "; " + string.Join(';', checkedResult.Compilation?.Diagnostics ?? []));
        }
        foreach (string helperExpression in new[] { "x / 3", "x % 3", "(long)((uint)x % 3U)", "Leaf(x) / 3", "UpdatedLeaf(x)" }) {
            string methods = "public static long Identity(long x) => " + helperExpression + "; private static long Leaf(long x) => x % 3; private static long UpdatedLeaf(long x) { x += 3; return x; }";
            ManagedSourceBodyWorldResultV1 mathLibrary = Library(library.Replace("public static class WideFactory {", "public static class WideFactory { " + methods));
            CompanionSourceRequestV1 mathRequest = externalHelperRequest with { ReferenceImages = [..references, mathLibrary.Source.Pe!] };
            ManagedSourceBodyWorldResultV1 math = Compile(mathRequest); ScalarControlFlowV2LinkedProgramV1 mathImage = Link(math); ManagedSourceBodyWorldResultV1 mathAgain = Compile(mathRequest);
            ManagedSourceBodyWorldResultV1 mathSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", mathRequest.Documents[0].Text +
                library.Replace("public static class WideFactory {", "public static class WideFactory { " + methods))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 mathScalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", mathRequest.Documents[0].Text
                .Replace("new WideValue(WideFactory.Identity(input))", "WideFactory.Identity(input)").Replace("value.Value", "value").Replace("alias.Value", "alias") +
                " public static class WideFactory { " + methods + " }")], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ScalarControlFlowV2LinkedProgramV1 scalarMathImage = Link(mathScalar);
            if (!Codes(mathImage).SequenceEqual(Codes(Link(mathSingle))) ||
                mathImage.MethodObjects.Where(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(") || Name(m.MethodIdentity).StartsWith("WideFactory."))
                    .Any(m => scalarMathImage.MethodObjects.Single(s => Name(s.MethodIdentity) == Name(m.MethodIdentity)).CodeSha256 != m.CodeSha256) ||
                !math.Source.Pe!.SequenceEqual(mathAgain.Source.Pe!) || !math.Source.Pdb!.SequenceEqual(mathAgain.Source.Pdb!) ||
                !mathImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(mathAgain).RestrictedImage!.PackageBytes) ||
                math.SourceMaps.Any(row => mathAgain.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("External pure math helper scalar/single-module/repeat/maps oracle");
            AssemblyLoadContext mathContext = new System.Runtime.Loader.AssemblyLoadContext("external-pure-math", isCollectible: true);
            try {
                mathContext.LoadFromStream(new MemoryStream(mathLibrary.Source.Pe!));
                Assembly actualMathHost = mathContext.LoadFromStream(new MemoryStream(math.Source.Pe!)); Assembly expectedMathHost = System.Reflection.Assembly.Load(mathScalar.Source.Pe!);
                foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                    if (!Equals(actualMathHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input]), expectedMathHost.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [input])))
                        throw new Exception("External pure math helper split-host boundary oracle");
            } finally { mathContext.Unload(); }
        }
        foreach (string expression in new[] { "x / -1", "x % -1", "x / x", "(long)((ulong)x / (ulong)x)", "(long)((uint)x % (uint)x)" }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(calledRequest with { Documents = [new("/src/Primary.cs", calledRequest.Documents[0].Text
                .Replace("private static long Identity(long x) => x;", "private static long Identity(long x) => " + expression + ";"))] });
            if (denied.Source.Pe is null || denied.Compilation?.Status != RestrictedCilImportStatusV1.Unsupported ||
                !denied.Compilation.Diagnostics.Any(d => d.Code == "HCCIL1844"))
                throw new Exception("Helper-body division/remainder gate changed: " + expression + "; " + string.Join(';', denied.Compilation?.Diagnostics ?? []));
        }
        Console.WriteLine("PASS proven-safe division/remainder and exact checked Int64 division/remainder constructor arguments -> scalar/single-module encoded bytes, host result/fault boundaries and repeat/maps; other checked-helper negatives");
        Console.WriteLine("PASS checked unsigned Int64 division/remainder: exact zero-only I8 probe, full-width DIVU/REMU with result dependency, scalar/single-module bytes, host faults and repeat/maps");
        Console.WriteLine("PASS exact checked signed I4 div/rem and unsigned I4 div constructor arguments: helper ABI/transfer, signed/unsigned I8 extension, scalar bytes, host faults and repeat/maps");
        Console.WriteLine("PASS dynamic unsigned I4 remainder: checked U4 zero probe, ADDW dependency and REMUW, exact helper/extension/encoded/host/repeat/maps/budget controls");
        Console.WriteLine("PASS pure helper-body proven-safe signed/unsigned I4/I8 division/remainder: existing V2 proof, local/external/transitive/conditional scalar bytes, host/repeat/maps; runtime/fault helper bodies fail closed");
        foreach ((string? declaration, string? argument, string? tail) in new[] {
            ("long assigned; int other = (int)input;", "assigned = input + 1", "assigned + other"),
            ("int assigned;", "(long)(assigned = (int)input + 1)", "assigned"),
            ("long assigned = input;", "assigned += 3", "assigned"),
            ("long assigned; long second;", "assigned = second = input + 1", "assigned + second"),
            ("long assigned;", "input < 0 ? (assigned = -input) : (assigned = input + 1)", "assigned"),
            ("long assigned;", "(assigned = input + 1) + assigned", "assigned"),
            ("long assigned;", "assigned = input / Identity(input)", "assigned"),
            ("long a=input+1,b=input+2,c=input+3,d=input+4,assigned;", "assigned = input * 3", "assigned + a + b + c + d") }) {
            CompanionSourceRequestV1 assignmentRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text.Replace(
                "var value = new WideValue(input); return value.Value;", declaration + " var value = new WideValue(" + argument +
                "); var alias = value; return value.Value + alias.Value + " + tail + ";")
                .Replace("public static class Fixture {", "public static class Fixture { private static long Identity(long x) => x;"))] };
            ManagedSourceBodyWorldResultV1 assigned = Compile(assignmentRequest); ScalarControlFlowV2LinkedProgramV1 assignedImage = Link(assigned); ManagedSourceBodyWorldResultV1 assignedAgain = Compile(assignmentRequest);
            ManagedSourceBodyWorldResultV1 assignedSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", assignmentRequest.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 assignedScalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", assignmentRequest.Documents[0].Text
                .Replace("new WideValue(" + argument + ")", "(" + argument + ")").Replace("value.Value", "value").Replace("alias.Value", "alias"))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            if (!Codes(assignedImage).SequenceEqual(Codes(Link(assignedSingle))) ||
                assignedImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 !=
                    Link(assignedScalar).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
                !assigned.Source.Pe!.SequenceEqual(assignedAgain.Source.Pe!) || !assigned.Source.Pdb!.SequenceEqual(assignedAgain.Source.Pdb!) ||
                !assignedImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(assignedAgain).RestrictedImage!.PackageBytes) ||
                assigned.SourceMaps.Any(row => assignedAgain.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("Primitive assignment argument scalar/single-module/repeat/maps oracle");
            AssemblyLoadContext assignmentContext = new System.Runtime.Loader.AssemblyLoadContext("assignment-argument", isCollectible: true);
            try {
                assignmentContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                Assembly actualHost = assignmentContext.LoadFromStream(new MemoryStream(assigned.Source.Pe!)); Assembly expectedHost = System.Reflection.Assembly.Load(assignedScalar.Source.Pe!);
                static object? Outcome(System.Reflection.MethodInfo method, long input) {
                    try { return method.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { return fault.InnerException!.GetType(); }
                }
                foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                    if (!Equals(Outcome(actualHost.GetType("Fixture")!.GetMethod("Read")!, input), Outcome(expectedHost.GetType("Fixture")!.GetMethod("Read")!, input)))
                        throw new Exception("Primitive assignment argument host result/fault oracle");
            } finally { assignmentContext.Unload(); }
            if (argument == "assigned = input + 1") {
                foreach (byte store in new byte[] { 0x0b, 0x0c }) {
                    byte[] changed = assigned.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader(); MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        MethodBodyBlock body = pe.GetMethodBody(read.RelativeVirtualAddress); byte[] il = body.GetILBytes()!;
                        byte[] signature = md.GetBlobBytes(md.GetStandaloneSignature(body.LocalSignature).Signature);
                        Console.WriteLine("ASSIGNMENT-IL signature=" + Convert.ToHexString(signature) + "; maxStack=" + body.MaxStack + "; IL=" + Convert.ToHexString(il));
                        if (signature[2] != 0x0a || signature[3] != 0x08) throw new Exception("Assignment negative local layout changed");
                        int duplicate = Enumerable.Range(0, il.Length - 1).Single(i => il[i] == 0x25 && il[i + 1] == 0x0a);
                        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                        int start = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
                        int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                        changed[start + header + duplicate + 1] = store;
                    }
                    ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "assignment-store-negative",
                        [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0)
                        throw new Exception("Assignment argument bypassed primitive-width/nominal store check");
                }
            }
        }
        Console.WriteLine("PASS primitive I4/I8 local assignment/chained/compound/conditional arguments: typed stores, scalar/single-module bytes, host faults, repeat/maps; width/nominal PE controls");
        foreach (string argument in new[] { "input += 1", "input++", "++input", "input *= 3", "input = (int)input + 1",
            "input < 0 ? (input = -input) : (input += 1)", "(input += 1) + (input += 2)", "input = input / Identity(input)" }) {
            CompanionSourceRequestV1 mutationRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text.Replace(
                "Read(long input)", "Read(long input, int word)").Replace(
                "var value = new WideValue(input); return value.Value;", "var value = new WideValue(" + argument +
                "); var alias = value; return value.Value + alias.Value + input + word;")
                .Replace("public static class Fixture {", "public static class Fixture { private static long Identity(long x) => x;"))] };
            ManagedSourceBodyWorldResultV1 mutated = Compile(mutationRequest); ScalarControlFlowV2LinkedProgramV1 mutationImage = Link(mutated); ManagedSourceBodyWorldResultV1 mutationAgain = Compile(mutationRequest);
            ManagedSourceBodyWorldResultV1 mutationSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", mutationRequest.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 mutationScalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", mutationRequest.Documents[0].Text
                .Replace("new WideValue(" + argument + ")", "(" + argument + ")").Replace("value.Value", "value").Replace("alias.Value", "alias"))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            if (!Codes(mutationImage).SequenceEqual(Codes(Link(mutationSingle))) ||
                mutationImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 !=
                    Link(mutationScalar).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
                !mutated.Source.Pe!.SequenceEqual(mutationAgain.Source.Pe!) || !mutated.Source.Pdb!.SequenceEqual(mutationAgain.Source.Pdb!) ||
                !mutationImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(mutationAgain).RestrictedImage!.PackageBytes) ||
                mutated.SourceMaps.Any(row => mutationAgain.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("Parameter assignment scalar/single-module/repeat/maps oracle");
            AssemblyLoadContext mutationContext = new System.Runtime.Loader.AssemblyLoadContext("parameter-update-argument", isCollectible: true);
            try {
                mutationContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                Assembly actualHost = mutationContext.LoadFromStream(new MemoryStream(mutated.Source.Pe!)); Assembly expectedHost = System.Reflection.Assembly.Load(mutationScalar.Source.Pe!);
                static object? Outcome(System.Reflection.MethodInfo method, long input) {
                    try { return method.Invoke(null, [input, unchecked((int)input)]); } catch (System.Reflection.TargetInvocationException fault) { return fault.InnerException!.GetType(); }
                }
                foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                    if (!Equals(Outcome(actualHost.GetType("Fixture")!.GetMethod("Read")!, input), Outcome(expectedHost.GetType("Fixture")!.GetMethod("Read")!, input)))
                        throw new Exception("Parameter assignment host result/fault oracle");
            } finally { mutationContext.Unload(); }
            if (argument == "input += 1") {
                foreach (byte target in new byte[] { 1, 2 }) {
                    byte[] changed = mutated.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader(); MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        MethodBodyBlock body = pe.GetMethodBody(read.RelativeVirtualAddress); byte[] il = body.GetILBytes()!;
                        Console.WriteLine("PARAMETER-ASSIGNMENT-IL signature=" + Convert.ToHexString(md.GetBlobBytes(read.Signature)) + "; maxStack=" + body.MaxStack + "; IL=" + Convert.ToHexString(il));
                        int store = Enumerable.Range(0, il.Length - 2).Single(i => il[i] == 0x25 && il[i + 1] == 0x10 && il[i + 2] == 0);
                        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                        int start = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
                        int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                        changed[start + header + store + 2] = target;
                    }
                    ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "parameter-store-negative",
                        [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0)
                        throw new Exception("Parameter assignment bypassed width/index check");
                }
            }
        }
        Console.WriteLine("PASS primitive parameter assignment/prefix/postfix/compound/conditional SSA in constructor arguments and pure helpers; scalar/single-module bytes, host/repeat/maps and typed index/width controls");
        foreach (string element in new[] { "int", "uint", "long", "ulong", "byte", "sbyte", "short", "ushort", "char", "bool" })
        foreach ((string? prefix, string? rawArgument) in new[] { ("", "(long)values[0]"), ("", "(long)values[index]"),
            ("", "(long)values[index] + values[index + 1]"), ("", "index < 0 ? (long)values[0] : values[index]"),
            ("int[] source = values;", "(long)source[index]"), ("int[] source = values;", "(long)source[index] + source[0]"),
            ("", "(long)values[Index(index)]"), ("", "(long)values.Length"), ("int[] source = values;", "(long)source.Length"),
            ("", "(long)values.Length + values.Length"), ("", "index < 0 ? 0L : (long)values.Length"),
            ("", "values.Length == 0 ? 0L : (long)values[values.Length - 1]") }) {
            byte elementCode = element switch { "int" => 8, "uint" => 9, "long" => 0x0a, "ulong" => 0x0b, "byte" => 5, "sbyte" => 4, "short" => 6, "ushort" => 7, "char" => 3, "bool" => 2, _ => throw new Exception() };
            byte loadCode = element switch { "int" => 0x94, "uint" => 0x95, "long" or "ulong" => 0x96, "byte" or "bool" => 0x91, "sbyte" => 0x90, "short" => 0x92, _ => 0x93 };
            string argument = element == "ulong" ? rawArgument.Replace("+ values[index + 1]", "+ (long)values[index + 1]")
                .Replace(": values[index]", ": (long)values[index]").Replace("+ source[0]", "+ (long)source[0]") : rawArgument;
            if (element == "bool") argument = rawArgument switch {
                "(long)values[0]" => "values[0] ? 1L : 0L",
                "(long)values[index]" => "values[index] ? 1L : 0L",
                "(long)values[index] + values[index + 1]" => "(values[index] ? 1L : 0L) + (values[index + 1] ? 1L : 0L)",
                "index < 0 ? (long)values[0] : values[index]" => "(index < 0 ? values[0] : values[index]) ? 1L : 0L",
                "(long)source[index]" => "source[index] ? 1L : 0L",
                "(long)source[index] + source[0]" => "(source[index] ? 1L : 0L) + (source[0] ? 1L : 0L)",
                "(long)values[Index(index)]" => "values[Index(index)] ? 1L : 0L",
                "values.Length == 0 ? 0L : (long)values[values.Length - 1]" => "values.Length == 0 ? 0L : values[values.Length - 1] ? 1L : 0L",
                _ => rawArgument };
                CompanionSourceRequestV1 indexedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(" + element + "[] values, int index)")
                .Replace("var value = new WideValue(input); return value.Value;", prefix.Replace("int[]", element + "[]") + " var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;")
                .Replace("public static class Fixture {", "public static class Fixture { private static int Index(int x) => x;"))] };
                ManagedSourceBodyWorldResultV1 indexed = Compile(indexedRequest); ScalarControlFlowV2LinkedProgramV1 indexedImage = Link(indexed); ManagedSourceBodyWorldResultV1 indexedAgain = Compile(indexedRequest);
                ManagedSourceBodyWorldResultV1 indexedSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", indexedRequest.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
                ManagedSourceBodyWorldResultV1 indexedScalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", indexedRequest.Documents[0].Text
                .Replace("new WideValue(" + argument + ")", "(" + argument + ")").Replace("value.Value", "value").Replace("alias.Value", "alias"))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
                RestrictedCilImportResultV1 read = indexed.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import;
                ScalarControlFlowV2MethodObjectV1 readObject = indexedImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read("));
            if (element == "uint" && argument == "(long)values[index]" &&
                (!read.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SLLI && i.Immediate == 32) ||
                 !read.Program.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SRLI && i.Immediate == 32)))
                throw new Exception("Unsigned array element lost exact 32-bit zero extension");
            string helperSuffix = element switch { "long" or "ulong" => "i8", "byte" or "sbyte" or "bool" => "u1", "short" => "i2", "ushort" or "char" => "u2", _ => "i4" };
            string arrayHelper = argument.Contains(".Length") ? "__hybridcpu_managed_array_length" : "__hybridcpu_managed_array_load_" + helperSuffix;
            string arrayModule = argument.Contains(".Length") ? "hybridcpu.managed-runtime.array-length/v1" : "hybridcpu.managed-runtime.array-load-" + helperSuffix + "/v1";
            if (element == "sbyte" && argument == "(long)values[index]" &&
                (!read.Program!.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SLLI && i.Immediate == 56) ||
                 !read.Program.Instructions.Any(i => i.Opcode == HybridCpuOpcode.SRAI && i.Immediate == 56)))
                throw new Exception("Signed byte array lost exact I1 extension");
            if (!read.RequiresNativeExceptionTransfer || read.ReceiverCallerStoragePlans?.Count > 0 ||
                !read.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == arrayHelper && i.Annotation.IsManagedGcSafepoint) ||
                !readObject.ObjectArtifact.Relocations.Any(r => r.TargetSymbol == arrayHelper) ||
                indexedImage.LinkedRuntimeModuleIdentities?.Contains(arrayModule) != true)
                throw new Exception("Array constructor argument lost exact helper/transfer/lifetime");
            if (!Codes(indexedImage).SequenceEqual(Codes(Link(indexedSingle))) ||
                readObject.CodeSha256 != Link(indexedScalar).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
                !indexed.Source.Pe!.SequenceEqual(indexedAgain.Source.Pe!) || !indexed.Source.Pdb!.SequenceEqual(indexedAgain.Source.Pdb!) ||
                !indexedImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(indexedAgain).RestrictedImage!.PackageBytes) ||
                indexed.SourceMaps.Any(row => indexedAgain.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("Array constructor scalar/single-module/repeat/maps oracle");
            byte[] gc = readObject.GcInfo ?? []; int gcOffset = 12; bool objectRoot = false;
            if (gc.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(gc) != 0x474d4348 || BinaryPrimitives.ReadInt32LittleEndian(gc.AsSpan(8)) == 0)
                throw new Exception("Array argument requires GC safepoint metadata");
            for (int record = 0; record < BinaryPrimitives.ReadInt32LittleEndian(gc.AsSpan(8)); record++) {
                int roots = BinaryPrimitives.ReadUInt16LittleEndian(gc.AsSpan(gcOffset + 6)); gcOffset += 8;
                for (int root = 0; root < roots; root++, gcOffset += 16) {
                    if (gc[gcOffset + 8] != 0) throw new Exception("Array argument introduced byref/interior GC root");
                    objectRoot = true;
                }
            }
            if (!objectRoot || gcOffset != gc.Length) throw new Exception("Array argument GC root coverage/encoding");
                AssemblyLoadContext indexedContext = new System.Runtime.Loader.AssemblyLoadContext("array-argument", isCollectible: true);
            try {
                indexedContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                    Assembly actualHost = indexedContext.LoadFromStream(new MemoryStream(indexed.Source.Pe!)); Assembly expectedHost = System.Reflection.Assembly.Load(indexedScalar.Source.Pe!);
                static object? Outcome(System.Reflection.MethodInfo method, Array? values, int index) {
                    try { return method.Invoke(null, [values, index]); } catch (System.Reflection.TargetInvocationException fault) { return fault.InnerException!.GetType(); }
                }
                foreach (Array? values in element == "int" ? new Array?[] { null, Array.Empty<int>(), new int[] { 7 }, new int[] { int.MinValue, -1, 0, 1, int.MaxValue } }
                    : element == "uint" ? new Array?[] { null, Array.Empty<uint>(), new uint[] { 7 }, new uint[] { 0, 1, int.MaxValue, 0x80000000u, uint.MaxValue } }
                    : element == "long" ? new Array?[] { null, Array.Empty<long>(), new long[] { 7 }, new long[] { long.MinValue, -1, 0, 1, long.MaxValue } }
                    : element == "ulong" ? new Array?[] { null, Array.Empty<ulong>(), new ulong[] { 7 }, new ulong[] { 0, 1, long.MaxValue, 0x8000000000000000UL, ulong.MaxValue } }
                    : element == "byte" ? new Array?[] { null, Array.Empty<byte>(), new byte[] { 7 }, new byte[] { 0, 1, 127, 128, 255 } }
                    : element == "sbyte" ? new Array?[] { null, Array.Empty<sbyte>(), new sbyte[] { 7 }, new sbyte[] { -128, -1, 0, 1, 127 } }
                    : element == "short" ? new Array?[] { null, Array.Empty<short>(), new short[] { 7 }, new short[] { -32768, -1, 0, 1, 32767 } }
                    : element == "ushort" ? new Array?[] { null, Array.Empty<ushort>(), new ushort[] { 7 }, new ushort[] { 0, 1, 32767, 32768, 65535 } }
                    : element == "char" ? new Array?[] { null, Array.Empty<char>(), new char[] { (char)7 }, new char[] { (char)0, (char)1, (char)32767, (char)32768, (char)65535 } }
                    : new Array?[] { null, Array.Empty<bool>(), new bool[] { true }, new bool[] { false, true, false, true, true } })
                    foreach (int index in new[] { int.MinValue, -1, 0, 1, 4, 5, int.MaxValue })
                        if (!Equals(Outcome(actualHost.GetType("Fixture")!.GetMethod("Read")!, values, index), Outcome(expectedHost.GetType("Fixture")!.GetMethod("Read")!, values, index)))
                            throw new Exception("Array argument null/bounds/value host oracle");
            } finally { indexedContext.Unload(); }
            if (rawArgument is "(long)values[index]" or "(long)values.Length") {
                    ScalarControlFlowV2LinkedProgramV1 missing = new ScalarControlFlowV2ObjectLinkerV1().Link(indexed.Compilation with { TypeUniverse = null }, indexed.Compilation.Graph!.RootIdentities.Single());
                string exceptionCode = argument.Contains(".Length") ? "HCSCF-LINK4029" : helperSuffix switch { "u1" => "HCSCF-LINK4035", "i2" => "HCSCF-LINK4039", "u2" => "HCSCF-LINK4040", _ => "HCSCF-LINK4027" };
                if (missing.Status != ScalarControlFlowV2LinkStatusV1.BackendRejected || !missing.Diagnostics.Any(d => d.Code == exceptionCode))
                    throw new Exception("Array argument exception-type image gate changed");
                foreach (int limit in new[] { 0, 1 }) {
                        ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(indexedRequest, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = limit }), [new("Fixture", "Read")], [4], [0]);
                    if (limit == 1 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                        budgeted.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted || !budgeted.Compilation.Diagnostics.Any(d => d.Code == "HCCIL2007"))
                        throw new Exception("Array argument call budget precedence");
                }
                foreach (int mutation in argument.Contains(".Length") ? new[] { 0 } : new[] { 0, 1, 2, 3 }) {
                    byte[] changed = indexed.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                            MetadataReader md = pe.GetMetadataReader(); MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        byte[] signature = md.GetBlobBytes(method.Signature); MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress); byte[] il = body.GetILBytes()!;
                        Console.WriteLine("ARRAY-ARGUMENT-IL signature=" + Convert.ToHexString(signature) + "; maxStack=" + body.MaxStack + "; IL=" + Convert.ToHexString(il));
                        if (!signature.SequenceEqual(new byte[] { 0, 2, 0x0a, 0x1d, elementCode, 8 })) throw new Exception("Array argument signature fixture changed");
                        if (mutation < 2) {
                            int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(method.Signature);
                            if (changed[blob] != signature.Length) throw new Exception("Array signature blob prefix");
                            changed[blob + 1 + (mutation == 0 ? 4 : 5)] = mutation == 0 ? (argument.Contains(".Length") ? (byte)0x0e : element switch { "int" => (byte)9, "sbyte" => (byte)5, "byte" or "bool" => (byte)4, "short" => (byte)7, "ushort" or "char" => (byte)6, _ => (byte)8 }) : (byte)0x0a;
                        } else {
                            int load = Array.IndexOf(il, loadCode); if (load < 0) throw new Exception("Array load fixture changed");
                                SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                            int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                            int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                            changed[start + header + load] = mutation == 2 ? (byte)0x9a : element switch { "long" or "ulong" => (byte)0x9f, "byte" or "sbyte" or "bool" => (byte)0x9c, "short" or "ushort" or "char" => (byte)0x9d, _ => (byte)0x9e };
                        }
                    }
                        ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "array-argument-negative", [new("Fixture", "Read")], [],
                        DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Array argument widened carrier/index/reference/write gate");
                }
            }
        }
        Console.WriteLine("PASS exact int[] parameter/local element constructor arguments: checked helper/null/bounds/GC, scalar/single-module bytes, host/repeat/maps and declaration/index/ref/write/budget controls");
        Console.WriteLine("PASS exact int[] Length constructor arguments: checked helper/null/GC, native-width conversion, local/repeated/conditional/element composition, scalar/host/repeat/maps and gates");
        Console.WriteLine("PASS exact uint[] element/Length constructor arguments: unsigned widening, checked helper/GC, scalar/single-module bytes, host boundaries, repeat/maps/image and carrier/index/ref/write/budget controls");
        ManagedSourceBodyWorldResultV1 VerifyMixed(CompanionSourceRequestV1 input, string argument, IEnumerable<object?[]> samples, bool expectCheckedReferences = true, bool? expectObjectRoots = null, string enumDeclarations = "", byte[]? hostLibrary = null) {
            ManagedSourceBodyWorldResultV1 actual = Compile(input); ScalarControlFlowV2LinkedProgramV1 linked = Link(actual); ManagedSourceBodyWorldResultV1 again = Compile(input);
            ManagedSourceBodyWorldResultV1 owned = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", input.Documents[0].Text + library + enumDeclarations)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 scalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", input.Documents[0].Text
                .Replace("new WideValue(" + argument + ")", "(" + argument + ")").Replace("value.Value", "value").Replace("alias.Value", "alias") + enumDeclarations)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            RestrictedCilImportResultV1 read = actual.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import;
            ScalarControlFlowV2MethodObjectV1 obj = linked.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read("));
            if (read.RequiresNativeExceptionTransfer != expectCheckedReferences || read.ReceiverCallerStoragePlans?.Count > 0 ||
                !Codes(linked).SequenceEqual(Codes(Link(owned))) || obj.CodeSha256 != Link(scalar).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
                !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(Link(again).RestrictedImage!.PackageBytes) || actual.SourceMaps.Count != again.SourceMaps.Count ||
                actual.SourceMaps.Any(row => again.SourceMaps[row.Key].Digest != row.Value.Digest)) {
                Console.WriteLine("REFERENCE-ORACLE argument=" + argument + "; nativeEH=" + read.RequiresNativeExceptionTransfer + "; receiver=" + read.ReceiverCallerStoragePlans?.Count +
                    "; single=" + Codes(linked).SequenceEqual(Codes(Link(owned))) + "; scalar=" + (obj.CodeSha256 == Link(scalar).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256));
                foreach ((string? label, ManagedSourceCompanionResultV1? source) in new[] { ("actual", actual.Source), ("scalar", scalar.Source) }) {
                    using PEReader debugPe = new PEReader(new MemoryStream(source.Pe!)); MetadataReader debugMd = debugPe.GetMetadataReader();
                    MethodDefinition debugMethod = debugMd.GetMethodDefinition(debugMd.MethodDefinitions.Single(h => debugMd.GetString(debugMd.GetMethodDefinition(h).Name) == "Read"));
                    Console.WriteLine("REFERENCE-ORACLE-IL " + label + "=" + Convert.ToHexString(debugPe.GetMethodBody(debugMethod.RelativeVirtualAddress).GetILBytes()!));
                }
                throw new Exception("Mixed reference scalar/single-module/repeat/maps/lifetime oracle");
            }
            byte[] gc = obj.GcInfo ?? []; int offset = 12; bool root = false;
            if (gc.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(gc) != 0x474d4348) throw new Exception("Mixed reference GC metadata");
            for (int record = 0; record < BinaryPrimitives.ReadInt32LittleEndian(gc.AsSpan(8)); record++) {
                int count = BinaryPrimitives.ReadUInt16LittleEndian(gc.AsSpan(offset + 6)); offset += 8;
                for (int i = 0; i < count; i++, offset += 16) { if (gc[offset + 8] != 0) throw new Exception("Mixed reference interior root"); root = true; }
            }
            if (root != (expectObjectRoots ?? expectCheckedReferences) || offset != gc.Length) throw new Exception("Mixed reference GC coverage");
            AssemblyLoadContext mixedContext = new System.Runtime.Loader.AssemblyLoadContext("mixed-reference-argument", isCollectible: true);
            try {
                mixedContext.LoadFromStream(new MemoryStream(hostLibrary ?? definition.Source.Pe!));
                Assembly host = mixedContext.LoadFromStream(new MemoryStream(actual.Source.Pe!)); Assembly expected = System.Reflection.Assembly.Load(scalar.Source.Pe!);
                static object? Outcome(System.Reflection.MethodInfo method, object?[] args) {
                    try { return method.Invoke(null, args); } catch (System.Reflection.TargetInvocationException fault) { return fault.InnerException!.GetType(); }
                }
                foreach (object?[] args in samples)
                    if (!Equals(Outcome(host.GetType("Fixture")!.GetMethod("Read")!, args), Outcome(expected.GetType("Fixture")!.GetMethod("Read")!, args)))
                        throw new Exception("Mixed reference host value/fault oracle");
            } finally { mixedContext.Unload(); }
            return actual;
        }
        foreach (string argument in new[] { "(long)values[0] + other[0]", "(long)(index < 0 ? values.Length : other.Length)" }) {
            CompanionSourceRequestV1 mixedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(uint[] values, int[] other, int index)")
                .Replace("var value = new WideValue(input); return value.Value;", "var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
            VerifyMixed(mixedRequest, argument, from uint[]? left in new uint[]?[] { null, [], [0, uint.MaxValue] }
                from int[]? right in new int[]?[] { null, [], [int.MinValue, int.MaxValue] } from int index in new[] { -1, 0, 1, 2, int.MaxValue } select new object?[] { left, right, index });
        }
        Console.WriteLine("PASS exact long[] element/Length constructor arguments: full I8 helper/GC/exception, scalar/single-module bytes, host boundaries, repeat/maps/image and carrier/index/ref/write/budget controls");
        Console.WriteLine("PASS exact ulong[] element/Length constructor arguments: distinct UInt64 declaration, I8 bits/unchecked cast, scalar/single-module/host/repeat/maps/image and checked helper gates");
        Console.WriteLine("PASS exact byte/sbyte/short/ushort/char/bool array element/Length constructor arguments: checked helpers/sign/width/logical/GC, scalar/single-module/host/repeat/maps/image and carrier/index/ref/write/budget controls");
        ManagedSourceBodyWorldResultV1 checkedWideArray = Compile(constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
            .Replace("Read(long input)", "Read(ulong[] values)")
            .Replace("var value = new WideValue(input); return value.Value;", "var value = new WideValue(checked((long)values[0])); var alias = value; return value.Value + alias.Value;"))] });
        if (checkedWideArray.Source.Pe is null || checkedWideArray.Compilation?.Status == RestrictedCilImportStatusV1.Success ||
            (checkedWideArray.Compilation?.Diagnostics.Count ?? 0) == 0) throw new Exception("Unchecked array cast admitted checked overflow conversion");
        foreach (string argument in new[] { "(long)values[0] + other[0]", "(long)(index < 0 ? values.Length : other.Length)" }) {
            CompanionSourceRequestV1 mixedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(ulong[] values, long[] other, int index)")
                .Replace("var value = new WideValue(input); return value.Value;", "var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
            VerifyMixed(mixedRequest, argument, from ulong[]? left in new ulong[]?[] { null, [], [0, ulong.MaxValue] }
                from long[]? right in new long[]?[] { null, [], [long.MinValue, long.MaxValue] } from int index in new[] { -1, 0, 1, 2, int.MaxValue } select new object?[] { left, right, index });
        }
        foreach ((string? prefix, string? argument) in new[] { ("", "(long)text.Length"), ("string source = text;", "(long)source.Length"),
            ("", "(long)text.Length + text.Length"), ("", "index < 0 ? 0L : (long)text.Length"),
            ("", "text == null ? 0L : (long)text.Length"), ("", "(long)text.Length + Index(index)"),
            ("string source = text;", "index < 0 ? (long)source.Length : 0L"),
            ("", "(long)text[0]"), ("", "(long)text[index]"), ("string source = text;", "(long)source[index]"),
            ("", "(long)text[index] + text[index + 1]"), ("", "index < 0 ? 0L : (long)text[index]"),
            ("", "text == null ? 0L : (long)text[index]"), ("", "text.Length == 0 ? 0L : (long)text[text.Length - 1]"),
            ("", "(long)text[Index(index)]") }) {
            bool character = argument.Contains('[');
            string stringHelper = character ? "__hybridcpu_managed_string_char" : "__hybridcpu_managed_string_length";
            string stringModule = character ? "hybridcpu.managed-runtime.string-char/v1" : "hybridcpu.managed-runtime.string-length/v1";
            CompanionSourceRequestV1 lengthRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(string text, int index)")
                .Replace("var value = new WideValue(input); return value.Value;", prefix + " var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;")
                .Replace("public static class Fixture {", "public static class Fixture { private static int Index(int x) => x;"))] };
            ManagedSourceBodyWorldResultV1 length = Compile(lengthRequest); ScalarControlFlowV2LinkedProgramV1 lengthImage = Link(length); ManagedSourceBodyWorldResultV1 lengthRepeat = Compile(lengthRequest);
            ManagedSourceBodyWorldResultV1 lengthSingle = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", lengthRequest.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 lengthScalar = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", lengthRequest.Documents[0].Text
                .Replace("new WideValue(" + argument + ")", "(" + argument + ")").Replace("value.Value", "value").Replace("alias.Value", "alias"))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            RestrictedCilImportResultV1 read = length.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import;
            ScalarControlFlowV2MethodObjectV1 readObject = lengthImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read("));
            if (!read.RequiresNativeExceptionTransfer || read.ReceiverCallerStoragePlans?.Count > 0 ||
                !read.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == stringHelper && i.Annotation.IsManagedGcSafepoint) ||
                !readObject.ObjectArtifact.Relocations.Any(r => r.TargetSymbol == stringHelper) ||
                lengthImage.LinkedRuntimeModuleIdentities?.Contains(stringModule) != true)
                throw new Exception("String length lost checked helper/transfer/GC/lifetime");
            if (!Codes(lengthImage).SequenceEqual(Codes(Link(lengthSingle))) ||
                readObject.CodeSha256 != Link(lengthScalar).MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).CodeSha256 ||
                !length.Source.Pe!.SequenceEqual(lengthRepeat.Source.Pe!) || !length.Source.Pdb!.SequenceEqual(lengthRepeat.Source.Pdb!) ||
                !lengthImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(lengthRepeat).RestrictedImage!.PackageBytes) ||
                length.SourceMaps.Any(row => lengthRepeat.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("String length scalar/single-module/repeat/maps oracle");
            byte[] gc = readObject.GcInfo ?? []; int gcOffset = 12; bool objectRoot = false;
            if (gc.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(gc) != 0x474d4348 || BinaryPrimitives.ReadInt32LittleEndian(gc.AsSpan(8)) == 0)
                throw new Exception("String length GC metadata");
            for (int record = 0; record < BinaryPrimitives.ReadInt32LittleEndian(gc.AsSpan(8)); record++) {
                int roots = BinaryPrimitives.ReadUInt16LittleEndian(gc.AsSpan(gcOffset + 6)); gcOffset += 8;
                for (int root = 0; root < roots; root++, gcOffset += 16) {
                    if (gc[gcOffset + 8] != 0) throw new Exception("String length introduced interior/byref root");
                    objectRoot = true;
                }
            }
            if (!objectRoot || gcOffset != gc.Length) throw new Exception("String length GC coverage");
            AssemblyLoadContext lengthContext = new System.Runtime.Loader.AssemblyLoadContext("string-length-argument", isCollectible: true);
            try {
                lengthContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                Assembly actual = lengthContext.LoadFromStream(new MemoryStream(length.Source.Pe!)); Assembly expected = System.Reflection.Assembly.Load(lengthScalar.Source.Pe!);
                static object? Outcome(System.Reflection.MethodInfo method, string? text, int index) {
                    try { return method.Invoke(null, [text, index]); } catch (System.Reflection.TargetInvocationException fault) { return fault.InnerException!.GetType(); }
                }
                foreach (string? text in new[] { null, "", "a", "\ud83d\ude00", "A\0\u0411\ud834\udd1e", new string('x', 257), "\u0000\u7fff\u8000\uffff" })
                    foreach (int index in new[] { int.MinValue, -1, 0, 1, 2, 3, 4, 257, int.MaxValue })
                        if (!Equals(Outcome(actual.GetType("Fixture")!.GetMethod("Read")!, text, index), Outcome(expected.GetType("Fixture")!.GetMethod("Read")!, text, index)))
                            throw new Exception("String length UTF16/null/conditional host oracle");
            } finally { lengthContext.Unload(); }
            if (argument is "(long)text.Length" or "(long)text[index]") {
                ScalarControlFlowV2LinkedProgramV1 missing = new ScalarControlFlowV2ObjectLinkerV1().Link(length.Compilation with { TypeUniverse = null }, length.Compilation.Graph!.RootIdentities.Single());
                if (!missing.Diagnostics.Any(d => d.Code == (character ? "HCSCF-LINK4031" : "HCSCF-LINK4032"))) throw new Exception("String access image exception type gate");
                foreach (int limit in new[] { 0, 1 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(lengthRequest, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = limit }), [new("Fixture", "Read")], [4], [0]);
                    if (limit == 1 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                        budgeted.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted || !budgeted.Compilation.Diagnostics.Any(d => d.Code == "HCCIL2007"))
                        throw new Exception("String length call budget precedence");
                }
                static void RedirectGetterScope(byte[] bytes, string getterName, string assemblyName) {
                    using PEReader pe = new PEReader(new MemoryStream(bytes)); MetadataReader md = pe.GetMetadataReader();
                    MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == getterName));
                    int width = md.GetTableRowSize(TableIndex.TypeRef) - 2 * (md.GetHeapSize(HeapIndex.String) >= 65536 ? 4 : 2);
                    int row = pe.PEHeaders.MetadataStartOffset + md.GetTableMetadataOffset(TableIndex.TypeRef) +
                        (MetadataTokens.GetRowNumber((TypeReferenceHandle)getter.Parent) - 1) * md.GetTableRowSize(TableIndex.TypeRef);
                    uint scope = checked((uint)(MetadataTokens.GetRowNumber(md.AssemblyReferences.Single(h => md.GetString(md.GetAssemblyReference(h).Name) == assemblyName)) << 2) | 2u);
                    if (width == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(row), checked((ushort)scope));
                    else if (width == 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row), scope);
                    else throw new Exception("String TypeRef scope fixture width");
                }
                byte[] rawScope = lengthScalar.Source.Pe!.ToArray();
                RedirectGetterScope(rawScope, character ? "get_Chars" : "get_Length", "HybridCPU.Compiler.Profiles");
                RestrictedCilImportResultV1 rawDenied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(rawScope, new("Fixture", "Read"));
                Console.WriteLine("RAW-STRING-SCOPE status=" + rawDenied.Status + "; codes=" + string.Join(',', rawDenied.Diagnostics.Select(d => d.Code)));
                if (rawDenied.Status != RestrictedCilImportStatusV1.Unsupported || !rawDenied.Diagnostics.Any(d => d.Code == "HCCIL1478"))
                    throw new Exception("Raw string getter accepted a foreign assembly scope");
                foreach (int mutation in character ? new[] { 0, 1, 2, 3, 4, 5 } : new[] { 0, 1, 2, 3, 5 }) {
                    byte[] changed = length.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader(); MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress); byte[] il = body.GetILBytes()!;
                        byte[] signature = md.GetBlobBytes(method.Signature);
                        Console.WriteLine("STRING-ACCESS-IL kind=" + (character ? "Chars" : "Length") + "; signature=" + Convert.ToHexString(signature) + "; maxStack=" + body.MaxStack + "; IL=" + Convert.ToHexString(il));
                        if (!signature.SequenceEqual(new byte[] { 0, 2, 0x0a, 0x0e, 8 })) throw new Exception("String length declaration fixture");
                        if (mutation == 0) {
                            int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(method.Signature);
                            changed[blob + 1 + 3] = 0x1c;
                        } else if (mutation is 1 or 2 or 4 or 5) {
                            MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == (character ? "get_Chars" : "get_Length")));
                            if (mutation is 1 or 4) {
                                int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(getter.Signature);
                                changed[blob + 1 + (mutation == 1 ? 2 : 3)] = 0x0a;
                            } else if (mutation == 2) {
                                TypeReference type = md.GetTypeReference((TypeReferenceHandle)getter.Parent);
                                int name = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.String) + MetadataTokens.GetHeapOffset(type.Name);
                                System.Text.Encoding.UTF8.GetBytes("Object").CopyTo(changed, name);
                            } else {
                                RedirectGetterScope(changed, character ? "get_Chars" : "get_Length", "WideLibrary");
                            }
                        } else {
                            int call = Array.IndexOf(il, (byte)0x6f); if (call < 0) throw new Exception("String length callvirt fixture");
                            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                            int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                            int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                            changed[start + header + call] = 0x8e;
                            Array.Clear(changed, start + header + call + 1, 4);
                        }
                    }
                    ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "string-length-negative", [new("Fixture", "Read")], [],
                        DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("String length declaration/getter/array-op negative");
                }
            }
        }
        Console.WriteLine("PASS exact string Length constructor arguments: parameter/local/conditional/null-skip/UTF16, checked helper/GC/native EH, scalar/single-module/host/repeat/maps/image and declaration/getter/array-op/budget controls");
        Console.WriteLine("PASS exact string character constructor arguments: UTF16 unsigned widening/null/bounds/conditional/Length composition, scalar/single-module/host/repeat/maps/image, getter scope/signature/declaration/array-op/budget controls");
        foreach ((string? prefix, string? argument) in new[] { ("", "(long)values[index] + text.Length"), ("", "(long)text[index] + values[index]"),
            ("byte[] source = values; string name = text;", "(long)source[index] + name.Length"),
            ("", "index < 0 ? (long)text.Length : values[index]"), ("", "text == null ? (long)values[0] : text.Length"),
            ("", "(long)(index < 0 ? values : other)[0] + (index < 0 ? text : alternate).Length"),
            ("", "(long)values[index] + (index < 0 ? text : alternate).Length"),
            ("", "(long)(index < 0 ? values : other)[index] + text.Length") }) {
            CompanionSourceRequestV1 mixedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(byte[] values, string text, int index, byte[] other, string alternate)")
                .Replace("var value = new WideValue(input); return value.Value;", prefix + " var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
            ManagedSourceBodyWorldResultV1 mixed = VerifyMixed(mixedRequest, argument, from byte[]? bytes in new byte[]?[] { null, [], [0, 128, 255] }
                from string? text in new[] { null, "", "\0\u8000\uffff" } from int index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue }
                select new object?[] { bytes, text, index, new byte[] { 7, 255 }, "\ud83d\ude00" });
            if (argument.Contains(" ? values : other") && !mixed.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import.ControlFlowAnalysis!.Phis.Any(p => p.Type == RestrictedCilTypeV1.ObjectReference))
                throw new Exception("Mixed reference oracle did not exercise an object-reference phi");
            if (argument == "(long)values[index] + text.Length") {
                foreach (int limit in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(mixedRequest, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = limit }), [new("Fixture", "Read")], [4], [0]);
                    if (limit == 2 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success : budgeted.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted ||
                        !budgeted.Compilation.Diagnostics.Any(d => d.Code == "HCCIL2007")) throw new Exception("Mixed reference call budget boundary");
                }
            }
            foreach (int mutation in argument == "(long)values[index] + text.Length" ? new[] { 0, 1, 2 } :
                argument.Contains(" ? values : other") ? new[] { 3 } : Array.Empty<int>()) {
                byte[] changed = mixed.Source.Pe!.ToArray();
                using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                    MetadataReader md = pe.GetMetadataReader(); MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                    byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                    Console.WriteLine("MIXED-REFERENCE-IL mutation=" + mutation + "; IL=" + Convert.ToHexString(il));
                    if (mutation == 2) {
                        int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(method.Signature);
                        changed[blob + 1 + 4] = 4;
                    } else {
                        byte opcode = mutation == 0 ? (byte)0x02 : mutation == 1 ? (byte)0x03 : (byte)0x05;
                        int load = mutation == 3 ? Enumerable.Range(0, il.Length - 1).Single(i => il[i] == opcode && il[i + 1] == 0x2b) :
                            mutation == 1 ? Enumerable.Range(0, il.Length - 1).Single(i => il[i] == opcode && il[i + 1] == 0x6f) : 2;
                        if (il[load] != opcode) throw new Exception("Mixed reference producer fixture");
                        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                        int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                        int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                        changed[start + header + load] = mutation == 1 ? (byte)0x02 : (byte)0x03;
                    }
                }
                ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "mixed-reference-negative", [new("Fixture", "Read")], [],
                    DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Per-value reference fact accepted wrong operand/join");
            }
        }
        Console.WriteLine("PASS mixed array/String and signed/unsigned constructor arguments: per-value declaration facts/CFG joins, scalar/single-module/host/repeat/maps/image, GC/consumer/budget negatives");
        foreach ((string? prefix, string? argument, string? suffix) in new[] {
            ("byte[] source = values;", "(long)(source = other)[index] + text.Length", " + source.Length"),
            ("byte[] source = values;", "(long)(source = other).Length + text.Length", " + source.Length"),
            ("string name = text;", "(long)(name = alternate).Length + values[index]", " + name.Length"),
            ("byte[] source = values; byte[] spare = other;", "(long)(source = spare = values)[index] + text.Length", " + source.Length + spare.Length"),
            ("byte[] source = values; string name = text;", "(long)(source = other)[index] + (name = alternate).Length", " + source.Length + name.Length"),
            ("byte[] source = values;", "(long)(source = index < 0 ? values : other)[0] + text.Length", " + source.Length"),
            ("byte[] source = values;", "index < 0 ? (long)(source = values)[0] : (source = other)[0]", " + source.Length"),
            ("", "(long)(values = other)[index] + text.Length", " + values.Length"),
            ("", "(long)(text = alternate).Length + values[index]", " + text.Length"),
            ("byte[] source = values; string name = text;", "(long)(source = values = other)[index] + (name = text = alternate).Length", " + source.Length + name.Length + values.Length + text.Length"),
            ("byte[] a0 = values, a1 = other, a2 = values, a3 = other, a4 = values; string name = text;", "(long)(a4 = other)[index] + (name = alternate).Length", " + a0.Length + a1.Length + a2.Length + a3.Length + a4.Length + name.Length") }) {
            CompanionSourceRequestV1 assignmentRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(byte[] values, string text, int index, byte[] other, string alternate)")
                .Replace("var value = new WideValue(input); return value.Value;", prefix + " var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value" + suffix + ";"))] };
            ManagedSourceBodyWorldResultV1 assigned = VerifyMixed(assignmentRequest, argument, from byte[]? bytes in new byte[]?[] { null, [], [0, 128, 255] }
                from string? text in new[] { null, "", "\0\u8000\uffff" } from int index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue }
                select new object?[] { bytes, text, index, new byte[] { 7, 255 }, "\ud83d\ude00" });
            if (prefix.StartsWith("byte[] a0", StringComparison.Ordinal)) {
                using PEReader pe = new PEReader(new MemoryStream(assigned.Source.Pe!)); MetadataReader md = pe.GetMetadataReader();
                MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                if (!Enumerable.Range(0, il.Length - 3).Any(i => il[i] == 5 && il[i + 1] == 0x25 && il[i + 2] == 0x13 && il[i + 3] == 4))
                    throw new Exception("Reference assignment fixture must exercise stloc.s4");
            }
            if (argument == "(long)(source = other).Length + text.Length") {
                foreach (int mutation in new[] { 0, 1, 2, 3 }) {
                    byte[] changed = assigned.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader(); MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress); byte[] il = body.GetILBytes()!;
                        Console.WriteLine("REFERENCE-STORE-IL mutation=" + mutation + "; locals=" + Convert.ToHexString(md.GetBlobBytes(md.GetStandaloneSignature(body.LocalSignature).Signature)) + "; IL=" + Convert.ToHexString(il));
                        if (mutation == 1) {
                            BlobHandle signature = md.GetStandaloneSignature(body.LocalSignature).Signature;
                            byte[] locals = md.GetBlobBytes(signature); if (locals[0] != 7 || locals[2] != 0x1d || locals[3] != 5) throw new Exception("Reference store local fixture");
                            int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(signature);
                            changed[blob + 1 + 3] = 4;
                        } else {
                            int store = Enumerable.Range(0, il.Length - 2).Single(i => il[i] == 5 && il[i + 1] == 0x25 && il[i + 2] == 0x0a);
                            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                            int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                            int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                            changed[start + header + store + (mutation == 0 ? 0 : 2)] = mutation == 0 ? (byte)3 : mutation == 2 ? (byte)0x0b : (byte)0x0d;
                        }
                    }
                    ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "reference-store-negative", [new("Fixture", "Read")], [],
                        DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Reference store widened declaration/operand/aggregate/index gate");
                }
            }
            if (argument == "(long)(values = other)[index] + text.Length") {
                foreach (int mutation in new[] { 0, 1, 2 }) {
                    byte[] changed = assigned.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader(); MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                        byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                        int store = Enumerable.Range(0, il.Length - 3).Single(i => il[i] == 5 && il[i + 1] == 0x25 && il[i + 2] == 0x10 && il[i + 3] == 0);
                        Console.WriteLine("REFERENCE-PARAMETER-STORE-IL mutation=" + mutation + "; IL=" + Convert.ToHexString(il));
                        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                        int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                        int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                        changed[start + header + store + (mutation == 0 ? 0 : 3)] = mutation == 0 ? (byte)3 : mutation == 1 ? (byte)1 : byte.MaxValue;
                    }
                    ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "reference-parameter-store-negative", [new("Fixture", "Read")], [],
                        DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Reference parameter store widened carrier/index gate");
                }
            }
        }
        Console.WriteLine("PASS exact reference local/parameter/chained/conditional assignments in constructor arguments: existing SSA/facts, observable reuse, scalar/single-module/host/repeat/maps/image and declaration/operand/aggregate/index negatives");
        foreach ((string? argument, string? suffix) in new[] {
            ("(long)(source = index < 0 ? null : (name = null) == null ? null : null).Length", " + (name == null ? 7 : name.Length) + (source == null ? 11 : source.Length)"),
            ("(long)((source = index < 0 ? null : values) ?? other)[0] + text.Length", " + (source == null ? 7 : source.Length)"),
            ("(long)(source = values ?? other)[index] + text.Length", " + (source == null ? 7 : source.Length)"),
            ("(long)(source ??= other)[index] + text.Length", " + (source == null ? 7 : source.Length)"),
            ("(long)(values = values ?? other)[index] + text.Length", " + (values == null ? 7 : values.Length)"),
            ("(long)(name = text ?? alternate).Length + values[index]", " + (name == null ? 7 : name.Length)"),
            ("(long)(name ??= alternate).Length + values[index]", " + (name == null ? 7 : name.Length)"),
            ("(long)(text = index < 0 ? null : alternate).Length + values[index]", " + (text == null ? 7 : text.Length)"),
            ("(long)((source = index < 0 ? values : null) ?? other).Length + (name = index < 0 ? null : text).Length", " + (source == null ? 7 : source.Length)"),
            ("(long)(source = null).Length + text.Length", ""),
            ("(long)(name = null).Length + values[index]", ""),
            ("(long)(source = index < 0 ? null : (index > 1 ? null : values))[0] + text.Length", " + (source == null ? 7 : source.Length)"),
            ("(long)(text?.Length ?? 0)", "") }) {
            CompanionSourceRequestV1 nullableRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(byte[] values, string text, int index, byte[] other, string alternate)")
                .Replace("var value = new WideValue(input); return value.Value;", "byte[] source = values; string name = text; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value" + suffix + ";"))] };
            ManagedSourceBodyWorldResultV1 nullable = VerifyMixed(nullableRequest, argument, from byte[]? bytes in new byte[]?[] { null, [], [0, 128, 255] }
                from string? text in new[] { null, "", "\0\u8000\uffff" } from int index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue }
                from byte[]? other in new byte[]?[] { null, [], [7, 255] } from string? alternate in new[] { null, "", "\ud83d\ude00" }
                select new object?[] { bytes, text, index, other, alternate });
            using PEReader pe = new PEReader(new MemoryStream(nullable.Source.Pe!)); MetadataReader md = pe.GetMetadataReader();
            MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
            Console.WriteLine("NULLABLE-REFERENCE-IL argument=" + argument + "; IL=" + Convert.ToHexString(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!));
            if (argument == "(long)((source = index < 0 ? null : values) ?? other)[0] + text.Length") {
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                int load = Enumerable.Range(0, il.Length - 2).Single(i => il[i] == 0x14 && il[i + 1] == 0x25 && il[i + 2] == 0x0a);
                SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                foreach (bool unknown in new[] { false, true }) {
                    byte[] changed = nullable.Source.Pe!.ToArray();
                    int header = (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                    changed[start + header + load] = 3; // String producer in the byte[]/null branch.
                    if (unknown) {
                        int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(method.Signature);
                        byte[] signature = md.GetBlobBytes(method.Signature);
                        if (signature[5] != 0x0e) throw new Exception("Nullable reference signature fixture");
                        changed[blob + 1 + 5] = 0x1c; // Object must stay unknown, not become Null.
                    }
                    ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-reference-negative", [new("Fixture", "Read")], [],
                        DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
                    Console.WriteLine("NULLABLE-REFERENCE-NEGATIVE unknown=" + unknown + "; status=" + denied.Status + "; diagnostics=" + string.Join(';', denied.Diagnostics.Select(d => d.Code)));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Null widened heterogeneous/unknown carrier join");
                }
            }
        }
        Console.WriteLine("PASS nullable reference local/parameter/conditional/coalescing/coalescing-assignment constructor arguments: exact carrier/null joins, scalar/single-module/host/fault/repeat/maps/image");
        foreach (string prefix in new[] {
            "int? length = text?.Length;",
            "int? length = index < 0 ? null : index;",
            "int? original = index < 0 ? null : index; int? length = original;",
            "int? length = null; if (index != 0) length = index;",
            "int? length = index; if (index < 0) length = null;" }) {
            foreach (string argument in new[] { "(long)(length ?? 0)", "(long)(length ?? -7)", "length.HasValue ? (long)length.GetValueOrDefault() : -7L" }) {
                CompanionSourceRequestV1 nullableRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(byte[] values, string text, int index, byte[] other, string alternate)")
                    .Replace("var value = new WideValue(input); return value.Value;", prefix + " var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                ManagedSourceBodyWorldResultV1 nullable = VerifyMixed(nullableRequest, argument, from string? text in new[] { null, "", "\0\u8000\uffff" }
                    from int index in new[] { int.MinValue, -1, 0, 1, int.MaxValue }
                    select new object?[] { null, text, index, null, null }, expectCheckedReferences: prefix.StartsWith("int? length = text", StringComparison.Ordinal));
                using PEReader pe = new PEReader(new MemoryStream(nullable.Source.Pe!)); MetadataReader md = pe.GetMetadataReader();
                MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                Console.WriteLine("NULLABLE-I4-IL prefix=" + prefix + "; argument=" + argument + "; IL=" + Convert.ToHexString(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!));
            }
        }
        Console.WriteLine("PASS exact Nullable<int> locals/conditional-access/copy/reassignment/HasValue/GetValueOrDefault/coalescing -> source-owned/external/scalar physical bytes, host boundaries/faults, repeat PE/PDB/maps/image");
        foreach (string prefix in new[] { "long? length = text == null ? null : (long)text.Length;",
            "long? length = input < 0 ? null : input;", "long? first = input; long? length = first;",
            "long? length = null; if (input != 0) length = input;", "long? length = input; if (input < 0) length = null;" }) {
            foreach (string argument in new[] { "length ?? 0L", "length.HasValue ? length.GetValueOrDefault() : -7L" }) {
                CompanionSourceRequestV1 wideRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(string text, long input)")
                    .Replace("var value = new WideValue(input); return value.Value;", prefix + " var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(wideRequest, argument, from string? text in new[] { null, "", "\0\u8000\uffff" }
                    from long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }
                    select new object?[] { text, input }, expectCheckedReferences: prefix.Contains("text"));
            }
        }
        Console.WriteLine("PASS exact Nullable<long> locals -> retained Int64 constructor/getter/copies: full I8/presence, scalar/single-module bytes, host boundaries, repeat/maps/image");
        foreach (string width in new[] { "int", "long" }) foreach (string init in new[] { "input", "input < 0 ? null : input" })
            foreach (string expression in new[] { "length.Value", "length.HasValue ? length.Value : 0", "length.Value + length.Value" }) {
                string argument = width == "int" ? "(long)(" + expression + ")" : expression;
                CompanionSourceRequestV1 valueRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(" + width + " input)")
                    .Replace("var value = new WideValue(input); return value.Value;", width + "? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                ManagedSourceBodyWorldResultV1 consumed = VerifyMixed(valueRequest, argument, new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }
                    .Select(input => new object?[] { width == "int" ? (object)unchecked((int)input) : input }), expectObjectRoots: false);
                ScalarControlFlowV2LinkedProgramV1 checkedImage = Link(consumed);
                if (checkedImage.LinkedRuntimeModuleIdentities?.Contains("hybridcpu.managed-runtime.nullable-value-i8-checked/v1") != true ||
                    !checkedImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture.Read(")).ObjectArtifact.Relocations.Any(r => r.TargetSymbol == "__hybridcpu_managed_nullable_value_i8_checked"))
                    throw new Exception("Nullable.Value lost checked runtime helper relocation");
            }
        Console.WriteLine("PASS checked Nullable<int/long>.Value -> retained Int64 consumers, scalar/single-module code, host result/InvalidOperationException, repeat PE/PDB/maps/image and exact helper/GC/native-EH path");
        foreach (string width in new[] { "int", "long" }) foreach (string init in new[] { "input", "input < 0 ? null : input" })
            foreach (string fallback in new[] { "input + 1", "input < 0 ? input - 7 : input + 9", "input / (input - input)" }) {
                string expression = "length.GetValueOrDefault(" + fallback + ")";
                string argument = width == "int" ? "(long)(" + expression + ")" : expression;
                CompanionSourceRequestV1 computedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(" + width + " input)")
                    .Replace("var value = new WideValue(input); return value.Value;", width + "? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(computedRequest, argument, new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }
                    .Select(input => new object?[] { width == "int" ? (object)unchecked((int)input) : input }),
                    expectCheckedReferences: fallback.Contains('/'), expectObjectRoots: false);
            }
        Console.WriteLine("PASS computed Nullable<int/long>.GetValueOrDefault arguments -> retained Int64 constructors; eager arithmetic/forward CFG/division fault, scalar/single-module bytes, host/repeat/maps/image");
        foreach (string width in new[] { "int", "long" }) foreach (string init in new[] { "input", "input < 0 ? null : input" })
            foreach (string getter in new[] { "fallback.Value", "fallback.GetValueOrDefault()", "fallback.HasValue ? fallback.Value : 7", "fallback.Value + input" }) {
                string expression = "length.GetValueOrDefault(" + getter + ")";
                string argument = width == "int" ? "(long)(" + expression + ")" : expression;
                CompanionSourceRequestV1 nestedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(" + width + " input)")
                    .Replace("var value = new WideValue(input); return value.Value;", width + "? length = input; " + width + "? fallback = " + init +
                        "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(nestedRequest, argument, new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }
                    .Select(input => new object?[] { width == "int" ? (object)unchecked((int)input) : input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS nested exact nullable leaf accessors in default arguments -> retained Int64 constructor/image, eager Value fault, scalar/single-module/repeat/maps and host boundaries");
        foreach (string width in new[] { "int", "long" }) foreach (string init in new[] { "input", "input < 0 ? null : input" })
            foreach (string rhs in new[] { "input + 1", "input < 0 ? input - 7 : input + 9", "input / (input - input)" }) {
                string expression = "length.GetValueOrDefault(fallback.GetValueOrDefault(" + rhs + "))";
                string argument = width == "int" ? "(long)(" + expression + ")" : expression;
                CompanionSourceRequestV1 nestedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(" + width + " input)")
                    .Replace("var value = new WideValue(input); return value.Value;", width + "? length = input; " + width + "? fallback = " + init +
                        "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(nestedRequest, argument, new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }
                    .Select(input => new object?[] { width == "int" ? (object)unchecked((int)input) : input }),
                    expectCheckedReferences: rhs.Contains('/'), expectObjectRoots: false);
            }
        Console.WriteLine("PASS nested computed nullable default arguments -> retained Int64 constructor/image, eager faults, scalar/single-module/repeat/maps and host boundaries");
        foreach (string initializer in new[] { "input + 1", "input < 0 ? input - 7 : input + 9", "input += 1", "input / (input - input)" }) {
            const string argument = "(long)length.GetValueOrDefault()";
            CompanionSourceRequestV1 computedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("Read(long input)", "Read(int input)")
                .Replace("var value = new WideValue(input); return value.Value;", "int? length = " + initializer + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value + input;"))] };
            VerifyMixed(computedRequest, argument, new[] { int.MinValue, -1, 0, 1, int.MaxValue }.Select(input => new object?[] { input }),
                expectCheckedReferences: initializer.Contains('/'), expectObjectRoots: false);
        }
        Console.WriteLine("PASS computed Nullable<int> in-place initializer -> retained external/source-owned Int64 constructor, scalar/single-module/repeat/maps/image and eager host faults");
        foreach (string init in new[] { "input + 1U", "input == 0 ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(input + 1U)", "length.HasValue ? length.Value : 0U", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 unsignedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(uint input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "uint? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(unsignedRequest, argument, new uint[] { 0, 1, 0x7fffffffU, 0x80000000U, uint.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<uint> locals -> retained Int64 consumers; full U4 bits/zero extension, scalar/single-module/repeat/maps/image and host fault boundaries");
        foreach (string init in new[] { "input + 1UL", "input == 0 ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(input + 1UL)", "length.HasValue ? length.Value : 0UL", "length.Value" }) {
                string argument = "unchecked((long)(" + getter + "))";
                CompanionSourceRequestV1 unsignedRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(ulong input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ulong? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(unsignedRequest, argument, new ulong[] { 0, 1, 0x7fffffffUL, 0x80000000UL, 0x7fffffffffffffffUL, 0x8000000000000000UL, ulong.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<ulong> locals -> retained Int64 consumers; full U8 payload/presence, scalar/single-module/repeat/maps/image and host fault boundaries");
        foreach (string init in new[] { "unchecked((ushort)(input + 1))", "input == 0 ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ushort)(input + 1)))", "length.HasValue ? length.Value : (ushort)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 narrowRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(ushort input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ushort? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(narrowRequest, argument, new ushort[] { 0, 1, 0x7fff, 0x8000, ushort.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<ushort> locals -> retained Int64 consumers; bounded U2 payload/zero extension, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((short)(input + 1))", "input < 0 ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((short)(input + 1)))", "length.HasValue ? length.Value : (short)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 narrowRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(short input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "short? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(narrowRequest, argument, new short[] { short.MinValue, -256, -1, 0, 1, 255, short.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<short> locals -> retained Int64 consumers; bounded I2 payload/sign extension, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((byte)(input + 1))", "input == 0 ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((byte)(input + 1)))", "length.HasValue ? length.Value : (byte)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 byteRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(byte input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "byte? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(byteRequest, argument, new byte[] { 0, 1, 0x7f, 0x80, byte.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<byte> locals -> retained Int64 consumers; bounded U1 payload/zero extension, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((sbyte)(input + 1))", "input < 0 ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((sbyte)(input + 1)))", "length.HasValue ? length.Value : (sbyte)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 narrowRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(sbyte input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "sbyte? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(narrowRequest, argument, new sbyte[] { sbyte.MinValue, -127, -1, 0, 1, 126, sbyte.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<sbyte> locals -> retained Int64 consumers; bounded I1 payload/sign extension, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((char)(input + 1))", "input == 0 ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((char)(input + 1)))", "length.HasValue ? length.Value : (char)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 narrowRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(char input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "char? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(narrowRequest, argument, new char[] { '\0', '\u0001', '\u7fff', '\u8000', '\ud800', '\udfff', char.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<char> locals -> retained Int64 consumers; bounded Char/U2 payload/zero extension, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "!input", "input ? null : input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(!input)", "length.HasValue ? length.Value : false", "length.Value" }) {
                string argument = "(" + getter + ") ? 1L : 0L";
                CompanionSourceRequestV1 logicalRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(bool input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "bool? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(logicalRequest, argument, new[] { false, true }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS exact Nullable<bool> locals -> retained Int64 consumers; distinct Boolean nominal/CLI byte payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((Choice)(input + 1))", "input < 0 ? null : (Choice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((Choice)(input + 1)))", "length.HasValue ? length.Value : (Choice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum Choice : int { Zero = 0, One = 1 } public static class Fixture {")
                    .Replace("Read(long input)", "Read(int input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "Choice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new[] { int.MinValue, -256, -1, 0, 1, 255, int.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<Choice:int> locals -> retained Int64 consumers; exact enum nominal/I4 payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((WideChoice)(input + 1))", "input < 0 ? null : (WideChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((WideChoice)(input + 1)))", "length.HasValue ? length.Value : (WideChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum WideChoice : long { Zero = 0, One = 1 } public static class Fixture {")
                    
                    .Replace("var value = new WideValue(input); return value.Value;", "WideChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<WideChoice:long> locals -> retained Int64 consumers; exact enum nominal/I8 payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((UnsignedChoice)(input + 1UL))", "input == 0 ? null : (UnsignedChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((UnsignedChoice)(input + 1UL)))", "length.HasValue ? length.Value : (UnsignedChoice)0", "length.Value" }) {
                string argument = "unchecked((long)(" + getter + "))";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum UnsignedChoice : ulong { Zero = 0, One = 1 } public static class Fixture {")
                    .Replace("Read(long input)", "Read(ulong input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "UnsignedChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new ulong[] { 0, 1, 0x7fffffffUL, 0x80000000UL, 0x7fffffffffffffffUL, 0x8000000000000000UL, ulong.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<UnsignedChoice:ulong> locals -> retained Int64 consumers; exact enum nominal/U8 payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((WordChoice)(input + 1U))", "input == 0 ? null : (WordChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((WordChoice)(input + 1U)))", "length.HasValue ? length.Value : (WordChoice)0", "length.Value" }) {
                string argument = "unchecked((long)(" + getter + "))";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum WordChoice : uint { Zero = 0, One = 1 } public static class Fixture {")
                    .Replace("Read(long input)", "Read(uint input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "WordChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new uint[] { 0, 1, 0x7fffffffU, 0x80000000U, uint.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<WordChoice:uint> locals -> retained Int64 consumers; exact enum nominal/U4 payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((NarrowChoice)(input + 1))", "input == 0 ? null : (NarrowChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((NarrowChoice)(input + 1)))", "length.HasValue ? length.Value : (NarrowChoice)0", "length.Value" }) {
                string argument = "unchecked((long)(" + getter + "))";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum NarrowChoice : ushort { Zero = 0, One = 1 } public static class Fixture {")
                    .Replace("Read(long input)", "Read(ushort input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "NarrowChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new ushort[] { 0, 1, 0x7fff, 0x8000, ushort.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<NarrowChoice:ushort> locals -> retained Int64 consumers; exact enum nominal/U2 payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((SignedChoice)(input + 1))", "input < 0 ? null : (SignedChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((SignedChoice)(input + 1)))", "length.HasValue ? length.Value : (SignedChoice)0", "length.Value" }) {
                string argument = "unchecked((long)(" + getter + "))";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum SignedChoice : short { Zero = 0, One = 1 } public static class Fixture {")
                    .Replace("Read(long input)", "Read(short input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "SignedChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new short[] { short.MinValue, -256, -1, 0, 1, 255, short.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<SignedChoice:short> locals -> retained Int64 consumers; exact enum nominal/I2 payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((ByteChoice)(input + 1))", "input == 0 ? null : (ByteChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ByteChoice)(input + 1)))", "length.HasValue ? length.Value : (ByteChoice)0", "length.Value" }) {
                string argument = "unchecked((long)(" + getter + "))";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum ByteChoice : byte { Zero = 0, One = 1 } public static class Fixture {")
                    .Replace("Read(long input)", "Read(byte input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ByteChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new byte[] { 0, 1, 0x7f, 0x80, byte.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<ByteChoice:byte> locals -> retained Int64 consumers; exact enum nominal/U1 payload, scalar/single-module/repeat/maps/image and host faults");
        foreach (string init in new[] { "unchecked((SByteChoice)(input + 1))", "input < 0 ? null : (SByteChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((SByteChoice)(input + 1)))", "length.HasValue ? length.Value : (SByteChoice)0", "length.Value" }) {
                string argument = "unchecked((long)(" + getter + "))";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("public static class Fixture {", "public enum SByteChoice : sbyte { Zero = 0, One = 1 } public static class Fixture {")
                    .Replace("Read(long input)", "Read(sbyte input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "SByteChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new sbyte[] { sbyte.MinValue, -127, -1, 0, 1, 126, sbyte.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false);
            }
        Console.WriteLine("PASS source-owned Nullable<SByteChoice:sbyte> locals -> retained Int64 consumers; exact enum nominal/I1 payload, scalar/single-module/repeat/maps/image and host faults");
        const string externalEnums = " public enum ExternalChoice : int { Zero = 0, One = 1 } public enum OtherChoice : int { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 enumDefinition = Library(library + externalEnums);
        if (enumDefinition.Source.Pe is null) throw new Exception("External nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input < 0 ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, enumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(int input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new[] { int.MinValue, -1, 0, 1, int.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalEnums, hostLibrary: enumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:int> selected definition -> scalar/single-module/host/repeat/maps/image");
        const string externalWideEnums = " public enum ExternalChoice : long { Zero = 0, One = 1 } public enum OtherChoice : long { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 wideEnumDefinition = Library(library + externalWideEnums);
        if (wideEnumDefinition.Source.Pe is null) throw new Exception("External wide nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input < 0 ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, wideEnumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new[] { long.MinValue, (long)int.MinValue, -1, 0, 1, (long)int.MaxValue, long.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalWideEnums, hostLibrary: wideEnumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:long> selected definition -> scalar/single-module/host/repeat/maps/image");
        const string externalUnsignedEnums = " public enum ExternalChoice : ulong { Zero = 0, One = 1 } public enum OtherChoice : ulong { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 unsignedEnumDefinition = Library(library + externalUnsignedEnums);
        if (unsignedEnumDefinition.Source.Pe is null) throw new Exception("External unsigned nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input == ulong.MaxValue ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, unsignedEnumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(ulong input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new ulong[] { 0, 1, uint.MaxValue, 1UL << 32, (1UL << 63) - 1, 1UL << 63, ulong.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalUnsignedEnums, hostLibrary: unsignedEnumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:ulong> selected definition -> scalar/single-module/host/repeat/maps/image");
        const string externalWordEnums = " public enum ExternalChoice : uint { Zero = 0, One = 1 } public enum OtherChoice : uint { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 wordEnumDefinition = Library(library + externalWordEnums);
        if (wordEnumDefinition.Source.Pe is null) throw new Exception("External word nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input == uint.MaxValue ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, wordEnumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(uint input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new uint[] { 0, 1, int.MaxValue, 1U << 31, uint.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalWordEnums, hostLibrary: wordEnumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:uint> selected definition -> scalar/single-module/host/repeat/maps/image");
        const string externalNarrowEnums = " public enum ExternalChoice : ushort { Zero = 0, One = 1 } public enum OtherChoice : ushort { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 narrowEnumDefinition = Library(library + externalNarrowEnums);
        if (narrowEnumDefinition.Source.Pe is null) throw new Exception("External narrow nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input == ushort.MaxValue ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, narrowEnumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(ushort input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new ushort[] { 0, 1, 32767, 32768, ushort.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalNarrowEnums, hostLibrary: narrowEnumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:ushort> selected definition -> scalar/single-module/host/repeat/maps/image");
        const string externalSignedNarrowEnums = " public enum ExternalChoice : short { Zero = 0, One = 1 } public enum OtherChoice : short { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 signedNarrowEnumDefinition = Library(library + externalSignedNarrowEnums);
        if (signedNarrowEnumDefinition.Source.Pe is null) throw new Exception("External signed narrow nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input < 0 ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, signedNarrowEnumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(short input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new short[] { short.MinValue, -32767, -1, 0, 1, 32766, short.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalSignedNarrowEnums, hostLibrary: signedNarrowEnumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:short> selected definition -> scalar/single-module/host/repeat/maps/image");
        const string externalByteEnums = " public enum ExternalChoice : byte { Zero = 0, One = 1 } public enum OtherChoice : byte { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 byteEnumDefinition = Library(library + externalByteEnums);
        if (byteEnumDefinition.Source.Pe is null) throw new Exception("External byte nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input == byte.MaxValue ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, byteEnumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(byte input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new byte[] { 0, 1, 127, 128, byte.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalByteEnums, hostLibrary: byteEnumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:byte> selected definition -> scalar/single-module/host/repeat/maps/image");
        const string externalSignedByteEnums = " public enum ExternalChoice : sbyte { Zero = 0, One = 1 } public enum OtherChoice : sbyte { Zero = 0 }";
        ManagedSourceBodyWorldResultV1 signedByteEnumDefinition = Library(library + externalSignedByteEnums);
        if (signedByteEnumDefinition.Source.Pe is null) throw new Exception("External signed byte nullable enum library emit");
        foreach (string init in new[] { "unchecked((ExternalChoice)(input + 1))", "input < 0 ? null : (ExternalChoice)input" })
            foreach (string getter in new[] { "length.GetValueOrDefault()", "length.GetValueOrDefault(unchecked((ExternalChoice)(input + 1)))", "length.HasValue ? length.Value : (ExternalChoice)0", "length.Value" }) {
                string argument = "(long)(" + getter + ")";
                CompanionSourceRequestV1 enumRequest = constructorRequest with { ReferenceImages = [..references, signedByteEnumDefinition.Source.Pe], Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                    .Replace("Read(long input)", "Read(sbyte input)")
                    .Replace("var value = new WideValue(input); return value.Value;", "ExternalChoice? length = " + init + "; var value = new WideValue(" + argument + "); var alias = value; return value.Value + alias.Value;"))] };
                VerifyMixed(enumRequest, argument, new sbyte[] { sbyte.MinValue, -127, -1, 0, 1, 126, sbyte.MaxValue }.Select(input => new object?[] { input }),
                    expectCheckedReferences: getter.Contains(".Value"), expectObjectRoots: false, enumDeclarations: externalSignedByteEnums, hostLibrary: signedByteEnumDefinition.Source.Pe);
            }
        Console.WriteLine("PASS external exact Nullable<ExternalChoice:sbyte> selected definition -> scalar/single-module/host/repeat/maps/image");
        CheckNullableParameterConsumers();
        void CheckNullableParameterConsumers() {
        foreach(int accessor in new[]{0,1,2,3,4,5,6})
        foreach(string initializer in new[]{"(int)input","null","input==0?null:(int)input","unchecked((int)input+1)"}) {
                    CompanionSourceRequestV1 nullableParameterRequest =constructorRequest with { Documents = [new("/src/Primary.cs", constructorRequest.Documents[0].Text
                .Replace("public static class Fixture {", "public static class Fixture { "+(accessor==6?"private static int Adjust(int fallback)=>unchecked(fallback+1); ":"")+"private static long Consume("+(accessor>=3?"int? length,int fallback":"int? length")+") => "+(accessor==6?"(long)length.GetValueOrDefault(Adjust(fallback))":accessor==5?"(long)length.GetValueOrDefault(fallback==0?-7:unchecked(fallback+1))":accessor==4?"(long)length.GetValueOrDefault(unchecked(fallback+1))":accessor==3?"(long)length.GetValueOrDefault(fallback)":accessor==1?"length.HasValue?1L:0L":accessor==2?"(long)length.GetValueOrDefault(-7)":"(long)length.GetValueOrDefault()")+";")
                .Replace("var value = new WideValue(input); return value.Value;", "int? length = "+initializer+"; var value = new WideValue(Consume(length"+(accessor>=3?",unchecked((int)(input>>32))":"")+")); return value.Value;"))] };
                    ManagedSourceBodyWorldResultV1 accepted =Compile(nullableParameterRequest); ManagedSourceBodyWorldResultV1 repeated =Compile(nullableParameterRequest);
                    ScalarControlFlowV2LinkedProgramV1 linkedParameter =Link(accepted); ScalarControlFlowV2LinkedProgramV1 repeatedParameter =Link(repeated);
                    ManagedSourceBodyWorldResultV1 nullableSingle =ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide",[new("/src/Primary.cs",nullableParameterRequest.Documents[0].Text+library)],references),
                new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],null,[0]);
            if(!Codes(linkedParameter).SequenceEqual(Codes(Link(nullableSingle)))||
                !linkedParameter.RestrictedImage!.PackageBytes.SequenceEqual(repeatedParameter.RestrictedImage!.PackageBytes)||
                !accepted.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!accepted.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||
                accepted.SourceMaps.Any(p=>repeated.SourceMaps[p.Key].Digest!=p.Value.Digest))throw new Exception("Retained nullable parameter equivalence/repeat");
                    AssemblyLoadContext context =new System.Runtime.Loader.AssemblyLoadContext("nullable-parameter-retained",isCollectible:true);
            try {
                context.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                        MethodInfo host =context.LoadFromStream(new MemoryStream(accepted.Source.Pe!)).GetType("Fixture")!.GetMethod("Read")!;
                foreach(long input in new[]{long.MinValue,(long)int.MinValue,-1,0,1,(long)int.MaxValue,long.MaxValue}) {
                    bool absent=initializer=="null"||initializer.Contains("input==0")&&input==0;
                    long expected=absent?0L:initializer.Contains("unchecked")?(long)unchecked((int)input+1):(long)unchecked((int)input);
                    if(accessor==1)expected=absent?0L:1L;
                    if(accessor==2 && absent)expected=-7L;
                    if(accessor==3 && absent)expected=unchecked((int)(input>>32));
                    if(accessor==4 && absent)expected=unchecked((int)(input>>32)+1);
                    if(accessor==6 && absent)expected=unchecked((int)(input>>32)+1);
                    if(accessor==5 && absent)expected=(int)(input>>32)==0?-7:unchecked((int)(input>>32)+1);
                    if(!Equals(host.Invoke(null,[input]),expected))throw new Exception("Retained nullable parameter independent scalar host boundaries");
                }
            } finally { context.Unload(); }
        }
        Console.WriteLine("PASS NoHeap private Nullable<int> helper parameter getters/constant/dynamic/computed/helper defaults -> retained Int64 selected constructor, single-module/repeat PE/PDB/maps/image/host");
        NullableInt32Checks.RunExternalDefault();
        }
        Console.WriteLine("PASS in-place Int64 constructor scalar arithmetic/bit/shift/conversion arguments -> existing typed proof and source-owned/external/scalar bytes; host boundaries, PE/PDB/maps/image repeat, original MaxStack/budget1/2/3 controls");
        Console.WriteLine("PASS conditional/nested/short-circuit Int64 constructor argument CFG -> source-owned/external/scalar bytes, boundary host semantics and exact repeat/maps/image");
        CompanionSourceRequestV1 parameterRequest = request with { Documents = [new("/src/Primary.cs", primary
            .Replace("public static class Fixture {", "public static class Fixture { public static long Consume(WideValue value) => value.Value;")
            .Replace("return value.Value;", "return Consume(value);"))] };
        foreach (string expression in new[] { "value.Value", "value.Value + value.Value", "value.Value > 0 ? value.Value : 0" }) {
            CompanionSourceRequestV1 input = parameterRequest with { Documents = [new("/src/Primary.cs", parameterRequest.Documents[0].Text.Replace("=> value.Value;", "=> " + expression + ";"))] };
            ManagedSourceBodyWorldResultV1 consumed = Compile(input); ScalarControlFlowV2LinkedProgramV1 consumedImage = Link(consumed); ManagedSourceBodyWorldResultV1 consumedRepeat = Compile(input);
            ManagedSourceBodyWorldResultV1 singleParameter = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", input.Documents[0].Text + library)], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 scalarParameter = ManagedSourceCompanionV1.CompileBodyWorld(new("ExternalWide", [new("/src/Primary.cs", input.Documents[0].Text
                .Replace("Consume(WideValue value)", "Consume(long value)").Replace("value.Value", "value") +
                library.Replace("public static WideValue Create(long value) => new WideValue(value);", "public static long Create(long value) => value;"))], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ScalarControlFlowV2LinkedProgramV1 scalarParameterImage = Link(scalarParameter);
            if (!Codes(consumedImage).SequenceEqual(Codes(Link(singleParameter))) ||
                new[] { "Read", "Consume" }.Any(name => consumedImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture." + name + "(")).CodeSha256 !=
                    scalarParameterImage.MethodObjects.Single(m => Name(m.MethodIdentity).StartsWith("Fixture." + name + "(")).CodeSha256) ||
                !consumed.Source.Pe!.SequenceEqual(consumedRepeat.Source.Pe!) || !consumed.Source.Pdb!.SequenceEqual(consumedRepeat.Source.Pdb!) ||
                !consumedImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(consumedRepeat).RestrictedImage!.PackageBytes) ||
                consumed.SourceMaps.Count != 2 || consumed.SourceMaps.Any(row => row.Value.Rows.All(r => r.Document is null) || consumedRepeat.SourceMaps[row.Key].Digest != row.Value.Digest))
                throw new Exception("External Int64 parameter caller/helper/code/maps/image repeat oracle failed");
            AssemblyLoadContext parameterContext = new System.Runtime.Loader.AssemblyLoadContext("external-wide-parameter-host", isCollectible: true);
            try {
                parameterContext.LoadFromStream(new MemoryStream(definition.Source.Pe!));
                Assembly actual = parameterContext.LoadFromStream(new MemoryStream(consumed.Source.Pe!));
                Assembly expected = System.Reflection.Assembly.Load(scalarParameter.Source.Pe!);
                foreach (long value in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                    if (!Equals(actual.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [value]), expected.GetType("Fixture")!.GetMethod("Read")!.Invoke(null, [value])))
                        throw new Exception("External Int64 parameter host boundary oracle failed");
            } finally { parameterContext.Unload(); }
            if (expression == "value.Value") Console.WriteLine("IDENTITY external-Int64-parameter artifact=" + consumed.Source.ArtifactIdentity!.Digest + "; image=" + consumedImage.RestrictedImage!.PackageSha256);
        }
        foreach (int limit in new[] { 0, 1, 2, 3 }) {
            ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(parameterRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with { MaximumMetadataResolutionSteps = limit }),
                [new("Fixture", "Consume")], [4], [0]);
            if (limit == 3 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                budgeted.Compilation?.Status != (limit == 0 ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.BudgetExhausted) ||
                !budgeted.Compilation.Diagnostics.Any(d => d.Code == (limit == 0 ? "HCSCF-BODYWORLD0004" : "HCSCF-BUDGET2002")))
                throw new Exception("External Int64 parameter exact proof-resolution budget boundary failed: " + limit);
        }
        foreach (ManagedSourceBodyWorldResultV1? denied in new[] {
            Compile(parameterRequest, []), Compile(parameterRequest, [], [0, 4]),
            Compile(parameterRequest with { Documents = [new("/src/Primary.cs", parameterRequest.Documents[0].Text.Replace("=> value.Value;", "{ value = WideFactory.Create(1); return value.Value; }"))] }),
            Compile(parameterRequest with { Documents = [new("/src/Primary.cs", parameterRequest.Documents[0].Text.Replace("Consume(WideValue value)", "Consume(in WideValue value)"))] }),
            Compile(parameterRequest with { Documents = [new("/src/Primary.cs", parameterRequest.Documents[0].Text.Replace("=> value.Value;", "{ try { return value.Value; } finally { System.GC.Collect(); } }"))] }) })
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("External Int64 parameter role/mutable/in/EH controls failed");
        // Exercise nominal argument admission before erasure, without an intervening local store.
        ManagedSourceBodyWorldResultV1 nominalParameter = Compile(parameterRequest with { Documents = [new("/src/Primary.cs", parameterRequest.Documents[0].Text
            .Replace("var value = WideFactory.Create(input); return Consume(value);", "return Consume(WideFactory.Create(input));")
            .Replace("public static class Fixture {", "public static class Fixture { public static long ScalarCreate(long x) => x; public static OtherValue OtherCreate(long x) => new OtherValue(x);") +
            " public readonly struct OtherValue { public long Value { get; } public OtherValue(long x) { Value = x; } }")] });
        Link(nominalParameter);
        foreach (string replacement in new[] { "ScalarCreate", "OtherCreate" }) {
            byte[] bytes = nominalParameter.Source.Pe!.ToArray();
            using PEReader pe = new PEReader(new MemoryStream(bytes)); MetadataReader md = pe.GetMetadataReader();
            MethodDefinition read = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
            int create = MetadataTokens.GetToken(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "Create"));
            int other = MetadataTokens.GetToken(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == replacement));
            byte[] il = pe.GetMethodBody(read.RelativeVirtualAddress).GetILBytes()!;
            int call = Enumerable.Range(0, il.Length - 4).Single(i => il[i] == 0x28 && BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(i + 1, 4)) == create);
            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => read.RelativeVirtualAddress >= s.VirtualAddress && read.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int body = section.PointerToRawData + read.RelativeVirtualAddress - section.VirtualAddress;
            int header = (bytes[body] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2)) >> 12) * 4;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(body + header + call + 1, 4), other);
            ManagedCallGraphCompilationV1 denied = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, bytes, "external-int64-parameter-negative",
                [new("Fixture", "Read")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
            if (denied.Status != RestrictedCilImportStatusV1.InvalidInput || !denied.Diagnostics.Any(d => d.Code == "HCCIL0017"))
                throw new Exception("External Int64 parameter accepted primitive/wrong-nominal argument: " + string.Join(';', denied.Diagnostics));
        }
        ManagedSourceBodyWorldResultV1 impureGetter = Library(library.Replace("public long Value { get; }", "private readonly long _value; public long Value => _value + 1;")
            .Replace("Value = value;", "_value = value;"));
        ManagedSourceBodyWorldResultV1 impureParameter = ManagedSourceCompanionV1.CompileBodyWorld(parameterRequest with { ReferenceImages = [..references, impureGetter.Source.Pe!] },
            importer, [new("Fixture", "Consume")], [4], [0]);
        if (impureParameter.Source.Pe is null || impureParameter.Compilation?.Status != RestrictedCilImportStatusV1.Unsupported ||
            !impureParameter.Compilation.Diagnostics.Any(d => d.Code == "HCCIL1001"))
            throw new Exception("External Int64 raw getter proof accepted nonpure getter");
        Console.WriteLine("PASS external Int64 immutable parameter/getter exact budgeted definition proof -> scalar caller/helper bytes, split-host boundaries, repeat/maps/image; budgets0/1/2/3 and role/mutable/in/EH/nonpure/nominal negatives");
        ManagedSourceBodyWorldResultV1 forwarding = Compile(request with { Documents = [new("/src/Primary.cs", primary
            .Replace("public static class Fixture {", "public static class Fixture { public static WideValue Forward(long input) => WideFactory.Create(input); public static long Primitive(long input) => input;")
            .Replace("var value = WideFactory.Create(input)", "var value = Forward(input)"))] });
        Link(forwarding);
        byte[] primitiveReturn = forwarding.Source.Pe!.ToArray();
        using (PEReader pe = new PEReader(new MemoryStream(primitiveReturn))) {
            MetadataReader md = pe.GetMetadataReader();
            MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Forward"));
            int primitive = MetadataTokens.GetToken(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Primitive"));
            byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            if (il.Length != 7 || il[0] != 2 || il[1] != 0x28 || il[6] != 0x2a) throw new Exception("External Int64 return fixture changed");
            SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int body = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
            int header = (primitiveReturn[body] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(primitiveReturn.AsSpan(body, 2)) >> 12) * 4;
            BinaryPrimitives.WriteInt32LittleEndian(primitiveReturn.AsSpan(body + header + 2, 4), primitive);
        }
        ManagedCallGraphCompilationV1 wrongReturn = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, primitiveReturn, "external-int64-return",
            [new("Fixture", "Forward")], [], DependencyModules: [new(definition.Source.Pe!, "wide-body")], MetadataOnlyModules: [new(references[0], "wide-core")]));
        if (wrongReturn.Status != RestrictedCilImportStatusV1.InvalidInput || !wrongReturn.Diagnostics.Any(d => d.Code == "HCCIL0019"))
            throw new Exception("External Int64 nominal return proof accepted a primitive: " + string.Join(';', wrongReturn.Diagnostics));
        foreach (string text in new[] {
            library.Replace("Value = value;", "Value = value + 1;"),
            library.Replace("Value = value;", "System.GC.Collect(); Value = value;"),
            library.Replace("public long Value { get; }", "private readonly long _value; public long Value => _value + 1;").Replace("Value = value;", "_value = value;"),
            library.Replace("public readonly struct WideValue {", "public readonly struct WideValue { static WideValue() {}"),
            library.Replace("struct WideValue {", "struct WideValue<T> {").Replace("public static WideValue Create", "public static WideValue<int> Create")
                .Replace("new WideValue(value)", "new WideValue<int>(value)") }) {
            ManagedSourceBodyWorldResultV1 changed = Library(text);
            if (changed.Source.Pe is null) throw new Exception("External Int64 negative fixture failed to emit");
            ManagedSourceBodyWorldResultV1 denied = Compile(request with { ReferenceImages = [..references, changed.Source.Pe] });
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success)
                throw new Exception("Unqualified external Int64 definition/getter/effects admitted");
        }
        foreach (int limit in new[] { 0, 1 }) {
            ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with { MaximumMetadataResolutionSteps = limit }),
                [new("Fixture", "Read")], [4], [0]);
            if (budgeted.Compilation?.Status != (limit == 0 ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.BudgetExhausted) ||
                !budgeted.Compilation.Diagnostics.Any(d => d.Code == (limit == 0 ? "HCSCF-BODYWORLD0004" : "HCSCF-BUDGET2002")))
                throw new Exception("External Int64 metadata resolution budget precedence failed");
        }
        ManagedSourceBodyWorldResultV1 changedDefinition = Library(library + " public static class Unused { public static int Read() => 9; }");
        ManagedSourceBodyWorldResultV1 changedInput = Compile(request with { ReferenceImages = [..references, changedDefinition.Source.Pe!] });
        if (changedInput.Source.SourceInputDigest == result.Source.SourceInputDigest ||
            changedInput.Compilation?.Status != RestrictedCilImportStatusV1.Success || !Codes(Link(changedInput)).SequenceEqual(Codes(image)) ||
            changedInput.Compilation.Methods.Single(m => m.Identity.MethodName == "Read").Import.Provenance!.OptionsDigest ==
                result.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import.Provenance!.OptionsDigest)
            throw new Exception("External Int64 definition/input/options identity control failed");
        if (!Link(Compile(request)).RestrictedImage!.PackageBytes.SequenceEqual(image.RestrictedImage!.PackageBytes))
            throw new Exception("External Int64 importer reuse after negatives changed image");
        Console.WriteLine("IDENTITY external-Int64 artifact=" + result.Source.ArtifactIdentity!.Digest + "; image=" + image.RestrictedImage!.PackageSha256 +
            "; options=" + result.Compilation!.Methods.Single(m => m.Identity.MethodName == "Read").Import.Provenance!.OptionsDigest);
        Console.WriteLine("PASS external Int64 factory/getter selected body library -> exact carrier/shared image; single-module/scalar caller/host boundary/repeat/maps oracle; omitted/metadata/effect/definition identity negatives");
    }
}
