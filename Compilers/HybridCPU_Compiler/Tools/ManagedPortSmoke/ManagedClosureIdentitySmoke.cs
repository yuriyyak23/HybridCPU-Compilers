using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;

internal static class ManagedClosureIdentitySmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).FullName!, "Read");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 Import(byte[] bytes, string source) => importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, bytes, source, [selector], []));
        ManagedCallGraphCompilationV1 baseline = Import(image, "closure-a");
        ManagedCallGraphCompilationV1 moved = Import(image, "closure-b");
        if (baseline.Status != RestrictedCilImportStatusV1.Success || baseline.Graph!.GraphDigest != moved.Graph?.GraphDigest)
            throw new Exception("Input closure must not depend on caller path/source label");
        ManagedCallGraphCompilationV1 changedBudget = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with
            { MaximumCallEdges = ScalarControlFlowV2ProfileContractV1.Default.Budgets.MaximumCallEdges - 1 })
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, image, "closure-a", [selector], []));
        if (changedBudget.Status != RestrictedCilImportStatusV1.Success ||
            changedBudget.Graph!.ContractDigest == baseline.Graph.ContractDigest ||
            !changedBudget.Graph.RootIdentities.SequenceEqual(baseline.Graph.RootIdentities))
            throw new Exception("Effective frontend budget identity failed to invalidate without reachability drift");
        Console.WriteLine("PASS managed frontend budget options identity with unchanged admitted roots; Qualification: no");
        using PEReader pe = new PEReader(new MemoryStream(image, writable: false));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition method = metadata.GetMethodDefinition(metadata.MethodDefinitions.Single(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Read"));
        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(section => method.RelativeVirtualAddress >= section.VirtualAddress &&
            method.RelativeVirtualAddress < section.VirtualAddress + section.SizeOfRawData);
        int body = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
        byte[] changed = (byte[])image.Clone();
        if ((changed[body] & 3) != 2 || changed[body + 1] != 0x1f || changed[body + 2] != 42)
            throw new Exception("Closure fixture requires exact ldc.i4.s42 body");
        changed[body + 2] = 43;
        ManagedCallGraphCompilationV1 mutation = Import(changed, "closure-a");
        if (mutation.Status != RestrictedCilImportStatusV1.Success || mutation.Graph!.GraphDigest == baseline.Graph.GraphDigest ||
            mutation.Graph.ContractDigest == baseline.Graph.ContractDigest ||
            !mutation.Graph.RootIdentities.SequenceEqual(baseline.Graph.RootIdentities))
            throw new Exception("Same-identity changed PE must invalidate graph input closure");
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ManagedBodyWorldModuleV1[] references =
        [
            new(File.ReadAllBytes(typeof(object).Assembly.Location), "corelib-reference"),
            new(File.ReadAllBytes(Path.Combine(runtime, "System.Runtime.dll")), "facade-reference")
        ];
        ManagedCallGraphCompilationV1 WithReferences(IReadOnlyList<ManagedBodyWorldModuleV1> modules) => importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, image, "closure-reference-order", [selector], [], MetadataOnlyModules: modules));
        ManagedCallGraphCompilationV1 referenceBaseline = WithReferences(references);
        ManagedCallGraphCompilationV1 reordered = WithReferences(references.Reverse().ToArray());
        ManagedCallGraphCompilationV1 removed = WithReferences([references[0]]);
        if (referenceBaseline.Status != RestrictedCilImportStatusV1.Success || reordered.Status != RestrictedCilImportStatusV1.Success ||
            removed.Status != RestrictedCilImportStatusV1.Success || referenceBaseline.Graph!.GraphDigest != reordered.Graph!.GraphDigest ||
            referenceBaseline.Graph.GraphDigest == removed.Graph!.GraphDigest)
            throw new Exception("Managed reference closure order/removal identity drift");
        Console.WriteLine("PASS managed reference closure reordered-input equality and reference-removal invalidation; Qualification: no");
        Console.WriteLine("PASS managed input closure source-label independence and same-method changed-PE invalidation; Qualification: no");
    }
}
