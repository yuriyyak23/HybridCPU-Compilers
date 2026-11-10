using HybridCPU.Compiler.Cil;

internal static class TypeInitializerCallGraphSmoke
{
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(TypeInitializerCallGraphFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            pe, "type-initializer-call-graph-smoke",
            [new(typeof(TypeInitializerCallGraphFixture).FullName!, nameof(TypeInitializerCallGraphFixture.Read))], []));

        if (graph.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception(string.Join(";", graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        if (!graph.Graph!.Edges.Any(edge => edge.IsTypeInitializerTarget &&
                edge.CallerIdentity.Contains(".Build", StringComparison.Ordinal) &&
                edge.CalleeIdentity.Contains(".cctor", StringComparison.Ordinal)))
            throw new Exception("The helper must retain its synthetic type-initializer reachability edge.");
        if (!graph.Graph.Edges.Any(edge => !edge.IsTypeInitializerTarget &&
                edge.CallerIdentity.Contains(".cctor", StringComparison.Ordinal) &&
                edge.CalleeIdentity.Contains(".Build", StringComparison.Ordinal)))
            throw new Exception("The cctor-to-helper managed call edge is missing.");
        if (graph.Graph.Sccs.Any(component => component.IsRecursive))
            throw new Exception("A non-reentrant type-initializer edge must not manufacture a recursive SCC.");
        if (TypeInitializerCallGraphFixture.Read() != 42)
            throw new Exception("CoreCLR type-initializer reference behavior changed.");

        Console.WriteLine("PASS type-initializer reachability edges are non-reentrant for recursion analysis");
    }
}

internal static class TypeInitializerCallGraphFixture
{
    public static readonly int Value = Build();

    private static int Build() => 42;

    public static int Read() => Value;
}
