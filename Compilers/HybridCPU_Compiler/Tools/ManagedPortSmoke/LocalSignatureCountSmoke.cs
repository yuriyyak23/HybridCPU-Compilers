using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;
internal static class LocalSignatureCountSmoke { public static void Run() {
        PersistedAssemblyBuilder a =new PersistedAssemblyBuilder(new AssemblyName("LocalCount"),typeof(object).Assembly);
        TypeBuilder t =a.DefineDynamicModule("fixture").DefineType("Fixture",TypeAttributes.Public);
        MethodBuilder m =t.DefineMethod("Read",MethodAttributes.Public|MethodAttributes.Static,typeof(int),[]);
        ILGenerator il =m.GetILGenerator();il.DeclareLocal(typeof(int));il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Stloc_0);il.Emit(OpCodes.Ldloc_0);il.Emit(OpCodes.Ret);
t.CreateType();using MemoryStream stream =new MemoryStream();a.Save(stream);byte[] image=stream.ToArray();
using PEReader pe =new PEReader(new MemoryStream(image)); MetadataReader md =pe.GetMetadataReader();
        MethodDefinition method =md.GetMethodDefinition(md.MethodDefinitions.Single(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Read"));
        BlobHandle sig =md.GetStandaloneSignature(pe.GetMethodBody(method.RelativeVirtualAddress).LocalSignature).Signature;
if(!md.GetBlobBytes(sig).SequenceEqual(new byte[]{7,1,8}))throw new Exception("Fixture drift");
int offset=pe.PEHeaders.MetadataStartOffset+md.GetHeapMetadataOffset(HeapIndex.Blob)+MetadataTokens.GetHeapOffset(sig)+2;

        RestrictedCilMethodSelectorV1 selector =new RestrictedCilMethodSelectorV1("Fixture","Read");
        RestrictedCilImporterV1 importer =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilImportResultV1 baseline =importer.ImportImage(image,selector);
if(baseline.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Local positive rejected");
foreach(byte count in new byte[]{2,127}) {
            byte[] bytes =(byte[])image.Clone();bytes[offset]=count;
            RestrictedCilImportResultV1 first =importer.ImportImage(bytes,selector); RestrictedCilImportResultV1 repeated =importer.ImportImage(bytes,selector);
 if(first.Status!=RestrictedCilImportStatusV1.InvalidInput || first.Program is not null || first.Diagnostics.Count!=1 ||
 first.Diagnostics[0].Code!="HCCIL0029" || !first.Diagnostics.SequenceEqual(repeated.Diagnostics))throw new Exception("Local count rejection drift");
}
        RestrictedCilImportResultV1 reused =importer.ImportImage(image,selector);
if(reused.Status!=RestrictedCilImportStatusV1.Success || System.Text.Json.JsonSerializer.Serialize(reused.Program)!=System.Text.Json.JsonSerializer.Serialize(baseline.Program))throw new Exception("Local reuse drift");
        RestrictedCilImporterV1 budget =new RestrictedCilImporterV1(RestrictedCilImportBudgetsV1.Production with {MaximumPeBytes=image.Length-1},mode:RestrictedCilImportModeV1.ScalarControlFlowV2);
image[offset]=127;if(budget.ImportImage(image,selector).Diagnostics[0].Code!="HCCIL2001")throw new Exception("Local PE budget precedence drift");
Console.WriteLine("PASS real PE local count byte admission, HCCIL0029/repeat/reuse and PE budget");
} }
