using HybridCPU.Compiler.Cil;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

internal static class EhIngressBudgetSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).FullName!, "CatchDivide");
        ManagedEhImportResultV1 positive = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportManagedEhPlan(image, selector);
        if (positive.Status != ManagedEhImportStatusV1.Success || positive.Plan is null)
            throw new Exception("EH budget positive: " + positive.Code + ":" + positive.Reason);
        RestrictedCilImporterV1 shared = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedBodyWorldV1 world = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            image, "eh-reader-reuse", [selector], []);
        ManagedCallGraphCompilationV1 oldGraph = shared.ImportBodyWorld(world);
        using CancellationTokenSource active = new CancellationTokenSource();
        ManagedCallGraphCompilationV1 currentGraph = shared.ImportBodyWorld(world, active.Token);
        if (oldGraph.Status != RestrictedCilImportStatusV1.Success ||
            System.Text.Json.JsonSerializer.Serialize(oldGraph) != System.Text.Json.JsonSerializer.Serialize(currentGraph))
            throw new Exception("EH borrowed-reader active result drift.");
        active.Cancel();
        try { shared.ImportBodyWorld(world, active.Token); throw new Exception("Cancelled EH graph returned."); }
        catch (OperationCanceledException error) when (error.CancellationToken == active.Token) { }
        if (System.Text.Json.JsonSerializer.Serialize(oldGraph) != System.Text.Json.JsonSerializer.Serialize(shared.ImportBodyWorld(world)))
            throw new Exception("EH reader cancellation/disposal contaminated reuse.");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(RestrictedCilImportBudgetsV1.Production with { MaximumMethodBodyBytes = 1 },
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedEhImportResultV1 first = importer.ImportManagedEhPlan(image, selector);
        ManagedEhImportResultV1 repeat = importer.ImportManagedEhPlan(image, selector);
        Console.WriteLine($"EH method byte budget=1: status={first.Status}; code={first.Code}");
        if (first.Status != ManagedEhImportStatusV1.BudgetExhausted || first.Plan is not null ||
            first.Code != "HCCIL2805" || first != repeat)
            throw new Exception("EH ingress ignored the deterministic method byte budget");
        using PEReader pe = new PEReader(new MemoryStream(image, writable: false));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition method = metadata.GetMethodDefinition(metadata.MethodDefinitions.Single(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "CatchDivide"));
        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(section => method.RelativeVirtualAddress >= section.VirtualAddress &&
            method.RelativeVirtualAddress < section.VirtualAddress + section.SizeOfRawData);
        int body = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
        int headerSize = (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(body)) >> 12) * 4;
        if ((image[body] & 3) != 3 || headerSize < 12) throw new Exception("EH fixture requires exact fat method header");
        int codeSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(body + 4));
        int table = (body + headerSize + codeSize + 3) & ~3;
        if ((image[table] & 0x3f) != 1) throw new Exception("EH fixture requires an exception section");
        bool fat = (image[table] & 0x40) != 0;
        int clause = table + 4;
        List<(string Name, byte[] Bytes)> mutations = new List<(string Name, byte[] Bytes)>();
        void Field(string name, int smallOffset, int fatOffset, int smallWidth, uint value)
        {
            byte[] bytes = (byte[])image.Clone();
            int offset = clause + (fat ? fatOffset : smallOffset);
            if (fat || smallWidth == 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
            else if (smallWidth == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
            else bytes[offset] = checked((byte)value);
            mutations.Add((name, bytes));
        }
        Field("zero-try-length", 4, 8, 1, 0);
        Field("zero-handler-length", 7, 16, 1, 0);
        Field("try-offset-outside-body", 2, 4, 2, ushort.MaxValue);
        Field("handler-offset-outside-body", 5, 12, 2, ushort.MaxValue);
        Field("catch-token-wrong-table", 8, 20, 4, 0x06000001);
        Field("catch-token-outside-table", 8, 20, 4, 0x0200ffff);
        RestrictedCilImporterV1 normal = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach ((string Name, byte[] Bytes) mutation in mutations)
        {
            ManagedEhImportResultV1 rejected = normal.ImportManagedEhPlan(mutation.Bytes, selector);
            ManagedEhImportResultV1 again = normal.ImportManagedEhPlan(mutation.Bytes, selector);
            if (rejected.Status == ManagedEhImportStatusV1.Success || rejected.Plan is not null ||
                string.IsNullOrWhiteSpace(rejected.Code) || rejected != again)
                throw new Exception("Malformed EH clause admission drift: " + mutation.Name);
            if (importer.ImportManagedEhPlan(mutation.Bytes, selector).Code != "HCCIL2805")
                throw new Exception("Method byte budget must precede clause semantic validation");
            Console.WriteLine($"EH mutation {mutation.Name} sha256={Convert.ToHexString(SHA256.HashData(mutation.Bytes))} status={rejected.Status} code={rejected.Code}");
        }
        if (normal.ImportManagedEhPlan(image, selector).Status != ManagedEhImportStatusV1.Success)
            throw new Exception("Malformed EH rejected input contaminated sequential reuse");
        Console.WriteLine($"EH malformed clauses={mutations.Count}; deterministic rejection, budget precedence and positive reuse");
        Console.WriteLine("PASS managed EH ingress method-byte budget, positive and deterministic rejection; Qualification: no");
    }
}
