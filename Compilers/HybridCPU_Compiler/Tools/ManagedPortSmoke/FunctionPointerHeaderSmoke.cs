using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using HybridCPU.Compiler.Cil;

internal static class FunctionPointerHeaderSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(FunctionPointerHeaderFixture).Assembly.Location);
        using PEReader pe = new PEReader(new MemoryStream(image));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition method = metadata.GetMethodDefinition(metadata.MethodDefinitions.Single(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) == nameof(FunctionPointerHeaderFixture.Accept)));
        if (!metadata.GetBlobBytes(method.Signature).SequenceEqual(new byte[] { 0, 1, 1, 0x1b, 0, 0, 1 }))
            throw new Exception("Nested FNPTR fixture signature drift.");
        int offset = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob) +
            MetadataTokens.GetHeapOffset(method.Signature) + 1 + 4;
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(FunctionPointerHeaderFixture).FullName!, nameof(FunctionPointerHeaderFixture.Accept));
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilImportResultV1 baseline = importer.ImportImage(image, selector);
        if (baseline.Status != RestrictedCilImportStatusV1.Success) throw new Exception("Managed FNPTR positive rejected.");
        foreach (byte header in new byte[] { 0x80, 0x81, 0x90, 0xa0, 0xc0, 0x01, 0x20 })
        {
            byte[] bytes = (byte[])image.Clone(); bytes[offset] = header;
            RestrictedCilImportResultV1 first = importer.ImportImage(bytes, selector);
            RestrictedCilImportResultV1 repeat = importer.ImportImage(bytes, selector);
            RestrictedCilImportStatusV1 expected = (header & 0x80) != 0 ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.Unsupported;
            string code = expected == RestrictedCilImportStatusV1.InvalidInput ? "HCCIL0006" : "HCCIL1010";
            if (first.Status != expected || first.Program is not null || first.Diagnostics.Count != 1 ||
                first.Diagnostics[0].Code != code || !first.Diagnostics.SequenceEqual(repeat.Diagnostics))
                throw new Exception("Nested FNPTR header rejection drift.");
        }
        RestrictedCilImportResultV1 reused = importer.ImportImage(image, selector);
        foreach (int countOffset in new[] { offset - 3, offset + 1 })
        foreach (byte count in new byte[] { 2, 127 })
        {
            byte[] bytes = (byte[])image.Clone(); bytes[countOffset] = count;
                RestrictedCilImportResultV1 rejected = importer.ImportImage(bytes, selector);
                RestrictedCilImportResultV1 repeated = importer.ImportImage(bytes, selector);
            if (rejected.Status != RestrictedCilImportStatusV1.InvalidInput || rejected.Program is not null ||
                rejected.Diagnostics.Count != 1 || rejected.Diagnostics[0].Code != "HCCIL0006" ||
                !rejected.Diagnostics.SequenceEqual(repeated.Diagnostics))
                throw new Exception("Signature parameter-count byte admission drift.");
        }
        if (reused.Status != RestrictedCilImportStatusV1.Success ||
            JsonSerializer.Serialize(baseline.Program) != JsonSerializer.Serialize(reused.Program))
            throw new Exception("Nested FNPTR rejection contaminated importer reuse.");
        image[offset] = 0x80;
        RestrictedCilImporterV1 budget = new RestrictedCilImporterV1(RestrictedCilImportBudgetsV1.Production with { MaximumPeBytes = image.Length - 1 },
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        if (budget.ImportImage(image, selector).Diagnostics[0].Code != "HCCIL2001") throw new Exception("FNPTR PE budget precedence drift.");
        Console.WriteLine("PASS real PE nested FNPTR reserved-header admission, convention negatives, positive/repeat/reuse and budget");
    }
}

public static unsafe class FunctionPointerHeaderFixture
{
    public static void Accept(delegate* managed<void> pointer) { }
}
