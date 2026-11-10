using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using System.Buffers;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class IngressCancellationSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).FullName!, "Read");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        using PEReader pe = new PEReader(new MemoryStream(image, writable: false));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition callMethod = metadata.GetMethodDefinition(metadata.MethodDefinitions.Single(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "CallIdentity"));
        byte[] callIl = pe.GetMethodBody(callMethod.RelativeVirtualAddress).GetILBytes()!;
        if (callIl.Length != 7 || callIl[1] != 0x28) throw new Exception("Snapshot fixture requires its exact call instruction");
        int callToken = BinaryPrimitives.ReadInt32LittleEndian(callIl.AsSpan(2));
        Dictionary<int, RestrictedCilManagedCallTargetV1> targets = new Dictionary<int, RestrictedCilManagedCallTargetV1>
        {
            [callToken] = new(callToken, "snapshot-target", [RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32)
        };
        RestrictedCilImporterV1 snapshotImporter = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            managedCallTargets: targets);
        RestrictedCilMethodSelectorV1 callSelector = selector with { MethodName = "CallIdentity" };
        RestrictedCilImportResultV1 snapshotBefore = snapshotImporter.ImportImage(image, callSelector);
        targets.Clear();
        RestrictedCilImportResultV1 snapshotAfter = snapshotImporter.ImportImage(image, callSelector);
        bool Bound(RestrictedCilImportResultV1 result) => result.Status == RestrictedCilImportStatusV1.Success &&
            result.Program!.Instructions.Any(i => i.Annotation.BranchTargetSymbolName == "snapshot-target");
        if (!Bound(snapshotBefore) || !Bound(snapshotAfter) || snapshotBefore.Provenance != snapshotAfter.Provenance ||
            !snapshotBefore.Diagnostics.SequenceEqual(snapshotAfter.Diagnostics))
            throw new Exception("Caller target-table mutation changed admitted managed-call binding");
        Console.WriteLine("PASS managed-call target constructor membership snapshot and stable ingress; Qualification: no");
        RestrictedCilImportResultV1 baseline = importer.ImportImage(image, selector);
        RestrictedCilImportResultV1 explicitNone = importer.ImportImage(image, selector, CancellationToken.None);
        if (baseline.Status != RestrictedCilImportStatusV1.Success || explicitNone.Status != baseline.Status ||
            baseline.Provenance != explicitNone.Provenance || !baseline.Diagnostics.SequenceEqual(explicitNone.Diagnostics))
            throw new Exception("Non-cancelled ingress default drift");
        byte[] Bytes(RestrictedCilImportResultV1 result)
        {
            IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(result.Program!);
            return HybridCpuPostBundleCompiler.Compile(new HybridCpuBundleFormer().BundleProgram(schedule), true, true).ProgramImage;
        }
        if (!Bytes(baseline).SequenceEqual(Bytes(explicitNone))) throw new Exception("Cancellation.None output drift");
        using CancellationTokenSource cancelled = new CancellationTokenSource(); cancelled.Cancel();
        foreach (byte[]? bytes in new[] { image, Array.Empty<byte>() })
        {
            bool denied = false;
            try { importer.ImportImage(bytes, selector, cancelled.Token); }
            catch (OperationCanceledException e) when (e.CancellationToken == cancelled.Token) { denied = true; }
            if (!denied) throw new Exception("Cancelled ingress must throw with the caller token before admission");
        }
        using CancellationTokenSource duringRead = new CancellationTokenSource();
        using CancellingInput managedInput = new CancellingInput(image, duringRead);
        bool stageDenied = false;
        try { importer.ImportImage(managedInput.Input, selector, duringRead.Token); }
        catch (OperationCanceledException e) when (e.CancellationToken == duringRead.Token) { stageDenied = true; }
        if (!stageDenied || managedInput.Reads == 0)
            throw new Exception("Cancellation requested during input hashing must be observed before successful mapping");
        RestrictedCilImportResultV1 after = importer.ImportImage(image, selector, CancellationToken.None);
        if (after.Status != baseline.Status || after.Provenance != baseline.Provenance || !Bytes(after).SequenceEqual(Bytes(baseline)))
            throw new Exception("Cancelled call contaminated sequential importer reuse");
        RestrictedCilMethodSelectorV1 root = selector with { MethodName = "CallIdentity" };
        ManagedBodyWorldV1 world = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule, image,
            "cancellation-body-world", [root], []);
        ManagedCallGraphCompilationV1 graphBaseline = importer.ImportBodyWorld(world);
        ManagedCallGraphCompilationV1 graphNone = importer.ImportBodyWorld(world, CancellationToken.None);
        if (graphBaseline.Status != RestrictedCilImportStatusV1.Success || graphNone.Graph?.GraphDigest != graphBaseline.Graph?.GraphDigest ||
            !graphNone.Methods.Select(m => m.Identity.StableIdentity).SequenceEqual(graphBaseline.Methods.Select(m => m.Identity.StableIdentity)))
            throw new Exception("Body-world None graph/order drift");
        void CancelGraph(ManagedBodyWorldV1 input, CancellationToken token)
        {
            bool denied = false;
            try { importer.ImportBodyWorld(input, token); }
            catch (OperationCanceledException e) when (e.CancellationToken == token) { denied = true; }
            if (!denied) throw new Exception("Cancelled body-world returned a graph result");
        }
        CancelGraph(world, cancelled.Token);
        using CancellationTokenSource graphCancellation = new CancellationTokenSource();
        using CancellingInput graphInput = new CancellingInput(image, graphCancellation);
        CancelGraph(world with { PeImage = graphInput.Input }, graphCancellation.Token);
        ManagedCallGraphCompilationV1 graphAfter = importer.ImportBodyWorld(world, CancellationToken.None);
        if (graphAfter.Graph?.GraphDigest != graphBaseline.Graph?.GraphDigest)
            throw new Exception("Body-world cancellation contaminated reuse");
        Console.WriteLine("PASS body-world cancellation pre-admission/mid-input, None graph/order equality and sequential reuse; Qualification: no");
        string path = typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location;
        RestrictedCilImportResultV1 fileBaseline = importer.ImportFile(path, selector);
        RestrictedCilImportResultV1 fileNone = importer.ImportFile(path, selector, CancellationToken.None);
        if (fileBaseline.Status != RestrictedCilImportStatusV1.Success || fileBaseline.Provenance != fileNone.Provenance ||
            !Bytes(fileBaseline).SequenceEqual(Bytes(fileNone))) throw new Exception("File None drift");
        bool fileDenied = false;
        try { importer.ImportFile(path, selector, cancelled.Token); }
        catch (OperationCanceledException e) when (e.CancellationToken == cancelled.Token) { fileDenied = true; }
        if (!fileDenied) throw new Exception("Precancelled file ingress returned a result");
        RestrictedCilMethodSelectorV1 ehSelector = selector with { MethodName = "CatchDivide" };
        ManagedEhImportResultV1 ehBaseline = importer.ImportManagedEhPlan(image, ehSelector);
        ManagedEhImportResultV1 ehNone = importer.ImportManagedEhPlan(image, ehSelector, CancellationToken.None);
        if (ehBaseline.Status != ManagedEhImportStatusV1.Success || ehBaseline.Plan?.ContractDigest != ehNone.Plan?.ContractDigest)
            throw new Exception("EH None plan drift");
        void CancelEh(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            bool denied = false;
            try { importer.ImportManagedEhPlan(bytes, ehSelector, token); }
            catch (OperationCanceledException e) when (e.CancellationToken == token) { denied = true; }
            if (!denied) throw new Exception("Cancelled EH ingress returned a plan");
        }
        CancelEh(image, cancelled.Token);
        using CancellationTokenSource ehCancellation = new CancellationTokenSource();
        using CancellingInput ehInput = new CancellingInput(image, ehCancellation);
        CancelEh(ehInput.Input, ehCancellation.Token);
        if (importer.ImportManagedEhPlan(image, ehSelector).Plan?.ContractDigest != ehBaseline.Plan?.ContractDigest ||
            importer.ImportFile(path, selector).Provenance != fileBaseline.Provenance)
            throw new Exception("EH/file cancellation contaminated sequential reuse");
        Console.WriteLine("PASS EH/file cancellation, None equivalence and sequential reuse; Qualification: no");
        Console.WriteLine("PASS ImportImage cancellation pre-admission, None bytes/provenance equality and sequential reuse; Qualification: no");
    }

    private sealed class CancellingInput(byte[] bytes, CancellationTokenSource cancellation) : MemoryManager<byte>
    {
        public int Reads { get; private set; }
        public ReadOnlyMemory<byte> Input => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan()
        {
            Reads++;
            cancellation.Cancel();
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
