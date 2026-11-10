using HybridCPU.Compiler.Cil;

internal static class SourceMapSmoke
{
    public static void Run()
    {
        string path = typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location;
        byte[] pe = File.ReadAllBytes(path), pdb = File.ReadAllBytes(Path.ChangeExtension(path, ".pdb"));
        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(pe, new(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).FullName!, "Read"));
        ManagedSourceMapV1 plain = ManagedSourceMapV1.Create(imported, pe);
        ManagedSourceMapV1 mapped = ManagedSourceMapV1.Create(imported, pe, pdb);
        if (plain.Rows.Count == 0 || plain.Rows.Any(r => r.Document is not null) ||
            mapped.Rows.All(r => r.Document is null) || mapped.Digest != ManagedSourceMapV1.Create(imported, pe, pdb).Digest ||
            !plain.Rows.Select(r => (r.MethodToken, r.IlOffset, r.IrIndex)).SequenceEqual(mapped.Rows.Select(r => (r.MethodToken, r.IlOffset, r.IrIndex))))
            throw new Exception("Source-map identity/correlation drift");
        foreach (byte[]? wrong in new[] { new byte[] { 1, 2, 3 }, File.ReadAllBytes(typeof(SourceMapSmoke).Assembly.Location.Replace(".dll", ".pdb")) })
        {
            bool denied = false;
            try { ManagedSourceMapV1.Create(imported, pe, wrong); } catch (ArgumentException) { denied = true; }
            if (!denied) throw new Exception("Corrupt/stale source-map PDB accepted");
        }
        bool peDenied = false;
        try { ManagedSourceMapV1.Create(imported, new byte[] { 1, 2, 3 }); } catch (ArgumentException) { peDenied = true; }
        if (!peDenied) throw new Exception("Source-map PE mismatch accepted");
        Console.WriteLine("PASS source-map token/IL/IR and matched Portable PDB document correlation, deterministic digest, stale/corrupt negatives; Qualification: no");
    }
}
