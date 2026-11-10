using System.Buffers.Binary;
using System.Security.Cryptography;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Runtime;

public sealed class HybridCpuRestrictedImageBuilderV1
{
    private const ushort SchemaMajor = 1;
    private const ushort SchemaMinor = 3;
    private const int ChecksumOffset = 288;
    private static ReadOnlySpan<byte> Magic => "HCEXE001"u8;

    public HybridCpuRestrictedImageV1 Build(
        HybridCpuRestrictedStartupRequestV1 request,
        HybridCpuRestrictedStartupOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= HybridCpuRestrictedStartupOptionsV1.Production;
        if (!Equals(options, HybridCpuRestrictedStartupOptionsV1.Production))
            return Failure(HybridCpuStartupStatusV1.VersionSkew, "HCSTART1001", "Only exact production startup options are qualified.", options);
        HybridCpuStaticLinkArtifactV1 link = request.LinkedImage;
        if (link is null || link.Status != HybridCpuLinkStatusV1.Success || link.ImageBytes is null || link.ImageBytes.Length == 0)
            return Failure(HybridCpuStartupStatusV1.Invalid, "HCSTART0001", "A successful non-empty HCLINK v1 artifact is required.", options);
        if (!string.Equals(link.OptionsDigest, options.LinkOptionsDigest, StringComparison.Ordinal) ||
            !string.Equals(Sha256(link.ImageBytes), link.ImageSha256, StringComparison.Ordinal))
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2001", "Linked image options or checksum do not match the startup contract.", options);
        if (string.IsNullOrWhiteSpace(request.EntrySymbol))
            return Failure(HybridCpuStartupStatusV1.Invalid, "HCSTART0002", "An explicit entry symbol is required.", options);
        if (request.ReturnAddressAdjustmentBytes is not 0 &&
            request.ReturnAddressAdjustmentBytes != HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes)
            return Failure(HybridCpuStartupStatusV1.Unsupported, "HCSTART1007",
                "Return-address adjustment is outside the exact restricted startup contract.", options);

        HybridCpuLinkedSymbolV1[] entryMatches = link.Symbols.Where(symbol =>
            string.Equals(symbol.Name, request.EntrySymbol, StringComparison.Ordinal)).ToArray();
        if (entryMatches.Length != 1)
            return Failure(HybridCpuStartupStatusV1.Invalid, "HCSTART1002", "The entry symbol must resolve exactly once.", options);
        HybridCpuLinkedSymbolV1 entry = entryMatches[0];
        if (entry.Binding != HybridCpuSymbolBinding.Global || entry.Visibility != HybridCpuSymbolVisibility.Default)
            return Failure(HybridCpuStartupStatusV1.Unsupported, "HCSTART1003", "The entry symbol must have global/default visibility.", options);
        HybridCpuLinkedSectionV1? entrySection = link.Sections.SingleOrDefault(section =>
            string.Equals(section.ModuleIdentity, entry.ModuleIdentity, StringComparison.Ordinal) &&
            section.Kind == HybridCpuObjectSectionKind.Code && entry.Address >= section.Address &&
            entry.Address < checked(section.Address + section.Size));
        if (entrySection is null || entry.Size == 0 || entry.Address % HybridCpuBundleSerializer.BundleSizeBytes != 0 ||
            entry.Size % HybridCpuBundleSerializer.BundleSizeBytes != 0 ||
            checked(entry.Address + entry.Size) > checked(entrySection.Address + entrySection.Size))
            return Failure(HybridCpuStartupStatusV1.Unsupported, "HCSTART1004", "The entry must be a whole, bundle-aligned code symbol.", options);

        HybridCpuImageRuntimeBootstrapDescriptorV1? bootstrap = request.RuntimeBootstrap;
        HybridCpuStartupDiagnosticV1? bootstrapFailure = ValidateBootstrap(link, request, bootstrap, options);
        if (bootstrapFailure is not null)
            return Failure(HybridCpuStartupStatusV1.Unsupported, bootstrapFailure.Code, bootstrapFailure.Message, options);
        if (link.Sections.Any(section => section.Kind == HybridCpuObjectSectionKind.ZeroFill &&
                link.ImageBytes.AsSpan(checked((int)(section.Address - link.ImageBase)), checked((int)section.Size)).IndexOfAnyExcept((byte)0) >= 0))
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2002", "Zero-fill sections contain non-zero image bytes.", options);

        ulong stackPointer = checked(options.StackBase + options.StackSize);
        if (stackPointer % HybridCpuNativeAbiContractV2.StackAlignmentBytes != 0)
            return Failure(HybridCpuStartupStatusV1.Invalid, "HCSTART0003", "Configured initial stack pointer violates native ABI alignment.", options);
        ulong initialReturnAddress = request.ReturnAddressAdjustmentBytes == 0
            ? options.ReturnSentinel
            : HybridCpuNativeCallControlContractV1.Default.BiasReturnSentinel(options.ReturnSentinel);
        ulong globalPointer = 0;
        if (request.GlobalPointerSymbol is not null)
        {
            HybridCpuLinkedSymbolV1[] globalMatches = link.Symbols.Where(symbol =>
                string.Equals(symbol.Name, request.GlobalPointerSymbol, StringComparison.Ordinal)).ToArray();
            if (globalMatches.Length != 1 || globalMatches[0].Size == 0 ||
                !link.Sections.Any(section => section.ModuleIdentity == globalMatches[0].ModuleIdentity &&
                    section.Kind == HybridCpuObjectSectionKind.ReadOnlyData &&
                    globalMatches[0].Address >= section.Address &&
                    globalMatches[0].Address + globalMatches[0].Size <= section.Address + section.Size))
                return Failure(HybridCpuStartupStatusV1.Unsupported, "HCSTART1016",
                    "Global-pointer symbol must resolve exactly once inside bounded read-only image data.", options);
            globalPointer = globalMatches[0].Address;
        }
        HybridCpuStartupRegisterStateV1 registers = new HybridCpuStartupRegisterStateV1(
            HybridCpuNativeAbiContractV2.StackPointerRegister,
            HybridCpuNativeAbiContractV2.FramePointerRegister,
            HybridCpuNativeAbiContractV2.ThreadPointerRegister,
            HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0],
            stackPointer, 0, 0, initialReturnAddress,
            HybridCpuNativeAbiContractV2.GlobalPointerRegister, globalPointer);

        byte[] bootstrapMetadata;
        try
        {
            bootstrapMetadata = bootstrap is null ? [] : HybridCpuManagedBootstrapEncodingV1.Encode(bootstrap);
        }
        catch (ArgumentException exception)
        {
            return Failure(HybridCpuStartupStatusV1.Invalid, "HCSTART1010", exception.Message, options);
        }
        int payloadOffset = AlignUp(checked(options.PackageHeaderBytes + bootstrapMetadata.Length), options.PayloadAlignmentBytes);
        int packageLength = checked(payloadOffset + link.ImageBytes.Length);
        if (packageLength > options.MaximumPackageBytes)
            return Failure(HybridCpuStartupStatusV1.Invalid, "HCSTART0004", "Restricted image package exceeds the deterministic byte budget.", options);
        byte[] package = new byte[packageLength];
        Magic.CopyTo(package);
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(8), SchemaMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(10), SchemaMinor);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(12), options.PackageHeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(16), packageLength);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(20), payloadOffset);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(24), link.ImageBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(28), bootstrapMetadata.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(32), link.ImageBase);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(40), entry.Address);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(48), options.StackBase);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(56), options.StackSize);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(64), stackPointer);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(72), initialReturnAddress);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(80), registers.StackPointerRegister);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(84), registers.FramePointerRegister);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(88), registers.ThreadPointerRegister);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(92), registers.ReturnAddressRegister);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(384), registers.GlobalPointerRegister);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(392), registers.GlobalPointer);
        WriteDigest(package, 96, options.TargetContractDigest);
        WriteDigest(package, 128, options.ManagedAbiDigest);
        WriteDigest(package, 160, options.NativeAbiDigest);
        WriteDigest(package, 192, link.LinkMapDigest);
        WriteDigest(package, 224, link.ImageSha256);
        WriteDigest(package, 256, options.OptionsDigest);
        WriteOptionalDigest(package, 320, bootstrap?.DescriptorDigest);
        WriteDigest(package, 352, HybridCpuPlatformContractV1.ContractDigest);
        bootstrapMetadata.CopyTo(package, options.PackageHeaderBytes);
        link.ImageBytes.CopyTo(package, payloadOffset);
        byte[] checksum = SHA256.HashData(package);
        checksum.CopyTo(package, ChecksumOffset);
        string packageSha = Sha256(package);
        return new(HybridCpuStartupStatusV1.Success, package, link.ImageBytes.ToArray(), link.ImageBase,
            entry.Address, entry.Name, registers, packageSha, link.ImageSha256, link.LinkMapDigest,
            options.OptionsDigest, Array.Empty<HybridCpuStartupDiagnosticV1>(), bootstrap);
    }

    public HybridCpuRestrictedImageV1 Inspect(
        byte[] package,
        HybridCpuRestrictedStartupOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        options ??= HybridCpuRestrictedStartupOptionsV1.Production;
        if (!Equals(options, HybridCpuRestrictedStartupOptionsV1.Production))
            return Failure(HybridCpuStartupStatusV1.VersionSkew, "HCSTART1001", "Only exact production startup options are qualified.", options);
        if (package.Length < options.PayloadAlignmentBytes || !package.AsSpan(0, 8).SequenceEqual(Magic))
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2003", "Package magic or minimum size is invalid.", options);
        if (BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(8)) != SchemaMajor ||
            BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(10)) != SchemaMinor)
            return Failure(HybridCpuStartupStatusV1.VersionSkew, "HCSTART2004", "Package schema version is unsupported.", options);
        int header = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(12));
        int packageLength = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(16));
        int payloadOffset = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(20));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(24));
        int bootstrapLength = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(28));
        if (header != options.PackageHeaderBytes || packageLength != package.Length || bootstrapLength < 0 ||
            bootstrapLength > HybridCpuManagedBootstrapEncodingV1.MaximumMetadataBytes ||
            payloadOffset != AlignUp(checked(header + bootstrapLength), options.PayloadAlignmentBytes) ||
            payloadLength <= 0 || payloadLength > package.Length - payloadOffset || packageLength > options.MaximumPackageBytes)
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2005", "Package offsets or deterministic budgets are invalid.", options);
        byte[] expectedChecksum = package.AsSpan(ChecksumOffset, 32).ToArray();
        byte[] scratch = package.ToArray();
        scratch.AsSpan(ChecksumOffset, 32).Clear();
        if (!CryptographicOperations.FixedTimeEquals(expectedChecksum, SHA256.HashData(scratch)))
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2006", "Package checksum mismatch.", options);
        if (!ReadDigest(package, 96).Equals(options.TargetContractDigest, StringComparison.Ordinal) ||
            !ReadDigest(package, 128).Equals(options.ManagedAbiDigest, StringComparison.Ordinal) ||
            !ReadDigest(package, 160).Equals(options.NativeAbiDigest, StringComparison.Ordinal) ||
            !ReadDigest(package, 256).Equals(options.OptionsDigest, StringComparison.Ordinal) ||
            !ReadDigest(package, 352).Equals(HybridCpuPlatformContractV1.ContractDigest, StringComparison.Ordinal))
            return Failure(HybridCpuStartupStatusV1.VersionSkew, "HCSTART2007", "Package contract digests do not match the qualified startup profile.", options);
        HybridCpuImageRuntimeBootstrapDescriptorV1? bootstrap = null;
        if (bootstrapLength != 0)
        {
            try
            {
                bootstrap = HybridCpuManagedBootstrapEncodingV1.Decode(package.AsSpan(header, bootstrapLength));
            }
            catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or IOException or OverflowException)
            {
                return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2009", exception.Message, options);
            }
            if (!ReadDigest(package, 320).Equals(bootstrap.DescriptorDigest, StringComparison.Ordinal) ||
                !string.Equals(bootstrap.ManagedAbiDigest, options.ManagedAbiDigest, StringComparison.Ordinal))
                return Failure(HybridCpuStartupStatusV1.VersionSkew, "HCSTART2010", "Managed bootstrap descriptor digest or ABI binding mismatch.", options);
        }
        else if (package.AsSpan(320, 32).IndexOfAnyExcept((byte)0) >= 0)
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2011", "Empty bootstrap metadata has a nonzero descriptor digest.", options);
        byte[] image = package.AsSpan(payloadOffset, payloadLength).ToArray();
        string imageSha = Sha256(image);
        if (!ReadDigest(package, 224).Equals(imageSha, StringComparison.Ordinal))
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2008", "Embedded linked-image checksum mismatch.", options);
        ulong imageBase = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(32));
        ulong entryAddress = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(40));
        ulong stackBase = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(48));
        ulong stackSize = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(56));
        ulong stackPointer = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(64));
        ulong returnAddress = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(72));
        int stackPointerRegister = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(80));
        int framePointerRegister = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(84));
        int threadPointerRegister = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(88));
        int returnAddressRegister = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(92));
        int globalPointerRegister = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(384));
        ulong globalPointer = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(392));
        bool validReturn = returnAddress == options.ReturnSentinel ||
            returnAddress == HybridCpuNativeCallControlContractV1.Default.BiasReturnSentinel(options.ReturnSentinel);
        if (entryAddress < imageBase || entryAddress - imageBase >= (ulong)payloadLength ||
            entryAddress % HybridCpuBundleSerializer.BundleSizeBytes != 0 ||
            stackBase != options.StackBase || stackSize != options.StackSize ||
            stackPointer != checked(options.StackBase + options.StackSize) || !validReturn ||
            stackPointerRegister != HybridCpuNativeAbiContractV2.StackPointerRegister ||
            framePointerRegister != HybridCpuNativeAbiContractV2.FramePointerRegister ||
            threadPointerRegister != HybridCpuNativeAbiContractV2.ThreadPointerRegister ||
            returnAddressRegister != HybridCpuNativeAbiContractV2.ReturnAddressRegister ||
            globalPointerRegister != HybridCpuNativeAbiContractV2.GlobalPointerRegister ||
            globalPointer != 0 && !bootstrapDataAddressIsValid(globalPointer))
            return Failure(HybridCpuStartupStatusV1.CorruptInput, "HCSTART2012",
                "Embedded entry or startup register state violates the qualified startup contract.", options);
        HybridCpuStartupRegisterStateV1 registers = new HybridCpuStartupRegisterStateV1(
            stackPointerRegister, framePointerRegister, threadPointerRegister, returnAddressRegister,
            HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0], stackPointer, 0, 0,
            returnAddress, globalPointerRegister, globalPointer);
        return new(HybridCpuStartupStatusV1.Success, package.ToArray(), image,
            imageBase, entryAddress, string.Empty, registers,
            Sha256(package), imageSha, ReadDigest(package, 192), options.OptionsDigest,
            Array.Empty<HybridCpuStartupDiagnosticV1>(), bootstrap);

        bool bootstrapDataAddressIsValid(ulong address) => address >= imageBase && address < imageBase + (ulong)image.Length;
    }

    private static HybridCpuStartupDiagnosticV1? ValidateBootstrap(
        HybridCpuStaticLinkArtifactV1 link,
        HybridCpuRestrictedStartupRequestV1 request,
        HybridCpuImageRuntimeBootstrapDescriptorV1? bootstrap,
        HybridCpuRestrictedStartupOptionsV1 options)
    {
        string[] linkedReservedHelpers = link.Symbols.Select(static symbol => symbol.Name)
            .Where(name => HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(name) is not null)
            .Order(StringComparer.Ordinal).ToArray();
        string[] linkedInitializers = link.Symbols.Select(static symbol => symbol.Name)
            .Where(IsStaticInitializer).Order(StringComparer.Ordinal).ToArray();
        if (bootstrap is null)
        {
            if (linkedReservedHelpers.Length != 0)
                return new("HCSTART1005", $"Runtime helper '{linkedReservedHelpers[0]}' requires a managed bootstrap descriptor.");
            if (linkedInitializers.Length != 0)
                return new("HCSTART1006", $"Executable static initializer '{linkedInitializers[0]}' requires managed bootstrap registration.");
            return null;
        }
        if (!string.Equals(bootstrap.ManagedAbiDigest, options.ManagedAbiDigest, StringComparison.Ordinal) ||
            !string.Equals(bootstrap.PlatformContractDigest, HybridCpuPlatformContractV1.ContractDigest, StringComparison.Ordinal) ||
            !string.Equals(bootstrap.RuntimeEntrySymbol, request.EntrySymbol, StringComparison.Ordinal))
            return new("HCSTART1008", "Managed bootstrap ABI/platform binding or runtime entry is inconsistent with the image request.");
        if (link.Symbols.Count(symbol => string.Equals(symbol.Name, bootstrap.ManagedEntrySymbol, StringComparison.Ordinal)) != 1)
            return new("HCSTART1009", "Managed bootstrap entry must resolve to exactly one image definition.");
        string[] imports = bootstrap.RuntimeHelpers.Select(static row => row.Symbol).Order(StringComparer.Ordinal).ToArray();
        if (!linkedReservedHelpers.SequenceEqual(imports, StringComparer.Ordinal))
            return new("HCSTART1011", "Linked runtime helper definitions must exactly match the managed bootstrap imports.");
        foreach (HybridCpuRuntimeHelperImportV1 import in bootstrap.RuntimeHelpers)
        {
            HybridCpuRuntimeHelperV1? helper = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(import.Symbol);
            if (helper is null || helper.Support != HybridCpuManagedAbiSupportV1.Supported ||
                !string.Equals(helper.Signature, import.Signature, StringComparison.Ordinal))
                return new("HCSTART1012", $"Runtime helper '{import.Symbol}' is missing, unsupported or has a signature mismatch.");
        }
        string[] registeredInitializers = bootstrap.ModuleInitializers.Select(static row => row.InitializerSymbol)
            .Order(StringComparer.Ordinal).ToArray();
        if (!linkedInitializers.SequenceEqual(registeredInitializers, StringComparer.Ordinal))
        {
            string? missing = linkedInitializers.Except(registeredInitializers, StringComparer.Ordinal).FirstOrDefault();
            string? unexpected = registeredInitializers.Except(linkedInitializers, StringComparer.Ordinal).FirstOrDefault();
            return new("HCSTART1013", "Executable static initializers must exactly match bootstrap registrations: " +
                $"missing={missing ?? "none"}; unexpected={unexpected ?? "none"}; " +
                $"linked={linkedInitializers.Length}; registered={registeredInitializers.Length}.");
        }
        if (bootstrap.CodeManagerRecords.Any(record => record.CodeStartOffsetBytes < 0 || record.CodeSizeBytes <= 0 ||
            checked((long)record.CodeStartOffsetBytes + record.CodeSizeBytes) > link.ImageBytes.Length))
            return new("HCSTART1014", "Code-manager registration range is outside the final linked image.");
        if ((bootstrap.EhMethods ?? []).Any(record => record.CodeStartOffsetBytes < 0 || record.CodeSizeBytes <= 0 ||
            checked((long)record.CodeStartOffsetBytes + record.CodeSizeBytes) > link.ImageBytes.Length ||
            !bootstrap.CodeManagerRecords.Any(code => code.MethodIdentity == record.MethodIdentity &&
                code.CodeStartOffsetBytes == record.CodeStartOffsetBytes && code.CodeSizeBytes == record.CodeSizeBytes)))
            return new("HCSTART1015", "EH registration is outside the image or is not bound to its code-manager record.");
        return null;
    }

    private static bool IsStaticInitializer(string name) =>
        name.Contains(".cctor", StringComparison.Ordinal) ||
        name.StartsWith("__hybridcpu_static_init", StringComparison.Ordinal) ||
        name.StartsWith("__managed_cctor", StringComparison.Ordinal);

    private static void WriteDigest(byte[] bytes, int offset, string digest) =>
        Convert.FromHexString(digest).CopyTo(bytes, offset);

    private static void WriteOptionalDigest(byte[] bytes, int offset, string? digest)
    {
        if (digest is not null) WriteDigest(bytes, offset, digest);
    }

    private static string ReadDigest(byte[] bytes, int offset) => Convert.ToHexString(bytes, offset, 32).ToLowerInvariant();

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int AlignUp(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static HybridCpuRestrictedImageV1 Failure(
        HybridCpuStartupStatusV1 status,
        string code,
        string message,
        HybridCpuRestrictedStartupOptionsV1 options) =>
        new(status, Array.Empty<byte>(), Array.Empty<byte>(), 0, 0, string.Empty, null,
            string.Empty, string.Empty, string.Empty, options.OptionsDigest,
            [new HybridCpuStartupDiagnosticV1(code, message)]);
}
