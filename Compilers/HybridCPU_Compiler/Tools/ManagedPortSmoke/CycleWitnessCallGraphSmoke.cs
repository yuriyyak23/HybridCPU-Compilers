using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using System.Text;

internal static class CycleWitnessCallGraphSmoke
{
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(CycleWitnessFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            pe, "cycle-witness-call-graph-smoke",
            [new(typeof(CycleWitnessFixture).FullName!, nameof(CycleWitnessFixture.A))], []));

        IrFrontendDiagnosticV1 diagnostic = graph.Diagnostics.Single(item => item.Code == "HCSCF-RECURSION1001");
        if (!diagnostic.Message.Contains("Shortest cycles: #1 ", StringComparison.Ordinal) ||
            diagnostic.Message.Contains("; #2 ", StringComparison.Ordinal))
            throw new Exception("Rotations of one managed cycle must have one deterministic witness.");

        string bounded = RestrictedCilImporterV1.BoundGraphDiagnosticMessage(new string('\u0416', 4096));
        if (Encoding.UTF8.GetByteCount(bounded) > RestrictedCilImporterV1.MaximumGraphDiagnosticUtf8Bytes ||
            !bounded.EndsWith("...[diagnostic truncated to deterministic UTF-8 budget]", StringComparison.Ordinal))
            throw new Exception("Graph diagnostics must remain inside the deterministic UTF-8 transport budget.");

        Console.WriteLine("PASS recursive cycle witnesses are canonical and transport-bounded");
    }
}

internal static class CycleWitnessFixture
{
    public static int A(int value) => B(value) + 1;
    private static int B(int value) => C(value) + 1;
    private static int C(int value) => A(value) + 1;
}
