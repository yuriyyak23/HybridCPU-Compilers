using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using SlotClass = HybridCPU.Compiler.Core.IR.IrSlotClass;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan3Phase02JointCycleCompositionTests
{
    [Fact]
    public void AdversarialGreedyFirstChoiceIsReplacedByCompleteFeasiblePair()
    {
        IrInstruction[] source = CreateIrInstructions(3);
        IrInstruction exclusive = WithFacts(
            source[0], IrIssueSlotMask.Slot0, SlotClass.AluClass,
            IrSerializationKind.ExclusiveCycle);
        IrInstruction left = WithFacts(
            source[1], IrIssueSlotMask.Slot0, SlotClass.AluClass);
        IrInstruction right = WithFacts(
            source[2], IrIssueSlotMask.Slot1, SlotClass.AluClass);
        IrCycleMembershipCandidate[] candidates =
        [
            Candidate(exclusive, criticalPath: 20),
            Candidate(left, criticalPath: 10),
            Candidate(right, criticalPath: 9)
        ];

        IrCycleSearchResult result = Search(candidates);

        Assert.False(result.Summary.ExistingPathFallbackRequired);
        Assert.Equal([left.Index, right.Index], result.ChosenMembers.Select(static item => item.InstructionIndex));
        Assert.Equal(2, result.PlacementWitness!.InstructionSlots.Distinct().Count());
        Assert.True(result.Summary.ResourcePruned + result.Summary.StructuralPruned >= 2);
    }

    [Fact]
    public void DeclaredAdversarialCompatibilityPathReducesThreeGreedyCyclesToTwo()
    {
        IrInstruction[] source = CreateIrInstructions(4);
        IrCycleMembershipCandidate[] ordered =
        [
            Candidate(WithFacts(source[0], IrIssueSlotMask.All, SlotClass.AluClass,
                structural: IrStructuralResource.ReductionUnit), 10),
            Candidate(WithFacts(source[3], IrIssueSlotMask.All, SlotClass.AluClass,
                structural: IrStructuralResource.CsrPort), 9),
            Candidate(WithFacts(source[1], IrIssueSlotMask.All, SlotClass.AluClass,
                structural: IrStructuralResource.ReductionUnit | IrStructuralResource.VectorPermuteCrossbar), 8),
            Candidate(WithFacts(source[2], IrIssueSlotMask.All, SlotClass.AluClass,
                structural: IrStructuralResource.VectorPermuteCrossbar | IrStructuralResource.CsrPort), 20)
        ];

        int greedyCycles = CountDeclaredGreedyCycles(ordered);
        IrCycleSearchResult first = Search(ordered);
        IrCycleMembershipCandidate[] remaining = ordered
            .Where(candidate => first.ChosenMembers.All(chosen => chosen.InstructionIndex != candidate.InstructionIndex))
            .ToArray();
        int jointCycles = 1 + CountDeclaredGreedyCycles(remaining);

        Assert.Equal(3, greedyCycles);
        Assert.Equal(2, jointCycles);
    }

    [Fact]
    public void SearchUsesExistingExactPlacementBackendAndProducesMatchingWitness()
    {
        IrInstruction[] source = CreateIrInstructions(3);
        IrCycleMembershipCandidate[] candidates =
        [
            Candidate(WithFacts(source[0], IrIssueSlotMask.Slot0 | IrIssueSlotMask.Slot1, SlotClass.AluClass), 3),
            Candidate(WithFacts(source[1], IrIssueSlotMask.Slot0, SlotClass.AluClass), 2),
            Candidate(WithFacts(source[2], IrIssueSlotMask.Slot1 | IrIssueSlotMask.Slot2, SlotClass.AluClass), 1)
        ];
        CountingPlacementBackend backend = new CountingPlacementBackend();
        HybridCpuCycleGroupSearch search = new HybridCpuCycleGroupSearch(placementBackend: backend);

        IrCycleSearchResult result = search.Search(Request(candidates));

        Assert.True(backend.CallCount > 0);
        Assert.Equal(3, result.ChosenMembers.Count);
        Assert.Equal(3, result.PlacementWitness!.InstructionSlots.Distinct().Count());
        Assert.Equal(HybridCpuMachineDescriptionV1.Default.Key, result.PlacementWitness.MachineDescriptionKey);
        Assert.Equal(HybridCpuCycleSearchOptionsV1.Default.Fingerprint, result.Summary.OptionsFingerprint);
    }

    [Fact]
    public void CounterBudgetSelectsOnlyFullyValidatedCandidateAndIsDeterministic()
    {
        IrInstruction[] source = CreateIrInstructions(4);
        IrCycleMembershipCandidate[] candidates = source.Select((instruction, index) =>
            Candidate(WithFacts(
                instruction,
                (IrIssueSlotMask)(1 << index),
                SlotClass.AluClass),
                4 - index)).ToArray();
        HybridCpuCycleSearchOptionsV1 options = new HybridCpuCycleSearchOptionsV1(4, 4, 1, 64);

        IrCycleSearchResult first = Search(candidates, options);

        Assert.True(first.Summary.StateCapHit);
        Assert.False(first.Summary.ExistingPathFallbackRequired);
        Assert.Equal(4, first.ChosenMembers.Count);
        for (int repetition = 0; repetition < 100; repetition++)
        {
            IrCycleSearchResult repeated = Search(candidates, options);
            Assert.Equal(first.PlacementWitness!.Fingerprint, repeated.PlacementWitness!.Fingerprint);
            Assert.Equal(first.Summary, repeated.Summary);
        }
    }

    [Fact]
    public void ExhaustedBudgetWithoutValidatedCandidateRequestsExactGreedyFallback()
    {
        IrInstruction[] source = CreateIrInstructions(2);
        IrCycleMembershipCandidate[] candidates =
        [
            Candidate(WithFacts(source[0], IrIssueSlotMask.Slot0, SlotClass.AluClass, IrSerializationKind.ExclusiveCycle), 2),
            Candidate(WithFacts(source[1], IrIssueSlotMask.Slot1, SlotClass.AluClass), 1)
        ];
        HybridCpuCycleSearchOptionsV1 options = new HybridCpuCycleSearchOptionsV1(2, 2, 1, 64);

        IrCycleSearchResult result = Search(candidates, options);

        Assert.True(result.Summary.StateCapHit);
        Assert.True(result.Summary.ExistingPathFallbackRequired);
        Assert.Equal("StateBudgetExhaustedWithoutValidatedCandidate", result.Summary.FallbackReason);
        Assert.Empty(result.ChosenMembers);
    }

    [Fact]
    public void PlacementBackendFailureCannotSelectPartialAlternative()
    {
        IrInstruction instruction = WithFacts(
            CreateIrInstructions(1)[0], IrIssueSlotMask.Slot0, SlotClass.AluClass);
        HybridCpuCycleGroupSearch search = new HybridCpuCycleGroupSearch(placementBackend: new ThrowingPlacementBackend());

        IrCycleSearchResult result = search.Search(Request([Candidate(instruction, 1)]));

        Assert.True(result.Summary.ExistingPathFallbackRequired);
        Assert.Equal("PlacementBackendFailure", result.Summary.FallbackReason);
        Assert.Null(result.PlacementWitness);
    }

    [Fact]
    public void UnknownResourceFactUsesExistingStructuralBackendsInsteadOfBecomingFree()
    {
        IrInstruction matrix = CreateIrInstructions(1)[0] with
        {
            Opcode = (HybridCpuOpcode)(uint)InstructionsEnum.MTILE_LOAD
        };
        CountingPlacementBackend backend = new CountingPlacementBackend();
        HybridCpuCycleGroupSearch search = new HybridCpuCycleGroupSearch(placementBackend: backend);

        IrCycleSearchResult result = search.Search(Request([Candidate(matrix, 1)]));

        Assert.False(result.Summary.ExistingPathFallbackRequired);
        Assert.Equal(1, result.ChosenMembers.Count);
        Assert.True(backend.CallCount > 0);
    }

    [Fact]
    public void DefaultDisabledSchedulerIsBitIdenticalToExplicitExistingPath()
    {
        IrProgram program = CreateProgram(8);
        HybridCpuLocalListScheduler baseline = new HybridCpuLocalListScheduler();
        HybridCpuLocalListScheduler explicitExisting = new HybridCpuLocalListScheduler { UseJointCycleComposition = false };

        IrProgramSchedule first = baseline.ScheduleProgram(program);
        IrProgramSchedule second = explicitExisting.ScheduleProgram(program);

        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(first),
            CompilerScheduleFingerprintV1.HashSchedule(second));
        Assert.All(first.BlockSchedules.SelectMany(static block => block.CycleGroups),
            static cycle => Assert.Null(cycle.PlacementWitness));
    }

    [Fact]
    public void BlockAboveTopKUsesExactExistingScheduleWithoutPartialSearchPolicy()
    {
        IrProgram program = CreateProgram(13);
        IrProgramSchedule baseline = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramSchedule bounded = new HybridCpuLocalListScheduler
        {
            UseJointCycleComposition = true
        }.ScheduleProgram(program);

        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(baseline),
            CompilerScheduleFingerprintV1.HashSchedule(bounded));
        Assert.All(bounded.BlockSchedules.SelectMany(static block => block.CycleGroups),
            static cycle => Assert.Null(cycle.CycleSearchSummary));
    }

    [Fact]
    public void EnabledSchedulerWitnessMaterializesWithoutMembershipRepair()
    {
        IrProgram program = CreateProgram(8);
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler { UseJointCycleComposition = true };
        IrProgramSchedule schedule = scheduler.ScheduleProgram(program);

        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);

        Assert.NotEmpty(bundles.BlockResults);
        IrScheduleCycleGroup[] witnessed = schedule.BlockSchedules
            .SelectMany(static block => block.CycleGroups)
            .Where(static cycle => cycle.PlacementWitness is not null)
            .ToArray();
        Assert.All(witnessed, cycle =>
        {
            Assert.Equal(
                cycle.Instructions.Select(static instruction => instruction.Index),
                cycle.PlacementWitness!.InstructionIndexes);
        });
    }

    [Fact]
    public void StaleWitnessFailsBeforeBundleMaterialization()
    {
        IrProgram program = CreateProgram(4);
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler { UseJointCycleComposition = true };
        IrBasicBlockSchedule original = scheduler.ScheduleProgram(program).BlockSchedules[0];
        IrScheduleCycleGroup first = original.CycleGroups[0];
        IrBundlePlacementSearchResult placement = HybridCpuSlotModel.SearchStructuralAssignments(
            first.Instructions.Select(static instruction => instruction.Annotation.StructurallyAllowedSlots).ToArray());
        IrCyclePlacementWitness stale = new IrCyclePlacementWitness(
            HybridCpuMachineDescriptionV1.Default.Key + ":stale",
            HybridCpuCycleSearchOptionsV1.Default.Fingerprint,
            first.Instructions.Select(static instruction => instruction.Index).ToArray(),
            placement.BestInstructionSlots);
        IrScheduleCycleGroup replaced = first with { PlacementWitness = stale };
        IrScheduleCycleGroup[] cycles = original.CycleGroups.ToArray();
        cycles[0] = replaced;
        IrBasicBlockSchedule staleSchedule = new IrBasicBlockSchedule(
            original.Block,
            original.Dag,
            cycles,
            original.ScheduledInstructions);

        Assert.Throws<InvalidOperationException>(() =>
            new HybridCpuBundleFormer().BundleBlock(staleSchedule));
    }

    [Fact]
    public void WindowAndAllProductionBudgetsAreVersionedCounters()
    {
        HybridCpuCycleSearchOptionsV1 options = HybridCpuCycleSearchOptionsV1.Default;
        Assert.Equal(12, options.TopK);
        Assert.Equal(8, options.MaximumDepth);
        Assert.Equal(4096, options.MaximumEvaluatedStates);
        Assert.Equal(64, options.BeamWidth);
        Assert.Equal(64, options.Fingerprint.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Request([Candidate(CreateIrInstructions(1)[0], 1)], options with { MaximumEvaluatedStates = 0 }));
    }

    [Fact]
    public void SearchSourceContainsExistingPackerCallAndNoWallClockSelectorOrRuntimeAuthority()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "Compilers",
            "HybridCPU_Compiler",
            "Core",
            "IR",
            "Scheduling",
            "HybridCpuCycleGroupSearch.cs"));

        Assert.Contains("HybridCpuSlotModel.SearchStructuralAssignments", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SafetyVerifier", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionReady", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitAuthority", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RetireAuthority", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepresentativeCorpusHasExactNoCycleOrBundleRegressionWhenExplicitlyEnabled()
    {
        int improvedProfiles = 0;
        List<string> observed = new List<string>();
        foreach (CompilerRepresentativeInputDescriptorV1 descriptor in
                 CompilerPhase00RepresentativeInputCorpusV1.Descriptors)
        {
            CompilerRepresentativeInputInstanceV1 baselineInput =
                CompilerPhase00RepresentativeInputCorpusV1.Create(descriptor.Id);
            CompilerRepresentativeInputInstanceV1 jointInput =
                CompilerPhase00RepresentativeInputCorpusV1.Create(descriptor.Id);
            HybridCpuCompiledProgram baseline = HybridCpuCanonicalCompiler.CompileProgram(
                0,
                baselineInput.Instructions,
                bundleAnnotations: baselineInput.BundleAnnotations);
            HybridCpuCompiledProgram joint = HybridCpuCanonicalCompiler.CompileProgramWithJointCycleComposition(
                0,
                jointInput.Instructions,
                bundleAnnotations: jointInput.BundleAnnotations);

            int baselineCycles = baseline.ProgramSchedule.BlockSchedules.Sum(static block => block.ScheduleLength);
            int jointCycles = joint.ProgramSchedule.BlockSchedules.Sum(static block => block.ScheduleLength);
            int dependencyLowerBound = baseline.ProgramSchedule.BlockSchedules.Sum(static block =>
                block.Dag.Nodes.Count == 0
                    ? 0
                    : block.Dag.Nodes.Max(static node => node.CriticalPathLengthCycles));
            int baselineBundles = baseline.BundleLayout.BlockResults.Sum(static block => block.Bundles.Count);
            int jointBundles = joint.BundleLayout.BlockResults.Sum(static block => block.Bundles.Count);
            Assert.True(jointCycles <= baselineCycles,
                $"{descriptor.Id}: joint cycles {jointCycles} > baseline {baselineCycles}.");
            Assert.True(jointBundles <= baselineBundles,
                $"{descriptor.Id}: joint bundles {jointBundles} > baseline {baselineBundles}.");
            if (jointCycles < baselineCycles) improvedProfiles++;
            observed.Add($"{descriptor.Id}:lb{dependencyLowerBound}:{baselineCycles}->{jointCycles}/{baselineBundles}->{jointBundles}");
        }

        Assert.Equal(0, improvedProfiles);
        Assert.Equal(6, observed.Count);
    }

    private static IrCycleSearchResult Search(
        IReadOnlyList<IrCycleMembershipCandidate> candidates,
        HybridCpuCycleSearchOptionsV1? options = null) =>
        new HybridCpuCycleGroupSearch().Search(Request(candidates, options));

    private static IrCycleSearchRequest Request(
        IReadOnlyList<IrCycleMembershipCandidate> candidates,
        HybridCpuCycleSearchOptionsV1? options = null) =>
        new(
            candidates,
            options ?? HybridCpuCycleSearchOptionsV1.Default,
            HybridCpuMachineDescriptionV1.Default.Key);

    private static IrCycleMembershipCandidate Candidate(
        IrInstruction instruction,
        int criticalPath,
        int successors = 0) =>
        new(instruction.Index, instruction, criticalPath, successors);

    private static IrProgram CreateProgram(int count) =>
        new HybridCpuIrBuilder().BuildProgram(
            0,
            NativeTransportRuntimeAdapter.ToCore(CreateEncodedInstructions(count)));

    private static IrInstruction[] CreateIrInstructions(int count) =>
        CreateProgram(count).Instructions.ToArray();

    private static VLIW_Instruction[] CreateEncodedInstructions(int count)
    {
        VLIW_Instruction[] instructions = new VLIW_Instruction[count];
        for (int index = 0; index < count; index++)
        {
            instructions[index] = new VLIW_Instruction
            {
                OpCode = (uint)InstructionsEnum.ADDI,
                DataTypeValue = DataTypeEnum.INT32,
                PredicateMask = 0xFF,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                    checked((byte)((index % 30) + 1)),
                    31,
                    VLIW_Instruction.NoArchReg),
                Src2Pointer = (ulong)(index + 1),
                VirtualThreadId = 0
            };
        }

        return instructions;
    }

    private static IrInstruction WithFacts(
        IrInstruction instruction,
        IrIssueSlotMask slots,
        SlotClass slotClass,
        IrSerializationKind serialization = IrSerializationKind.None,
        IrStructuralResource structural = IrStructuralResource.None) =>
        instruction with
        {
            Annotation = instruction.Annotation with
            {
                LegalSlots = slots,
                RequiredSlotClass = slotClass,
                BindingKind = BitCount(slots) == 1
                    ? IrSlotBindingKind.HardPinned
                    : IrSlotBindingKind.ClassFlexible,
                Serialization = serialization,
                StructuralResources = structural,
                MemoryReadRegion = null,
                MemoryWriteRegion = null,
                ControlFlowKind = IrControlFlowKind.None,
                IsBarrierLike = false
            }
        };

    private static int CountDeclaredGreedyCycles(IReadOnlyList<IrCycleMembershipCandidate> ordered)
    {
        List<IrCycleMembershipCandidate> remaining = ordered.ToList();
        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker();
        int cycles = 0;
        while (remaining.Count > 0)
        {
            List<IrInstruction> group = new List<IrInstruction>();
            for (int index = 0; index < remaining.Count;)
            {
                IrCycleMembershipCandidate candidate = remaining[index];
                IrInstruction[] proposed = group.Append(candidate.Instruction).ToArray();
                if (checker.AnalyzeCandidateBundle(proposed).IsStructurallyAdmissible)
                {
                    group.Add(candidate.Instruction);
                    remaining.RemoveAt(index);
                }
                else
                {
                    index++;
                }
            }

            Assert.NotEmpty(group);
            cycles++;
        }

        return cycles;
    }

    private static int BitCount(IrIssueSlotMask mask)
    {
        int count = 0;
        uint value = (uint)mask;
        while (value != 0)
        {
            count += (int)(value & 1u);
            value >>= 1;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Compilers", "HybridCPU_Compiler")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class CountingPlacementBackend : IHybridCpuCyclePlacementBackend
    {
        public int CallCount { get; private set; }

        public IrBundlePlacementSearchResult SearchStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots)
        {
            CallCount++;
            return HybridCpuSlotModel.SearchStructuralAssignments(structurallyAllowedSlots);
        }
    }

    private sealed class ThrowingPlacementBackend : IHybridCpuCyclePlacementBackend
    {
        public IrBundlePlacementSearchResult SearchStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots) =>
            throw new InvalidOperationException("Synthetic placement failure.");
    }
}
