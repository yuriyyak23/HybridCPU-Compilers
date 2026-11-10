using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Cil;

public sealed record ExactAotGenericsContractV1(
    string SchemaId,
    int SchemaVersion,
    int MaximumReachableInstantiations,
    bool ExactBodyPerInstantiation,
    bool SupportsConstructedReferenceTypes,
    bool SupportsGenericSharing,
    bool SupportsRuntimeDictionaries,
    bool SupportsRuntimeCodeGeneration,
    bool SupportsOpenInstantiations,
    bool SupportsGenericConstraints,
    string ContractDigest)
{
    public const string Schema = "hybridcpu.exact-aot-generics/v1";

    public static ExactAotGenericsContractV1 Default { get; } = Create();

    public bool HasRuntimeAuthority => false;
    public bool HasIseAuthority => false;
    public bool OwnsReachability => false;

    private static ExactAotGenericsContractV1 Create()
    {
        int budget = ScalarControlFlowV2ProfileContractV1.Default.Budgets.MaximumReachableMethods;
        string text = string.Join('|', Schema, 1, budget,
            "exact-body=true", "constructed-reference-types=true", "sharing=false", "dictionaries=false",
            "runtime-codegen=false", "open=false", "constraints=false",
            "identity=definition+exact-type-arguments+exact-method-arguments",
            "ordering=ordinal-stable-identity", "backend=canonical-scfv2-hco-link-image");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new(Schema, 1, budget, true, true, false, false, false, false, false, digest);
    }
}
