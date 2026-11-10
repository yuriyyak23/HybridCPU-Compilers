using System.Buffers.Binary;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Threading;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Auth;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

#pragma warning disable CS0618

internal sealed record AppCompilerEmissionStep(
    string Mnemonic,
    string HelperName,
    Instruction ExpectedOpcode,
    Action<AppAsmFacade> Emit,
    byte ExpectedRd,
    byte ExpectedRs1,
    byte ExpectedRs2,
    ushort ExpectedImmediate = 0,
    DataTypeEnum? ExpectedDataType = null);

internal sealed record PlatformCompilerEmissionStep(
    string Mnemonic,
    string HelperName,
    Instruction ExpectedOpcode,
    Action<PlatformAsmFacade> Emit,
    byte ExpectedRd,
    byte ExpectedRs1,
    byte ExpectedRs2,
    ushort ExpectedImmediate = 0,
    DataTypeEnum? ExpectedDataType = null);

#pragma warning restore CS0618

internal sealed record DmaStreamComputeCompilerDescriptorInput(
    byte[] DescriptorBytes,
    DmaStreamComputeDescriptorReference Reference,
    DmaStreamComputeOwnerGuardDecision GuardDecision);

internal static class NonVmxCompilerExampleSupport
{
    private const ulong OwnerDomainTag = 0xD0A11UL;

    private const ulong DmaDescriptorIdentityHash = 0xA11CE5EEDUL;
    private const int DmaHeaderSize = 128;
    private const int DmaRangeEntrySize = 16;

    private const uint OwnerContextId = 77;
    private const uint OwnerCoreId = 1;
    private const uint OwnerPodId = 2;

    public static CpuExampleResult RunAppFacadeExample(
        string output,
        IReadOnlyList<AppCompilerEmissionStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
#pragma warning restore CS0618

        foreach (AppCompilerEmissionStep step in steps)
        {
            step.Emit(facade);
        }

        return BuildCompilerEmissionResult(
            output,
            context,
            steps.Select(static step => new CompilerEmissionExpectation(
                step.Mnemonic,
                step.HelperName,
                step.ExpectedOpcode,
                step.ExpectedRd,
                step.ExpectedRs1,
                step.ExpectedRs2,
                step.ExpectedImmediate,
                step.ExpectedDataType)).ToArray());
    }

    public static CpuExampleResult RunPlatformFacadeExample(
        string output,
        IReadOnlyList<PlatformCompilerEmissionStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
#pragma warning disable CS0618
        PlatformAsmFacade facade = new PlatformAsmFacade(0, context);
#pragma warning restore CS0618

        foreach (PlatformCompilerEmissionStep step in steps)
        {
            step.Emit(facade);
        }

        return BuildCompilerEmissionResult(
            output,
            context,
            steps.Select(static step => new CompilerEmissionExpectation(
                step.Mnemonic,
                step.HelperName,
                step.ExpectedOpcode,
                step.ExpectedRd,
                step.ExpectedRs1,
                step.ExpectedRs2,
                step.ExpectedImmediate,
                step.ExpectedDataType)).ToArray());
    }

    public static DmaStreamComputeCompilerDescriptorInput CreateDmaStreamComputeDescriptorInput()
    {
        byte[] descriptorBytes = BuildDmaStreamComputeDescriptor();
        DmaStreamComputeDescriptorReference reference = new DmaStreamComputeDescriptorReference(
            descriptorAddress: 0x8000,
            descriptorSize: (uint)descriptorBytes.Length,
            descriptorIdentityHash: DmaDescriptorIdentityHash);

        DmaStreamComputeStructuralReadResult structuralRead =
            DmaStreamComputeDescriptorParser.ReadStructuralOwnerBinding(
                descriptorBytes,
                reference);
        if (!structuralRead.IsValid)
        {
            throw new InvalidOperationException(structuralRead.Message);
        }

        DmaStreamComputeOwnerBinding ownerBinding =
            structuralRead.RequireOwnerBindingForGuard();
        DmaStreamComputeOwnerGuardContext guardContext = new DmaStreamComputeOwnerGuardContext(
            ownerBinding.OwnerVirtualThreadId,
            ownerBinding.OwnerContextId,
            ownerBinding.OwnerCoreId,
            ownerBinding.OwnerPodId,
            ownerBinding.OwnerDomainTag,
            activeDomainCertificate: ownerBinding.OwnerDomainTag,
            deviceId: ownerBinding.DeviceId);
        DmaStreamComputeOwnerGuardDecision guardDecision =
            new SafetyVerifier().EvaluateDmaStreamComputeOwnerGuard(
                ownerBinding,
                guardContext);

        if (!guardDecision.IsAllowed)
        {
            throw new InvalidOperationException(guardDecision.Message);
        }

        return new DmaStreamComputeCompilerDescriptorInput(
            descriptorBytes,
            reference,
            guardDecision);
    }

    public static DmaStreamComputeDescriptor CreateDmaStreamComputeDescriptor()
    {
        DmaStreamComputeCompilerDescriptorInput input = CreateDmaStreamComputeDescriptorInput();
        DmaStreamComputeValidationResult validation =
            DmaStreamComputeDescriptorParser.Parse(
                input.DescriptorBytes,
                input.GuardDecision,
                input.Reference);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        return validation.RequireDescriptorForAdmission();
    }

    public static AcceleratorCommandDescriptor CreateAcceleratorSubmitDescriptor()
    {
        AcceleratorOwnerBinding ownerBinding = new AcceleratorOwnerBinding
        {
            OwnerVirtualThreadId = 0,
            OwnerContextId = OwnerContextId,
            OwnerCoreId = OwnerCoreId,
            OwnerPodId = OwnerPodId,
            DomainTag = OwnerDomainTag
        };

        AcceleratorGuardEvidence guardEvidence =
            AcceleratorGuardEvidence.FromGuardPlane(
                ownerBinding,
                activeDomainCertificate: ownerBinding.DomainTag);
        AcceleratorGuardDecision guardDecision =
            AcceleratorOwnerDomainGuard.Default.EnsureBeforeDescriptorAcceptance(
                ownerBinding,
                guardEvidence);
        if (!guardDecision.IsAllowed)
        {
            throw new InvalidOperationException(guardDecision.Message);
        }

        AcceleratorMemoryRange[] sourceRanges =
        [
            new(0x1000, 0x40),
            new(0x2000, 0x40)
        ];
        AcceleratorMemoryRange[] destinationRanges =
        [
            new(0x9000, 0x40)
        ];
        AcceleratorMemoryRange[] scratchRanges = [];

        IReadOnlyList<AcceleratorMemoryRange> normalizedSourceRanges =
            AcceleratorDescriptorParser.NormalizeMemoryRanges(sourceRanges);
        IReadOnlyList<AcceleratorMemoryRange> normalizedDestinationRanges =
            AcceleratorDescriptorParser.NormalizeMemoryRanges(destinationRanges);
        IReadOnlyList<AcceleratorMemoryRange> normalizedScratchRanges =
            AcceleratorDescriptorParser.NormalizeMemoryRanges(scratchRanges);
        AcceleratorAlignmentRequirement alignment = new AcceleratorAlignmentRequirement(16);
        ulong footprintHash =
            AcceleratorDescriptorParser.ComputeNormalizedFootprintHash(
                AcceleratorClassId.Matrix,
                AcceleratorDeviceId.ReferenceMatMul,
                AcceleratorOperationKind.MatMul,
                AcceleratorDatatype.Float32,
                AcceleratorShapeKind.Matrix2D,
                shapeRank: 2,
                elementCount: 64,
                AcceleratorPartialCompletionPolicy.AllOrNone,
                alignment,
                normalizedSourceRanges,
                normalizedDestinationRanges,
                normalizedScratchRanges);

        AcceleratorDescriptorIdentity identity = new AcceleratorDescriptorIdentity(
            DescriptorIdentityHash: 0xACCE5510UL,
            NormalizedFootprintHash: footprintHash);
        const uint descriptorSize = (uint)(AcceleratorDescriptorParser.CurrentHeaderSize + (3 * 16));
        AcceleratorDescriptorHeader header = new AcceleratorDescriptorHeader(
            Magic: AcceleratorDescriptorParser.Magic,
            AbiVersion: AcceleratorDescriptorParser.CurrentAbiVersion,
            HeaderSize: AcceleratorDescriptorParser.CurrentHeaderSize,
            DescriptorSize: descriptorSize,
            AcceleratorClass: AcceleratorClassId.Matrix,
            AcceleratorId: AcceleratorDeviceId.ReferenceMatMul,
            Operation: AcceleratorOperationKind.MatMul,
            Datatype: AcceleratorDatatype.Float32,
            Shape: AcceleratorShapeKind.Matrix2D,
            ShapeRank: 2,
            SourceRangeCount: (ushort)sourceRanges.Length,
            DestinationRangeCount: (ushort)destinationRanges.Length,
            ScratchRangeCount: 0,
            PartialCompletionPolicy: AcceleratorPartialCompletionPolicy.AllOrNone,
            Alignment: alignment,
            ElementCount: 64,
            CapabilityVersion: 1,
            Identity: identity,
            OwnerBinding: ownerBinding,
            ScratchRequiredBytes: 0);

        return new AcceleratorCommandDescriptor
        {
            DescriptorReference = new AcceleratorDescriptorReference(
                DescriptorAddress: 0x8800,
                DescriptorSize: descriptorSize,
                DescriptorIdentityHash: identity.DescriptorIdentityHash),
            Header = header,
            AbiVersion = header.AbiVersion,
            HeaderSize = header.HeaderSize,
            DescriptorSize = header.DescriptorSize,
            AcceleratorClass = header.AcceleratorClass,
            AcceleratorId = header.AcceleratorId,
            Operation = header.Operation,
            Datatype = header.Datatype,
            Shape = header.Shape,
            ShapeRank = header.ShapeRank,
            ElementCount = header.ElementCount,
            CapabilityVersion = header.CapabilityVersion,
            Alignment = header.Alignment,
            ScratchRequirement = new AcceleratorScratchRequirement(0, scratchRanges),
            PartialCompletionPolicy = header.PartialCompletionPolicy,
            OwnerBinding = ownerBinding,
            OwnerGuardDecision = guardDecision,
            Identity = identity,
            SourceRanges = sourceRanges,
            DestinationRanges = destinationRanges,
            ScratchRanges = scratchRanges,
            NormalizedFootprint = new AcceleratorNormalizedFootprint
            {
                SourceRanges = normalizedSourceRanges,
                DestinationRanges = normalizedDestinationRanges,
                ScratchRanges = normalizedScratchRanges,
                Hash = footprintHash
            }
        }.Freeze();
    }

    public static IReadOnlyList<string> DescribeInstruction(string caption, in VLIW_Instruction instruction)
    {
        List<string> notes = [caption];
        notes.AddRange(CpuInstructionDescriber.Describe(in instruction));
        return notes;
    }

    private static CpuExampleResult BuildCompilerEmissionResult(
        string output,
        HybridCpuThreadCompilerContext context,
        IReadOnlyList<CompilerEmissionExpectation> expectations)
    {
        VLIW_Instruction[] instructions = context.GetCompiledInstructions().ToArray();
        if (instructions.Length != expectations.Count)
        {
            throw new InvalidOperationException(
                $"Expected {expectations.Count} emitted instruction(s), got {instructions.Length}.");
        }

        List<string> notes = [];
        for (int index = 0; index < instructions.Length; index++)
        {
            VLIW_Instruction instruction = instructions[index];
            CompilerEmissionExpectation expectation = expectations[index];
            ValidateCompilerCarrier(in instruction, expectation, index);

            if (!VLIW_Instruction.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2))
            {
                throw new InvalidOperationException(
                    $"{expectation.Mnemonic}: emitted word1 is not a packed architectural register tuple.");
            }

            notes.Add(
                $"{expectation.Mnemonic} via {expectation.HelperName}: opcode={(uint)expectation.ExpectedOpcode}, rd=x{rd}, rs1=x{rs1}, rs2=x{rs2}, imm={instruction.Immediate}, dataType={instruction.DataTypeValue}");
        }

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        if (compiledProgram.BundleLayout.Program.Instructions.Count != expectations.Count)
        {
            throw new InvalidOperationException(
                $"Expected {expectations.Count} IR instruction(s), got {compiledProgram.BundleLayout.Program.Instructions.Count}.");
        }

        notes.Add(
            $"canonical compile accepted {compiledProgram.BundleLayout.Program.Instructions.Count} instruction(s) into {compiledProgram.BundleCount} lowered bundle(s)");
        notes.Add(
            $"typed-slot agreement valid = {compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid}");

        return CpuExampleResult.Ok(output, notes: notes);
    }

    private static void ValidateCompilerCarrier(
        in VLIW_Instruction instruction,
        CompilerEmissionExpectation expectation,
        int index)
    {
        if ((Instruction)instruction.OpCode != expectation.ExpectedOpcode)
        {
            throw new InvalidOperationException(
                $"{expectation.Mnemonic}: expected opcode {expectation.ExpectedOpcode}, got {(Instruction)instruction.OpCode} at index {index}.");
        }

        if (expectation.ExpectedDataType is { } expectedDataType &&
            instruction.DataTypeValue != expectedDataType)
        {
            throw new InvalidOperationException(
                $"{expectation.Mnemonic}: expected data type {expectedDataType}, got {instruction.DataTypeValue}.");
        }

        if (instruction.Immediate != expectation.ExpectedImmediate)
        {
            throw new InvalidOperationException(
                $"{expectation.Mnemonic}: expected immediate {expectation.ExpectedImmediate}, got {instruction.Immediate}.");
        }

        if (!VLIW_Instruction.TryUnpackArchRegs(instruction.Word1, out byte rd, out byte rs1, out byte rs2))
        {
            throw new InvalidOperationException(
                $"{expectation.Mnemonic}: emitted word1 is not a packed architectural register tuple.");
        }

        if (rd != expectation.ExpectedRd ||
            rs1 != expectation.ExpectedRs1 ||
            rs2 != expectation.ExpectedRs2)
        {
            throw new InvalidOperationException(
                $"{expectation.Mnemonic}: expected rd/rs1/rs2 x{expectation.ExpectedRd}/x{expectation.ExpectedRs1}/x{expectation.ExpectedRs2}, got x{rd}/x{rs1}/x{rs2}.");
        }
    }

    private static byte[] BuildDmaStreamComputeDescriptor()
    {
        const ushort sourceRangeCount = 1;
        const ushort destinationRangeCount = 1;
        const int sourceRangeTableOffset = DmaHeaderSize;
        const int destinationRangeTableOffset = DmaHeaderSize + DmaRangeEntrySize;
        const int totalSizeBytes = DmaHeaderSize + (2 * DmaRangeEntrySize);
        const uint totalSize = totalSizeBytes;

        byte[] bytes = new byte[totalSizeBytes];
        WriteUInt32(bytes, 0, DmaStreamComputeDescriptorParser.Magic);
        WriteUInt16(bytes, 4, DmaStreamComputeDescriptorParser.CurrentAbiVersion);
        WriteUInt16(bytes, 6, DmaHeaderSize);
        WriteUInt32(bytes, 8, totalSize);
        WriteUInt64(bytes, 24, DmaDescriptorIdentityHash);
        WriteUInt64(bytes, 32, 0xC011EC7EUL);
        WriteUInt16(bytes, 40, (ushort)DmaStreamComputeOperationKind.Copy);
        WriteUInt16(bytes, 42, (ushort)DmaStreamComputeElementType.UInt32);
        WriteUInt16(bytes, 44, (ushort)DmaStreamComputeShapeKind.Contiguous1D);
        WriteUInt16(bytes, 46, (ushort)DmaStreamComputeRangeEncoding.InlineContiguous);
        WriteUInt16(bytes, 48, sourceRangeCount);
        WriteUInt16(bytes, 50, destinationRangeCount);
        WriteUInt16(bytes, 56, (ushort)DmaStreamComputePartialCompletionPolicy.AllOrNone);
        WriteUInt16(bytes, 60, 0);
        WriteUInt32(bytes, 64, OwnerContextId);
        WriteUInt32(bytes, 68, OwnerCoreId);
        WriteUInt32(bytes, 72, OwnerPodId);
        WriteUInt32(bytes, 76, DmaStreamComputeDescriptor.CanonicalLane6DeviceId);
        WriteUInt64(bytes, 80, OwnerDomainTag);
        WriteUInt32(bytes, 96, (uint)sourceRangeTableOffset);
        WriteUInt32(bytes, 100, (uint)destinationRangeTableOffset);

        WriteRange(bytes, sourceRangeTableOffset, 0x1000, 16);
        WriteRange(bytes, destinationRangeTableOffset, 0x9000, 16);
        return bytes;
    }

    private static void WriteRange(byte[] bytes, int offset, ulong address, ulong length)
    {
        WriteUInt64(bytes, offset, address);
        WriteUInt64(bytes, offset + sizeof(ulong), length);
    }

    private static void WriteUInt16(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteUInt64(byte[] bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);

    private sealed record CompilerEmissionExpectation(
        string Mnemonic,
        string HelperName,
        Instruction ExpectedOpcode,
        byte ExpectedRd,
        byte ExpectedRs1,
        byte ExpectedRs2,
        ushort ExpectedImmediate,
        DataTypeEnum? ExpectedDataType);
}
