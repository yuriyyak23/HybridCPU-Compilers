using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCpuCanonicalCompiler = HybridCPU.Compiler.Core.Runtime.NativeTransportRuntimeAdapter;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class DmaStreamComputeCompilerContractTests
{
    [Fact]
    public void CompilerDescriptorEmission_UsesNativeLane6DmaStreamComputePath()
    {
        byte[] descriptorBytes = DmaStreamComputeTestDescriptorFactory.BuildDescriptor();
        DmaStreamComputeDescriptorReference reference =
            DmaStreamComputeTestDescriptorFactory.CreateReference(descriptorBytes);
        DmaStreamComputeOwnerGuardDecision guardDecision =
            DmaStreamComputeTestDescriptorFactory.CreateGuardDecision(descriptorBytes, reference);
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 2)
        {
            DomainTag = 0xD0A11
        };

        DmaStreamComputeDescriptor descriptor =
            context.CompileDmaStreamComputeDescriptor(
                descriptorBytes,
                guardDecision,
                reference);

        ReadOnlySpan<VLIW_Instruction> rawInstructions = context.GetCompiledInstructions();
        Assert.Equal(1, rawInstructions.Length);
        Assert.Equal(InstructionsEnum.DmaStreamCompute, (InstructionsEnum)rawInstructions[0].OpCode);
        Assert.Equal(0, rawInstructions[0].Reserved);
        Assert.Equal(0, rawInstructions[0].VirtualThreadId);
        Assert.Equal(0UL, rawInstructions[0].DestSrc1Pointer);
        Assert.Equal(0UL, rawInstructions[0].Src2Pointer);

        VliwBundleAnnotations annotations = context.GetBundleAnnotations();
        Assert.True(annotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata metadata));
        Assert.Equal(reference, metadata.DmaStreamComputeDescriptorReference);
        Assert.Same(descriptor, metadata.DmaStreamComputeDescriptor);

        HybridCpuCompiledProgram compiledProgram =
            HybridCpuCanonicalCompiler.CompileProgram(
                context.VirtualThreadId,
                rawInstructions,
                frontendMode: FrontendMode.NativeVLIW,
                bundleAnnotations: annotations,
                domainTag: context.DomainTag);

        IrInstruction irInstruction = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.DmaStreamCompute, (InstructionsEnum)(uint)irInstruction.Opcode);
        Assert.Equal(InstructionClass.Memory, (InstructionClass)(byte)irInstruction.InstructionClass);
        Assert.Equal(SerializationClass.MemoryOrdered, (SerializationClass)(byte)irInstruction.SerializationClass);
        Assert.Equal(IrResourceClass.DmaStream, irInstruction.Annotation.ResourceClass);
        Assert.NotEqual(IrResourceClass.LoadStore, irInstruction.Annotation.ResourceClass);
        Assert.Equal(SlotClass.DmaStreamClass, (SlotClass)(byte)irInstruction.Annotation.RequiredSlotClass);
        Assert.Equal(IrIssueSlotMask.DmaStream, irInstruction.Annotation.LegalSlots);
        Assert.Equal(IrIssueSlotMask.Slot6, irInstruction.Annotation.LegalSlots);
        Assert.Same(descriptor, irInstruction.DmaStreamComputeDescriptor);
        Assert.Empty(irInstruction.Operands);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(irInstruction.Index, out IrMaterializedBundleSlot? slot));
        Assert.NotNull(slot);
        Assert.Equal(6, slot!.SlotIndex);
        Assert.Equal(SlotClass.DmaStreamClass, (SlotClass)(byte)slot.Instruction!.Annotation.RequiredSlotClass);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.DmaStreamCount);
        Assert.Equal(0, facts.AluCount);
        Assert.Equal(0, facts.LsuCount);
        Assert.Equal(SlotClass.DmaStreamClass, (SlotClass)(byte)facts.Slot6Class);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(6);
        Assert.Equal(InstructionsEnum.DmaStreamCompute, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(0, lowered.Reserved);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.Equal(0UL, lowered.DestSrc1Pointer);
        Assert.Equal(0UL, lowered.Src2Pointer);
    }

    [Theory]
    [InlineData(DmaStreamComputeCompilerAdoptionMode.Compatibility)]
    [InlineData(DmaStreamComputeCompilerAdoptionMode.Strict)]
    [InlineData(DmaStreamComputeCompilerAdoptionMode.Future)]
    public void CompilerAdoptionModes_AreExplicitAndNeverLowerToFallback(
        DmaStreamComputeCompilerAdoptionMode mode)
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

        context.CompileDmaStreamCompute(descriptor, mode);

        ReadOnlySpan<VLIW_Instruction> rawInstructions = context.GetCompiledInstructions();
        Assert.Equal(1, rawInstructions.Length);
        Assert.Equal(InstructionsEnum.DmaStreamCompute, (InstructionsEnum)rawInstructions[0].OpCode);
        Assert.NotEqual(InstructionsEnum.ADD, (InstructionsEnum)rawInstructions[0].OpCode);
        Assert.False(InstructionRegistry.IsCustomAcceleratorOpcode(rawInstructions[0].OpCode));
    }

    [Fact]
    public void CompilerLane6StatusQueryQueueTokenFenceAndRuntimeFallbackSurfaces_RemainNoEmission()
    {
        string[] threadMethods = typeof(HybridCpuThreadCompilerContext)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name)
            .ToArray();

        Assert.Equal(
            [nameof(HybridCpuThreadCompilerContext.CompileDmaStreamCompute),
             nameof(HybridCpuThreadCompilerContext.CompileDmaStreamComputeDescriptor)],
            threadMethods
                .Where(static name => name.Contains("DmaStreamCompute", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());

        string[] forbiddenMethodFragments =
        [
            "Dsc",
            "DmaStreamStatus",
            "DmaStreamQuery",
            "DmaStreamQueue",
            "DmaStreamToken",
            "DmaStreamFence",
            "DmaStreamBackend",
            "DmaStreamFallback",
            "DmaStreamRuntime",
            "Production"
        ];
        foreach (string fragment in forbiddenMethodFragments)
        {
            Assert.DoesNotContain(
                threadMethods,
                methodName => methodName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        string compilerSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();
        string[] forbiddenSourceFragments =
        [
            "DSC_STATUS",
            "DSC_QUERY_CAPS",
            "DmaStreamComputeStatus",
            "DmaStreamComputeQueryCaps",
            "DmaStreamComputeTokenStore",
            "DmaStreamComputeRuntime",
            "DmaStreamComputeQueue",
            "DmaStreamComputeFence",
            "CompileDsc",
            "CompileDmaStreamStatus",
            "CompileDmaStreamQuery",
            "CompileDmaStreamQueue",
            "CompileDmaStreamToken",
            "CompileDmaStreamFence"
        ];
        foreach (string fragment in forbiddenSourceFragments)
        {
            Assert.DoesNotContain(fragment, compilerSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompilerDescriptorEmission_RejectsUnguardedDescriptorInsteadOfFallback()
    {
        byte[] descriptorBytes = DmaStreamComputeTestDescriptorFactory.BuildDescriptor();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileDmaStreamComputeDescriptor(
                descriptorBytes,
                default,
                DmaStreamComputeTestDescriptorFactory.CreateReference(descriptorBytes)));

        Assert.Contains("OwnerDomainFault", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.GetCompiledInstructions().Length);
    }

    [Fact]
    public void CompilerDescriptorEmission_RejectsInvalidDescriptorObjectInsteadOfFallback()
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileDmaStreamCompute(
                descriptor with
                {
                    Operation = (DmaStreamComputeOperationKind)0xFFFF
                }));

        Assert.Contains("unsupported descriptor semantics", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.GetCompiledInstructions().Length);
    }

    [Fact]
    public void CompilerDescriptorEmission_RejectsUnknownModeInsteadOfFallback()
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => context.CompileDmaStreamCompute(
                descriptor,
                (DmaStreamComputeCompilerAdoptionMode)0x7F));
        Assert.Equal(0, context.GetCompiledInstructions().Length);
    }

}
