#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Bindings;
using CircuitWeave.Frontend.Roslyn;
using CircuitWeave.HWIR;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;
using Microsoft.CodeAnalysis.CSharp;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Unqualified bridge capture and validated HWIR candidate, never accepted RTL or equivalence.</summary>
public sealed record BridgeHwirAddWordResult(
    BridgeSourceSnapshotV1 SourceSnapshot, string MetadataSha256, GoldenAddWordCandidate Candidate);

/// <summary>
/// Bounded AddWord bridge: freezes caller bytes and runtime metadata, captures the
/// existing Bridge source identity, then invokes the real pinned Roslyn compiler
/// and replays the scalar HWIR validator. No generated or manually authored RTL.
/// </summary>
public sealed class BridgeHwirAddWordCompiler
{
    /// <summary>Observe selected dispatch arms from exact captured bytes; does not elaborate Compute.</summary>
    public ScalarWordDispatchEvidence ObserveCapturedDispatch(byte[] aluSource, byte[] enumSource,
        string aluSha256, string enumSha256) =>
        ScalarWordDispatchReader.Read((byte[])aluSource.Clone(), (byte[])enumSource.Clone(), aluSha256, enumSha256);

    public BridgeHwirAddWordResult Compile(ReadOnlySpan<byte> originalSource,
        HardwareBindingEntry binding, HardwareBindingsToolchain toolchain,
        CancellationToken cancellationToken = default) => CompileCore(originalSource, binding, toolchain, false, cancellationToken, null, null);

    /// <summary>Produce a separately bound SubWord candidate, without changing AddWord identity.</summary>
    public BridgeHwirAddWordResult CompileSubWord(ReadOnlySpan<byte> originalSource,
        HardwareBindingEntry binding, HardwareBindingsToolchain toolchain,
        CancellationToken cancellationToken = default) => CompileCore(originalSource, binding, toolchain, true, cancellationToken, null, null);

    /// <summary>Produce a separately bound MultiplyWord candidate.</summary>
    public BridgeHwirAddWordResult CompileMultiplyWord(ReadOnlySpan<byte> source,
        HardwareBindingEntry binding, HardwareBindingsToolchain toolchain,
        CancellationToken cancellationToken = default) => CompileCore(source, binding, toolchain, false, cancellationToken, null, null, true);

    /// <summary>Compile captured MultiplyWord bytes without claiming a clean worktree.</summary>
    public BridgeHwirAddWordResult CompileCapturedMultiplyWord(ReadOnlySpan<byte> source,
        HardwareBindingEntry binding, HardwareBindingsToolchain toolchain, string baseCommit,
        string sourceSha256, CancellationToken cancellationToken = default) =>
        CompileCore(source, binding, toolchain, false, cancellationToken, baseCommit, sourceSha256, true);

    /// <summary>Compile exact captured working bytes; baseCommit is context, not a clean-worktree assertion.</summary>
    public BridgeHwirAddWordResult CompileCapturedWord(ReadOnlySpan<byte> originalSource,
        HardwareBindingEntry binding, HardwareBindingsToolchain toolchain, bool isSubWord,
        string baseCommit, string sourceSha256, CancellationToken cancellationToken = default) =>
        CompileCore(originalSource, binding, toolchain, isSubWord, cancellationToken, baseCommit, sourceSha256);

    private static BridgeHwirAddWordResult CompileCore(ReadOnlySpan<byte> originalSource,
        HardwareBindingEntry binding, HardwareBindingsToolchain toolchain, bool isSubWord,
        CancellationToken cancellationToken, string? baseCommit, string? sourceSha256, bool isMultiplyWord = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = originalSource.ToArray();
        // Run the pinned raw-byte admission first so malformed or changed Golden
        // input retains the frontend's existing refusal semantics.
        var core = File.ReadAllBytes(typeof(object).Assembly.Location);
        var metadataSha = Convert.ToHexString(SHA256.HashData(core)).ToLowerInvariant();
        var compiler = new GoldenAddWordCandidateCompiler();
        var candidate = isMultiplyWord ? (baseCommit is not null && sourceSha256 is not null
            ? compiler.CompileCapturedMultiplyWord(source, binding, toolchain, baseCommit, sourceSha256, cancellationToken, core)
            : compiler.CompileMultiplyWord(source, binding, toolchain, cancellationToken, core))
            : baseCommit is not null && sourceSha256 is not null
            ? compiler.CompileCapturedWord(source, binding, toolchain, isSubWord, baseCommit, sourceSha256, cancellationToken, core)
            : isSubWord ? compiler.CompileSubWord(source, binding, toolchain, cancellationToken, core)
            : compiler.Compile(source, binding, toolchain, cancellationToken, core);
        var text = new UTF8Encoding(false, true).GetString(source);
        var capture = BridgeSourceSnapshotV1.Capture(new BridgeSourceRequestV1(
            candidate.ValidatedHwir.Design.Modules.Single().Origin!.Assembly,
            baseCommit is null ? "roslyn-pinned-source-projection" : "roslyn-captured-working-tree",
            [new BridgeSourceTextV1(GoldenAddWordCandidateCompiler.GoldenPath, text)],
            [],
            [new BridgeRootSelectorV1(GoldenAddWordCandidateCompiler.GoldenType, isMultiplyWord ? "MultiplyWord" : isSubWord ? "SubWord" : "AddWord", null, [], []),
             new BridgeRootSelectorV1(GoldenAddWordCandidateCompiler.GoldenType, "SignExtendWord", null, [], [])],
            [], []), "candidate-only-scalar-hwir", typeof(GoldenAddWordCandidateCompiler).Assembly.GetName().Version?.ToString() ?? "unknown",
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown", cancellationToken);
        var sourceSha = Convert.ToHexString(SHA256.HashData(source));
        if (!capture.HasConsistentDigest() || capture.Documents.Single().Sha256 != sourceSha)
            throw new InvalidOperationException("Bridge capture does not match original source bytes.");
        // Snapshot v1 has a 16 MiB/reference budget; current CoreLib exceeds it.
        // Freeze and report its independent digest rather than silently omitting it.
        var replay = new ScalarCombinationalHardwareIrValidator().Validate(candidate.ValidatedHwir.Design, cancellationToken);
        if (!replay.Accepted || replay.Validated is null ||
            replay.Validated.CanonicalDigest != candidate.ValidatedHwir.CanonicalDigest)
            throw new InvalidOperationException("HardwareBridge HWIR replay differs from frontend output.");
        return new(capture, metadataSha, candidate);
    }
}
#endif
