using System.Reflection;
using System.Text.Json;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase07ValueLivenessPressureTests
{
    [Fact]
    public void NativePrecoloredValues_UseExplicitRegisterKindAndTargetClasses()
    {
        IrProgram program = BuildNativeProgram((1, 0), (2, 1));

        Assert.All(
            program.Instructions.SelectMany(static instruction => instruction.Annotation.Defs.Concat(instruction.Annotation.Uses)),
            static operand => Assert.Equal(IrOperandKind.ArchitecturalRegister, operand.Kind));
        Assert.Contains(program.ValueFlow.Values, static value => value.StableId == "native:vt0:x1");
        Assert.All(program.ValueFlow.Values, value =>
        {
            Assert.Equal(HybridCpuArchitecturalRegisterClass.ScalarInteger64, value.Allocation.ArchitecturalClass);
            Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, value.Allocation.TargetContractDigest);
            Assert.NotNull(value.Allocation.FixedRegisterId);
        });
    }

    [Fact]
    public void ExistingRawWarWawDependencies_ArePreservedWithExplicitRegisterOperands()
    {
        IrProgram program = BuildNativeProgram((1, 0), (2, 1), (1, 2));
        IrProgramDependencyGraph graph = new HybridCpuProgramDependencyAnalyzer().AnalyzeProgram(program);
        IrInstructionDependency[] dependencies = graph.BlockGraphs.SelectMany(static block => block.Dependencies).ToArray();

        Assert.Contains(dependencies, static dependency =>
            dependency.Kind == IrInstructionDependencyKind.RegisterRaw &&
            dependency.ProducerInstructionIndex == 0 && dependency.ConsumerInstructionIndex == 1);
        Assert.Contains(dependencies, static dependency => dependency.Kind == IrInstructionDependencyKind.RegisterWar);
        Assert.Contains(dependencies, static dependency => dependency.Kind == IrInstructionDependencyKind.RegisterWaw);
    }

    [Fact]
    public void ReadModifyWrite_UsesValueBeforeSameInstructionDef()
    {
        IrProgram program = BuildNativeProgram((1, 1));
        IrValueAnalysisReportV1 report = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);

        Assert.Contains("native:vt0:x1", Assert.Single(report.Blocks).LiveIn);
    }

    [Fact]
    public void PhiEdgesAndLoopBackedge_ProduceEdgeCorrectLiveness()
    {
        IrProgram program = BuildLoopProgram();
        IrValueAnalysisReportV1 report = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);

        Assert.Equal(IrValueAnalysisStatus.Complete, report.Status);
        IrBlockLivenessV1 entry = Assert.Single(report.Blocks, static block => block.BlockId == 0);
        IrBlockLivenessV1 loop = Assert.Single(report.Blocks, static block => block.BlockId == 1);
        Assert.Contains("v.entry", entry.LiveOut);
        Assert.DoesNotContain("v.entry", loop.LiveIn);
        Assert.Contains("v.loop", loop.LiveOut);
        IrLoopLivenessV1 loopReport = Assert.Single(report.Loops);
        Assert.Equal(1, loopReport.HeaderBlockId);
        Assert.Equal([1], loopReport.LatchBlockIds);
    }

    [Fact]
    public void IntervalsAreHalfOpen_AndPressureReconcilesWithUsesAndDefs()
    {
        IrProgram program = BuildNativeProgram((1, 0), (2, 1), (3, 2));
        IrValueAnalysisReportV1 report = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);

        Assert.Equal(IrValueAnalysisStatus.Complete, report.Status);
        Assert.All(report.Intervals, static interval =>
            Assert.True(interval.EndInstructionIndexExclusive > interval.StartInstructionIndex));
        IrBlockPressureV1 pressure = Assert.Single(report.Pressure);
        Assert.True(Assert.Single(pressure.Classes).PeakLiveValues >= 2);
        Assert.True(pressure.PeakPredictedReads >= 1);
        Assert.Equal(1, pressure.PeakPredictedWrites);
        Assert.Contains(pressure.RegisterGroups, static group => group.RegisterGroup == 0);
    }

    [Fact]
    public void CollectionOrderPerturbation_LeavesAnalysisAndDigestStable()
    {
        IrProgram program = BuildLoopProgram();
        IrValueAnalysisReportV1 first = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);
        IrProgram perturbed = program with
        {
            ValueFlow = program.ValueFlow with
            {
                Values = program.ValueFlow.Values.Reverse().ToArray(),
                Accesses = program.ValueFlow.Accesses.Reverse().ToArray()
            }
        };
        IrValueAnalysisReportV1 second = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(perturbed);

        Assert.Equal(first.ValueFlowDigest, second.ValueFlowDigest);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void VectorValueWithoutVerifiedLowering_FailsClosed()
    {
        IrProgram program = BuildNativeProgram((1, 0));
        IrVirtualValueV1 vector = VirtualValue("vector", fixedRegister: null) with
        {
            ValueKind = new(IrCanonicalValueKind.Vector, 64, IsSigned: false, LaneCount: 2),
            VirtualClass = IrVirtualValueClass.LoweredVector
        };
        program = program with { ValueFlow = new("hybridcpu.value-flow/v1", 1, [vector], [new("vector", 0, IrValueAccessKind.Def)]) };

        IrValueAnalysisReportV1 rejected = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);
        IrValueAnalysisReportV1 accepted = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program with
        {
            ValueFlow = program.ValueFlow with
            {
                Values = [vector with { Allocation = vector.Allocation with { VerifiedVectorLoweringIdentity = "lower.vector-to-scalar-parts/v1" } }]
            }
        });

        Assert.Equal(IrValueAnalysisStatus.Unsupported, rejected.Status);
        Assert.Equal(IrValueAnalysisStatus.Complete, accepted.Status);
    }

    [Fact]
    public void UnknownTargetMappingAndAllocatorOwnedSpecialState_FailClosed()
    {
        IrProgram program = BuildNativeProgram((1, 0));
        IrVirtualValueV1 unknown = VirtualValue("unknown", fixedRegister: null) with
        {
            Allocation = VirtualValue("unknown", null).Allocation with { TargetContractDigest = "stale-target" }
        };
        IrVirtualValueV1 special = VirtualValue("special", fixedRegister: null) with
        {
            Allocation = VirtualValue("special", null).Allocation with
            {
                SpecialStateClass = HybridCpuSpecialStateClass.ControlStatus,
                IsAllocatable = true
            }
        };

        Assert.Equal(IrValueAnalysisStatus.Unknown, AnalyzeSingle(program, unknown).Status);
        Assert.Equal(IrValueAnalysisStatus.Unsupported, AnalyzeSingle(program, special).Status);
    }

    [Fact]
    public void DeterministicBudgets_ReturnUnknownReportWithoutChangingSchedule()
    {
        HybridCpuCompiledProgram compiled = HybridCpuCanonicalCompiler.CompileProgram(0, CreateWords((1, 0), (2, 1)));
        HybridCpuValueLivenessPressureAnalyzerV1 analyzer = new HybridCpuValueLivenessPressureAnalyzerV1(new(1, 1, 1));
        IrValueAnalysisReportV1 report = analyzer.Analyze(compiled.BundleLayout.Program);

        Assert.Equal(IrValueAnalysisStatus.BudgetExhausted, report.Status);
        Assert.Empty(report.Blocks);
        Assert.NotEmpty(compiled.ProgramImage);
    }

    [Fact]
    public void ScheduleReportIsObservational_AndNativeBytesRemainDeterministic()
    {
        HybridCpuInstructionWord[] words = CreateWords((1, 0), (2, 1), (3, 2));
        HybridCpuCompiledProgram first = HybridCpuCanonicalCompiler.CompileProgram(0, words);
        HybridCpuCompiledProgram second = HybridCpuCanonicalCompiler.CompileProgram(0, words);

        Assert.Equal(first.ProgramImage, second.ProgramImage);
        Assert.Equal(IrValueAnalysisStatus.Complete, first.ProgramSchedule.ValueAnalysis.Status);
        IrDerivedFactVersionsV1 facts = first.ProgramSchedule.Program.Contract.DerivedFacts;
        Assert.True(facts.IsCurrent(facts.Liveness));
        Assert.True(facts.IsCurrent(facts.Pressure));
        Assert.Equal(first.ProgramSchedule.ValueAnalysis.ValueFlowDigest, second.ProgramSchedule.ValueAnalysis.ValueFlowDigest);
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(first.ProgramSchedule),
            CompilerScheduleFingerprintV1.HashSchedule(second.ProgramSchedule));
    }

    [Fact]
    public void MixedPrecoloredAndVirtualValues_ReportUnassignedPressureSeparately()
    {
        IrProgram program = BuildNativeProgram((1, 0), (2, 1));
        IrVirtualValueV1 precolored = VirtualValue("fixed", 1);
        IrVirtualValueV1 virtualValue = VirtualValue("virtual", null);
        program = program with
        {
            ValueFlow = new(
                "hybridcpu.value-flow/v1",
                1,
                [precolored, virtualValue],
                [
                    new("fixed", 0, IrValueAccessKind.Def),
                    new("fixed", 1, IrValueAccessKind.Use),
                    new("virtual", 0, IrValueAccessKind.Def),
                    new("virtual", 1, IrValueAccessKind.Use)
                ])
        };

        IrBlockPressureV1 pressure = Assert.Single(new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program).Pressure);
        Assert.Equal(1, pressure.PeakUnassignedAllocatableValues);
        Assert.Contains(pressure.RegisterGroups, static group => group.IsPossibleRatherThanAssigned);
    }

    [Fact]
    public void VirtualThreadIdentityIsPartOfNativeValueIdentity()
    {
        IrProgram vt0 = BuildNativeProgram((1, 0));
        HybridCpuInstructionWord[] words = CreateWords((1, 0));
        words[0].VirtualThreadId = 1;
        IrProgram vt1 = new HybridCpuIrBuilder().BuildProgram(1, words);

        Assert.All(vt0.ValueFlow.Values, static value => Assert.Contains("vt0", value.StableId, StringComparison.Ordinal));
        Assert.All(vt1.ValueFlow.Values, static value => Assert.Contains("vt1", value.StableId, StringComparison.Ordinal));
        Assert.Empty(vt0.ValueFlow.Values.Select(static value => value.StableId)
            .Intersect(vt1.ValueFlow.Values.Select(static value => value.StableId), StringComparer.Ordinal));
    }

    [Fact]
    public void CallsAndReturnsExposeOnlyExplicitPrecoloredOperands()
    {
        HybridCpuInstructionWord call = new()
        {
            OpCode = (uint)HybridCpuOpcode.JAL,
            DataTypeValue = HybridCpuDataType.INT64,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                1,
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg)
        };
        HybridCpuInstructionWord ret = new()
        {
            OpCode = (uint)HybridCpuOpcode.JALR,
            DataTypeValue = HybridCpuDataType.INT64,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                0,
                1,
                HybridCpuInstructionWord.NoArchReg)
        };

        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, [call, ret]);

        Assert.Contains(program.ValueFlow.Accesses, static access =>
            access.ValueId == "native:vt0:x1" && access.InstructionIndex == 0 && access.Kind == IrValueAccessKind.Def);
        Assert.Contains(program.ValueFlow.Accesses, static access =>
            access.ValueId == "native:vt0:x1" && access.InstructionIndex == 1 && access.Kind == IrValueAccessKind.Use);
        Assert.DoesNotContain(program.ValueFlow.Values, static value => !value.Allocation.FixedRegisterId.HasValue);
    }

    [Fact]
    public void LaneAndContourConstraintsRemainValueConstraints_NotRegisterFiles()
    {
        IrProgram program = BuildNativeProgram((1, 0));
        IrVirtualValueV1 constrained = VirtualValue("lane-value", null);
        constrained = constrained with
        {
            Allocation = constrained.Allocation with
            {
                LegalContours = ["lane6-stream", "lane7-control"],
                LegalLaneMask = 0b1100_0000
            }
        };

        IrValueAnalysisReportV1 report = AnalyzeSingle(program, constrained);

        Assert.Equal(IrValueAnalysisStatus.Complete, report.Status);
        Assert.Equal(HybridCpuArchitecturalRegisterClass.ScalarInteger64, constrained.Allocation.ArchitecturalClass);
        Assert.DoesNotContain("Vector", constrained.Allocation.ArchitecturalClass.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedSingleBlockLiveness_AgreesWithExhaustiveReferenceUpTo32Instructions()
    {
        for (int length = 1; length <= 32; length++)
        {
            (byte Destination, byte Source)[] registers = Enumerable.Range(0, length)
                .Select(static index => (
                    Destination: checked((byte)(index % 7 + 1)),
                    Source: checked((byte)((index * 3 + 2) % 7 + 1))))
                .ToArray();
            IrProgram program = BuildNativeProgram(registers);
            HashSet<string> defined = new HashSet<string>(StringComparer.Ordinal);
            SortedSet<string> expectedLiveIn = new SortedSet<string>(StringComparer.Ordinal);
            foreach (IrValueAccessV1 access in program.ValueFlow.Accesses
                         .OrderBy(static access => access.InstructionIndex)
                         .ThenBy(static access => access.Kind))
            {
                if (access.Kind == IrValueAccessKind.Use && !defined.Contains(access.ValueId)) expectedLiveIn.Add(access.ValueId);
                if (access.Kind == IrValueAccessKind.Def) defined.Add(access.ValueId);
            }

            IrValueAnalysisReportV1 report = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);
            Assert.Equal(expectedLiveIn, Assert.Single(report.Blocks).LiveIn);
            Assert.Empty(Assert.Single(report.Blocks).LiveOut);
        }
    }

    [Fact]
    public void ValueAnalysisDoesNotExposeRuntimeOwnershipOrPhysicalAllocation()
    {
        string[] forbidden = ["Prf", "Rename", "FreeList", "Scoreboard", "Commit", "Retire", "RuntimeAuthority"];
        Type[] types =
        [
            typeof(IrValueFlowGraphV1),
            typeof(IrValueAnalysisReportV1),
            typeof(HybridCpuValueLivenessPressureAnalyzerV1)
        ];

        Assert.All(types.SelectMany(static type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)), property =>
            Assert.DoesNotContain(forbidden, token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static IrValueAnalysisReportV1 AnalyzeSingle(IrProgram program, IrVirtualValueV1 value) =>
        new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program with
        {
            ValueFlow = new("hybridcpu.value-flow/v1", 1, [value], [new(value.StableId, 0, IrValueAccessKind.Def)])
        });

    private static IrVirtualValueV1 VirtualValue(string id, int? fixedRegister)
    {
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;
        int[] legalIds = fixedRegister.HasValue ? [fixedRegister.Value] : [1, 2, 3, 4, 5, 6, 7];
        int[] legalGroups = legalIds.Select(idValue => target.ArchitecturalRegisters[idValue].RegisterGroup)
            .Distinct().Order().ToArray();
        return new(
            id,
            new(IrCanonicalValueKind.Integer, 64, IsSigned: false),
            IrVirtualValueClass.ScalarInteger,
            new(
                64,
                1,
                RequiresPair: false,
                IsAllocatable: true,
                HybridCpuArchitecturalRegisterClass.ScalarInteger64,
                SpecialStateClass: null,
                fixedRegister,
                VirtualThreadId: 0,
                ["native-scalar"],
                LegalLaneMask: null,
                legalIds,
                legalGroups,
                target.ContractDigest,
                VerifiedVectorLoweringIdentity: null));
    }

    private static IrProgram BuildLoopProgram()
    {
        IrProgram original = BuildNativeProgram((1, 0), (2, 1), (3, 2));
        IrInstruction[] instructions = original.Instructions.ToArray();
        IrBasicBlock block0 = new IrBasicBlock(
            0, 0, 0, instructions[0].EncodedAddress, instructions[0].EncodedAddress + 32,
            false, [instructions[0]], [], [1], false, false, null, [], null, null);
        IrBasicBlock block1 = new IrBasicBlock(
            1, 1, 2, instructions[1].EncodedAddress, instructions[2].EncodedAddress + 32,
            false, [instructions[1], instructions[2]], [0, 1], [1], false, false, null, [], null, null);
        IrVirtualValueV1 entry = VirtualValue("v.entry", null);
        IrVirtualValueV1 loop = VirtualValue("v.loop", null);
        IrValueFlowGraphV1 flow = new IrValueFlowGraphV1(
            "hybridcpu.value-flow/v1",
            1,
            [entry, loop],
            [
                new("v.entry", 0, IrValueAccessKind.Def),
                new("v.entry", 1, IrValueAccessKind.PhiEdgeUse, 0, 1),
                new("v.loop", 1, IrValueAccessKind.Def),
                new("v.loop", 1, IrValueAccessKind.PhiEdgeUse, 1, 1),
                new("v.loop", 2, IrValueAccessKind.Use)
            ]);
        return original with
        {
            ControlFlowGraph = new(
                [block0, block1],
                [new(0, 1, IrControlFlowEdgeKind.Fallthrough), new(1, 1, IrControlFlowEdgeKind.Branch)]),
            ValueFlow = flow
        };
    }

    private static IrProgram BuildNativeProgram(params (byte Destination, byte Source)[] registers) =>
        new HybridCpuIrBuilder().BuildProgram(0, CreateWords(registers));

    private static HybridCpuInstructionWord[] CreateWords(params (byte Destination, byte Source)[] registers) =>
        registers.Select(static pair => new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.ADDI,
            DataTypeValue = HybridCpuDataType.INT64,
            VirtualThreadId = 0,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                pair.Destination,
                pair.Source,
                HybridCpuInstructionWord.NoArchReg),
            Immediate = 1
        }).ToArray();
}
