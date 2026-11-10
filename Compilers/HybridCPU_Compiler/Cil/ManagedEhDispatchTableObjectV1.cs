using System.Buffers.Binary;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;

namespace HybridCPU.Compiler.Cil;

public sealed record ManagedEhDispatchTableMethodV1(
    string MethodIdentity,
    int CodeSizeBytes,
    int GcInfoSizeBytes,
    int UnwindInfoSizeBytes,
    int EhInfoSizeBytes,
    int FinallyInfoSizeBytes,
    HybridCpuManagedUnwindRecordV2? Unwind = null);

public sealed record ManagedEhDispatchTypeV1(ulong TypeHandle, ulong TypeId, ulong BaseTypeId);

/// <summary>
/// Image-native EH index. Absolute addresses are linker-owned relocations; payload sizes and
/// method ordering are compiler-owned and deterministic. The table contains no host pointers.
/// </summary>
public static class ManagedEhDispatchTableObjectV1
{
    public const string Symbol = "__hybridcpu_managed_eh_dispatch_table";
    public const string CountSymbol = "__hybridcpu_managed_eh_dispatch_count";
    public const string TypeTableSymbol = "__hybridcpu_managed_eh_type_table";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-dispatch-table/v1";
    public const int RowSizeBytes = HybridCpuManagedEhDispatchIndexV1.RowSizeBytes;

    public static HybridCpuObjectArtifactV1 Emit(IReadOnlyList<ManagedEhDispatchTableMethodV1> methods) =>
        Emit(methods, [], HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize);

    public static HybridCpuObjectArtifactV1 Emit(IReadOnlyList<ManagedEhDispatchTableMethodV1> methods,
        IReadOnlyList<ManagedEhDispatchTypeV1> types, ulong stackBase, ulong stackSize,
        bool bindRuntimeHelpers = false, bool bindStringLiterals = false, bool bindDispatch = false,
        bool bindProcessExitHelper = false, bool bindFinallyContinuationHelpers = false)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(types);
        ManagedEhDispatchTableMethodV1[] rows = methods.OrderBy(static row => row.MethodIdentity, StringComparer.Ordinal).ToArray();
        ManagedEhDispatchTypeV1[] typeRows = types.OrderBy(static row => row.TypeHandle).ToArray();
        if (stackBase == 0 || stackSize == 0 || stackBase % 16 != 0 || stackSize % 16 != 0 ||
            stackBase > ulong.MaxValue - stackSize ||
            rows.Length == 0 || rows.Length > HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.MaximumCodeManagerRecords ||
            rows.Select(static row => row.MethodIdentity).Distinct(StringComparer.Ordinal).Count() != rows.Length ||
            rows.Any(static row => string.IsNullOrWhiteSpace(row.MethodIdentity) || row.CodeSizeBytes <= 0 ||
                row.GcInfoSizeBytes <= 0 || row.UnwindInfoSizeBytes <= 0 || row.EhInfoSizeBytes < 0 ||
                row.FinallyInfoSizeBytes < 0))
            throw new ArgumentException("EH dispatch table methods are empty, duplicated, malformed or over budget.", nameof(methods));
        if (typeRows.Length > HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.MaximumManagedTypes ||
            typeRows.Where((row, index) => row.TypeHandle != checked((ulong)(index + 1)) || row.TypeId == 0).Any() ||
            typeRows.Select(static row => row.TypeId).Distinct().Count() != typeRows.Length ||
            typeRows.Any(row => row.BaseTypeId != 0 && !typeRows.Any(candidate => candidate.TypeId == row.BaseTypeId)))
            throw new ArgumentException("EH type rows must be contiguous exact handles with closed base dependencies.", nameof(types));

        int typeTableOffset = checked(HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes + rows.Length * RowSizeBytes);
        byte[] data = new byte[checked(typeTableOffset + typeRows.Length * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, HybridCpuManagedEhDispatchIndexV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), HybridCpuManagedEhDispatchIndexV1.Version);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(HybridCpuManagedEhDispatchIndexV1.CountOffset), rows.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(HybridCpuManagedEhDispatchIndexV1.TypeCountOffset), typeRows.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(HybridCpuManagedEhDispatchIndexV1.StackBaseOffset), stackBase);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(HybridCpuManagedEhDispatchIndexV1.StackEndOffset), stackBase + stackSize);
        List<HybridCpuObjectSymbolV1> symbols = new List<HybridCpuObjectSymbolV1>
        {
            new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".hcehindex", 0, (ulong)data.Length, IsDefinition: true),
            new(CountSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".hcehindex", HybridCpuManagedEhDispatchIndexV1.CountOffset, 4, IsDefinition: true)
        };
        if (typeRows.Length != 0)
            symbols.Add(new(TypeTableSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".hcehindex", checked((ulong)typeTableOffset), checked((ulong)(typeRows.Length * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes)), IsDefinition: true));
        List<HybridCpuObjectRelocationV1> relocations = new List<HybridCpuObjectRelocationV1>(rows.Length * 4);
        if (bindRuntimeHelpers)
        {
            AddReference(HybridCpuManagedEhFrameLookupEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.FindFrameHelperOffset);
            AddReference(HybridCpuManagedEhCatchSelectorEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.CatchSelectorHelperOffset);
            AddReference(HybridCpuManagedEhUnwindStepEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.UnwindStepHelperOffset);
            AddReference(HybridCpuManagedExceptionTransferObjectV1.Symbol, HybridCpuManagedEhDispatchIndexV1.TransferHelperOffset);
            AddReference(HybridCpuManagedEhCatchDispatchEmitterV1.UnhandledProcessExitSymbol,
                HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
            AddReference(HybridCpuManagedEhStateObjectV1.Symbol, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
            AddReference(HybridCpuManagedEhCatchDispatchEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.CatchDispatchHelperOffset);
            AddReference(HybridCpuManagedEhScopeEmitterV1.RethrowSymbol, HybridCpuManagedEhDispatchIndexV1.RethrowHelperOffset);
            AddReference(HybridCpuManagedEhScopeEmitterV1.LeaveCatchSymbol, HybridCpuManagedEhDispatchIndexV1.LeaveCatchHelperOffset);
            AddReference(HybridCpuManagedEhFinallySelectorEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.FinallySelectorHelperOffset);
            AddReference(HybridCpuManagedEhExceptionalResumeEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.FinallyResumeHelperOffset);
        }
        else if (bindFinallyContinuationHelpers)
        {
            AddReference(HybridCpuManagedEhFrameLookupEmitterV1.Symbol,
                HybridCpuManagedEhDispatchIndexV1.FindFrameHelperOffset);
            AddReference(HybridCpuManagedEhCatchDispatchEmitterV1.UnhandledProcessExitSymbol,
                HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        }
        else if (bindProcessExitHelper)
        {
            AddReference(HybridCpuManagedEhCatchDispatchEmitterV1.UnhandledProcessExitSymbol,
                HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        }
        if (bindStringLiterals)
        {
            AddReference(HybridCpuManagedStringLiteralTableV1.Symbol, HybridCpuManagedEhDispatchIndexV1.StringLiteralTableOffset);
            AddReference(HybridCpuManagedLdstrEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.LdstrHelperOffset);
        }
        if (bindDispatch)
        {
            AddReference(HybridCpuManagedDispatchMetadataEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.DispatchMetadataOffset);
            AddReference(HybridCpuManagedDispatchResolverEmitterV1.InterfaceSymbol, HybridCpuManagedEhDispatchIndexV1.ResolveInterfaceHelperOffset);
            AddReference(HybridCpuManagedDispatchResolverEmitterV1.VirtualSymbol, HybridCpuManagedEhDispatchIndexV1.ResolveVirtualHelperOffset);
        }
        for (int index = 0; index < rows.Length; index++)
        {
            ManagedEhDispatchTableMethodV1 row = rows[index];
            int offset = HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes + index * RowSizeBytes;
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset), row.CodeSizeBytes);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.EhSizeOffset), row.EhInfoSizeBytes);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.UnwindSizeOffset), row.UnwindInfoSizeBytes);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.GcSizeOffset), row.GcInfoSizeBytes);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.FinallySizeOffset), row.FinallyInfoSizeBytes);
            HybridCpuManagedUnwindRecordV2 unwind = row.Unwind ?? throw new ArgumentException(
                $"Managed frame '{row.MethodIdentity}' lacks its finalized unwind-v2 plan.", nameof(methods));
            if (unwind.SavedRegisters.Count > HybridCpuManagedEhDispatchIndexV1.MaximumSavedRegisters)
                throw new ArgumentException("Managed unwind saved-register count exceeds the image index.", nameof(methods));
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.CfaBaseOffset), (int)unwind.CfaBase);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.CfaOffsetOffset), unwind.CfaOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.ReturnRegisterOffset), unwind.ReturnPcRegisterId ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.ReturnStackOffset), unwind.ReturnPcCfaRelativeOffsetBytes ?? int.MinValue);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.SavedCountOffset), unwind.SavedRegisters.Count);
            int savedOffset = offset + HybridCpuManagedEhDispatchIndexV1.SavedRowsOffset;
            foreach (HybridCpuManagedSavedRegisterV1 saved in unwind.SavedRegisters.OrderBy(static saved => saved.RegisterId))
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(savedOffset), saved.RegisterId);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(savedOffset + 4), saved.CfaRelativeOffsetBytes);
                savedOffset += HybridCpuManagedEhDispatchIndexV1.SavedRowSizeBytes;
            }
            AddReference(row.MethodIdentity, offset + HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
            if (row.EhInfoSizeBytes != 0)
                AddReference(ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(row.MethodIdentity, "eh"), offset + HybridCpuManagedEhDispatchIndexV1.EhAddressOffset);
            AddReference(ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(row.MethodIdentity, "unwind"), offset + HybridCpuManagedEhDispatchIndexV1.UnwindAddressOffset);
            AddReference(ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(row.MethodIdentity, "gc"), offset + HybridCpuManagedEhDispatchIndexV1.GcAddressOffset);
            if (row.FinallyInfoSizeBytes != 0)
                AddReference(ScalarControlFlowV2ObjectLinkerV1.MetadataSymbol(row.MethodIdentity, "finally"),
                    offset + HybridCpuManagedEhDispatchIndexV1.FinallyAddressOffset);
        }
        for (int index = 0; index < typeRows.Length; index++)
        {
            int offset = typeTableOffset + index * HybridCpuManagedEhDispatchIndexV1.TypeRowSizeBytes;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.TypeHandleOffset), typeRows[index].TypeHandle);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.TypeIdOffset), typeRows[index].TypeId);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.BaseTypeIdOffset), typeRows[index].BaseTypeId);
            ulong baseHandle = typeRows[index].BaseTypeId == 0 ? 0 : typeRows.Single(candidate =>
                candidate.TypeId == typeRows[index].BaseTypeId).TypeHandle;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + HybridCpuManagedEhDispatchIndexV1.BaseTypeHandleOffset), baseHandle);
        }
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".hcehindex", HybridCpuObjectSectionKind.ReadOnlyData, 8, data, (ulong)data.Length)],
            symbols, relocations, HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));

        void AddReference(string target, int offset)
        {
            symbols.Add(new(target, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                null, 0, 0, IsDefinition: false));
            relocations.Add(new(".hcehindex", checked((ulong)offset), HybridCpuRelocationKind.Absolute64, target, 0));
        }
    }
}
