using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;

internal static class NestedGenericCountSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(NestedGenericCountFixture).Assembly.Location);
        using PEReader pe = new PEReader(new MemoryStream(image));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodSpecification spec = metadata.GetMethodSpecification(Enumerable.Range(1, metadata.GetTableRowCount(TableIndex.MethodSpec))
            .Select(MetadataTokens.MethodSpecificationHandle).Single(handle => {
                byte[] signature = metadata.GetBlobBytes(metadata.GetMethodSpecification(handle).Signature);
                return signature.Length > 4 && signature[0] == 10 && signature[1] == 1 &&
                    signature[2] == 0x15 && signature[3] == 0x12 &&
                    metadata.GetMethodSpecification(handle).Method.Kind == HandleKind.MethodDefinition &&
                    metadata.GetString(metadata.GetMethodDefinition((MethodDefinitionHandle)
                        metadata.GetMethodSpecification(handle).Method).Name) == nameof(NestedGenericCountFixture.Identity);
            }));
        byte[] bytes = metadata.GetBlobBytes(spec.Signature);
        int relative = 4;
        relative += bytes[relative] < 0x80 ? 1 : bytes[relative] < 0xc0 ? 2 : 4;
        if (bytes[relative] != 1) throw new Exception("Nested generic count fixture drift.");
        int blob = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob) +
            MetadataTokens.GetHeapOffset(spec.Signature);
        int prefix = image[blob] < 0x80 ? 1 : image[blob] < 0xc0 ? 2 : 4;
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(NestedGenericCountFixture).FullName!, nameof(NestedGenericCountFixture.Call));
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 Import(byte[] input) => importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, input, "nested-generic-count", [selector], []));
        ManagedCallGraphCompilationV1 baseline = Import(image);
        if (baseline.Status != RestrictedCilImportStatusV1.Success || baseline.Graph is null)
            throw new Exception("Nested closed generic positive rejected: " + string.Join(';', baseline.Diagnostics));
        foreach (byte count in new byte[] { 2, 127 })
        {
            byte[] mutated = (byte[])image.Clone(); mutated[blob + prefix + relative] = count;
            ManagedCallGraphCompilationV1 first = Import(mutated); ManagedCallGraphCompilationV1 repeat = Import(mutated);
            if (first.Status != RestrictedCilImportStatusV1.Unsupported || first.Graph is not null ||
                first.Methods.Count != 0 || !first.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD1002") ||
                !first.Diagnostics.SequenceEqual(repeat.Diagnostics))
                throw new Exception("Nested generic count rejection drift.");
        }
        ManagedCallGraphCompilationV1 reused = Import(image);
        if (reused.Status != RestrictedCilImportStatusV1.Success ||
            !baseline.Methods.Select(m => m.Identity.StableIdentity).SequenceEqual(reused.Methods.Select(m => m.Identity.StableIdentity)))
            throw new Exception("Nested closed generic identity/reuse drift.");
        Console.WriteLine("PASS real PE nested generic count byte admission, closed identities, deterministic rejection and reuse");
    }
}

public class NestedGenericCountBox<T> { }
public static class NestedGenericCountFixture
{
    public static T Identity<T>(T value) => value;
    public static NestedGenericCountBox<int> Call(NestedGenericCountBox<int> value) => Identity<NestedGenericCountBox<int>>(value);
}
