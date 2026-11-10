using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target.Managed;

internal static class MetadataStringLiteralSmoke
{
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(MetadataStringLiteralFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(MetadataStringLiteralFixture).FullName!, "Read");
        ManagedCallGraphCompilationV1 Import() => importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "literal-table", [selector], []));
        ManagedCallGraphCompilationV1 graph = Import();
        if (graph.Status != RestrictedCilImportStatusV1.Success || graph.StringLiteralPlan is not { } plan ||
            plan.Bindings.Count != 2 || plan.Bindings[0].Literal != "" || plan.Bindings[1].Literal != "\0\ud800" ||
            plan.PlanDigest != Import().StringLiteralPlan!.PlanDigest)
            throw new Exception("Reachable literal table must preserve empty/NUL/unpaired UTF-16 and deterministic handles: " + string.Join(';', graph.Diagnostics));
        ScalarControlFlowV2LinkedProgramV1 link = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities[0]);
        if (link.Status != ScalarControlFlowV2LinkStatusV1.Success || link.RestrictedImage is not { RuntimeBootstrap: { } bootstrap } ||
            bootstrap.StringLiterals is not { Count: 2 } || bootstrap.StaticRoots.Count(root =>
                root.Identity.StartsWith("__hybridcpu_managed_string_literal_root_", StringComparison.Ordinal)) != 2 ||
            bootstrap.ManagedTypes is not [{ StableIdentity: "System.String" }] ||
            link.LinkedImage?.Symbols.Count(symbol => symbol.Name == HybridCpuManagedLdstrEmitterV1.Symbol) != 1 ||
            link.LinkedImage.Symbols.Count(symbol => symbol.Name == HybridCpuManagedStringLiteralTableV1.Symbol) != 1)
            throw new Exception("Literal table must be image-owned, rooted, descriptor-bound and resolved by native ldstr: " +
                string.Join(';', link.Diagnostics));
        if (!importer.ImportImage(pe, selector).Diagnostics.Any(d => d.Code == "HCCIL1411"))
            throw new Exception("Direct import without literal ownership remains fail-closed");
        RestrictedCilMethodSelectorV1 emptySelector = new RestrictedCilMethodSelectorV1(typeof(MetadataStringLiteralFixture).FullName!, "Empty");
        byte[] coreLib = File.ReadAllBytes(typeof(string).Assembly.Location);
        ManagedCallGraphCompilationV1 emptyGraph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            pe, "string-empty", [emptySelector], [], MetadataOnlyModules: [new(coreLib, "exact-corelib")]));
        if (emptyGraph.Status != RestrictedCilImportStatusV1.Success || emptyGraph.Methods.Single().Import.Program is not { } emptyProgram ||
            !emptyProgram.Instructions.Any(instruction => instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_ensure_type_initialized") ||
            !emptyProgram.Instructions.Any(instruction => instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_static_load_ref"))
            throw new Exception("System.String.Empty requires an exact CoreLib field, runtime-preinitialized type and rooted reference load: " +
                string.Join(';', emptyGraph.Diagnostics));
        if (!importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
                pe, "string-empty-negative", [emptySelector], [])).Diagnostics.Any(d => d.Code == "HCCIL1207"))
            throw new Exception("System.String.Empty without exact CoreLib metadata must remain fail closed.");
        Console.WriteLine("PASS reachable UTF-16 literals and exact runtime-preinitialized System.String.Empty root binding");
    }
}
public static class MetadataStringLiteralFixture
{
    public static string Read(bool empty) => empty ? "" : "\0\ud800";
    public static string Empty() => string.Empty;
    public static string Unreachable() => "not-reachable";
}
