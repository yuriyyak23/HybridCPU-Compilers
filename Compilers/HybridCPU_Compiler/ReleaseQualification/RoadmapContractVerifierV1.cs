namespace HybridCPU.Compiler.Release;

public sealed record HybridCpuRoadmapContractCheckV1(bool IsValid, IReadOnlyList<string> Diagnostics, string Digest);

public static class HybridCpuRoadmapContractVerifierV1
{
    public static HybridCpuRoadmapContractCheckV1 Verify(
        string readme,
        string phase04,
        string phase13,
        string phase27)
    {
        ArgumentNullException.ThrowIfNull(readme);
        ArgumentNullException.ThrowIfNull(phase04);
        ArgumentNullException.ThrowIfNull(phase13);
        ArgumentNullException.ThrowIfNull(phase27);
        List<string> diagnostics = [];

        Require(readme, "Proven", "HCRD1001", diagnostics);
        Require(readme, "NotApplicable", "HCRD1002", diagnostics);
        Require(readme, "Unknown", "HCRD1003", diagnostics);
        Require(readme, "Unsupported", "HCRD1004", diagnostics);
        Require(readme, "Unknown`/`Unsupported` are never silently treated as zero", "HCRD1005", diagnostics);
        Require(readme, "ChosenII >= ProvenLowerBoundII", "HCRD1006", diagnostics);
        Require(readme, "FeasibleModuloPlacement(ChosenII)", "HCRD1007", diagnostics);
        Require(phase13, "Proven(value", "HCRD1008:Proven", diagnostics);
        Require(phase13, "NotApplicable(reason)", "HCRD1008:NotApplicable", diagnostics);
        Require(phase13, "Unknown(reason)", "HCRD1008:Unknown", diagnostics);
        Require(phase13, "Unsupported(reason)", "HCRD1008:Unsupported", diagnostics);

        const string nativeChain = "ASM/API -> Canonical IR -> schedule/bundles -> ASM -> object/binary where supported -> evidence -> scheduling-report -> provenance";
        Require(readme, nativeChain, "HCRD1010", diagnostics);
        Require(phase04, "Canonical IR -> schedule -> bundles -> ASM -> object/binary where supported -> evidence -> scheduling report -> provenance", "HCRD1011", diagnostics);
        foreach (string forbiddenDependency in new[] { "LLVMSharp", "Roslyn", "ILCompiler" })
        {
            Require(readme, forbiddenDependency, "HCRD1012:" + forbiddenDependency, diagnostics);
            Require(phase04, forbiddenDependency, "HCRD1013:" + forbiddenDependency, diagnostics);
        }

        // Keep runtime-owned type identifiers out of compiler source while still auditing the prose token.
        string runtimeLegalityType = string.Concat("Legality", "Decision");
        foreach (string authority in new[] { "SafetyVerifier", runtimeLegalityType, "replay/freshness", "execution", "publication", "commit", "retire" })
            Require(readme, authority, "HCRD1020:" + authority, diagnostics);
        Require(readme, "Wall-clock", "HCRD1021", diagnostics, StringComparison.OrdinalIgnoreCase);
        Require(phase27, "Wall-clock", "HCRD1022", diagnostics, StringComparison.OrdinalIgnoreCase);

        foreach (string dependency in new[]
        {
            "| 06 | Frontend-neutral Canonical IR and SchedulingRegion contracts | 04,05 |",
            "| 08A | TargetMachine core: DataLayout/register inventory/primitive ABI | 05,06 |",
            "| 07 | Virtual values, allocation constraints, liveness and pressure | 03,06,08A |",
            "| 08B | Full native ABI, memory model, stack/object/link/platform contract | 05–08A,07 |"
        })
            Require(readme, dependency, "HCRD1030", diagnostics);
        Require(readme, "Final physical/group register allocation occurs after region/loop/modulo transforms", "HCRD1031", diagnostics);

        string digest = HybridCpuReleaseQualificationV1.Hash(string.Join('|',
            HybridCpuReleaseQualificationV1.Hash(readme),
            HybridCpuReleaseQualificationV1.Hash(phase04),
            HybridCpuReleaseQualificationV1.Hash(phase13),
            HybridCpuReleaseQualificationV1.Hash(phase27),
            string.Join(';', diagnostics)));
        return new(diagnostics.Count == 0, diagnostics, digest);
    }

    private static void Require(
        string text,
        string required,
        string code,
        List<string> diagnostics,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (!text.Contains(required, comparison)) diagnostics.Add($"{code}: missing '{required}'.");
    }
}
