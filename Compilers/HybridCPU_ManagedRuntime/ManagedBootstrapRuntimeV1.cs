using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedBootstrapStatusV1 : byte
{
    Success = 0,
    InvalidDescriptor = 1,
    VersionMismatch = 2,
    KernelFailure = 3,
    MissingHelper = 4,
    HelperFailure = 5
}

public sealed record HybridCpuManagedBootstrapResultV1(
    HybridCpuManagedBootstrapStatusV1 Status,
    HybridCpuStartupFailureKindV1 FailureKind,
    string Reason,
    IReadOnlyList<string> RegisteredMethods,
    IReadOnlyList<string> RegisteredStaticRoots,
    IReadOnlyList<string> RegisteredModuleInitializers,
    IReadOnlyList<string>? RegisteredTypes = null)
{
    public bool IsSuccess => Status == HybridCpuManagedBootstrapStatusV1.Success;
}

public delegate bool HybridCpuRuntimeHelperEntryV1(HybridCpuExecutionContextDescriptorV1 context);

public sealed class HybridCpuManagedBootstrapRuntimeV1
{
    private readonly string _expectedManagedAbiDigest;

    public const string BootstrapSchemaId = HybridCpuImageRuntimeBootstrapContractV1.SchemaId;
    public const int BootstrapSchemaMajor = HybridCpuImageRuntimeBootstrapContractV1.SchemaMajor;
    public const int BootstrapSchemaMinor = HybridCpuImageRuntimeBootstrapContractV1.SchemaMinor;

    public HybridCpuManagedBootstrapRuntimeV1(string expectedManagedAbiDigest)
    {
        if (!IsSha256(expectedManagedAbiDigest))
            throw new ArgumentException("Expected managed ABI digest must be an exact SHA-256 identity.", nameof(expectedManagedAbiDigest));
        _expectedManagedAbiDigest = expectedManagedAbiDigest;
    }

    public HybridCpuManagedBootstrapResultV1 Bootstrap(
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor,
        IHybridCpuRuntimeKernelV1 kernel,
        IReadOnlyDictionary<string, HybridCpuRuntimeHelperEntryV1> helpers,
        HybridCpuManagedTypeSystemV1? typeSystem = null,
        IReadOnlyDictionary<string, Func<bool>>? initializers = null,
        HybridCpuManagedStringRuntimeV1? stringRuntime = null,
        bool executeInitializers = true)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(helpers);
        if (descriptor.SchemaId != BootstrapSchemaId || descriptor.SchemaMajor != BootstrapSchemaMajor ||
            descriptor.SchemaMinor > BootstrapSchemaMinor)
            return Failure(HybridCpuManagedBootstrapStatusV1.VersionMismatch, HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                "Managed bootstrap schema version is unsupported.");
        if (!string.Equals(descriptor.PlatformContractDigest, HybridCpuPlatformContractV1.ContractDigest, StringComparison.Ordinal) ||
            !string.Equals(descriptor.ManagedAbiDigest, _expectedManagedAbiDigest, StringComparison.Ordinal) ||
            !IsSha256(descriptor.DescriptorDigest) ||
            !string.Equals(descriptor.DescriptorDigest, HybridCpuImageRuntimeBootstrapContractV1.ComputeDigest(descriptor), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(descriptor.RuntimeEntrySymbol) || string.IsNullOrWhiteSpace(descriptor.ManagedEntrySymbol))
            return Failure(HybridCpuManagedBootstrapStatusV1.InvalidDescriptor, HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                "Managed bootstrap descriptor identity or contract digest is invalid.");
        HybridCpuExecutionContextDescriptorV1? context = kernel.CurrentContext();
        if (context is null)
            return Failure(HybridCpuManagedBootstrapStatusV1.KernelFailure, HybridCpuStartupFailureKindV1.KernelFailure,
                "RuntimeKernel has no current execution context.");
        if (!WithinBudgetAndCanonicallyOrdered(descriptor))
            return Failure(HybridCpuManagedBootstrapStatusV1.InvalidDescriptor, HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                "Managed bootstrap metadata exceeds a deterministic budget or is not canonically ordered.");

        // Registration declarations are not proof that the image type table was installed.
        // Validate all of them before invoking any bootstrap helper or initializer.
        foreach (HybridCpuManagedTypeRegistrationV1 registration in descriptor.ManagedTypes ?? [])
        {
            HybridCpuManagedTypeDescriptorV1? installed = typeSystem?.Resolve(registration.TypeId);
            if (installed is null || installed.StableIdentity != registration.StableIdentity ||
                installed.DescriptorDigest != registration.DescriptorDigest ||
                installed.DescriptorDigest != HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(installed))
                return Failure(HybridCpuManagedBootstrapStatusV1.InvalidDescriptor,
                    HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                    $"Registered type '{registration.StableIdentity}' is not installed with its exact image descriptor.");
        }

        foreach (HybridCpuRuntimeHelperImportV1 import in descriptor.RuntimeHelpers)
        {
            if (!helpers.TryGetValue(import.Symbol, out HybridCpuRuntimeHelperEntryV1? helper))
            {
                if (import.Required)
                    return Failure(HybridCpuManagedBootstrapStatusV1.MissingHelper, HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                        $"Required runtime helper '{import.Symbol}' is missing.");
                continue;
            }
            // Operational helpers are bound here but are invoked by generated managed code
            // with their own ABI signatures. Only the bootstrap helper consumes the
            // execution-context descriptor during bootstrap.
            if (string.Equals(import.Symbol, "__hybridcpu_runtime_bootstrap", StringComparison.Ordinal) &&
                !helper(context))
                return Failure(HybridCpuManagedBootstrapStatusV1.HelperFailure, HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                    $"Runtime helper '{import.Symbol}' rejected bootstrap.");
        }

        if (descriptor.ModuleInitializers.Count != 0 && initializers is null)
            return Failure(HybridCpuManagedBootstrapStatusV1.MissingHelper,
                HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                "Registered module/type initializers require exact runtime initializer bindings.");
        if (initializers is not null)
        {
            HybridCpuManagedTypeInitializerRuntimeV1? typeInitializer = typeSystem is null ? null : new HybridCpuManagedTypeInitializerRuntimeV1(typeSystem);
            foreach (HybridCpuModuleInitializerRegistrationV1 registration in descriptor.ModuleInitializers)
            {
                if (!initializers.TryGetValue(registration.InitializerSymbol, out Func<bool>? initializer))
                    return Failure(HybridCpuManagedBootstrapStatusV1.MissingHelper,
                        HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                        $"Registered initializer '{registration.InitializerSymbol}' is missing.");
                if (!executeInitializers)
                    continue;
                HybridCpuManagedShapeResultV1 result = registration.TypeId is ulong typeId
                    ? typeInitializer?.EnsureInitialized(typeId, initializer) ??
                      new(HybridCpuManagedShapeStatusV1.InvalidType, "Type initializer execution requires the runtime type system.")
                    : initializer()
                        ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty)
                        : new(HybridCpuManagedShapeStatusV1.InitializationFailed, "Module initializer returned failure.");
                if (!result.IsSuccess)
                    return Failure(HybridCpuManagedBootstrapStatusV1.HelperFailure,
                        HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                        $"Initializer '{registration.InitializerSymbol}' failed: {result.Reason}");
            }
        }
        if ((descriptor.StringLiterals ?? []).Count != 0)
        {
            if (typeSystem is null || stringRuntime is null)
                return Failure(HybridCpuManagedBootstrapStatusV1.HelperFailure,
                    HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                    "String literal registration requires the runtime type system and string runtime.");
            foreach (HybridCpuManagedStringLiteralRegistrationV1 literal in descriptor.StringLiterals!)
            {
                ulong? typeHandle = typeSystem.TypeHandle(literal.StringTypeId);
                HybridCpuManagedShapeResultV1 registered = typeHandle is ulong handle
                    ? stringRuntime.RegisterLiteral(literal.LiteralHandle, handle, literal.Utf16Value)
                    : new(HybridCpuManagedShapeStatusV1.InvalidType, "String literal TypeId is absent.");
                if (!registered.IsSuccess)
                    return Failure(HybridCpuManagedBootstrapStatusV1.HelperFailure,
                        HybridCpuStartupFailureKindV1.RuntimeContractViolation,
                        $"String literal '{literal.Identity}' failed: {registered.Reason}");
            }
        }

        return new(HybridCpuManagedBootstrapStatusV1.Success, HybridCpuStartupFailureKindV1.None, string.Empty,
            descriptor.CodeManagerRecords.Select(static row => row.MethodIdentity).ToArray(),
            descriptor.StaticRoots.Select(static row => row.Identity)
                .Concat((descriptor.StringLiterals ?? []).Select(static row => row.Identity)).ToArray(),
            descriptor.ModuleInitializers.Select(static row => row.InitializerSymbol).ToArray(),
            (descriptor.ManagedTypes ?? []).Select(static row => row.StableIdentity).ToArray());
    }

    private static bool WithinBudgetAndCanonicallyOrdered(HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor) =>
        descriptor.RuntimeHelpers.Count <= HybridCpuPlatformContractV1.MaximumRuntimeHelpers &&
        descriptor.CodeManagerRecords.Count <= HybridCpuPlatformContractV1.MaximumCodeManagerRecords &&
        descriptor.StaticRoots.Count <= HybridCpuPlatformContractV1.MaximumStaticRoots &&
        descriptor.ModuleInitializers.Count <= HybridCpuPlatformContractV1.MaximumModuleInitializers &&
        IsOrderedUnique(descriptor.RuntimeHelpers.Select(static row => row.Symbol)) &&
        descriptor.RuntimeHelpers.All(static row => !string.IsNullOrWhiteSpace(row.Signature)) &&
        IsOrderedUnique(descriptor.CodeManagerRecords.Select(static row => row.MethodIdentity)) &&
        descriptor.CodeManagerRecords.All(static row => row.CodeStartOffsetBytes >= 0 && row.CodeSizeBytes > 0 &&
            IsSha256(row.GcInfoDigest) && IsSha256(row.UnwindInfoDigest)) &&
        IsOrderedUnique(descriptor.StaticRoots.Select(static row => row.Identity)) &&
        descriptor.StaticRoots.All(static row => row.Address != 0 && row.Size != 0 &&
            row.Address % HybridCpuPlatformContractV1.AddressSizeBytes == 0 &&
            row.Size % HybridCpuPlatformContractV1.AddressSizeBytes == 0 &&
            row.Address <= ulong.MaxValue - row.Size) &&
        descriptor.ModuleInitializers.All(static row => row.Order >= 0 &&
            !string.IsNullOrWhiteSpace(row.ModuleIdentity) && !string.IsNullOrWhiteSpace(row.InitializerSymbol)) &&
        descriptor.ModuleInitializers.Select(static row => row.Order).SequenceEqual(
            descriptor.ModuleInitializers.Select(static row => row.Order).Order()) &&
        IsOrderedUnique(descriptor.ModuleInitializers.Select(static row => $"{row.Order:D10}:{row.ModuleIdentity}:{row.InitializerSymbol}")) &&
        (descriptor.ManagedTypes ?? []).Count <= HybridCpuPlatformContractV1.MaximumManagedTypes &&
        (descriptor.ManagedTypes ?? []).Select(static row => row.TypeId).SequenceEqual(
            (descriptor.ManagedTypes ?? []).Select(static row => row.TypeId).Order()) &&
        (descriptor.ManagedTypes ?? []).Select(static row => row.TypeId).Distinct().Count() == (descriptor.ManagedTypes ?? []).Count &&
        (descriptor.ManagedTypes ?? []).All(static row => row.TypeId != 0 && !string.IsNullOrWhiteSpace(row.StableIdentity) &&
            IsSha256(row.DescriptorDigest) && row.MetadataOffsetBytes >= 0 && row.MetadataSizeBytes > 0) &&
        (descriptor.StringLiterals ?? []).Count <= HybridCpuPlatformContractV1.MaximumManagedStringLiterals &&
        (descriptor.StringLiterals ?? []).Aggregate(0L, static (total, row) => total + (row.Utf16Value?.Length ?? 0)) <= HybridCpuPlatformContractV1.MaximumManagedStringLiteralCodeUnits &&
        IsOrderedUnique((descriptor.StringLiterals ?? []).Select(static row => row.Identity)) &&
        descriptor.StaticRoots.Select(static row => row.Identity)
            .Concat((descriptor.StringLiterals ?? []).Select(static row => row.Identity))
            .Distinct(StringComparer.Ordinal).Count() == descriptor.StaticRoots.Count + (descriptor.StringLiterals ?? []).Count &&
        (descriptor.StringLiterals ?? []).Select(static row => row.LiteralHandle).All(static handle => handle != 0) &&
        (descriptor.StringLiterals ?? []).Select(static row => row.LiteralHandle).Distinct().Count() == (descriptor.StringLiterals ?? []).Count &&
        (descriptor.StringLiterals ?? []).All(static row => row.StringTypeId != 0 && row.Utf16Value is not null) &&
        (descriptor.EhMethods ?? []).Count <= HybridCpuPlatformContractV1.MaximumCodeManagerRecords &&
        IsOrderedUnique((descriptor.EhMethods ?? []).Select(static row => row.MethodIdentity)) &&
        (descriptor.EhMethods ?? []).All(row => row.CodeStartOffsetBytes >= 0 && row.CodeSizeBytes > 0 &&
            row.EhInfo is { Length: > 0 } && row.UnwindInfo is { Length: > 0 } &&
            descriptor.CodeManagerRecords.Any(code => code.MethodIdentity == row.MethodIdentity &&
                code.CodeStartOffsetBytes == row.CodeStartOffsetBytes && code.CodeSizeBytes == row.CodeSizeBytes));

    private static bool IsOrderedUnique(IEnumerable<string> values)
    {
        string[] materialized = values.ToArray();
        return materialized.All(static value => !string.IsNullOrWhiteSpace(value)) &&
            materialized.Distinct(StringComparer.Ordinal).Count() == materialized.Length &&
            materialized.SequenceEqual(materialized.Order(StringComparer.Ordinal));
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => char.IsAsciiHexDigit(character));

    private static HybridCpuManagedBootstrapResultV1 Failure(
        HybridCpuManagedBootstrapStatusV1 status,
        HybridCpuStartupFailureKindV1 kind,
        string reason) => new(status, kind, reason, [], [], [], []);
}
