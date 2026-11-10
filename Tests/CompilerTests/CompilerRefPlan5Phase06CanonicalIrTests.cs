using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase06CanonicalIrTests
{
    [Fact]
    public void NativeFrontend_PassesValidatedCanonicalSeam_WithBbRegionAndCarrierParity()
    {
        HybridCpuInstructionWord[] words = ProgramWords();
        HybridCpuCompiledProgram compiled = HybridCpuCanonicalCompiler.CompileProgram(0, words);

        HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
        IrProgram program = builder.BuildProgram(0, words);
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        IrProgramSchedule directSchedule = scheduler.ScheduleProgram(program);
        HybridCpuBundleFormer bundler = new HybridCpuBundleFormer();
        IrProgramBundlingResult directBundles = bundler.BundleProgram(directSchedule);
        IReadOnlyList<HybridCpuInstructionBundle> directLowered = new HybridCpuBundleLowerer().LowerProgram(directBundles);
        byte[] directImage = new HybridCpuBundleSerializer().SerializeProgram(directLowered);

        Assert.Equal(directImage, compiled.ProgramImage);
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(directSchedule),
            CompilerScheduleFingerprintV1.HashSchedule(compiled.ProgramSchedule));
        Assert.Equal(program.BasicBlocks.Count, compiled.ProgramSchedule.SchedulingRegions.Count);
        Assert.All(compiled.ProgramSchedule.SchedulingRegions, region =>
        {
            Assert.Single(region.Blocks);
            Assert.False(region.ExpansionEnabled);
            Assert.Equal("hybridcpu.scheduling-region/v1", region.SchemaId);
        });
    }

    [Fact]
    public void NativeCanonicalInstructions_HaveExplicitStableTypesSemanticsEffectsAndCapabilities()
    {
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, ProgramWords());

        Assert.Equal("hybridcpu.canonical-ir/v1", program.Contract.SchemaId);
        Assert.Contains("isa.hybridcpu-w8-native-v1", program.Contract.RequiredCapabilities);
        Assert.All(program.Instructions, instruction =>
        {
            Assert.NotEmpty(instruction.StableIdentity);
            Assert.NotEqual(IrCanonicalValueKind.Unknown, instruction.CanonicalType.Kind);
            Assert.True(instruction.Semantics.IsFullySpecified);
            Assert.NotEmpty(instruction.OriginChain.Links);
            Assert.NotEqual(IrArchitecturalEffectKind.Unknown, instruction.SideEffects.ArchitecturalEffects);
        });
        Assert.Equal(IrFrontendAdapterStatus.Success, CanonicalIrFrontendBoundaryV1.Validate(program).Status);
    }

    [Fact]
    public void SourceOriginChain_SurvivesBuilderAndRecordTransformRoundTrip()
    {
        IrSourceSpan span = new IrSourceSpan("unit.asm", 3, 2, 3, 18, 24, 16);
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(
            0,
            ProgramWords(),
            instructionSourceBindings: [new(0, span)]);
        IrInstruction transformed = program.Instructions[0] with { Immediate = 99 };

        Assert.Equal(span, transformed.SourceSpan);
        IrSourceOriginLinkV1 origin = Assert.Single(transformed.OriginChain.Links);
        Assert.Equal(span, origin.Span);
        Assert.Equal(IrFrontendEvidenceTrust.ValidatedStructural, origin.Trust);
    }

    [Fact]
    public void UnknownFrontendSemanticsAndUnsupportedAddressSpace_RejectBeforeScheduling()
    {
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, ProgramWords());
        IrInstruction unknownSemantics = program.Instructions[0] with { Semantics = IrInstructionSemanticsV1.Unknown };
        IrFrontendAdapterResultV1 semanticsResult = CanonicalIrFrontendBoundaryV1.Validate(
            program with { Instructions = [unknownSemantics, .. program.Instructions.Skip(1)] });
        Assert.Equal(IrFrontendAdapterStatus.UnknownSemantics, semanticsResult.Status);

        IrInstruction unknownAddressSpace = program.Instructions[0] with
        {
            SideEffects = new(
                IrCanonicalMemoryEffectV1.Unknown,
                IrArchitecturalEffectKind.TrapOrFault)
        };
        IrFrontendAdapterResultV1 addressResult = CanonicalIrFrontendBoundaryV1.Validate(
            program with { Instructions = [unknownAddressSpace, .. program.Instructions.Skip(1)] });
        Assert.Equal(IrFrontendAdapterStatus.UnknownSemantics, addressResult.Status);
    }

    [Fact]
    public void UnknownVolatileAndAtomicMemoryEffects_ConservativelyParticipateInDependencies()
    {
        IrCanonicalMemoryEffectV1 read = new IrCanonicalMemoryEffectV1(
            IrMemoryEffectKind.Read,
            IrAddressSpaceIdentity.Generic,
            IrMemoryOrdering.NotAtomic,
            new IrMemoryRegion(0x1000, 4, false),
            null);
        IrCanonicalMemoryEffectV1 atomic = new IrCanonicalMemoryEffectV1(
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic,
            IrAddressSpaceIdentity.Generic,
            IrMemoryOrdering.SequentiallyConsistent,
            new IrMemoryRegion(0x2000, 4, false),
            new IrMemoryRegion(0x2000, 4, true));
        IrCanonicalMemoryEffectV1 volatileWrite = new IrCanonicalMemoryEffectV1(
            IrMemoryEffectKind.Write | IrMemoryEffectKind.Volatile,
            IrAddressSpaceIdentity.Generic,
            IrMemoryOrdering.Release,
            null,
            new IrMemoryRegion(0x3000, 4, true));

        Assert.True(IrCanonicalMemoryEffectV1.Unknown.ConservativelyAliases(read));
        Assert.True(atomic.ConservativelyAliases(read));
        Assert.True(volatileWrite.ConservativelyAliases(read));
        Assert.False(IrCanonicalMemoryEffectV1.None.ConservativelyAliases(read));
    }

    [Fact]
    public void ScheduleChangingMutation_InvalidatesEveryLegalityRelevantDerivedFact()
    {
        IrDerivedFactVersionsV1 current = IrDerivedFactVersionsV1.Initial.WithDependenciesCurrent() with
        {
            Liveness = new IrMutationStamp(0),
            Pressure = new IrMutationStamp(0),
            Mii = new IrMutationStamp(0),
            Placement = new IrMutationStamp(0)
        };
        IrDerivedFactVersionsV1 invalidated = current.InvalidateAfterScheduleChangingMutation();

        Assert.Equal(new IrMutationStamp(1), invalidated.ProgramMutation);
        Assert.Null(invalidated.Dependency);
        Assert.Null(invalidated.Liveness);
        Assert.Null(invalidated.Pressure);
        Assert.Null(invalidated.Mii);
        Assert.Null(invalidated.Placement);
    }

    [Fact]
    public void SchedulingRegion_UsesCurrentDependencyStampAndDeterministicIdentity()
    {
        HybridCpuCompiledProgram first = HybridCpuCanonicalCompiler.CompileProgram(0, ProgramWords());
        HybridCpuCompiledProgram second = HybridCpuCanonicalCompiler.CompileProgram(0, ProgramWords());

        Assert.Equal(
            first.ProgramSchedule.SchedulingRegions.Select(static region => region.RegionId),
            second.ProgramSchedule.SchedulingRegions.Select(static region => region.RegionId));
        Assert.All(first.ProgramSchedule.SchedulingRegions, region => Assert.Equal(
            first.ProgramSchedule.Program.Contract.DerivedFacts.ProgramMutation,
            region.DependenceView.MutationStamp));
        Assert.True(first.ProgramSchedule.Program.Contract.DerivedFacts.IsCurrent(
            first.ProgramSchedule.Program.Contract.DerivedFacts.Dependency));
    }

    [Fact]
    public void StableIdentity_IsIndependentOfFrontendEnumerationOrder()
    {
        IrInstruction instruction = new HybridCpuIrBuilder().BuildProgram(0, ProgramWords()).Instructions[0];
        IrSourceOriginChainV1 alpha = Origin("source-alpha");
        IrSourceOriginChainV1 beta = Origin("source-beta");

        Dictionary<string, string> forward = new[] { alpha, beta }.ToDictionary(
            chain => chain.Links[0].StableSourceIdentity,
            chain => IrCanonicalIdentityV1.Create(instruction, chain),
            StringComparer.Ordinal);
        Dictionary<string, string> reverse = new[] { beta, alpha }.ToDictionary(
            chain => chain.Links[0].StableSourceIdentity,
            chain => IrCanonicalIdentityV1.Create(instruction, chain),
            StringComparer.Ordinal);

        Assert.Equal(forward.OrderBy(static item => item.Key), reverse.OrderBy(static item => item.Key));
    }

    [Fact]
    public void CoreIrPublicSurface_DoesNotRetainLlvmCilOrRoslynHandles()
    {
        string[] forbidden = ["LLVMValueRef", "LLVMSharp", "MetadataToken", "Roslyn", "Microsoft.CodeAnalysis", "ISymbol"];
        IEnumerable<PropertyInfo> properties = typeof(IrProgram).Assembly.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("HybridCPU.Compiler.Core.IR", StringComparison.Ordinal) == true)
            .SelectMany(static type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.All(properties, property => Assert.DoesNotContain(
            forbidden,
            token => (property.PropertyType.FullName ?? property.PropertyType.Name)
                .Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void FrontendSpecificObjectHandle_IsRejectedFromLegacyOpaqueSidebandSeam()
    {
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, ProgramWords());
        IrInstruction poisoned = program.Instructions[0] with
        {
            DmaStreamComputeDescriptor = new LLVMValueRefFake()
        };
        IrFrontendAdapterResultV1 result = CanonicalIrFrontendBoundaryV1.Validate(
            program with { Instructions = [poisoned, .. program.Instructions.Skip(1)] });

        Assert.Equal(IrFrontendAdapterStatus.Unsupported, result.Status);
        Assert.Null(result.Program);
    }

    [Fact]
    public void UnknownOpcodeRejects_AndNativeLoadCarriesExplicitReadAndFaultEffects()
    {
        HybridCpuInstructionWord unknown = ProgramWords()[0];
        unknown.OpCode = ushort.MaxValue;
        Assert.Throws<ArgumentException>(() => new HybridCpuIrBuilder().BuildProgram(0, [unknown]));

        HybridCpuInstructionWord load = ProgramWords()[0];
        load.OpCode = (uint)HybridCpuOpcode.LW;
        IrInstruction instruction = Assert.Single(new HybridCpuIrBuilder().BuildProgram(0, [load]).Instructions);
        Assert.True(instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Read));
        Assert.True(instruction.Semantics.OperationMayFault);

        HybridCpuInstructionWord nope = default;
        IrInstruction canonicalNope = Assert.Single(new HybridCpuIrBuilder().BuildProgram(0, [nope]).Instructions);
        Assert.Equal(HybridCpuOpcode.Nope, canonicalNope.Opcode);
        Assert.Equal(IrMemoryEffectKind.None, canonicalNope.SideEffects.Memory.Kind);
        Assert.True(canonicalNope.Semantics.IsFullySpecified);
    }

    private static IrSourceOriginChainV1 Origin(string identity) => new(
        1,
        [new(identity, IrSourceOriginKind.Generated, "test-frontend", "1", null, IrFrontendEvidenceTrust.ValidatedStructural)]);

    private static HybridCpuInstructionWord[] ProgramWords() =>
    [
        new()
        {
            OpCode = (uint)HybridCpuOpcode.ADDI,
            DataTypeValue = HybridCpuDataType.INT32,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(1, 0, HybridCpuInstructionWord.NoArchReg),
            Src2Pointer = 7,
            VirtualThreadId = 0
        },
        new()
        {
            OpCode = (uint)HybridCpuOpcode.ADD,
            DataTypeValue = HybridCpuDataType.INT32,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(2, 1, 1),
            VirtualThreadId = 0
        }
    ];

    private sealed class LLVMValueRefFake
    {
    }
}
