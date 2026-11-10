using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.Target;

public enum HybridCpuPlatformFactStatus : byte
{
    Supported = 0,
    Unsupported = 1,
    Unknown = 2,
    Invalid = 3
}

public enum HybridCpuPlatformCompatibility : byte
{
    Compatible = 0,
    UnsupportedVersion = 1,
    CoreTargetMismatch = 2,
    MachineDescriptionMismatch = 3,
    PlatformContractMismatch = 4,
    Unknown = 5
}

public enum HybridCpuObjectFormat : byte
{
    None = 0,
    Unknown = 255
}

public enum HybridCpuTlsModel : byte
{
    LocalExec = 0,
    InitialExec = 1,
    LocalDynamic = 2,
    GeneralDynamic = 3,
    Unknown = 255
}

public enum HybridCpuObjectSectionKind : byte
{
    Code = 0,
    ReadOnlyData = 1,
    WritableData = 2,
    ZeroFill = 3,
    ThreadLocalData = 4,
    Debug = 5,
    Unwind = 6,
    ExceptionHandling = 7,
    Unknown = 255
}

public enum HybridCpuSymbolBinding : byte
{
    Local = 0,
    Global = 1,
    Weak = 2,
    Unknown = 255
}

public enum HybridCpuSymbolVisibility : byte
{
    Hidden = 0,
    Default = 1,
    Unknown = 255
}

public enum HybridCpuRelocationKind : byte
{
    BundleRelativeSigned16 = 0,
    Absolute64 = 1,
    PcRelative32 = 2,
    ThreadLocal = 3,
    ManagedCallRelativeSigned16 = 4,
    ManagedCallPcRelativeHighSigned16 = 5,
    ManagedCallPcRelativeLowSigned16 = 6,
    Unknown = 255
}

public enum HybridCpuRelocationOwner : byte
{
    CompilerInternalControlFlow = 0,
    ObjectWriter = 1,
    Linker = 2,
    Unknown = 255
}

public sealed record HybridCpuNativeCallingConventionV1(
    string SchemaId,
    HybridCpuPlatformFactStatus FullFunctionAbi,
    IReadOnlyList<int> ArgumentRegisters,
    IReadOnlyList<int> ReturnRegisters,
    IReadOnlyList<int> CallerSavedRegisters,
    IReadOnlyList<int> CalleeSavedRegisters,
    HybridCpuPlatformFactStatus AggregatePassing,
    HybridCpuPlatformFactStatus VarArgs,
    HybridCpuPlatformFactStatus TailCalls,
    HybridCpuPlatformFactStatus SpecialContourCalls);

public sealed record HybridCpuStackFrameContractV1(
    HybridCpuPlatformFactStatus Support,
    int StackAlignmentBytes,
    int? StackPointerRegister,
    int? FramePointerRegister,
    int RedZoneBytes,
    HybridCpuPlatformFactStatus DynamicAllocation,
    HybridCpuPlatformFactStatus StackProbing);

public sealed record HybridCpuAtomicMemoryContractV1(
    IReadOnlyList<int> SupportedWidthsBits,
    IReadOnlyList<IrMemoryOrdering> SupportedAtomicOrderings,
    IrMemoryOrdering FenceOrdering,
    HybridCpuPlatformFactStatus VolatileSemantics,
    bool RequiresNaturalAlignment);

public sealed record HybridCpuObjectWriterFeatureV1(
    string Identity,
    int Version,
    HybridCpuPlatformFactStatus Support);

public sealed record HybridCpuRelocationFeatureV1(
    HybridCpuRelocationKind Kind,
    int Version,
    HybridCpuRelocationOwner Owner,
    HybridCpuPlatformFactStatus Support,
    bool AllowsAddend);

public sealed record HybridCpuObjectSectionRequestV1(
    string Name,
    HybridCpuObjectSectionKind Kind,
    int AlignmentBytes);

public sealed record HybridCpuObjectSymbolRequestV1(
    string Name,
    HybridCpuSymbolBinding Binding,
    HybridCpuSymbolVisibility Visibility,
    string? SectionName,
    bool IsDefinition,
    string? ComdatKey = null);

public sealed record HybridCpuObjectRelocationRequestV1(
    string SectionName,
    int OffsetBytes,
    HybridCpuRelocationKind Kind,
    string TargetSymbol,
    long Addend);

public sealed record HybridCpuObjectMetadataPlanV1(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    IReadOnlyList<HybridCpuObjectSectionRequestV1> Sections,
    IReadOnlyList<HybridCpuObjectSymbolRequestV1> Symbols,
    IReadOnlyList<HybridCpuObjectRelocationRequestV1> Relocations,
    string Digest);

public sealed record HybridCpuDebugOriginRecordV1(int InstructionIndex, IrSourceOriginChainV1 Origin);

public sealed record HybridCpuDebugMapResultV1(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    IReadOnlyList<HybridCpuDebugOriginRecordV1> Records,
    string Digest);

/// <summary>
/// Versioned Phase 08B target-platform facts. Unsupported facts remain explicit and cannot
/// be filled from the host, LLVM defaults, runtime state or a managed frontend.
/// </summary>
public sealed class HybridCpuTargetPlatformContractV1
{
    public const string SchemaId = "hybridcpu.target-platform";
    public const int SchemaMajor = 1;
    public const int SchemaMinor = 1;
    public const string MachineDescriptionDigest =
        "ee837ac7c7bc9c81ac254ca133b6b7233c0d2a857d8c8371f25b570b65aefe0d";

    private static readonly int[] AtomicWidths = [32, 64];
    private static readonly IrMemoryOrdering[] AtomicOrderings =
        [IrMemoryOrdering.Acquire, IrMemoryOrdering.Release, IrMemoryOrdering.AcquireRelease, IrMemoryOrdering.SequentiallyConsistent];
    private static readonly HybridCpuObjectWriterFeatureV1[] ObjectWriterFeatureTable =
    [
        new("object-container", 1, HybridCpuPlatformFactStatus.Unsupported),
        new("code-section", 1, HybridCpuPlatformFactStatus.Unsupported),
        new("data-sections", 1, HybridCpuPlatformFactStatus.Unsupported),
        new("cross-module-symbols", 1, HybridCpuPlatformFactStatus.Unsupported),
        new("comdat-duplicates", 1, HybridCpuPlatformFactStatus.Unsupported),
        new("debug-sections", 1, HybridCpuPlatformFactStatus.Unsupported),
        new("unwind-sections", 1, HybridCpuPlatformFactStatus.Unsupported),
        new("eh-sections", 1, HybridCpuPlatformFactStatus.Unsupported)
    ];
    private static readonly HybridCpuRelocationFeatureV1[] RelocationFeatureTable =
    [
        new(HybridCpuRelocationKind.BundleRelativeSigned16, 1,
            HybridCpuRelocationOwner.CompilerInternalControlFlow, HybridCpuPlatformFactStatus.Supported, AllowsAddend: false),
        new(HybridCpuRelocationKind.Absolute64, 1,
            HybridCpuRelocationOwner.ObjectWriter, HybridCpuPlatformFactStatus.Unsupported, AllowsAddend: true),
        new(HybridCpuRelocationKind.PcRelative32, 1,
            HybridCpuRelocationOwner.Linker, HybridCpuPlatformFactStatus.Unsupported, AllowsAddend: true),
        new(HybridCpuRelocationKind.ThreadLocal, 1,
            HybridCpuRelocationOwner.Linker, HybridCpuPlatformFactStatus.Unsupported, AllowsAddend: true)
    ];

    public static HybridCpuTargetPlatformContractV1 Default { get; } = new();

    private HybridCpuTargetPlatformContractV1()
    {
        CoreTargetContractDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        CallingConvention = new(
            "hybridcpu.native-calling-convention/v1",
            HybridCpuPlatformFactStatus.Unsupported,
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(),
            HybridCpuPlatformFactStatus.Unsupported,
            HybridCpuPlatformFactStatus.Unsupported,
            HybridCpuPlatformFactStatus.Unsupported,
            HybridCpuPlatformFactStatus.Unsupported);
        StackFrame = new(
            HybridCpuPlatformFactStatus.Unsupported,
            StackAlignmentBytes: 0,
            StackPointerRegister: null,
            FramePointerRegister: null,
            RedZoneBytes: 0,
            DynamicAllocation: HybridCpuPlatformFactStatus.Unsupported,
            StackProbing: HybridCpuPlatformFactStatus.Unsupported);
        AtomicMemory = new(
            Array.AsReadOnly(AtomicWidths),
            Array.AsReadOnly(AtomicOrderings),
            IrMemoryOrdering.SequentiallyConsistent,
            HybridCpuPlatformFactStatus.Supported,
            RequiresNaturalAlignment: true);
        ObjectWriterFeatures = Array.AsReadOnly(ObjectWriterFeatureTable);
        RelocationFeatures = Array.AsReadOnly(RelocationFeatureTable);
        ContractDigest = ComputeDigest();
    }

    public string CoreTargetContractDigest { get; }
    public HybridCpuNativeCallingConventionV1 CallingConvention { get; }
    public HybridCpuStackFrameContractV1 StackFrame { get; }
    public HybridCpuAtomicMemoryContractV1 AtomicMemory { get; }
    public HybridCpuObjectFormat ObjectFormat => HybridCpuObjectFormat.None;
    public HybridCpuPlatformFactStatus ThreadLocalStorage => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus RuntimeHelperAbi => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus ExecutableStartup => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus DynamicLibraries => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus NativeOriginMapping => HybridCpuPlatformFactStatus.Supported;
    public HybridCpuPlatformFactStatus ObjectDebugSections => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus UnwindSemantics => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus ExceptionHandlingSemantics => HybridCpuPlatformFactStatus.Unsupported;
    public IReadOnlyList<HybridCpuObjectWriterFeatureV1> ObjectWriterFeatures { get; }
    public IReadOnlyList<HybridCpuRelocationFeatureV1> RelocationFeatures { get; }
    public string ContractDigest { get; }

    public HybridCpuPlatformCompatibility CheckCompatibility(
        int schemaMajor,
        string coreTargetContractDigest,
        string machineDescriptionDigest,
        string platformContractDigest)
    {
        if (schemaMajor != SchemaMajor) return HybridCpuPlatformCompatibility.UnsupportedVersion;
        if (string.IsNullOrWhiteSpace(coreTargetContractDigest) ||
            string.IsNullOrWhiteSpace(machineDescriptionDigest) ||
            string.IsNullOrWhiteSpace(platformContractDigest))
            return HybridCpuPlatformCompatibility.Unknown;
        if (!string.Equals(coreTargetContractDigest, CoreTargetContractDigest, StringComparison.Ordinal))
            return HybridCpuPlatformCompatibility.CoreTargetMismatch;
        if (!string.Equals(machineDescriptionDigest, MachineDescriptionDigest, StringComparison.Ordinal))
            return HybridCpuPlatformCompatibility.MachineDescriptionMismatch;
        return string.Equals(platformContractDigest, ContractDigest, StringComparison.Ordinal)
            ? HybridCpuPlatformCompatibility.Compatible
            : HybridCpuPlatformCompatibility.PlatformContractMismatch;
    }

    public HybridCpuPlatformFactStatus ValidateAtomic(
        int widthBits,
        int alignmentBytes,
        IrMemoryOrdering ordering,
        IrAddressSpaceIdentity addressSpace,
        bool isVolatile)
    {
        if (widthBits <= 0 || alignmentBytes <= 0 ||
            ordering == IrMemoryOrdering.Unknown || addressSpace == IrAddressSpaceIdentity.Unknown)
            return HybridCpuPlatformFactStatus.Unknown;
        if (HybridCpuTargetMachineContractV1.Default.GetAddressSpaceSupport(addressSpace) != HybridCpuTargetFactSupport.Supported)
            return HybridCpuPlatformFactStatus.Unsupported;
        if (!AtomicWidths.Contains(widthBits) || !AtomicOrderings.Contains(ordering))
            return HybridCpuPlatformFactStatus.Unsupported;
        return alignmentBytes >= widthBits / 8 && alignmentBytes % (widthBits / 8) == 0
            ? HybridCpuPlatformFactStatus.Supported
            : HybridCpuPlatformFactStatus.Unsupported;
    }

    public HybridCpuPlatformFactStatus ValidateFence(IrMemoryOrdering ordering) => ordering switch
    {
        IrMemoryOrdering.AcquireRelease or IrMemoryOrdering.SequentiallyConsistent => HybridCpuPlatformFactStatus.Supported,
        IrMemoryOrdering.Unknown => HybridCpuPlatformFactStatus.Unknown,
        _ => HybridCpuPlatformFactStatus.Unsupported
    };

    public HybridCpuPlatformFactStatus ValidateTls(HybridCpuTlsModel model) =>
        model == HybridCpuTlsModel.Unknown
            ? HybridCpuPlatformFactStatus.Unknown
            : HybridCpuPlatformFactStatus.Unsupported;

    public HybridCpuPlatformFactStatus ResolveRuntimeHelper(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return HybridCpuPlatformFactStatus.Invalid;
        return HybridCpuPlatformFactStatus.Unsupported;
    }

    public HybridCpuPlatformFactStatus ValidateInternalControlRelocation(long displacement, ulong legacyTargetSideband) =>
        legacyTargetSideband == 0 && displacement is >= short.MinValue and <= short.MaxValue
            ? HybridCpuPlatformFactStatus.Supported
            : HybridCpuPlatformFactStatus.Invalid;

    public HybridCpuObjectMetadataPlanV1 PlanObjectMetadata(
        IReadOnlyList<HybridCpuObjectSectionRequestV1> sections,
        IReadOnlyList<HybridCpuObjectSymbolRequestV1> symbols,
        IReadOnlyList<HybridCpuObjectRelocationRequestV1> relocations)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(relocations);
        HybridCpuObjectSectionRequestV1[] orderedSections = sections
            .OrderBy(static item => item.Kind).ThenBy(static item => item.Name, StringComparer.Ordinal).ToArray();
        HybridCpuObjectSymbolRequestV1[] orderedSymbols = symbols
            .OrderBy(static item => item.Name, StringComparer.Ordinal).ThenBy(static item => item.Binding).ToArray();
        HybridCpuObjectRelocationRequestV1[] orderedRelocations = relocations
            .OrderBy(static item => item.SectionName, StringComparer.Ordinal).ThenBy(static item => item.OffsetBytes)
            .ThenBy(static item => item.Kind).ThenBy(static item => item.TargetSymbol, StringComparer.Ordinal).ToArray();

        HybridCpuPlatformFactStatus status = ValidateObjectMetadata(
            orderedSections, orderedSymbols, orderedRelocations, out string reason);
        return new(status, reason, orderedSections, orderedSymbols, orderedRelocations,
            DigestObjectMetadata(orderedSections, orderedSymbols, orderedRelocations));
    }

    public HybridCpuDebugMapResultV1 BuildNativeDebugMap(IReadOnlyList<HybridCpuDebugOriginRecordV1> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        HybridCpuDebugOriginRecordV1[] ordered = records.OrderBy(static item => item.InstructionIndex).ToArray();
        if (ordered.Any(static item => item.InstructionIndex < 0 || item.Origin.SchemaVersion != 1 || item.Origin.Links.Count == 0) ||
            ordered.Select(static item => item.InstructionIndex).Distinct().Count() != ordered.Length)
            return new(HybridCpuPlatformFactStatus.Invalid, "Malformed or duplicate native origin mapping.", ordered, DigestDebugMap(ordered));
        if (ordered.SelectMany(static item => item.Origin.Links).Any(static link =>
                string.IsNullOrWhiteSpace(link.StableSourceIdentity) || string.IsNullOrWhiteSpace(link.ProducerId) ||
                link.Trust == IrFrontendEvidenceTrust.Unknown))
            return new(HybridCpuPlatformFactStatus.Unknown, "Native origin mapping contains an untrusted or unknown origin.", ordered, DigestDebugMap(ordered));
        return new(HybridCpuPlatformFactStatus.Supported, "Canonical native origin mapping is available; object debug sections remain unsupported.",
            ordered, DigestDebugMap(ordered));
    }

    private static HybridCpuPlatformFactStatus ValidateObjectMetadata(
        IReadOnlyList<HybridCpuObjectSectionRequestV1> sections,
        IReadOnlyList<HybridCpuObjectSymbolRequestV1> symbols,
        IReadOnlyList<HybridCpuObjectRelocationRequestV1> relocations,
        out string reason)
    {
        if (sections.Any(static item => string.IsNullOrWhiteSpace(item.Name) || item.Kind == HybridCpuObjectSectionKind.Unknown ||
                item.AlignmentBytes <= 0 || item.AlignmentBytes > 4096 || (item.AlignmentBytes & (item.AlignmentBytes - 1)) != 0) ||
            sections.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != sections.Count)
            return Result(HybridCpuPlatformFactStatus.Invalid, "Malformed or duplicate section metadata.", out reason);

        HashSet<string> sectionNames = sections.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (symbols.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Binding == HybridCpuSymbolBinding.Unknown ||
                item.Visibility == HybridCpuSymbolVisibility.Unknown ||
                (item.IsDefinition && (item.SectionName is null || !sectionNames.Contains(item.SectionName)))))
            return Result(HybridCpuPlatformFactStatus.Invalid, "Malformed symbol metadata or unknown definition section.", out reason);

        foreach (IGrouping<string, HybridCpuObjectSymbolRequestV1> group in symbols
                     .Where(static item => item.IsDefinition).GroupBy(static item => item.Name, StringComparer.Ordinal))
        {
            if (group.Count() <= 1) continue;
            if (group.Any(static item => !string.IsNullOrWhiteSpace(item.ComdatKey)))
                return Result(HybridCpuPlatformFactStatus.Unsupported, "COMDAT and duplicate coalescing are unsupported.", out reason);
            return Result(HybridCpuPlatformFactStatus.Invalid, "Duplicate symbol definitions are rejected.", out reason);
        }

        HashSet<string> symbolNames = symbols.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (relocations.Any(item => string.IsNullOrWhiteSpace(item.SectionName) || !sectionNames.Contains(item.SectionName) ||
                item.OffsetBytes < 0 || string.IsNullOrWhiteSpace(item.TargetSymbol) || !symbolNames.Contains(item.TargetSymbol) ||
                item.Kind == HybridCpuRelocationKind.Unknown))
            return Result(HybridCpuPlatformFactStatus.Invalid, "Malformed relocation or unresolved relocation symbol.", out reason);
        if (relocations.Any(static item => item.Kind == HybridCpuRelocationKind.BundleRelativeSigned16 &&
                (item.Addend != 0 || item.OffsetBytes % 32 != 0)))
            return Result(HybridCpuPlatformFactStatus.Invalid, "Internal control relocation has an invalid addend or slot offset.", out reason);
        if (relocations.Any(static item => item.Kind != HybridCpuRelocationKind.BundleRelativeSigned16))
            return Result(HybridCpuPlatformFactStatus.Unsupported, "Requested object relocation kind is unsupported.", out reason);
        return Result(HybridCpuPlatformFactStatus.Unsupported,
            "Logical metadata is deterministic, but no HybridCPU object container/writer is authorized.", out reason);
    }

    private static HybridCpuPlatformFactStatus Result(HybridCpuPlatformFactStatus status, string value, out string reason)
    {
        reason = value;
        return status;
    }

    private string ComputeDigest()
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(SchemaId).Append('|').Append(SchemaMajor).Append('.').Append(SchemaMinor)
            .Append('|').Append(CoreTargetContractDigest).Append('|').Append(MachineDescriptionDigest)
            .Append("|abi:").Append(CallingConvention.SchemaId).Append(':').Append(CallingConvention.FullFunctionAbi)
            .Append(':').AppendJoin(',', CallingConvention.ArgumentRegisters)
            .Append(':').AppendJoin(',', CallingConvention.ReturnRegisters)
            .Append(':').AppendJoin(',', CallingConvention.CallerSavedRegisters)
            .Append(':').AppendJoin(',', CallingConvention.CalleeSavedRegisters)
            .Append(':').Append(CallingConvention.AggregatePassing).Append(':').Append(CallingConvention.VarArgs)
            .Append(':').Append(CallingConvention.TailCalls).Append(':').Append(CallingConvention.SpecialContourCalls)
            .Append("|stack:").Append(StackFrame.Support).Append(':').Append(StackFrame.StackAlignmentBytes)
            .Append(':').Append(StackFrame.StackPointerRegister).Append(':').Append(StackFrame.FramePointerRegister)
            .Append(':').Append(StackFrame.RedZoneBytes).Append(':').Append(StackFrame.DynamicAllocation)
            .Append(':').Append(StackFrame.StackProbing)
            .Append("|atomic:").AppendJoin(',', AtomicWidths).Append(':').AppendJoin(',', AtomicOrderings)
            .Append("|fence:").Append(AtomicMemory.FenceOrdering).Append("|volatile:").Append(AtomicMemory.VolatileSemantics)
            .Append("|object:").Append(ObjectFormat).Append("|tls:").Append(ThreadLocalStorage)
            .Append("|helper:").Append(RuntimeHelperAbi).Append("|startup:").Append(ExecutableStartup)
            .Append("|dynamic-libraries:").Append(DynamicLibraries)
            .Append("|debug:").Append(NativeOriginMapping).Append(':').Append(ObjectDebugSections)
            .Append("|unwind:").Append(UnwindSemantics).Append("|eh:").Append(ExceptionHandlingSemantics);
        foreach (HybridCpuObjectWriterFeatureV1 feature in ObjectWriterFeatureTable)
            builder.Append("|wf:").Append(feature.Identity).Append(':').Append(feature.Version).Append(':').Append(feature.Support);
        foreach (HybridCpuRelocationFeatureV1 feature in RelocationFeatureTable)
            builder.Append("|rf:").Append(feature.Kind).Append(':').Append(feature.Version).Append(':')
                .Append(feature.Owner).Append(':').Append(feature.Support).Append(':').Append(feature.AllowsAddend);
        return Hash(builder.ToString());
    }

    private static string DigestObjectMetadata(
        IEnumerable<HybridCpuObjectSectionRequestV1> sections,
        IEnumerable<HybridCpuObjectSymbolRequestV1> symbols,
        IEnumerable<HybridCpuObjectRelocationRequestV1> relocations)
    {
        StringBuilder builder = new StringBuilder("hybridcpu.object-metadata/v1");
        foreach (HybridCpuObjectSectionRequestV1 item in sections)
            builder.Append("|s:").Append(item.Kind).Append(':').Append(item.Name).Append(':').Append(item.AlignmentBytes);
        foreach (HybridCpuObjectSymbolRequestV1 item in symbols)
            builder.Append("|y:").Append(item.Name).Append(':').Append(item.Binding).Append(':').Append(item.Visibility)
                .Append(':').Append(item.SectionName).Append(':').Append(item.IsDefinition).Append(':').Append(item.ComdatKey);
        foreach (HybridCpuObjectRelocationRequestV1 item in relocations)
            builder.Append("|r:").Append(item.SectionName).Append(':').Append(item.OffsetBytes).Append(':').Append(item.Kind)
                .Append(':').Append(item.TargetSymbol).Append(':').Append(item.Addend);
        return Hash(builder.ToString());
    }

    private static string DigestDebugMap(IEnumerable<HybridCpuDebugOriginRecordV1> records)
    {
        StringBuilder builder = new StringBuilder("hybridcpu.native-origin-map/v1");
        foreach (HybridCpuDebugOriginRecordV1 record in records)
        {
            builder.Append("|i:").Append(record.InstructionIndex).Append(':').Append(record.Origin.SchemaVersion);
            foreach (IrSourceOriginLinkV1 link in record.Origin.Links)
            {
                builder.Append(':').Append(link.StableSourceIdentity).Append(':').Append(link.Kind).Append(':')
                    .Append(link.ProducerId).Append(':').Append(link.ProducerVersion).Append(':').Append(link.Trust);
                if (link.Span is IrSourceSpan span)
                    builder.Append(':').Append(span.DocumentName).Append(':').Append(span.StartLine).Append(':')
                        .Append(span.StartColumn).Append(':').Append(span.EndLine).Append(':').Append(span.EndColumn)
                        .Append(':').Append(span.StartOffset).Append(':').Append(span.Length);
            }
        }
        return Hash(builder.ToString());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
