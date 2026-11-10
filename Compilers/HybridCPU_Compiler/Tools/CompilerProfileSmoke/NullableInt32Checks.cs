using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Reflection;

internal static class NullableInt32Checks
{
    public static void RunMap()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string header="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture{";
        const string declaration="private static int? Shift(int? value)";
        const string body="=>value.HasValue?unchecked(value.GetValueOrDefault()+1):null;";
        const string consumer="[HybridCpuKernelNoHeap]public static long Read(int input,int present){int? value=present==0?null:input;int? shifted=Shift(value);return new WideValue(shifted.GetValueOrDefault(-7)).Value;}";
        string baseline=header+declaration+body+consumer+"}";
        CompanionSourceRequestV1 Request(string source)=>new("NullableMap",[new("/src/Map.cs",source)],references);
        ManagedSourceBodyWorldResultV1 Compile(string source,RestrictedCilImportBudgetsV1? budgets=null,CancellationToken token=default)=>ManagedSourceCompanionV1.CompileBodyWorld(Request(source),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0],token);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Nullable map: "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
        foreach((string producer,Func<int,bool,int?> expected) in new (string,Func<int,bool,int?>)[]{
            (declaration+body,(input,present)=>present?unchecked(input+1):null),
            (declaration+"=>value.HasValue?unchecked(value.Value+1):null;",(input,present)=>present?unchecked(input+1):null),
            (declaration+"=>value;",(input,present)=>present?input:null),
            (declaration+"{value=null;return value;}",(_,_)=>null),
            (declaration+"{value=value.HasValue?unchecked(value.GetValueOrDefault()+1):null;return value;}",(input,present)=>present?unchecked(input+1):null),
            (declaration+"{value=unchecked(value.GetValueOrDefault()+1);return value;}",(input,present)=>present?unchecked(input+1):1),
            (declaration+"{value=Adjust(value.GetValueOrDefault());return value;}private static int Adjust(int input)=>unchecked(input+1);",(input,present)=>present?unchecked(input+1):1),
            (declaration+"{value=Adjust(value.GetValueOrDefault());return value;}private static int Adjust(int input)=>Leaf(input);private static int Leaf(int input)=>unchecked(input+1);",(input,present)=>present?unchecked(input+1):1),
            (declaration+"{int? copy=value;return copy.HasValue?unchecked(copy.GetValueOrDefault()-3):null;}",(input,present)=>present?unchecked(input-3):null),
            (declaration+"=>value.HasValue?null:42;",(_,present)=>present?null:42),
            (declaration+"=>Step(value);private static int? Step(int? value)"+body,(input,present)=>present?unchecked(input+1):null),
            (declaration+"=>value.HasValue?unchecked(ReadPayload(value)+1):null;private static int ReadPayload(int? value)=>value.GetValueOrDefault();",(input,present)=>present?unchecked(input+1):null),
            (declaration+"=>Step(Step(value));private static int? Step(int? value)"+body,(input,present)=>present?unchecked(input+2):null)}) {
            string source=header+producer+consumer+"}";var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Nullable map repeat/maps/image");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1})if(!Equals(host.Invoke(null,[input,present]),(long)(expected(input,present!=0)??-7)))throw new Exception("Nullable map independent payload/presence/overflow oracle");
            if(source!=baseline)continue;
            var inline=Compile(source.Replace("shifted=Shift(value)","shifted=value.HasValue?unchecked(value.GetValueOrDefault()+1):null"));Link(inline);var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1})if(!Equals(host.Invoke(null,[input,present]),oracle.Invoke(null,[input,present])))throw new Exception("Nullable map inline oracle");
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-map-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            var standalone=ManagedSourceCompanionV1.Compile(Request(source.Replace("[HybridCpuKernelNoHeap]","")),new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),new("Fixture","Shift"));if(standalone.Pe is null||standalone.Imported?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable map standalone authority gate");
            var rootDenied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,actual.Source.Pe!,"map-root-gate",[new("Fixture","Shift")],[],MetadataOnlyModules:[new(references[0],"core")]));if(rootDenied.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable map root gate");
            string roleSource="using HybridCPU.Compiler.Profiles;public static class Primary{[HybridCpuKernelNoHeap]public static long Read(int input,int present)=>Fixture.Read(input,present);}";
            CompanionSourceRequestV1 roleRequest=new("NullableMapRoles",[new("/src/Role.cs",roleSource)],[..references,actual.Source.Pe!]);
            foreach(bool selected in new[]{false,true}){var denied=ManagedSourceCompanionV1.CompileBodyWorld(roleRequest,new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Primary","Read")],selected?[4]:[],selected?[0]:[0,4]);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("External nullable map metadata/body authority gate: "+string.Join(';',denied.Source.Diagnostics)+";"+string.Join(';',denied.Compilation?.Diagnostics??[]));}
            using(var pe=new PEReader(new MemoryStream(actual.Source.Pe!))){var md=pe.GetMetadataReader();var root=md.MethodDefinitions.Single(handle=>md.GetString(md.GetMethodDefinition(handle).Name)=="Read");var shift=md.MethodDefinitions.Single(handle=>md.GetString(md.GetMethodDefinition(handle).Name)=="Shift");var method=md.GetMethodDefinition(root);var cil=pe.GetMethodBody(method.RelativeVirtualAddress);byte[] forged=actual.Source.Pe!.ToArray();var section=pe.PEHeaders.SectionHeaders.Single(row=>method.RelativeVirtualAddress>=row.VirtualAddress&&method.RelativeVirtualAddress<row.VirtualAddress+row.SizeOfRawData);int offset=section.PointerToRawData+method.RelativeVirtualAddress-section.VirtualAddress;offset+=(forged[offset]&3)==2?1:(forged[offset+1]>>4)*4;forged.AsSpan(offset,cil.GetILBytes()!.Length).Clear();forged[offset]=0x21;forged[offset+9]=0x28;BinaryPrimitives.WriteInt32LittleEndian(forged.AsSpan(offset+10,4),MetadataTokens.GetToken(shift));forged[offset+14]=0x26;forged[offset+15]=0x21;forged[offset+cil.GetILBytes()!.Length-1]=0x2a;var denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,forged,"forged-map-input",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));if(denied.Status==RestrictedCilImportStatusV1.Success||!denied.Diagnostics.Any(row=>row.Code=="HCCIL0017"))throw new Exception("Primitive I8 cannot forge nullable map ingress: "+string.Join(';',denied.Diagnostics));}
        }
        foreach(string replacement in new[]{"public static int? Shift(int? value)"+body,declaration+"=>Shift(value);",declaration+"=>1/value.GetValueOrDefault();",declaration+"{System.GC.Collect();return value;}",declaration+"{try{return value;}finally{System.GC.Collect();}}","[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]"+declaration+body}){var denied=Compile(baseline.Replace(declaration+body,replacement));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable map public/recursive/fault/effect/EH/implementation gate");}
        var changedGuard=Compile(baseline.Replace(declaration+body,declaration+"{if(value.HasValue){value=value.GetValueOrDefault()==0?null:1;return value.Value;}return null;}"));if(changedGuard.Source.Pe is null||changedGuard.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable map mutation cannot reuse an old checked presence proof");
        foreach(string helper in new[]{"private static int Adjust(int input)=>1/input;","private static int Adjust(int input)=>Adjust(input);","private static int Adjust(int input){System.GC.Collect();return input;}","[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]private static int Adjust(int input)=>input;"}) {
            var denied=Compile(baseline.Replace(declaration+body,declaration+"{value=Adjust(value.GetValueOrDefault());return value;}"+helper));
            if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable initializer discovery cannot admit fault/effect/recursive/implementation helper");
        }
        foreach(string bad in new[]{baseline.Replace(declaration,"private static int? Shift<T>(int? value)").Replace("Shift(value)","Shift<uint>(value)"),baseline.Replace("int? shifted=Shift(value);","int? shifted;try{shifted=Shift(value);}finally{input=unchecked(input+1);}")}){var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable map generic/EH-caller gate: "+string.Join(';',denied.Source.Diagnostics));}
        foreach(string bad in new[]{baseline.Replace(declaration+body,declaration+"=>value.Value;"),baseline.Replace(declaration+body,declaration+"=>ReadPayload(value);private static int ReadPayload(int? value)=>value.Value;"),baseline.Replace("Shift(int? value)","Shift(ref int? value)").Replace("Shift(value)","Shift(ref value)"),baseline.Replace(declaration+body,"private static int? Shift(int? value,int? other)=>value;").Replace("Shift(value)","Shift(value,value)"),baseline.Replace("int? value=present==0?null:input;","uint? value=present==0?null:unchecked((uint)input);").Replace(declaration+body,"private static int? Shift(uint? value)=>value.HasValue?unchecked((int)value.GetValueOrDefault()+1):null;")}){var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable map checked getter/byref/multiple-nominal/unsigned gate: "+string.Join(';',denied.Source.Diagnostics)+";"+string.Join(';',denied.Compilation?.Diagnostics??[]));}
        foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumArguments=1},RestrictedCilImportBudgetsV1.Production with{MaximumLocals=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}}){var denied=Compile(baseline,budget);if(denied.Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Nullable map original budgets");}
        using(var cancellation=new CancellationTokenSource()){cancellation.Cancel();try{Compile(baseline,token:cancellation.Token);throw new Exception("Nullable map cancellation");}catch(OperationCanceledException){}}
        Link(Compile(baseline));
        Console.WriteLine("PASS private nullable input/return map identity/copy/conditional/transitive chains -> NoHeap/source/image/host/inline/presence/payload/extrema/overflow/repeat/maps and authority/effect/EH/implementation/budget/cancel controls");
    }
    public static void RunMapMixed()
    {
        RunMapScaled();
        RunMapFallback();
        RunMapSwitch();
        RunMapLoop();
        RunWordComparisons();
        RunGenericMap();
        RunGenericPayload();
        RunGenericPayload(presence: true);
        RunGenericPayload(construction: true);
        RunGenericPayload(construction: true, genericDefault: true);
        RunGenericPayload(construction: true, fallbackArgument: true);
        RunGenericPayload(construction: true, fallbackArgument: true, ownerType: "int");
        RunGenericPayload(construction: true, fallbackArgument: true, ownerType: "long");
        RunGenericPayload(construction: true, fallbackArgument: true, ownerType: "int", ownerConstraint: true);
        RunGenericPayload(construction: true, fallbackArgument: true, ownerType: "long", ownerConstraint: true);
        RunStructScalarDefault();
        RunStructScalarDefault(genericMethod:true);
        RunStructScalarDefault(genericMethod:true,wideMethod:true);
        RunStructScalarDefault(genericMethod:true,binaryMethod:true);
        RunStructScalarDefault(genericMethod:true,wideMethod:true,binaryMethod:true);
        RunStructScalarArity();
        RunStructScalarArity(genericZero: true);
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(bool wide in new[]{false,true}) {
            string type=wide?"long":"int";
            string expression="value.HasValue?unchecked(value.GetValueOrDefault()-(int)delta):null";
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture{private static int? Shift(int? value,"+type+" delta)=>"+expression+";[HybridCpuKernelNoHeap]public static long Read(int input,int present,"+type+" delta){int? value=present==0?null:input;int? result=Shift(value,delta);return new WideValue((long)delta^((long)input<<32)^result.GetValueOrDefault(-7)).Value;}}";
            ManagedSourceBodyWorldResultV1 Compile(string text)=>ManagedSourceCompanionV1.CompileBodyWorld(new("NullableMapMixed",[new("/src/MapMixed.cs",text)],references),new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Mixed map "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var image=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(image.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',image.Diagnostics));return image;}
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);var inline=Compile(source.Replace("result=Shift(value,delta)","result="+expression));Link(inline);
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Mixed map repeat/maps/image");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1})foreach(long raw in new[]{long.MinValue,-1L,0L,1L,long.MaxValue,1L<<40,(1L<<40)+7}){long delta=wide?raw:unchecked((int)raw);object operand=wide?(object)delta:unchecked((int)raw);long expected=delta^((long)input<<32)^(present==0?-7L:unchecked(input-(int)delta));if(!Equals(host.Invoke(null,[input,present,operand]),expected)||!Equals(oracle.Invoke(null,[input,present,operand]),expected))throw new Exception("Mixed map signed operand/high bits/truncation/order oracle");}
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,wide?"nullable-map-i8-artifacts":"nullable-map-i4-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS nullable input/return map + signed "+type+" operand NoHeap/source/image/host/inline/full payload/presence/high bits/truncation/order/preservation/repeat/maps");
        }
    }
    public static void RunGenericDefault() => RunGenericPayload(construction: true, genericDefault: true);
    public static void RunStructScalarDefault(bool genericMethod=false,bool wideMethod=false,bool binaryMethod=false)
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(string ownerType in new[]{"int","long"}) foreach(bool transitive in new[]{false,true}) {
            const string adjust="private static int Adjust(int value)=>unchecked(value+1);";
            string helpers=genericMethod?(transitive?"private static U Step<U>(U value) where U:struct=>value;private static U Adjust<U>(U value) where U:struct=>Step<U>(value);":"private static U Adjust<U>(U value) where U:struct=>value;"):transitive?"private static int Step(int value)=>unchecked(value+1);private static int Adjust(int value)=>Step(value);":adjust;
            if(binaryMethod)helpers=helpers.Replace("(U value)","(U value,U other)").Replace("=>value;","=>other;").Replace("Step<U>(value)","Step<U>(value,other)");
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Mapper<O> where O:struct{"+helpers+
                "private static int? Copy(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust(fallback)));public static long Use(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=Copy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}}public static class Fixture{[HybridCpuKernelNoHeap]public static long Read(int input,int delta)=>new WideValue(Mapper<"+ownerType+">.Use(input,delta)).Value;}";
            if(genericMethod)source=source.Replace("Adjust(fallback)","Adjust<int>(fallback)");
            if(wideMethod)source=source.Replace("Adjust<int>(fallback)","unchecked((int)Adjust<long>(fallback))").Replace("int delta","long delta").Replace("int fallback","long fallback").Replace("new int?(unchecked(input+delta))","new int?(unchecked(input+(int)delta))");
            string scalarType=wideMethod?"long":"int";
            if(binaryMethod)source=source.Replace("Adjust<"+scalarType+">(fallback)","Adjust<"+scalarType+">(fallback,unchecked(fallback+1))");
            string selectedCall="Adjust<"+scalarType+">(fallback"+(binaryMethod?",unchecked(fallback+1)":"")+")";
            string defaultExpression=wideMethod?"unchecked((int)"+selectedCall+")":genericMethod?selectedCall:"Adjust(fallback)";
            ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null,CancellationToken token=default)=>ManagedSourceCompanionV1.CompileBodyWorld(new("StructScalarDefault",[new("/src/StructScalar.cs",text)],references),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0],token);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result) {
                if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Struct scalar default: "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));
                var image=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(image.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',image.Diagnostics));return image;
            }
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);
            var inline=Compile(source.Replace(defaultExpression,binaryMethod?(wideMethod?"unchecked((int)(fallback+1))":"unchecked(fallback+1)"):wideMethod?"unchecked((int)fallback)":genericMethod?"fallback":"unchecked(fallback+1)"));Link(inline);
            var concrete=Compile(source.Replace("Mapper<O> where O:struct","Mapper").Replace("Mapper<"+ownerType+">.Use","Mapper.Use")
                .Replace("U Adjust<U>(U value) where U:struct",scalarType+" Adjust("+scalarType+" value)").Replace("U Step<U>(U value) where U:struct",scalarType+" Step("+scalarType+" value)")
                .Replace("U Adjust<U>(U value,U other) where U:struct",scalarType+" Adjust("+scalarType+" value,"+scalarType+" other)").Replace("U Step<U>(U value,U other) where U:struct",scalarType+" Step("+scalarType+" value,"+scalarType+" other)")
                .Replace("Step<U>(value)","Step(value)").Replace("Step<U>(value,other)","Step(value,other)").Replace(selectedCall,"Adjust(fallback"+(binaryMethod?",unchecked(fallback+1)":"")+")"));var scalar=Link(concrete);
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest)||
                image.MethodObjects.Single(row=>row.MethodIdentity.Contains(".Adjust(",StringComparison.Ordinal)).CodeSha256!=scalar.MethodObjects.Single(row=>row.MethodIdentity.Contains(".Adjust(",StringComparison.Ordinal)).CodeSha256)throw new Exception("Struct scalar deterministic PE/PDB/maps/image/independent helper bytes");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long delta in wideMethod?new[]{long.MinValue,(long)int.MinValue,-1L,0L,1L,int.MaxValue,long.MaxValue,1L<<40,(1L<<40)+7}:new long[]{int.MinValue,-1,0,1,int.MaxValue}) {
                long expected=((long)input<<32)^delta^(input<0?unchecked((int)delta+(genericMethod&&!binaryMethod?0:1)):unchecked(input+(int)delta));object operand=wideMethod?(object)delta:unchecked((int)delta);
                if(!Equals(host.Invoke(null,[input,operand]),expected)||!Equals(oracle.Invoke(null,[input,operand]),expected))throw new Exception("Struct scalar host/inline/presence/extrema/high bits/overflow/input preservation");
            }
            using(var pe=new PEReader(new MemoryStream(actual.Source.Pe!))) {
                var md=pe.GetMetadataReader();var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)==(transitive?"Step":"Adjust")));
                var body=pe.GetMethodBody(method.RelativeVirtualAddress);
                if(!md.GetBlobBytes(method.Signature).SequenceEqual(binaryMethod?new byte[]{0x10,1,2,0x1e,0,0x1e,0,0x1e,0}:genericMethod?new byte[]{0x10,1,1,0x1e,0,0x1e,0}:new byte[]{0,1,8,8})||!body.GetILBytes()!.SequenceEqual(genericMethod?new byte[]{binaryMethod?(byte)3:(byte)2,0x2a}:new byte[]{2,0x17,0x58,0x2a})||!body.LocalSignature.IsNil||body.ExceptionRegions.Length!=0||method.GetGenericParameters().Count!=(genericMethod?1:0)||md.GetTypeDefinition(method.GetDeclaringType()).GetGenericParameters().Count!=1)throw new Exception("Struct scalar exact emitted IL/signature/owner definition oracle");
                if(genericMethod){var parameter=md.GetGenericParameter(method.GetGenericParameters().Single());if(parameter.Index!=0||parameter.Attributes!=(GenericParameterAttributes.NotNullableValueTypeConstraint|GenericParameterAttributes.DefaultConstructorConstraint)||parameter.GetConstraints().Count!=1)throw new Exception("Struct scalar emitted method constraint oracle");}
                if(wideMethod&&!Enumerable.Range(1,md.GetTableRowCount(TableIndex.MethodSpec)).Any(row=>md.GetBlobBytes(md.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row)).Signature).SequenceEqual(new byte[]{0x0a,1,0x0a})))throw new Exception("Constrained scalar selected I8 MethodSpec oracle");
            }
            foreach(string badHelper in wideMethod||binaryMethod?Array.Empty<string>():new[]{"private static int Adjust(int value){System.GC.Collect();return value;}","private static int Adjust(int value)=>Adjust(value);","private static int Adjust(int value)=>checked(value+1);","private static int Adjust(int value)=>100/value;","private static int Adjust(int value){try{return value;}finally{System.GC.Collect();}}","[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]private static int Adjust(int value)=>value;"}) {
                string negative=source.Replace(helpers,badHelper);
                if(genericMethod){negative=negative.Replace("Adjust<int>(fallback)","Adjust(fallback)");negative=negative.Replace(badHelper,"private static U Keep<U>(U value) where U:struct=>value;"+badHelper.Replace("=>Adjust(value)","=>Keep<int>(Adjust(value))"));}
                var denied=Compile(negative);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Struct scalar effect/cycle/fault/EH/synchronized gate: "+badHelper+";"+string.Join(';',denied.Source.Diagnostics)+";"+string.Join(';',denied.Compilation?.Diagnostics??[]));
            }
            foreach(string bad in new[]{source.Replace("where O:struct{","where O:unmanaged{"),source.Replace("where O:struct{","where O:struct,System.IComparable<O>{"),source.Replace("Mapper<"+ownerType+">.Use","Mapper<uint>.Use"),source.Replace("Mapper<"+ownerType+">.Use","Mapper<short>.Use")}) {
                var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Struct scalar owner constraint/width gate");
            }
            if(genericMethod) {
                foreach(string bad in new[]{source.Replace("where U:struct=>","where U:unmanaged=>"),source.Replace("where U:struct=>","where U:struct,System.IComparable<U> =>"),
                    source.Replace("private static U Adjust","public static U Adjust"),source.Replace(defaultExpression,wideMethod?"unchecked((int)Adjust<ulong>(unchecked((ulong)fallback)"+(binaryMethod?",unchecked((ulong)(fallback+1))":"")+"))":"(int)Adjust<uint>(unchecked((uint)fallback)"+(binaryMethod?",unchecked((uint)(fallback+1))":"")+")"),
                    source.Replace(helpers,"private static U Adjust<U>(U value) where U:struct{System.GC.Collect();return value;}"),
                    source.Replace(helpers,"private static U Adjust<U>(U value) where U:struct=>Adjust<U>(value);"),
                    source.Replace(helpers,"private static U Adjust<U>(U value) where U:struct{try{return value;}finally{System.GC.Collect();}}"),
                    source.Replace(helpers,"[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]private static U Adjust<U>(U value) where U:struct=>value;"),
                    source.Replace("Mapper<O> where O:struct{","Mapper<O> where O:struct{static Mapper(){System.GC.Collect();}"),
                    source.Replace(binaryMethod?"U Adjust<U>(U value,U other)":"U Adjust<U>(U value)",binaryMethod?"U Adjust<U>(ref U value,U other)":"U Adjust<U>(ref U value)").Replace(defaultExpression,(wideMethod?"unchecked((int)":"")+"Adjust<"+scalarType+">(ref fallback"+(binaryMethod?",unchecked(fallback+1)":"")+")"+(wideMethod?")":""))}.Select(text=>binaryMethod?text.Replace("U Adjust<U>(U value)","U Adjust<U>(U value,U other)").Replace("Adjust<U>(value)","Adjust<U>(value,other)"):text)) {
                    var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained scalar method final purity/constraint/public/width/cycle/EH/implementation/cctor/byref gate: "+string.Join(';',denied.Source.Diagnostics)+";"+string.Join(';',denied.Compilation?.Diagnostics??[]));
                }
                if(wideMethod||binaryMethod){var denied=Compile(source.Replace(defaultExpression,(wideMethod?"unchecked((int)":"")+"Adjust<"+scalarType+">(100/fallback"+(binaryMethod?",unchecked(fallback+1)":"")+")"+(wideMethod?")":"")));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained default first argument possible fault/eager gate");}
                if(binaryMethod){var denied=Compile(source.Replace("U Adjust<U>(U value,U other)","U Adjust<U>(U value,U other,U e0,U e1,U e2,U e3,U e4,U e5,U e6)").Replace(selectedCall,"Adjust<"+scalarType+">(fallback,unchecked(fallback+1),fallback,fallback,fallback,fallback,fallback,fallback,fallback)"));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained scalar arity9 remains closed");}
                var selector=new RestrictedCilMethodSelectorV1("Mapper`1","Adjust",GenericTypeArguments:[new(ownerType=="int"?"System.Int32":"System.Int64",ownerType=="int"?RestrictedCilTypeV1.Int32:RestrictedCilTypeV1.Int64)],GenericMethodArguments:[new(wideMethod?"System.Int64":"System.Int32",wideMethod?RestrictedCilTypeV1.Int64:RestrictedCilTypeV1.Int32)]);
                if(new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(actual.Source.Pe!,selector).Status!=RestrictedCilImportStatusV1.Unsupported||new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,actual.Source.Pe!,"scalar-method-root",[selector],[],MetadataOnlyModules:[new(references[0],"core")])).Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained scalar method standalone/root authority");
                foreach(bool selected in new[]{false,true}) {
                    var denied=ManagedSourceCompanionV1.CompileBodyWorld(new("ScalarMethodRoles",[new("/src/Role.cs","using HybridCPU.Compiler.Profiles;public static class Primary{[HybridCpuKernelNoHeap]public static long Read(int input,int delta)=>Fixture.Read(input,delta);}")],[..references,actual.Source.Pe!]),new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Primary","Read")],selected?[4]:[],selected?[0]:[0,4]);
                    if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained scalar method external declaration/body authority");
                }
                using var pe=new PEReader(new MemoryStream(actual.Source.Pe!));var md=pe.GetMetadataReader();
                var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Adjust"));var handle=method.GetGenericParameters().Single();
                foreach(int mutation in new[]{0,1,2,3,4}) {
                    byte[] forged=actual.Source.Pe!.ToArray();int row=pe.PEHeaders.MetadataStartOffset+md.GetTableMetadataOffset(TableIndex.GenericParam)+(MetadataTokens.GetRowNumber(handle)-1)*md.GetTableRowSize(TableIndex.GenericParam);
                    if(mutation==0)BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(row),1);
                    else BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(row+2),mutation switch{1=>(ushort)0,2=>(ushort)GenericParameterAttributes.DefaultConstructorConstraint,3=>(ushort)GenericParameterAttributes.ReferenceTypeConstraint,_=>(ushort)(GenericParameterAttributes.NotNullableValueTypeConstraint|GenericParameterAttributes.DefaultConstructorConstraint|GenericParameterAttributes.Covariant)});
                    var denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,forged,"scalar-method-forged",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));
                    if(denied.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Constrained scalar method forged index/flags/variance");
                }
            }
            foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}})
                if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Struct scalar original budgets");
            var noheap=Compile(source.Replace("=>new WideValue(Mapper<"+ownerType+">.Use(input,delta)).Value;","{var heap=new object();return 0;}"));if(noheap.Source.Pe is not null||noheap.Compilation is not null||!noheap.Source.Diagnostics.Any(row=>row.Id=="HC0004"))throw new Exception("Struct scalar NoHeap before emit: "+string.Join(';',noheap.Source.Diagnostics));
            var syntax=Compile(source+" invalid");if(syntax.Source.Pe is not null||syntax.Compilation is not null||!syntax.Source.Diagnostics.Any(row=>row.Severity=="Error"))throw new Exception("Struct scalar source diagnostics precedence");
            using(var canceled=new CancellationTokenSource()){canceled.Cancel();try{Compile(source,token:canceled.Token);throw new Exception("Struct scalar cancellation");}catch(OperationCanceledException){}}
            Link(Compile(source));
            string output=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"struct-scalar-"+(genericMethod?"method-":"")+(binaryMethod?"binary-":"")+(wideMethod?"i8-":"")+ownerType+(transitive?"-transitive":"")+"-artifacts");Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(output,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(output,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(output,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(output,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS struct owner "+ownerType+(transitive?" transitive":" direct")+(genericMethod?" constrained method":"")+(binaryMethod?" binary":"")+(wideMethod?" selected Int64":"")+" scalar default NoHeap/source/exact emitted IL/metadata/helper bytes/host/inline/overflow/preservation/repeat PE/PDB/maps/image/effect/cycle/fault/EH/constraints/width/budget/cancel/reuse gates");
        }
    }
    public static void RunStructScalarArity(bool genericZero = false)
    {
        static bool IsGenericZeroBody(MetadataReader md, MethodBodyBlock body)
        {
            byte[] il=body.GetILBytes()!;
            if(il.Length!=10 || il[0]!=0x12 || il[1]!=0 || il[2]!=0xfe || il[3]!=0x15 || il[8]!=0x06 || il[9]!=0x2a || body.LocalSignature.IsNil || body.MaxStack!=1) return false;
            var type=MetadataTokens.EntityHandle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(4,4)));
            return type.Kind==HandleKind.TypeSpecification && md.GetBlobBytes(md.GetTypeSpecification((TypeSpecificationHandle)type).Signature).SequenceEqual(new byte[]{0x1e,0}) && md.GetBlobBytes(md.GetStandaloneSignature(body.LocalSignature).Signature).SequenceEqual(new byte[]{7,1,0x1e,0});
        }
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(int count in Enumerable.Range(3,6))foreach(bool wide in new[]{false,true})foreach(string ownerType in new[]{"int","long"})foreach(bool transitive in new[]{false,true}) {
            string scalarType=wide?"long":"int",parameters=string.Join(',',Enumerable.Range(0,count).Select(i=>"U p"+i)),names=string.Join(',',Enumerable.Range(0,count).Select(i=>"p"+i));
            string arguments=string.Join(',',Enumerable.Range(0,count).Select(i=>i==0?"fallback":"unchecked(fallback+"+i+")"));
            string resultExpression=genericZero?"default(U)":"p"+(count-1);
            string leaf="private static U Adjust<U>("+parameters+") where U:struct=>"+resultExpression+";";
            string helpers=transitive?leaf.Replace("Adjust<U>","Step<U>")+leaf.Replace("=>"+resultExpression,"=>Step<U>("+names+")"):leaf;
            string call="Adjust<"+scalarType+">("+arguments+")",expression=wide?"unchecked((int)"+call+")":call;
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Mapper<O> where O:struct{"+helpers+"private static int? Copy(int? value,"+scalarType+" fallback)=>new int?(value.GetValueOrDefault("+expression+"));public static long Use(int input,"+scalarType+" delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=Copy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}}public static class Fixture{[HybridCpuKernelNoHeap]public static long Read(int input,"+scalarType+" delta)=>new WideValue(Mapper<"+ownerType+">.Use(input,delta)).Value;}";
            ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null,CancellationToken token=default)=>ManagedSourceCompanionV1.CompileBodyWorld(new("StructScalarArity",[new("/src/Arity.cs",text)],references),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0],token);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Constrained arity "+count+" "+scalarType+": "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);
            var inline=Compile(source.Replace(expression,genericZero?"0":"unchecked((int)(fallback+"+(count-1)+"))"));Link(inline);
            string concreteParameters=parameters.Replace("U ",scalarType+" ");
            var concrete=Link(Compile(source.Replace("Mapper<O> where O:struct","Mapper").Replace("Mapper<"+ownerType+">.Use","Mapper.Use").Replace("U Adjust<U>("+parameters+") where U:struct",scalarType+" Adjust("+concreteParameters+")").Replace("U Step<U>("+parameters+") where U:struct",scalarType+" Step("+concreteParameters+")").Replace("Step<U>("+names+")","Step("+names+")").Replace(call,"Adjust("+arguments+")").Replace("default(U)","default("+scalarType+")")));
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest)||!genericZero && image.MethodObjects.Single(row=>row.MethodIdentity.Contains(".Adjust(",StringComparison.Ordinal)).CodeSha256!=concrete.MethodObjects.Single(row=>row.MethodIdentity.Contains(".Adjust(",StringComparison.Ordinal)).CodeSha256)throw new Exception("Constrained arity deterministic PE/PDB/maps/image/independent concrete helper bytes");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long raw in new[]{long.MinValue,(long)int.MinValue,-1L,0L,1L,int.MaxValue,long.MaxValue,1L<<40,(1L<<40)+7}) {
                long delta=wide?raw:unchecked((int)raw);object operand=wide?(object)delta:unchecked((int)delta);long expected=((long)input<<32)^delta^(input<0?(genericZero?0:unchecked((int)delta+count-1)):unchecked(input+(int)delta));
                if(!Equals(host.Invoke(null,[input,operand]),expected)||!Equals(oracle.Invoke(null,[input,operand]),expected))throw new Exception("Constrained arity independent host/inline/null/present/high bits/overflow/preservation");
            }
            using(var pe=new PEReader(new MemoryStream(actual.Source.Pe!))){var md=pe.GetMetadataReader();var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)==(transitive?"Step":"Adjust")));var body=pe.GetMethodBody(method.RelativeVirtualAddress);byte[] signature=[0x10,1,(byte)count,0x1e,0,..Enumerable.Range(0,count).SelectMany(_=>new byte[]{0x1e,0})];byte[] il=count<=4?[(byte)(count+1),0x2a]:[0x0e,(byte)(count-1),0x2a];var gp=md.GetGenericParameter(method.GetGenericParameters().Single());if(!md.GetBlobBytes(method.Signature).SequenceEqual(signature)||(!genericZero && (!body.GetILBytes()!.SequenceEqual(il)||!body.LocalSignature.IsNil))||genericZero && !IsGenericZeroBody(md,body)||body.ExceptionRegions.Length!=0||gp.Index!=0||gp.Attributes!=(GenericParameterAttributes.NotNullableValueTypeConstraint|GenericParameterAttributes.DefaultConstructorConstraint)||gp.GetConstraints().Count!=1)throw new Exception("Constrained arity emitted CIL/signature/definition oracle");}
            // Reuse signed role/identity/body controls once per width/arity. Every
            // owner/chain still gets full positive/repeat/host/byte proof above.
            if(ownerType=="int"&&!transitive) {
                void Deny(string text){var denied=Compile(text);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained arity negative "+count+": "+string.Join(';',denied.Source.Diagnostics)+";"+string.Join(';',denied.Compilation?.Diagnostics??[]));}
                foreach(string bad in new[]{source.Replace("private static U Adjust","public static U Adjust"),source.Replace("where U:struct=>","where U:unmanaged=>"),source.Replace("where U:struct=>","where U:struct,System.IComparable<U> =>"),source.Replace("=>"+resultExpression+";","{System.GC.Collect();return p0;}"),source.Replace("=>"+resultExpression+";","=>Adjust<U>("+names+");"),source.Replace("=>"+resultExpression+";","{try{return p0;}finally{System.GC.Collect();}}"),source.Replace("private static U Adjust","[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]private static U Adjust"),source.Replace("Mapper<O> where O:struct{","Mapper<O> where O:struct{static Mapper(){System.GC.Collect();}"),source.Replace("where O:struct{","where O:unmanaged{"),source.Replace("Mapper<int>.Use","Mapper<uint>.Use")})Deny(bad);
                Deny(source.Replace("U Adjust<U>(U p0,","U Adjust<U>(ref U p0,").Replace(call,"Adjust<"+scalarType+">(ref "+arguments+")"));
                foreach(int faultIndex in Enumerable.Range(0,count-1)){string[] badArguments=arguments.Split(',');badArguments[faultIndex]="100/fallback";Deny(source.Replace(call,"Adjust<"+scalarType+">("+string.Join(',',badArguments)+")"));}
                string extras=string.Join(',',Enumerable.Range(count,9-count).Select(i=>"U p"+i));string extraArgs=string.Join(',',Enumerable.Repeat("fallback",9-count));
                if(count<9)Deny(source.Replace("U Adjust<U>("+parameters+")","U Adjust<U>("+parameters+","+extras+")").Replace(call,"Adjust<"+scalarType+">("+arguments+","+extraArgs+")"));
                Deny(source.Replace("U Adjust<U>("+parameters+")","U Adjust<U>("+parameters.Replace("U p0",scalarType+" p0")+")"));
                var selector=new RestrictedCilMethodSelectorV1("Mapper`1","Adjust",GenericTypeArguments:[new("System.Int32",RestrictedCilTypeV1.Int32)],GenericMethodArguments:[new(wide?"System.Int64":"System.Int32",wide?RestrictedCilTypeV1.Int64:RestrictedCilTypeV1.Int32)]);
                if(new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(actual.Source.Pe!,selector).Status!=RestrictedCilImportStatusV1.Unsupported||new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,actual.Source.Pe!,"arity-root",[selector],[],MetadataOnlyModules:[new(references[0],"core")])).Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained arity root/standalone authority");
                foreach(bool selected in new[]{false,true}){var denied=ManagedSourceCompanionV1.CompileBodyWorld(new("ArityRoles",[new("/src/Role.cs","using HybridCPU.Compiler.Profiles;public static class Primary{[HybridCpuKernelNoHeap]public static long Read(int input,"+scalarType+" delta)=>Fixture.Read(input,delta);}")],[..references,actual.Source.Pe!]),new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Primary","Read")],selected?[4]:[],selected?[0]:[0,4]);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained arity external role");}
                foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumArguments=count-1},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=count-1}})if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Constrained arity original budgets");
                var noheap=Compile(source.Replace("=>new WideValue(Mapper<int>.Use(input,delta)).Value;","{var heap=new object();return 0;}"));if(noheap.Source.Pe is not null||noheap.Compilation is not null||!noheap.Source.Diagnostics.Any(row=>row.Id=="HC0004"))throw new Exception("Constrained arity NoHeap before emit");
                var syntax=Compile(source+" invalid");if(syntax.Source.Pe is not null||syntax.Compilation is not null)throw new Exception("Constrained arity source diagnostics");using(var cancel=new CancellationTokenSource()){cancel.Cancel();try{Compile(source,token:cancel.Token);throw new Exception("Constrained arity cancel");}catch(OperationCanceledException){}}Link(Compile(source));
            }
            string output=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,(genericZero?"struct-zero-":"struct-arity-")+count+"-"+scalarType+"-"+ownerType+(transitive?"-transitive":"")+"-artifacts");Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(output,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(output,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(output,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(output,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS constrained scalar zero="+genericZero+" arity="+count+" method="+scalarType+" owner="+ownerType+" transitive="+transitive+" source/CIL/concrete bytes/host/inline/overflow/high bits/repeat/maps/role/body/context/fault/eager/budget/cancel controls");
        }
    }
    private static void RunGenericPayload(bool presence = false, bool construction = false, bool genericDefault = false, bool fallbackArgument = false, string? ownerType = null, bool ownerConstraint = false)
    {
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")), File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        string expression = fallbackArgument ? "new T?(value.GetValueOrDefault(fallback))" : genericDefault ? "new T?(value.GetValueOrDefault(default(T)))" : construction ? "new T?(value.GetValueOrDefault())" : presence ? "value.HasValue?value:null" : "value";
        string declaration = "private static T? Copy<T>(T? value"+(fallbackArgument?",T fallback":"")+") where T:struct=>" + expression + ";";
        string call="Copy<int>(value"+(fallbackArgument?",delta":"")+")";
        string source = "using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture{" + declaration +
            "[HybridCpuKernelNoHeap]public static long Read(int input,int delta){int? value=input<0?null:"+(construction?"new int?(unchecked(input+delta))":"unchecked(input+delta)")+";int? shifted="+call+";return new WideValue(((long)input<<32)^delta^shifted.GetValueOrDefault(-7)).Value;}}";
        if(ownerType is not null) source=source.Replace("public static class Fixture{","public static class Mapper<O>{").Replace("public static long Read(","public static long Use(")+
            "public static class Fixture{[HybridCpuKernelNoHeap]public static long Read(int input,int delta)=>new WideValue(Mapper<"+ownerType+">.Use(input,delta)).Value;}";
        if(ownerConstraint) source=source.Replace("class Mapper<O>{","class Mapper<O> where O:struct{");
        ManagedSourceBodyWorldResultV1 Compile(string text, RestrictedCilImportBudgetsV1? budgets = null) =>
            ManagedSourceCompanionV1.CompileBodyWorld(new("NullableGenericPayload",[new("/src/Payload.cs",text)],references),
                new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result) {
            if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success) throw new Exception("Generic nullable payload "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));
            var image=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());
            if(image.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',image.Diagnostics));return image;
        }
        var actual=Compile(source);var repeated=Compile(source);
        if(genericDefault && actual.Source.Pe is not null) { string output=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-generic-default-artifacts");Directory.CreateDirectory(output);File.WriteAllBytes(Path.Combine(output,"Primary.dll"),actual.Source.Pe);File.WriteAllText(Path.Combine(output,"Primary.cs"),source); }
        var image=Link(actual);var repeat=Link(repeated);
        string concreteExpression=expression.Replace("new T?","new int?").Replace("default(T)","default(int)");
        var scalar=Compile(source.Replace(declaration,"private static int? Copy(int? value"+(fallbackArgument?",int fallback":"")+")=>"+concreteExpression+";").Replace(call,"Copy(value"+(fallbackArgument?",delta":"")+")"));var scalarImage=Link(scalar);
        var inline=Compile(source.Replace("shifted="+call,"shifted="+concreteExpression.Replace("fallback","delta")));Link(inline);
        if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||
            !image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||
            actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest)||
            image.MethodObjects.Single(row=>row.MethodIdentity.Contains(".Copy",StringComparison.Ordinal)).CodeSha256!=
                scalarImage.MethodObjects.Single(row=>row.MethodIdentity.Contains(".Copy",StringComparison.Ordinal)).CodeSha256)
            throw new Exception("Generic nullable payload repeat/maps/independent non-generic helper bytes");
        var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
        var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
        foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}) {
            long expected=((long)input<<32)^delta^(input<0?(fallbackArgument?delta:construction?0:-7):unchecked(input+delta));
            if(!Equals(host.Invoke(null,[input,delta]),expected)||!Equals(oracle.Invoke(null,[input,delta]),expected))throw new Exception("Generic nullable payload independent scalar/presence/overflow/preservation");
        }
        using(var pe=new PEReader(new MemoryStream(actual.Source.Pe!))) {
            var md=pe.GetMetadataReader();var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Copy"));
            if(method.GetGenericParameters().Count!=1||pe.GetMethodBody(method.RelativeVirtualAddress).ExceptionRegions.Length!=0||
                (!presence && !construction && !pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!.SequenceEqual(new byte[]{0x02,0x2a}) ||
                 (presence || construction) && !pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!.Contains((byte)0x28) ||
                 construction && ((!genericDefault && !pe.GetMethodBody(method.RelativeVirtualAddress).LocalSignature.IsNil) || !pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!.Contains((byte)0x73)))||
                !md.GetBlobBytes(method.Signature).Contains((byte)0x1e))throw new Exception("Generic nullable payload emitted CIL/metadata oracle");
            if(genericDefault) {
                var body=pe.GetMethodBody(method.RelativeVirtualAddress);byte[] il=body.GetILBytes()!;
                if(body.LocalSignature.IsNil||!md.GetBlobBytes(md.GetStandaloneSignature(body.LocalSignature).Signature).SequenceEqual(new byte[]{0x07,0x01,0x1e,0x00}))throw new Exception("Generic default exact emitted local context");
                int zero=Enumerable.Range(0,il.Length-1).Single(index=>il[index]==0xfe&&il[index+1]==0x15);
                var section=pe.PEHeaders.SectionHeaders.Single(row=>method.RelativeVirtualAddress>=row.VirtualAddress&&method.RelativeVirtualAddress<row.VirtualAddress+row.SizeOfRawData);
                int start=section.PointerToRawData+method.RelativeVirtualAddress-section.VirtualAddress;
                start+=(actual.Source.Pe![start]&3)==2?1:(actual.Source.Pe![start+1]>>4)*4;
                foreach(bool incoming in new[]{false,true}) {
                    byte[] forged=actual.Source.Pe!.ToArray();
                    if(incoming){forged[start]=0x2b;forged[start+1]=checked((byte)(zero-2));}
                    else BinaryPrimitives.WriteInt32LittleEndian(forged.AsSpan(start+zero+2,4),MetadataTokens.GetToken(md.TypeDefinitions.Single(h=>md.GetString(md.GetTypeDefinition(h).Name)=="WideValue")));
                    var denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,forged,"generic-default-forged",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));
                    if(denied.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Generic default wrong type/incoming initobj edge gate");
                }
            }
        }
        foreach(string bad in new[]{source.Replace("private static T? Copy","public static T? Copy"),
            source.Replace("where T:struct=>","where T:struct,System.IComparable<T> =>"),
            source.Replace("where T:struct=>","where T:unmanaged=>"),
            source.Replace("=>"+expression+";","=>Copy<T>(value"+(fallbackArgument?",fallback":"")+");"),
            source.Replace("=>"+expression+";","{System.GC.Collect();return value;}"),
            source.Replace("=>"+expression+";","{try{return value;}finally{System.GC.Collect();}}"),
            source.Replace(call,"(int?)Copy<long>((long?)value"+(fallbackArgument?",(long)delta":"")+")"),
            source.Replace(call,"(int?)Copy<uint>((uint?)value"+(fallbackArgument?",unchecked((uint)delta)":"")+")")}) {
            var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Generic nullable payload public/constraint/effect/EH/cycle/width gates: "+string.Join(';',denied.Source.Diagnostics)+";"+string.Join(';',denied.Compilation?.Diagnostics??[]));
        }
        foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumArguments=1},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}})
            if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Generic nullable payload original budgets");
        if(ownerType is not null) foreach(string bad in new[]{
            source.Replace("Mapper<"+ownerType+">.Use","Mapper<uint>.Use"),
            source.Replace(ownerConstraint?"class Mapper<O> where O:struct{":"class Mapper<O>{","class Mapper<O> where O:struct,System.IComparable<O>{"),
            source.Replace(ownerConstraint?"class Mapper<O> where O:struct{":"class Mapper<O>{","class Mapper<O,P>{").Replace("Mapper<"+ownerType+">.Use","Mapper<"+ownerType+",int>.Use")}) {
            var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Constrained nullable owner signed identity/constraint/arity gates: "+string.Join(';',denied.Source.Diagnostics)+";"+string.Join(';',denied.Compilation?.Diagnostics??[]));
        }
        if(ownerConstraint) {
            foreach(string bad in new[]{source.Replace("where O:struct{","where O:unmanaged{"),
                source.Replace("Mapper<"+ownerType+">.Use","Mapper<short>.Use")}) {
                var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Struct owner unmanaged/narrow selection gate");
            }
            RestrictedCilMethodSelectorV1 ownerEntry=new("Mapper`1","Use",GenericTypeArguments:[new(ownerType=="int"?"System.Int32":"System.Int64",ownerType=="int"?RestrictedCilTypeV1.Int32:RestrictedCilTypeV1.Int64)]);
            var standalone=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(actual.Source.Pe!,ownerEntry);
            var rootDenied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,actual.Source.Pe!,"struct-owner-root",[ownerEntry],[],MetadataOnlyModules:[new(references[0],"core")]));
            if(standalone.Status!=RestrictedCilImportStatusV1.Unsupported||rootDenied.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Struct owner standalone/root authority gate");
            const string roleSource="using HybridCPU.Compiler.Profiles;public static class Primary{[HybridCpuKernelNoHeap]public static long Read(int input,int delta)=>Fixture.Read(input,delta);}";
            foreach(bool selected in new[]{false,true}) {
                var denied=ManagedSourceCompanionV1.CompileBodyWorld(new("StructOwnerRoles",[new("/src/Role.cs",roleSource)],[..references,actual.Source.Pe!]),new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Primary","Read")],selected?[4]:[],selected?[0]:[0,4]);
                if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Struct owner external metadata/body role gate");
            }
            using var reader=new PEReader(new MemoryStream(actual.Source.Pe!));var md=reader.GetMetadataReader();
            var owner=md.GetTypeDefinition(md.TypeDefinitions.Single(h=>md.GetString(md.GetTypeDefinition(h).Name)=="Mapper`1"));
            var parameterHandle=owner.GetGenericParameters().Single();var parameter=md.GetGenericParameter(parameterHandle);
            if(parameter.Attributes!=(GenericParameterAttributes.NotNullableValueTypeConstraint|GenericParameterAttributes.DefaultConstructorConstraint)||parameter.Index!=0||parameter.GetConstraints().Count!=1)throw new Exception("Struct owner emitted definition constraint oracle");
            foreach(int mutation in new[]{0,1,2,3,4,5}) {
                byte[] forged=actual.Source.Pe!.ToArray();
                int row=reader.PEHeaders.MetadataStartOffset+md.GetTableMetadataOffset(TableIndex.GenericParam)+(MetadataTokens.GetRowNumber(parameterHandle)-1)*md.GetTableRowSize(TableIndex.GenericParam);
                if(mutation==0)BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(row),1);
                else if(mutation<5)BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(row+2),mutation switch{1=>(ushort)0,2=>(ushort)GenericParameterAttributes.DefaultConstructorConstraint,3=>(ushort)GenericParameterAttributes.ReferenceTypeConstraint,_=>(ushort)(parameter.Attributes|GenericParameterAttributes.Covariant)});
                else {
                    int constraintRow=reader.PEHeaders.MetadataStartOffset+md.GetTableMetadataOffset(TableIndex.GenericParamConstraint)+(MetadataTokens.GetRowNumber(parameter.GetConstraints().Single())-1)*md.GetTableRowSize(TableIndex.GenericParamConstraint);
                    if(md.GetTableRowSize(TableIndex.GenericParamConstraint)!=4)throw new Exception("Struct owner constraint fixture width");
                    BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(constraintRow+2),checked((ushort)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h=>md.GetString(md.GetTypeDefinition(h).Name)=="WideValue"))<<2)));
                }
                var denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,forged,"struct-owner-forged",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));
                if(denied.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Struct owner forged index/flags/variance/nominal constraint admitted");
            }
        }
        if(fallbackArgument)foreach(string bad in new[]{
            source.Replace(",T fallback)",",T fallback,T extra)").Replace(call,"Copy<int>(value,delta,delta)"),
            source.Replace(",T fallback)",",T? fallback)").Replace(expression,"value"),
            source.Replace(",T fallback)",",ref T fallback)").Replace(call,"Copy<int>(value,ref delta)")}) {
            var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Generic fallback extra/nullable/byref argument gates");
        }
        if(construction)foreach(string deniedExpression in new[]{"(int?)new long?(unchecked(input+delta))","(int?)new uint?(unchecked((uint)(input+delta)))","new int?()"}) {
            var denied=Compile(source.Replace("new int?(unchecked(input+delta))",deniedExpression));
            if(denied.Source.Pe is not null||denied.Compilation is not null||!denied.Source.Diagnostics.Any(row=>row.Id=="HC0006"&&row.Severity=="Error"))throw new Exception("Nullable construction NoHeap exact Int32/one-argument source gate");
        }
        string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,ownerType is not null?"nullable-owner-"+(ownerConstraint?"struct-":"")+ownerType+"-artifacts":fallbackArgument?"nullable-generic-fallback-artifacts":genericDefault?"nullable-generic-default-artifacts":construction?"nullable-generic-factory-artifacts":presence?"nullable-generic-presence-artifacts":"nullable-generic-payload-artifacts");Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);
        File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
        Console.WriteLine("PASS exact selected Nullable<T> Copy<int> "+(fallbackArgument?"generic fallback argument ":genericDefault?"generic default(T) ":construction?"explicit nullable factory ":presence?"HasValue/copy/null ":"")+"struct-constraint NoHeap/source/CIL/image/host/scalar helper bytes/presence/overflow/repeat/maps/constraint/body/width/budget gates");
    }
    private static void RunGenericMap()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(int axis in new[]{0,1,2})foreach(string type in new[]{"int","long"})foreach(string method in axis==2?new[]{"int","long"}:new[]{type}) {
            string owner=axis==0?"Mapper":"Mapper<T>",target=axis==0?"Mapper":"Mapper<"+type+">",generic=axis==1?"":"<U>",context=axis==1?"T":"U",call=axis==1?"":"<"+method+">";
            const string expression="value.HasValue?unchecked(value.GetValueOrDefault()+Adjust<CONTEXT>(delta)):null";
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class "+owner+"{private static int Adjust<V>(int input)=>unchecked(input+1);private static int? Shift"+generic+"(int? value,int delta)=>"+expression.Replace("CONTEXT",context)+";public static long Use(int input,int delta){int? value=input<0?null:input;int? result=Shift"+call+"(value,delta);return new WideValue(((long)input<<32)^delta^result.GetValueOrDefault(-7)).Value;}}public static class Fixture{[HybridCpuKernelNoHeap]public static long Read(int input,int delta)=>"+target+".Use(input,delta);}";
            ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null)=>ManagedSourceCompanionV1.CompileBodyWorld(new("GenericNullableMap",[new("/src/Generic.cs",text)],references),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Generic map "+axis+type+method+" "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Generic map repeat/maps/image");
            string inlineSource=source.Replace("result=Shift"+call+"(value,delta)","result="+expression.Replace("CONTEXT",axis==1?"T":method));var inline=Compile(inlineSource);Link(inline);var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}){long expected=((long)input<<32)^delta^(input<0?-7:unchecked(input+delta+1));if(!Equals(host.Invoke(null,[input,delta]),expected)||!Equals(oracle.Invoke(null,[input,delta]),expected))throw new Exception("Generic map independent scalar/extrema/overflow/preservation");}
            foreach(string bad in new[]{source.Replace("private static int? Shift","public static int? Shift"),source.Replace("unchecked(input+1)","1/input"),source.Replace("unchecked(input+1)","Adjust<V>(input)"),source.Replace("private static int Adjust<V>(int input)=>unchecked(input+1);","private static int Adjust<V>(int input){System.GC.Collect();return input;}")}){var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Generic map public/fault/recursive/effect body gate");}
            foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumArguments=1},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}})if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Generic map original budgets");
            if(axis==0)foreach(string bad in new[]{source.Replace("Shift<"+method+">","Shift<uint>"),source.Replace("Shift<U>(int? value,int delta)=>","Shift<U>(int? value,int delta) where U:struct=>")}){var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Generic nullable context unsigned/constraints gate");}
            if(axis==0&&method=="int"){
                string bothSource=source.Replace("int? result=Shift<int>(value,delta);","int? first=Shift<int>(value,delta);int? result=Shift<long>(first,delta);");var both=Compile(bothSource);Link(both);
                if(both.Compilation!.Methods.Count(row=>row.Identity.MethodName=="Shift")!=2||both.Compilation.Methods.Where(row=>row.Identity.MethodName=="Shift").Select(row=>row.Identity.StableIdentity).Distinct(StringComparer.Ordinal).Count()!=2)throw new Exception("Generic nullable contexts must have distinct exact identities in one graph");
                var bothHost=Assembly.Load(both.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}){long expected=((long)input<<32)^delta^(input<0?-7:unchecked(input+2*unchecked(delta+1)));if(!Equals(bothHost.Invoke(null,[input,delta]),expected))throw new Exception("Generic nullable context coexistence/call ordering/overflow");}
            }
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-map-generic-"+axis+"-"+type+"-"+method+"-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS exact private generic nullable map axis="+axis+" type="+type+" method="+method+" NoHeap/source/image/exact helper context/host/inline/overflow/repeat/maps/budgets/body/ABI gates");
        }
    }
    private static void RunWordComparisons()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach((string name,string expression,Func<int,int,long> expected) in new (string,string,Func<int,int,long>)[]{
            ("branch","unchecked(input+delta)<0?-7L:11L",(input,delta)=>unchecked(input+delta)<0?-7L:11L),
            ("zero","unchecked(input*delta)==0?3L:9L",(input,delta)=>unchecked(input*delta)==0?3L:9L),
            ("unsigned","unchecked((uint)(input+delta))<0x80000001U?5L:13L",(input,delta)=>unchecked((uint)(input+delta))<0x80000001U?5L:13L),
            ("ceq","(long)(unchecked(input+delta)==int.MinValue?1:0)",(input,delta)=>unchecked(input+delta)==int.MinValue?1:0),
            ("clt","(long)(unchecked(input+delta)<0?1:0)",(input,delta)=>unchecked(input+delta)<0?1:0),
            ("cltu","(long)(unchecked((uint)(input+delta))<0x80000001U?1:0)",(input,delta)=>unchecked((uint)(input+delta))<0x80000001U?1:0),
            ("switch","unchecked(input*delta) switch{0=>23L,1=>29L,2=>31L,3=>37L,_=>41L}",(input,delta)=>unchecked(input*delta) switch{0=>23L,1=>29L,2=>31L,3=>37L,_=>41L})}) {
            string source="using HybridCPU.Compiler.Profiles;public static class Fixture{[HybridCpuKernelNoHeap]public static long Read(int input,int delta)=>"+expression+";}";
            ManagedSourceBodyWorldResultV1 Compile()=>ManagedSourceCompanionV1.CompileBodyWorld(new("Word"+name,[new("/src/Word.cs",source)],references),new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Word comparison "+name+" "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
            var actual=Compile();var repeated=Compile();var image=Link(actual);var repeat=Link(repeated);if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Word comparison repeat/maps/image");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;foreach(int input in new[]{int.MinValue,int.MinValue+1,-65536,-1,0,1,3,65536,int.MaxValue})foreach(int delta in new[]{int.MinValue,-65536,-1,0,1,3,65536,int.MaxValue})if(!Equals(host.Invoke(null,[input,delta]),expected(input,delta)))throw new Exception("Word comparison overflow/low32/unsigned boundary oracle");
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"cil-word-"+name+"-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
        }
        Console.WriteLine("PASS I4/U4 comparison/condition/switch after overflow NoHeap/source/image/host/low32/extrema/repeat/maps; native 64-bit comparison operands canonicalized");
    }
    private static void RunMapLoop()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(int kind in new[]{0,1,2,3,4,5,6,7}) {
            string countType=kind==1?"long":"int",loop=kind switch{1=>"for(long i=0;i<count;i++)",2=>"for(uint i=0x7ffffffeU;i<0x80000001U;i++)",3=>"for(ulong i=0x7ffffffffffffffeUL;i<0x8000000000000001UL;i++)",_=>"for(int i=0;i<count;i++)"};
            string update=kind==4?"Adjust(payload,delta)":"unchecked(payload+delta)",helper=kind==4?"private static int Adjust(int input,int delta)=>unchecked(input+delta);":"";
            string declaration=kind==6?"private static int? Shift(int input,int present,int count,int delta)":"private static int? Shift(int? value,"+countType+" count,int delta)";
            string producer="{if("+(kind==6?"present==0":"!value.HasValue")+")return null;int payload="+(kind==6?"input":kind==5?"value.Value":"value.GetValueOrDefault()")+";"+loop+(kind==7?"{switch(i&3){case 0:payload=unchecked(payload+delta);break;case 1:payload=unchecked(payload-delta);break;case 2:payload^=delta;break;default:payload=~payload;break;}}":"payload="+update+";")+"return payload;}";
            string echo="((long)input<<32)^(long)count^delta",consumer="[HybridCpuKernelNoHeap]public static long Read(int input,int present,"+countType+" count,int delta){int? value=present==0?null:input;int? result="+(kind==6?"Shift(input,present,count,delta)":"Shift(value,count,delta)")+";return new WideValue("+echo+"^result.GetValueOrDefault(-7)).Value;}";
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture{"+declaration+producer+helper+consumer+"}";
            ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null)=>ManagedSourceCompanionV1.CompileBodyWorld(new("NullableMapLoop",[new("/src/Loop.cs",text)],references),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Map loop kind="+kind+" "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Map loop repeat/maps/image");
            string scalarConsumer="[HybridCpuKernelNoHeap]public static long Read(int input,int present,"+countType+" count,int delta){if(present==0)return new WideValue("+echo+"^-7L).Value;int payload=input;"+loop+(kind==7?"{int selector=i&3;if(selector==0)payload=unchecked(payload+delta);else if(selector==1)payload=unchecked(payload-delta);else if(selector==2)payload^=delta;else payload=~payload;}":"payload=unchecked(payload+delta);")+"return new WideValue("+echo+"^payload).Value;}";
            var scalar=Compile(source.Replace(consumer,scalarConsumer));Link(scalar);var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(scalar.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1})foreach(long raw in new[]{long.MinValue,(long)int.MinValue,-1L,0L,1L,3L,7L})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}){long count=kind==1?raw:unchecked((int)raw);object operand=kind==1?(object)count:unchecked((int)raw);int trips=kind is 2 or 3?3:count>0?(int)count:0;int payload=unchecked(input+delta*trips);if(kind==7){payload=input;for(int i=0;i<trips;i++)payload=(i&3) switch{0=>unchecked(payload+delta),1=>unchecked(payload-delta),2=>payload^delta,_=>~payload};}long expected=((long)input<<32)^count^delta^(present==0?-7L:payload);if(!Equals(host.Invoke(null,[input,present,operand,delta]),expected)||!Equals(oracle.Invoke(null,[input,present,operand,delta]),expected))throw new Exception("Counted map independent modular scalar/presence/zero-trip/high-bit/overflow oracle");}
            foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumArguments=3},RestrictedCilImportBudgetsV1.Production with{MaximumLocals=0},RestrictedCilImportBudgetsV1.Production with{MaximumBasicBlocks=1},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}})if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Counted map original budgets");
            if(kind==0){foreach(string badLoop in new[]{"for(int i=0;i<=count;i++)","for(int i=0;i<count;i+=2)","for(int i=0;i<count;i++,count--)","while(delta>0)"}){var denied=Compile(source.Replace(loop,badLoop));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted map inclusive/overflow/mutable/noninduction gate");}foreach(string badUpdate in new[]{"unchecked(payload+1/delta)","Effect(payload,delta)"}){string bad=source.Replace(update,badUpdate);if(badUpdate.StartsWith("Effect",StringComparison.Ordinal))bad=bad.Replace(consumer,"private static int Effect(int input,int delta){System.GC.Collect();return input+delta;}"+consumer);var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted map fault/effect gate");}}
            if(kind==4)foreach(string badHelper in new[]{"private static int Adjust(int input,int delta)=>1/delta;","private static int Adjust(int input,int delta)=>Adjust(input,delta);","private static int Adjust(int input,int delta){System.GC.Collect();return input;}","[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]private static int Adjust(int input,int delta)=>input;"}){var denied=Compile(source.Replace(helper,badHelper));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted map selected helper purity/recursion/implementation gate");}
            if(kind is 2 or 3){string badLoop=kind==2?"for(uint i=uint.MaxValue-2;i<=uint.MaxValue;i++)":"for(ulong i=ulong.MaxValue-2;i<=ulong.MaxValue;i++)";var denied=Compile(source.Replace(loop,badLoop));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted map unsigned wrap gate");}
            if(kind==5)foreach(string bad in new[]{source.Replace("if(!value.HasValue)return null;",""),source.Replace("int payload=value.Value;","value=null;int payload=value.Value;")}){var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted checked payload still requires immutable dominating presence");}
            if(kind==6){var denied=Compile(source.Replace(declaration,declaration.Replace("private static","public static")));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted primitive-input producer does not open public nullable return ABI");}
            if(kind==7){
                foreach(string changed in new[]{"case 0:i=unchecked(i+1);payload=unchecked(payload+delta);break;","case 0:count--;payload=unchecked(payload+delta);break;","case 0:System.GC.Collect();payload=unchecked(payload+delta);break;"}){var denied=Compile(source.Replace("case 0:payload=unchecked(payload+delta);break;",changed));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted switch counter/bound/effect gate");}
                using var pe=new PEReader(new MemoryStream(actual.Source.Pe!));var md=pe.GetMetadataReader();var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Shift"));var body=pe.GetMethodBody(method.RelativeVirtualAddress);byte[] il=body.GetILBytes()!;int table=Array.IndexOf(il,(byte)0x45);if(table<0)throw new Exception("Counted switch must emit real CIL table");int targets=BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(table+1,4));var section=pe.PEHeaders.SectionHeaders.Single(row=>method.RelativeVirtualAddress>=row.VirtualAddress&&method.RelativeVirtualAddress<row.VirtualAddress+row.SizeOfRawData);int offset=section.PointerToRawData+method.RelativeVirtualAddress-section.VirtualAddress;offset+=(actual.Source.Pe![offset]&3)==2?1:(actual.Source.Pe![offset+1]>>4)*4;
                foreach(int target in new[]{0,table}){byte[] forged=actual.Source.Pe!.ToArray();BinaryPrimitives.WriteInt32LittleEndian(forged.AsSpan(offset+table+5,4),target-(table+5+4*targets));var denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,forged,"counted-switch-backedge",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));if(denied.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Counted switch backward/self target cannot bypass induction: "+string.Join(';',denied.Diagnostics));}
            }
            if(kind==1)using(var pe=new PEReader(new MemoryStream(actual.Source.Pe!))){var md=pe.GetMetadataReader();var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Shift"));var body=pe.GetMethodBody(method.RelativeVirtualAddress);byte[] il=body.GetILBytes()!;int bound=Enumerable.Range(0,il.Length-1).Single(i=>il[i]==0x03&&il[i+1] is 0x32 or 0x3f);byte[] forged=actual.Source.Pe!.ToArray();var section=pe.PEHeaders.SectionHeaders.Single(row=>method.RelativeVirtualAddress>=row.VirtualAddress&&method.RelativeVirtualAddress<row.VirtualAddress+row.SizeOfRawData);int offset=section.PointerToRawData+method.RelativeVirtualAddress-section.VirtualAddress;offset+=(forged[offset]&3)==2?1:(forged[offset+1]>>4)*4;forged[offset+bound]=0x02;var denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,forged,"nominal-carrier-not-loop-bound",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));if(denied.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nominal packed I8 cannot be an induction bound: "+string.Join(';',denied.Diagnostics));}
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-map-loop-"+kind+"-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS counted nullable map kind="+kind+" NoHeap/source/image/host/scalar/modular result/zero-trip/null/present/extrema/overflow/high-bit/repeat/maps/original budgets/purity/induction/nominal gates");
        }
    }
    private static void RunMapSwitch()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(bool statement in new[]{false,true}) {
            const string declaration="private static int? Shift(int? value,int scale,int selector)";
            const string fallback="selector switch{0=>scale,1=>unchecked(scale+1),2=>unchecked(scale-1),3=>0,_=>selector}";
            string producer=statement?"{switch(selector){case 0:return null;case 1:return value;case 2:return unchecked(value.GetValueOrDefault()+scale);case 3:return unchecked(value.GetValueOrDefault()-scale);default:return unchecked(value.GetValueOrDefault()*scale);}}":"=>unchecked(value.GetValueOrDefault("+fallback+")*scale);";
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture{"+declaration+producer+"[HybridCpuKernelNoHeap]public static long Read(int input,int present,int scale,int selector){int? value=present==0?null:input;int? result=Shift(value,scale,selector);return new WideValue(((long)input<<32)^scale^selector^result.GetValueOrDefault(-7)).Value;}}";
            ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null)=>ManagedSourceCompanionV1.CompileBodyWorld(new("NullableMapSwitch",[new("/src/Switch.cs",text)],references),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Map switch "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Map switch repeat/maps/image");
            string inlineExpression=statement?"selector switch{0=>null,1=>value,2=>unchecked(value.GetValueOrDefault()+scale),3=>unchecked(value.GetValueOrDefault()-scale),_=>unchecked(value.GetValueOrDefault()*scale)}":"unchecked(value.GetValueOrDefault("+fallback+")*scale)";
            var inline=Compile(source.Replace("result=Shift(value,scale,selector)","result="+inlineExpression));Link(inline);var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1})foreach(int scale in new[]{int.MinValue,-3,0,1,int.MaxValue})foreach(int selector in new[]{int.MinValue,-1,0,1,2,3,4,int.MaxValue}){int value=present==0?0:input;int? payload=statement?selector switch{0=>null,1=>present==0?null:input,2=>unchecked(value+scale),3=>unchecked(value-scale),_=>unchecked(value*scale)}:unchecked((present==0?selector switch{0=>scale,1=>unchecked(scale+1),2=>unchecked(scale-1),3=>0,_=>selector}:input)*scale);long expected=((long)input<<32)^scale^selector^(payload??-7);if(!Equals(host.Invoke(null,[input,present,scale,selector]),expected)||!Equals(oracle.Invoke(null,[input,present,scale,selector]),expected))throw new Exception("Map switch independent selector/default/presence/overflow oracle");}
            foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumBasicBlocks=3},RestrictedCilImportBudgetsV1.Production with{MaximumDecodedInstructions=8},RestrictedCilImportBudgetsV1.Production with{MaximumLocals=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}})if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Map switch original budgets");
            foreach(string bad in new[]{source.Replace(declaration,"public static int? Shift(int? value,int scale,int selector)"),source.Replace(declaration+producer,declaration+"{switch(selector){case 0:return value;case 1:return null;case 2:System.GC.Collect();return value;case 3:return value;default:return value;}}"),source.Replace(declaration+producer,declaration+"{switch(selector){case 0:return value;case 1:return null;case 2:while(scale>0)scale--;return value;case 3:return value;default:return value;}}")}){var denied=Compile(bad);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Map switch public/effect/backedge gate");}
            using(var pe=new PEReader(new MemoryStream(actual.Source.Pe!))){var md=pe.GetMetadataReader();var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Shift"));var body=pe.GetMethodBody(method.RelativeVirtualAddress);byte[] il=body.GetILBytes()!;int switchOffset=Array.IndexOf(il,(byte)0x45);if(switchOffset<0||body.ExceptionRegions.Length!=0)throw new Exception("Map switch emitted CIL oracle");int count=BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(switchOffset+1,4));byte[] forged=actual.Source.Pe!.ToArray();var section=pe.PEHeaders.SectionHeaders.Single(row=>method.RelativeVirtualAddress>=row.VirtualAddress&&method.RelativeVirtualAddress<row.VirtualAddress+row.SizeOfRawData);int offset=section.PointerToRawData+method.RelativeVirtualAddress-section.VirtualAddress;offset+=(forged[offset]&3)==2?1:(forged[offset+1]>>4)*4;BinaryPrimitives.WriteInt32LittleEndian(forged.AsSpan(offset+switchOffset+5,4),-(switchOffset+5+4*count));var denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,forged,"backward-switch-map",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));if(denied.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Backward switch table edge cannot bypass producer purity: "+string.Join(';',denied.Diagnostics));}
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-map-switch-"+(statement?"statement":"expression")+"-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS private nullable map switch "+(statement?"statement":"expression")+" CIL/NoHeap/image/inline/host/dense/default/presence/extrema/overflow/repeat/maps and original budgets/public/effect/loop/backward table gates");
        }
    }
    private static void RunMapFallback()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(int count in new[]{3,8})foreach(bool mutate in new[]{false,true})foreach(bool computed in new[]{false,true}) {
            string parameters=string.Join(',',Enumerable.Range(1,count-1).Select(i=>"int p"+i)),arguments=string.Join(',',Enumerable.Range(1,count-1).Select(i=>"p"+i)),fallback="p"+(count-1);
            string defaultInput=computed?"unchecked("+fallback+"+1)":fallback;
            string expression="unchecked(value.GetValueOrDefault("+defaultInput+")*p1)",mutation=mutate?fallback+"=unchecked("+fallback+"+1);":"";
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture{private static int? Shift(int? value,"+parameters+"){"+mutation+"return "+expression+";}[HybridCpuKernelNoHeap]public static long Read(int input,"+parameters+"){int? value=input==0?null:input;int? result=Shift(value,"+arguments+");return new WideValue(((long)input<<32)^"+fallback+"^result.GetValueOrDefault(-7)).Value;}}";
            ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null)=>ManagedSourceCompanionV1.CompileBodyWorld(new("NullableMapFallback",[new("/src/Fallback.cs",text)],references),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Map fallback "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Map fallback repeat/maps/image");
            string inlineSource=source.Replace("int? result=Shift(value,"+arguments+");","int original="+fallback+";"+mutation+"int? result="+expression+";"+fallback+"=original;");var inline=Compile(inlineSource);Link(inline);var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int scale in new[]{int.MinValue,-3,0,1,int.MaxValue})foreach(int raw in new[]{int.MinValue,-1,0,1,int.MaxValue}){object[] values=Enumerable.Repeat<object>(7,count).ToArray();values[0]=input;values[1]=scale;values[^1]=raw;int fallbackValue=unchecked(raw+(mutate?1:0)+(computed?1:0));long expected=((long)input<<32)^raw^unchecked((input==0?fallbackValue:input)*scale);if(!Equals(host.Invoke(null,values),expected)||!Equals(oracle.Invoke(null,values),expected))throw new Exception("Map fallback current SSA/null/present/extrema/overflow/preservation oracle");}
            if(computed&&count==3&&!mutate) {
                const string helper="private static int Adjust(int input)=>unchecked(input+1);";
                string helperSource=source.Replace("GetValueOrDefault("+defaultInput+")","GetValueOrDefault(Adjust("+fallback+"))").Replace("[HybridCpuKernelNoHeap]",helper+"[HybridCpuKernelNoHeap]");
                Link(Compile(helperSource));
                foreach(string badHelper in new[]{"private static int Adjust(int input)=>1/input;","private static int Adjust(int input)=>Adjust(input);","private static int Adjust(int input){System.GC.Collect();return input;}","[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]private static int Adjust(int input)=>input;"}){var denied=Compile(helperSource.Replace(helper,badHelper));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Computed map fallback helper discovery must not admit fault/effect/recursive/synchronized body");}
            }
            foreach(string deniedSource in new[]{source.Replace("private static int? Shift","public static int? Shift"),source.Replace(expression,"unchecked(value.GetValueOrDefault(1/"+fallback+")*p1)"),source.Replace("int? value,"+parameters,"ref int? value,"+parameters).Replace("Shift(value,"+arguments+")","Shift(ref value,"+arguments+")")}){var denied=Compile(deniedSource);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Map fallback public/fault/byref gate");}
            foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumArguments=count-1},RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}})if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Map fallback original budgets");
            var constant=Compile(source.Replace("GetValueOrDefault("+defaultInput+")","GetValueOrDefault(-7)"));Link(constant);
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-map-fallback-"+count+(mutate?"-assigned":"-input")+(computed?"-computed":"")+"-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS nullable map fallback argument="+(count-1)+" mutation="+mutate+" computed="+computed+" NoHeap/source/image/host/inline/current SSA/extrema/overflow/preservation/constant/repeat/maps/budgets/public/fault/byref");
        }
    }
    private static void RunMapScaled()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(int count in new[]{3,8})foreach(bool wide in new[]{false,true}) {
            string parameters=string.Join(',',Enumerable.Range(1,count-1).Select(i=>(wide&&i%2==0?"long":"int")+" p"+i));
            string arguments=string.Join(',',Enumerable.Range(1,count-1).Select(i=>"p"+i));
            string arithmetic="value.GetValueOrDefault()*p1+(int)p2"+string.Concat(Enumerable.Range(3,count-3).Select(i=>"^(int)p"+i));
            string expression="value.HasValue?unchecked("+arithmetic+"):null";
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue{public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture{private static int? Shift(int? value,"+parameters+")=>"+expression+";[HybridCpuKernelNoHeap]public static long Read(int input,"+parameters+"){int? value=input==0?null:input;int? result=Shift(value,"+arguments+");return new WideValue(((long)input<<32)^p2^result.GetValueOrDefault(-7)).Value;}}";
            ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null)=>ManagedSourceCompanionV1.CompileBodyWorld(new("NullableMapScaled",[new("/src/Scaled.cs",text)],references),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Scaled map "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
            var actual=Compile(source);var repeated=Compile(source);var image=Link(actual);var repeat=Link(repeated);var inline=Compile(source.Replace("result=Shift(value,"+arguments+")","result="+expression));Link(inline);
            if(!actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Scaled map repeat/maps/image");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long raw in new[]{long.MinValue,-1L,0L,1L,long.MaxValue,(1L<<40)+7}) {
                object[] values=new object[count];values[0]=input;int payload=input;
                for(int i=1;i<count;i++){long operand=unchecked(raw+i-2);values[i]=wide&&i%2==0?(object)operand:unchecked((int)operand);if(i==1)payload=unchecked(payload*(int)operand);else if(i==2)payload=unchecked(payload+(int)operand);else payload^=unchecked((int)operand);}
                long delta=wide?(long)values[2]:(int)values[2];long expected=((long)input<<32)^delta^(input==0?-7L:payload);
                if(!Equals(host.Invoke(null,values),expected)||!Equals(oracle.Invoke(null,values),expected))throw new Exception("Scaled signed operands/order/high bits/overflow host oracle");
            }
            using(var pe=new PEReader(new MemoryStream(actual.Source.Pe!))){var md=pe.GetMetadataReader();var method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Shift"));byte[] il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;if(!il.Contains((byte)0x5a)||!il.Contains((byte)0x58)||pe.GetMethodBody(method.RelativeVirtualAddress).ExceptionRegions.Length!=0)throw new Exception("Scaled emitted CIL MUL/ADD/EH oracle");}
            foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumArguments=count-1},RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}})if(Compile(source,budget).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Scaled original argument/call/stack budget");
            foreach(string deniedSource in new[]{source.Replace("private static int? Shift","public static int? Shift"),source.Replace("unchecked("+arithmetic+")","1/p1"),source.Replace("int? value,"+parameters,"int? value,ref "+parameters).Replace("Shift(value,"+arguments+")","Shift(value,ref "+arguments+")")}){var denied=Compile(deniedSource);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Scaled public/fault/byref gate");}
            if(count==8){var denied=Compile(source.Replace(parameters+")",parameters+",int ninth)").Replace("Shift(value,"+arguments+")","Shift(value,"+arguments+",ninth)"));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Scaled ninth register argument remains closed");}
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-map-scaled-"+count+(wide?"-mixed":"-i4")+"-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            Console.WriteLine("PASS scaled nullable map "+count+" signed "+(wide?"mixed I4/I8":"I4")+" arguments NoHeap/CIL/image/inline/host/extrema/overflow/repeat/maps/budgets/authority/fault/byref");
        }
    }
    public static void RunReturnHelper(bool composition=false)
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string helper="private static int Adjust(int input)=>unchecked(input+1);";
        string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue {public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture {"+helper+"private static int? Produce(int input)=>input==0?null:Adjust(input);[HybridCpuKernelNoHeap]public static long Read(int input){int? value=Produce(input);return new WideValue(value.GetValueOrDefault(-7)).Value;}}";
        const string relay="private static int? Relay(int input)=>Produce(input);";
        if(composition)source=source.Replace("[HybridCpuKernelNoHeap]",relay+"[HybridCpuKernelNoHeap]").Replace("value=Produce(input)","value=Relay(input)");
        CompanionSourceRequestV1 Request(string text)=>new("NullableReturnHelper",[new("/src/Helper.cs",text)],references);
        ManagedSourceBodyWorldResultV1 Compile(string text,RestrictedCilImportBudgetsV1? budgets=null)=>ManagedSourceCompanionV1.CompileBodyWorld(Request(text),new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result){if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Nullable helper: "+string.Join(';',result.Source.Diagnostics)+";"+string.Join(';',result.Compilation?.Diagnostics??[]));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
        string[] cases=composition ? [source,
            source.Replace(relay,"private static int? Relay(int input){int? value=Produce(input);int? copy=value;return copy;}"),
            source.Replace(relay,"private static int? Relay(int input)=>input==0?null:Produce(input);"),
            source.Replace(relay,"private static int? Relay(int input)=>RelayAgain(input);private static int? RelayAgain(int input)=>Produce(input);")] :
            [source,source.Replace(helper,"private static int Leaf(int input)=>unchecked(input+1);private static int Adjust(int input)=>Leaf(input);")];
        foreach(string text in cases) {
            var actual=Compile(text);var repeat=Compile(text);var image=Link(actual);var again=Link(repeat);var inline=Compile(composition?text.Replace("value=Relay(input)","value=input==0?null:unchecked(input+1)"):text.Replace("null:Adjust(input)","null:unchecked(input+1)"));Link(inline);
            if(!actual.Source.Pe!.SequenceEqual(repeat.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!)||!image.RestrictedImage!.PackageBytes.SequenceEqual(again.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeat.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Nullable helper repeat/maps/image");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue}){long expected=input==0?-7:unchecked(input+1);if(!Equals(host.Invoke(null,[input]),expected)||!Equals(oracle.Invoke(null,[input]),expected))throw new Exception("Nullable helper host/inline overflow oracle");}
            if(text!=source)continue;
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,composition?"nullable-return-composition-artifacts":"nullable-return-helper-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),text);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
        }
        foreach(string replacement in new[]{"private static int Adjust(int input)=>1/input;","private static int Adjust(int input)=>checked(input+1);","private static int Adjust(int input)=>Adjust(input);","private static int Adjust(int input){System.GC.Collect();return input;}","private static int Adjust(int input){try{return input;}finally{System.GC.Collect();}}","private static int state;private static int Adjust(int input){state=input;return input;}"}){var denied=Compile(source.Replace(helper,replacement));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable helper purity/fault/EH/recursive/effect gate");}
        if(composition)foreach(string replacement in new[]{"private static int? Relay(int input)=>Relay(input);","public static int? Relay(int input)=>Produce(input);","private static int? Relay(int input){try{return Produce(input);}finally{System.GC.Collect();}}"}){var denied=Compile(source.Replace(relay,replacement));if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable composition recursive/public/EH gate");}
        foreach(string method in new[]{"Produce","Relay","Read"}) {
            if(!composition && method=="Relay")continue;
            string signature=method=="Read"?"public static long Read":"private static int? "+method;
            string deniedText=source.Replace(signature,"[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)] "+signature);
            var denied=Compile(deniedText);if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable producer implicit synchronization effect gate");
        }
        foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}}){var denied=Compile(source,budget);if(denied.Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Nullable helper original budgets");}
        Console.WriteLine(composition?"PASS private nullable relay/copy/conditional/transitive composition exact dependency proof -> NoHeap/source/image/repeat/maps/inline/extrema/overflow and inherited fault/effect/EH/recursion/budget gates":"PASS private nullable producer exact scalar/transitive helper proof -> NoHeap/source/image/repeat/maps/inline/extrema/overflow and fault/effect/EH/recursion/budget gates");
    }
    public static void RunMixedReturn(bool composition=false)
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(int mode in new[]{0,1,2,3}) {
            string signature=mode==0?"int input,long delta":mode==1?"long delta,int input":mode==2?"long input,long delta":"long input";
            string expression=mode==3?"input<0?null:unchecked((int)input)":"input<0?null:unchecked((int)input-(int)delta)";
            string arguments=mode==1?"delta,input":mode==3?"input":"input,delta";
            string source="using HybridCPU.Compiler.Profiles;public readonly struct WideValue {public long Value{get;}public WideValue(long value){Value=value;}}public static class Fixture {private static int? Produce("+signature+")=>"+expression+";[HybridCpuKernelNoHeap]public static long Read("+(mode==1?"int input,long delta":signature)+"){int? value=Produce("+arguments+");return new WideValue(value.GetValueOrDefault(-7)).Value;}}";
            if(composition)source=source.Replace("[HybridCpuKernelNoHeap]","private static int? Relay("+signature+")=>Produce("+arguments+");[HybridCpuKernelNoHeap]").Replace("value=Produce(","value=Relay(");
            CompanionSourceRequestV1 request=new("MixedNullableReturn",[new("/src/Mixed.cs",source)],references);
            ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 r,RestrictedCilImporterV1? importer=null)=>ManagedSourceCompanionV1.CompileBodyWorld(r,importer??new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0]);
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 r){if(r.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Mixed nullable return "+string.Join(';',r.Source.Diagnostics)+";"+string.Join(';',r.Compilation?.Diagnostics??[]));var image=new ScalarControlFlowV2ObjectLinkerV1().Link(r.Compilation,r.Compilation.Graph!.RootIdentities.Single());if(image.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',image.Diagnostics));return image;}
            var actual=Compile(request);var repeat=Compile(request);var linked=Link(actual);var again=Link(repeat);var inline=Compile(request with{Documents=[new("/src/Mixed.cs",source.Replace((composition?"Relay(":"Produce(")+arguments+");return new",expression+";return new"))]});Link(inline);
            if(!actual.Source.Pe!.SequenceEqual(repeat.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!)||!linked.RestrictedImage!.PackageBytes.SequenceEqual(again.RestrictedImage!.PackageBytes)||actual.SourceMaps.Any(row=>repeat.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Mixed return determinism/maps/image");
            var host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;var oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(long input in new[]{long.MinValue,-1L,0L,1L,long.MaxValue,1L<<40})foreach(long delta in new[]{long.MinValue,-1L,0L,long.MaxValue,1L<<40}) {
                object first=mode<2?(object)unchecked((int)input):input;long signed=mode<2?unchecked((int)input):input;
                long expected=signed<0?-7:mode==3?unchecked((int)input):unchecked((int)input-(int)delta);
                object[] args=mode==3?[first]:[first,delta];
                if(!Equals(host.Invoke(null,args),expected)||!Equals(oracle.Invoke(null,args),expected))throw new Exception("Mixed return high-bit/truncation/order oracle");
            }
            if(mode!=0)continue;
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,composition?"nullable-mixed-composition-artifacts":"nullable-mixed-return-artifacts");Directory.CreateDirectory(artifacts);File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),linked.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            foreach(string bad in new[]{source.Replace("private static int?","public static int?"),source.Replace("=>"+expression+";","{return Produce(input,delta);}"),source.Replace("=>"+expression+";","{return unchecked((int)(1L/delta));}"),source.Replace("=>"+expression+";","{try{return input;}finally{System.GC.Collect();}}")}){var denied=Compile(request with{Documents=[new("/src/Mixed.cs",bad)]});if(denied.Source.Pe is null||denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Mixed return purity/ABI negative");}
            foreach(var budget in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumArguments=1},RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1}}){var denied=Compile(request,new(budgets:budget,mode:RestrictedCilImportModeV1.ScalarControlFlowV2));if(denied.Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Mixed return original budgets");}
        }
        Console.WriteLine(composition?"PASS nullable relay signed I4/I8 permutations/one-or-two-I8 explicit truncation/high bits/order -> NoHeap/image/inline host/repeat/maps and purity/ABI/budgets":"PASS private nullable return signed I4/I8 permutations/one-or-two-I8 explicit truncation/high bits/order -> NoHeap/image/inline host/repeat/maps and purity/ABI/budgets");
    }
    public static void RunBinaryReturn()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),
            File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string header="private static int? ReadOptional(int input,int delta)";
        const string wrapper="public readonly struct WideValue {public long Value {get;} public WideValue(long value){Value=value;}}";
        CompanionSourceRequestV1 Request(string source)=>new("OptionalBinaryReturn",[new("/src/OptionalBinary.cs",source)],references);
        ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 request,RestrictedCilImporterV1? importer=null,CancellationToken token=default)=>
            ManagedSourceCompanionV1.CompileBodyWorld(request,importer??new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0],token);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result) {
            if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Binary nullable return: "+string.Join(';',result.Source.Diagnostics)+"; "+string.Join(';',result.Compilation?.Diagnostics??[]));
            var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());
            if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;
        }
        foreach((string expression,string prefix,Func<int,int,int?> expected) in new (string,string,Func<int,int,int?>)[]{
            ("input<0?null:unchecked(input+delta)","",(a,b)=>a<0?null:unchecked(a+b)),
            ("input==0?null:unchecked(input-delta)","",(a,b)=>a==0?null:unchecked(a-b)),
            ("delta==0?null:unchecked(delta-input)","",(a,b)=>b==0?null:unchecked(b-a)),
            ("input==0?null:delta","delta=unchecked(delta-input);",(a,b)=>a==0?null:unchecked(b-a)),
            ("input<0?null:input","input=unchecked(input+delta);",(a,b)=>unchecked(a+b)<0?null:unchecked(a+b))})foreach(bool reverse in new[]{false,true}) {
            string body="{"+prefix+"return "+expression+";}";
            string consumer="int? value=ReadOptional("+(reverse?"delta,input":"input,delta")+");int? copy=value;return new WideValue(copy.GetValueOrDefault(-7)).Value;";
            string text="using HybridCPU.Compiler.Profiles; "+wrapper+" public static class Fixture {"+header+body+"[HybridCpuKernelNoHeap] public static long Read(int input,int delta){"+consumer+"}}";
            var request=Request(text);var actual=Compile(request);var repeat=Compile(request);var image=Link(actual);var repeated=Link(repeat);
            string inline="int left="+(reverse?"delta":"input")+",right="+(reverse?"input":"delta")+";"+prefix.Replace("input","left").Replace("delta","right")+
                "int? value="+expression.Replace("input","left").Replace("delta","right")+";int? copy=value;return new WideValue(copy.GetValueOrDefault(-7)).Value;";
            var oracle=Compile(Request(text.Replace(consumer,inline)));Link(oracle);
            if(!actual.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) || !image.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) ||
                actual.SourceMaps.Any(row=>repeat.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Binary nullable return repeat/maps/image");
            MethodInfo host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;MethodInfo inlineHost=Assembly.Load(oracle.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-7,0,1,int.MaxValue}) {
                long value=expected(reverse?delta:input,reverse?input:delta)??-7;
                if(!Equals(host.Invoke(null,[input,delta]),value)||!Equals(inlineHost.Invoke(null,[input,delta]),value))throw new Exception("Binary nullable return independent argument order/extrema");
            }
            if(reverse || prefix.Length!=0 || !expression.Contains("input+delta",StringComparison.Ordinal))continue;
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-binary-return-artifacts");Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),text);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);
            File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage.PackageBytes);File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            foreach(string bad in new[]{text.Replace(header,"public static int? ReadOptional(int input,int delta)"),
                text.Replace(header,"private static int? ReadOptional(int input,ulong delta)").Replace(body,"{return input<0?null:unchecked(input+(int)delta);}").Replace("value=ReadOptional(input,delta)","value=ReadOptional(input,unchecked((ulong)delta))"),
                text.Replace(header,"private static int? ReadOptional<T>(int input,int delta)").Replace("value=ReadOptional(input,delta)","value=ReadOptional<uint>(input,delta)"),
                text.Replace(body,"{return 1/delta;}"),text.Replace(body,"{return ReadOptional(delta,input);}"),
                text.Replace(body,"{try{return input;}finally{System.GC.Collect();}}"),
                text.Replace(consumer,"int original=input;try{ReadOptional(input,delta);}finally{original=unchecked(original+1);}return original;")}) {
                var denied=Compile(Request(bad));if(denied.Source.Pe is null || denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Binary nullable return ABI/effect/EH/recursion gate: "+string.Join(';',denied.Source.Diagnostics));
            }
            foreach(string context in new[]{"int","long"}){var generic=Compile(Request(text.Replace(header,"private static int? ReadOptional<T>(int input,int delta)").Replace("value=ReadOptional(input,delta)","value=ReadOptional<"+context+">(input,delta)")));Link(generic);var genericHost=Assembly.Load(generic.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue})if(!Equals(genericHost.Invoke(null,[input,delta]),host.Invoke(null,[input,delta])))throw new Exception("Exact binary generic nullable producer scalar/overflow equivalence");}
            foreach(var budgets in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumArguments=1},RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1},RestrictedCilImportBudgetsV1.Production with{MaximumLocals=0}}) {
                var denied=Compile(request,new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2));if(denied.Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Binary nullable return original budget");
            }
            using(StringWriter output=new())using(StringWriter error=new()) {
                int exit=ManagedSourceCommandLineV1.Run(["compile-source-image","--source",Path.Combine(artifacts,"Primary.cs"),"--metadata-reference",typeof(object).Assembly.Location,
                    "--reference",Path.Combine(directory,"System.Runtime.dll"),"--reference",Path.Combine(directory,"netstandard.dll"),"--reference",typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location,
                    "--assembly-name","OptionalBinaryReturn","--type","Fixture","--method","Read","--out",Path.Combine(artifacts,"Cli.hcexe")],output,error);
                if(exit!=0 || !image.RestrictedImage.PackageBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(artifacts,"Cli.hcexe"))))throw new Exception("Binary nullable return CLI/API bytes: "+error);
            }
            RestrictedCilImporterV1 reusable=new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2);using CancellationTokenSource cancelled=new();cancelled.Cancel();
            try{Compile(request,reusable,cancelled.Token);throw new Exception("Binary nullable return pre-cancel");}catch(OperationCanceledException){}
            if(!Link(Compile(request,reusable)).RestrictedImage!.PackageBytes.SequenceEqual(image.RestrictedImage.PackageBytes))throw new Exception("Binary nullable return reuse");
        }
        Console.WriteLine("PASS private nullable return two-I4 argument order/computation/assignment/extrema/overflow -> NoHeap/image/repeat/maps/CLI and original ABI/EH/purity/budget/cancel controls");
    }
    public static void RunReturn()
    {
        string directory=Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references=[File.ReadAllBytes(typeof(object).Assembly.Location),File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")),
            File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string wrapper="public readonly struct WideValue { public long Value {get;} public WideValue(long value){Value=value;} }";
        const string producer="private static int? ReadOptional(int input)";
        string Source(string body,string consumer="int? value=ReadOptional(input); int? copy=value; return new WideValue(copy.GetValueOrDefault()).Value;")=>
            "using HybridCPU.Compiler.Profiles; "+wrapper+" public static class Fixture { "+producer+body+" [HybridCpuKernelNoHeap] public static long Read(int input){"+consumer+"} }";
        CompanionSourceRequestV1 Request(string text)=>new("OptionalReturn",[new("/src/Optional.cs",text)],references);
        ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 request,RestrictedCilImporterV1? importer=null,CancellationToken token=default)=>
            ManagedSourceCompanionV1.CompileBodyWorld(request,importer??new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],[],[0],token);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result) {
            if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Nullable return: "+string.Join(';',result.Source.Diagnostics)+"; "+string.Join(';',result.Compilation?.Diagnostics??[]));
            ScalarControlFlowV2LinkedProgramV1 linked=new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());
            if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception("Nullable return link: "+string.Join(';',linked.Diagnostics));
            return linked;
        }
        foreach(string body in new[]{"=>input<0?null:input;","=>input==0?null:unchecked(input+1);","=>input;","=>null;",
            "{int? value=input;int? copy=value;if(input==0)copy=null;return copy;}","{if(input<0)return null;return unchecked(input+1);}",
            "{input=unchecked(input+1);return input==0?null:input;}",
            "{int? value=input==0?null:input;return value.HasValue?unchecked(value.GetValueOrDefault()+1):null;}"}) {
            string text=Source(body);
            CompanionSourceRequestV1 request=Request(text);
            ManagedSourceBodyWorldResultV1 actual=Compile(request),repeat=Compile(request);
            ScalarControlFlowV2LinkedProgramV1 linked=Link(actual),again=Link(repeat);
            ManagedSourceBodyWorldResultV1 inline=Compile(Request(text.Replace("int? value=ReadOptional(input);", "int? value="+(body.StartsWith("{input=")?"unchecked(input+1)==0?null:unchecked(input+1)":body=="=>input;"?"input":body=="=>null;"?"null":body.Contains("input==0")?"input==0?null:"+(body.Contains("unchecked")?"unchecked(input+1)":"input"):"input<0?null:"+(body.Contains("unchecked")?"unchecked(input+1)":"input"))+";")));
            Link(inline);
            if(!actual.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(again.RestrictedImage!.PackageBytes) || actual.SourceMaps.Count!=repeat.SourceMaps.Count ||
                actual.SourceMaps.Any(row=>repeat.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Nullable return repeat/maps/image");
            MethodInfo host=Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            MethodInfo oracle=Assembly.Load(inline.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})if(!Equals(host.Invoke(null,[input]),oracle.Invoke(null,[input])))throw new Exception("Nullable return inline/CoreCLR boundary");
            if(body!="=>input<0?null:input;")continue;
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"nullable-return-artifacts");Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),text);File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe);
            File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),linked.RestrictedImage.PackageBytes);
            File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
            using(PEReader pe=new(new MemoryStream(actual.Source.Pe))) {
                MetadataReader md=pe.GetMetadataReader();MethodDefinition method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="ReadOptional"));
                MethodBodyBlock cil=pe.GetMethodBody(method.RelativeVirtualAddress);
                Console.WriteLine("NULLABLE-RETURN-CIL signature="+Convert.ToHexString(md.GetBlobBytes(method.Signature))+" body="+Convert.ToHexString(cil.GetILBytes()!));
                byte[] changed=actual.Source.Pe.ToArray();var section=pe.PEHeaders.SectionHeaders.Single(s=>method.RelativeVirtualAddress>=s.VirtualAddress && method.RelativeVirtualAddress<s.VirtualAddress+s.SizeOfRawData);
                int start=section.PointerToRawData+method.RelativeVirtualAddress-section.VirtualAddress;
                start+=(changed[start]&3)==2?1:(changed[start+1]>>4)*4;
                changed.AsSpan(start,cil.GetILBytes()!.Length).Clear();changed[start]=0x21;changed[start+cil.GetILBytes()!.Length-1]=0x2a;
                ManagedCallGraphCompilationV1 denied=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule,changed,"forged-nullable-return",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));
                if(denied.Status==RestrictedCilImportStatusV1.Success || !denied.Diagnostics.Any(d=>d.Code=="HCCIL0019"))throw new Exception("Primitive carrier cannot forge nullable return: "+string.Join(';',denied.Diagnostics));
            }
            using(StringWriter output=new())using(StringWriter error=new()) {
                int exit=ManagedSourceCommandLineV1.Run(["compile-source-image","--source",Path.Combine(artifacts,"Primary.cs"),"--metadata-reference",typeof(object).Assembly.Location,
                    "--reference",Path.Combine(directory,"System.Runtime.dll"),"--reference",Path.Combine(directory,"netstandard.dll"),"--reference",typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location,
                    "--assembly-name","OptionalReturn","--type","Fixture","--method","Read","--out",Path.Combine(artifacts,"Cli.hcexe")],output,error);
                if(exit!=0 || !linked.RestrictedImage.PackageBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(artifacts,"Cli.hcexe"))))throw new Exception("Nullable return CLI/API equality: "+error);
            }
            foreach(string bad in new[]{text.Replace(producer,"public static int? ReadOptional(int input)"),text.Replace(body,"{while(true){} }"),
                text.Replace(body,"{try{return input;}finally{System.GC.Collect();}}"),text.Replace(body,"=>ReadOptional(input);"),
                text.Replace(body,"=>(new int[1])[0];"),text.Replace(body,"=>1/input;"),text.Replace(body,"=>System.Math.Abs(input);")}) {
                ManagedSourceBodyWorldResultV1 denied=Compile(Request(bad));
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nullable return public/effects/EH/recursive/fault/loop gate: "+string.Join(';',denied.Source.Diagnostics));
                if(bad.Contains("finally{System.GC.Collect",StringComparison.Ordinal)) {
                    var ehPlan=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportManagedEhPlan(denied.Source.Pe,new("Fixture","ReadOptional"));
                    if(ehPlan.Status!=ManagedEhImportStatusV1.Unsupported || ehPlan.Code!="HCCIL1805")throw new Exception("Nullable return standalone EH signature gate");
                }
            }
            ManagedSourceBodyWorldResultV1 root=ManagedSourceCompanionV1.CompileBodyWorld(request,new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","ReadOptional")],[],[0]);
            if(root.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nullable return root ABI gate");
            var ehCaller=Compile(Request(Source(body,"int original=input;try{ReadOptional(input);}finally{original=unchecked(original+1);}return original;")));
            if(ehCaller.Source.Pe is null || ehCaller.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported || !ehCaller.Compilation.Diagnostics.Any(d=>d.Code=="HCCIL1890"))throw new Exception("Nullable return EH caller lifetime gate");
            foreach(string bad in new[]{text.Replace(producer,"private static int? ReadOptional<T>(int input)").Replace("value=ReadOptional(input)","value=ReadOptional<uint>(input)"),
                text.Replace("private static int?","private static long?").Replace("int? value=ReadOptional(input); int? copy=value;","long? value=ReadOptional(input); long? copy=value;"),
                text.Replace(producer,"private static int? ReadOptional(ref int input)").Replace("value=ReadOptional(input)","value=ReadOptional(ref input)")}) {
                var denied=Compile(Request(bad));
                if(denied.Source.Pe is null || denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Nullable return generic/width/byref gate: "+string.Join(';',denied.Source.Diagnostics));
            }
            ManagedCompiledMethodV1 callee=actual.Compilation!.Methods.Single(m=>m.Identity.MethodName=="ReadOptional");
            int token=actual.Compilation.Graph!.Edges.Single(e=>e.CalleeIdentity==callee.Identity.StableIdentity).MetadataToken;
            RestrictedCilManagedCallTargetV1 target=new(token,callee.Identity.StableIdentity,[RestrictedCilTypeV1.Int32],RestrictedCilTypeV1.Int64,"compiler-local:corelib-nullable-int32/v1");
            RestrictedCilImportResultV1 standalone=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2,managedCallTargets:new Dictionary<int,RestrictedCilManagedCallTargetV1>{{token,target}}).ImportImage(actual.Source.Pe,new("Fixture","Read"));
            if(standalone.Status!=RestrictedCilImportStatusV1.Unsupported || standalone.Diagnostics.Count==0)throw new Exception("Standalone nullable return authority: "+standalone.Status+" "+string.Join(';',standalone.Diagnostics));
            Console.WriteLine("NULLABLE-RETURN-STANDALONE "+string.Join(';',standalone.Diagnostics.Select(d=>d.Code)));
            var simple=Compile(Request(Source(body,"int? value=ReadOptional(input);return value.GetValueOrDefault();")));Link(simple);
            var unowned=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2,managedCallTargets:new Dictionary<int,RestrictedCilManagedCallTargetV1>{{token,target}})
                .ImportImage(simple.Source.Pe!,new("Fixture","Read"));
            if(unowned.Status!=RestrictedCilImportStatusV1.Unsupported || !unowned.Diagnostics.Any(d=>d.Code=="HCCIL1890"))throw new Exception("Unowned nullable-return call: "+string.Join(';',unowned.Diagnostics));
            var standaloneReturn=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(actual.Source.Pe,new("Fixture","ReadOptional"));
            if(standaloneReturn.Status!=RestrictedCilImportStatusV1.Unsupported || !standaloneReturn.Diagnostics.Any(d=>d.Code=="HCCIL1010"))throw new Exception("Standalone nullable return entry gate");
            string libraryText=text.Replace("class Fixture","class ExternalFactory");
            var library=ManagedSourceCompanionV1.CompileBodyWorld(Request(libraryText) with{AssemblyName="OptionalReturnLibrary"},new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("ExternalFactory","Read")],[],[0]);
            if(library.Compilation?.Status!=RestrictedCilImportStatusV1.Success || library.Source.Pe is null)throw new Exception("Nullable-return dependency emit");
            var externalRequest=Request("using HybridCPU.Compiler.Profiles; public static class Fixture {[HybridCpuKernelNoHeap] public static long Read(int input)=>ExternalFactory.Read(input);}") with{ReferenceImages=[..references,library.Source.Pe!]};
            foreach(bool selected in new[]{false,true}) {
                var denied=ManagedSourceCompanionV1.CompileBodyWorld(externalRequest,new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fixture","Read")],selected?[4]:[],selected?[0]:[0,4]);
                if(denied.Source.Pe is null || denied.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("External nullable return body/declaration gate: "+string.Join(';',denied.Source.Diagnostics));
            }
            foreach(RestrictedCilImportBudgetsV1 budgets in new[]{RestrictedCilImportBudgetsV1.Production with{MaximumCalls=0},RestrictedCilImportBudgetsV1.Production with{MaximumEvaluationStack=1},
                RestrictedCilImportBudgetsV1.Production with{MaximumLocals=0},RestrictedCilImportBudgetsV1.Production with{MaximumBasicBlocks=1},RestrictedCilImportBudgetsV1.Production with{MaximumMethodBodyBytes=1}}) {
                var denied=Compile(request,new(budgets:budgets,mode:RestrictedCilImportModeV1.ScalarControlFlowV2));
                if(denied.Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Nullable return original budget: "+string.Join(';',denied.Compilation?.Diagnostics??[]));
            }
            RestrictedCilImporterV1 reusable=new(mode:RestrictedCilImportModeV1.ScalarControlFlowV2);
            using CancellationTokenSource cancelled=new();cancelled.Cancel();try{Compile(request,reusable,cancelled.Token);throw new Exception("Nullable return cancellation did not escape");}catch(OperationCanceledException){}
            if(!Link(Compile(request,reusable)).RestrictedImage!.PackageBytes.SequenceEqual(linked.RestrictedImage.PackageBytes))throw new Exception("Nullable return cancellation/reuse");
            foreach(string invalid in new[]{text.Replace("int? copy=value;","int? copy=Missing;"),text.Replace("int? copy=value;","int[] allocation=new int[1];int? copy=value;")}) {
                var denied=Compile(Request(invalid));
                if(denied.Source.Pe is not null || denied.Compilation is not null || !denied.Source.Diagnostics.Any(d=>d.Severity=="Error"))throw new Exception("Nullable return source/NoHeap before emit");
            }
        }
        Console.WriteLine("PASS private primary Nullable<int> return NoHeap/source/nominal packed-I8 call/local/ret, inline host oracle/repeat/maps/image and ABI/body/forgery/budget/cancel controls");
    }
    public static void RunExternalDefault(bool constantOnly = false, bool lazyOnly = false, bool wideHelperOnly = false, int? onlyHelperKind = null)
    {
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] core = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")), File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string wide = "public readonly struct WideValue { public long Value { get; } public WideValue(long value) { Value=value; } }";
        const string helper = "public static class Fallbacks { public static int Adjust(int fallback)=>unchecked(fallback+1); }";
        byte[] Library(string body, string name = "FallbackLibrary") {
            ManagedSourceBodyWorldResultV1 emitted = ManagedSourceCompanionV1.CompileBodyWorld(new(name,[new("/src/Library.cs",wide+body)],core),
                new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2),[new("Fallbacks","Adjust")],null,[0]);
            return emitted.Source.Pe ?? throw new Exception("External fallback source emit: "+string.Join(';',emitted.Source.Diagnostics));
        }
        byte[] definition=Library(helper);
        ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 request, int[]? bodies=null, int[]? declarations=null,
            int calls=100, int stack=100, int resolution=10000, int? maximumLocals=null, int? blocks=null) => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets:RestrictedCilImportBudgetsV1.Production with {MaximumCalls=calls,MaximumEvaluationStack=stack,MaximumLocals=maximumLocals??RestrictedCilImportBudgetsV1.Production.MaximumLocals,MaximumBasicBlocks=blocks??RestrictedCilImportBudgetsV1.Production.MaximumBasicBlocks},
                    graphBudgets:ScalarControlFlowV2ProfileContractV1.Default.Budgets with {MaximumMetadataResolutionSteps=resolution}),
                [new("Fixture","Read")],bodies??[4],declarations??[0]);
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 result) {
            if(result.Compilation?.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("External nullable default: "+string.Join(';',result.Source.Diagnostics)+"; "+string.Join(';',result.Compilation?.Diagnostics??[]));
            ScalarControlFlowV2LinkedProgramV1 image =new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation,result.Compilation.Graph!.RootIdentities.Single());
            if(image.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception("External nullable image: "+string.Join(';',image.Diagnostics));
            return image;
        }
        static string Code(ScalarControlFlowV2LinkedProgramV1 image,string name)=>image.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+name+"(",StringComparison.Ordinal)).CodeSha256;
        if(lazyOnly){CheckConstant(onlyLazy:true);return;}
        if(constantOnly){CheckConstant();return;}
        foreach(bool kernel in new[]{false,true}) foreach(string initializer in new[]{"(int)input","null","input==0?null:(int)input"}) {
            string source="using HybridCPU.Compiler.Profiles; public static class Fixture { private static long Consume(int? value,int fallback)=>(long)value.GetValueOrDefault(Fallbacks.Adjust(fallback)); "+
                (kernel?"[HybridCpuKernelNoHeap] ":"")+"public static long Read(long input) { int? value="+initializer+"; return new WideValue(Consume(value,-7)).Value; } }";
            CompanionSourceRequestV1 request =new CompanionSourceRequestV1("ExternalFallback",[new("/src/Primary.cs",source)],[..core,definition]);
            ManagedSourceBodyWorldResultV1 actual =Compile(request); ScalarControlFlowV2LinkedProgramV1 image =Link(actual); ManagedSourceBodyWorldResultV1 repeat =Compile(request);
            if(!kernel && initializer=="(int)input") {
                string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"external-default-artifacts");
                Directory.CreateDirectory(artifacts);
                File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),source);
                File.WriteAllBytes(Path.Combine(artifacts,"FallbackLibrary.dll"),definition);
                File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),actual.Source.Pe!);
                File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),actual.Source.Pdb!);
                File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),image.RestrictedImage!.PackageBytes);
                File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
                string cliImage=Path.Combine(artifacts,"Cli.hcexe");
                string[] cliArguments=["compile-source-image","--source",Path.Combine(artifacts,"Primary.cs"),
                    "--metadata-reference",typeof(object).Assembly.Location,"--reference",Path.Combine(directory,"System.Runtime.dll"),
                    "--reference",Path.Combine(directory,"netstandard.dll"),"--reference",typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location,
                    "--body-reference",Path.Combine(artifacts,"FallbackLibrary.dll"),"--assembly-name","ExternalFallback","--type","Fixture","--method","Read","--out",cliImage];
                using StringWriter cliOutput =new StringWriter(); using StringWriter cliError =new StringWriter();
                if(ManagedSourceCommandLineV1.Run(cliArguments,cliOutput,cliError)!=0 || !image.RestrictedImage.PackageBytes.SequenceEqual(File.ReadAllBytes(cliImage)))throw new Exception("External fallback CLI/API image equality: "+cliError);
                ManagedCallGraphCompilationV1 wrongIdentity =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule,actual.Source.Pe!,"wrong-identity",[new("Fixture","Read")],[],
                    DependencyModules:[new(Library(helper,"WrongIdentity"),"wrong-body")],MetadataOnlyModules:[new(core[0],"core")]));
                if(wrongIdentity.Status==RestrictedCilImportStatusV1.Success)throw new Exception("External fallback unresolved assembly accepted");
                ManagedSourceBodyWorldResultV1 changed =Compile(request with{ReferenceImages=[..core,Library(helper.Replace("fallback+1","fallback+2"))]});
                if(Code(Link(changed),"Adjust")==Code(image,"Adjust") || changed.Source.SourceInputDigest==actual.Source.SourceInputDigest ||
                    changed.Compilation!.Graph!.GraphDigest==actual.Compilation!.Graph!.GraphDigest || !changed.Source.Pe!.SequenceEqual(actual.Source.Pe!))
                    throw new Exception("External fallback body identity/output invalidation");
            }
            using(PEReader pe =new PEReader(new MemoryStream(actual.Source.Pe!))) {
                MetadataReader md =pe.GetMetadataReader();
                MethodDefinition consume =md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                MemberReference adjust =md.GetMemberReference(md.MemberReferences.Single(h=>md.GetString(md.GetMemberReference(h).Name)=="Adjust"));
                Console.WriteLine("EXTERNAL-DEFAULT-CIL "+Convert.ToHexString(pe.GetMethodBody(consume.RelativeVirtualAddress).GetILBytes()!)+" signature="+Convert.ToHexString(md.GetBlobBytes(adjust.Signature)));
            }
            ManagedSourceBodyWorldResultV1 single =Compile(request with{Documents=[new("/src/Primary.cs",source.Replace("public static class Fixture {","public static class Fixture { private static int Adjust(int fallback)=>unchecked(fallback+1);").Replace("Fallbacks.Adjust(fallback)","Adjust(fallback)")+wide)],ReferenceImages=core},[],[0]);
            ScalarControlFlowV2LinkedProgramV1 singleImage =Link(single);
            // Packed ingress is independently expressed using scalar bits. Keep the
            // eager helper call before selecting payload/presence, including present values.
            string scalarSource=source.Replace("Consume(int? value,int fallback)=>(long)value.GetValueOrDefault(Fallbacks.Adjust(fallback));",
                "Consume(long value,int fallback) { int adjusted=Fallbacks.Adjust(fallback); return (value&1)!=0?(long)unchecked((int)(value>>1)):(long)adjusted; }")
                .Replace("int? value="+initializer+";", "long value="+(initializer=="null"?"0L":initializer.Contains('?')?"input==0?0L:(((long)(uint)(int)input<<1)|1L)":"((long)(uint)(int)input<<1)|1L")+";");
            ManagedSourceBodyWorldResultV1 scalar =Compile(request with{Documents=[new("/src/Primary.cs",scalarSource)]}); ScalarControlFlowV2LinkedProgramV1 scalarImage =Link(scalar);
            if(new[]{"Read","Consume","Adjust"}.Any(name=>Code(image,name)!=Code(singleImage,name)) || Code(image,"Adjust")!=Code(scalarImage,"Adjust") ||
                !actual.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) ||
                !image.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeat).RestrictedImage!.PackageBytes) || actual.SourceMaps.Count!=2 ||
                actual.SourceMaps.Any(row=>repeat.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("External nullable scalar/single-module/repeat/map/image oracle");
            AssemblyLoadContext context =new System.Runtime.Loader.AssemblyLoadContext("external-fallback",isCollectible:true);
            try {
                context.LoadFromStream(new MemoryStream(definition));
                Type host =context.LoadFromStream(new MemoryStream(actual.Source.Pe!)).GetType("Fixture")!;
                MethodInfo consume =host.GetMethod("Consume",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue}) foreach(int fallback in new[]{int.MinValue,-7,0,int.MaxValue}) foreach(bool present in new[]{false,true})
                    if(!Equals(consume.Invoke(null,[present?(int?)input:null,fallback]),present?(long)input:(long)unchecked(fallback+1)))throw new Exception("External fallback host boundary");
                foreach(long input in new[]{long.MinValue,-1L,0,1L,long.MaxValue})
                    if(!Equals(host.GetMethod("Read")!.Invoke(null,[input]),initializer=="null"||initializer.Contains('?')&&input==0?-6L:(long)unchecked((int)input)))throw new Exception("Retained host boundary");
            } finally {context.Unload();}
            foreach(ManagedSourceBodyWorldResultV1? denied in new[]{Compile(request,[]),Compile(request,[],[0,4]),
                Compile(request with{Documents=[new("/src/Primary.cs",source.Replace("[HybridCpuKernelNoHeap]","").Replace("private static long Consume","public static long Consume"))]})})
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success || denied.Source.Pe is null && !denied.Source.Diagnostics.Any(d=>d.Severity=="Error"))throw new Exception("External fallback role/identity/public gate: "+string.Join(';',denied.Source.Diagnostics)+"; "+string.Join(';',denied.Compilation?.Diagnostics??[]));
            foreach(int limit in new[]{0,1,2}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(request,calls:limit);
                if(denied.Compilation?.Status!=(limit==2?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("External fallback original call budget "+limit+": "+denied.Compilation?.Status);
            }
            if(Compile(request,stack:1).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted || Compile(request,resolution:1).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("External fallback original stack/metadata budgets");
            if(kernel || initializer!="(int)input")continue;
            foreach((string? declaration, string? expression) in new[]{
                ("public static long Adjust(long x)=>unchecked(x+1);","(int)Fallbacks.Adjust(fallback)"),
                ("public static int Adjust(ref int x)=>++x;","Fallbacks.Adjust(ref fallback)"),
                ("public static int Adjust(int? x)=>x.GetValueOrDefault();","Fallbacks.Adjust(fallback)")}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(request with{ReferenceImages=[..core,Library("public static class Fallbacks { "+declaration+" }")],
                    Documents=[new("/src/Primary.cs",source.Replace("Fallbacks.Adjust(fallback)",expression))]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("External fallback width/byref/nullable signature gate");
            }
            foreach(string bad in new[]{
                "public static int Adjust(int x)=>checked(x+1);", "public static int Adjust(int x)=>10/x;",
                "public static int Adjust(int x)=>Adjust(x);", "public static int Adjust(int x) { while(x>0)x--; return x; }",
                "public static int Adjust(int x) { try { return x; } finally { System.GC.KeepAlive(x); } }",
                "private static int state; public static int Adjust(int x)=>state++;"}) {
                byte[] badDefinition=Library("public static class Fallbacks { "+bad+" }");
                ManagedSourceBodyWorldResultV1 denied =Compile(request with{ReferenceImages=[..core,badDefinition]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("External fallback effect/fault/EH/recursive/loop gate");
            }
        }
        Console.WriteLine("PASS selected external I4 helper eager nullable default -> retained Int64 source/NoHeap/CIL/host/single-module/scalar/repeat/maps/image; roles/identity/public/effects/EH/fault/cycle/budgets");
        CheckConstant();
        void CheckConstant(bool onlyLazy = false) {
            const string source="using HybridCPU.Compiler.Profiles; public static class Fixture { private static long Consume(int? value)=>(long)value.GetValueOrDefault(Fallbacks.Adjust(-7)); [HybridCpuKernelNoHeap] public static long Read(long input) { int? value=(int)input; return new WideValue(Consume(value)).Value; } }";
            CompanionSourceRequestV1 request =new CompanionSourceRequestV1("ExternalConstantFallback",[new("/src/Constant.cs",source)],[..core,definition]);
            if(onlyLazy){CheckLazy();return;}
            ManagedSourceBodyWorldResultV1 actual =Compile(request);
            Console.WriteLine("CONSTANT-EXTERNAL-DEFAULT emitted="+(actual.Source.Pe is not null)+" status="+actual.Compilation?.Status+" "+string.Join(';',actual.Compilation?.Diagnostics??[]));
            ScalarControlFlowV2LinkedProgramV1 linked =Link(actual); ManagedSourceBodyWorldResultV1 repeated =Compile(request);
            ManagedSourceBodyWorldResultV1 single =Compile(request with{ReferenceImages=core,Documents=[new("/src/Constant.cs",source.Replace("public static class Fixture {","public static class Fixture { private static int Adjust(int x)=>unchecked(x+1);").Replace("Fallbacks.Adjust(-7)","Adjust(-7)")+wide)]},[],[0]);
            if(new[]{"Read","Consume","Adjust"}.Any(name=>Code(linked,name)!=Code(Link(single),name)) ||
                !actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!) ||
                actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeated).RestrictedImage!.PackageBytes))throw new Exception("Constant external fallback source/image/repeat/single-module");
            AssemblyLoadContext context =new System.Runtime.Loader.AssemblyLoadContext("constant-external-fallback",isCollectible:true);
            try {
                context.LoadFromStream(new MemoryStream(definition));
                Type host =context.LoadFromStream(new MemoryStream(actual.Source.Pe!)).GetType("Fixture")!;
                MethodInfo consume =host.GetMethod("Consume",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                foreach(int? value in new int?[]{null,int.MinValue,-1,0,1,int.MaxValue})
                    if(!Equals(consume.Invoke(null,[value]),(long)(value??-6)))throw new Exception("Constant external fallback host null/present");
            } finally {context.Unload();}
            foreach(int calls in new[]{0,1,2})if(Compile(request,calls:calls).Compilation?.Status!=(calls==2?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("Constant external fallback call budget");
            if(Compile(request,stack:1).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Constant external fallback erased receiver stack budget");
            foreach(string deniedSource in new[]{source.Replace("private static long Consume","public static long Consume"),source.Replace("Fallbacks.Adjust(-7)","Fallbacks.Adjust((int)value.Value)")}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(request with{Documents=[new("/src/Constant.cs",deniedSource.Replace("[HybridCpuKernelNoHeap]",""))]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Constant external fallback public/borrowed-receiver gate");
            }
            Console.WriteLine("PASS private nullable parameter with eager constant external helper default/no second parameter -> NoHeap/source/image/host/single-module/repeat/maps and original call/stack/public/receiver gates");
            const string binary="public static class MoreFallbacks { public static int Combine(int fallback,int delta)=>unchecked(fallback+delta); }";
            CompanionSourceRequestV1 binaryRequest =request with{ReferenceImages=[..core,Library(helper+binary)],
                Documents=[new("/src/Constant.cs",source.Replace("Fallbacks.Adjust(-7)","MoreFallbacks.Combine(-7,1)"))]};
            ManagedSourceBodyWorldResultV1 next =Compile(binaryRequest); ScalarControlFlowV2LinkedProgramV1 binaryImage =Link(next); ManagedSourceBodyWorldResultV1 binaryRepeat =Compile(binaryRequest);
            string binaryArtifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"binary-default-artifacts");
            Directory.CreateDirectory(binaryArtifacts);
            File.WriteAllText(Path.Combine(binaryArtifacts,"Primary.cs"),binaryRequest.Documents[0].Text);
            File.WriteAllBytes(Path.Combine(binaryArtifacts,"FallbackLibrary.dll"),binaryRequest.ReferenceImages[4].ToArray());
            File.WriteAllBytes(Path.Combine(binaryArtifacts,"Primary.dll"),next.Source.Pe!);
            File.WriteAllBytes(Path.Combine(binaryArtifacts,"Primary.pdb"),next.Source.Pdb!);
            File.WriteAllBytes(Path.Combine(binaryArtifacts,"Primary.hcexe"),binaryImage.RestrictedImage!.PackageBytes);
            File.WriteAllText(Path.Combine(binaryArtifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(next.SourceMaps));
            ManagedSourceBodyWorldResultV1 binarySingle =Compile(binaryRequest with{ReferenceImages=core,Documents=[new("/src/Constant.cs",binaryRequest.Documents[0].Text.Replace("public static class Fixture {","public static class Fixture { private static int Combine(int x,int y)=>unchecked(x+y);").Replace("MoreFallbacks.Combine(-7,1)","Combine(-7,1)")+wide)]},[],[0]);
            if(new[]{"Read","Consume","Combine"}.Any(name=>Code(binaryImage,name)!=Code(Link(binarySingle),name)) ||
                !next.Source.Pe!.SequenceEqual(binaryRepeat.Source.Pe!) || !next.Source.Pdb!.SequenceEqual(binaryRepeat.Source.Pdb!) ||
                next.SourceMaps.Count!=2 || next.SourceMaps.Any(row=>binaryRepeat.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                !binaryImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(binaryRepeat).RestrictedImage!.PackageBytes))throw new Exception("Binary external fallback single-module/PE/PDB/maps/image oracle");
            using(PEReader pe =new PEReader(new MemoryStream(next.Source.Pe!))) {
                MetadataReader md =pe.GetMetadataReader(); MethodDefinition method =md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                Console.WriteLine("BINARY-DEFAULT-CIL "+Convert.ToHexString(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!));
            }
            foreach((string? label, ManagedSourceBodyWorldResultV1? denied) in new[]{("omitted",Compile(binaryRequest,[])),("metadata",Compile(binaryRequest,[],[0,4])),("stack",Compile(binaryRequest,stack:2)),("calls",Compile(binaryRequest,calls:1)),("resolution",Compile(binaryRequest,resolution:1))})
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success || denied.Source.Pe is null && !denied.Source.Diagnostics.Any(d=>d.Severity=="Error"))throw new Exception("Binary helper authority/original stack/call/resolution budgets: "+label+" "+denied.Compilation?.Status);
            foreach(string body in new[]{"checked(fallback+delta)","fallback/delta","Combine(fallback,delta)"}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(binaryRequest with{ReferenceImages=[..core,Library(helper+binary.Replace("unchecked(fallback+delta)",body))]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Binary helper fault/recursive proof");
            }
            AssemblyLoadContext binaryContext =new System.Runtime.Loader.AssemblyLoadContext("binary-fallback",isCollectible:true);
            try {
                binaryContext.LoadFromStream(new MemoryStream(binaryRequest.ReferenceImages[4].ToArray()));
                Type host =binaryContext.LoadFromStream(new MemoryStream(next.Source.Pe!)).GetType("Fixture")!;
                MethodInfo consume =host.GetMethod("Consume",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                foreach(int? value in new int?[]{null,int.MinValue,-1,0,1,int.MaxValue})if(!Equals(consume.Invoke(null,[value]),(long)(value??-6)))throw new Exception("Binary helper host null/present");
            } finally {binaryContext.Unload();}
            Console.WriteLine("PASS selected external I4,I4->I4 nullable constant default -> NoHeap/retained Int64/CIL/single-module/host/repeat/maps/image and roles/budgets/fault/cycle controls");
            const string genericLibrary=" public static class MoreFallbacks { public static T Identity<T>(T value)=>value; }";
            CompanionSourceRequestV1 genericRequest =binaryRequest with{ReferenceImages=[..core,Library(helper+genericLibrary)],
                Documents=[new("/src/Constant.cs",binaryRequest.Documents[0].Text.Replace("MoreFallbacks.Combine(-7,1)","MoreFallbacks.Identity<int>(-7)"))]};
            ManagedSourceBodyWorldResultV1 generic =Compile(genericRequest); ScalarControlFlowV2LinkedProgramV1 genericImage =Link(generic); ManagedSourceBodyWorldResultV1 genericRepeat =Compile(genericRequest);
            ManagedSourceBodyWorldResultV1 genericSingle =Compile(genericRequest with{ReferenceImages=core,Documents=[new("/src/Constant.cs",genericRequest.Documents[0].Text
                .Replace("public static class Fixture {","public static class Fixture { private static int Identity(int x)=>x;").Replace("MoreFallbacks.Identity<int>(-7)","Identity(-7)")+wide)]},[],[0]);
            if(new[]{"Read","Consume","Identity"}.Any(name=>Code(genericImage,name)!=Code(Link(genericSingle),name)) ||
                !generic.Source.Pe!.SequenceEqual(genericRepeat.Source.Pe!) || !generic.Source.Pdb!.SequenceEqual(genericRepeat.Source.Pdb!) ||
                generic.SourceMaps.Count!=2 || generic.SourceMaps.Any(row=>genericRepeat.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                !genericImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(genericRepeat).RestrictedImage!.PackageBytes))throw new Exception("Generic nullable default scalar/single-module/repeat/maps/image");
            ManagedCompiledMethodV1 instantiated =generic.Compilation!.Methods.Single(m=>m.Identity.MethodName=="Identity");
            if(instantiated.Identity.GenericMethodArguments?.Single()!=new RestrictedCilGenericArgumentV1("System.Int32",RestrictedCilTypeV1.Int32))throw new Exception("Generic helper exact context identity");
            using(PEReader pe =new PEReader(new MemoryStream(generic.Source.Pe!))) {
                MetadataReader md =pe.GetMetadataReader(); MethodDefinition method =md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                if(md.GetTableRowCount(TableIndex.MethodSpec)!=1)throw new Exception("Exact generic fixture MethodSpec count");
                MethodSpecificationHandle spec =MetadataTokens.MethodSpecificationHandle(1);
                Console.WriteLine("GENERIC-DEFAULT-CIL "+Convert.ToHexString(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!)+" MethodSpec="+Convert.ToHexString(md.GetBlobBytes(md.GetMethodSpecification(spec).Signature)));
                BlobHandle blob =md.GetMethodSpecification(spec).Signature;
                int blobStart=pe.PEHeaders.MetadataStartOffset+md.GetHeapMetadataOffset(HeapIndex.Blob)+MetadataTokens.GetHeapOffset(blob)+1;
                ManagedCallGraphCompilationV1 Raw(byte[] image)=>new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule,image,"generic-raw",[new("Fixture","Read")],[],
                    DependencyModules:[new(genericRequest.ReferenceImages[4],"selected-helper")],MetadataOnlyModules:[new(core[0],"core")]));
                if(Raw(generic.Source.Pe!).Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Exact generic raw body-world baseline");
                foreach((int position, byte replacement) in new[]{(0,(byte)0),(1,(byte)2),(2,(byte)9)}) {
                    byte[] changed=generic.Source.Pe!.ToArray(); changed[blobStart+position]=replacement;
                    ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                    if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status ||
                        !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Malformed/wrong-width MethodSpec admitted or unstable");
                }
            }
            foreach(ManagedSourceBodyWorldResultV1? denied in new[]{Compile(genericRequest,[]),Compile(genericRequest,[],[0,4]),Compile(genericRequest,stack:1),Compile(genericRequest,calls:1),Compile(genericRequest,resolution:1)})
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success || denied.Source.Pe is null && !denied.Source.Diagnostics.Any(d=>d.Severity=="Error"))throw new Exception("Generic helper role/original budget gate");
            foreach((string? declaration, string? expression) in new[]{
                (genericLibrary,"(int)MoreFallbacks.Identity<long>(-7L)"),
                (genericLibrary.Replace("=>value;"," where T:struct =>value;"),"MoreFallbacks.Identity<int>(-7)"),
                (genericLibrary.Replace("=>value;","{ System.GC.KeepAlive(value); return value; }"),"MoreFallbacks.Identity<int>(-7)"),
                (genericLibrary.Replace("=>value;","=>Identity<T>(value);"),"MoreFallbacks.Identity<int>(-7)")}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(genericRequest with{ReferenceImages=[..core,Library(helper+declaration)],Documents=[new("/src/Constant.cs",genericRequest.Documents[0].Text.Replace("MoreFallbacks.Identity<int>(-7)",expression))]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Generic helper width/constraints/effects/recursion gate");
            }
            AssemblyLoadContext genericContext =new System.Runtime.Loader.AssemblyLoadContext("generic-fallback",isCollectible:true);
            try {
                genericContext.LoadFromStream(new MemoryStream(genericRequest.ReferenceImages[4].ToArray()));
                Type host =genericContext.LoadFromStream(new MemoryStream(generic.Source.Pe!)).GetType("Fixture")!;
                MethodInfo consume =host.GetMethod("Consume",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                foreach(int? value in new int?[]{null,int.MinValue,-1,0,1,int.MaxValue})if(!Equals(consume.Invoke(null,[value]),(long)(value??-7)))throw new Exception("Generic helper host null/present");
            } finally {genericContext.Unload();}
            Console.WriteLine("PASS selected external closed Identity<int> nullable default -> NoHeap/retained Int64/MethodSpec/context/scalar bytes/host/repeat/maps/image; roles/budgets/width/constraints/effects/cycle");
            string genericArtifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"generic-default-artifacts");
            Directory.CreateDirectory(genericArtifacts);
            File.WriteAllText(Path.Combine(genericArtifacts,"Primary.cs"),genericRequest.Documents[0].Text);
            File.WriteAllBytes(Path.Combine(genericArtifacts,"FallbackLibrary.dll"),genericRequest.ReferenceImages[4].ToArray());
            File.WriteAllBytes(Path.Combine(genericArtifacts,"Primary.dll"),generic.Source.Pe!);
            File.WriteAllBytes(Path.Combine(genericArtifacts,"Primary.pdb"),generic.Source.Pdb!);
            File.WriteAllBytes(Path.Combine(genericArtifacts,"Primary.hcexe"),genericImage.RestrictedImage!.PackageBytes);
            File.WriteAllText(Path.Combine(genericArtifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(generic.SourceMaps));
            string genericCliImage=Path.Combine(genericArtifacts,"Cli.hcexe");
            using(StringWriter output =new StringWriter()) using(StringWriter error =new StringWriter()) {
                int exit=ManagedSourceCommandLineV1.Run(["compile-source-image","--source",Path.Combine(genericArtifacts,"Primary.cs"),
                    "--metadata-reference",typeof(object).Assembly.Location,"--reference",Path.Combine(directory,"System.Runtime.dll"),
                    "--reference",Path.Combine(directory,"netstandard.dll"),"--reference",typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location,
                    "--body-reference",Path.Combine(genericArtifacts,"FallbackLibrary.dll"),"--assembly-name","ExternalConstantFallback","--type","Fixture","--method","Read","--out",genericCliImage],output,error);
                if(exit!=0 || !genericImage.RestrictedImage!.PackageBytes.SequenceEqual(File.ReadAllBytes(genericCliImage)))throw new Exception("Generic fallback CLI/API image equality: "+error);
                Console.WriteLine("PASS generic nullable default actual CLI/API image bytes equal");
            }
            const string ownerLibrary=" public static class MoreFallbacks<T> { public static T Identity(T value)=>value; }";
            CompanionSourceRequestV1 ownerRequest =genericRequest with{ReferenceImages=[..core,Library(helper+ownerLibrary)],
                Documents=[new("/src/Constant.cs",genericRequest.Documents[0].Text.Replace("MoreFallbacks.Identity<int>(-7)","MoreFallbacks<int>.Identity(-7)"))]};
            ManagedSourceBodyWorldResultV1 genericOwner =Compile(ownerRequest); ScalarControlFlowV2LinkedProgramV1 ownerImage =Link(genericOwner); ManagedSourceBodyWorldResultV1 ownerRepeat =Compile(ownerRequest);
            if(new[]{"Read","Consume","Identity"}.Any(name=>Code(ownerImage,name)!=Code(genericImage,name)) ||
                !genericOwner.Source.Pe!.SequenceEqual(ownerRepeat.Source.Pe!) || !genericOwner.Source.Pdb!.SequenceEqual(ownerRepeat.Source.Pdb!) ||
                genericOwner.SourceMaps.Count!=2 || genericOwner.SourceMaps.Any(row=>ownerRepeat.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                !ownerImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(ownerRepeat).RestrictedImage!.PackageBytes))throw new Exception("Constructed owner scalar/method-context equivalence and repeat/maps/image");
            ManagedMethodIdentityV1 ownerMethod =genericOwner.Compilation!.Methods.Single(m=>m.Identity.MethodName=="Identity").Identity;
            if(ownerMethod.GenericTypeArguments?.Single()!=new RestrictedCilGenericArgumentV1("System.Int32",RestrictedCilTypeV1.Int32) || ownerMethod.GenericMethodArguments?.Count!=0 ||
                ownerMethod.StableIdentity==instantiated.Identity.StableIdentity)throw new Exception("VAR and MVAR identities conflated");
            using(PEReader pe =new PEReader(new MemoryStream(genericOwner.Source.Pe!))) {
                MetadataReader md =pe.GetMetadataReader();
                MemberReference member =md.GetMemberReference(md.MemberReferences.Single(h=>md.GetString(md.GetMemberReference(h).Name)=="Identity"));
                if(member.Parent.Kind!=HandleKind.TypeSpecification)throw new Exception("Constructed owner emitted parent");
                BlobHandle blob =md.GetTypeSpecification((TypeSpecificationHandle)member.Parent).Signature;
                byte[] signature=md.GetBlobBytes(blob);
                Console.WriteLine("OWNER-DEFAULT-TYPESPEC "+Convert.ToHexString(signature));
                if(signature.Length>=128 || signature[^2]!=1 || signature[^1]!=8)throw new Exception("Constructed owner exact fixture signature");
                int start=pe.PEHeaders.MetadataStartOffset+md.GetHeapMetadataOffset(HeapIndex.Blob)+MetadataTokens.GetHeapOffset(blob)+1;
                ManagedCallGraphCompilationV1 RawOwner(byte[] bytes)=>new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule,bytes,"owner-raw",[new("Fixture","Read")],[],
                    DependencyModules:[new(ownerRequest.ReferenceImages[4],"selected-owner")],MetadataOnlyModules:[new(core[0],"core")]));
                if(RawOwner(genericOwner.Source.Pe!).Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Constructed owner raw baseline");
                foreach((int position, byte replacement) in new[]{(0,(byte)0),(signature.Length-2,(byte)2),(signature.Length-1,(byte)9)}) {
                    byte[] changed=genericOwner.Source.Pe!.ToArray(); changed[start+position]=replacement;
                    ManagedCallGraphCompilationV1 denied =RawOwner(changed); ManagedCallGraphCompilationV1 again =RawOwner(changed);
                    if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Constructed owner malformed/arity/width TypeSpec gate");
                }
            }
            foreach(ManagedSourceBodyWorldResultV1? denied in new[]{Compile(ownerRequest,[]),Compile(ownerRequest,[],[0,4]),Compile(ownerRequest,stack:1),Compile(ownerRequest,calls:1),Compile(ownerRequest,resolution:1)})
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success || denied.Source.Pe is null && !denied.Source.Diagnostics.Any(d=>d.Severity=="Error"))throw new Exception("Constructed owner roles/budgets");
            foreach((string? declaration, string? expression) in new[]{
                (ownerLibrary,"(int)MoreFallbacks<long>.Identity(-7L)"),
                (ownerLibrary.Replace("MoreFallbacks<T> {","MoreFallbacks<T> where T:struct {"),"MoreFallbacks<int>.Identity(-7)"),
                (ownerLibrary.Replace("=>value;","{ System.GC.KeepAlive(value); return value; }"),"MoreFallbacks<int>.Identity(-7)"),
                (ownerLibrary.Replace("=>value;","=>Identity(value);"),"MoreFallbacks<int>.Identity(-7)")}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(ownerRequest with{ReferenceImages=[..core,Library(helper+declaration)],Documents=[new("/src/Constant.cs",ownerRequest.Documents[0].Text.Replace("MoreFallbacks<int>.Identity(-7)",expression))]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Constructed owner width/constraints/effects/recursion");
            }
            AssemblyLoadContext ownerContext =new System.Runtime.Loader.AssemblyLoadContext("owner-fallback",isCollectible:true);
            try {
                ownerContext.LoadFromStream(new MemoryStream(ownerRequest.ReferenceImages[4].ToArray()));
                Type host =ownerContext.LoadFromStream(new MemoryStream(genericOwner.Source.Pe!)).GetType("Fixture")!;
                MethodInfo consume =host.GetMethod("Consume",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                foreach(int? value in new int?[]{null,int.MinValue,-1,0,1,int.MaxValue})if(!Equals(consume.Invoke(null,[value]),(long)(value??-7)))throw new Exception("Constructed owner host null/present");
            } finally {ownerContext.Unload();}
            Console.WriteLine("PASS selected external constructed owner<int> nullable default -> NoHeap/scalar bytes/VAR-MVAR identity separation/host/repeat/maps/image; roles/budgets/width/constraints/effects/cycle");
            CompanionSourceRequestV1 bothRequest =ownerRequest with{ReferenceImages=[..core,Library(helper+" public static class MoreFallbacks<T> { public static U Identity<U>(U value)=>value; }")],
                Documents=[new("/src/Constant.cs",ownerRequest.Documents[0].Text.Replace("MoreFallbacks<int>.Identity(-7)","MoreFallbacks<int>.Identity<int>(-7)"))]};
            ManagedSourceBodyWorldResultV1 both =Compile(bothRequest); ManagedSourceBodyWorldResultV1 bothRepeat =Compile(bothRequest);
            ScalarControlFlowV2LinkedProgramV1 bothImage =Link(both);
            if(new[]{"Read","Consume","Identity"}.Any(name=>Code(bothImage,name)!=Code(genericImage,name)) ||
                !both.Source.Pe!.SequenceEqual(bothRepeat.Source.Pe!) || !both.Source.Pdb!.SequenceEqual(bothRepeat.Source.Pdb!) ||
                both.SourceMaps.Count!=2 || both.SourceMaps.Any(row=>bothRepeat.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                !bothImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(bothRepeat).RestrictedImage!.PackageBytes))throw new Exception("Combined context scalar bytes/repeat/maps/image");
            ManagedMethodIdentityV1 bothIdentity =both.Compilation!.Methods.Single(m=>m.Identity.MethodName=="Identity").Identity;
            if(bothIdentity.GenericTypeArguments?.Single()!=new RestrictedCilGenericArgumentV1("System.Int32",RestrictedCilTypeV1.Int32) ||
                bothIdentity.GenericMethodArguments?.Single()!=new RestrictedCilGenericArgumentV1("System.Int32",RestrictedCilTypeV1.Int32) ||
                bothIdentity.StableIdentity==ownerMethod.StableIdentity || bothIdentity.StableIdentity==instantiated.Identity.StableIdentity)throw new Exception("Combined VAR/MVAR exact identities");
            Console.WriteLine("PASS combined external owner<int>/method<int> nullable default scalar bytes and separate exact VAR/MVAR context identity");
            CheckLazy();
            void CheckLazy() {
            string lazySource=source.Replace("private static long Consume(int? value)=>(long)value.GetValueOrDefault(Fallbacks.Adjust(-7));",
                "private static long Consume(int? value)=>(long)(value ?? Fallbacks.Adjust(-7));");
                CompanionSourceRequestV1 lazyRequest =request with{Documents=[new("/src/Constant.cs",lazySource)]};
                ManagedSourceBodyWorldResultV1 lazy =Compile(lazyRequest); ManagedSourceBodyWorldResultV1 repeat =Compile(lazyRequest);
            Console.WriteLine("LAZY nullable coalescing private parameter emitted="+(lazy.Source.Pe is not null)+" status="+lazy.Compilation?.Status+" "+string.Join(';',lazy.Compilation?.Diagnostics??[]));
            if(lazy.Source.Pe is not null)using(PEReader pe =new PEReader(new MemoryStream(lazy.Source.Pe))) {
                    MetadataReader md =pe.GetMetadataReader(); MethodDefinition method =md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                    MethodBodyBlock body =pe.GetMethodBody(method.RelativeVirtualAddress);
                Console.WriteLine("LAZY-PARAMETER-CIL "+Convert.ToHexString(body.GetILBytes()!)+" locals="+(body.LocalSignature.IsNil?"none":Convert.ToHexString(md.GetBlobBytes(md.GetStandaloneSignature(body.LocalSignature).Signature)))+" stack="+body.MaxStack);
            }
                ScalarControlFlowV2LinkedProgramV1 lazyImage =Link(lazy);
            if(lazyImage.RestrictedImage is null)throw new Exception("Lazy private nullable coalescing image missing");
                ManagedSourceBodyWorldResultV1 single =Compile(lazyRequest with{ReferenceImages=core,Documents=[new("/src/Constant.cs",lazySource.Replace("public static class Fixture {","public static class Fixture { private static int Adjust(int value)=>unchecked(value+1);").Replace("Fallbacks.Adjust(-7)","Adjust(-7)")+wide)]},[],[0]);
            if(new[]{"Read","Consume","Adjust"}.Any(name=>Code(lazyImage,name)!=Code(Link(single),name)) ||
                !lazy.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !lazy.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) ||
                lazy.SourceMaps.Count!=2 || lazy.SourceMaps.Any(row=>repeat.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                !lazyImage.RestrictedImage.PackageBytes.SequenceEqual(Link(repeat).RestrictedImage!.PackageBytes))throw new Exception("Lazy source/private single-module/repeat/maps/image");
                AssemblyLoadContext context =new System.Runtime.Loader.AssemblyLoadContext("lazy-fallback",isCollectible:true);
            try {
                context.LoadFromStream(new MemoryStream(definition));
                    Type host =context.LoadFromStream(new MemoryStream(lazy.Source.Pe!)).GetType("Fixture")!;
                    MethodInfo consume =host.GetMethod("Consume",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                foreach(int? value in new int?[]{null,int.MinValue,-1,0,1,int.MaxValue})if(!Equals(consume.Invoke(null,[value]),(long)(value??-6)))throw new Exception("Lazy coalescing host boundary");
            } finally {context.Unload();}
            foreach(ManagedSourceBodyWorldResultV1? denied in new[]{Compile(lazyRequest,[]),Compile(lazyRequest,[],[0,4]),Compile(lazyRequest,stack:1),Compile(lazyRequest,calls:1),Compile(lazyRequest,resolution:1)})
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success || denied.Source.Pe is null && !denied.Source.Diagnostics.Any(d=>d.Severity=="Error"))throw new Exception("Lazy roles/original budgets");
            foreach(int callLimit in new[]{2,3})if(Compile(lazyRequest,calls:callLimit).Compilation?.Status!=(callLimit==3?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("Lazy original HasValue/helper/GetValueOrDefault call budget "+callLimit);
            if(Compile(lazyRequest,maximumLocals:0).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted ||
                Compile(lazyRequest,blocks:1).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Lazy original local/CFG budget");
            foreach(string badSource in new[]{lazySource.Replace("private static long Consume","public static long Consume"),
                lazySource.Replace("=>(long)(value ?? Fallbacks.Adjust(-7));","{ value=null; return (long)(value ?? Fallbacks.Adjust(-7)); }"),
                lazySource.Replace("int? value)=>(long)(value ?? Fallbacks.Adjust(-7))","int? value)=>(long)(value ?? Fallbacks.Adjust(value.Value))")}) {
                    ManagedSourceBodyWorldResultV1 denied =Compile(lazyRequest with{Documents=[new("/src/Constant.cs",badSource.Replace("[HybridCpuKernelNoHeap]",""))]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Lazy public/mutable/receiver fault gate");
            }
            foreach(string badHelper in new[]{helper.Replace("unchecked(fallback+1)","checked(fallback+1)"),
                helper.Replace("unchecked(fallback+1)","10/fallback"),helper.Replace("unchecked(fallback+1)","Adjust(fallback)")}) {
                    ManagedSourceBodyWorldResultV1 denied =Compile(lazyRequest with{ReferenceImages=[..core,Library(badHelper)]});
                if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Lazy helper effect/fault/recursive gate");
            }
            Console.WriteLine("PASS private nullable lazy coalescing selected external helper -> NoHeap/retained Int64/single-module/host/repeat/maps/image; roles/budgets/public/mutable/fault/recursive gates");
            string artifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"lazy-default-artifacts");
            Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts,"Primary.cs"),lazySource);
            File.WriteAllBytes(Path.Combine(artifacts,"FallbackLibrary.dll"),definition);
            File.WriteAllBytes(Path.Combine(artifacts,"Primary.dll"),lazy.Source.Pe!);
            File.WriteAllBytes(Path.Combine(artifacts,"Primary.pdb"),lazy.Source.Pdb!);
            File.WriteAllBytes(Path.Combine(artifacts,"Primary.hcexe"),lazyImage.RestrictedImage.PackageBytes);
            File.WriteAllText(Path.Combine(artifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(lazy.SourceMaps));
            using(StringWriter output =new StringWriter())using(StringWriter error =new StringWriter()) {
                string cliImage=Path.Combine(artifacts,"Cli.hcexe");
                int exit=ManagedSourceCommandLineV1.Run(["compile-source-image","--source",Path.Combine(artifacts,"Primary.cs"),
                    "--metadata-reference",typeof(object).Assembly.Location,"--reference",Path.Combine(directory,"System.Runtime.dll"),
                    "--reference",Path.Combine(directory,"netstandard.dll"),"--reference",typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location,
                    "--body-reference",Path.Combine(artifacts,"FallbackLibrary.dll"),"--assembly-name","ExternalConstantFallback","--type","Fixture","--method","Read","--out",cliImage],output,error);
                if(exit!=0 || !lazyImage.RestrictedImage.PackageBytes.SequenceEqual(File.ReadAllBytes(cliImage)))throw new Exception("Lazy CLI/API image equality: "+error);
            }
            Console.WriteLine("PASS lazy coalescing CLI/API image equality and original calls2/3, local/CFG/stack/metadata budgets");
            foreach((string? expression, Func<int, int>? oracle, string? variant) in new (string,Func<int,int>,string)[]{
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"dynamic-helper"),
                ("unchecked(fallback+1)",f=>unchecked(f+1),"inline-add"),
                ("(fallback ^ -7) & 255",f=>(f ^ -7)&255,"bitmask"),
                ("(fallback==0?-7:unchecked(fallback+1))",f=>f==0?-7:unchecked(f+1),"conditional"),
                ("(fallback==0?-7:Fallbacks.Adjust(fallback))",f=>f==0?-7:unchecked(f+1),"conditional-helper"),
                ("(fallback>0 && fallback<10 ? fallback : -7)",f=>f>0&&f<10?f:-7,"short-circuit"),
                ("(fallback>=0 ? Fallbacks.Adjust(fallback) : Fallbacks.Adjust(0))",f=>f>=0?unchecked(f+1):1,"two-helper-arms"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"explicit-presence"),
                ("(fallback==0?-7:Fallbacks.Adjust(fallback))",f=>f==0?-7:unchecked(f+1),"explicit-presence-conditional"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"guarded-value"),
                ("(fallback==0?-7:Fallbacks.Adjust(fallback))",f=>f==0?-7:unchecked(f+1),"guarded-value-conditional"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return"),
                ("(fallback==0?-7:Fallbacks.Adjust(fallback))",f=>f==0?-7:unchecked(f+1),"early-return-conditional"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first"),
                ("(fallback==0?-7:Fallbacks.Adjust(fallback))",f=>f==0?-7:unchecked(f+1),"present-first-conditional"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-add"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-mask"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-add"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-mask"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"guarded-value-add"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"guarded-value-mask"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-repeat"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-repeat"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"guarded-value-repeat"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-local"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-local"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f>0?unchecked(f+1)+1:f+1),"early-return-both-local"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop5"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop0"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-call3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-getter0"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-getter5"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-call-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-if-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-if-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-if-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-call-if-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-phi-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-phi-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-break-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-break-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-break-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-call-break-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-index-break-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-index-break-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-call-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-index-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-index-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-call-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-index-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-index-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-high-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-index-high-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-wide-normal-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-index-wide-normal-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-i8local-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-i8local-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-i8local-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"present-first-loop-call-i8local-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-index-i8local-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-index-i8local-wide-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-i8local-high-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-index-i8local-wide-normal-return-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-i8local-phi-getter3"),
                ("Fallbacks.Adjust(fallback)",f=>unchecked(f+1),"early-return-loop-call-i8local-phi-getter3")}) {
                if(wideHelperOnly && variant!="early-return-loop-i8local-wide-return-getter3")continue;
                string text=lazySource.Replace("Consume(int? value)","Consume(int? value,int fallback)")
                    .Replace("Fallbacks.Adjust(-7)",expression).Replace("Consume(value)","Consume(value,fallback)").Replace("Read(long input)","Read(long input,int fallback)");
                bool presentFirst=variant.StartsWith("present-first",StringComparison.Ordinal);
                bool earlyReturn=presentFirst || variant.StartsWith("early-return",StringComparison.Ordinal);
                bool guardedValue=earlyReturn || variant.StartsWith("guarded-value",StringComparison.Ordinal);
                bool explicitPresence=guardedValue || variant.StartsWith("explicit-presence",StringComparison.Ordinal);
                string region=guardedValue?"value.HasValue ? value.Value : ":explicitPresence?"value.HasValue ? value.GetValueOrDefault() : ":"value ?? ";
                if(explicitPresence)text=text.Replace("value ?? ",region);
                string earlyBody=presentFirst?"{ if(value.HasValue) return value.Value; return "+expression+"; }":
                    "{ if(!value.HasValue) return "+expression+"; return value.Value; }";
                if(earlyReturn)text=text.Replace("=>(long)("+region+expression+");",earlyBody);
                bool repeatedPayload=variant.EndsWith("-repeat",StringComparison.Ordinal);
                bool payloadExpression=repeatedPayload || variant is "present-first-add" or "present-first-mask" or "early-return-add" or "early-return-mask" or "guarded-value-add" or "guarded-value-mask";
                string payloadText=repeatedPayload?"unchecked(value.Value+value.Value)":variant.EndsWith("-mask",StringComparison.Ordinal)?"(value.Value ^ fallback) & 255":"unchecked(value.Value+fallback)";
                if(payloadExpression) { text=text.Replace("return value.Value;","return "+payloadText+";"); earlyBody=earlyBody.Replace("return value.Value;","return "+payloadText+";"); }
                if(payloadExpression && !earlyReturn) { text=text.Replace("value.HasValue ? value.Value : ","value.HasValue ? "+payloadText+" : "); region="value.HasValue ? "+payloadText+" : "; }
                bool payloadLoop=variant.Contains("-loop",StringComparison.Ordinal);
                bool loopCall=variant.Contains("-loop-call",StringComparison.Ordinal);
                bool loopGetter=variant.Contains("-getter",StringComparison.Ordinal);
                bool loopIf=variant.Contains("-if-",StringComparison.Ordinal), loopPhi=variant.Contains("-phi-",StringComparison.Ordinal);
                bool loopBreak=variant.Contains("-break-",StringComparison.Ordinal), indexBreak=variant.Contains("-index-",StringComparison.Ordinal);
                bool loopReturn=variant.Contains("-return-getter",StringComparison.Ordinal);
                bool highReturn=variant.Contains("-high-",StringComparison.Ordinal);
                bool wideReturn=highReturn || variant.Contains("-wide-",StringComparison.Ordinal), normalWide=variant.Contains("-normal-",StringComparison.Ordinal);
                bool i8Local=variant.Contains("-i8local-",StringComparison.Ordinal);
                int trips=payloadLoop?variant[^1]-'0':0;
                bool payloadLocal=payloadLoop || variant.EndsWith("-local",StringComparison.Ordinal);
                string localRegion=payloadLoop?"int payload=value.Value; for(int i=0;i<"+trips+";i++) payload=unchecked(payload+fallback); return unchecked(payload+payload);":
                    "int payload=value.Value; if(fallback>0) payload=unchecked(payload+fallback); return unchecked(payload+payload);";
                if(loopCall)localRegion=localRegion.Replace("payload=unchecked(payload+fallback);","payload=Fallbacks.Adjust(payload);");
                if(loopGetter)localRegion=loopCall?localRegion.Replace("Fallbacks.Adjust(payload)","Fallbacks.Adjust(unchecked(payload+value.Value))"):
                    localRegion.Replace("unchecked(payload+fallback)","unchecked(payload+value.Value)");
                string loopAssignment=loopCall?"payload=Fallbacks.Adjust(unchecked(payload+value.Value));":"payload=unchecked(payload+value.Value);";
                if(loopIf)localRegion=localRegion.Replace(loopAssignment,"if(fallback>0) "+loopAssignment);
                if(loopPhi)localRegion=localRegion.Replace(loopAssignment,"payload=fallback>0?"+(loopCall?"Fallbacks.Adjust(unchecked(payload+value.Value))":"unchecked(payload+value.Value)")+":unchecked(payload^fallback);");
                if(loopBreak)localRegion=localRegion.Replace(loopAssignment,"{ if("+(indexBreak?"i>=fallback":"fallback>0")+") break; "+loopAssignment+" }");
                if(loopReturn)localRegion=localRegion.Replace(loopAssignment,"{ if("+(indexBreak?"i>=fallback":"fallback>0")+") return payload; "+loopAssignment+" }");
                if(wideReturn)localRegion=localRegion.Replace("return payload;",highReturn?"return ((long)payload<<40)^fallback;":"return (long)payload+fallback;");
                if(normalWide)localRegion=localRegion.Replace("return unchecked(payload+payload);","return (long)payload+fallback;");
                if(i8Local)localRegion=localRegion.Replace("int payload=value.Value;","long payload=value.Value;")
                    .Replace("Fallbacks.Adjust(unchecked(payload+value.Value))","unchecked(payload+Fallbacks.Adjust(value.Value))");
                if(payloadLocal) { text=text.Replace("return value.Value;",localRegion);earlyBody=earlyBody.Replace("return value.Value;",localRegion);
                    if(presentFirst) { text=text.Replace("if(value.HasValue) "+localRegion,"if(value.HasValue) { "+localRegion+" }");earlyBody=earlyBody.Replace("if(value.HasValue) "+localRegion,"if(value.HasValue) { "+localRegion+" }"); }
                    if(variant=="early-return-both-local") {
                        string absent="{ int empty=fallback; if(empty>0) empty=unchecked(empty+1); return Fallbacks.Adjust(empty); }";
                        text=text.Replace("return "+expression+";",absent);earlyBody=earlyBody.Replace("return "+expression+";",absent);
                    }
                }
                    CompanionSourceRequestV1 dynamicRequest =lazyRequest with{Documents=[new("/src/Constant.cs",text)]};
                    ManagedSourceBodyWorldResultV1 actual =Compile(dynamicRequest); ScalarControlFlowV2LinkedProgramV1 actualImage =Link(actual); ManagedSourceBodyWorldResultV1 repeated =Compile(dynamicRequest);
                string singleText=text.Replace("public static class Fixture {","public static class Fixture { private static int Adjust(int value)=>unchecked(value+1);")
                    .Replace("Fallbacks.Adjust(","Adjust(")+wide;
                    ManagedSourceBodyWorldResultV1 singleDynamic =Compile(dynamicRequest with{ReferenceImages=core,Documents=[new("/src/Constant.cs",singleText)]},[],[0]);
                if(new[]{"Read","Consume"}.Any(name=>Code(actualImage,name)!=Code(Link(singleDynamic),name)) ||
                    !actual.Source.Pe!.SequenceEqual(repeated.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(repeated.Source.Pdb!) ||
                    actual.SourceMaps.Count!=2 || actual.SourceMaps.Any(row=>repeated.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                    !actualImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(repeated).RestrictedImage!.PackageBytes))throw new Exception("Dynamic lazy expression single-module/repeat/maps/image");
                using(PEReader pe =new PEReader(new MemoryStream(actual.Source.Pe!))) {
                        MetadataReader md =pe.GetMetadataReader(); MethodDefinition method =md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                    if(explicitPresence && !payloadLocal && !pe.GetMethodBody(method.RelativeVirtualAddress).LocalSignature.IsNil)throw new Exception("Explicit presence emitted unexpected local");
                    if(payloadLocal && variant!="early-return-both-local") {
                            MethodBodyBlock body =pe.GetMethodBody(method.RelativeVirtualAddress);
                            BlobHandle signatureBlob =md.GetStandaloneSignature(body.LocalSignature).Signature;
                        if(!md.GetBlobBytes(signatureBlob).AsSpan().SequenceEqual(payloadLoop?new byte[]{7,2,i8Local?(byte)10:(byte)8,8}:new byte[]{7,1,8}))throw new Exception("Actual primitive payload local signature");
                        byte[] il=body.GetILBytes()!;
                        int payload=presentFirst?9:9+unchecked((sbyte)il[8]);
                        int store=payload+(i8Local?8:7);
                        if(il[store]!=0x0a || i8Local && il[payload+7]!=0x6a)throw new Exception("Actual payload local store/widening");
                        int rva=method.RelativeVirtualAddress;
                            SectionHeader section =pe.PEHeaders.SectionHeaders.Single(s=>rva>=s.VirtualAddress && rva<s.VirtualAddress+s.VirtualSize);
                        int start=section.PointerToRawData+rva-section.VirtualAddress;
                        int header=(BinaryPrimitives.ReadUInt16LittleEndian(actual.Source.Pe!.AsSpan(start,2))>>12)*4;
                        int blob=pe.PEHeaders.MetadataStartOffset+md.GetHeapMetadataOffset(HeapIndex.Blob)+MetadataTokens.GetHeapOffset(signatureBlob)+1;
                            RestrictedCilImporterV1 rawImporter =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2);
                        ManagedCallGraphCompilationV1 Raw(byte[] bytes)=>rawImporter.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,bytes,"payload-local-negative",[new("Fixture","Read")],[],
                            DependencyModules:[new(definition,"selected-helper")],MetadataOnlyModules:[new(core[0],"core")]));
                        if(Raw(actual.Source.Pe!).Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Payload local raw baseline");
                        foreach(int mutation in new[]{0,1,2,3,4,5}) {
                            byte[] changed=actual.Source.Pe!.ToArray();
                            if(mutation==0)changed[start+header+store]=0x26;
                            if(mutation==1)changed[start+header+store]=payloadLoop?(byte)0x0c:(byte)0x0b;
                            if(mutation==2)changed[blob+2]=i8Local?(byte)0x08:(byte)0x0a;
                            if(mutation==3)changed[blob+2]=0x10;
                            if(mutation==4)changed[start+header+7]=presentFirst?(byte)0x2d:(byte)0x2c;
                            if(mutation==5)changed[start+header+payload+1]=1;
                                ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                            if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Payload local initialization/index/width/byref/guard/receiver controls");
                        }
                        if(payloadLoop && !loopCall && !loopGetter) {
                            int latch=payload+22;
                            if(il[latch]!=0x32 || il[payload+8]!=0x16 || il[payload+10]!=0x2b)throw new Exception("Actual counted loop IL shape");
                            foreach(int mutation in new[]{0,1,2,3,4,5,6,7}) {
                                byte[] changed=actual.Source.Pe!.ToArray();
                                if(mutation==0)changed[start+header+latch]=0x37;
                                if(mutation==1)changed[start+header+latch-1]=0x03;
                                if(mutation==2)changed[start+header+payload+8]=0x15;
                                if(mutation==3)changed[start+header+latch-5]=0x16;
                                if(mutation==4)changed[start+header+latch-4]=0x59;
                                if(mutation==5)changed[start+header+latch-7]=0x0b;
                                if(mutation==6)changed[start+header+payload+11]=0;
                                if(mutation==7)changed[blob+3]=0x0a;
                                    ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                                if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Counted loop bound/step/store/entry/width controls");
                            }
                        }
                        if(loopGetter && !loopIf && !loopPhi && !loopBreak && !loopReturn) {
                            int loan=payload+13;
                            int latch=payload+(loopCall?33:28);
                            if(il[loan]!=0x0f || il[loan+1]!=0 || il[loan+2]!=0x28 || il[latch]!=0x32)throw new Exception("Actual loop getter loan/latch IL");
                            foreach(int mutation in new[]{0,1,2,3,4,5,6}) {
                                byte[] changed=actual.Source.Pe!.ToArray();
                                if(mutation==0)changed[start+header+loan+1]=1;
                                if(mutation==2)changed[start+header+latch+1]=unchecked((byte)(sbyte)(loan+2-latch-2));
                                if(mutation==3)changed[start+header+loan]=0x12;
                                if(mutation==4)changed[start+header+latch]=0x37;
                                if(mutation==5)changed[start+header+payload+8]=0x15;
                                if(mutation==6)changed[start+header+latch-5]=0x16;
                                // Mutation1 selects HasValue instead of the exact I4 payload getter.
                                if(mutation==1)il.AsSpan(2,5).CopyTo(changed.AsSpan(start+header+loan+2,5));
                                    ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                                if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Counted getter receiver/member/interior/escape/latch/seed/step controls");
                            }
                        }
                        if(loopIf || loopPhi || loopBreak || loopReturn) {
                            // Actual body branch must not skip or enter the middle
                            // of the induction update, re-enter the body, or enter
                            // the receiver call interior. Locate exact emitted pairs.
                            int branch=payload+(i8Local?15:14);
                            int latch=Array.FindLastIndex(il,b=>b==0x32);
                            int loan=Enumerable.Range(payload+12,il.Length-payload-18).First(i=>il[i]==0x0f && il[i+1]==0 && il[i+2]==0x28);
                            if(il[branch] is not (0x30 or 0x31 or 0x2f or 0x32) || il[loan+2]!=0x28 || latch<=branch)throw new Exception("Actual counted conditional IL");
                            foreach(int target in new[]{latch-4,latch-2,branch,loan+2,latch+3}) {
                                byte[] changed=actual.Source.Pe!.ToArray();changed[start+header+branch+1]=unchecked((byte)(sbyte)(target-branch-2));
                                    ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                                if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Counted conditional induction/interior/exit/backedge controls");
                            }
                        }
                        if(loopReturn && !wideReturn) {
                            int conversion=payload+17;
                            if(il[conversion]!=0x6a || il[conversion+1]!=0x2a)throw new Exception("Actual early return widening");
                            foreach(int mutation in new[]{0,1,2,3}) {
                                byte[] changed=actual.Source.Pe!.ToArray();
                                if(mutation==0)changed[start+header+conversion]=0x69;
                                if(mutation==1)changed[start+header+conversion-1]=0x26;
                                if(mutation==2)changed[start+header+payload+15]=unchecked((byte)(sbyte)(conversion+1-(payload+14)-2));
                                if(mutation==3)changed[start+header+conversion]=0x21;
                                    ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                                if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Early return width/stack/interior/malformed controls");
                            }
                        }
                        if(wideReturn && !i8Local) {
                            int conversion=payload+17, ret=payload+(highReturn?24:21), otherConversion=ret-2;
                            if(il[conversion]!=0x6a || il[otherConversion]!=0x6a || il[ret]!=0x2a)throw new Exception("Actual I8 return expression IL");
                            foreach(int mutation in new[]{0,1,2,3}) {
                                byte[] changed=actual.Source.Pe!.ToArray();
                                if(mutation==0)changed[start+header+conversion]=0x69;
                                if(mutation==1)changed[start+header+otherConversion]=0x69;
                                if(mutation==2)changed[start+header+payload+15]=unchecked((byte)(sbyte)(ret-(payload+14)-2));
                                if(mutation==3)changed[start+header+ret-1]=0x5b;
                                    ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                                if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("I8 return width/interior/fault controls");
                            }
                        }
                        if(i8Local) {
                            foreach(int mutation in new[]{0,1,2,3}) {
                                byte[] changed=actual.Source.Pe!.ToArray();
                                if(mutation==0)changed[start+header+payload+7]=0x69;
                                if(mutation==1)changed[blob+3]=0x0a;
                                if(mutation==2)changed[blob+2]=0x0b;
                                if(mutation==3)changed[start+header+store]=0x00;
                                    ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                                if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("I8 local widening/induction-width/unsigned/initialization controls");
                            }
                        }
                        if(Raw(actual.Source.Pe!).Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Payload local raw reuse");
                    }
                    Console.WriteLine("DYNAMIC-LAZY-CIL "+expression+" "+Convert.ToHexString(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!));
                    if(repeatedPayload || variant is "conditional" or "explicit-presence" or "guarded-value" or "early-return" or "present-first" or "present-first-add" or "early-return-add" or "guarded-value-add") {
                        byte[] il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                        int presenceBranch=explicitPresence?7:9;
                        int fallbackStart=presenceBranch+2+(presentFirst?unchecked((sbyte)il[presenceBranch+1]):0);
                        if(il[presenceBranch]!=(presentFirst?0x2c:0x2d))throw new Exception("Conditional fixture presence branch");
                        int payload=presentFirst?presenceBranch+2:fallbackStart+unchecked((sbyte)il[presenceBranch+1]);
                        int branch=earlyReturn?presenceBranch:Enumerable.Range(0,il.Length-1).First(i=>il[i]==0x2b && i+2+unchecked((sbyte)il[i+1])==il.Length-2);
                        int rva=method.RelativeVirtualAddress;
                            SectionHeader section =pe.PEHeaders.SectionHeaders.Single(s=>rva>=s.VirtualAddress && rva<s.VirtualAddress+s.VirtualSize);
                        int start=section.PointerToRawData+rva-section.VirtualAddress;
                        int header=(actual.Source.Pe![start]&3)==2?1:(BinaryPrimitives.ReadUInt16LittleEndian(actual.Source.Pe.AsSpan(start,2))>>12)*4;
                            RestrictedCilImporterV1 rawImporter =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2);
                        ManagedCallGraphCompilationV1 Raw(byte[] bytes)=>rawImporter.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,bytes,"conditional-negative",[new("Fixture","Read")],[],
                            DependencyModules:[new(definition,"selected-helper")],MetadataOnlyModules:[new(core[0],"core")]));
                        if(Raw(actual.Source.Pe).Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Conditional raw baseline");
                            int[] mutations =payloadExpression && !earlyReturn?new[]{0,1,2,3,4,5,6,10}:presentFirst || payloadExpression?new[]{0,1,2,3,4,5,6,7,8,9,10}:earlyReturn?new[]{0,1,2,3,4,5,6,7,8,9}:guardedValue?new[]{0,1,2,3,4,5,6}:explicitPresence?new[]{0,1,2,3,4}:new[]{0,1,2};
                        foreach(int mutation in mutations.Concat(repeatedPayload?new[]{11,12,13}:[])) {
                            byte[] changed=actual.Source.Pe.ToArray();
                            if(mutation==0) {
                                if(earlyReturn)changed[start+header+(presentFirst?fallbackStart:payload)-1]=0x00;
                                else changed[start+header+branch+1]=unchecked((byte)(sbyte)(payload-branch-2));
                            }
                            if(mutation==1)changed[start+header+branch+1]=0xfe;
                            if(mutation==2)changed[start+header+fallbackStart]=0x02;
                            if(mutation==3)changed[start+header+payload+1]=1;
                            if(mutation==4)il.AsSpan(3,4).CopyTo(changed.AsSpan(start+header+payload+3,4));
                            if(mutation==5)changed[start+header+presenceBranch]=presentFirst?(byte)0x2d:(byte)0x2c;
                            if(mutation==6)changed[start+header+1]=1;
                            if(mutation==7)changed[start+header+(presentFirst?il.Length:payload)-2]=0x6e;
                            if(mutation is 8 or 9) {
                                int call=fallbackStart+1;
                                changed[start+header+call]=0x2b;
                                changed[start+header+call+1]=unchecked((byte)(sbyte)((mutation==8?payload:0)-call-2));
                                changed.AsSpan(start+header+call+2,3).Clear();
                            }
                            if(mutation==10)changed[start+header+(presentFirst?fallbackStart:il.Length)-2]=0x6e;
                            if(mutation==11)changed[start+header+payload+8]=1;
                            if(mutation==12)il.AsSpan(3,4).CopyTo(changed.AsSpan(start+header+payload+10,4));
                            if(mutation==13)changed[start+header+presenceBranch+1]=unchecked((byte)(sbyte)(payload+9-presenceBranch-2));
                                ManagedCallGraphCompilationV1 denied =Raw(changed); ManagedCallGraphCompilationV1 again =Raw(changed);
                            if(denied.Status==RestrictedCilImportStatusV1.Success || denied.Status!=again.Status || !denied.Diagnostics.Select(d=>d.Code).SequenceEqual(again.Diagnostics.Select(d=>d.Code)))throw new Exception("Conditional payload-entry/backedge/nominal-load gate");
                        }
                        if(Raw(actual.Source.Pe).Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Conditional raw rejection/reuse");
                    }
                }
                if(payloadLocal || repeatedPayload || variant is "explicit-presence" or "guarded-value" or "early-return" or "present-first" or "present-first-add" or "early-return-add") {
                    string mutableText=earlyReturn?text.Replace(earlyBody,earlyBody.Replace("{ if(","{ value=null; if(")):
                        text.Replace("=>(long)("+region+expression+");","{value=null;return (long)("+region+expression+");}");
                    foreach(ManagedSourceBodyWorldResultV1? denied in new[]{Compile(dynamicRequest,[]),Compile(dynamicRequest,[],[0,4]),
                        Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",text.Replace("private static long Consume","public static long Consume"))]}),
                        Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",mutableText.Replace("[HybridCpuKernelNoHeap]",""))]})})
                        if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Explicit presence roles/public/mutable gate");
                    foreach(string badHelper in new[]{helper.Replace("unchecked(fallback+1)","checked(fallback+1)"),
                        helper.Replace("unchecked(fallback+1)","10/fallback"),helper.Replace("unchecked(fallback+1)","Adjust(fallback)")}) {
                            ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{ReferenceImages=[..core,Library(badHelper)]});
                        if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Explicit presence selected helper purity");
                    }
                    if(guardedValue)foreach(string badText in earlyReturn?new[]{text.Replace(presentFirst?"if(value.HasValue)":"if(!value.HasValue)",presentFirst?"if(!value.HasValue)":"if(value.HasValue)"),
                        text.Replace(variant=="early-return-both-local"?"return Fallbacks.Adjust(empty);":"return "+expression+";","return value.Value;"),text.Replace(earlyBody,"{ return value.Value + "+expression+"; }")}:
                        new[]{"!value.HasValue ? value.Value : ","value.HasValue ? Fallbacks.Adjust(fallback) : value.Value + ","value.Value + "}.Select(badRegion=>text.Replace(region,badRegion))) {
                            ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",badText.Replace("[HybridCpuKernelNoHeap]",""))]});
                        if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Guarded Value inverse/absent/unguarded gate");
                    }
                }
                    AssemblyLoadContext hostContext =new System.Runtime.Loader.AssemblyLoadContext("dynamic-lazy",isCollectible:true);
                try {
                    hostContext.LoadFromStream(new MemoryStream(definition));
                        Type host =hostContext.LoadFromStream(new MemoryStream(actual.Source.Pe!)).GetType("Fixture")!;
                        MethodInfo consume =host.GetMethod("Consume",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                    foreach(int? value in new int?[]{null,int.MinValue,-1,0,1,int.MaxValue})foreach(int fallback in new[]{int.MinValue,-7,-1,0,1,9,10,int.MaxValue}) {
                        long expected=repeatedPayload && value.HasValue?unchecked(value.Value+value.Value):payloadExpression && value.HasValue ? variant.EndsWith("-mask",StringComparison.Ordinal)?(value.Value^fallback)&255:unchecked(value.Value+fallback):value??oracle(fallback);
                        if(payloadLocal && value.HasValue){int payload=value.Value; if(fallback>0)payload=unchecked(payload+fallback);expected=unchecked(payload+payload);}
                        if(payloadLoop && value.HasValue)expected=unchecked(2*unchecked(value.Value+trips*fallback));
                        if(loopCall && value.HasValue)expected=unchecked(2*unchecked(value.Value+trips));
                        if(loopGetter && value.HasValue)expected=unchecked(2*unchecked(value.Value+trips*unchecked(value.Value+(loopCall?1:0))));
                        if((loopIf || loopPhi) && value.HasValue && fallback<=0)expected=unchecked(2*(loopPhi && (trips&1)!=0?value.Value^fallback:value.Value));
                        if(loopBreak && value.HasValue){int iterations=indexBreak?Math.Clamp(fallback,0,trips):fallback>0?0:trips;expected=unchecked(2*unchecked(value.Value+iterations*unchecked(value.Value+(loopCall?1:0))));}
                        if(loopReturn && value.HasValue){int iterations=indexBreak?Math.Clamp(fallback,0,trips):fallback>0?0:trips;int result=unchecked(value.Value+iterations*unchecked(value.Value+(loopCall?1:0)));expected=indexBreak?fallback<trips?(long)result:unchecked(result+result):fallback>0?(long)result:unchecked(result+result);}
                        if(wideReturn && value.HasValue){int iterations=indexBreak?Math.Clamp(fallback,0,trips):fallback>0?0:trips;int result=unchecked(value.Value+iterations*unchecked(value.Value+(loopCall?1:0)));if(indexBreak?fallback<trips:fallback>0)expected=highReturn?unchecked(((long)result<<40)^fallback):(long)result+fallback;else if(normalWide)expected=(long)result+fallback;}
                        if(i8Local && value.HasValue){int iterations=loopReturn?(indexBreak?Math.Clamp(fallback,0,trips):fallback>0?0:trips):trips;long result=(long)value.Value+(long)iterations*(loopCall?unchecked(value.Value+1):value.Value);if(loopPhi && fallback<=0)result=(trips&1)!=0?(long)value.Value^fallback:value.Value;bool early=loopReturn && (indexBreak?fallback<trips:fallback>0);expected=early?(highReturn?unchecked((result<<40)^fallback):result+fallback):normalWide?result+fallback:unchecked(result+result);}
                        if(!Equals(consume.Invoke(null,[value,fallback]),expected))throw new Exception("Dynamic lazy independent host boundary");
                    }
                }finally {hostContext.Unload();}
                if(payloadLocal && !loopCall && !loopGetter)foreach(string bad in new[]{localRegion.Replace("unchecked(payload+fallback)","checked(payload+fallback)"),
                    localRegion.Replace("unchecked(payload+fallback)","payload/fallback"),
                    localRegion.Replace("int payload=value.Value;","nint payload=(nint)value.Value;").Replace("return unchecked(payload+payload);","return unchecked((long)(payload+payload));"),
                    localRegion.Replace(payloadLoop?"for(int i=0;i<"+trips+";i++) payload=unchecked(payload+fallback);":"if(fallback>0) payload=unchecked(payload+fallback);","while(fallback>0) payload=unchecked(payload+fallback);")}) {
                        ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",text.Replace(localRegion,bad).Replace("[HybridCpuKernelNoHeap]",""))]});
                    if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Payload local fault/width/loop gate");
                }
                if(payloadLoop && !loopCall && !loopGetter)foreach(string bad in new[]{localRegion.Replace("i<"+trips,"i<fallback"),localRegion.Replace("i++","i--"),
                    localRegion.Replace("i++","i+=2"),localRegion.Replace("payload=unchecked(payload+fallback);","payload=unchecked(payload+i++);")}) {
                        ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",text.Replace(localRegion,bad).Replace("[HybridCpuKernelNoHeap]",""))]});
                    if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Counted loop source counter/bound gate");
                }
                if(loopGetter)foreach(string bad in new[]{localRegion.Replace("i<"+trips,"i<fallback"),localRegion.Replace("i++","i--"),
                    localRegion.Replace(i8Local&&loopCall?"Fallbacks.Adjust(value.Value)":"payload+value.Value",i8Local&&loopCall?"Fallbacks.Adjust((value=fallback).Value)":"payload+(value=fallback).Value"),
                    localRegion.Replace(i8Local&&loopCall?"unchecked(payload+Fallbacks.Adjust(value.Value))":"payload+value.Value",i8Local&&loopCall?"checked(payload+Fallbacks.Adjust(value.Value))":"checked(payload+value.Value)")}) {
                        ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",text.Replace(localRegion,bad).Replace("[HybridCpuKernelNoHeap]",""))]});
                    if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Counted getter bound/counter/mutable/fault controls");
                }
                if(payloadExpression)foreach(string bad in new[]{"checked(value.Value+fallback)","value.Value/fallback","unchecked(value.Value+(fallback=1))"}) {
                        ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",text.Replace(payloadText,bad).Replace("[HybridCpuKernelNoHeap]",""))]});
                    if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Present payload fault/mutation gate");
                }
                foreach(string bad in new[]{"checked(fallback+1)","10/fallback","Fallbacks.Adjust(fallback=1)","Fallbacks.Adjust(value.Value)"}) {
                    string badText=variant=="early-return-both-local"?text.Replace("return Fallbacks.Adjust(empty);","return "+bad+";"):
                        earlyReturn?text.Replace("return "+expression+";","return "+bad+";"):text.Replace(region+expression,region+bad);
                        ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",badText.Replace("[HybridCpuKernelNoHeap]",""))]});
                    if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Dynamic lazy fault/mutable/receiver gate: "+bad);
                }
                if(loopCall && !loopGetter)foreach(string bad in new[]{localRegion.Replace("i<"+trips,"i<fallback"),localRegion.Replace("i++","i--"),
                    localRegion.Replace("Fallbacks.Adjust(payload)","Fallbacks.Adjust(i=payload)")}) {
                        ManagedSourceBodyWorldResultV1 denied =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",text.Replace(localRegion,bad).Replace("[HybridCpuKernelNoHeap]",""))]});
                    if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Counted loop-call bound/counter gate");
                }
                int expectedCalls=2+(repeatedPayload?1:0)+(loopCall?1:0)+(loopGetter?1:0)+System.Text.RegularExpressions.Regex.Matches(expression,"Fallbacks\\.Adjust\\(").Count;
                foreach(int limit in new[]{expectedCalls-1,expectedCalls})if(Compile(dynamicRequest,calls:limit).Compilation?.Status!=(limit==expectedCalls?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("Dynamic lazy original call budget "+limit);
                foreach(ManagedSourceBodyWorldResultV1? denied in new[]{Compile(dynamicRequest,stack:1),Compile(dynamicRequest,blocks:1),Compile(dynamicRequest,resolution:1),Compile(dynamicRequest,maximumLocals:0)})
                    if(denied.Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("Dynamic lazy original stack/local/CFG/metadata budgets");
                string dynamicArtifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"lazy-expression-artifacts",variant);
                Directory.CreateDirectory(dynamicArtifacts);
                File.WriteAllText(Path.Combine(dynamicArtifacts,"Primary.cs"),text);
                File.WriteAllBytes(Path.Combine(dynamicArtifacts,"FallbackLibrary.dll"),definition);
                File.WriteAllBytes(Path.Combine(dynamicArtifacts,"Primary.dll"),actual.Source.Pe!);
                File.WriteAllBytes(Path.Combine(dynamicArtifacts,"Primary.pdb"),actual.Source.Pdb!);
                File.WriteAllBytes(Path.Combine(dynamicArtifacts,"Primary.hcexe"),actualImage.RestrictedImage!.PackageBytes);
                File.WriteAllText(Path.Combine(dynamicArtifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(actual.SourceMaps));
                using(StringWriter output =new StringWriter())using(StringWriter error =new StringWriter()) {
                    string cliImage=Path.Combine(dynamicArtifacts,"Cli.hcexe");
                    int exit=ManagedSourceCommandLineV1.Run(["compile-source-image","--source",Path.Combine(dynamicArtifacts,"Primary.cs"),
                        "--metadata-reference",typeof(object).Assembly.Location,"--reference",Path.Combine(directory,"System.Runtime.dll"),
                        "--reference",Path.Combine(directory,"netstandard.dll"),"--reference",typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location,
                        "--body-reference",Path.Combine(dynamicArtifacts,"FallbackLibrary.dll"),"--assembly-name","ExternalConstantFallback","--type","Fixture","--method","Read","--out",cliImage],output,error);
                    if(exit!=0 || !actualImage.RestrictedImage.PackageBytes.SequenceEqual(File.ReadAllBytes(cliImage)))throw new Exception("Lazy expression CLI/API image equality: "+error);
                }
                Console.WriteLine("PASS private nullable lazy dynamic expression "+expression+" -> NoHeap/source/image/host/single-module/repeat/maps, original calls and fault/mutable/receiver controls");
                if(variant=="early-return-repeat") {
                        ManagedSourceBodyWorldResultV1 localCopy =Compile(dynamicRequest with{Documents=[new("/src/Constant.cs",text.Replace("return "+payloadText+";","int payload=value.Value; return unchecked(payload+payload);"))]});
                    if(localCopy.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(localCopy).RestrictedImage is null)throw new Exception("Roslyn dup local-copy consumer");
                }
                if(variant=="early-return-loop-i8local-wide-return-getter3")foreach(int helperKind in new[]{0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24}.Where(kind=>onlyHelperKind is null || kind==onlyHelperKind)) {
                    bool scaledWide=helperKind==24, affineWide=helperKind==23, dependentWide=helperKind>=22, nestedWide=helperKind==21, sequentialWide=helperKind==20, i64Wide=helperKind==19, u64Wide=helperKind==18, uintWide=helperKind==17, inclusiveWide=helperKind==16, descendingWide=helperKind==15, seedWide=helperKind==14, strideWide=helperKind==13, boundWide=helperKind==12, loopWide=helperKind>=11, binaryWide=helperKind==1 || helperKind is >=5 and <=10, genericWide=helperKind is >=2 and <=10, ownerWide=helperKind is 3 or 4 or 6 or 7 or 9 or 10, methodWide=helperKind is 2 or 4 or 5 or 7 or 8 or 10, selectorWide=helperKind is >=8 and <=10;
                    string wideDeclaration=helperKind switch {24=>"public static long Advance(long payload){for(long index=0;index<3;index++)for(long next=0;next<2*index+1;next++)payload=unchecked(payload+1L);return payload;}",23=>"public static long Advance(long payload){for(long index=0;index<3;index++)for(long next=0;next<index+1;next++)payload=unchecked(payload+1L);return payload;}",22=>"public static long Advance(long payload){for(long index=0;index<3;index++)for(long next=0;next<index;next++)payload=unchecked(payload+1L);return payload;}",21=>"public static long Advance(long payload){for(long index=0;index<2;index++)for(long next=0;next<2;next++)payload=unchecked(payload+1L);return payload;}",20=>"public static long Advance(long payload){for(long index=0;index<2;index++)payload=unchecked(payload+1L);for(long next=0;next<2;next++)payload=unchecked(payload+1L);return payload;}",19=>"public static long Advance(long payload){for(long index=0;index<3;index++)payload=unchecked(payload+1L);return payload;}",18=>"public static long Advance(long payload){for(ulong index=0;index<3;index++)payload=unchecked(payload+1L);return payload;}",17=>"public static long Advance(long payload){for(uint index=0;index<3;index++)payload=unchecked(payload+1L);return payload;}",16=>"public static long Advance(long payload){for(int index=5;index>=1;index-=2)payload=unchecked(payload+1L);return payload;}",15=>"public static long Advance(long payload){for(int index=5;index>1;index-=2)payload=unchecked(payload+1L);return payload;}",14=>"public static long Advance(long payload){for(int index=1;index<5;index+=2)payload=unchecked(payload+1L);return payload;}",13=>"public static long Advance(long payload){for(int index=0;index<4;index+=2)payload=unchecked(payload+1L);return payload;}",12=>"public static long Advance(long payload,int limit){for(int index=0;index<limit;index++)payload=unchecked(payload+1L);return payload;}",11=>"public static long Advance(long payload){for(int index=0;index<2;index++)payload=unchecked(payload+1L);return payload;}",2=>"public static T Advance<T>(T payload)=>payload;",3=>"public static T Advance(T payload)=>payload;",4=>"public static U Advance<U>(U payload)=>payload;",5=>"public static T Advance<T>(T previous,T next)=>next;",6=>"public static T Advance(T previous,T next)=>next;",7=>"public static U Advance<U>(U previous,U next)=>next;",8=>"public static T Advance<T>(int index,T previous,T next)=>index==0?previous:next;",9=>"public static T Advance(int index,T previous,T next)=>index==0?previous:next;",10=>"public static U Advance<U>(int index,U previous,U next)=>index==0?previous:next;",1=>"public static long Advance(long payload,long delta)=>unchecked(payload+delta);",_=>"public static long Advance(long payload)=>unchecked(payload+1L);"};
                    byte[] WideLibrary(string declaration,string name="FallbackLibrary")=>Library(ownerWide?helper+" public static class FallbackOwner<T> { "+declaration+" }":helper.Replace(" }"," "+declaration+" }"),name);
                    byte[] widerLibrary=WideLibrary(wideDeclaration);
                    string wideCall=boundWide?"Fallbacks.Advance(payload,2)":genericWide?(ownerWide?"FallbackOwner<long>":"Fallbacks")+".Advance"+(methodWide?"<long>":"")+(selectorWide?"(i,payload,unchecked(payload+fallback))":binaryWide?"(payload,unchecked(payload+fallback))":"(unchecked(payload+fallback))"):binaryWide?"Fallbacks.Advance(payload,(long)fallback)":"Fallbacks.Advance(payload)";
                    string helperText=text.Replace("payload=unchecked(payload+value.Value);","payload="+wideCall+";");
                    CompanionSourceRequestV1 helperRequest=dynamicRequest with{ReferenceImages=[..core,widerLibrary],Documents=[new("/src/Constant.cs",helperText)]};
                    ManagedSourceBodyWorldResultV1 candidate=Compile(helperRequest), again=Compile(helperRequest);
                    ScalarControlFlowV2LinkedProgramV1 helperImage=Link(candidate);
                    Console.WriteLine("LOOP-I8-HELPER emitted="+(candidate.Source.Pe is not null)+" status="+candidate.Compilation?.Status+" "+string.Join(';',candidate.Compilation?.Diagnostics??[]));
                    if(candidate.Source.Pe is not null)using(PEReader pe =new PEReader(new MemoryStream(candidate.Source.Pe))) {
                            MetadataReader md =pe.GetMetadataReader(); MethodDefinition method =md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                        MethodBodyBlock helperCallerBody=pe.GetMethodBody(method.RelativeVirtualAddress);
                        MemberReference helperMember=md.GetMemberReference(md.MemberReferences.Single(h=>md.GetString(md.GetMemberReference(h).Name)=="Advance"));
                        Console.WriteLine("PAYLOAD-EXPRESSION-CIL "+Convert.ToHexString(helperCallerBody.GetILBytes()!)+" member="+Convert.ToHexString(md.GetBlobBytes(helperMember.Signature))+" locals="+Convert.ToHexString(md.GetBlobBytes(md.GetStandaloneSignature(helperCallerBody.LocalSignature).Signature))+" stack="+helperCallerBody.MaxStack);
                        if(methodWide) {
                            if(md.GetTableRowCount(TableIndex.MethodSpec)!=1 || !md.GetBlobBytes(md.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(1)).Signature).AsSpan().SequenceEqual(new byte[]{0x0a,1,0x0a}))throw new Exception("Exact Int64 MethodSpec");
                            Console.WriteLine("I8-HELPER-METHODSPEC 0A010A");
                        }
                        if(genericWide) {
                            ManagedMethodIdentityV1 identity=candidate.Compilation!.Methods.Single(m=>m.Identity.MethodName=="Advance").Identity;
                            if((methodWide?identity.GenericMethodArguments?.Single()!=new RestrictedCilGenericArgumentV1("System.Int64",RestrictedCilTypeV1.Int64):identity.GenericMethodArguments?.Count!=0) ||
                                (ownerWide?identity.GenericTypeArguments?.Single()!=new RestrictedCilGenericArgumentV1("System.Int64",RestrictedCilTypeV1.Int64):identity.GenericTypeArguments?.Count!=0))throw new Exception("Exact Int64 VAR/MVAR identity");
                            if(ownerWide) {
                                if(helperMember.Parent.Kind!=HandleKind.TypeSpecification)throw new Exception("I8 owner TypeSpec parent");
                                byte[] ownerSignature=md.GetBlobBytes(md.GetTypeSpecification((TypeSpecificationHandle)helperMember.Parent).Signature);
                                if(ownerSignature[0]!=0x15 || ownerSignature[^1]!=0x0a)throw new Exception("I8 owner TypeSpec width");
                                Console.WriteLine("I8-HELPER-TYPESPEC "+Convert.ToHexString(ownerSignature));
                            }
                        }
                    }
                    using(PEReader helperPe=new(new MemoryStream(widerLibrary))) {
                        MetadataReader md=helperPe.GetMetadataReader();
                        MethodDefinition advance=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Advance"));
                        Console.WriteLine("SELECTED-I8-HELPER-CIL signature="+Convert.ToHexString(md.GetBlobBytes(advance.Signature))+" body="+Convert.ToHexString(helperPe.GetMethodBody(advance.RelativeVirtualAddress).GetILBytes()!));
                    }
                    string singleHelper=helperText.Replace("public static class Fixture {","public static class Fixture { "+wideDeclaration.Replace("public static","private static")+" private static int Adjust(int value)=>unchecked(value+1);")
                        .Replace("Fallbacks.Advance(","Advance(").Replace("Fallbacks.Advance<","Advance<").Replace("Fallbacks.Adjust(","Adjust(")+wide;
                    if(ownerWide)singleHelper=helperText+wide+helper+" public static class FallbackOwner<T> { "+wideDeclaration+" }";
                    ManagedSourceBodyWorldResultV1 singleWideHelper=Compile(helperRequest with{ReferenceImages=core,Documents=[new("/src/Constant.cs",singleHelper)]},[],[0]);
                    if(new[]{"Read","Consume","Advance"}.Any(name=>Code(helperImage,name)!=Code(Link(singleWideHelper),name)) ||
                        !candidate.Source.Pe!.SequenceEqual(again.Source.Pe!) || !candidate.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                        candidate.SourceMaps.Count!=2 || candidate.SourceMaps.Any(row=>again.SourceMaps[row.Key].Digest!=row.Value.Digest) ||
                        !helperImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(again).RestrictedImage!.PackageBytes))throw new Exception("I8 selected helper single-module/repeat/maps/image");
                    AssemblyLoadContext wideContext=new("wide-helper",isCollectible:true);
                    try {
                        Assembly hostLibrary=wideContext.LoadFromStream(new MemoryStream(widerLibrary));
                        Type library=hostLibrary.GetType(ownerWide?"FallbackOwner`1":"Fallbacks")!;
                        if(ownerWide)library=library.MakeGenericType(typeof(long));
                        MethodInfo advance=library.GetMethod("Advance")!;
                        if(methodWide)advance=advance.MakeGenericMethod(typeof(long));
                        foreach(long input in new[]{long.MinValue,long.MaxValue,-1L,0L,1L,1L<<40})foreach(long delta in binaryWide?new[]{long.MinValue,-1L,0L,1L,long.MaxValue}:new[]{1L})foreach(int selector in selectorWide?new[]{int.MinValue,0,1,int.MaxValue}:new[]{0})
                            if(!Equals(advance.Invoke(null,selectorWide?[selector,input,delta]:binaryWide?[input,delta]:boundWide?[input,2]:[input]),selectorWide?(selector==0?input:delta):genericWide?(binaryWide?delta:input):unchecked(input+(scaledWide?9:affineWide?6:dependentWide?3L:nestedWide||sequentialWide?4L:i64Wide||u64Wide||uintWide||inclusiveWide?3L:loopWide?2L:delta))))throw new Exception("I8 helper overflow/high bits/selector");
                        MethodInfo consume=wideContext.LoadFromStream(new MemoryStream(candidate.Source.Pe!)).GetType("Fixture")!.GetMethod("Consume",BindingFlags.NonPublic|BindingFlags.Static)!;
                        foreach(int? value in new int?[]{null,int.MinValue,-1,0,1,int.MaxValue})foreach(int fallback in new[]{int.MinValue,-7,0,1,int.MaxValue}) {
                            long expected=value.HasValue?(fallback>0?(long)value.Value+fallback:2*((long)value.Value+(selectorWide?2L:3L)*(binaryWide||genericWide?fallback:scaledWide?9:affineWide?6:dependentWide?3:nestedWide||sequentialWide?4:i64Wide||u64Wide||uintWide||inclusiveWide?3:loopWide?2:1))):unchecked(fallback+1);
                            if(!Equals(consume.Invoke(null,[value,fallback]),expected))throw new Exception("I8 helper lazy loop/early-return semantics");
                        }
                    } finally {wideContext.Unload();}
                    if(!genericWide)foreach(string body in new[]{"public static long Advance(long payload)=>checked(payload+1L);",
                        "public static long Advance(long payload)=>10L/payload;", "public static long Advance(long payload)=>Advance(payload);",
                        "public static long Advance(long payload){try{return payload+1L;}finally{System.GC.Collect();}}",
                        "public static long Advance(long payload){System.GC.Collect();return payload+1L;}",
                        "public static int Advance(int payload)=>payload+1;", "public static long Advance(ref long payload)=>++payload;"}) {
                        string badBody=boundWide?body.Replace("long payload)","long payload,int limit)").Replace("=>Advance(payload);","=>Advance(payload,limit);"):binaryWide?body.Replace("long payload)","long payload,long delta)").Replace("=>Advance(payload);","=>Advance(payload,delta);"):body;
                        byte[] badLibrary=Library(helper.Replace(" }"," "+badBody+" }"));
                        ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,badLibrary]});
                        if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("I8 helper effect/EH/recursive/width/byref gate: "+body);
                    }
                    foreach(var roles in new[]{(Bodies:Array.Empty<int>(),Metadata:new[]{0}),(Bodies:Array.Empty<int>(),Metadata:new[]{0,4})})
                        if(Compile(helperRequest,roles.Bodies,roles.Metadata).Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("I8 helper explicit body role");
                    string genericResult=selectorWide?"index==0?previous:next":binaryWide?"next":"payload";
                    string changedWideDeclaration=genericWide?wideDeclaration.Replace("=>"+genericResult+";","=>Echo("+genericResult+");")+" public static Q Echo<Q>(Q payload)=>payload;":wideDeclaration.Replace(binaryWide?"payload+delta":"payload+1L",binaryWide?"payload+delta+1L":"payload+2L");
                    byte[] changedWideLibrary=WideLibrary(changedWideDeclaration);
                    ManagedSourceBodyWorldResultV1 changedWide=Compile(helperRequest with{ReferenceImages=[..core,changedWideLibrary]});
                    if(Code(Link(changedWide),"Advance")==Code(helperImage,"Advance") || changedWide.Source.SourceInputDigest==candidate.Source.SourceInputDigest ||
                        changedWide.Compilation!.Graph!.GraphDigest==candidate.Compilation!.Graph!.GraphDigest || !changedWide.Source.Pe!.SequenceEqual(candidate.Source.Pe!))
                        throw new Exception("I8 helper exact body identity/output invalidation");
                    ManagedCallGraphCompilationV1 wrongWideIdentity=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule,candidate.Source.Pe!,"wrong-wide-identity",[new("Fixture","Read")],[],
                        DependencyModules:[new(WideLibrary(wideDeclaration,"WrongWideIdentity"),"wrong-body")],MetadataOnlyModules:[new(core[0],"core")]));
                    if(wrongWideIdentity.Status==RestrictedCilImportStatusV1.Success)throw new Exception("I8 helper wrong nominal assembly identity");
                    if(!binaryWide && !genericWide && !boundWide)foreach((string declaration,string call) in new[]{
                        ("public static long Advance(ref long payload)=>++payload;","Fallbacks.Advance(ref payload)"),
                        ("public static ulong Advance(ulong payload)=>unchecked(payload+1UL);","(long)Fallbacks.Advance((ulong)payload)"),
                        ("public static long Advance(long payload,long delta,long extra,long last)=>unchecked(payload+delta+extra+last);","Fallbacks.Advance(payload,1L,2L,3L)")}) {
                        ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,Library(helper.Replace(" }"," "+declaration+" }"))],
                            Documents=[new("/src/Constant.cs",helperText.Replace("Fallbacks.Advance(payload)",call).Replace("[HybridCpuKernelNoHeap]",""))]});
                        if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("I8 helper emitted byref/unsigned/binary signature gate");
                    }
                    foreach(int limit in new[]{3,4})if(Compile(helperRequest,calls:limit).Compilation?.Status!=(limit==4?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("I8 helper original call budget "+limit);
                    foreach(var denied in new[]{Compile(helperRequest,stack:1),Compile(helperRequest,blocks:1),Compile(helperRequest,resolution:1),Compile(helperRequest,maximumLocals:0)})
                        if(denied.Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)throw new Exception("I8 helper original stack/local/CFG/metadata budget");
                    if(helperKind==1) {
                        CompanionSourceRequestV1 mixedRequest=helperRequest with{ReferenceImages=[..core,Library(helper.Replace(" }"," public static long Advance(long payload,int delta)=>unchecked(payload+delta); }"))],
                            Documents=[new("/src/Constant.cs",helperText.Replace(wideCall,"Fallbacks.Advance(payload,fallback)"))]};
                        ManagedSourceBodyWorldResultV1 mixed=Compile(mixedRequest), mixedAgain=Compile(mixedRequest);
                        ScalarControlFlowV2LinkedProgramV1 mixedImage=Link(mixed);
                        if(!mixed.Source.Pe!.SequenceEqual(mixedAgain.Source.Pe!) || !mixed.Source.Pdb!.SequenceEqual(mixedAgain.Source.Pdb!) ||
                            !mixedImage.RestrictedImage!.PackageBytes.SequenceEqual(Link(mixedAgain).RestrictedImage!.PackageBytes) || mixed.SourceMaps.Any(row=>mixedAgain.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Mixed I8/I4 helper repeat/maps/image");
                        Console.WriteLine("PASS selected I8/I4 -> I8 pure helper NoHeap/source/image/repeat/maps");
                    }
                    if(genericWide) {
                        foreach(string bad in new[]{wideDeclaration.Replace("=>"+genericResult+";","{System.GC.Collect();return "+genericResult+";}"),
                            wideDeclaration.Replace("=>"+genericResult+";",selectorWide?"=>Advance(index,previous,next);":binaryWide?"=>Advance(previous,next);":"=>Advance(payload);"),
                            wideDeclaration.Replace("=>"+genericResult+";","{try{return "+genericResult+";}finally{System.GC.Collect();}}")}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(bad)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Generic I8 effect/recursive/EH/constraint proof gate");
                        }
                        string prefix=(ownerWide?"FallbackOwner<ulong>":"Fallbacks")+".Advance"+(methodWide?"<ulong>":"");
                        string refCall=(ownerWide?"FallbackOwner<long>":"Fallbacks")+".Advance"+(methodWide?"<long>":"")+(selectorWide?"(i,ref payload,(long)fallback)":binaryWide?"(ref payload,(long)fallback)":"(ref payload)");
                        foreach(string badCall in new[]{"(long)"+prefix+(selectorWide?"(i,(ulong)payload,(ulong)fallback)":binaryWide?"((ulong)payload,(ulong)fallback)":"((ulong)payload)"),refCall}) {
                            string declaration=badCall.Contains("ref payload",StringComparison.Ordinal)?wideDeclaration.Replace("(T payload)","(ref T payload)").Replace("(U payload)","(ref U payload)").Replace("T previous,T next)","ref T previous,T next)").Replace("U previous,U next)","ref U previous,U next)"):wideDeclaration;
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(declaration)],Documents=[new("/src/Constant.cs",helperText.Replace(wideCall,badCall).Replace("[HybridCpuKernelNoHeap]",""))]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Generic I8 emitted unsigned/byref gate");
                        }
                        if(methodWide) {
                            string constrained=wideDeclaration.Replace("=>"+genericResult+";",(helperKind is 4 or 7 or 10?"where U:struct =>":"where T:struct =>")+genericResult+";");
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(constrained)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("I8 method constraints remain closed");
                        }
                        if(ownerWide) {
                            byte[] constrained=Library(helper+" public static class FallbackOwner<T> where T:struct { "+wideDeclaration+" }");
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,constrained]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("I8 owner constraints remain closed");
                        }
                    }
                    string helperArtifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,helperKind switch {24=>"i8-scaled-helper-artifacts",23=>"i8-affine-helper-artifacts",22=>"i8-dependent-helper-artifacts",21=>"i8-nested-helper-artifacts",20=>"i8-sequential-helper-artifacts",19=>"i8-i64-counter-artifacts",18=>"i8-u64-helper-artifacts",17=>"i8-uint-helper-artifacts",16=>"i8-inclusive-helper-artifacts",15=>"i8-descending-helper-artifacts",14=>"i8-seed-helper-artifacts",13=>"i8-stride-helper-artifacts",12=>"i8-bound-helper-artifacts",11=>"i8-loop-helper-artifacts",2=>"i8-method-helper-artifacts",3=>"i8-owner-helper-artifacts",4=>"i8-combined-helper-artifacts",5=>"i8-method-binary-helper-artifacts",6=>"i8-owner-binary-helper-artifacts",7=>"i8-combined-binary-helper-artifacts",8=>"i8-method-selector-artifacts",9=>"i8-owner-selector-artifacts",10=>"i8-combined-selector-artifacts",1=>"i8-binary-helper-artifacts",_=>"i8-helper-artifacts"});
                    Directory.CreateDirectory(helperArtifacts);
                    File.WriteAllText(Path.Combine(helperArtifacts,"Primary.cs"),helperText);
                    File.WriteAllBytes(Path.Combine(helperArtifacts,"Primary.dll"),candidate.Source.Pe!);
                    File.WriteAllBytes(Path.Combine(helperArtifacts,"Primary.pdb"),candidate.Source.Pdb!);
                    File.WriteAllBytes(Path.Combine(helperArtifacts,"FallbackLibrary.dll"),widerLibrary);
                    File.WriteAllBytes(Path.Combine(helperArtifacts,"Primary.hcexe"),helperImage.RestrictedImage.PackageBytes);
                    File.WriteAllText(Path.Combine(helperArtifacts,"maps.json"),System.Text.Json.JsonSerializer.Serialize(candidate.SourceMaps));
                    using(StringWriter output=new())using(StringWriter error=new()) {
                        string cliImage=Path.Combine(helperArtifacts,"Cli.hcexe");
                        int exit=ManagedSourceCommandLineV1.Run(["compile-source-image","--source",Path.Combine(helperArtifacts,"Primary.cs"),
                            "--metadata-reference",typeof(object).Assembly.Location,"--reference",Path.Combine(directory,"System.Runtime.dll"),
                            "--reference",Path.Combine(directory,"netstandard.dll"),"--reference",typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location,
                            "--body-reference",Path.Combine(helperArtifacts,"FallbackLibrary.dll"),"--assembly-name","ExternalConstantFallback","--type","Fixture","--method","Read","--out",cliImage],output,error);
                        if(exit!=0 || !helperImage.RestrictedImage.PackageBytes.SequenceEqual(File.ReadAllBytes(cliImage)))throw new Exception("I8 helper CLI/API image equality: "+error);
                    }
                    Console.WriteLine("PASS selected "+(loopWide?"bounded-loop":genericWide?"exact context<Int64> kind="+helperKind:binaryWide?"binary":"unary")+" I8 helper NoHeap/source/image/exact bodies/high bits/overflow/lazy loop/single-module/repeat/maps/budgets/negatives");
                    if(scaledWide) {
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(int i=0;i<3;i++)for(int j=0;j<2*i+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i*3-1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<-2*i+5;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=3;i>0;i--)for(long j=8;j>i*2-1;j--)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<2*i;j++)for(long k=0;k<j*2+1;k++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Scaled ancestor bound/typed hierarchy: "+body);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(long i=long.MaxValue/2+1;i<long.MaxValue/2+2;i++)for(long j=0;j<2*i-long.MaxValue;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=long.MinValue;i<long.MinValue+1;i++)for(long j=0;j<-1L*i+long.MinValue;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(int i=int.MaxValue/2+1;i<int.MaxValue/2+2;i++)for(int j=0;j<2*i-int.MaxValue;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i*payload+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i*i+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong i=0;i<3;i++)for(ulong j=0;j<2*i+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<2*i+1;j++){i=0;payload++;}return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Scaled intermediate overflow/invariance/nominal gate: "+body);
                        }
                    }
                    if(affineWide) {
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(int i=0;i<3;i++)for(int j=0;j<i+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=3;i>0;i--)for(long j=4;j>i-1;j--)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i-1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i+1;j++)for(long k=0;k<j+1;k++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Affine ancestor bound/typed hierarchy: "+body);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(long i=long.MaxValue-1;i<long.MaxValue;i++)for(long j=0;j<i+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=long.MinValue+1;i>long.MinValue;i--)for(long j=0;j>i-1;j--)payload++;return payload;}",
                            "public static long Advance(long payload){for(int i=int.MaxValue-1;i<int.MaxValue;i++)for(int j=0;j<i+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i+payload;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i*i+1;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i+1;j++){i=0;payload++;}return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Affine bound no-wrap/invariance/shape gate: "+body);
                        }
                    }
                    if(nestedWide) {
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(int i=0;i<3;i++)for(int j=0;j<i;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=3;i>0;i--)for(long j=4;j>i;j--)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong i=0;i<3;i++)for(ulong j=0;j<i;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<3;i++)for(long j=0;j<i;j++)for(long k=0;k<j;k++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Ancestor counter domain/typed nested hierarchy");
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(long i=0;i<2;i++)for(long j=0;j<2;j++){i=0;payload++;}return payload;}",
                            "public static long Advance(long payload){long j=0;for(long i=0;i<2;i++)for(;j<2;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<2;i++)for(long j=0;j<payload;j++)payload++;return payload;}",
                            "public static long Advance(long payload){long limit=2;for(long i=0;i<2;i++)for(long j=0;j<limit;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=long.MaxValue-2;i<long.MaxValue;i++)for(long j=long.MaxValue-2;j<=i;j+=2)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<2;i++)while(true)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<2;i++)for(long j=0;j<2;j++)payload=10L/payload;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nested reset/outer-invariance/dependent-bound/effect gate: "+body);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(long i=0;i<2;i++)for(long j=0;j<2;j++)for(long k=0;k<2;k++)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint i=2147483648U;i<2147483650U;i++)for(ulong j=9223372036854775808UL;j<9223372036854775810UL;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<2;i++){for(long j=0;j<2;j++)payload++;for(long j=0;j<2;j++)payload++;}return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Nested forest/mixed-width hierarchy");
                        }
                    }
                    if(sequentialWide) {
                        // Local bounds must belong to an enclosing proven counter.
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(long i=0;i<2;i++)for(long j=0;j<payload;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<2;i++)payload++;while(true)payload++;}",
                            "public static long Advance(long payload){again:for(long i=0;i<2;i++)payload++;if(payload==0)goto again;for(long j=0;j<2;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<2;i++)payload++;for(long j=0;j<2;j++){j=0;payload++;}return payload;}",
                            "public static long Advance(long payload){for(long i=0;i<2;i++)payload++;for(long j=0;j<2;j++)payload=10L/payload;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Sequential cycle/nesting/effect gate: "+body);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){long index;for(index=0;index<2;index++)payload++;for(index=0;index<2;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint i=2147483648U;i<2147483650U;i++)payload++;for(ulong j=9223372036854775808UL;j<9223372036854775810UL;j++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long i=2;i<0;i++)payload++;payload=unchecked(payload*3L);for(long j=0;j<2;j++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Sequential counter reuse/mixed-width/zero-trip");
                        }
                    }
                    if(i64Wide) {
                        CompanionSourceRequestV1 boundRequest=helperRequest with{ReferenceImages=[..core,WideLibrary("public static long Advance(long payload,long limit){for(long index=0;index<limit;index++)payload++;return payload;}")],Documents=[new("/src/Constant.cs",helperText.Replace(wideCall,"Fallbacks.Advance(payload,3L)"))]};
                        ManagedSourceBodyWorldResultV1 bound=Compile(boundRequest), boundAgain=Compile(boundRequest);
                        if(bound.Compilation?.Status!=RestrictedCilImportStatusV1.Success || !bound.Source.Pe!.SequenceEqual(boundAgain.Source.Pe!) || !bound.Source.Pdb!.SequenceEqual(boundAgain.Source.Pdb!) || !Link(bound).RestrictedImage!.PackageBytes.SequenceEqual(Link(boundAgain).RestrictedImage!.PackageBytes) || bound.SourceMaps.Any(row=>boundAgain.SourceMaps[row.Key].Digest!=row.Value.Digest))throw new Exception("Signed I8 immutable bound source/image/repeat/maps");
                        foreach(string body in new[]{"public static long Advance(long payload,long limit){limit++;for(long index=0;index<limit;index++)payload++;return payload;}","public static long Advance(long payload,long limit){for(long index=0;index<=limit;index++)payload++;return payload;}"})
                            if(Compile(boundRequest with{ReferenceImages=[..core,WideLibrary(body)]}).Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Signed I8 mutable/inclusive parameter bound");
                        Console.WriteLine("PASS signed I8 immutable bound NoHeap/source/image/repeat/maps and mutable/inclusive bound negatives");
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(long index=0;index<=long.MaxValue;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long index=long.MaxValue-1;index<long.MaxValue;index+=2)payload++;return payload;}",
                            "public static long Advance(long payload){for(long index=long.MinValue;index>=long.MinValue;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(long index=payload;index<3;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long index=0;(ulong)index<3;index++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Int64 overflow/typed counter gate: "+body);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(long index=long.MinValue;index<long.MinValue+3;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long index=long.MaxValue;index>long.MaxValue-3;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(long index=-3;index<=-1;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(long index=long.MaxValue;index<1;index++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Int64 signed constant boundaries");
                        }
                    }
                    if(u64Wide) {
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(ulong index=0;index<=ulong.MaxValue;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong index=ulong.MaxValue-1;index<ulong.MaxValue;index+=2)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong index=0;index>=0;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong index=(ulong)payload;index<3;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong index=0;(long)index<3;index++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("UInt64 no-wrap/typed counter gate: "+body);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(ulong index=9223372036854775808UL;index<9223372036854775811UL;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong index=ulong.MaxValue;index>ulong.MaxValue-3;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong index=3;index>=1;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(ulong index=ulong.MaxValue;index<1;index++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("UInt64 high-bit constant boundaries");
                        }
                    }
                    if(uintWide) {
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(uint index=0;index<=uint.MaxValue;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint index=uint.MaxValue-1;index<uint.MaxValue;index+=2)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint index=0;index>=0;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint index=(uint)payload;index<3;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint index=0;(int)index<3;index++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Unsigned no-wrap/typed counter gate: "+body);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(uint index=2147483648U;index<2147483651U;index++)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint index=uint.MaxValue;index>uint.MaxValue-3;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint index=3;index>=1;index--)payload++;return payload;}",
                            "public static long Advance(long payload){for(uint index=uint.MaxValue;index<1;index++)payload++;return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Unsigned constant high-bit boundaries");
                        }
                    }
                    if(inclusiveWide) {
                        foreach(string bad in new[]{wideDeclaration.Replace("index>=1","index>=int.MinValue"),
                            wideDeclaration.Replace("index>=1","index<=1"),wideDeclaration.Replace("index-=2","index+=2"),
                            wideDeclaration.Replace("index=5","index=(int)payload")}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(bad)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Inclusive underflow/direction/seed gate: "+bad);
                        }
                        foreach(string body in new[]{
                            "public static long Advance(long payload){for(int index=1;index<=5;index+=2)payload=unchecked(payload+1L);return payload;}",
                            "public static long Advance(long payload){for(int index=-1;index>=-5;index-=2)payload=unchecked(payload+1L);return payload;}",
                            "public static long Advance(long payload){for(int index=0;index<=-1;index++)payload=unchecked(payload+1L);return payload;}",
                            "public static long Advance(long payload){for(int index=int.MaxValue;index<=int.MaxValue;index++)payload=unchecked(payload+1L);return payload;}",
                            "public static long Advance(long payload){for(int index=int.MinValue;index>=int.MinValue;index--)payload=unchecked(payload+1L);return payload;}"}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(body)]});
                            bool overflow=body.Contains("int.MaxValue",StringComparison.Ordinal)||body.Contains("int.MinValue",StringComparison.Ordinal);
                            if(overflow ? bounded.Compilation?.Status==RestrictedCilImportStatusV1.Success : bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Inclusive constant proof: "+body);
                        }
                    }
                    if(descendingWide) {
                        foreach(string bad in new[]{wideDeclaration.Replace("index>1","index>int.MinValue"),
                            wideDeclaration.Replace("index-=2","index+=2"),wideDeclaration.Replace("index-=2","index-=0"),
                            wideDeclaration.Replace("index>1","(uint)index>=1U"),wideDeclaration.Replace("index=5","index=(int)payload")}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(bad)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Descending direction/underflow/compare gate: "+bad);
                        }
                        foreach((int seed,int bound) in new[]{(-1,-5),(0,1),(int.MinValue,int.MinValue),(int.MaxValue,int.MaxValue-4),(5,0)}) {
                            string declaration=wideDeclaration.Replace("index=5","index="+seed).Replace("index>1","index>"+bound);
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(declaration)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Descending constant boundaries "+seed);
                        }
                    }
                    if(seedWide) {
                        foreach(string bad in new[]{wideDeclaration.Replace("index=1","index=(int)payload"),
                            wideDeclaration.Replace("index<5","index<int.MaxValue").Replace("index+=2","index+=4"),
                            wideDeclaration.Replace("index+=2","index-=2"),wideDeclaration.Replace("index<5","(uint)index<=5U")}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(bad)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Seed/overflow/compare gate: "+bad);
                        }
                        foreach((int seed,int bound,int stride) in new[]{(-5,-1,2),(-3,2,2),(5,1,2),(int.MaxValue,int.MaxValue,2),(int.MinValue,int.MinValue+4,2)}) {
                            string declaration=wideDeclaration.Replace("index=1","index="+seed).Replace("index<5","index<"+bound).Replace("index+=2","index+="+stride);
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(declaration)]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Signed constant seed/bound "+seed);
                        }
                    }
                    if(strideWide) {
                        foreach(string bad in new[]{wideDeclaration.Replace("index+=2","index+=0"),wideDeclaration.Replace("index+=2","index-=2"),
                            wideDeclaration.Replace("index<4","index<int.MaxValue"),wideDeclaration.Replace("index<4","(uint)index<=4U"),
                            wideDeclaration.Replace("index=0","index=(int)payload"),wideDeclaration.Replace("index+=2","index+=(int)payload")}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(bad)]});
                            if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Constant stride/overflow gate: "+bad);
                        }
                        foreach(int bound in new[]{0,1,3,4,5,6}) {
                            ManagedSourceBodyWorldResultV1 bounded=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(wideDeclaration.Replace("index<4","index<"+bound))]});
                            if(bounded.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(bounded).RestrictedImage is null)throw new Exception("Stride rounded constant bound "+bound);
                        }
                    }
                    if(boundWide) {
                        AssemblyLoadContext boundedContext=new("bounded-helper",isCollectible:true);
                        try {
                            MethodInfo advance=boundedContext.LoadFromStream(new MemoryStream(widerLibrary)).GetType("Fallbacks")!.GetMethod("Advance")!;
                            foreach(long payload in new[]{long.MinValue,long.MaxValue,-1L,0L,1L,1L<<40})foreach(int limit in new[]{int.MinValue,-1,0,1,2,7})
                                if(!Equals(advance.Invoke(null,[payload,limit]),unchecked(payload+Math.Max(0,limit))))throw new Exception("Bound parameter host zero/positive-trip semantics");
                        } finally {boundedContext.Unload();}
                        foreach(string bad in new[]{wideDeclaration.Replace("index++","index+=2"),wideDeclaration.Replace("index<limit","index<=limit"),
                            wideDeclaration.Replace("{for(int","{limit=2;for(int"),wideDeclaration.Replace("{for(int","{limit++;for(int"),
                            wideDeclaration.Replace("payload=unchecked(payload+1L);","{limit--;payload=unchecked(payload+1L);}"),
                            wideDeclaration.Replace("int index=0","long index=0"),wideDeclaration.Replace("int index=0","int index=limit")}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(bad)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Immutable I4 bound/counter gate: "+bad);
                        }
                    }
                    if(loopWide && !boundWide && !strideWide && !seedWide && !descendingWide && !inclusiveWide && !uintWide && !u64Wide && !i64Wide && !sequentialWide && !nestedWide && !dependentWide) {
                        foreach(string bad in new[]{wideDeclaration.Replace("index=0","index=(int)payload"),wideDeclaration.Replace("index++","index+=0"),
                            wideDeclaration.Replace("index<2","index<payload"),wideDeclaration.Replace("index<2","(uint)index<=2U"),
                            wideDeclaration.Replace("payload+1L","payload/index"),wideDeclaration.Replace("payload=unchecked(payload+1L);","{index=0;payload=unchecked(payload+1L);}"),
                            wideDeclaration.Replace("for(int index=0;index<2;index++)","while(true)"),
                            wideDeclaration.Replace("payload=unchecked(payload+1L);","{for(int inner=0;inner<2;inner++){index=0;payload=unchecked(payload+1L);}}")}) {
                            ManagedSourceBodyWorldResultV1 denied=Compile(helperRequest with{ReferenceImages=[..core,WideLibrary(bad)]});
                            if(denied.Source.Pe is null || denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Exact bounded helper induction/effect gate: "+bad);
                        }
                        string nextText=helperText.Replace(wideCall,"Fallbacks.Advance(payload)");
                        byte[] nextLibrary=Library(helper.Replace(" }"," public static long Advance(long payload){for(long index=0;index<3;index++)for(long next=0;next<index*index+1;next++)payload=unchecked(payload+1L);return payload;} }"));
                        ManagedSourceBodyWorldResultV1 next=Compile(helperRequest with{ReferenceImages=[..core,nextLibrary],Documents=[new("/src/Constant.cs",nextText)]});
                        if(next.Source.Pe is null || next.Compilation?.Status!=RestrictedCilImportStatusV1.Unsupported)throw new Exception("Next bounded helper loop consumer gate changed");
                        string nextArtifacts=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("TEMP"))!,"next-helper-square-bound");
                        Directory.CreateDirectory(nextArtifacts);
                        File.WriteAllText(Path.Combine(nextArtifacts,"Primary.cs"),nextText);
                        File.WriteAllBytes(Path.Combine(nextArtifacts,"Primary.dll"),next.Source.Pe);
                        File.WriteAllBytes(Path.Combine(nextArtifacts,"Primary.pdb"),next.Source.Pdb!);
                        File.WriteAllBytes(Path.Combine(nextArtifacts,"FallbackLibrary.dll"),nextLibrary);
                        using PEReader nextPe=new(new MemoryStream(next.Source.Pe));
                        MetadataReader md=nextPe.GetMetadataReader();
                        MethodDefinition method=md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                        Console.WriteLine("NEXT PURE-HELPER-LOOP emitted=True status="+next.Compilation.Status+" "+string.Join(';',next.Compilation.Diagnostics)+" IL="+Convert.ToHexString(nextPe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!));
                    }
                }
            }
            }
        }
    }
    public static void RunParameter(bool presence = false, int? explicitDefault = null, bool dynamicDefault = false, string? computedDefault = null)
    {
        dynamicDefault |= computedDefault is not null;
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(directory,"System.Runtime.dll")), File.ReadAllBytes(Path.Combine(directory,"netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        foreach(string initializer in explicitDefault is not null && explicitDefault != -7 ? new[]{"input","null"} : new[]{"input","null","input==0?null:input","unchecked(input+1)"}) {
            string source="using HybridCPU.Compiler.Profiles; public static class Fixture { private static long Primitive(long bits)=>bits; private static long Consume(int? value)=>(long)value.GetValueOrDefault(); [HybridCpuKernelNoHeap] public static long Read(int input) { int? value="+initializer+"; int? copy=value; return Consume(copy); } }";
            if (presence) source=source.Replace("(long)value.GetValueOrDefault()", "value.HasValue?1L:0L");
            if(explicitDefault is not null) source=source.Replace("GetValueOrDefault()", "GetValueOrDefault("+explicitDefault.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+")");
            if(dynamicDefault) source=source.Replace("Primitive(long bits)","Primitive(long bits,int fallback)").Replace("Consume(int? value)","Consume(int? value,int fallback)").Replace("GetValueOrDefault()","GetValueOrDefault(fallback)").Replace("Read(int input)","Read(int input,int fallback)").Replace("Consume(copy)","Consume(copy,fallback)");
            if(computedDefault is not null)source=source.Replace("GetValueOrDefault(fallback)","GetValueOrDefault("+computedDefault+")");
            if(computedDefault=="Adjust(fallback)")source=source.Replace("public static class Fixture {","public static class Fixture { private static int Adjust(int fallback)=>unchecked(fallback+1);");
            string getter=dynamicDefault?"(long)value.GetValueOrDefault("+(computedDefault??"fallback")+")":presence?"value.HasValue?1L:0L":explicitDefault is null?"(long)value.GetValueOrDefault()":"(long)value.GetValueOrDefault("+explicitDefault.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+")";
            string parameters=dynamicDefault?"int? value,int fallback":"int? value";
            CompanionSourceRequestV1 request =new CompanionSourceRequestV1("NullableParameter",[new("/src/Parameter.cs",source)],references);
            ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 input,int calls=100,int? stack=null,int? blocks=null)=>ManagedSourceCompanionV1.CompileBodyWorld(input,
                new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2,budgets:RestrictedCilImportBudgetsV1.Production with{MaximumCalls=calls,MaximumEvaluationStack=stack??RestrictedCilImportBudgetsV1.Production.MaximumEvaluationStack,MaximumBasicBlocks=blocks??RestrictedCilImportBudgetsV1.Production.MaximumBasicBlocks}),[new("Fixture","Read")],null,[0]);
            ManagedSourceBodyWorldResultV1 actual =Compile(request); ManagedSourceBodyWorldResultV1 repeat =Compile(request);
            if((presence || explicitDefault is not null || dynamicDefault) && actual.Source.Pe is not null) {
                using PEReader emitted =new PEReader(new MemoryStream(actual.Source.Pe)); MetadataReader metadata =emitted.GetMetadataReader();
                MethodDefinition helper =metadata.GetMethodDefinition(metadata.MethodDefinitions.Single(h=>metadata.GetString(metadata.GetMethodDefinition(h).Name)=="Consume"));
                Console.WriteLine("NULLABLE-GETTER-PARAMETER-IL "+getter+" "+Convert.ToHexString(emitted.GetMethodBody(helper.RelativeVirtualAddress).GetILBytes()!));
            }
            if(actual.Source.Pe is null||actual.Compilation?.Status!=RestrictedCilImportStatusV1.Success)
                throw new Exception("Nullable parameter: "+string.Join(';',actual.Source.Diagnostics)+"; "+string.Join(';',actual.Compilation?.Diagnostics??[]));
            ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 value)=>new ScalarControlFlowV2ObjectLinkerV1().Link(value.Compilation!,value.Compilation!.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 image =Link(actual); ScalarControlFlowV2LinkedProgramV1 again =Link(repeat);
            ManagedSourceBodyWorldResultV1 scalar =Compile(request with{Documents=[new("/src/Parameter.cs",source.Replace("Consume("+parameters+")=>"+getter,"Consume("+(dynamicDefault?"long value,int fallback":"long value")+")=>"+(dynamicDefault?"(value & 1L)!=0?(long)(int)(value >>> 1):(long)("+(computedDefault??"fallback")+")":presence?"(value & 1L)!=0?1L:0L":explicitDefault is null?"(long)(int)(value >>> 1)":"(value & 1L)!=0?(long)(int)(value >>> 1):"+explicitDefault.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+"L"))
                .Replace("int? value="+initializer+"; int? copy=value;", "long value="+(initializer=="null"?"0L":initializer=="input==0?null:input"?"input==0?0L:(((long)input<<32)>>>31)|1L":"(((long)("+initializer+")<<32)>>>31)|1L")+"; long copy=value;"))]});
            // Scalar I8 mask constants add conv.i8 expansions absent from bool getter IL.
            // Presence equivalence is evaluated independently on serialized objects in ManagedPortSmoke.
            if(scalar.Compilation?.Status!=RestrictedCilImportStatusV1.Success || Link(scalar).Status!=ScalarControlFlowV2LinkStatusV1.Success || !presence && explicitDefault is null && !dynamicDefault && image.MethodObjects.Single(m=>m.MethodIdentity.Contains(".Consume",StringComparison.Ordinal)).CodeSha256!=
                Link(scalar).MethodObjects.Single(m=>m.MethodIdentity.Contains(".Consume",StringComparison.Ordinal)).CodeSha256) {
                Console.WriteLine("SCALAR-STATUS "+scalar.Compilation?.Status+" "+string.Join(';',scalar.Compilation?.Diagnostics??[]));
                foreach(ManagedCallGraphCompilationV1? compilation in new[]{actual.Compilation,scalar.Compilation})if(compilation?.Status==RestrictedCilImportStatusV1.Success)
                    foreach(IrInstruction instruction in compilation.Methods.Single(m=>m.Identity.MethodName=="Consume").Import.Program!.Instructions)
                        Console.WriteLine("PARAMETER-IR "+instruction.Opcode+" "+string.Join(',',instruction.Operands)+" "+string.Join(',',instruction.Annotation.Defs));
                throw new Exception("Nullable parameter independent scalar helper bytes");
            }
            if(image.Status!=ScalarControlFlowV2LinkStatusV1.Success||!image.RestrictedImage!.PackageBytes.SequenceEqual(again.RestrictedImage!.PackageBytes)||
                !actual.Source.Pe.SequenceEqual(repeat.Source.Pe!)||!actual.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!)||actual.SourceMaps.Any(p=>repeat.SourceMaps[p.Key].Digest!=p.Value.Digest))throw new Exception("Nullable parameter repeat/maps/image");
            MethodInfo host =System.Reflection.Assembly.Load(actual.Source.Pe).GetType("Fixture")!.GetMethod("Read")!;
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue}) foreach(int fallback in dynamicDefault?new[]{int.MinValue,-7,0,int.MaxValue}:new[]{0}) {
                long expected=initializer=="null"||initializer.Contains("input==0")&&input==0?0L:initializer.Contains("unchecked")?(long)unchecked(input+1):(long)input;
                if(presence)expected=initializer=="null"||initializer.Contains("input==0")&&input==0?0L:1L;
                if(explicitDefault is not null && (initializer=="null"||initializer.Contains("input==0")&&input==0)) expected=explicitDefault.Value;
                if(dynamicDefault && (initializer=="null"||initializer.Contains("input==0")&&input==0))expected=fallback;
                if(computedDefault is not null && (initializer=="null"||initializer.Contains("input==0")&&input==0))expected=computedDefault switch {
                    "unchecked(fallback+1)"=>unchecked(fallback+1),
                    "Adjust(fallback)"=>unchecked(fallback+1),
                    "fallback==0?-7:unchecked(fallback+1)"=>fallback==0?-7:unchecked(fallback+1),
                    "(fallback ^ -7) & 255"=>(fallback ^ -7)&255,
                    _=>throw new Exception("Missing independent computed default oracle") };
                if(!Equals(host.Invoke(null,dynamicDefault?[input,fallback]:[input]),expected))throw new Exception("Nullable parameter independent host boundary oracle");
            }
            if(initializer!="input")continue;
            foreach(string helper in new[]{"public static long Consume(int? value)=>(long)value.GetValueOrDefault();",
                "private static long Consume(int? value) { value=null; return (long)value.GetValueOrDefault(); }",
                "private static long Consume(int? value) { System.GC.KeepAlive(value); return value.GetValueOrDefault(); }",
                "private static long Consume(int? value) { try { return value.GetValueOrDefault(); } finally { System.GC.KeepAlive(value); } }",
                "private static long Consume(int? value) => value.Value;",
                "private static long Consume(int? value) => value.HasValue?7L:3L;",
                "private static long Consume(long? value)=>value.GetValueOrDefault();"}) {
                string deniedHelper=presence?helper.Replace("value.GetValueOrDefault()","(value.HasValue?1:0)"):explicitDefault is null?helper:helper.Replace("GetValueOrDefault()","GetValueOrDefault("+explicitDefault.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+")");
                if(dynamicDefault)deniedHelper=deniedHelper.Replace("Consume(int? value)","Consume(int? value,int fallback)").Replace("Consume(long? value)","Consume(long? value,int fallback)").Replace("GetValueOrDefault()","GetValueOrDefault(fallback)");
                ManagedSourceBodyWorldResultV1 denied =Compile(request with{Documents=[new("/src/Parameter.cs",source.Replace("private static long Consume("+parameters+")=>"+getter+";",deniedHelper).Replace("[HybridCpuKernelNoHeap]",""))]});
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nullable parameter signature/body gate: "+helper);
            }
            ManagedCallGraphCompilationV1 root =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,actual.Source.Pe,"nullable-root",[new("Fixture","Consume")],[],MetadataOnlyModules:[new(references[0],"core")]));
            if(dynamicDefault)foreach(string bad in new[]{source.Replace(getter,"(long)value.GetValueOrDefault(fallback=1)"),source.Replace("Consume(int? value,int fallback)","Consume(int? value,long fallback)").Replace(getter,"(long)value.GetValueOrDefault((int)("+(computedDefault??"fallback")+"))")}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(request with{Documents=[new("/src/Parameter.cs",bad.Replace("[HybridCpuKernelNoHeap]",""))]});
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nullable fallback mutation/width gate");
            }
            if(computedDefault is not null)foreach(string bad in new[]{"(long)value.GetValueOrDefault(10/fallback)","(long)value.GetValueOrDefault(checked(fallback+1))","(long)value.GetValueOrDefault(System.Math.Abs(fallback))"}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(request with{Documents=[new("/src/Parameter.cs",source.Replace(getter,bad).Replace("[HybridCpuKernelNoHeap]",""))]});
                if(denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nullable computed default fault/call proof gate");
            }
            if(computedDefault=="Adjust(fallback)")foreach(string badHelper in new[]{
                "private static int Adjust(int fallback) { System.GC.KeepAlive(fallback); return fallback; }",
                "private static int Adjust(int fallback) { try { return fallback; } finally { System.GC.KeepAlive(fallback); } }",
                "private static int Adjust(int fallback) { while(fallback>0)fallback--; return fallback; }",
                "private static int Adjust(int fallback)=>Adjust(fallback);",
                "private static int counter; private static int Adjust(int fallback)=>counter++;"}) {
                ManagedSourceBodyWorldResultV1 denied =Compile(request with{Documents=[new("/src/Parameter.cs",source.Replace("private static int Adjust(int fallback)=>unchecked(fallback+1);",badHelper).Replace("[HybridCpuKernelNoHeap]",""))]});
                if(denied.Source.Pe is null||denied.Compilation?.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nullable helper default effect/EH/loop/cycle/memory gate");
            }
            if(root.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Nullable private ingress became a root ABI");
            byte[] changed=actual.Source.Pe.ToArray();
            using(PEReader pe =new System.Reflection.PortableExecutable.PEReader(new MemoryStream(changed))) {
                MetadataReader md =pe.GetMetadataReader();
                MethodDefinitionHandle read =md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Read");
                int consume=System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Consume"));
                int primitive=System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Primitive"));
                int rva=md.GetMethodDefinition(read).RelativeVirtualAddress; byte[] il =pe.GetMethodBody(rva).GetILBytes()!;
                int call=Enumerable.Range(0,il.Length-4).Single(i=>il[i]==0x28&&System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(i+1,4))==consume);
                SectionHeader section =pe.PEHeaders.SectionHeaders.Single(s=>rva>=s.VirtualAddress&&rva<s.VirtualAddress+s.VirtualSize);
                int start=section.PointerToRawData+rva-section.VirtualAddress;
                int header=(changed[start]&3)==2?1:(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start,2))>>12)*4;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(start+header+call+1,4),primitive);
            }
            ManagedCallGraphCompilationV1 mismatched =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,changed,"nullable-primitive-negative",[new("Fixture","Read")],[],MetadataOnlyModules:[new(references[0],"core")]));
            if(mismatched.Status==RestrictedCilImportStatusV1.Success)throw new Exception("Packed nullable escaped into primitive I8 helper");
            foreach(int calls in new[]{0,1,2}) {
                ManagedSourceBodyWorldResultV1 budgeted =Compile(request,calls);
                if(budgeted.Compilation?.Status!=(calls==2?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("Nullable parameter original calls "+calls+": "+budgeted.Compilation?.Status+"; "+string.Join(';',budgeted.Compilation?.Diagnostics??[]));
            }
            int requiredStack=computedDefault is null||computedDefault=="Adjust(fallback)"?2:3;
            if(explicitDefault is not null || dynamicDefault)foreach(int stack in new[]{requiredStack-1,requiredStack}) {
                ManagedSourceBodyWorldResultV1 budgeted =Compile(request,stack:stack);
                if(budgeted.Compilation?.Status!=(stack==requiredStack?RestrictedCilImportStatusV1.Success:RestrictedCilImportStatusV1.BudgetExhausted))throw new Exception("Nullable parameter erased receiver/default original MaxStack");
            }
            if(computedDefault?.Contains('?')==true && Compile(request,blocks:1).Compilation?.Status!=RestrictedCilImportStatusV1.BudgetExhausted)
                throw new Exception("Nullable computed default CFG proof budget precedence");
        }
        Console.WriteLine("PASS private exact Nullable<int> parameter/"+(computedDefault is not null?"computed-default "+computedDefault:dynamicDefault?"dynamic-default":presence?"HasValue":explicitDefault is null?"GetValueOrDefault":"explicit-default "+explicitDefault)+" packed ingress, NoHeap source/image/host/repeat/maps and signature/body/root/budget controls");
    }
    public static void Run()
    {
        RunParameter();
        RunParameter(presence:true);
        RunParameter(explicitDefault:-7);
        RunParameter(explicitDefault:int.MinValue);
        RunParameter(explicitDefault:int.MaxValue);
        RunParameter(dynamicDefault:true);
        RunComputedParameters();
        RunParameter(computedDefault:"Adjust(fallback)");
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location)];
        foreach ((string? body, string? oracle) in new[] {
            ("int? n = text?.Length; return n ?? 0;", "return text == null ? 0 : text.Length;"),
            ("int? n = input < 0 ? null : input; return n ?? -7;", "return input < 0 ? -7 : input;"),
            ("int? n = input; int? copy = n; return copy.GetValueOrDefault();", "return input;"),
            ("int? n = null; if (input != 0) n = input; return n.HasValue ? n.GetValueOrDefault() : -7;", "return input == 0 ? -7 : input;"),
            ("int? n = input; if (input < 0) n = null; return n ?? 0;", "return input < 0 ? 0 : input;") }) {
            CompanionSourceRequestV1 Request(string expression) => new("NullableLocal", [new("/src/Nullable.cs", "public static class Fixture { public static int Read(string text, int input) { " + expression + " } }")], references);
            ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 request) => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = Compile(Request(body)); ManagedSourceBodyWorldResultV1 scalar = Compile(Request(oracle));
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success || scalar.Compilation?.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception("Nullable local source/import: " + body + "; " + string.Join(';', actual.Source.Diagnostics) + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception("Nullable local link: " + string.Join(';', linked.Diagnostics));
            HashSet<(HybridCpuOpcode, long)> required = new HashSet<(HybridCpuOpcode, long)> { (HybridCpuOpcode.SLLI, 32), (HybridCpuOpcode.SRLI, 31), (HybridCpuOpcode.ORI, 1), (HybridCpuOpcode.SRLI, 1), (HybridCpuOpcode.ADDIW, 0) };
            byte[] bytes = linked.MethodObjects.Single().ObjectArtifact.Sections.Single(s => s.Name == ".text").Data;
            for (int offset = 0; offset < bytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                if (!bundle.TryReadBytes(bytes, offset)) throw new Exception("Nullable serialized bundle");
                for (int slot = 0; slot < HybridCpuInstructionBundle.SlotCount; slot++) {
                    HybridCpuInstructionWord word = bundle.GetInstruction(slot); HybridCpuOpcode opcode = (HybridCpuOpcode)word.OpCode;
                    if (!required.Contains((opcode, word.Immediate))) continue;
                    if (slot > 3 || !HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte rd, out byte rs, out _) || rd is 0 or >= 32 || rs >= 32)
                        throw new Exception("Nullable scalar encoded physical operands/ALU lane");
                    required.Remove((opcode, word.Immediate));
                }
            }
            if (required.Count != 0) throw new Exception("Nullable packed scalar encoding lost exact shifts/flag/word extension: " + string.Join(';', required));
            MethodInfo actualMethod = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            MethodInfo scalarMethod = System.Reflection.Assembly.Load(scalar.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (string? text in new[] { null, "", "\0\u8000\uffff" }) foreach (int input in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
                if (!Equals(actualMethod.Invoke(null, [text, input]), scalarMethod.Invoke(null, [text, input]))) throw new Exception("Nullable independent scalar host oracle");
            if (body.StartsWith("int? n = text", StringComparison.Ordinal)) {
                foreach (int mutation in new[] { 0, 1, 2, 3, 4 }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        if (mutation == 0) {
                            TypeReference type = md.GetTypeReference(md.TypeReferences.Single(h => md.GetString(md.GetTypeReference(h).Name) == "Nullable`1"));
                            int name = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.String) + MetadataTokens.GetHeapOffset(type.Name);
                            changed[name] = (byte)'X';
                        } else if (mutation == 1) {
                            MemberReference member = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                            int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(member.Signature);
                            changed[blob + 1 + 2] = 0x0a;
                        } else {
                            MethodDefinition method = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                            MethodBodyBlock ilBody = pe.GetMethodBody(method.RelativeVirtualAddress);
                            if (mutation == 2) {
                                BlobHandle local = md.GetStandaloneSignature(ilBody.LocalSignature).Signature;
                                byte[] signature = md.GetBlobBytes(local);
                                int item = Array.LastIndexOf(signature, (byte)8);
                                if (item < 0) throw new Exception("Nullable local mutation fixture");
                                int blob = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(local);
                                changed[blob + 1 + item] = 9;
                            } else if (mutation == 3) {
                                byte[] il = ilBody.GetILBytes()!;
                                int address = Enumerable.Range(0, il.Length - 2).First(i => il[i] == 0x12 && il[i + 2] == 0xfe);
                                SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                                int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                                int header = (changed[start] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                                changed[start + header + address + 1] = 255;
                            } else {
                                byte[] il = ilBody.GetILBytes()!;
                                int address = Enumerable.Range(0, il.Length - 2).Single(i => il[i] == 0x12 && il[i + 2] == 0x28);
                                SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => method.RelativeVirtualAddress >= s.VirtualAddress && method.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                                int start = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
                                int header = (changed[start] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                                new byte[] { 0x11, il[address + 1], 0x6a, 0x69, 0, 0, 0 }.CopyTo(changed, start + header + address);
                            }
                        }
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable name/signature/carrier/local-index negative");
                    if (mutation == 4 && !denied.Diagnostics.Any(d => d.Code == "HCCIL1845")) throw new Exception("Nullable carrier escaped as primitive integer");
                }
                foreach (int limit in new[] { 0, 1, 2, 3 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(Request(body), new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = limit }), [new("Fixture", "Read")], null, [0]);
                    if (limit == 3 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success :
                        budgeted.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted)
                        throw new Exception("Nullable operations bypassed original call budget boundary: " + limit);
                }
            }
        }
        foreach (string body in new[] {
            "System.DayOfWeek? n = (System.DayOfWeek)input; return (int)n.GetValueOrDefault();",
            "int? n = input; object boxed = n; return boxed == null ? 0 : 1;",
            "try { int? n = input; return n ?? 0; } finally { input = input + 1; }" }) {
            ManagedSourceBodyWorldResultV1 denied = ManagedSourceCompanionV1.CompileBodyWorld(new("NullableDenied", [new("/src/Nullable.cs",
                "public static class Fixture { public static int Read(string text, int input) { " + body + " } }")], references),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            if (denied.Source.Pe is null || denied.Compilation?.Status == RestrictedCilImportStatusV1.Success || (denied.Compilation?.Diagnostics.Count ?? 0) == 0)
                throw new Exception("Nullable value/I8/boxing/EH gate widened: " + body);
        }
        Console.WriteLine("PASS exact Nullable<int> local source/CFG/image and independent scalar host boundary oracle; malformed/opaque/budget/value/I8/boxing/EH controls");
        foreach ((string? body, string? oracle) in new[] {
            ("long? n = input < 0 ? input : other; return n ?? -7L;", "return input < 0 ? input : other;"),
            ("long? n = input; long? copy = n; return copy.GetValueOrDefault();", "return input;"),
            ("long? n = null; if (input != 0) n = input; return n.HasValue ? n.GetValueOrDefault() : -7L;", "return input == 0 ? -7L : input;"),
            ("long? n = input; if (input < 0) n = null; return n.GetValueOrDefault(other);", "return input < 0 ? other : input;"),
            ("long? n = input < 0 ? null : input; return n ?? -7L;", "return input < 0 ? -7L : input;"),
            ("long? n = input < 0 ? null : input; return n.GetValueOrDefault(long.MinValue);", "return input < 0 ? long.MinValue : input;"),
            ("long? n = text == null ? null : (long)text.Length; return n ?? 0L;", "return text == null ? 0L : (long)text.Length;") }) {
            CompanionSourceRequestV1 RequestWide(string expression) => new("NullableWide", [new("/src/Nullable.cs", "public static class Fixture { public static long Read(string text, long input, long other) { " + expression + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileWide(string expression) => ManagedSourceCompanionV1.CompileBodyWorld(RequestWide(expression),
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileWide(body); ManagedSourceBodyWorldResultV1 scalar = CompileWide(oracle);
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success || scalar.Compilation?.Status != RestrictedCilImportStatusV1.Success) {
                if (actual.Source.Pe is { } emitted) {
                    using PEReader pe = new PEReader(new MemoryStream(emitted)); MetadataReader md = pe.GetMetadataReader();
                    MethodDefinition failedMethod = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                    Console.WriteLine("NULLABLE-I8-FAIL-IL=" + Convert.ToHexString(pe.GetMethodBody(failedMethod.RelativeVirtualAddress).GetILBytes()!));
                    foreach (MemberReferenceHandle h in md.MemberReferences) { MemberReference m = md.GetMemberReference(h); Console.WriteLine("MEMBER " + md.GetString(m.Name) + "=" + Convert.ToHexString(md.GetBlobBytes(m.Signature))); }
                }
                throw new Exception("Nullable I8 import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            }
            ScalarControlFlowV2LinkedProgramV1 image = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            if (image.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception("Nullable I8 link: " + string.Join(';', image.Diagnostics));
            MethodInfo method = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            MethodInfo expected = System.Reflection.Assembly.Load(scalar.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (string? text in new[] { null, "", "\0\u8000\uffff" }) foreach (long input in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue })
                foreach (long other in new[] { long.MinValue, -7L, 0, long.MaxValue })
                    if (!Equals(method.Invoke(null, [text, input, other]), expected.Invoke(null, [text, input, other]))) throw new Exception("Nullable I8 independent host oracle");
            Console.WriteLine("NULLABLE-I8-SOURCE " + body);
            if (body.StartsWith("long? n = input; long? copy", StringComparison.Ordinal)) {
                foreach (int mutation in new[] { 0, 1, 2, 3 }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        if (mutation == 0) {
                            TypeReference type = md.GetTypeReference(md.TypeReferences.Single(h => md.GetString(md.GetTypeReference(h).Name) == "Nullable`1"));
                            changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.String) + MetadataTokens.GetHeapOffset(type.Name)] = (byte)'X';
                        } else if (mutation == 1) {
                            MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                            changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(getter.Signature) + 1 + 2] = 8;
                        } else {
                            MethodDefinition definition = md.GetMethodDefinition(md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Read"));
                            MethodBodyBlock ilBody = pe.GetMethodBody(definition.RelativeVirtualAddress);
                            if (mutation == 2) {
                                BlobHandle signature = md.GetStandaloneSignature(ilBody.LocalSignature).Signature;
                                byte[] declaration = md.GetBlobBytes(signature); int item = Array.LastIndexOf(declaration, (byte)0x0a);
                                if (item < 0) throw new Exception("Nullable I8 local mutation fixture");
                                changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(signature) + 1 + item] = 8;
                            } else {
                                byte[] il = ilBody.GetILBytes()!;
                                int getter = Enumerable.Range(0, il.Length - 6).Last(i => il[i] == 0x12 && il[i + 2] == 0x28);
                                SectionHeader section = pe.PEHeaders.SectionHeaders.Single(s => definition.RelativeVirtualAddress >= s.VirtualAddress && definition.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
                                int start = section.PointerToRawData + definition.RelativeVirtualAddress - section.VirtualAddress;
                                int header = (changed[start] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(start, 2)) >> 12) * 4;
                                new byte[] { 0x11, il[getter + 1], 0x6a, 0, 0, 0, 0 }.CopyTo(changed, start + header + getter);
                            }
                        }
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-wide-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    Console.WriteLine("NULLABLE-I8-NEGATIVE mutation=" + mutation + "; status=" + denied.Status + "; codes=" + string.Join(';', denied.Diagnostics.Select(d => d.Code)));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0 || mutation == 3 && !denied.Diagnostics.Any(d => d.Code == "HCCIL1845"))
                        throw new Exception("Nullable I8 wrong nominal/signature/carrier/opaque gate");
                }
                foreach (int limit in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(RequestWide(body), new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = limit }), [new("Fixture", "Read")], null, [0]);
                    if (limit == 2 ? budgeted.Compilation?.Status != RestrictedCilImportStatusV1.Success : budgeted.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted)
                        throw new Exception("Nullable I8 original call budget boundary");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<long> locals full-I8/presence SSA, conditional/copy/reassignment/accessor/default source/image and independent scalar host oracle");
        foreach (string width in new[] { "int", "long" }) foreach (bool conditional in new[] { false, true })
            foreach (string argument in new[] { "input + 1", "input < 0 ? input - 7 : input + 9", "input / (input - input)", "input += 1" }) {
            string body = width + "? n = " + (conditional ? "input < 0 ? null : input" : "input") +
                "; " + width + " result = n.GetValueOrDefault(" + argument + "); return result ^ input;";
            string oracle = width + " original = input; bool present = " + (conditional ? "input >= 0" : "true") +
                "; " + width + " fallback = " + (argument == "input += 1" ? "(input += 1)" : argument) + "; return (present ? original : fallback) ^ input;";
            CompanionSourceRequestV1 RequestComputed(string text) => new("ComputedNullable", [new("/src/Computed.cs",
                "public static class Fixture { public static " + width + " Read(" + width + " input) { " + text + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileComputed(CompanionSourceRequestV1 request, int calls = 256) => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                ManagedSourceBodyWorldResultV1 actual = CompileComputed(RequestComputed(body)); ManagedSourceBodyWorldResultV1 scalar = CompileComputed(RequestComputed(oracle));
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success || scalar.Compilation?.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception("Computed nullable import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
                ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
                ManagedSourceBodyWorldResultV1 again = CompileComputed(RequestComputed(body));
                ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) ||
                !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) || !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) ||
                actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest)) throw new Exception("Computed nullable repeat/image");
                MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
                MethodInfo expected = System.Reflection.Assembly.Load(scalar.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            static object? Outcome(System.Reflection.MethodInfo method, object input) {
                try { return method.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { return fault.InnerException!.GetType(); }
            }
            foreach (long boundary in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }) {
                object input = width == "int" ? (object)unchecked((int)boundary) : boundary;
                if (!Equals(Outcome(host, input), Outcome(expected, input))) throw new Exception("Computed nullable eager result/fault oracle");
            }
            if (!conditional && argument == "input + 1") {
                foreach (int calls in new[] { 0, 1, 2 }) {
                        ManagedSourceBodyWorldResultV1 budgeted = CompileComputed(RequestComputed(body), calls);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Computed nullable call budget " + calls);
                }
                byte[] changed = actual.Source.Pe!.ToArray();
                using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference member = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                    changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(member.Signature) + 1 + 4] = 8;
                }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "computed-nullable-signature", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Computed nullable exact signature gate");
            }
        }
        Console.WriteLine("PASS computed nullable default scalar/CFG/eager argument stores/fault oracle, repeat PE/PDB/maps/image and exact signature/call budget controls");
        foreach (string width in new[] { "int", "long" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("ComputedNullableBudget", [new("/src/Budget.cs",
                "public static class Fixture { public static " + width + " Read(string text, " + width + " input) { " + width +
                "? n = input; return n.GetValueOrDefault((" + width + ")text.Length + 1); } }")], references);
            foreach (int calls in new[] { 0, 1, 2, 3 }) {
                ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                if (budgeted.Compilation?.Status != (calls == 3 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                    throw new Exception("Computed nullable helper proof budget precedence: " + calls + "; " + string.Join(';', budgeted.Compilation?.Diagnostics ?? []));
            }
        }
        foreach (string width in new[] { "int", "long" }) foreach (bool conditional in new[] { false, true }) {
            string source = "public static class Fixture { public static " + width + " Read(" + width + " input) { " + width + "? n = " +
                (conditional ? "input < 0 ? null : input" : "input") + "; return n.Value; } }";
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableValue", [new("/src/Value.cs", source)], references);
            ManagedSourceBodyWorldResultV1 CompileValue() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileValue(); ManagedSourceBodyWorldResultV1 repeat = CompileValue();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Checked nullable import: " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 image = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeatedImage = new ScalarControlFlowV2ObjectLinkerV1().Link(repeat.Compilation!, repeat.Compilation!.Graph!.RootIdentities.Single());
            if (image.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Compilation.Methods.Single().Import.RequiresNativeExceptionTransfer ||
                !actual.Source.Pe!.SequenceEqual(repeat.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(repeat.Source.Pdb!) ||
                !image.RestrictedImage!.PackageBytes.SequenceEqual(repeatedImage.RestrictedImage!.PackageBytes) ||
                actual.SourceMaps.Any(p => repeat.SourceMaps[p.Key].Digest != p.Value.Digest)) throw new Exception("Checked nullable image/repeat: " + string.Join(';', image.Diagnostics));
            MethodInfo method = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (long boundary in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }) {
                object input = width == "int" ? (object)unchecked((int)boundary) : boundary;
                object? outcome;
                try { outcome = method.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                bool missing = conditional && (width == "int" ? (int)input < 0 : (long)input < 0);
                if (!Equals(outcome, missing ? typeof(InvalidOperationException) : input)) throw new Exception("Checked nullable scalar result/fault oracle");
            }
        }
        Console.WriteLine("PASS checked Nullable<int/long>.Value result/InvalidOperationException source/image/native-EH and repeated PE/PDB/maps/image");
        foreach (string width in new[] { "int", "long" }) foreach (bool outerPresent in new[] { false, true })
            foreach (string getter in new[] { "fallback.Value", "fallback.GetValueOrDefault()", "fallback.HasValue ? fallback.Value + 1 : 7",
                "fallback.GetValueOrDefault(input + 1)", "fallback.GetValueOrDefault(fallback.GetValueOrDefault(input + 1))" }) {
            string body = width + "? length = " + (outerPresent ? "input" : "null") + "; " + width +
                "? fallback = input < 0 ? null : input; return length.GetValueOrDefault(" + getter + ");";
                CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NestedNullable", [new("/src/Nested.cs", "public static class Fixture { public static " + width +
                " Read(" + width + " input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileNested(int calls = 256, int stack = 256) => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls, MaximumEvaluationStack = stack }), [new("Fixture", "Read")], null, [0]);
                ManagedSourceBodyWorldResultV1 actual = CompileNested(); ManagedSourceBodyWorldResultV1 again = CompileNested();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nested nullable import: " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
                ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
                ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) ||
                !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) || !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) ||
                actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest)) throw new Exception("Nested nullable repeat/image");
                MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (long boundary in new[] { long.MinValue, (long)int.MinValue - 1, -1L, 0, 1, (long)int.MaxValue + 1, long.MaxValue }) {
                object input = width == "int" ? (object)unchecked((int)boundary) : boundary;
                long scalar = width == "int" ? (int)input : (long)input;
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected;
                if (scalar < 0 && getter == "fallback.Value") expected = typeof(InvalidOperationException);
                else {
                    long fallback = getter.StartsWith("fallback.HasValue") ? scalar < 0 ? 7 : unchecked(scalar + 1) :
                        scalar < 0 ? getter.Contains("input + 1") ? unchecked(scalar + 1) : 0 : scalar;
                    long result = outerPresent ? scalar : fallback;
                    expected = width == "int" ? (object)unchecked((int)result) : result;
                }
                if (!Equals(outcome, expected)) throw new Exception("Nested nullable independent eager result/fault oracle");
            }
            if (outerPresent && getter == "fallback.Value") {
                foreach (int calls in new[] { 0, 1, 2, 3, 4 }) {
                        ManagedSourceBodyWorldResultV1 budgeted = CompileNested(calls);
                    if (budgeted.Compilation?.Status != (calls == 4 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nested nullable original call budget " + calls);
                }
            }
            if (outerPresent && getter.Contains("input + 1")) {
                int callsRequired = getter.Contains("fallback.GetValueOrDefault(fallback.") ? 5 : 4;
                foreach (int calls in new[] { 0, callsRequired - 1, callsRequired }) {
                        ManagedSourceBodyWorldResultV1 budgeted = CompileNested(calls);
                    if (budgeted.Compilation?.Status != (calls == callsRequired ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nested computed nullable original call budget " + calls);
                }
                int originalStack = host.GetMethodBody()!.MaxStackSize;
                foreach (int stack in new[] { originalStack - 1, originalStack }) {
                        ManagedSourceBodyWorldResultV1 budgeted = CompileNested(stack: stack);
                    if (budgeted.Compilation?.Status != (stack == originalStack ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nested computed nullable original MaxStack boundary " + stack);
                }
            }
        }
        Console.WriteLine("PASS nested nullable leaf default arguments independent present/empty/eager Value fault oracle, repeat/maps/image and original call budgets");
        foreach (string argument in new[] { "input + 1", "input < 0 ? input - 7 : input + 9", "input += 1", "input / (input - input)" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableComputedConstructor", [new("/src/Initializer.cs",
                "public static class Fixture { public static long Read(int input) { int? length = " + argument + "; return (long)length.GetValueOrDefault() + input; } }")], references);
            ManagedSourceBodyWorldResultV1 CompileCtor(int calls = 256, int stack = 256) => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls, MaximumEvaluationStack = stack }), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileCtor(); ManagedSourceBodyWorldResultV1 again = CompileCtor();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Computed I4 nullable constructor: " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Computed I4 nullable constructor repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (int input in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                int result = argument.Contains('?') ? input < 0 ? unchecked(input - 7) : unchecked(input + 9) : unchecked(input + 1);
                object expected = argument.Contains('/') ? typeof(DivideByZeroException) : (long)result + (argument.Contains("+=") ? result : input);
                if (!Equals(outcome, expected)) throw new Exception("Computed I4 nullable constructor independent host oracle");
            }
            if (argument == "input + 1") {
                foreach (int calls in new[] { 0, 1, 2 }) if (CompileCtor(calls).Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                    throw new Exception("Computed I4 nullable original call budget");
                int maxStack = host.GetMethodBody()!.MaxStackSize;
                foreach (int stack in new[] { maxStack - 1, maxStack }) if (CompileCtor(stack: stack).Compilation?.Status != (stack == maxStack ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                    throw new Exception("Computed I4 nullable original MaxStack");
            }
        }
        Console.WriteLine("PASS computed Nullable<int> in-place initializer arithmetic/forward CFG/eager stores/division faults, original budgets and repeat PE/PDB/maps/image");
        foreach (string body in new[] {
            "uint? n = input; uint? copy = n; return (long)copy.GetValueOrDefault();",
            "uint? n = input + 1U; return (long)n.GetValueOrDefault();",
            "uint? n = input == 0 ? null : input; return (long)n.GetValueOrDefault(0x80000000U);",
            "uint? n = null; if (input != 0) n = input; return (long)n.GetValueOrDefault(input + 1U);",
            "uint? n = input == 0 ? null : input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableUnsigned", [new("/src/Unsigned.cs",
                "public static class Fixture { public static long Read(uint input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable uint import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable uint repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (uint input in new uint[] { 0, 1, 0x7fffffffU, 0x80000000U, uint.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = input + 1U") ? (long)unchecked(input + 1U) :
                    input == 0 && body.Contains("0x80000000U") ? 0x80000000L :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable uint independent zero-extension/host fault oracle");
            }
            if (body.StartsWith("uint? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable uint original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = 8;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-u4-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable uint nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<uint> local/copy/join/initializer/default/Value full U4 payload and zero-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "ulong? n = input; ulong? copy = n; return unchecked((long)copy.GetValueOrDefault());",
            "ulong? n = input + 1UL; return unchecked((long)n.GetValueOrDefault());",
            "ulong? n = input == 0 ? null : input; return unchecked((long)n.GetValueOrDefault(0x8000000000000000UL));",
            "ulong? n = null; if (input != 0) n = input; return unchecked((long)n.GetValueOrDefault(input + 1UL));",
            "ulong? n = input == 0 ? null : input; return unchecked((long)n.Value);" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableUnsignedWide", [new("/src/UnsignedWide.cs",
                "public static class Fixture { public static long Read(ulong input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileWideUnsigned(int calls = 256) => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                    budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileWideUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileWideUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable ulong import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable ulong repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (ulong input in new ulong[] { 0, 1, 0x7fffffffUL, 0x80000000UL, 0x7fffffffffffffffUL, 0x8000000000000000UL, ulong.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    unchecked((long)(body.Contains("n = input + 1UL") ? input + 1UL : input == 0 && body.Contains("0x8000000000000000UL") ? 0x8000000000000000UL :
                    input == 0 && body.Contains("if (input != 0)") ? 1UL : input));
                if (!Equals(outcome, expected)) throw new Exception("Nullable ulong independent full-bit/host fault oracle");
            }
            if (body.StartsWith("ulong? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) if (CompileWideUnsigned(calls).Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                    throw new Exception("Nullable ulong original call budget");
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + (wrongNominal ? bytes.Length - 1 : 2)] = 0x0a;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-u8-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable ulong nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<ulong> local/copy/join/initializer/default/Value full U8/presence, host boundary/fault, nominal/signature/budget and repeat/maps/image");
        foreach (string body in new[] {
            "ushort? n = input; ushort? copy = n; return (long)copy.GetValueOrDefault();",
            "ushort? n = unchecked((ushort)(input + 1)); return (long)n.GetValueOrDefault();",
            "ushort? n = input == 0 ? null : input; return (long)n.GetValueOrDefault(0x8000);",
            "ushort? n = null; if (input != 0) n = input; return (long)n.GetValueOrDefault(unchecked((ushort)(input + 1)));",
            "ushort? n = input == 0 ? null : input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableUnsignedNarrow", [new("/src/Narrow.cs",
                "public static class Fixture { public static long Read(ushort input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable ushort import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable ushort repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (ushort input in new ushort[] { 0, 1, 0x7fff, 0x8000, ushort.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((ushort)(input + 1))") ? (long)unchecked(unchecked((ushort)(input + 1))) :
                    input == 0 && body.Contains("0x8000") ? 0x8000L :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable ushort independent zero-extension/host fault oracle");
            }
            if (body.StartsWith("ushort? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable ushort original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = 6;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-u2-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable ushort nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<ushort> local/copy/join/initializer/default/Value bounded U2 payload and zero-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "short? n = input; short? copy = n; return (long)copy.GetValueOrDefault();",
            "short? n = unchecked((short)(input + 1)); return (long)n.GetValueOrDefault();",
            "short? n = input < 0 ? null : input; return (long)n.GetValueOrDefault(short.MinValue);",
            "short? n = null; if (input != 0) n = input; return (long)n.GetValueOrDefault(unchecked((short)(input + 1)));",
            "short? n = input < 0 ? null : input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableSignedNarrow", [new("/src/SignedNarrow.cs",
                "public static class Fixture { public static long Read(short input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable short import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable short repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (short input in new short[] { short.MinValue, -256, -1, 0, 1, 255, short.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input < 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((short)(input + 1))") ? (long)unchecked(unchecked((short)(input + 1))) :
                    input < 0 && body.Contains("short.MinValue") ? (long)short.MinValue :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable short independent zero-extension/host fault oracle");
            }
            if (body.StartsWith("short? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable short original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = 7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-i2-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable short nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<short> local/copy/join/initializer/default/Value bounded I2 payload and sign-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "byte? n = input; byte? copy = n; return (long)copy.GetValueOrDefault();",
            "byte? n = unchecked((byte)(input + 1)); return (long)n.GetValueOrDefault();",
            "byte? n = input == 0 ? null : input; return (long)n.GetValueOrDefault(0x80);",
            "byte? n = null; if (input != 0) n = input; return (long)n.GetValueOrDefault(unchecked((byte)(input + 1)));",
            "byte? n = input == 0 ? null : input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableUnsignedByte", [new("/src/Byte.cs",
                "public static class Fixture { public static long Read(byte input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable byte import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable byte repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (byte input in new byte[] { 0, 1, 0x7f, 0x80, byte.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((byte)(input + 1))") ? (long)unchecked(unchecked((byte)(input + 1))) :
                    input == 0 && body.Contains("0x80") ? 0x80L :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable byte independent zero-extension/host fault oracle");
            }
            if (body.StartsWith("byte? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable byte original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = 4;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-u1-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable byte nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<byte> local/copy/join/initializer/default/Value bounded U1 payload and zero-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "sbyte? n = input; sbyte? copy = n; return (long)copy.GetValueOrDefault();",
            "sbyte? n = unchecked((sbyte)(input + 1)); return (long)n.GetValueOrDefault();",
            "sbyte? n = input < 0 ? null : input; return (long)n.GetValueOrDefault(sbyte.MinValue);",
            "sbyte? n = null; if (input != 0) n = input; return (long)n.GetValueOrDefault(unchecked((sbyte)(input + 1)));",
            "sbyte? n = input < 0 ? null : input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableSignedByte", [new("/src/SignedByte.cs",
                "public static class Fixture { public static long Read(sbyte input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable sbyte import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable sbyte repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (sbyte input in new sbyte[] { sbyte.MinValue, -127, -1, 0, 1, 126, sbyte.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input < 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((sbyte)(input + 1))") ? (long)unchecked(unchecked((sbyte)(input + 1))) :
                    input < 0 && body.Contains("sbyte.MinValue") ? (long)sbyte.MinValue :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable sbyte independent sign-extension/host fault oracle");
            }
            if (body.StartsWith("sbyte? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable sbyte original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = 5;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-i1-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable sbyte nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<sbyte> local/copy/join/initializer/default/Value bounded I1 payload and sign-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "char? n = input; char? copy = n; return (long)copy.GetValueOrDefault();",
            "char? n = unchecked((char)(input + 1)); return (long)n.GetValueOrDefault();",
            "char? n = input == 0 ? null : input; return (long)n.GetValueOrDefault((char)0x8000);",
            "char? n = null; if (input != 0) n = input; return (long)n.GetValueOrDefault(unchecked((char)(input + 1)));",
            "char? n = input == 0 ? null : input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableCharacter", [new("/src/Character.cs",
                "public static class Fixture { public static long Read(char input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable char import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable char repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (char input in new char[] { '\0', '\u0001', '\u7fff', '\u8000', '\ud800', '\udfff', char.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((char)(input + 1))") ? (long)unchecked(unchecked((char)(input + 1))) :
                    input == 0 && body.Contains("0x8000") ? 0x8000L :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable char independent zero-extension/host fault oracle");
            }
            if (body.StartsWith("char? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable char original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = 7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-char-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable char nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<char> local/copy/join/initializer/default/Value bounded Char/U2 payload and zero-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "bool? n = input; bool? copy = n; return copy.GetValueOrDefault() ? 1L : 0L;",
            "bool? n = !input; return n.GetValueOrDefault() ? 1L : 0L;",
            "bool? n = input ? null : input; return n.GetValueOrDefault(true) ? 1L : 0L;",
            "bool? n = null; if (input) n = input; return n.GetValueOrDefault(!input) ? 1L : 0L;",
            "bool? n = input ? null : input; return n.Value ? 1L : 0L;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableBoolean", [new("/src/Boolean.cs",
                "public static class Fixture { public static long Read(bool input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable bool import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable bool repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (bool input in new[] { false, true }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input ? typeof(InvalidOperationException) :
                    body.Contains("n = !input") ? (!input ? 1L : 0L) :
                    body.Contains("if (input)") ? 1L : input ? 1L : 0L;
                if (!Equals(outcome, expected)) throw new Exception("Nullable bool independent logical/host fault oracle");
            }
            if (body.StartsWith("bool? n = input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable bool original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = 5;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-bool-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable bool nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<bool> local/copy/join/initializer/default/Value Boolean nominal and CLI byte payload, host logical/fault and repeat/maps/image");
        foreach (string body in new[] {
            "Choice? n = (Choice)input; Choice? copy = n; return (long)copy.GetValueOrDefault();",
            "Choice? n = unchecked((Choice)(input + 1)); return (long)n.GetValueOrDefault();",
            "Choice? n = input < 0 ? null : (Choice)input; return (long)n.GetValueOrDefault((Choice)int.MinValue);",
            "Choice? n = null; if (input != 0) n = (Choice)input; return (long)n.GetValueOrDefault(unchecked((Choice)(input + 1)));",
            "Choice? n = input < 0 ? null : (Choice)input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumInt32", [new("/src/EnumInt32.cs",
                "public enum Choice : int { Zero = 0 } public enum Other : int { Zero = 0 } public static class Fixture { public static long Read(int input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable enum repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (int input in new int[] { int.MinValue, -256, -1, 0, 1, 255, int.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input < 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((Choice)(input + 1))") ? (long)unchecked(input + 1) :
                    input < 0 && body.Contains("int.MinValue") ? (long)int.MinValue :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable enum independent zero-extension/host fault oracle");
            }
            if (body.StartsWith("Choice? n = (Choice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "Other")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-i4-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<Choice> local/copy/join/initializer/default/Value bounded I4 payload and sign-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "WideChoice? n = (WideChoice)input; WideChoice? copy = n; return (long)copy.GetValueOrDefault();",
            "WideChoice? n = unchecked((WideChoice)(input + 1)); return (long)n.GetValueOrDefault();",
            "WideChoice? n = input < 0 ? null : (WideChoice)input; return (long)n.GetValueOrDefault((WideChoice)long.MinValue);",
            "WideChoice? n = null; if (input != 0) n = (WideChoice)input; return (long)n.GetValueOrDefault(unchecked((WideChoice)(input + 1)));",
            "WideChoice? n = input < 0 ? null : (WideChoice)input; return (long)n.Value;" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumInt64", [new("/src/EnumInt64.cs",
                "public enum WideChoice : long { Zero = 0 } public enum WideOther : long { Zero = 0 } public static class Fixture { public static long Read(long input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable wide enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable wide enum repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (long input in new long[] { long.MinValue, (long)int.MinValue - 1, -1, 0, 1, (long)int.MaxValue + 1, long.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input < 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((WideChoice)(input + 1))") ? (long)unchecked(input + 1) :
                    input < 0 && body.Contains("long.MinValue") ? (long)long.MinValue :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : (long)input;
                if (!Equals(outcome, expected)) throw new Exception("Nullable wide enum independent zero-extension/host fault oracle");
            }
            if (body.StartsWith("WideChoice? n = (WideChoice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable wide enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "WideOther")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-i8-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable wide enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<WideChoice> local/copy/join/initializer/default/Value bounded I8/presence and sign-extension, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "UnsignedChoice? n = (UnsignedChoice)input; UnsignedChoice? copy = n; return unchecked((long)copy.GetValueOrDefault());",
            "UnsignedChoice? n = unchecked((UnsignedChoice)(input + 1UL)); return unchecked((long)n.GetValueOrDefault());",
            "UnsignedChoice? n = input == 0 ? null : (UnsignedChoice)input; return unchecked((long)n.GetValueOrDefault((UnsignedChoice)0x8000000000000000UL));",
            "UnsignedChoice? n = null; if (input != 0) n = (UnsignedChoice)input; return unchecked((long)n.GetValueOrDefault(unchecked((UnsignedChoice)(input + 1UL))));",
            "UnsignedChoice? n = input == 0 ? null : (UnsignedChoice)input; return unchecked((long)n.Value);" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumUInt64", [new("/src/EnumUInt64.cs",
                "public enum UnsignedChoice : ulong { Zero = 0 } public enum UnsignedOther : ulong { Zero = 0 } public static class Fixture { public static long Read(ulong input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable unsigned wide enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable unsigned wide enum repeat/image: " + body +
                    "; link=" + linked.Status + "; diagnostics=" + string.Join(';', linked.Diagnostics) +
                    "; repeat=" + repeated.Status + "; repeat diagnostics=" + string.Join(';', repeated.Diagnostics) +
                    "; PE=" + actual.Source.Pe!.SequenceEqual(again.Source.Pe!) +
                    "; PDB=" + actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!));
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (ulong input in new ulong[] { 0, 1, 0x7fffffffUL, 0x80000000UL, 0x7fffffffffffffffUL, 0x8000000000000000UL, ulong.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((UnsignedChoice)(input + 1UL))") ? unchecked((long)(input + 1UL)) :
                    input == 0 && body.Contains("0x8000000000000000UL") ? long.MinValue :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : unchecked((long)input);
                if (!Equals(outcome, expected)) throw new Exception("Nullable unsigned wide enum independent full-width host/fault oracle");
            }
            if (body.StartsWith("UnsignedChoice? n = (UnsignedChoice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable unsigned wide enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "UnsignedOther")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-u8-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable unsigned wide enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<UnsignedChoice> local/copy/join/initializer/default/Value bounded U8/presence and explicit widening, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "WordChoice? n = (WordChoice)input; WordChoice? copy = n; return unchecked((long)copy.GetValueOrDefault());",
            "WordChoice? n = unchecked((WordChoice)(input + 1U)); return unchecked((long)n.GetValueOrDefault());",
            "WordChoice? n = input == 0 ? null : (WordChoice)input; return unchecked((long)n.GetValueOrDefault((WordChoice)0x80000000U));",
            "WordChoice? n = null; if (input != 0) n = (WordChoice)input; return unchecked((long)n.GetValueOrDefault(unchecked((WordChoice)(input + 1U))));",
            "WordChoice? n = input == 0 ? null : (WordChoice)input; return unchecked((long)n.Value);" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumUInt32", [new("/src/EnumUInt32.cs",
                "public enum WordChoice : uint { Zero = 0 } public enum WordOther : uint { Zero = 0 } public static class Fixture { public static long Read(uint input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable unsigned word enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable unsigned word enum repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (uint input in new uint[] { 0, 1, 0x7fffffffU, 0x80000000U, uint.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((WordChoice)(input + 1U))") ? unchecked((long)(input + 1U)) :
                    input == 0 && body.Contains("0x80000000U") ? 0x80000000L :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : unchecked((long)input);
                if (!Equals(outcome, expected)) throw new Exception("Nullable unsigned word enum independent full-width host/fault oracle");
            }
            if (body.StartsWith("WordChoice? n = (WordChoice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable unsigned word enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "WordOther")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-u4-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable unsigned word enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<WordChoice> local/copy/join/initializer/default/Value bounded U4/zero-extension and explicit widening, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "NarrowChoice? n = (NarrowChoice)input; NarrowChoice? copy = n; return unchecked((long)copy.GetValueOrDefault());",
            "NarrowChoice? n = unchecked((NarrowChoice)(input + 1)); return unchecked((long)n.GetValueOrDefault());",
            "NarrowChoice? n = input == 0 ? null : (NarrowChoice)input; return unchecked((long)n.GetValueOrDefault((NarrowChoice)0x8000));",
            "NarrowChoice? n = null; if (input != 0) n = (NarrowChoice)input; return unchecked((long)n.GetValueOrDefault(unchecked((NarrowChoice)(input + 1))));",
            "NarrowChoice? n = input == 0 ? null : (NarrowChoice)input; return unchecked((long)n.Value);" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumUInt16", [new("/src/EnumUInt16.cs",
                "public enum NarrowChoice : ushort { Zero = 0 } public enum NarrowOther : ushort { Zero = 0 } public static class Fixture { public static long Read(ushort input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable unsigned narrow enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable unsigned narrow enum repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (ushort input in new ushort[] { 0, 1, 0x7fff, 0x8000, ushort.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((NarrowChoice)(input + 1))") ? (long)unchecked((ushort)(input + 1)) :
                    input == 0 && body.Contains("0x8000") ? 0x8000L :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : unchecked((long)input);
                if (!Equals(outcome, expected)) throw new Exception("Nullable unsigned narrow enum independent full-width host/fault oracle");
            }
            if (body.StartsWith("NarrowChoice? n = (NarrowChoice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable unsigned narrow enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "NarrowOther")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-u2-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable unsigned narrow enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<NarrowChoice> local/copy/join/initializer/default/Value bounded U2/zero-extension and explicit widening, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "SignedChoice? n = (SignedChoice)input; SignedChoice? copy = n; return unchecked((long)copy.GetValueOrDefault());",
            "SignedChoice? n = unchecked((SignedChoice)(input + 1)); return unchecked((long)n.GetValueOrDefault());",
            "SignedChoice? n = input < 0 ? null : (SignedChoice)input; return unchecked((long)n.GetValueOrDefault((SignedChoice)short.MinValue));",
            "SignedChoice? n = null; if (input != 0) n = (SignedChoice)input; return unchecked((long)n.GetValueOrDefault(unchecked((SignedChoice)(input + 1))));",
            "SignedChoice? n = input < 0 ? null : (SignedChoice)input; return unchecked((long)n.Value);" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumInt16", [new("/src/EnumInt16.cs",
                "public enum SignedChoice : short { Zero = 0 } public enum SignedOther : short { Zero = 0 } public static class Fixture { public static long Read(short input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable signed narrow enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable signed narrow enum repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (short input in new short[] { short.MinValue, -256, -1, 0, 1, 255, short.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input < 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((SignedChoice)(input + 1))") ? (long)unchecked((short)(input + 1)) :
                    input < 0 && body.Contains("short.MinValue") ? (long)short.MinValue :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : unchecked((long)input);
                if (!Equals(outcome, expected)) throw new Exception("Nullable signed narrow enum independent full-width host/fault oracle");
            }
            if (body.StartsWith("SignedChoice? n = (SignedChoice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable signed narrow enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "SignedOther")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-i2-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable signed narrow enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<SignedChoice> local/copy/join/initializer/default/Value bounded I2/sign-extension and explicit widening, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "ByteChoice? n = (ByteChoice)input; ByteChoice? copy = n; return unchecked((long)copy.GetValueOrDefault());",
            "ByteChoice? n = unchecked((ByteChoice)(input + 1)); return unchecked((long)n.GetValueOrDefault());",
            "ByteChoice? n = input == 0 ? null : (ByteChoice)input; return unchecked((long)n.GetValueOrDefault((ByteChoice)0x80));",
            "ByteChoice? n = null; if (input != 0) n = (ByteChoice)input; return unchecked((long)n.GetValueOrDefault(unchecked((ByteChoice)(input + 1))));",
            "ByteChoice? n = input == 0 ? null : (ByteChoice)input; return unchecked((long)n.Value);" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumUInt8", [new("/src/EnumUInt8.cs",
                "public enum ByteChoice : byte { Zero = 0 } public enum ByteOther : byte { Zero = 0 } public static class Fixture { public static long Read(byte input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable byte enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable byte enum repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (byte input in new byte[] { 0, 1, 0x7f, 0x80, byte.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input == 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((ByteChoice)(input + 1))") ? (long)unchecked((byte)(input + 1)) :
                    input == 0 && body.Contains("0x80") ? 0x80L :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : unchecked((long)input);
                if (!Equals(outcome, expected)) throw new Exception("Nullable byte enum independent full-width host/fault oracle");
            }
            if (body.StartsWith("ByteChoice? n = (ByteChoice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable byte enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "ByteOther")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-u1-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable byte enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<ByteChoice> local/copy/join/initializer/default/Value bounded U1/zero-extension and explicit widening, host boundary/fault and repeat/maps/image");
        foreach (string body in new[] {
            "SByteChoice? n = (SByteChoice)input; SByteChoice? copy = n; return unchecked((long)copy.GetValueOrDefault());",
            "SByteChoice? n = unchecked((SByteChoice)(input + 1)); return unchecked((long)n.GetValueOrDefault());",
            "SByteChoice? n = input < 0 ? null : (SByteChoice)input; return unchecked((long)n.GetValueOrDefault((SByteChoice)sbyte.MinValue));",
            "SByteChoice? n = null; if (input != 0) n = (SByteChoice)input; return unchecked((long)n.GetValueOrDefault(unchecked((SByteChoice)(input + 1))));",
            "SByteChoice? n = input < 0 ? null : (SByteChoice)input; return unchecked((long)n.Value);" }) {
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1("NullableEnumInt8", [new("/src/EnumInt8.cs",
                "public enum SByteChoice : sbyte { Zero = 0 } public enum SByteOther : sbyte { Zero = 0 } public static class Fixture { public static long Read(sbyte input) { " + body + " } }")], references);
            ManagedSourceBodyWorldResultV1 CompileUnsigned() => ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")], null, [0]);
            ManagedSourceBodyWorldResultV1 actual = CompileUnsigned(); ManagedSourceBodyWorldResultV1 again = CompileUnsigned();
            if (actual.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Nullable signed byte enum import: " + body + "; " + string.Join(';', actual.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(actual.Compilation, actual.Compilation.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 repeated = new ScalarControlFlowV2ObjectLinkerV1().Link(again.Compilation!, again.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || !actual.Source.Pe!.SequenceEqual(again.Source.Pe!) || !actual.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !linked.RestrictedImage!.PackageBytes.SequenceEqual(repeated.RestrictedImage!.PackageBytes) || actual.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest))
                throw new Exception("Nullable signed byte enum repeat/image");
            MethodInfo host = System.Reflection.Assembly.Load(actual.Source.Pe!).GetType("Fixture")!.GetMethod("Read")!;
            foreach (sbyte input in new sbyte[] { sbyte.MinValue, -127, -1, 0, 1, 126, sbyte.MaxValue }) {
                object? outcome;
                try { outcome = host.Invoke(null, [input]); } catch (System.Reflection.TargetInvocationException fault) { outcome = fault.InnerException!.GetType(); }
                object expected = body.Contains("n.Value") && input < 0 ? typeof(InvalidOperationException) :
                    body.Contains("n = unchecked((SByteChoice)(input + 1))") ? (long)unchecked((sbyte)(input + 1)) :
                    input < 0 && body.Contains("sbyte.MinValue") ? (long)sbyte.MinValue :
                    input == 0 && body.Contains("if (input != 0)") ? 1L : unchecked((long)input);
                if (!Equals(outcome, expected)) throw new Exception("Nullable signed byte enum independent full-width host/fault oracle");
            }
            if (body.StartsWith("SByteChoice? n = (SByteChoice)input;")) {
                foreach (int calls in new[] { 0, 1, 2 }) {
                    ManagedSourceBodyWorldResultV1 budgeted = ManagedSourceCompanionV1.CompileBodyWorld(request, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                        budgets: RestrictedCilImportBudgetsV1.Production with { MaximumCalls = calls }), [new("Fixture", "Read")], null, [0]);
                    if (budgeted.Compilation?.Status != (calls == 2 ? RestrictedCilImportStatusV1.Success : RestrictedCilImportStatusV1.BudgetExhausted))
                        throw new Exception("Nullable signed byte enum original call budget");
                }
                foreach (bool wrongNominal in new[] { false, true }) {
                    byte[] changed = actual.Source.Pe!.ToArray();
                    using (PEReader pe = new PEReader(new MemoryStream(changed))) {
                        MetadataReader md = pe.GetMetadataReader();
                        MemberReference getter = md.GetMemberReference(md.MemberReferences.Single(h => md.GetString(md.GetMemberReference(h).Name) == "GetValueOrDefault"));
                        BlobHandle blob = wrongNominal ? md.GetTypeSpecification((TypeSpecificationHandle)getter.Parent).Signature : getter.Signature;
                        byte[] bytes = md.GetBlobBytes(blob);
                        int index = wrongNominal ? bytes.Length - 1 : 2;
                        changed[pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1 + index] = wrongNominal
                            ? checked((byte)(MetadataTokens.GetRowNumber(md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == "SByteOther")) << 2))
                            : (byte)7;
                    }
                    ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                        ManagedBodyWorldModeV1.StandaloneRestrictedModule, changed, "nullable-enum-i1-negative", [new("Fixture", "Read")], [], MetadataOnlyModules: [new(references[0], "core")]));
                    if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0) throw new Exception("Nullable signed byte enum nominal/signature gate");
                }
            }
        }
        Console.WriteLine("PASS exact Nullable<SByteChoice> local/copy/join/initializer/default/Value bounded I1/sign-extension and explicit widening, host boundary/fault and repeat/maps/image");
    }
    public static void RunComputedParameters()
    {
        foreach(string expression in new[]{"unchecked(fallback+1)","fallback==0?-7:unchecked(fallback+1)","(fallback ^ -7) & 255"})RunParameter(computedDefault:expression);
    }
}
