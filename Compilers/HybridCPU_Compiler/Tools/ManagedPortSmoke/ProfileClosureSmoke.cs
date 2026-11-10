using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;

internal static class ProfileClosureSmoke
{
    public static void Run(ManagedCallGraphCompilationV1 graph)
    {
        ManagedCompiledMethodV1 template = graph.Methods.Single();
        ManagedCompiledMethodV1 root = template with
        {
            Import = template.Import with
            {
                SourceProfileMarkers = [ManagedProfileClosureContractV1.Managed]
            }
        };
        (ManagedProfileClosureEvidenceV1? Evidence, string? Failure) positive = ManagedProfileClosureContractV1.Validate(
            [root], [root.Identity.StableIdentity], graph.Graph!.GraphDigest);
        Check(positive.Failure is null && positive.Evidence is
            {
                SchemaId: ManagedProfileClosureContractV1.SchemaId,
                HasRuntimeAuthority: false,
                EvidenceDigest.Length: 64
            } evidence && evidence.ReachableMethodIdentities.SequenceEqual([root.Identity.StableIdentity]) &&
            evidence.CoreLibMatrixDigests.Count == 1,
            "Profile evidence must be graph-bound, deterministic and non-authoritative.");

        ManagedCompiledMethodV1 unknown = root with
        {
            Import = root.Import with { SourceProfileMarkers = ["HybridCPU.Unknown"] }
        };
        Check(ManagedProfileClosureContractV1.Validate(
                [unknown], [unknown.Identity.StableIdentity], graph.Graph.GraphDigest).Failure?.StartsWith("HCPROFILE2001:", StringComparison.Ordinal) == true,
            "Unknown source profiles must fail closed.");

        ManagedCompiledMethodV1 forbiddenType = root with
        {
            Identity = root.Identity with
            {
                DeclaringType = "System.Reflection.Emit.DynamicMethod",
                StableIdentity = root.Identity.StableIdentity + ":forbidden-type"
            }
        };
        Check(ManagedProfileClosureContractV1.Validate(
                [forbiddenType], [forbiddenType.Identity.StableIdentity], graph.Graph.GraphDigest).Failure?.StartsWith("HCPROFILE2002:", StringComparison.Ordinal) == true,
            "The CoreLib subset matrix must reject a reachable forbidden type.");

        IrInstruction[] instructions = root.Import.Program!.Instructions.ToArray();
        instructions[0] = instructions[0] with
        {
            Annotation = instructions[0].Annotation with
            {
                BranchTargetSymbolName = "__hybridcpu_managed_alloc"
            }
        };
        ManagedCompiledMethodV1 forbiddenHelper = root with
        {
            Import = root.Import with
            {
                Program = root.Import.Program with { Instructions = instructions },
                SourceProfileMarkers = [ManagedProfileClosureContractV1.KernelNoHeap]
            }
        };
        Check(ManagedProfileClosureContractV1.Validate(
                [forbiddenHelper], [forbiddenHelper.Identity.StableIdentity], graph.Graph.GraphDigest).Failure?.StartsWith("HCPROFILE2003:", StringComparison.Ordinal) == true,
            "The profile matrix must reject a reachable forbidden runtime helper.");
        ManagedCompiledMethodV1 cachedArrayHelper = forbiddenHelper with { Import = forbiddenHelper.Import with {
            Program = forbiddenHelper.Import.Program! with { Instructions = instructions.Select((instruction, index) => index == 0
                ? instruction with { Annotation = instruction.Annotation with { BranchTargetSymbolName = "__hybridcpu_managed_array_empty" } }
                : instruction).ToArray() }
        } };
        Check(ManagedProfileClosureContractV1.Validate([cachedArrayHelper], [cachedArrayHelper.Identity.StableIdentity], graph.Graph.GraphDigest)
                .Failure?.StartsWith("HCPROFILE2003:", StringComparison.Ordinal) == true,
            "NoHeap must reject first-use cached-array allocation independently of source admission.");
        ManagedCompiledMethodV1 managedCachedArray = cachedArrayHelper with { Import = cachedArrayHelper.Import with { SourceProfileMarkers = [ManagedProfileClosureContractV1.Managed] } };
        Check(ManagedProfileClosureContractV1.Validate([managedCachedArray], [managedCachedArray.Identity.StableIdentity], graph.Graph.GraphDigest).Failure is null,
            "Managed profile retains cached-array helper admission.");
        Console.WriteLine("PASS graph-bound profile closure and deterministic CoreLib subset matrices");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
