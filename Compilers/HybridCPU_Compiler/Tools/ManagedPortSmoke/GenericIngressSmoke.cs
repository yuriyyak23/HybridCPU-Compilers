using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using HybridCPU.Compiler.Cil;

internal static class GenericIngressSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location);
        string type = typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).FullName!;
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 Import(byte[] bytes, RestrictedCilMethodSelectorV1 selector) =>
            importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, bytes,
                "generic-ingress-fixed", [selector], []));
        RestrictedCilMethodSelectorV1 caller = new RestrictedCilMethodSelectorV1(type, "CallIdentity");
        ManagedCallGraphCompilationV1 positive = Import(image, caller);
        if (positive.Status != RestrictedCilImportStatusV1.Success || positive.Graph is null)
            throw new Exception("Closed MethodSpec positive: " + string.Join(';', positive.Diagnostics));
        using PEReader pe = new PEReader(new MemoryStream(image, writable: false));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodSpecification spec = metadata.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(1));
        int blob = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(spec.Signature);
        int prefix = image[blob] < 0x80 ? 1 : image[blob] < 0xc0 ? 2 : 4;
        int signature = blob + prefix;
        if (image[signature] != 0x0a || image[signature + 1] != 1)
            throw new Exception("Generic fixture requires its exact single-argument MethodSpec blob");
        int checks = 1;
        foreach ((string, int, byte) mutation in new[] { ("spec-header", 0, (byte)0xff), ("spec-arity-two", 1, (byte)2), ("spec-arity", 1, (byte)0x7f), ("spec-type", 2, (byte)0xff) })
        {
            byte[] bytes = (byte[])image.Clone(); bytes[signature + mutation.Item2] = mutation.Item3;
            Reject(mutation.Item1, bytes, caller);
        }
        foreach (IReadOnlyList<RestrictedCilGenericArgumentV1> arguments in new IReadOnlyList<RestrictedCilGenericArgumentV1>[]
                 { [], [new("System.Int32", RestrictedCilTypeV1.Int32), new("System.Int32", RestrictedCilTypeV1.Int32)] })
            Reject("generic-root-arity-" + arguments.Count, image, new(type, "Identity", GenericMethodArguments: arguments));
        if (Import(image, caller).Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Malformed generic inputs contaminated importer reuse");
        checks++;
        Console.WriteLine($"PASS MethodSpec/generic ingress {checks} checks; malformed blobs/arity, closed positive and reuse; Qualification: no");

        void Reject(string name, byte[] bytes, RestrictedCilMethodSelectorV1 selector)
        {
            ManagedCallGraphCompilationV1 first = Import(bytes, selector); ManagedCallGraphCompilationV1 repeat = Import(bytes, selector);
            if (first.Status == RestrictedCilImportStatusV1.Success || first.Graph is not null || first.Methods.Count != 0 ||
                first.Diagnostics.Count == 0 || first.Status != repeat.Status || !first.Diagnostics.SequenceEqual(repeat.Diagnostics))
                throw new Exception("Malformed generic ingress rejection drift: " + name);
            if (name.StartsWith("spec-arity", StringComparison.Ordinal) &&
                (first.Status != RestrictedCilImportStatusV1.Unsupported ||
                 !first.Diagnostics.Any(diagnostic => diagnostic.Code == "HCSCF-BODYWORLD1002")))
                throw new Exception("MethodSpec count diagnostic precedence drift.");
            Console.WriteLine($"generic mutation {name} sha256={Convert.ToHexString(SHA256.HashData(bytes))} status={first.Status} codes={string.Join(',', first.Diagnostics.Select(d => d.Code))}");
            checks++;
        }
    }
}
