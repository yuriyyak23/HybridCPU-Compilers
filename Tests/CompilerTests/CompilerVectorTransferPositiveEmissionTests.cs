using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerVectorTransferPositiveEmissionTests
{
    public static IEnumerable<object[]> GoldenVectors()
    {
        yield return
        [
            new VectorTransferGoldenVector(
                CompilerVectorTransferPositiveEmissionKind.Vload,
                InstructionsEnum.VLOAD,
                DestinationBase: 0x200UL,
                SourceBase: 0x300UL,
                DataTypeEnum.INT32,
                ElementCount: 4,
                StrideBytes: 4,
                PredicateMask: 0x0F)
        ];
        yield return
        [
            new VectorTransferGoldenVector(
                CompilerVectorTransferPositiveEmissionKind.Vstore,
                InstructionsEnum.VSTORE,
                DestinationBase: 0x380UL,
                SourceBase: 0x280UL,
                DataTypeEnum.INT32,
                ElementCount: 4,
                StrideBytes: 4,
                PredicateMask: 0x0F)
        ];
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void FacadeHelpers_EmitDirectVectorTransferGoldenCarrierThroughCompilerIrAndRuntimeProjection(
        VectorTransferGoldenVector vector)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

#pragma warning disable CS0618
        PlatformAsmFacade facade = new PlatformAsmFacade(0, context);
        EmitGoldenVector(facade, vector);
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        AssertVectorCarrier(vector, raw);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(vector.Opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.Memory, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(IrResourceClass.LoadStore, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.LsuClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.True(ir.Annotation.MayTrap);
        Assert.Null(ir.MatrixTileEmission);
        Assert.NotNull(ir.VectorTransferEmission);

        CompilerVectorTransferEmissionPlan plan = ir.VectorTransferEmission!;
        Assert.Equal(vector.Opcode, (InstructionsEnum)(uint)plan.Request.Opcode);
        Assert.Equal(vector.Mnemonic, plan.Request.Mnemonic);
        Assert.Equal((uint)vector.Opcode, plan.RuntimeOpcodeInfo.OpCode);
        Assert.Equal(InstructionClass.Memory, (InstructionClass)(byte)plan.RuntimeOpcodeInfo.InstructionClass);
        Assert.Equal(CompilerVectorTransferPositiveEmissionAbiContract.RuntimeHandoffReference, plan.RuntimeHandoffReference);
        Assert.True(plan.RuntimeOwnedLegalityIsFinal);
        Assert.False(plan.UsesFallbackPath);
        Assert.False(plan.UsesAliasPromotion);
        Assert.False(plan.UsesScalarVectorDotOrBackendFallback);
        Assert.False(plan.UsesBaseMemoryFallback);
        Assert.False(plan.UsesBaseVectorFallback);
        Assert.False(plan.UsesScalarHelperFallback);
        Assert.False(plan.UsesWideningFmaFallback);
        Assert.False(plan.UsesVectorTransposeOrSegmentFallback);
        AssertCarrier(raw, plan.EncodedInstruction);
        AssertVectorIrContract(vector, ir);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        AssertCarrier(raw, lowered);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);
        VectorTransferMicroOp microOp = Assert.IsType<VectorTransferMicroOp>(carrier);
        Assert.Equal(MicroOpClass.Lsu, microOp.Class);
        Assert.Equal(SlotClass.LsuClass, (SlotClass)(byte)microOp.AdmissionMetadata.Placement.RequiredSlotClass);
        Assert.False(microOp.IsMemoryOp);
        Assert.False(microOp.AdmissionMetadata.IsMemoryOp);
        Assert.Equal((vector.SourceBase, vector.EffectiveLength), Assert.Single(microOp.AdmissionMetadata.ReadMemoryRanges));
        Assert.Equal((vector.DestinationBase, vector.EffectiveLength), Assert.Single(microOp.AdmissionMetadata.WriteMemoryRanges));
    }

    [Fact]
    public void MalformedHelperInputs_FailClosedBeforeEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

#pragma warning disable CS0618
        PlatformAsmFacade facade = new PlatformAsmFacade(0, context);
#pragma warning restore CS0618

        CompilerVectorTransferMemoryAddressAbi destination = CompilerVectorTransferMemoryAddressAbi.Create(0x200);
        CompilerVectorTransferMemoryAddressAbi source = CompilerVectorTransferMemoryAddressAbi.Create(0x300);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            facade.VLoad(
                destination,
                source,
                new CompilerVectorTransferShapeAbi(HybridCpuDataType.INT32, 0, 4)));
        Assert.Equal(0, context.InstructionCount);

        Assert.Throws<ArgumentException>(() =>
            facade.VStore(
                source,
                destination,
                new CompilerVectorTransferShapeAbi(HybridCpuDataType.INT32, 4, 2)));
        Assert.Equal(0, context.InstructionCount);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            facade.VLoad(
                destination,
                source,
                new CompilerVectorTransferShapeAbi((HybridCpuDataType)0xFF, 4, 1)));
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void RawAndSurrogateVectorTransferIngress_IsRejectedInFavorOfTypedHelperAbi()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

        InvalidOperationException directException = Assert.Throws<InvalidOperationException>(() =>
            context.CompileInstruction(
                (uint)InstructionsEnum.VLOAD,
                (byte)DataTypeEnum.INT32,
                predicate: 0,
                immediate: 0,
                destSrc1: 0x200,
                src2: 0x300,
                streamLength: 4,
                stride: 4,
                stealabilityPolicy: StealabilityPolicy.NotStealable));
        Assert.Contains("typed vector load/store helper ABI", directException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);

        InvalidOperationException insertException = Assert.Throws<InvalidOperationException>(() =>
            context.InsertInstruction(
                0,
                (uint)InstructionsEnum.VSTORE,
                (byte)DataTypeEnum.INT32,
                predicate: 0,
                immediate: 0,
                destSrc1: 0x280,
                src2: 0x380,
                streamLength: 4,
                stride: 4,
                stealabilityPolicy: StealabilityPolicy.NotStealable));
        Assert.Contains("typed vector load/store helper ABI", insertException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);

#pragma warning disable CS0618
        PlatformAsmFacade facade = new PlatformAsmFacade(0, context);
#pragma warning restore CS0618

        InvalidOperationException vectorOpException = Assert.Throws<InvalidOperationException>(() =>
            facade.VectorOp(
                InstructionsEnum.VLOAD,
                DataTypeEnum.INT32,
                dest: 0x200,
                src: 0x300,
                streamLength: 4,
                stride: 4));
        Assert.Contains("VectorOp transport is not emission authority", vectorOpException.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void MalformedRecoveredCarrier_FailsClosedBeforeGenericLoadStoreFallback()
    {
        VLIW_Instruction instruction = new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.VLOAD,
            DataTypeValue = DataTypeEnum.INT32,
            DestSrc1Pointer = 0x200,
            Src2Pointer = 0x300,
            StreamLength = 0,
            Stride = 4
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new HybridCpuIrBuilder().BuildProgram(
                virtualThreadId: 0,
                [NativeTransportRuntimeAdapter.ToCore(in instruction)]));

        Assert.Contains("StreamLength == 0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fail closed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorTransferPositiveEmissionInventory_MatchesRuntimeHandoffAndCompilerSurface()
    {
        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicCompilerMethods =
        [
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(HybridCpuThreadCompilerContext).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
        ];

        Assert.True(CompilerVectorTransferPositiveEmissionAbiContract.HasCurrentCompilerImplementation);
        Assert.True(CompilerVectorTransferPositiveEmissionAbiContract.HasCurrentCompilerHelper);
        Assert.True(CompilerVectorTransferPositiveEmissionAbiContract.HasCurrentCompilerEmission);
        Assert.True(CompilerVectorTransferPositiveEmissionAbiContract.UsesRuntimeHandoff);
        Assert.True(CompilerVectorTransferPositiveEmissionAbiContract.RuntimeOwnedLegalityIsFinal);
        Assert.False(CompilerVectorTransferPositiveEmissionAbiContract.AllowsCompilerToOverrideRuntimeLegality);
        Assert.False(CompilerVectorTransferPositiveEmissionAbiContract.UsesFallbackPath);
        Assert.False(CompilerVectorTransferPositiveEmissionAbiContract.UsesAliasPromotion);
        Assert.True(CompilerVectorTransferPositiveEmissionAbiContract.EmitsDirectVectorTransferOpcode);

        foreach (CompilerFailClosedEmissionRow inventoryRow in CompilerFailClosedEmissionInventory.VectorTransferPositiveEmissionRows)
        {
            CompilerVectorTransferPositiveEmissionRow row = Assert.Single(
                CompilerVectorTransferPositiveEmissionAbiContract.Rows,
                candidate => candidate.Mnemonic == inventoryRow.Mnemonic);

            Assert.Equal(Enum.Parse<InstructionsEnum>(inventoryRow.EnumCandidate), (InstructionsEnum)(uint)row.Opcode);
            Assert.Equal((ushort)row.Opcode, row.NumericOpcode);
            Assert.Equal(CompilerVectorTransferPositiveEmissionAbiContract.CompilerPositiveEmissionDecision, inventoryRow.ContractMetadataFragment);
            Assert.True(row.UsesRuntimeHandoff);
            Assert.True(row.RuntimeOwnedLegalityIsFinal);
            Assert.True(row.EmitsDirectVectorTransferOpcode);
            Assert.False(row.UsesFallbackPath);
            Assert.False(row.UsesAliasPromotion);
            Assert.False(string.IsNullOrWhiteSpace(row.RequiredTypedOperandContract));
            CompilerVectorTransferPositiveEmissionAbiContract.RequireRuntimeHandoffAuthority(row.Mnemonic);

            OpcodeInfo? opcodeInfo = OpcodeRegistry.GetInfo((uint)row.Opcode);
            Assert.NotNull(opcodeInfo);
            Assert.Equal(InstructionClass.Memory, (InstructionClass)(byte)opcodeInfo!.Value.InstructionClass);

            InstructionSupportStatus status = InstructionSupportStatusCatalog.GetStatus(row.Mnemonic);
            Assert.Equal(IsaInstructionStatus.OptionalEnabled, status.Status);
            Assert.Equal(RuntimeInstructionEvidence.ConformanceTested, status.RuntimeEvidence);
            Assert.True(status.HasRuntimeOpcodeMetadata);
            Assert.True(status.HasCanonicalDecoderAcceptance);
            Assert.True(status.HasRegistryFactory);
            Assert.True(status.HasExecutionSemantics);
            Assert.True(status.IsExecutableClaim);

            Assert.Contains(inventoryRow.FacadeHelperFragment, publicCompilerMethods);
            Assert.Contains(row.HelperName, publicCompilerMethods);
            foreach (string fragment in inventoryRow.CompilerSourceFragments)
            {
                Assert.Contains(fragment, compilerSource, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void VectorTransferCompilerOwnedSources_DoNotReferenceFallbackOrAliasOpcodeFamilies()
    {
        string source = ReadCompilerOwnedVectorTransferSource();
        string[] forbiddenFragments =
        [
            "InstructionsEnum.VADD",
            "InstructionsEnum.VSUB",
            "InstructionsEnum.VMUL",
            "InstructionsEnum.VDOT",
            "InstructionsEnum.VDOT_WIDE",
            "InstructionsEnum.VTRANSPOSE",
            "InstructionsEnum.VLDSEG",
            "InstructionsEnum.VSTSEG",
            "InstructionsEnum.FMAC",
            "InstructionsEnum.MTILE",
            "InstructionsEnum.DmaStreamCompute",
            "InstructionsEnum.ACCEL_SUBMIT",
            "CompileVector",
            "CompileVdot",
            "CompileDmaStreamCompute",
            "CompileAccelerator",
            "EmitRawInstruction(",
            "ExpertBackendFacade",
            "ExternalBackend",
            ".ISA.Instructions.Vmx."
        ];

        foreach (string forbiddenFragment in forbiddenFragments)
        {
            Assert.DoesNotContain(forbiddenFragment, source, StringComparison.Ordinal);
        }

        Assert.Contains(nameof(HybridCpuThreadCompilerContext.CompileVload), source, StringComparison.Ordinal);
        Assert.Contains(nameof(HybridCpuThreadCompilerContext.CompileVstore), source, StringComparison.Ordinal);
        Assert.Contains(CompilerVectorTransferPositiveEmissionAbiContract.NoFallbackDecision, source, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorTransferHelperNames_StayInsideIntendedCompilerOwnedFiles()
    {
        string[] allowedFileNames =
        [
            "CompilerVectorTransferPositiveEmissionAbiContract.cs",
            "ThreadCompilerContext.VectorTransfer.cs",
            "ThreadCompilerContext.FacadeAudit.cs",
            "IPlatformAsmFacade.cs",
            "PlatformAsmFacade.cs"
        ];
        string[] helperNames =
        [
            nameof(HybridCpuThreadCompilerContext.CompileVload),
            nameof(HybridCpuThreadCompilerContext.CompileVstore),
            nameof(IPlatformAsmFacade.VLoad),
            nameof(IPlatformAsmFacade.VStore)
        ];

        List<string> unexpectedSites = new List<string>();
        foreach (string path in CompilerSourceScanner.EnumerateCompilerSourceFiles())
        {
            if (allowedFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(path);
            foreach (string helperName in helperNames)
            {
                if (text.Contains(helperName, StringComparison.Ordinal))
                {
                    unexpectedSites.Add($"{Path.GetFileName(path)}:{helperName}");
                }
            }
        }

        Assert.Empty(unexpectedSites);
    }

    private static void EmitGoldenVector(
        PlatformAsmFacade facade,
        VectorTransferGoldenVector vector)
    {
        CompilerVectorTransferMemoryAddressAbi destination = CompilerVectorTransferMemoryAddressAbi.Create(vector.DestinationBase);
        CompilerVectorTransferMemoryAddressAbi source = CompilerVectorTransferMemoryAddressAbi.Create(vector.SourceBase);
        CompilerVectorTransferShapeAbi shape = CompilerVectorTransferShapeAbi.Create(
            NativeTransportRuntimeAdapter.ToCore(vector.ElementType),
            vector.ElementCount,
            vector.StrideBytes,
            vector.PredicateMask);

        switch (vector.Kind)
        {
            case CompilerVectorTransferPositiveEmissionKind.Vload:
                facade.VLoad(destination, source, shape);
                break;
            case CompilerVectorTransferPositiveEmissionKind.Vstore:
                facade.VStore(source, destination, shape);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vector), vector.Kind, "Unsupported vector transfer golden vector.");
        }
    }

    private static void AssertVectorCarrier(
        VectorTransferGoldenVector expected,
        VLIW_Instruction actual)
    {
        Assert.Equal(expected.Opcode, (InstructionsEnum)actual.OpCode);
        Assert.Equal(expected.ElementType, actual.DataTypeValue);
        Assert.Equal(expected.PredicateMask, actual.PredicateMask);
        Assert.Equal(expected.ElementCount, actual.StreamLength);
        Assert.Equal(expected.StrideBytes, actual.Stride);
        Assert.Equal(0, actual.Immediate);
        Assert.Equal(0, actual.RowStride);
        Assert.False(actual.Indexed);
        Assert.False(actual.Is2D);
        Assert.False(actual.Reduction);
        Assert.False(actual.TailAgnostic);
        Assert.False(actual.MaskAgnostic);

        switch (expected.Kind)
        {
            case CompilerVectorTransferPositiveEmissionKind.Vload:
                Assert.Equal(expected.DestinationBase, actual.DestSrc1Pointer);
                Assert.Equal(expected.SourceBase, actual.Src2Pointer);
                break;
            case CompilerVectorTransferPositiveEmissionKind.Vstore:
                Assert.Equal(expected.SourceBase, actual.DestSrc1Pointer);
                Assert.Equal(expected.DestinationBase, actual.Src2Pointer);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected), expected.Kind, "Unsupported vector transfer golden vector.");
        }
    }

    private static void AssertVectorIrContract(
        VectorTransferGoldenVector expected,
        IrInstruction instruction)
    {
        Assert.Contains(
            instruction.Operands,
            operand => operand.Kind == IrOperandKind.MemoryAddress &&
                       operand.Name == "vectorDestinationBase" &&
                       operand.Value == expected.DestinationBase);
        Assert.Contains(
            instruction.Operands,
            operand => operand.Kind == IrOperandKind.MemoryAddress &&
                       operand.Name == "vectorSourceBase" &&
                       operand.Value == expected.SourceBase);
        Assert.Contains(
            instruction.Operands,
            operand => operand.Kind == IrOperandKind.StreamLength &&
                       operand.Name == "vectorElementCount" &&
                       operand.Value == expected.ElementCount);
        Assert.Contains(
            instruction.Operands,
            operand => operand.Kind == IrOperandKind.Stride &&
                       operand.Name == "vectorStrideBytes" &&
                       operand.Value == expected.StrideBytes);
        Assert.Contains(
            instruction.Operands,
            operand => operand.Kind == IrOperandKind.PredicateMask &&
                       operand.Name == "vectorPredicateMask" &&
                       operand.Value == expected.PredicateMask);
        Assert.Contains(
            instruction.Annotation.Defs,
            operand => operand.Kind == IrOperandKind.MemoryAddress &&
                       operand.Name == "vectorDestinationBase" &&
                       operand.Value == expected.DestinationBase);
        Assert.Contains(
            instruction.Annotation.Uses,
            operand => operand.Kind == IrOperandKind.MemoryAddress &&
                       operand.Name == "vectorSourceBase" &&
                       operand.Value == expected.SourceBase);
        AssertMemoryRegion(
            instruction.Annotation.MemoryReadRegion,
            expected.SourceBase,
            checked((uint)expected.EffectiveLength),
            isWrite: false);
        AssertMemoryRegion(
            instruction.Annotation.MemoryWriteRegion,
            expected.DestinationBase,
            checked((uint)expected.EffectiveLength),
            isWrite: true);
    }

    private static void AssertMemoryRegion(
        IrMemoryRegion? region,
        ulong expectedAddress,
        uint expectedLength,
        bool isWrite)
    {
        Assert.NotNull(region);
        Assert.Equal(expectedAddress, region!.Address);
        Assert.Equal(expectedLength, region.Length);
        Assert.Equal(isWrite, region.IsWrite);
    }

    private static void AssertCarrier(
        VLIW_Instruction expected,
        HybridCpuInstructionWord actual)
    {
        AssertCarrier(expected, NativeTransportRuntimeAdapter.ToRuntime(in actual));
    }

    private static void AssertCarrier(
        VLIW_Instruction expected,
        VLIW_Instruction actual)
    {
        Assert.Equal(expected.Word0, actual.Word0);
        Assert.Equal(expected.Word1, actual.Word1);
        Assert.Equal(expected.Word2, actual.Word2);
        Assert.Equal(expected.Word3, actual.Word3);
    }

    private static string ReadCompilerOwnedVectorTransferSource()
    {
        string[] ownedFileNames =
        [
            "CompilerVectorTransferPositiveEmissionAbiContract.cs",
            "CompilerVectorTransferEmissionLowerer.cs",
            "ThreadCompilerContext.VectorTransfer.cs"
        ];

        return string.Join(
            Environment.NewLine,
            CompilerSourceScanner.EnumerateCompilerSourceFiles()
                .Where(path => ownedFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    public readonly record struct VectorTransferGoldenVector(
        CompilerVectorTransferPositiveEmissionKind Kind,
        InstructionsEnum Opcode,
        ulong DestinationBase,
        ulong SourceBase,
        DataTypeEnum ElementType,
        uint ElementCount,
        ushort StrideBytes,
        byte PredicateMask)
    {
        public string Mnemonic => CompilerVectorTransferPositiveEmissionAbiContract.GetMnemonic(Kind);

        public ulong EffectiveLength => checked((ulong)ElementCount * StrideBytes);
    }
}
