using System.Buffers.Binary;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Runtime;

public static class HybridCpuManagedBootstrapEncodingV1
{
    private const uint Magic = 0x54424348; // HCBT
    public const int MaximumMetadataBytes = 16 * 1024 * 1024;

    public static byte[] Encode(HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor)
    {
        Validate(descriptor);
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write((ushort)1);
        writer.Write((ushort)6);
        WriteString(writer, descriptor.SchemaId);
        writer.Write(descriptor.SchemaMajor);
        writer.Write(descriptor.SchemaMinor);
        WriteString(writer, descriptor.PlatformContractDigest);
        WriteString(writer, descriptor.ManagedAbiDigest);
        WriteString(writer, descriptor.DescriptorDigest);
        WriteString(writer, descriptor.RuntimeEntrySymbol);
        WriteString(writer, descriptor.ManagedEntrySymbol);
        writer.Write(descriptor.RuntimeHelpers.Count);
        foreach (HybridCpuRuntimeHelperImportV1 row in descriptor.RuntimeHelpers)
        {
            WriteString(writer, row.Symbol);
            WriteString(writer, row.Signature);
            writer.Write(row.Required);
        }
        writer.Write(descriptor.CodeManagerRecords.Count);
        foreach (HybridCpuCodeManagerRegistrationV1 row in descriptor.CodeManagerRecords)
        {
            WriteString(writer, row.MethodIdentity);
            writer.Write(row.CodeStartOffsetBytes);
            writer.Write(row.CodeSizeBytes);
            WriteString(writer, row.GcInfoDigest);
            WriteString(writer, row.UnwindInfoDigest);
        }
        writer.Write(descriptor.StaticRoots.Count);
        foreach (HybridCpuStaticRootRegistrationV1 row in descriptor.StaticRoots)
        {
            WriteString(writer, row.Identity);
            writer.Write(row.Address);
            writer.Write(row.Size);
        }
        writer.Write(descriptor.ModuleInitializers.Count);
        foreach (HybridCpuModuleInitializerRegistrationV1 row in descriptor.ModuleInitializers)
        {
            WriteString(writer, row.ModuleIdentity);
            WriteString(writer, row.InitializerSymbol);
            writer.Write(row.Order);
            writer.Write(row.TypeId.HasValue);
            if (row.TypeId.HasValue) writer.Write(row.TypeId.Value);
        }
        writer.Write((descriptor.ManagedTypes ?? []).Count);
        foreach (HybridCpuManagedTypeRegistrationV1 row in descriptor.ManagedTypes ?? [])
        {
            writer.Write(row.TypeId);
            WriteString(writer, row.StableIdentity);
            WriteString(writer, row.DescriptorDigest);
            writer.Write(row.MetadataOffsetBytes);
            writer.Write(row.MetadataSizeBytes);
            writer.Write(row.StaticRootIdentity is not null);
            if (row.StaticRootIdentity is not null) WriteString(writer, row.StaticRootIdentity);
            writer.Write(row.TypeHandle);
        }
        writer.Write((descriptor.StringLiterals ?? []).Count);
        foreach (HybridCpuManagedStringLiteralRegistrationV1 row in descriptor.StringLiterals ?? [])
        {
            WriteString(writer, row.Identity);
            writer.Write(row.LiteralHandle);
            writer.Write(row.StringTypeId);
            WriteString(writer, row.Utf16Value);
        }
        writer.Write((descriptor.EhMethods ?? []).Count);
        foreach (HybridCpuManagedEhMethodRegistrationV1 row in descriptor.EhMethods ?? [])
        {
            WriteString(writer, row.MethodIdentity);
            writer.Write(row.CodeStartOffsetBytes);
            writer.Write(row.CodeSizeBytes);
            writer.Write(row.EhInfo.Length);
            writer.Write(row.EhInfo);
            writer.Write(row.UnwindInfo.Length);
            writer.Write(row.UnwindInfo);
        }
        writer.Write((descriptor.FieldData ?? []).Count);
        foreach (HybridCpuManagedFieldDataRegistrationV1 row in descriptor.FieldData ?? [])
        {
            writer.Write(row.DataHandle);
            writer.Write(row.Data.Length);
            writer.Write(row.Data);
        }
        writer.Flush();
        if (stream.Length > MaximumMetadataBytes)
            throw new ArgumentException("Managed bootstrap metadata exceeds its deterministic byte budget.", nameof(descriptor));
        return stream.ToArray();
    }

    public static HybridCpuImageRuntimeBootstrapDescriptorV1 Decode(ReadOnlySpan<byte> metadata)
    {
        ushort encodingMinor = metadata.Length >= 8 ? BinaryPrimitives.ReadUInt16LittleEndian(metadata[6..]) : ushort.MaxValue;
        if (metadata.Length is < 8 or > MaximumMetadataBytes || BinaryPrimitives.ReadUInt32LittleEndian(metadata) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(metadata[4..]) != 1 || encodingMinor > 6)
            throw new ArgumentException("Managed bootstrap metadata header is invalid.", nameof(metadata));
        using MemoryStream stream = new MemoryStream(metadata.ToArray(), writable: false);
        using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        reader.ReadUInt32();
        reader.ReadUInt16();
        reader.ReadUInt16();
        string schema = ReadString(reader);
        int major = reader.ReadInt32();
        int minor = reader.ReadInt32();
        string platform = ReadString(reader);
        string managedAbi = ReadString(reader);
        string digest = ReadString(reader);
        string runtimeEntry = ReadString(reader);
        string managedEntry = ReadString(reader);
        HybridCpuRuntimeHelperImportV1[] helpers = ReadRows(reader, HybridCpuPlatformContractV1.MaximumRuntimeHelpers,
            static input => new HybridCpuRuntimeHelperImportV1(ReadString(input), ReadString(input), input.ReadBoolean()));
        HybridCpuCodeManagerRegistrationV1[] methods = ReadRows(reader, HybridCpuPlatformContractV1.MaximumCodeManagerRecords,
            static input => new HybridCpuCodeManagerRegistrationV1(ReadString(input), input.ReadInt32(), input.ReadInt32(), ReadString(input), ReadString(input)));
        HybridCpuStaticRootRegistrationV1[] roots = ReadRows(reader, HybridCpuPlatformContractV1.MaximumStaticRoots,
            static input => new HybridCpuStaticRootRegistrationV1(ReadString(input), input.ReadUInt64(), input.ReadUInt64()));
        HybridCpuModuleInitializerRegistrationV1[] initializers = ReadRows(reader, HybridCpuPlatformContractV1.MaximumModuleInitializers,
            input => new HybridCpuModuleInitializerRegistrationV1(ReadString(input), ReadString(input), input.ReadInt32(),
                encodingMinor >= 5 && input.ReadBoolean() ? input.ReadUInt64() : null));
        HybridCpuManagedTypeRegistrationV1[] types = encodingMinor == 0 ? [] : ReadRows<HybridCpuManagedTypeRegistrationV1>(reader,
            HybridCpuPlatformContractV1.MaximumManagedTypes, input =>
            {
                ulong typeId = input.ReadUInt64();
                string identity = ReadString(input);
                string descriptorDigest = ReadString(input);
                int offset = input.ReadInt32();
                int size = input.ReadInt32();
                string? staticRoot = input.ReadBoolean() ? ReadString(input) : null;
                ulong typeHandle = encodingMinor >= 6 ? input.ReadUInt64() : 0;
                return new(typeId, identity, descriptorDigest, offset, size, staticRoot, typeHandle);
            });
        HybridCpuManagedStringLiteralRegistrationV1[] stringLiterals = encodingMinor < 4 ? [] :
            ReadRows<HybridCpuManagedStringLiteralRegistrationV1>(reader,
                HybridCpuPlatformContractV1.MaximumManagedStringLiterals, static input =>
                    new(ReadString(input), input.ReadUInt64(), input.ReadUInt64(), ReadString(input)));
        HybridCpuManagedEhMethodRegistrationV1[] ehMethods = encodingMinor < 2 ? [] : ReadRows<HybridCpuManagedEhMethodRegistrationV1>(reader,
            HybridCpuPlatformContractV1.MaximumCodeManagerRecords, static input =>
            {
                string identity = ReadString(input);
                int codeStart = input.ReadInt32();
                int codeSize = input.ReadInt32();
                byte[] eh = ReadBlob(input);
                byte[] unwind = ReadBlob(input);
                return new(identity, codeStart, codeSize, eh, unwind);
            });
        HybridCpuManagedFieldDataRegistrationV1[] fieldData = encodingMinor < 3 ? [] :
            ReadRows<HybridCpuManagedFieldDataRegistrationV1>(reader,
                HybridCpuPlatformContractV1.MaximumManagedFieldData, static input =>
                    new(input.ReadUInt64(), ReadBlob(input)));
        if (stream.Position != stream.Length) throw new ArgumentException("Managed bootstrap metadata has trailing bytes.", nameof(metadata));
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = new HybridCpuImageRuntimeBootstrapDescriptorV1(schema, major, minor, platform, managedAbi, digest,
            runtimeEntry, managedEntry, helpers, methods, roots, initializers, types,
            StringLiterals: stringLiterals, EhMethods: ehMethods, FieldData: fieldData);
        Validate(descriptor);
        return descriptor;
    }

    private static void Validate(HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        void Reject(string reason) => throw new ArgumentException(
            $"Managed bootstrap descriptor violates the versioned platform contract: {reason}.", nameof(descriptor));
        if (descriptor.SchemaId != HybridCpuImageRuntimeBootstrapContractV1.SchemaId) Reject("schema id mismatch");
        if (descriptor.SchemaMajor != HybridCpuImageRuntimeBootstrapContractV1.SchemaMajor) Reject("schema major mismatch");
        if (descriptor.SchemaMinor > HybridCpuImageRuntimeBootstrapContractV1.SchemaMinor) Reject("schema minor is newer than the loader");
        if (descriptor.PlatformContractDigest != HybridCpuPlatformContractV1.ContractDigest) Reject("platform contract digest mismatch");
        if (descriptor.DescriptorDigest != HybridCpuImageRuntimeBootstrapContractV1.ComputeDigest(descriptor)) Reject("descriptor digest mismatch");
        if (descriptor.RuntimeHelpers.Count > HybridCpuPlatformContractV1.MaximumRuntimeHelpers) Reject("runtime helper budget exceeded");
        if (descriptor.CodeManagerRecords.Count > HybridCpuPlatformContractV1.MaximumCodeManagerRecords) Reject("code-manager record budget exceeded");
        if (descriptor.StaticRoots.Count > HybridCpuPlatformContractV1.MaximumStaticRoots) Reject("static-root budget exceeded");
        if (descriptor.ModuleInitializers.Count > HybridCpuPlatformContractV1.MaximumModuleInitializers) Reject("module-initializer budget exceeded");
        if ((descriptor.ManagedTypes ?? []).Count > HybridCpuPlatformContractV1.MaximumManagedTypes) Reject("managed-type budget exceeded");
        if ((descriptor.StringLiterals ?? []).Count > HybridCpuPlatformContractV1.MaximumManagedStringLiterals) Reject("managed string-literal budget exceeded");
        if ((descriptor.StringLiterals ?? []).Any(static row => row.LiteralHandle == 0 || row.StringTypeId == 0 ||
            row.Utf16Value is null || row.Utf16Value.Length > HybridCpuPlatformContractV1.MaximumManagedStringLiteralCodeUnits)) Reject("managed string-literal row is out of bounds");
        if ((descriptor.EhMethods ?? []).Count > HybridCpuPlatformContractV1.MaximumCodeManagerRecords) Reject("EH-method budget exceeded");
        if ((descriptor.EhMethods ?? []).Any(static row => row.EhInfo is not { Length: > 0 } || row.UnwindInfo is not { Length: > 0 })) Reject("EH method lacks exact EH or unwind data");
        if ((descriptor.FieldData ?? []).Count > HybridCpuPlatformContractV1.MaximumManagedFieldData) Reject("managed field-data budget exceeded");
        if ((descriptor.FieldData ?? []).Any(static row => row.DataHandle is 0 or > 4096 || row.Data is not { Length: > 0 and <= 1_048_576 })) Reject("managed field-data row is out of bounds");
        if ((descriptor.FieldData ?? []).Select(static row => row.DataHandle).Distinct().Count() != (descriptor.FieldData ?? []).Count) Reject("managed field-data handle is duplicated");
    }

    private static byte[] ReadBlob(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length is < 0 or > MaximumMetadataBytes || reader.BaseStream.Length - reader.BaseStream.Position < length)
            throw new ArgumentException("Managed bootstrap blob is truncated or exceeds its deterministic budget.");
        return reader.ReadBytes(length);
    }

    private static T[] ReadRows<T>(BinaryReader reader, int maximum, Func<BinaryReader, T> read)
    {
        int count = reader.ReadInt32();
        if (count is < 0 || count > maximum) throw new ArgumentException("Managed bootstrap row count exceeds its deterministic budget.");
        T[] rows = new T[count];
        for (int index = 0; index < count; index++) rows[index] = read(reader);
        return rows;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 65535) throw new ArgumentException("Managed bootstrap string exceeds its deterministic budget.", nameof(value));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length is < 0 or > 65535 || reader.BaseStream.Length - reader.BaseStream.Position < length)
            throw new ArgumentException("Managed bootstrap string is truncated or exceeds its deterministic budget.");
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}
