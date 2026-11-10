using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.Target.Object;

public enum HybridCpuObjectStatusV1 : byte
{
    Success = 0,
    Unsupported = 1,
    Invalid = 2,
    Corrupt = 3,
    VersionSkew = 4
}

public sealed record HybridCpuObjectSectionV1(
    string Name,
    HybridCpuObjectSectionKind Kind,
    int AlignmentBytes,
    byte[] Data,
    ulong VirtualSize);

public sealed record HybridCpuObjectSymbolV1(
    string Name,
    HybridCpuSymbolBinding Binding,
    HybridCpuSymbolVisibility Visibility,
    string? SectionName,
    ulong Offset,
    ulong Size,
    bool IsDefinition,
    string? ComdatKey = null);

public sealed record HybridCpuObjectRelocationV1(
    string SectionName,
    ulong Offset,
    HybridCpuRelocationKind Kind,
    string TargetSymbol,
    long Addend);

public sealed record HybridCpuObjectRequestV1(
    IReadOnlyList<HybridCpuObjectSectionV1> Sections,
    IReadOnlyList<HybridCpuObjectSymbolV1> Symbols,
    IReadOnlyList<HybridCpuObjectRelocationV1> Relocations,
    string TargetContractDigest,
    string ManagedAbiDigest);

public sealed record HybridCpuObjectDiagnosticV1(string Code, string Message);

public sealed record HybridCpuObjectArtifactV1(
    HybridCpuObjectStatusV1 Status,
    byte[] Bytes,
    string ObjectSha256,
    string MetadataDigest,
    IReadOnlyList<HybridCpuObjectSectionV1> Sections,
    IReadOnlyList<HybridCpuObjectSymbolV1> Symbols,
    IReadOnlyList<HybridCpuObjectRelocationV1> Relocations,
    IReadOnlyList<HybridCpuObjectDiagnosticV1> Diagnostics)
{
    public bool HasLinkAuthority => false;
    public bool HasRuntimeAuthority => false;
    public bool HasPublicationAuthority => false;
}

public sealed record HybridCpuRelocationEvaluationV1(
    HybridCpuObjectStatusV1 Status,
    ulong EncodedValue,
    int WidthBits,
    string DiagnosticCode);

public static class HybridCpuObjectFormatContractV1
{
    public const string SchemaId = "hybridcpu.object/hco-v1";
    public const ushort SchemaMajor = 1;
    public const ushort SchemaMinor = 2;
    public const int HeaderSize = 176;
    public const int SectionEntrySize = 40;
    public const int SymbolEntrySize = 32;
    public const int RelocationEntrySize = 32;
    public const int BundleAlignmentBytes = 32;
    public const int MaximumSections = 4096;
    public const int MaximumSymbols = 65536;
    public const int MaximumRelocations = 1_000_000;
    public const int MaximumStringTableBytes = 4 * 1024 * 1024;
    public const int MaximumObjectBytes = 256 * 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "HCOBJ001"u8;

    public static string ContractDigest { get; } = Hash(string.Join('|',
        SchemaId, $"{SchemaMajor}.{SchemaMinor}", "little-endian", HeaderSize, SectionEntrySize,
        SymbolEntrySize, RelocationEntrySize, BundleAlignmentBytes,
        "sections=Code,ReadOnlyData,WritableData,ZeroFill,Unwind,ExceptionHandling",
        "bindings=Local,Global", "visibility=Hidden,Default",
        "relocations=Absolute64:Linker,PcRelative32:Linker,ManagedCallRelativeSigned16:Linker,ManagedCallPcRelativeHighSigned16:Linker,ManagedCallPcRelativeLowSigned16:Linker",
        "ManagedCallRelativeSigned16=S+A-BundleBase(P):signed16:little-endian:field-offset-slot-aligned",
        "ManagedCallPcRelativeHighSigned16=hi16((S+A-BundleBase(P)+0x800)>>12):AUIPC",
        "ManagedCallPcRelativeLowSigned16=lo16(S+A-BundleBase(P)-hi16<<12):JALR",
        "BundleRelativeSigned16=CompilerInternalControlFlow:forbidden-in-object",
        "ThreadLocal=Unsupported", "Weak=Unsupported", "Comdat=Unsupported",
        "Debug=Unsupported", "Unwind=managed-final-frame-only", "ExceptionHandling=managed-final-pc-only"));

    public static string OptionsDigest { get; } = Hash(string.Join('|',
        ContractDigest, MaximumSections, MaximumSymbols, MaximumRelocations,
        MaximumStringTableBytes, MaximumObjectBytes, "canonical-ordering", "zero-padding",
        "no-timestamps", "no-build-id", "no-host-format-fallback"));

    public static HybridCpuRelocationEvaluationV1 EvaluateRelocation(
        HybridCpuRelocationKind kind,
        ulong symbolAddress,
        ulong placeAddress,
        long addend)
    {
        try
        {
            return kind switch
            {
                HybridCpuRelocationKind.Absolute64 => new(
                    HybridCpuObjectStatusV1.Success,
                    AddSignedChecked(symbolAddress, addend),
                    64,
                    string.Empty),
                HybridCpuRelocationKind.PcRelative32 => EvaluatePcRelative32(symbolAddress, placeAddress, addend),
                HybridCpuRelocationKind.ManagedCallRelativeSigned16 =>
                    EvaluateManagedCallRelativeSigned16(symbolAddress, placeAddress, addend),
                HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16 =>
                    EvaluateManagedCallPcRelativeSigned16(symbolAddress, placeAddress, addend, highPart: true),
                HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16 =>
                    EvaluateManagedCallPcRelativeSigned16(symbolAddress, placeAddress, addend, highPart: false),
                HybridCpuRelocationKind.ThreadLocal => new(
                    HybridCpuObjectStatusV1.Unsupported, 0, 0, "HCOBJ1014"),
                HybridCpuRelocationKind.BundleRelativeSigned16 => new(
                    HybridCpuObjectStatusV1.Unsupported, 0, 0, "HCOBJ1015"),
                _ => new(HybridCpuObjectStatusV1.Invalid, 0, 0, "HCOBJ0012")
            };
        }
        catch (OverflowException)
        {
            return new(HybridCpuObjectStatusV1.Invalid, 0, 0, "HCOBJ1013");
        }
    }

    internal static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    internal static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static HybridCpuRelocationEvaluationV1 EvaluatePcRelative32(
        ulong symbolAddress,
        ulong placeAddress,
        long addend)
    {
        long symbolWithAddend = checked((long)symbolAddress + addend);
        long displacement = checked(symbolWithAddend - (long)placeAddress);
        if (displacement is < int.MinValue or > int.MaxValue)
            return new(HybridCpuObjectStatusV1.Invalid, 0, 32, "HCOBJ1013");
        return new(HybridCpuObjectStatusV1.Success, unchecked((uint)(int)displacement), 32, string.Empty);
    }

    private static HybridCpuRelocationEvaluationV1 EvaluateManagedCallRelativeSigned16(
        ulong symbolAddress,
        ulong placeAddress,
        long addend)
    {
        if (addend != 0 ||
            symbolAddress % HybridCpuManagedCallRelocationContractV1.BundleSizeBytes != 0 ||
            placeAddress % HybridCpuManagedCallRelocationContractV1.InstructionSlotSizeBytes != 0)
            return new(HybridCpuObjectStatusV1.Invalid, 0, 16, "HCOBJ1016");
        ulong bundleAddress = placeAddress & ~((ulong)HybridCpuManagedCallRelocationContractV1.BundleSizeBytes - 1);
        long displacement = checked((long)symbolAddress - (long)bundleAddress);
        if (displacement is < short.MinValue or > short.MaxValue)
            return new(HybridCpuObjectStatusV1.Invalid, 0, 16, "HCOBJ1013");
        return new(HybridCpuObjectStatusV1.Success, unchecked((ushort)(short)displacement), 16, string.Empty);
    }

    private static HybridCpuRelocationEvaluationV1 EvaluateManagedCallPcRelativeSigned16(
        ulong symbolAddress, ulong placeAddress, long addend, bool highPart)
    {
        if (symbolAddress % HybridCpuManagedCallRelocationContractV1.BundleSizeBytes != 0 ||
            placeAddress % HybridCpuManagedCallRelocationContractV1.InstructionSlotSizeBytes != 0)
            return new(HybridCpuObjectStatusV1.Invalid, 0, 16, "HCOBJ1017");
        ulong bundleAddress = placeAddress & ~((ulong)HybridCpuManagedCallRelocationContractV1.BundleSizeBytes - 1);
        long displacement = checked(checked((long)symbolAddress + addend) - (long)bundleAddress);
        long high = checked((displacement + 0x800L) >> 12);
        long low = checked(displacement - (high << 12));
        long value = highPart ? high : low;
        if (value is < short.MinValue or > short.MaxValue)
            return new(HybridCpuObjectStatusV1.Invalid, 0, 16, "HCOBJ1013");
        return new(HybridCpuObjectStatusV1.Success, unchecked((ushort)(short)value), 16, string.Empty);
    }

    private static ulong AddSignedChecked(ulong value, long addend) => addend >= 0
        ? checked(value + (ulong)addend)
        : checked(value - (ulong)(-(addend + 1)) - 1);
}

public static class HybridCpuManagedCallRelocationContractV1
{
    public const string SchemaId = "hybridcpu.relocation/managed-call-relative-signed16-v1";
    public const int SchemaVersion = 1;
    public const int BundleSizeBytes = 256;
    public const int InstructionSlotSizeBytes = 32;
    public const int ImmediateFieldOffsetBytes = 0;
    public const int WidthBits = 16;
    public const long RequiredAddend = 0;

    public static string ContractDigest { get; } = HybridCpuObjectFormatContractV1.Hash(string.Join('|',
        SchemaId, SchemaVersion, HybridCpuObjectFormatContractV1.ContractDigest,
        BundleSizeBytes, InstructionSlotSizeBytes, ImmediateFieldOffsetBytes, WidthBits,
        "S+A-BundleBase(P)", "signed-byte-displacement", "little-endian", "overflow=fail-closed"));
}
