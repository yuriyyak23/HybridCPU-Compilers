using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;

namespace HybridCPU.Compiler.Roslyn;

/// <summary>Exact output identity, not an input-cache key or reference-closure proof.</summary>
public sealed record CompanionArtifactIdentityV1(
    string Schema, string RoslynVersion, string CompilerVersion, string PeSha256, string PdbSha256)
{
    public const string SchemaV1 = "hybridcpu.source-companion-artifacts/v1";
    public string Digest => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, SerializerOptions)));
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        IgnoreReadOnlyProperties = true
    };
    internal static CompanionArtifactIdentityV1 Create(byte[] pe, byte[] pdb) => new(
        SchemaV1, typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(),
        HybridCPU.Compiler.Core.IR.HybridCpuCompilerContract.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), Hash(pe), Hash(pdb));
    public bool Matches(ReadOnlySpan<byte> pe, ReadOnlySpan<byte> pdb) =>
        Schema == SchemaV1 && string.Equals(PeSha256, Hash(pe), StringComparison.Ordinal) &&
        string.Equals(PdbSha256, Hash(pdb), StringComparison.Ordinal);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
