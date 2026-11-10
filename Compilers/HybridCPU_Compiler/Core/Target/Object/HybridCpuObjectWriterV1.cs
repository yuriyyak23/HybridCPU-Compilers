using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;

namespace HybridCPU.Compiler.Core.Target.Object;

public sealed class HybridCpuObjectWriterV1
{
    public HybridCpuObjectArtifactV1 Write(HybridCpuObjectRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sections);
        ArgumentNullException.ThrowIfNull(request.Symbols);
        ArgumentNullException.ThrowIfNull(request.Relocations);

        if (!IsDigest(request.TargetContractDigest) || !IsDigest(request.ManagedAbiDigest))
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0001", "Target and managed ABI digests must be lowercase SHA-256 values.");
        if (!string.Equals(request.TargetContractDigest, HybridCpuTargetPlatformContractV1.Default.ContractDigest, StringComparison.Ordinal) ||
            !string.Equals(request.ManagedAbiDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest, StringComparison.Ordinal))
            return Failure(HybridCpuObjectStatusV1.VersionSkew, "HCOBJ1001", "Target or managed ABI digest does not match the qualified object format.");
        if (request.Sections.Count is 0 or > HybridCpuObjectFormatContractV1.MaximumSections ||
            request.Symbols.Count > HybridCpuObjectFormatContractV1.MaximumSymbols ||
            request.Relocations.Count > HybridCpuObjectFormatContractV1.MaximumRelocations)
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0002", "Object table count is outside the production budget.");

        HybridCpuObjectSectionV1[] sections = request.Sections
            .OrderBy(static section => section.Kind).ThenBy(static section => section.Name, StringComparer.Ordinal).ToArray();
        HybridCpuObjectSymbolV1[] symbols = request.Symbols
            .OrderBy(static symbol => symbol.Name, StringComparer.Ordinal).ThenBy(static symbol => symbol.Binding)
            .ThenBy(static symbol => symbol.Visibility).ToArray();
        HybridCpuObjectRelocationV1[] relocations = request.Relocations
            .OrderBy(static relocation => relocation.SectionName, StringComparer.Ordinal).ThenBy(static relocation => relocation.Offset)
            .ThenBy(static relocation => relocation.Kind).ThenBy(static relocation => relocation.TargetSymbol, StringComparer.Ordinal)
            .ThenBy(static relocation => relocation.Addend).ToArray();

        HybridCpuObjectArtifactV1? validation = Validate(sections, symbols, relocations);
        if (validation is not null) return validation;

        string[] names = sections.Select(static item => item.Name)
            .Concat(symbols.Select(static item => item.Name))
            .Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        Dictionary<string, uint> stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        using MemoryStream stringStream = new MemoryStream();
        stringStream.WriteByte(0);
        foreach (string name in names)
        {
            if (name.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(name) > 1024)
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0003", "Object name is empty, oversized, or contains NUL.");
            stringOffsets.Add(name, checked((uint)stringStream.Position));
            stringStream.Write(Encoding.UTF8.GetBytes(name));
            stringStream.WriteByte(0);
        }
        byte[] strings = stringStream.ToArray();
        if (strings.Length > HybridCpuObjectFormatContractV1.MaximumStringTableBytes)
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0004", "String table exceeds the production budget.");

        ulong sectionTableOffset = HybridCpuObjectFormatContractV1.HeaderSize;
        ulong symbolTableOffset = checked(sectionTableOffset + (ulong)sections.Length * HybridCpuObjectFormatContractV1.SectionEntrySize);
        ulong relocationTableOffset = checked(symbolTableOffset + (ulong)symbols.Length * HybridCpuObjectFormatContractV1.SymbolEntrySize);
        ulong stringTableOffset = checked(relocationTableOffset + (ulong)relocations.Length * HybridCpuObjectFormatContractV1.RelocationEntrySize);
        ulong payloadOffset = Align(checked(stringTableOffset + (ulong)strings.Length), HybridCpuObjectFormatContractV1.BundleAlignmentBytes);
        ulong cursor = payloadOffset;
        ulong[] sectionFileOffsets = new ulong[sections.Length];
        for (int index = 0; index < sections.Length; index++)
        {
            if (sections[index].Kind == HybridCpuObjectSectionKind.ZeroFill) continue;
            cursor = Align(cursor, sections[index].AlignmentBytes);
            sectionFileOffsets[index] = cursor;
            cursor = checked(cursor + (ulong)sections[index].Data.Length);
        }
        if (cursor > HybridCpuObjectFormatContractV1.MaximumObjectBytes)
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0005", "Object exceeds the production byte budget.");

        byte[] bytes = new byte[checked((int)cursor)];
        HybridCpuObjectFormatContractV1.Magic.CopyTo(bytes);
        WriteUInt16(bytes, 8, HybridCpuObjectFormatContractV1.SchemaMajor);
        WriteUInt16(bytes, 10, HybridCpuObjectFormatContractV1.SchemaMinor);
        WriteUInt32(bytes, 12, HybridCpuObjectFormatContractV1.HeaderSize);
        WriteUInt32(bytes, 16, 0);
        WriteUInt32(bytes, 20, checked((uint)sections.Length));
        WriteUInt32(bytes, 24, checked((uint)symbols.Length));
        WriteUInt32(bytes, 28, checked((uint)relocations.Length));
        WriteUInt64(bytes, 32, sectionTableOffset);
        WriteUInt64(bytes, 40, symbolTableOffset);
        WriteUInt64(bytes, 48, relocationTableOffset);
        WriteUInt64(bytes, 56, stringTableOffset);
        WriteUInt64(bytes, 64, checked((ulong)strings.Length));
        WriteUInt64(bytes, 72, payloadOffset);
        Convert.FromHexString(request.TargetContractDigest).CopyTo(bytes, 80);
        Convert.FromHexString(request.ManagedAbiDigest).CopyTo(bytes, 112);

        for (int index = 0; index < sections.Length; index++)
        {
            HybridCpuObjectSectionV1 section = sections[index];
            int offset = checked((int)sectionTableOffset + index * HybridCpuObjectFormatContractV1.SectionEntrySize);
            WriteUInt32(bytes, offset, stringOffsets[section.Name]);
            bytes[offset + 4] = (byte)section.Kind;
            bytes[offset + 5] = section.Kind == HybridCpuObjectSectionKind.ZeroFill ? (byte)0 : (byte)1;
            WriteUInt32(bytes, offset + 8, checked((uint)section.AlignmentBytes));
            WriteUInt64(bytes, offset + 16, sectionFileOffsets[index]);
            WriteUInt64(bytes, offset + 24, checked((ulong)section.Data.Length));
            WriteUInt64(bytes, offset + 32, section.VirtualSize);
            if (section.Data.Length != 0) section.Data.CopyTo(bytes, checked((int)sectionFileOffsets[index]));
        }

        Dictionary<string, int> sectionIndices = sections.Select((section, index) => (section.Name, index))
            .ToDictionary(static item => item.Name, static item => item.index, StringComparer.Ordinal);
        Dictionary<string, int> symbolIndices = symbols.Select((symbol, index) => (symbol.Name, index))
            .ToDictionary(static item => item.Name, static item => item.index, StringComparer.Ordinal);
        for (int index = 0; index < symbols.Length; index++)
        {
            HybridCpuObjectSymbolV1 symbol = symbols[index];
            int offset = checked((int)symbolTableOffset + index * HybridCpuObjectFormatContractV1.SymbolEntrySize);
            WriteUInt32(bytes, offset, stringOffsets[symbol.Name]);
            WriteInt32(bytes, offset + 4, symbol.IsDefinition ? sectionIndices[symbol.SectionName!] : -1);
            bytes[offset + 8] = (byte)symbol.Binding;
            bytes[offset + 9] = (byte)symbol.Visibility;
            bytes[offset + 10] = symbol.IsDefinition ? (byte)1 : (byte)0;
            WriteUInt64(bytes, offset + 16, symbol.Offset);
            WriteUInt64(bytes, offset + 24, symbol.Size);
        }
        for (int index = 0; index < relocations.Length; index++)
        {
            HybridCpuObjectRelocationV1 relocation = relocations[index];
            int offset = checked((int)relocationTableOffset + index * HybridCpuObjectFormatContractV1.RelocationEntrySize);
            WriteUInt32(bytes, offset, checked((uint)sectionIndices[relocation.SectionName]));
            WriteUInt32(bytes, offset + 4, checked((uint)symbolIndices[relocation.TargetSymbol]));
            WriteUInt64(bytes, offset + 8, relocation.Offset);
            WriteInt64(bytes, offset + 16, relocation.Addend);
            bytes[offset + 24] = (byte)relocation.Kind;
            bytes[offset + 25] = (byte)HybridCpuRelocationOwner.Linker;
            WriteUInt16(bytes, offset + 26, RelocationWidthBits(relocation.Kind));
        }
        strings.CopyTo(bytes, checked((int)stringTableOffset));
        SHA256.HashData(bytes).CopyTo(bytes, 144);

        string metadataDigest = DigestMetadata(sections, symbols, relocations, request.TargetContractDigest, request.ManagedAbiDigest);
        return new(HybridCpuObjectStatusV1.Success, bytes, HybridCpuObjectFormatContractV1.Hash(bytes), metadataDigest,
            sections, symbols, relocations, Array.Empty<HybridCpuObjectDiagnosticV1>());
    }

    public HybridCpuObjectArtifactV1 Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HybridCpuObjectFormatContractV1.HeaderSize ||
            !bytes[..HybridCpuObjectFormatContractV1.Magic.Length].SequenceEqual(HybridCpuObjectFormatContractV1.Magic))
            return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2001", "Object header or magic is corrupt.");
        if (ReadUInt16(bytes, 8) != HybridCpuObjectFormatContractV1.SchemaMajor ||
            ReadUInt16(bytes, 10) != HybridCpuObjectFormatContractV1.SchemaMinor)
            return Failure(HybridCpuObjectStatusV1.VersionSkew, "HCOBJ2002", "Object schema version is unsupported.");

        try
        {
            if (ReadUInt32(bytes, 12) != HybridCpuObjectFormatContractV1.HeaderSize || ReadUInt32(bytes, 16) != 0)
                return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2003", "Object header size or flags are invalid.");
            byte[] checksumInput = bytes.ToArray();
            checksumInput.AsSpan(144, 32).Clear();
            if (!bytes.Slice(144, 32).SequenceEqual(SHA256.HashData(checksumInput)))
                return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2015", "Object content checksum does not match.");
            int sectionCount = checked((int)ReadUInt32(bytes, 20));
            int symbolCount = checked((int)ReadUInt32(bytes, 24));
            int relocationCount = checked((int)ReadUInt32(bytes, 28));
            if (sectionCount is 0 or > HybridCpuObjectFormatContractV1.MaximumSections ||
                symbolCount > HybridCpuObjectFormatContractV1.MaximumSymbols ||
                relocationCount > HybridCpuObjectFormatContractV1.MaximumRelocations)
                return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2004", "Object table count is invalid.");
            ulong sectionTableOffset = ReadUInt64(bytes, 32);
            ulong symbolTableOffset = ReadUInt64(bytes, 40);
            ulong relocationTableOffset = ReadUInt64(bytes, 48);
            ulong stringTableOffset = ReadUInt64(bytes, 56);
            ulong stringTableSize = ReadUInt64(bytes, 64);
            ulong payloadOffset = ReadUInt64(bytes, 72);
            ulong expectedSymbolOffset = checked((ulong)HybridCpuObjectFormatContractV1.HeaderSize +
                (ulong)sectionCount * HybridCpuObjectFormatContractV1.SectionEntrySize);
            ulong expectedRelocationOffset = checked(expectedSymbolOffset + (ulong)symbolCount * HybridCpuObjectFormatContractV1.SymbolEntrySize);
            ulong expectedStringOffset = checked(expectedRelocationOffset + (ulong)relocationCount * HybridCpuObjectFormatContractV1.RelocationEntrySize);
            if (sectionTableOffset != HybridCpuObjectFormatContractV1.HeaderSize || symbolTableOffset != expectedSymbolOffset ||
                relocationTableOffset != expectedRelocationOffset || stringTableOffset != expectedStringOffset ||
                stringTableSize is 0 or > HybridCpuObjectFormatContractV1.MaximumStringTableBytes ||
                payloadOffset != Align(checked(stringTableOffset + stringTableSize), HybridCpuObjectFormatContractV1.BundleAlignmentBytes) ||
                payloadOffset > (ulong)bytes.Length)
                return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2005", "Object table offsets are not canonical.");

            string targetDigest = Convert.ToHexString(bytes.Slice(80, 32)).ToLowerInvariant();
            string managedAbiDigest = Convert.ToHexString(bytes.Slice(112, 32)).ToLowerInvariant();
            ReadOnlySpan<byte> strings = bytes.Slice(checked((int)stringTableOffset), checked((int)stringTableSize));
            if (strings[0] != 0) return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2006", "String table is corrupt.");

            HybridCpuObjectSectionV1[] sections = new HybridCpuObjectSectionV1[sectionCount];
            for (int index = 0; index < sectionCount; index++)
            {
                int offset = checked((int)sectionTableOffset + index * HybridCpuObjectFormatContractV1.SectionEntrySize);
                if (ReadUInt16(bytes, offset + 6) != 0 || ReadUInt32(bytes, offset + 12) != 0)
                    return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2007", "Section reserved fields are nonzero.");
                string name = ReadString(strings, ReadUInt32(bytes, offset));
                HybridCpuObjectSectionKind kind = (HybridCpuObjectSectionKind)bytes[offset + 4];
                byte flags = bytes[offset + 5];
                int alignment = checked((int)ReadUInt32(bytes, offset + 8));
                ulong fileOffset = ReadUInt64(bytes, offset + 16);
                ulong fileSize = ReadUInt64(bytes, offset + 24);
                ulong virtualSize = ReadUInt64(bytes, offset + 32);
                if (fileSize > int.MaxValue || fileOffset > (ulong)bytes.Length || checked(fileOffset + fileSize) > (ulong)bytes.Length)
                    return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2008", "Section payload range is corrupt.");
                if ((kind == HybridCpuObjectSectionKind.ZeroFill && (flags != 0 || fileOffset != 0 || fileSize != 0)) ||
                    (kind != HybridCpuObjectSectionKind.ZeroFill && flags != 1))
                    return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2009", "Section storage flags are corrupt.");
                byte[] data = fileSize == 0 ? Array.Empty<byte>() : bytes.Slice(checked((int)fileOffset), checked((int)fileSize)).ToArray();
                sections[index] = new(name, kind, alignment, data, virtualSize);
            }

            HybridCpuObjectSymbolV1[] symbols = new HybridCpuObjectSymbolV1[symbolCount];
            for (int index = 0; index < symbolCount; index++)
            {
                int offset = checked((int)symbolTableOffset + index * HybridCpuObjectFormatContractV1.SymbolEntrySize);
                if (bytes[offset + 11] != 0 || ReadUInt32(bytes, offset + 12) != 0)
                    return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2010", "Symbol reserved fields are nonzero.");
                string name = ReadString(strings, ReadUInt32(bytes, offset));
                int sectionIndex = ReadInt32(bytes, offset + 4);
                bool definition = bytes[offset + 10] == 1;
                if (bytes[offset + 10] > 1 || (definition && (sectionIndex < 0 || sectionIndex >= sections.Length)) || (!definition && sectionIndex != -1))
                    return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2011", "Symbol definition reference is corrupt.");
                symbols[index] = new(name, (HybridCpuSymbolBinding)bytes[offset + 8], (HybridCpuSymbolVisibility)bytes[offset + 9],
                    definition ? sections[sectionIndex].Name : null, ReadUInt64(bytes, offset + 16), ReadUInt64(bytes, offset + 24), definition);
            }

            HybridCpuObjectRelocationV1[] relocations = new HybridCpuObjectRelocationV1[relocationCount];
            for (int index = 0; index < relocationCount; index++)
            {
                int offset = checked((int)relocationTableOffset + index * HybridCpuObjectFormatContractV1.RelocationEntrySize);
                int sectionIndex = checked((int)ReadUInt32(bytes, offset));
                int symbolIndex = checked((int)ReadUInt32(bytes, offset + 4));
                HybridCpuRelocationKind kind = (HybridCpuRelocationKind)bytes[offset + 24];
                ushort expectedWidth = RelocationWidthBits(kind);
                if (sectionIndex >= sections.Length || symbolIndex >= symbols.Length ||
                    expectedWidth == 0 || bytes[offset + 25] != (byte)HybridCpuRelocationOwner.Linker ||
                    ReadUInt16(bytes, offset + 26) != expectedWidth ||
                    ReadUInt32(bytes, offset + 28) != 0)
                    return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2012", "Relocation record is corrupt.");
                relocations[index] = new(sections[sectionIndex].Name, ReadUInt64(bytes, offset + 8), kind,
                    symbols[symbolIndex].Name, ReadInt64(bytes, offset + 16));
            }

            HybridCpuObjectArtifactV1 canonical = Write(new(sections, symbols, relocations, targetDigest, managedAbiDigest));
            if (canonical.Status != HybridCpuObjectStatusV1.Success || !bytes.SequenceEqual(canonical.Bytes))
                return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2013", "Object is structurally valid but not canonically encoded.");
            return canonical;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return Failure(HybridCpuObjectStatusV1.Corrupt, "HCOBJ2014", "Object structure is truncated or malformed.");
        }
    }

    private static HybridCpuObjectArtifactV1? Validate(
        IReadOnlyList<HybridCpuObjectSectionV1> sections,
        IReadOnlyList<HybridCpuObjectSymbolV1> symbols,
        IReadOnlyList<HybridCpuObjectRelocationV1> relocations)
    {
        if (sections.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != sections.Count ||
            sections.Any(static item => string.IsNullOrWhiteSpace(item.Name) || item.Data is null ||
                item.AlignmentBytes <= 0 || item.AlignmentBytes > 4096 || (item.AlignmentBytes & (item.AlignmentBytes - 1)) != 0))
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0006", "Section name or alignment is invalid or duplicated.");
        if (sections.Any(static item => item.Kind is HybridCpuObjectSectionKind.ThreadLocalData or HybridCpuObjectSectionKind.Debug or
                HybridCpuObjectSectionKind.Unknown))
            return Failure(HybridCpuObjectStatusV1.Unsupported, "HCOBJ1002", "Requested section kind is not qualified by the current HCO contract.");
        if (sections.Any(static item => item.Kind == HybridCpuObjectSectionKind.ZeroFill
                ? item.Data.Length != 0 || item.VirtualSize == 0
                : item.VirtualSize != (ulong)item.Data.Length || item.Data.Length == 0))
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0007", "Section file and virtual sizes are inconsistent.");
        if (sections.Any(static item => item.Kind == HybridCpuObjectSectionKind.Code &&
                (item.AlignmentBytes < HybridCpuObjectFormatContractV1.BundleAlignmentBytes ||
                 item.Data.Length % HybridCpuObjectFormatContractV1.BundleAlignmentBytes != 0)))
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0008", "Code sections must preserve exact bundle size and alignment.");

        if (symbols.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != symbols.Count ||
            symbols.Any(static item => string.IsNullOrWhiteSpace(item.Name) || item.Binding == HybridCpuSymbolBinding.Unknown ||
                item.Visibility == HybridCpuSymbolVisibility.Unknown))
            return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0009", "Symbol identity is invalid or duplicated.");
        if (symbols.Any(static item => item.Binding == HybridCpuSymbolBinding.Weak || !string.IsNullOrWhiteSpace(item.ComdatKey)))
            return Failure(HybridCpuObjectStatusV1.Unsupported, "HCOBJ1003", "Weak and COMDAT symbols are not qualified by Phase 24A.");
        Dictionary<string, HybridCpuObjectSectionV1> sectionMap = sections.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        foreach (HybridCpuObjectSymbolV1 symbol in symbols)
        {
            if (!symbol.IsDefinition)
            {
                if (symbol.Binding != HybridCpuSymbolBinding.Global || symbol.SectionName is not null || symbol.Offset != 0 || symbol.Size != 0)
                    return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0010", "Undefined symbols must be zero-sized global declarations.");
                continue;
            }
            if (symbol.SectionName is null || !sectionMap.TryGetValue(symbol.SectionName, out HybridCpuObjectSectionV1? section) ||
                symbol.Offset > section.VirtualSize || symbol.Size > section.VirtualSize - symbol.Offset)
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0011", "Defined symbol range is outside its section.");
        }

        HashSet<string> symbolNames = symbols.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> relocationKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (HybridCpuObjectRelocationV1 relocation in relocations)
        {
            if (!sectionMap.TryGetValue(relocation.SectionName, out HybridCpuObjectSectionV1? section) ||
                section.Kind == HybridCpuObjectSectionKind.ZeroFill || !symbolNames.Contains(relocation.TargetSymbol))
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0012",
                    $"Relocation section or target symbol is invalid: section='{relocation.SectionName}', " +
                    $"target='{relocation.TargetSymbol}', kind={relocation.Kind}, offset={relocation.Offset}; " +
                    $"section-present={sectionMap.ContainsKey(relocation.SectionName)}, " +
                    $"target-present={symbolNames.Contains(relocation.TargetSymbol)}.");
            if (relocation.Kind is HybridCpuRelocationKind.ThreadLocal or HybridCpuRelocationKind.BundleRelativeSigned16)
                return Failure(HybridCpuObjectStatusV1.Unsupported,
                    relocation.Kind == HybridCpuRelocationKind.ThreadLocal ? "HCOBJ1014" : "HCOBJ1015",
                    "Relocation kind is not object-writer-owned in Phase 24A.");
            int width = relocation.Kind switch
            {
                HybridCpuRelocationKind.Absolute64 => 8,
                HybridCpuRelocationKind.PcRelative32 => 4,
                HybridCpuRelocationKind.ManagedCallRelativeSigned16 => 2,
                HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16 => 2,
                HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16 => 2,
                _ => 0
            };
            if (width == 0 || relocation.Offset % (ulong)width != 0 ||
                relocation.Offset > (ulong)section.Data.Length || (ulong)width > (ulong)section.Data.Length - relocation.Offset)
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0013", "Relocation kind, alignment, or patch range is invalid.");
            if (relocation.Kind == HybridCpuRelocationKind.ManagedCallRelativeSigned16 &&
                (section.Kind != HybridCpuObjectSectionKind.Code ||
                 section.AlignmentBytes < HybridCpuManagedCallRelocationContractV1.BundleSizeBytes ||
                 relocation.Addend != HybridCpuManagedCallRelocationContractV1.RequiredAddend ||
                 relocation.Offset % HybridCpuManagedCallRelocationContractV1.InstructionSlotSizeBytes !=
                    HybridCpuManagedCallRelocationContractV1.ImmediateFieldOffsetBytes ||
                 !IsManagedCallCarrier(section.Data, relocation.Offset)))
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ1016",
                    "Managed-call relocation requires a bundle-aligned code section, zero addend, slot-aligned Immediate field, and JAL carrier.");
            if (relocation.Kind is HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16 or
                    HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16 &&
                (section.Kind != HybridCpuObjectSectionKind.Code ||
                 section.AlignmentBytes < HybridCpuManagedCallRelocationContractV1.BundleSizeBytes ||
                 relocation.Offset % HybridCpuManagedCallRelocationContractV1.InstructionSlotSizeBytes !=
                    HybridCpuManagedCallRelocationContractV1.ImmediateFieldOffsetBytes ||
                 !IsLongManagedCallCarrier(section.Data, relocation.Offset, relocation.Kind)))
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ1017",
                    "Long managed-call relocation requires a bundle-aligned code section, slot-aligned Immediate field, and exact AUIPC/JALR carrier.");
            if (relocation.Kind == HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16 && relocation.Addend != 0)
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ1017",
                    "Long managed-call AUIPC high relocation requires zero addend.");
            if (relocation.Kind == HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16 &&
                (relocation.Addend < 0 || relocation.Addend % HybridCpuManagedCallRelocationContractV1.BundleSizeBytes != 0 ||
                 (ulong)relocation.Addend > section.VirtualSize))
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ1017",
                    "Long managed-call JALR low relocation requires an in-section non-negative bundle displacement to its AUIPC base.");
            string key = $"{relocation.SectionName}\0{relocation.Offset}";
            if (!relocationKeys.Add(key))
                return Failure(HybridCpuObjectStatusV1.Invalid, "HCOBJ0014", "Multiple relocations cannot own the same patch location.");
        }
        return null;
    }

    private static string DigestMetadata(
        IEnumerable<HybridCpuObjectSectionV1> sections,
        IEnumerable<HybridCpuObjectSymbolV1> symbols,
        IEnumerable<HybridCpuObjectRelocationV1> relocations,
        string targetDigest,
        string managedAbiDigest)
    {
        StringBuilder builder = new StringBuilder(HybridCpuObjectFormatContractV1.SchemaId)
            .Append('|').Append(targetDigest).Append('|').Append(managedAbiDigest);
        foreach (HybridCpuObjectSectionV1 section in sections)
            builder.Append("|s:").Append(section.Kind).Append(':').Append(section.Name).Append(':').Append(section.AlignmentBytes)
                .Append(':').Append(section.VirtualSize).Append(':').Append(HybridCpuObjectFormatContractV1.Hash(section.Data));
        foreach (HybridCpuObjectSymbolV1 symbol in symbols)
            builder.Append("|y:").Append(symbol.Name).Append(':').Append(symbol.Binding).Append(':').Append(symbol.Visibility)
                .Append(':').Append(symbol.SectionName).Append(':').Append(symbol.Offset).Append(':').Append(symbol.Size)
                .Append(':').Append(symbol.IsDefinition).Append(':').Append(symbol.ComdatKey);
        foreach (HybridCpuObjectRelocationV1 relocation in relocations)
            builder.Append("|r:").Append(relocation.SectionName).Append(':').Append(relocation.Offset).Append(':')
                .Append(relocation.Kind).Append(':').Append(relocation.TargetSymbol).Append(':').Append(relocation.Addend);
        return HybridCpuObjectFormatContractV1.Hash(builder.ToString());
    }

    private static HybridCpuObjectArtifactV1 Failure(HybridCpuObjectStatusV1 status, string code, string message) =>
        new(status, Array.Empty<byte>(), string.Empty, string.Empty,
            Array.Empty<HybridCpuObjectSectionV1>(), Array.Empty<HybridCpuObjectSymbolV1>(),
            Array.Empty<HybridCpuObjectRelocationV1>(), [new(code, message)]);

    private static bool IsDigest(string value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ushort RelocationWidthBits(HybridCpuRelocationKind kind) => kind switch
    {
        HybridCpuRelocationKind.Absolute64 => 64,
        HybridCpuRelocationKind.PcRelative32 => 32,
        HybridCpuRelocationKind.ManagedCallRelativeSigned16 => 16,
        HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16 => 16,
        HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16 => 16,
        _ => 0
    };

    private static bool IsManagedCallCarrier(byte[] code, ulong offset)
    {
        ulong word0 = ReadUInt64(code, checked((int)offset));
        return (HybridCpuOpcode)(word0 >> 48) == HybridCpuOpcode.JAL;
    }

    private static bool IsLongManagedCallCarrier(byte[] code, ulong offset, HybridCpuRelocationKind kind)
    {
        ulong word0 = ReadUInt64(code, checked((int)offset));
        HybridCpuOpcode opcode = (HybridCpuOpcode)(word0 >> 48);
        return kind == HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16
            ? opcode == HybridCpuOpcode.AUIPC
            : opcode == HybridCpuOpcode.JALR;
    }

    private static string ReadString(ReadOnlySpan<byte> table, uint offset)
    {
        if (offset == 0 || offset >= table.Length) throw new ArgumentException("Invalid string offset.");
        ReadOnlySpan<byte> tail = table[checked((int)offset)..];
        int terminator = tail.IndexOf((byte)0);
        if (terminator <= 0) throw new ArgumentException("Unterminated object string.");
        return new UTF8Encoding(false, true).GetString(tail[..terminator]);
    }

    private static ulong Align(ulong value, int alignment) => checked((value + (ulong)alignment - 1) & ~((ulong)alignment - 1));
    private static void WriteUInt16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);
    private static void WriteUInt32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void WriteInt32(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void WriteUInt64(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
    private static void WriteInt64(Span<byte> bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes[offset..], value);
    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static long ReadInt64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
}
