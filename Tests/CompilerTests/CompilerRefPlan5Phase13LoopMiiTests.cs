using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase13LoopMiiTests
{
    [Fact]
    public void Contract_IsPinnedAnalysisOnlyAndListsEveryTypedComponent()
    {
        HybridCpuLoopMiiContractV1 contract = HybridCpuLoopMiiContractV1.Default;
        Assert.Equal("hybridcpu.loop-distance-mii/v1", HybridCpuLoopMiiContractV1.SchemaId);
        Assert.Equal("174bb33489d6e33cd0bc883c2e0b4069937db54ba7fbfb4c47b1a903d2b656e0", contract.ContractDigest);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, contract.TargetDigest);
        Assert.Equal(HybridCpuRegionSchedulingContractV1.Default.ContractDigest, contract.RegionContractDigest);
        Assert.Equal(HybridCpuMiiResourceModelV1.Default.ModelDigest, contract.DefaultResourceModelDigest);
        Assert.Equal(IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly, contract.OutputDisposition);
        Assert.Equal(10, Enum.GetValues<IrMiiComponentKindV1>().Length);
    }

    [Fact]
    public void NaturalLoop_CanonicalizesWithStablePhiDistanceDagAndVersionBindings()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 first = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        IrCanonicalLoopV1 second = Assert.Single(analyzer.Canonicalize(program, model).Loops);

        Assert.Equal(IrCanonicalLoopStatusV1.Qualified, first.Status);
        Assert.Equal(0, first.PreheaderBlockId);
        Assert.Equal(1, first.HeaderBlockId);
        Assert.Equal([1], first.LatchBlockIds);
        Assert.Empty(first.ExitBlockIds);
        Assert.Contains(first.PhiIncoming, static incoming =>
            incoming.ValueId == "native:vt0:x2" && incoming.SourceBlockId == 1 && incoming.TargetBlockId == 1);
        Assert.Contains(first.DistanceDependencies, static edge => edge.IterationDistance == 1);
        Assert.All(first.DistanceDependencies, static edge => Assert.True(edge.IterationDistance >= 0));
        Assert.Equal(first.LoopId, second.LoopId);
        Assert.Equal(first.VersionStamp, second.VersionStamp);
        Assert.Equal(first.InstructionDigest, second.InstructionDigest);
        Assert.Equal(first.DistanceDagDigest, second.DistanceDagDigest);
    }

    [Fact]
    public void FullyKnownModel_ProducesIndependentlyCheckableMiiFamilyAndLowerBound()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel(prfReads: 2, prfWrites: 1);
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        IrLoopMiiReportV1 report = analyzer.ComputeMii(program, loop, model);

        Assert.Equal(IrLoopMiiEligibilityV1.EligibleLowerBound, report.Eligibility);
        Assert.Equal(10, report.Components.Count);
        Assert.Equal(2, report.ProvenLowerBoundIi);
        Assert.Equal(2, Component(report, IrMiiComponentKindV1.PrfWritePortMii).Value);
        Assert.Equal(2, Component(report, IrMiiComponentKindV1.PrfWritePortMii).Numerator);
        Assert.Equal(1, Component(report, IrMiiComponentKindV1.PrfWritePortMii).Capacity);
        Assert.Equal(1, Component(report, IrMiiComponentKindV1.SlotMii).Value);
        Assert.Equal(IrMiiComponentStatusV1.NotApplicable,
            Component(report, IrMiiComponentKindV1.MemoryBankMii).Status);
        Assert.Empty(report.Diagnostics);
    }

    [Fact]
    public void DefaultUnknownCapacities_AreNeverEncodedAsZeroAndFailClosed()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program).Loops);
        IrLoopMiiReportV1 report = analyzer.ComputeMii(program, loop);

        Assert.Equal(IrLoopMiiEligibilityV1.IneligibleUnknown, report.Eligibility);
        Assert.Null(report.ProvenLowerBoundIi);
        IrMiiComponentResultV1 reads = Component(report, IrMiiComponentKindV1.PrfReadPortMii);
        IrMiiComponentResultV1 writes = Component(report, IrMiiComponentKindV1.PrfWritePortMii);
        Assert.Equal(IrMiiComponentStatusV1.Unknown, reads.Status);
        Assert.Null(reads.Value);
        Assert.Null(writes.Value);
        Assert.Contains(report.Diagnostics, static diagnostic => diagnostic.Code == "HCMI1001");
    }

    [Fact]
    public void MultiDistanceRecurrence_UsesCycleLatencyOverDistanceAndRejectsBadDistances()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel(prfReads: 64, prfWrites: 64);
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        IrLoopDistanceDependencyV1[] recurrence =
        [
            new("edge-a", 1, 2, 0, 3, IrInstructionDependencyKind.RegisterRaw,
                IrLoopProofPrecisionV1.Exact, "synthetic-forward"),
            new("edge-b", 2, 1, 2, 4, IrInstructionDependencyKind.RegisterRaw,
                IrLoopProofPrecisionV1.Exact, "synthetic-distance-two")
        ];
        IrCanonicalLoopV1 rebound = Rebind(loop, recurrence);
        IrMiiComponentResultV1 recurrenceMii = ComputeSyntheticRecMii(rebound, model);
        Assert.Equal(IrMiiComponentStatusV1.Proven, recurrenceMii.Status);
        Assert.Equal(4, recurrenceMii.Value);
        Assert.Equal(7, recurrenceMii.Numerator);
        Assert.Equal(2, recurrenceMii.Capacity);

        IrCanonicalLoopV1 negative = Rebind(loop, [recurrence[0] with { IterationDistance = -1 }]);
        Assert.Equal(IrMiiComponentStatusV1.Unsupported,
            ComputeSyntheticRecMii(negative, model).Status);
        IrCanonicalLoopV1 zeroSelf = Rebind(loop,
            [new("zero", 1, 1, 0, 1, IrInstructionDependencyKind.RegisterRaw,
                IrLoopProofPrecisionV1.Exact, "invalid-zero-self")]);
        Assert.Equal(IrMiiComponentStatusV1.Unsupported,
            ComputeSyntheticRecMii(zeroSelf, model).Status);
    }

    [Fact]
    public void ExactBankChannelLaneAndCertificateBottlenecks_AreSeparateComponents()
    {
        HybridCpuMiiResourceModelV1 model = KnownModel(
            prfReads: 64, prfWrites: 64, bankCount: 4, bankWidth: 8,
            channelCount: 2, channelWidth: 16, certificateCapacity: 2);
        IrProgram memoryProgram = TransformLoopInstructions(BuildLoopProgram(), instruction => instruction with
        {
            Annotation = instruction.Annotation with
            {
                MemoryReadRegion = new(0x1000, 4, false)
            },
            SideEffects = new(
                new(IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic,
                    new(0x1000, 4, false), null),
                IrArchitecturalEffectKind.None)
        });
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 memoryLoop = Assert.Single(analyzer.Canonicalize(memoryProgram, model).Loops);
        IrLoopMiiReportV1 memory = analyzer.ComputeMii(memoryProgram, memoryLoop, model);
        Assert.Equal(2, Component(memory, IrMiiComponentKindV1.MemoryBankMii).Value);
        Assert.Equal(2, Component(memory, IrMiiComponentKindV1.MemoryChannelMii).Value);

        IrProgram contourProgram = TransformLoopInstructions(BuildLoopProgram(loopInstructionCount: 4),
            (instruction, ordinal) => instruction with
            {
                Annotation = instruction.Annotation with
                {
                    RequiredSlotClass = ordinal < 3 ? IrSlotClass.DmaStreamClass : IrSlotClass.SystemSingleton
                }
            });
        IrCanonicalLoopV1 contourLoop = Assert.Single(analyzer.Canonicalize(contourProgram, model).Loops);
        IrLoopMiiReportV1 contours = analyzer.ComputeMii(contourProgram, contourLoop, model);
        Assert.Equal(3, Component(contours, IrMiiComponentKindV1.Lane6Mii).Value);
        Assert.Equal(1, Component(contours, IrMiiComponentKindV1.Lane7Mii).Value);
        Assert.Equal(2, Component(contours, IrMiiComponentKindV1.CertificateMii).Value);
        Assert.Contains("runtime", Component(contours, IrMiiComponentKindV1.CertificateMii).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlotAndRegisterGroupBounds_UseW8AndPhase07Facts()
    {
        HybridCpuMiiResourceModelV1 model = KnownModel(prfReads: 64, prfWrites: 64);
        IrProgram slotProgram = BuildLoopProgram(loopInstructionCount: 9);
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(slotProgram, model).Loops);
        IrLoopMiiReportV1 report = analyzer.ComputeMii(slotProgram, loop, model);

        Assert.Equal(3, Component(report, IrMiiComponentKindV1.SlotMii).Value);
        IrMiiComponentResultV1 groups = Component(report, IrMiiComponentKindV1.RegisterGroupMii);
        Assert.Contains(groups.Status, new[] { IrMiiComponentStatusV1.Proven, IrMiiComponentStatusV1.NotApplicable });
        Assert.DoesNotContain("occupancy", groups.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileCannotChangeProofAndMalformedProfileIsRejected()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        IrLoopMiiReportV1 absent = analyzer.ComputeMii(program, loop, model);
        IrLoopMiiReportV1 profiled = analyzer.ComputeMii(program, loop, model, new string('a', 64));

        Assert.Equal(Project(absent.Components), Project(profiled.Components));
        Assert.NotEqual(absent.ProofStamp.ProofDigest, profiled.ProofStamp.ProofDigest);
        Assert.Equal(new string('a', 64), profiled.ProofStamp.ProfileIdentity);
        IrLoopMiiReportV1 malformed = analyzer.ComputeMii(program, loop, model, "profile");
        Assert.Equal(IrLoopMiiEligibilityV1.IneligibleUnknown, malformed.Eligibility);
        Assert.Equal("HCMI0003", Assert.Single(malformed.Diagnostics).Code);
    }

    [Fact]
    public void StaleMutationDistanceDagAndResourceModelProofs_FailClosed()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        IrProgram mutated = program with
        {
            Contract = program.Contract with
            {
                DerivedFacts = program.Contract.DerivedFacts.InvalidateAfterScheduleChangingMutation()
            }
        };
        Assert.Equal(IrLoopMiiEligibilityV1.StaleProof,
            analyzer.ComputeMii(mutated, loop, model).Eligibility);
        Assert.Equal(IrLoopMiiEligibilityV1.StaleProof,
            analyzer.ComputeMii(program, loop with { DistanceDagDigest = new string('0', 64) }, model).Eligibility);
        IrCanonicalLoopV1 forgedDistanceDag = Rebind(loop,
            [new("forged", 1, 2, 7, 99, IrInstructionDependencyKind.RegisterRaw,
                IrLoopProofPrecisionV1.Exact, "caller-forged")]);
        Assert.Equal(IrLoopMiiEligibilityV1.StaleProof,
            analyzer.ComputeMii(program, forgedDistanceDag, model).Eligibility);
        HybridCpuMiiResourceModelV1 changedModel = KnownModel(prfReads: 8, prfWrites: 8);
        Assert.Equal(IrLoopMiiEligibilityV1.StaleProof,
            analyzer.ComputeMii(program, loop, changedModel).Eligibility);
        HybridCpuMiiResourceModelV1 forged = model with { ModelDigest = new string('f', 64) };
        Assert.Equal(IrLoopMiiEligibilityV1.IneligibleUnknown,
            analyzer.ComputeMii(program, loop, forged).Eligibility);
    }

    [Fact]
    public void UnsupportedSideEffectsSideExitsAndIrreducibleCycles_AreExplicit()
    {
        HybridCpuMiiResourceModelV1 model = KnownModel();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrProgram atomic = TransformLoopInstructions(BuildLoopProgram(), instruction => instruction with
        {
            SideEffects = new(
                new(IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
                    IrAddressSpaceIdentity.Generic, IrMemoryOrdering.SequentiallyConsistent,
                    new(0x1000, 4, false), new(0x1000, 4, true)),
                IrArchitecturalEffectKind.None)
        });
        Assert.Equal(IrCanonicalLoopStatusV1.Unsupported,
            Assert.Single(analyzer.Canonicalize(atomic, model).Loops).Status);

        IrProgram unknownEffect = TransformLoopInstructions(BuildLoopProgram(), instruction => instruction with
        {
            SideEffects = instruction.SideEffects with
            {
                ArchitecturalEffects = IrArchitecturalEffectKind.Unknown
            }
        });
        IrCanonicalLoopV1 unknownLoop = Assert.Single(analyzer.Canonicalize(unknownEffect, model).Loops);
        Assert.Equal(IrCanonicalLoopStatusV1.Unknown, unknownLoop.Status);
        Assert.Equal("unknown-loop-side-effect", unknownLoop.Reason);

        IrProgram sideExits = BuildMultipleExitLoopProgram();
        Assert.Equal("unsupported-multiple-side-exits",
            Assert.Single(analyzer.Canonicalize(sideExits, model).Loops).Reason);

        IrLoopCanonicalizationResultV1 irreducible = analyzer.Canonicalize(BuildIrreducibleProgram(), model);
        Assert.Equal(IrCanonicalLoopStatusV1.Unsupported, irreducible.Status);
        Assert.Equal("HCMI0008", Assert.Single(irreducible.Diagnostics).Code);
    }

    [Fact]
    public void AnalysisNeverChangesEmittedNativeBytes()
    {
        HybridCpuInstructionWord[] words = LoopWords(2);
        byte[] before = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        Assert.NotNull(analyzer.ComputeMii(program, loop, model).ProvenLowerBoundIi);
        byte[] after = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        Assert.Equal(before, after);
    }

    [Fact]
    public void DeterministicBudgets_FailClosedWithoutWallClockSelection()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 4);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        HybridCpuLoopMiiBudgetsV1 budgets = new HybridCpuLoopMiiBudgetsV1(1, 8, 1, 8);
        IrLoopCanonicalizationResultV1 first = analyzer.Canonicalize(program, model, budgets);
        IrLoopCanonicalizationResultV1 second = analyzer.Canonicalize(program, model, budgets);

        Assert.Equal(IrCanonicalLoopStatusV1.BudgetExhausted, first.Status);
        Assert.Equal("distance-edge-budget-exhausted", Assert.Single(first.Loops).Reason);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.ProgramShapeDigest, second.ProgramShapeDigest);
        Assert.Equal(
            first.Loops.Select(static loop => $"{loop.LoopId}:{loop.Status}:{loop.Reason}:{loop.DistanceDagDigest}"),
            second.Loops.Select(static loop => $"{loop.LoopId}:{loop.Status}:{loop.Reason}:{loop.DistanceDagDigest}"));
        Assert.Equal(new HybridCpuLoopMiiBudgetsV1(64, 128, 4096, 8192),
            HybridCpuLoopMiiBudgetsV1.Production);
    }

    private static IrMiiComponentResultV1 Component(IrLoopMiiReportV1 report, IrMiiComponentKindV1 kind) =>
        Assert.Single(report.Components, component => component.Component == kind);

    private static IrMiiComponentResultV1 ComputeSyntheticRecMii(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model)
    {
        MethodInfo method = typeof(HybridCpuLoopMiiAnalyzerV1).GetMethod(
            "ComputeRecMii", BindingFlags.NonPublic | BindingFlags.Static)!;
        return Assert.IsType<IrMiiComponentResultV1>(method.Invoke(null, [loop, model, 8192, 1_000_000]));
    }

    private static string Project(IReadOnlyList<IrMiiComponentResultV1> components) => string.Join('|',
        components.Select(static component => $"{component.Component}:{component.Status}:{component.Value}:{component.Numerator}:{component.Capacity}:{component.Precision}"));

    private static HybridCpuMiiResourceModelV1 KnownModel(
        int prfReads = 2,
        int prfWrites = 1,
        int bankCount = 4,
        int bankWidth = 8,
        int channelCount = 2,
        int channelWidth = 16,
        int certificateCapacity = 1) => HybridCpuMiiResourceModelV1.Create(
            new(prfReads, prfWrites, bankCount, bankWidth, channelCount, channelWidth),
            certificateCapacity);

    private static IrProgram BuildLoopProgram(int loopInstructionCount = 2)
    {
        HybridCpuInstructionWord[] words = LoopWords(loopInstructionCount);
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, words);
        IrInstruction[] instructions = original.Instructions.ToArray();
        IrBasicBlock preheader = new IrBasicBlock(
            0, 0, 0, instructions[0].EncodedAddress, instructions[0].EncodedAddress,
            false, [instructions[0]], [], [1], false, false, null, [], null, null);
        IrBasicBlock loop = new IrBasicBlock(
            1, 1, instructions.Length - 1, instructions[1].EncodedAddress, instructions[^1].EncodedAddress,
            false, instructions[1..], [0, 1], [1], false, false, null, [], null, null);
        IrValueAccessV1 phi = new("native:vt0:x2", 1, IrValueAccessKind.PhiEdgeUse, 1, 1);
        return original with
        {
            ControlFlowGraph = new(
                [preheader, loop],
                [new(0, 1, IrControlFlowEdgeKind.Fallthrough), new(1, 1, IrControlFlowEdgeKind.Branch)]),
            ValueFlow = original.ValueFlow with { Accesses = [.. original.ValueFlow.Accesses, phi] }
        };
    }

    private static HybridCpuInstructionWord[] LoopWords(int loopInstructionCount) =>
        Enumerable.Range(0, loopInstructionCount + 1).Select(index => new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.ADDI,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            VirtualThreadId = 0,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                checked((byte)(index % 15 + 1)),
                checked((byte)((index + 15) % 15 + 1)),
                HybridCpuInstructionWord.NoArchReg),
            Immediate = 1
        }).ToArray();

    private static IrProgram TransformLoopInstructions(
        IrProgram program,
        Func<IrInstruction, IrInstruction> transform) =>
        TransformLoopInstructions(program, (instruction, _) => transform(instruction));

    private static IrProgram TransformLoopInstructions(
        IrProgram program,
        Func<IrInstruction, int, IrInstruction> transform)
    {
        int ordinal = 0;
        Dictionary<int, IrInstruction> replacements = program.BasicBlocks.Single(static block => block.Id == 1).Instructions
            .ToDictionary(static instruction => instruction.Index, instruction => transform(instruction, ordinal++));
        IrInstruction[] instructions = program.Instructions
            .Select(instruction => replacements.GetValueOrDefault(instruction.Index, instruction)).ToArray();
        IrBasicBlock[] blocks = program.BasicBlocks.Select(block => block with
        {
            Instructions = block.Instructions.Select(instruction => replacements.GetValueOrDefault(instruction.Index, instruction)).ToArray()
        }).ToArray();
        return program with
        {
            Instructions = instructions,
            ControlFlowGraph = program.ControlFlowGraph with { Blocks = blocks }
        };
    }

    private static IrCanonicalLoopV1 Rebind(
        IrCanonicalLoopV1 loop,
        IReadOnlyList<IrLoopDistanceDependencyV1> edges)
    {
        string distanceDigest = Hash(string.Join('|',
            "hybridcpu.loop-distance-dag/v1",
            string.Join(';', edges.OrderBy(static edge => edge.ProducerInstructionIndex)
                .ThenBy(static edge => edge.ConsumerInstructionIndex)
                .ThenBy(static edge => edge.IterationDistance)
                .ThenBy(static edge => edge.Kind)
                .Select(static edge => string.Join(':',
                    edge.EdgeId, edge.ProducerInstructionIndex, edge.ConsumerInstructionIndex,
                    edge.IterationDistance, edge.LatencyCycles, edge.Kind, edge.Precision, edge.ProofReason)))));
        string version = Hash($"hybridcpu.loop-version/v1|{loop.ProgramVersionStamp}|{loop.InstructionDigest}|{distanceDigest}");
        string loopId = Hash(string.Join('|',
            HybridCpuLoopMiiContractV1.SchemaId,
            version,
            loop.HeaderBlockId,
            string.Join(',', loop.LatchBlockIds),
            string.Join(',', loop.BlockIds)));
        return loop with
        {
            LoopId = loopId,
            VersionStamp = version,
            DistanceDagDigest = distanceDigest,
            DistanceDependencies = edges
        };
    }

    private static IrProgram BuildMultipleExitLoopProgram()
    {
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, LoopWords(4));
        IrInstruction[] i = original.Instructions.ToArray();
        IrBasicBlock[] blocks =
        [
            new(0, 0, 0, i[0].EncodedAddress, i[0].EncodedAddress, false, [i[0]], [], [1], false, false, null, [], null, null),
            new(1, 1, 2, i[1].EncodedAddress, i[2].EncodedAddress, false, [i[1], i[2]], [0, 1], [1, 2, 3], false, false, null, [], null, null),
            new(2, 3, 3, i[3].EncodedAddress, i[3].EncodedAddress, false, [i[3]], [1], [], true, false, null, [], null, null),
            new(3, 4, 4, i[4].EncodedAddress, i[4].EncodedAddress, false, [i[4]], [1], [], true, false, null, [], null, null)
        ];
        return original with
        {
            ControlFlowGraph = new(blocks,
                [new(0, 1, IrControlFlowEdgeKind.Fallthrough), new(1, 1, IrControlFlowEdgeKind.Branch),
                    new(1, 2, IrControlFlowEdgeKind.Fallthrough), new(1, 3, IrControlFlowEdgeKind.Branch)])
        };
    }

    private static IrProgram BuildIrreducibleProgram()
    {
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, LoopWords(3));
        IrInstruction[] i = original.Instructions.ToArray();
        IrBasicBlock[] blocks =
        [
            new(0, 0, 0, i[0].EncodedAddress, i[0].EncodedAddress, false, [i[0]], [], [1, 2], false, false, null, [], null, null),
            new(1, 1, 1, i[1].EncodedAddress, i[1].EncodedAddress, false, [i[1]], [0, 2], [2], false, false, null, [], null, null),
            new(2, 2, 2, i[2].EncodedAddress, i[2].EncodedAddress, false, [i[2]], [0, 1], [1], false, false, null, [], null, null),
            new(3, 3, 3, i[3].EncodedAddress, i[3].EncodedAddress, false, [i[3]], [], [], true, false, null, [], null, null)
        ];
        return original with
        {
            ControlFlowGraph = new(blocks,
                [new(0, 1, IrControlFlowEdgeKind.Branch), new(0, 2, IrControlFlowEdgeKind.Fallthrough),
                    new(1, 2, IrControlFlowEdgeKind.Branch), new(2, 1, IrControlFlowEdgeKind.Branch)])
        };
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
