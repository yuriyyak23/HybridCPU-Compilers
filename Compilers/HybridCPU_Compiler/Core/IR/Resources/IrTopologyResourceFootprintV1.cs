using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR.Resources;

public enum IrAddressEvidenceKindV1 : byte { Exact, FiniteSet, All, Unknown }

public sealed record IrAddressResourceEvidenceV1(
    IrAddressEvidenceKindV1 Precision,
    IReadOnlyList<int> ResourceIds)
{
    public static IrAddressResourceEvidenceV1 Unknown { get; } = new(IrAddressEvidenceKindV1.Unknown, Array.Empty<int>());
}

[Flags]
public enum CompilerCertificateClassV1 : byte
{
    None = 0,
    Lane6Stream = 1,
    Lane7Control = 2
}

/// <summary>Static compiler evidence only; it is not a runtime resource certificate.</summary>
public sealed record IrTopologyResourceFootprintV1(
    int InstructionIndex,
    byte VirtualThreadId,
    ushort RegisterReadGroupMask,
    ushort RegisterWriteGroupMask,
    int RequiredPrfReadPorts,
    int RequiredPrfWritePorts,
    IrResourceFactPrecisionV1 RegisterPrecision,
    IrResourceFactPrecisionV1 PrfPortPrecision,
    IrAddressResourceEvidenceV1 Banks,
    IrAddressResourceEvidenceV1 Channels,
    CompilerCertificateClassV1 CertificateClass,
    IrResourceFactPrecisionV1 CertificatePrecision,
    string TopologyDigest,
    string Fingerprint);

public static class IrTopologyResourceFootprintBuilderV1
{
    public static IrTopologyResourceFootprintV1 Build(IrInstruction instruction, HybridCpuMachineTopologyV1 topology)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(topology);

        bool registerUnknown = instruction.VirtualThreadId >= HybridCpuMachineTopologyV1.VirtualThreadCount;
        ushort reads = 0, writes = 0;
        int readPorts = 0, writePorts = 0;
        AddRegisters(instruction.Annotation.Uses, topology, ref reads, ref readPorts, ref registerUnknown);
        AddRegisters(instruction.Annotation.Defs, topology, ref writes, ref writePorts, ref registerUnknown);

        IrResourceFactPrecisionV1 registerPrecision = registerUnknown
            ? IrResourceFactPrecisionV1.Unknown : IrResourceFactPrecisionV1.Exact;
        IrResourceFactPrecisionV1 portPrecision = registerPrecision;
        IrAddressResourceEvidenceV1 banks = BuildAddressEvidence(instruction, topology.MemoryBankCount, topology.MemoryBankWidthBytes);
        IrAddressResourceEvidenceV1 channels = BuildAddressEvidence(instruction, topology.MemoryChannelCount, topology.MemoryChannelWidthBytes);
        CompilerCertificateClassV1 certificateClass = instruction.Annotation.RequiredSlotClass switch
        {
            IrSlotClass.DmaStreamClass or IrSlotClass.MatrixTileStreamClass => CompilerCertificateClassV1.Lane6Stream,
            IrSlotClass.BranchControl or IrSlotClass.SystemSingleton => CompilerCertificateClassV1.Lane7Control,
            _ => CompilerCertificateClassV1.None
        };
        IrResourceFactPrecisionV1 certificatePrecision = instruction.Annotation.RequiredSlotClass == IrSlotClass.Unclassified
            ? IrResourceFactPrecisionV1.Unknown : IrResourceFactPrecisionV1.Exact;

        string input = string.Join('|', instruction.Index, instruction.VirtualThreadId, reads, writes,
            readPorts, writePorts, (byte)registerPrecision, (byte)portPrecision,
            AddressKey(banks), AddressKey(channels), (byte)certificateClass,
            (byte)certificatePrecision, topology.ContractDigest);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return new(instruction.Index, instruction.VirtualThreadId, reads, writes, readPorts, writePorts,
            registerPrecision, portPrecision, banks, channels, certificateClass,
            certificatePrecision, topology.ContractDigest, fingerprint);
    }

    private static void AddRegisters(
        IReadOnlyList<IrOperand> operands,
        HybridCpuMachineTopologyV1 topology,
        ref ushort mask,
        ref int ports,
        ref bool unknown)
    {
        foreach (IrOperand operand in operands)
        {
            if (operand.Kind != IrOperandKind.Pointer)
            {
                if (operand.Kind is IrOperandKind.Tile) unknown = true;
                continue;
            }
            if (operand.Value > HybridCpuMachineTopologyV1.MaximumRepresentableRegisterId)
            {
                unknown = true;
                continue;
            }
            mask |= checked((ushort)(1 << topology.GetRegisterGroup((int)operand.Value)));
            ports++;
        }
    }

    private static IrAddressResourceEvidenceV1 BuildAddressEvidence(
        IrInstruction instruction,
        int? resourceCount,
        int? widthBytes)
    {
        IrMemoryRegion? read = instruction.Annotation.MemoryReadRegion;
        IrMemoryRegion? write = instruction.Annotation.MemoryWriteRegion;
        if (read is null && write is null)
            return new IrAddressResourceEvidenceV1(IrAddressEvidenceKindV1.Exact, Array.Empty<int>());
        if (resourceCount is null || widthBytes is null)
            return IrAddressResourceEvidenceV1.Unknown;

        SortedSet<int> ids = new SortedSet<int>();
        foreach (IrMemoryRegion region in new[] { read, write }.Where(static region => region is not null)!)
        {
            ulong length = Math.Max(1U, region.Length);
            ulong startLine = region.Address / (ulong)widthBytes.Value;
            ulong endAddress = region.Address > ulong.MaxValue - (length - 1)
                ? ulong.MaxValue : region.Address + length - 1;
            ulong endLine = endAddress / (ulong)widthBytes.Value;
            ulong lineCount = endLine - startLine + 1;
            if (lineCount > HybridCpuMachineTopologyV1.MaximumFiniteAddressSet)
                return new IrAddressResourceEvidenceV1(IrAddressEvidenceKindV1.All, Array.Empty<int>());
            for (ulong offset = 0; offset < lineCount; offset++)
                ids.Add((int)((startLine + offset) % (ulong)resourceCount.Value));
        }
        if (ids.Count > HybridCpuMachineTopologyV1.MaximumFiniteAddressSet)
            return new IrAddressResourceEvidenceV1(IrAddressEvidenceKindV1.All, Array.Empty<int>());
        int[] result = ids.ToArray();
        return new IrAddressResourceEvidenceV1(
            result.Length <= 1 ? IrAddressEvidenceKindV1.Exact : IrAddressEvidenceKindV1.FiniteSet,
            Array.AsReadOnly(result));
    }

    private static string AddressKey(IrAddressResourceEvidenceV1 evidence) =>
        $"{(byte)evidence.Precision}:{string.Join(',', evidence.ResourceIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))}";
}
