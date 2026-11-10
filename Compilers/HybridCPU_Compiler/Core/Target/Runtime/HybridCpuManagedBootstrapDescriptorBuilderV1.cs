using System.Security.Cryptography;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Runtime;

public sealed record HybridCpuManagedBootstrapMethodInputV1(
    string Symbol,
    byte[] GcInfo,
    HybridCpuManagedUnwindRecordV1 Unwind,
    byte[]? FinalUnwindInfo = null,
    byte[]? EhInfo = null);

/// <summary>
/// Creates managed bootstrap registrations only from the final linked image and finalized
/// GC/unwind payloads. It has no CIL, IR, scheduling, allocation, frame-lowering or ISE authority.
/// </summary>
public sealed class HybridCpuManagedBootstrapDescriptorBuilderV1
{
    public HybridCpuImageRuntimeBootstrapDescriptorV1 Create(
        HybridCpuStaticLinkArtifactV1 finalLink,
        string runtimeEntrySymbol,
        string managedEntrySymbol,
        IReadOnlyList<HybridCpuManagedBootstrapMethodInputV1> methods,
        IReadOnlyList<string> runtimeHelperSymbols,
        IReadOnlyList<HybridCpuStaticRootRegistrationV1>? staticRoots = null,
        IReadOnlyList<HybridCpuModuleInitializerRegistrationV1>? moduleInitializers = null,
        IReadOnlyList<HybridCpuManagedTypeRegistrationV1>? managedTypes = null)
    {
        ArgumentNullException.ThrowIfNull(finalLink);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(runtimeHelperSymbols);
        if (finalLink.Status != HybridCpuLinkStatusV1.Success || finalLink.ImageBytes.Length == 0)
            throw new ArgumentException("A successful final linked image is required.", nameof(finalLink));
        if (methods.Count > HybridCpuPlatformContractV1.MaximumCodeManagerRecords ||
            methods.Any(static row => row is null || string.IsNullOrWhiteSpace(row.Symbol) || row.GcInfo is null || row.Unwind is null) ||
            methods.Select(static row => row.Symbol).Distinct(StringComparer.Ordinal).Count() != methods.Count)
            throw new ArgumentException("Managed method registrations are malformed or exceed deterministic budgets.", nameof(methods));
        if (runtimeHelperSymbols.Count > HybridCpuPlatformContractV1.MaximumRuntimeHelpers ||
            runtimeHelperSymbols.Any(string.IsNullOrWhiteSpace) ||
            runtimeHelperSymbols.Distinct(StringComparer.Ordinal).Count() != runtimeHelperSymbols.Count)
            throw new ArgumentException("Runtime helper imports are malformed, duplicated or exceed deterministic budgets.", nameof(runtimeHelperSymbols));

        HybridCpuCodeManagerRegistrationV1[] registrations = methods.Select(method =>
        {
            HybridCpuLinkedSymbolV1 symbol = ResolveFinalCodeSymbol(finalLink, method.Symbol);
            byte[] unwind = method.FinalUnwindInfo ?? HybridCpuManagedUnwindCodecV1.Encode(method.Unwind);
            return new HybridCpuCodeManagerRegistrationV1(method.Symbol,
                checked((int)(symbol.Address - finalLink.ImageBase)), checked((int)symbol.Size),
                Sha256(method.GcInfo), Sha256(unwind));
        }).ToArray();
        HybridCpuManagedEhMethodRegistrationV1[] ehRegistrations = methods
            .Where(static method => method.EhInfo is { Length: > 0 })
            .Select(method =>
            {
                HybridCpuLinkedSymbolV1 symbol = ResolveFinalCodeSymbol(finalLink, method.Symbol);
                byte[] unwind = method.FinalUnwindInfo ?? throw new ArgumentException(
                    $"EH method '{method.Symbol}' requires finalized unwind-v2 metadata.", nameof(methods));
                _ = HybridCpuManagedUnwindCodecV2.Decode(unwind);
                return new HybridCpuManagedEhMethodRegistrationV1(method.Symbol,
                    checked((int)(symbol.Address - finalLink.ImageBase)), checked((int)symbol.Size),
                    method.EhInfo!, unwind);
            }).ToArray();
        HybridCpuRuntimeHelperImportV1[] helpers = runtimeHelperSymbols.Select(symbol =>
        {
            HybridCpuRuntimeHelperV1? helper = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(symbol);
            if (helper?.Support != HybridCpuManagedAbiSupportV1.Supported)
                throw new ArgumentException($"Runtime helper '{symbol}' is not supported by the managed ABI.", nameof(runtimeHelperSymbols));
            return new HybridCpuRuntimeHelperImportV1(helper.Symbol, helper.Signature, Required: true);
        }).ToArray();

        ResolveFinalCodeSymbol(finalLink, runtimeEntrySymbol);
        ResolveFinalCodeSymbol(finalLink, managedEntrySymbol);
        HybridCpuManagedTypeRegistrationV1[] typeRegistrations = (managedTypes ?? [])
            .OrderBy(static row => row.TypeId).ToArray();
        if (typeRegistrations.Length > HybridCpuPlatformContractV1.MaximumManagedTypes ||
            typeRegistrations.Select(static row => row.TypeId).Distinct().Count() != typeRegistrations.Length ||
            typeRegistrations.Any(row => row.TypeId == 0 || string.IsNullOrWhiteSpace(row.StableIdentity) ||
                !IsSha256(row.DescriptorDigest) || row.MetadataOffsetBytes < 0 || row.MetadataSizeBytes <= 0 ||
                (ulong)row.MetadataOffsetBytes + (ulong)row.MetadataSizeBytes > (ulong)finalLink.ImageBytes.Length ||
                row.StaticRootIdentity is not null && !(staticRoots ?? []).Any(root =>
                    string.Equals(root.Identity, row.StaticRootIdentity, StringComparison.Ordinal))))
            throw new ArgumentException("Managed type registrations are malformed or outside the final linked image.", nameof(managedTypes));
        return HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, runtimeEntrySymbol, managedEntrySymbol,
            helpers, registrations, staticRoots, moduleInitializers, typeRegistrations, ehMethods: ehRegistrations);
    }

    private static HybridCpuLinkedSymbolV1 ResolveFinalCodeSymbol(HybridCpuStaticLinkArtifactV1 link, string identity)
    {
        HybridCpuLinkedSymbolV1[] matches = link.Symbols.Where(symbol =>
            string.Equals(symbol.Name, identity, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException($"Final code symbol '{identity}' must resolve exactly once.", nameof(identity));
        HybridCpuLinkedSymbolV1 symbol = matches[0];
        bool inCode = link.Sections.Any(section => section.Kind == HybridCpuObjectSectionKind.Code &&
            section.ModuleIdentity == symbol.ModuleIdentity && symbol.Address >= section.Address &&
            symbol.Size != 0 && symbol.Address <= ulong.MaxValue - symbol.Size &&
            symbol.Address + symbol.Size <= section.Address + section.Size);
        if (!inCode || symbol.Address < link.ImageBase || symbol.Size > int.MaxValue)
            throw new ArgumentException($"Final code symbol '{identity}' has no bounded linked code range.", nameof(identity));
        return symbol;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => char.IsAsciiHexDigit(character));
}
