using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;

internal static class ModuleAcquisitionSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).FullName!, "Read");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach (bool metadata in new[] { false, true })
        {
            ManagedBodyWorldModuleV1[] modules = [new(image, "first-valid"), new(new byte[] { 1, 2, 3, 4 }, "late-malformed")];
            ManagedBodyWorldV1 world = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule, image,
                "primary", [selector], [], DependencyModules: metadata ? null : modules, MetadataOnlyModules: metadata ? modules : null);
            ManagedCallGraphCompilationV1 first = importer.ImportBodyWorld(world); ManagedCallGraphCompilationV1 repeat = importer.ImportBodyWorld(world);
            if (first.Status != RestrictedCilImportStatusV1.InvalidInput || first.Graph is not null ||
                first.Methods.Count != 0 || !first.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD0003") ||
                !first.Diagnostics.SequenceEqual(repeat.Diagnostics))
                throw new Exception("Partial module failure diagnostic drift.");
        }
        // Observe disposal of an already acquired private context when the next acquisition throws.
        Type contextType = typeof(RestrictedCilImporterV1).GetNestedType("ManagedModuleContext", BindingFlags.NonPublic)!;
        Type collectionType = typeof(RestrictedCilImporterV1).GetNestedType("ManagedModuleContextCollection", BindingFlags.NonPublic)!;
        object context = Activator.CreateInstance(contextType, [new ReadOnlyMemory<byte>(image), "tracked"])!;
        IEnumerable Items() { yield return context; throw new InvalidOperationException("controlled-acquisition"); }
        object cast = typeof(Enumerable).GetMethod(nameof(Enumerable.Cast))!.MakeGenericMethod(contextType).Invoke(null, [Items()])!;
        try { Activator.CreateInstance(collectionType, [cast]); throw new Exception("Partial acquisition returned."); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException failure &&
            failure.Message == "controlled-acquisition") { }
        PEReader reader = (PEReader)contextType.GetProperty("Reader")!.GetValue(context)!;
        try { reader.GetMetadataReader(); throw new Exception("Acquired reader was not disposed."); }
        catch (ObjectDisposedException) { }
        using CancellationTokenSource cancelled = new CancellationTokenSource();
        object cancelledContext = Activator.CreateInstance(contextType, [new ReadOnlyMemory<byte>(image), "cancelled-tracked"])!;
        IEnumerable CancelledItems() { yield return cancelledContext; cancelled.Cancel(); }
        object cancelledCast = typeof(Enumerable).GetMethod(nameof(Enumerable.Cast))!.MakeGenericMethod(contextType)
            .Invoke(null, [CancelledItems()])!;
        try { Activator.CreateInstance(collectionType, [cancelledCast, cancelled.Token]); throw new Exception("Cancelled acquisition returned."); }
        catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException failure &&
            failure.CancellationToken == cancelled.Token) { }
        PEReader cancelledReader = (PEReader)contextType.GetProperty("Reader")!.GetValue(cancelledContext)!;
        try { cancelledReader.GetMetadataReader(); throw new Exception("Cancelled reader was not disposed."); }
        catch (ObjectDisposedException) { }
        foreach (bool metadata in new[] { false, true })
        {
            using CancellationTokenSource token = new CancellationTokenSource();
            CancellingModules rows = new CancellingModules(image, token);
            ManagedBodyWorldV1 world = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule, image,
                "cancel-primary", [selector], [], DependencyModules: metadata ? null : rows, MetadataOnlyModules: metadata ? rows : null);
            try { importer.ImportBodyWorld(world, token.Token); throw new Exception("Cancelled module opened."); }
            catch (OperationCanceledException error) when (error.CancellationToken == token.Token) { }
            if (rows.Reads != 2) throw new Exception("Module acquisition cancellation read drift.");
        }
        if (importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, image, "reuse", [selector], [])).Status !=
            RestrictedCilImportStatusV1.Success) throw new Exception("Partial acquisition contaminated reuse.");
        Console.WriteLine("PASS partial module acquisition disposal, late malformed dependency/metadata diagnostics and reuse");
    }

    private sealed class CancellingModules(byte[] image, CancellationTokenSource token) : IReadOnlyList<ManagedBodyWorldModuleV1>
    {
        private int _passes;
        public int Reads { get; private set; }
        public int Count => 2;
        public ManagedBodyWorldModuleV1 this[int index] => new(index == 0 ? image : new byte[] { 1, 2, 3, 4 }, $"module{index}");
        public IEnumerator<ManagedBodyWorldModuleV1> GetEnumerator()
        {
            int pass = ++_passes;
            for (int index = 0; index < 2; index++)
            {
                if (pass == 2) { Reads++; if (index == 1) token.Cancel(); }
                yield return this[index];
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
