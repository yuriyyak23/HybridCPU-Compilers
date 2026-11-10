using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;

internal static class AllocationTypeClosureSmoke
{
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(AllocationTypeClosureFixture).Assembly.Location);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "allocation-type-closure-smoke",
                [new(typeof(AllocationTypeClosureFixture).FullName!, nameof(AllocationTypeClosureFixture.Adjacent))], []));
        Check(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(';', graph.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));

        ManagedAllocationTypeUseV1[] uses = (graph.AllocationTypeUses ?? []).ToArray();
        Check(uses.Length == 2 && uses.Select(static use => use.CilOffset).Distinct().Count() == 2,
            "Adjacent newobj instructions must retain two exact allocation type uses.");
        ManagedTypeUniverseRowV1[] expected = graph.TypeUniverse!.Rows.Where(row =>
            row.StableIdentity is nameof(AllocationClosureFirst) or nameof(AllocationClosureSecond)).ToArray();
        Check(expected.Length == 2 && uses.Select(static use => (use.TypeId, use.TypeHandle)).Order()
                .SequenceEqual(expected.Select(static row => (row.TypeId, row.TypeHandle)).Order()),
            "Allocation uses must preserve each exact universe TypeId/TypeHandle through neighboring constructor calls.");

        ManagedDispatchTypeObjectArtifactV1 metadata = ManagedDispatchTypeObjectV1.Emit(graph);
        Check(expected.All(row => metadata.Rows.Any(imageRow => imageRow.Descriptor.TypeId == row.TypeId &&
                imageRow.TypeHandle == row.TypeHandle)),
            "Every lowered object allocation must enter the image TypeDescriptor closure.");
        HybridCpuStaticLinkArtifactV1 linked = new HybridCpuStaticLinkerV1().Link(
            [new(ManagedDispatchTypeObjectV1.ModuleIdentity, metadata.ObjectArtifact.Bytes)]);
        Check(linked.Status == HybridCpuLinkStatusV1.Success,
            string.Join(';', linked.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        HybridCpuManagedTypeRegistrationV1[] registrations = ManagedDispatchTypeObjectV1.Registrations(metadata, linked);
        Dictionary<ulong, ulong> handles = graph.TypeUniverse.Rows.ToDictionary(
            static row => row.TypeId, static row => row.TypeHandle);
        HybridCpuManagedTypeSystemBuildV1 loaded = HybridCpuManagedImageTypeLoaderV1.Load(
            linked.ImageBytes, registrations, handles);
        Check(loaded.IsSuccess && expected.All(row => loaded.TypeSystem!.TypeHandle(row.TypeId) == row.TypeHandle),
            "Loader must own the exact class descriptors and handles used by adjacent allocations.");
        Console.WriteLine("PASS allocation TypeDescriptor closure for adjacent newobj and constructor calls");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal static class AllocationTypeClosureFixture
{
    public static int Adjacent()
    {
        AllocationClosureFirst first = new AllocationClosureFirst(17);
        AllocationClosureSecond second = new AllocationClosureSecond(first, 25);
        return first.Value + second.Value;
    }
}

internal sealed class AllocationClosureFirst
{
    public AllocationClosureFirst(int value) => Value = value;
    public int Value;
}

internal sealed class AllocationClosureSecond
{
    public AllocationClosureSecond(AllocationClosureFirst first, int value) => Value = first.Value + value;
    public int Value;
}
