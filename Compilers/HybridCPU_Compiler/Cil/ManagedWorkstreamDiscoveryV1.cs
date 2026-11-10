using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Cil;

public sealed record ManagedWorkstreamDiscoveryV1(
    string SchemaId,
    IReadOnlyList<string> RequiredWorkstreams,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> UnclassifiedRuntimeModules,
    string ContractDigest)
{
    public bool IsComplete => UnclassifiedRuntimeModules.Count == 0;
    public bool HasPublicationAuthority => false;
}

public static class ManagedWorkstreamDiscoveryContractV1
{
    public const string SchemaId = "hybridcpu.managed-workstream-discovery/v1";

    public static ManagedWorkstreamDiscoveryV1 Discover(
        ManagedCallGraphCompilationV1 compilation,
        ScalarControlFlowV2LinkedProgramV1 linked)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(linked);
        if (compilation.Status != RestrictedCilImportStatusV1.Success || compilation.Graph is null ||
            linked.Status != ScalarControlFlowV2LinkStatusV1.Success || linked.LinkedRuntimeModuleIdentities is null)
            throw new InvalidOperationException("HCWORK1001: workstream discovery requires the successful imported and post-link object worlds.");

        SortedSet<string> required = new SortedSet<string>(StringComparer.Ordinal);
        SortedSet<string> evidence = new SortedSet<string>(StringComparer.Ordinal);
        SortedSet<string> unclassified = new SortedSet<string>(StringComparer.Ordinal);
        void Require(string workstream, string source)
        {
            required.Add(workstream);
            evidence.Add($"{workstream}:{source}");
        }

        if (compilation.TypeUniverse is { Rows.Count: > 0 })
            Require("type-layout-object-references", $"type-universe={compilation.TypeUniverse.Rows.Count}");
        if (linked.MethodObjects.Any(static method => method.GcInfo is { Length: > 0 }))
            Require("gc-maps-safepoints", $"method-gc-records={linked.MethodObjects.Count(static method => method.GcInfo is { Length: > 0 })}");
        if (compilation.Graph.ExactGenericInstantiationCount > 0)
            Require("exact-aot-generics", $"exact-instantiations={compilation.Graph.ExactGenericInstantiationCount}");
        if (compilation.StringLiteralPlan is { Bindings.Count: > 0 })
            Require("utf16-string-literals", $"literal-bindings={compilation.StringLiteralPlan.Bindings.Count}");
        if (compilation.Methods.Any(static method => method.Identity.MethodName == ".cctor"))
            Require("static-type-initialization", "compiled-cctor");
        if (compilation.DispatchCallPlans is { Count: > 0 } || compilation.VirtualSlotPlans is { Count: > 0 })
            Require("virtual-interface-dispatch", "imported-dispatch-plan");
        if (compilation.Methods.Any(static method => method.Import.RequiresNativeExceptionTransfer) ||
            linked.MethodObjects.Any(static method => method.EhInfo is { Length: > 0 }))
            Require("eh-unwind", "native-eh-method-record");

        foreach (string module in linked.LinkedRuntimeModuleIdentities)
        {
            bool classified = false;
            void Map(string workstream)
            {
                Require(workstream, $"runtime-hco={module}");
                classified = true;
            }

            if (module.Contains(".array-", StringComparison.Ordinal) ||
                module.Contains(".newarr/", StringComparison.Ordinal) ||
                module.Contains(".initialize-array/", StringComparison.Ordinal) ||
                module.Contains(".string-from-utf16-array/", StringComparison.Ordinal))
                Map("szarray-core");
            if (module.Contains(".string-", StringComparison.Ordinal) || module.Contains(".ldstr/", StringComparison.Ordinal))
                Map("utf16-string-literals");
            if (module.Contains(".static-", StringComparison.Ordinal) || module.Contains(".ensure-type-initialized/", StringComparison.Ordinal))
                Map("static-type-initialization");
            if (module.Contains(".dispatch-metadata/", StringComparison.Ordinal) || module.Contains(".resolve-interface/", StringComparison.Ordinal))
                Map("virtual-interface-dispatch");
            if (module.Contains(".alloc/", StringComparison.Ordinal) || module.Contains(".newarr/", StringComparison.Ordinal) ||
                module.Contains(".array-empty/", StringComparison.Ordinal) || module.Contains(".string-concat", StringComparison.Ordinal) ||
                module.Contains(".string-from-utf16-array/", StringComparison.Ordinal))
                Map("deterministic-allocation");
            if (module.Contains(".guest-", StringComparison.Ordinal))
                Map("managed-unmanaged-interop");
            if (module.Contains(".eh-", StringComparison.Ordinal) && !module.Contains(".eh-dispatch-table/", StringComparison.Ordinal) ||
                module.Contains(".exception-", StringComparison.Ordinal) ||
                module.Contains(".rethrow/", StringComparison.Ordinal) || module.Contains(".leave-catch/", StringComparison.Ordinal) ||
                module.Contains(".endfinally/", StringComparison.Ordinal) ||
                module.Contains(".null-check/", StringComparison.Ordinal) ||
                module.Contains(".divide-", StringComparison.Ordinal) || module.Contains(".remainder-", StringComparison.Ordinal) ||
                module.Contains(".math-abs-", StringComparison.Ordinal))
                Map("eh-unwind");
            if (module.Contains(".dispatch-types/", StringComparison.Ordinal) || module.Contains(".isinst/", StringComparison.Ordinal) ||
                module.Contains(".null-check/", StringComparison.Ordinal) || module.Contains(".exception-", StringComparison.Ordinal) ||
                module.Contains(".argument-", StringComparison.Ordinal))
                Map("type-layout-object-references");
            // Scalar arithmetic has no independent managed workstream. These exact HCOs are
            // still retained as closure evidence; checked variants were classified as EH above.
            if (module.Contains(".math-max-", StringComparison.Ordinal) ||
                module.Contains(".process-exit/", StringComparison.Ordinal) ||
                module.Contains(".eh-dispatch-table/", StringComparison.Ordinal))
            {
                evidence.Add($"infrastructure:runtime-hco={module}");
                classified = true;
            }
            if (!classified)
                unclassified.Add(module);
        }

        string[] requiredRows = required.ToArray();
        string[] evidenceRows = evidence.ToArray();
        string[] unclassifiedRows = unclassified.ToArray();
        string digest = Hash(string.Join('|', SchemaId, compilation.Graph.GraphDigest, linked.OrderedObjectDigest,
            string.Join(',', requiredRows), string.Join(';', evidenceRows), string.Join(',', unclassifiedRows)));
        return new(SchemaId, requiredRows, evidenceRows, unclassifiedRows, digest);
    }

    public static bool ValidateArtifact(string graphDigest, string orderedObjectDigest,
        IReadOnlyList<string>? required, IReadOnlyList<string>? evidence,
        IReadOnlyList<string>? unclassified, string? digest)
    {
        if (required is null || evidence is null || unclassified is null || string.IsNullOrWhiteSpace(digest))
            return false;
        string[] requiredRows = required.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] evidenceRows = evidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] unclassifiedRows = unclassified.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!required.SequenceEqual(requiredRows, StringComparer.Ordinal) ||
            !evidence.SequenceEqual(evidenceRows, StringComparer.Ordinal) ||
            !unclassified.SequenceEqual(unclassifiedRows, StringComparer.Ordinal))
            return false;
        return string.Equals(digest, Hash(string.Join('|', SchemaId, graphDigest, orderedObjectDigest,
            string.Join(',', requiredRows), string.Join(';', evidenceRows), string.Join(',', unclassifiedRows))),
            StringComparison.Ordinal);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
