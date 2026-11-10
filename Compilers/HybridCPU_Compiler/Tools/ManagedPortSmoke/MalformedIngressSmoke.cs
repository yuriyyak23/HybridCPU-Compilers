using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using HybridCPU.Compiler.Cil;

internal static class MalformedIngressSmoke
{
    public static void Run()
    {
        byte[] image = File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location);
        Console.WriteLine($"malformed-ingress input sha256={Convert.ToHexString(SHA256.HashData(image))}");
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).FullName!, "Read");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        if (importer.ImportImage(image, selector).Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Malformed ingress positive fixture rejected");
        using PEReader pe = new PEReader(new MemoryStream(image, writable: false));
        int metadata = pe.PEHeaders.MetadataStartOffset;
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c));
        List<(string Name, byte[] Bytes)> cases = new List<(string Name, byte[] Bytes)>();
        foreach (int length in new[] { 0, 1, 2, 16, 63, peOffset, peOffset + 3, metadata, metadata + 3 }.Distinct())
            cases.Add(($"truncate-{length}", image[..length]));
        void Mutate(string name, int offset, ReadOnlySpan<byte> replacement)
        {
            byte[] bytes = (byte[])image.Clone(); replacement.CopyTo(bytes.AsSpan(offset)); cases.Add((name, bytes));
        }
        Mutate("pe-signature", peOffset, new byte[4]);
        Mutate("metadata-signature", metadata, new byte[4]);
        Mutate("pe-offset-overflow", 0x3c, [0xff, 0xff, 0xff, 0x7f]);
        MetadataReader reader = pe.GetMetadataReader();
        MethodDefinition method = reader.GetMethodDefinition(reader.MethodDefinitions.Single(handle =>
            reader.GetString(reader.GetMethodDefinition(handle).Name) == "Read"));
        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(section => method.RelativeVirtualAddress >= section.VirtualAddress &&
            method.RelativeVirtualAddress < section.VirtualAddress + section.SizeOfRawData);
        int bodyOffset = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
        if ((image[bodyOffset] & 3) != 2) throw new Exception("Mutation fixture requires its exact tiny method header");
        Mutate("method-header", bodyOffset, [0]);
        Mutate("unknown-opcode", bodyOffset, [6, 0xff]);
        Mutate("truncated-call-operand", bodyOffset, [6, 0x28]);
        Mutate("invalid-call-token", bodyOffset, [26, 0x28, 0xff, 0xff, 0xff, 0x06, 0x2a]);
        Mutate("invalid-calli-signature-token", bodyOffset, [26, 0x29, 0xff, 0xff, 0xff, 0x11, 0x2a]);
        Mutate("invalid-function-pointer-token", bodyOffset, [30, 0xfe, 0x06, 0xff, 0xff, 0xff, 0x06, 0x2a]);
        int checks = 1;
        foreach ((string Name, byte[] Bytes) item in cases)
        {
            RestrictedCilImportResultV1 first = importer.ImportImage(item.Bytes, selector, item.Name);
            RestrictedCilImportResultV1 second = importer.ImportImage(item.Bytes, selector, item.Name);
            if (first.Status == RestrictedCilImportStatusV1.Success || first.Program is not null ||
                first.Diagnostics.Count == 0 || first.Status != second.Status ||
                !first.Diagnostics.SequenceEqual(second.Diagnostics))
                throw new Exception($"Malformed ingress rejection drift: {item.Name}");
            Console.WriteLine($"mutation {item.Name} sha256={Convert.ToHexString(SHA256.HashData(item.Bytes))} status={first.Status} codes={string.Join(',', first.Diagnostics.Select(d => d.Code))}");
            checks++;
        }
        if (importer.ImportImage(image, selector).Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Rejected ingress contaminated the reusable importer");
        checks++;
        RestrictedCilImporterV1 peBudget = new RestrictedCilImporterV1(RestrictedCilImportBudgetsV1.Production with { MaximumPeBytes = image.Length - 1 });
        byte[] malformedOverBudget = cases.Single(item => item.Name == "metadata-signature").Bytes;
        RestrictedCilImportResultV1 budgetResult = peBudget.ImportImage(malformedOverBudget, selector);
        if (budgetResult.Status != RestrictedCilImportStatusV1.BudgetExhausted || budgetResult.Program is not null ||
            !budgetResult.Diagnostics.Any(d => d.Code == "HCCIL2001"))
            throw new Exception("PE budget must reject before malformed metadata decoding");
        checks++;
        RestrictedCilImporterV1 bodyBudget = new RestrictedCilImporterV1(RestrictedCilImportBudgetsV1.Production with { MaximumMethodBodyBytes = 1 });
        RestrictedCilImportResultV1 bodyResult = bodyBudget.ImportImage(image, selector);
        if (bodyResult.Status != RestrictedCilImportStatusV1.BudgetExhausted || bodyResult.Program is not null ||
            !bodyResult.Diagnostics.Any(d => d.Code == "HCCIL2003"))
            throw new Exception("Exact method byte budget rejection drift");
        checks++;
        Console.WriteLine($"PASS malformed PE/metadata ingress {cases.Count} mutations / {checks} checks; deterministic rejection and session reuse; Qualification: no");
    }
}
