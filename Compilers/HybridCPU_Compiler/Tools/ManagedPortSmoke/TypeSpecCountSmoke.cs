using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;

internal static class TypeSpecCountSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(TypeSpecCountFixture).Assembly.Location);
        using PEReader pe = new PEReader(new MemoryStream(image));
        MetadataReader metadata = pe.GetMetadataReader();
        MemberReference member = metadata.GetMemberReference(metadata.MemberReferences.Single(handle =>
            metadata.GetString(metadata.GetMemberReference(handle).Name) == nameof(TypeSpecCountBox<int>.ReadTypeSpec) &&
            metadata.GetMemberReference(handle).Parent.Kind == HandleKind.TypeSpecification));
        BlobHandle signature = metadata.GetTypeSpecification((TypeSpecificationHandle)member.Parent).Signature;
        byte[] bytes = metadata.GetBlobBytes(signature);
        int relative = 2;
        relative += bytes[relative] < 0x80 ? 1 : bytes[relative] < 0xc0 ? 2 : 4;
        if (bytes[0] != 0x15 || bytes[relative] != 1) throw new Exception("TypeSpec count fixture drift.");
        int blob = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(signature);
        int prefix = image[blob] < 0x80 ? 1 : image[blob] < 0xc0 ? 2 : 4;
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(TypeSpecCountFixture).FullName!, nameof(TypeSpecCountFixture.CallTypeSpec));
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 Import(byte[] input) => importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, input, "typespec-count", [selector], []));
        ManagedCallGraphCompilationV1 baseline = Import(image);
        if (baseline.Status != RestrictedCilImportStatusV1.Success || baseline.Graph is null)
            throw new Exception("Closed TypeSpec positive rejected.");
        foreach (byte count in new byte[] { 2, 127 })
        {
            byte[] input = (byte[])image.Clone(); input[blob + prefix + relative] = count;
            ManagedCallGraphCompilationV1 first = Import(input); ManagedCallGraphCompilationV1 repeat = Import(input);
            if (first.Status != RestrictedCilImportStatusV1.Unsupported || first.Graph is not null || first.Methods.Count != 0 ||
                !first.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD1002") || !first.Diagnostics.SequenceEqual(repeat.Diagnostics))
                throw new Exception("TypeSpec count admission drift.");
        }
        ManagedCallGraphCompilationV1 reused = Import(image);
        if (reused.Status != RestrictedCilImportStatusV1.Success ||
            !baseline.Methods.Select(m => m.Identity.StableIdentity).SequenceEqual(reused.Methods.Select(m => m.Identity.StableIdentity)))
            throw new Exception("TypeSpec context identity/reuse drift.");
        Console.WriteLine("PASS real PE constructed TypeSpec count byte admission, exact contexts, deterministic rejection and reuse");
    }
}

public class TypeSpecCountBox<T> { public static T ReadTypeSpec(T value) => value; }
public static class TypeSpecCountFixture { public static int CallTypeSpec(int value) => TypeSpecCountBox<int>.ReadTypeSpec(value); }
