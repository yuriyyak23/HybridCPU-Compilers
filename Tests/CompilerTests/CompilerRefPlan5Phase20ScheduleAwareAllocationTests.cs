using System.Reflection;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Llvm;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase20ScheduleAwareAllocationTests
{
    [Fact]
    public void ContractIsVersionedTargetAbiTopologyBoundAndProductionDisabled()
    {
        HybridCpuRegisterAllocationContractV1 contract = HybridCpuRegisterAllocationContractV1.Default;

        Assert.Equal("hybridcpu.schedule-aware-register-allocation/v1", HybridCpuRegisterAllocationContractV1.SchemaId);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, contract.TargetDigest);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.ContractDigest, contract.AbiDigest);
        Assert.Equal(HybridCpuMachineTopologyV1.Default.ContractDigest, contract.MachineTopologyDigest);
        Assert.False(HybridCpuRegisterAllocationOptionsV1.Production.EnableAllocation);
        Assert.True(HybridCpuRegisterAllocationOptionsV1.Qualification.EnableAllocation);
        Assert.True(HybridCpuRegisterAllocationOptionsV1.Qualification.EnableSpills);
        Assert.Equal(64, contract.ContractDigest.Length);
        // The digest follows the versioned ABI/resource contracts, not an old checkout's hash.
        string digestInput = string.Join('|', HybridCpuRegisterAllocationContractV1.SchemaId,
            contract.TargetDigest, contract.AbiDigest, contract.MachineTopologyDigest,
            contract.DefaultResourceModelDigest, contract.ProductionOptionsDigest,
            contract.QualificationOptionsDigest, "physical-architectural-only", "deterministic-interval-coalescing",
            "spill-scratch=x28,x29,x30,x31", "spill-is-real-exclusive-frame-memory", "abi-ingress-live-from-entry/v1",
            "semantic-value-kinds-in-witness", "all-proofs-rebuilt", "exact-fallback", "wall-clock=false");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(digestInput))).ToLowerInvariant(), contract.ContractDigest);
        Assert.Equal("b4a9c4392096d51fd8c939b9b8864389b8bc0ee0f32ff9c171ad8a8b507bb97f",
            contract.ProductionOptionsDigest);
        Assert.Equal("6e38124939906e5949e225d764d2a610de5c91d6de9828166b94f4b1d2859b43",
            contract.QualificationOptionsDigest);
    }

    [Fact]
    public void ProductionDefaultPreservesExactSelectedPath()
    {
        Subject subject = BuildSubject([new(HybridCpuOpcode.ADDI, ["v"], []), new(HybridCpuOpcode.ADDI, [], ["v"])]);

        IrRegisterAllocationResultV1 result = new HybridCpuScheduleAwareRegisterAllocatorV1()
            .Allocate(subject.Schedule, subject.Bundles);

        Assert.Equal(IrRegisterAllocationStatusV1.SafeFallback, result.Status);
        Assert.True(result.UsedExactFallback);
        Assert.Same(subject.Schedule, result.FinalSchedule);
        Assert.Same(subject.Bundles, result.FinalBundles);
        Assert.Null(result.Witness);
    }

    [Fact]
    public void UnknownResourceFactsAndMalformedPlanIdentityFailClosed()
    {
        Subject subject = BuildSubject([new(HybridCpuOpcode.ADDI, ["v"], []), new(HybridCpuOpcode.ADDI, [], ["v"])]);
        HybridCpuScheduleAwareRegisterAllocatorV1 allocator = new HybridCpuScheduleAwareRegisterAllocatorV1();

        IrRegisterAllocationResultV1 unknown = allocator.Allocate(subject.Schedule, subject.Bundles,
            resourceModel: HybridCpuMiiResourceModelV1.Default,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        IrRegisterAllocationResultV1 malformed = allocator.Allocate(subject.Schedule, subject.Bundles,
            new(["not-a-sha"], [], [], []), KnownModel(), HybridCpuRegisterAllocationOptionsV1.Qualification);

        Assert.Equal(IrRegisterAllocationStatusV1.Unknown, unknown.Status);
        Assert.True(unknown.UsedExactFallback);
        Assert.Equal(IrRegisterAllocationStatusV1.Unknown, malformed.Status);
        Assert.True(malformed.UsedExactFallback);
    }

    [Fact]
    public void MismatchedBundleIdentityAndForgedOptionsDigestAreInvalidExactFallbacks()
    {
        Subject subject = BuildSubject([new(HybridCpuOpcode.ADDI, ["v"], []), new(HybridCpuOpcode.ADDI, [], ["v"])]);
        IrProgramSchedule otherSchedule = new HybridCpuLocalListScheduler().ScheduleProgram(subject.Schedule.Program);
        IrProgramBundlingResult otherBundles = new HybridCpuBundleFormer().BundleProgram(otherSchedule);
        HybridCpuRegisterAllocationOptionsV1 forged = HybridCpuRegisterAllocationOptionsV1.Qualification with { OptionsDigest = new('0', 64) };
        HybridCpuScheduleAwareRegisterAllocatorV1 allocator = new HybridCpuScheduleAwareRegisterAllocatorV1();

        IrRegisterAllocationResultV1 mismatch = allocator.Allocate(
            subject.Schedule, otherBundles, resourceModel: KnownModel(), options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        IrRegisterAllocationResultV1 badOptions = allocator.Allocate(
            subject.Schedule, subject.Bundles, resourceModel: KnownModel(), options: forged);

        Assert.Equal(IrRegisterAllocationStatusV1.InvalidInput, mismatch.Status);
        Assert.True(mismatch.UsedExactFallback);
        Assert.Equal(IrRegisterAllocationStatusV1.InvalidInput, badOptions.Status);
        Assert.True(badOptions.UsedExactFallback);
    }

    [Fact]
    public void QualificationLowersVirtualValuesToArchitecturalRegistersAndRebuildsProofs()
    {
        Subject subject = BuildSubject([new(HybridCpuOpcode.ADDI, ["v"], []), new(HybridCpuOpcode.ADDI, [], ["v"])]);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, result.Reason);
        Assert.False(result.UsedExactFallback);
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(result.Witness);
        IrRegisterAssignmentV1 assignment = Assert.Single(witness.Assignments);
        Assert.Equal("v", assignment.ValueId);
        Assert.DoesNotContain(result.FinalSchedule.Program.Instructions.SelectMany(static instruction =>
            instruction.Annotation.Defs.Concat(instruction.Annotation.Uses)),
            static operand => operand.Kind == IrOperandKind.VirtualValue);
        Assert.Empty(witness.Spills);
        Assert.Equal(0, witness.Frame.FrameSizeBytes);
        Assert.True(witness.Rebuild.DependenciesCurrent);
        Assert.True(witness.Rebuild.LivenessCurrent);
        Assert.True(witness.Rebuild.PressureCurrent);
        Assert.True(witness.Rebuild.ResourceFactsCurrent);
        Assert.True(witness.Rebuild.MiiRecomputed);
        Assert.True(witness.Rebuild.ExactW8PlacementRecomputed);
        Assert.All(result.FinalBundles.BlockResults.SelectMany(static block => block.Bundles),
            static bundle => Assert.Equal(8, bundle.Slots.Count));

        HybridCpuInstructionWord[] words = LowerWords(result.FinalBundles)
            .Where(static word => word.OpCode == (uint)HybridCpuOpcode.ADDI).ToArray();
        Assert.Equal(2, words.Length);
        Assert.True(HybridCpuInstructionWord.TryUnpackArchRegs(words[0].Word1, out byte firstRd, out _, out _));
        Assert.True(HybridCpuInstructionWord.TryUnpackArchRegs(words[1].Word1, out _, out byte secondRs1, out _));
        Assert.Equal(assignment.RegisterId, firstRd);
        Assert.Equal(assignment.RegisterId, secondRs1);
    }

    [Fact]
    public void PostAllocationScalarWriteBeforeBranchConsumer_RequiresWritebackVisibleLatency()
    {
        Subject subject = BuildSubject([
            new(HybridCpuOpcode.ADDI, ["induction"], []),
            new(HybridCpuOpcode.BGE, [], ["induction"])
        ]);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, result.Reason);
        IrInstruction producer = result.FinalSchedule.Program.Instructions.Single(static instruction =>
            instruction.Opcode == HybridCpuOpcode.ADDI);
        IrInstruction consumer = result.FinalSchedule.Program.Instructions.Single(static instruction =>
            instruction.Opcode == HybridCpuOpcode.BGE);
        IrInstructionDependency dependency = Assert.Single(
            result.FinalSchedule.DependencyGraph.BlockGraphs.SelectMany(static graph => graph.Dependencies),
            dependency => dependency.ProducerInstructionIndex == producer.Index &&
                dependency.ConsumerInstructionIndex == consumer.Index &&
                dependency.Kind == IrInstructionDependencyKind.RegisterRaw);
        Assert.Equal(4, dependency.MinimumLatencyCycles);
        IrBasicBlockSchedule block = Assert.Single(result.FinalSchedule.BlockSchedules);
        Assert.True(block.GetCycleForInstruction(consumer.Index) - block.GetCycleForInstruction(producer.Index) >= 4);
        IrBasicBlockBundlingResult bundled = Assert.Single(result.FinalBundles.BlockResults);
        Assert.Equal(block.ScheduleLength, bundled.Bundles.Count);
        Assert.All(bundled.Bundles.Where(bundle => bundle.Cycle > block.GetCycleForInstruction(producer.Index) &&
            bundle.Cycle < block.GetCycleForInstruction(consumer.Index)),
            static bundle => Assert.Equal(0, bundle.IssuedInstructionCount));
    }

    [Fact]
    public void PostAllocationLoadBeforeReturnConsumer_RequiresLoadUseLatency()
    {
        Subject subject = BuildSubject([
            new(HybridCpuOpcode.LD, ["returnAddress"], []),
            new(HybridCpuOpcode.JALR, [], ["returnAddress"])
        ]);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, result.Reason);
        IrInstruction producer = result.FinalSchedule.Program.Instructions.Single(static instruction =>
            instruction.Opcode == HybridCpuOpcode.LD);
        IrInstruction consumer = result.FinalSchedule.Program.Instructions.Single(static instruction =>
            instruction.Opcode == HybridCpuOpcode.JALR);
        IrInstructionDependency dependency = Assert.Single(
            result.FinalSchedule.DependencyGraph.BlockGraphs.SelectMany(static graph => graph.Dependencies),
            dependency => dependency.ProducerInstructionIndex == producer.Index &&
                dependency.ConsumerInstructionIndex == consumer.Index &&
                dependency.Kind == IrInstructionDependencyKind.RegisterRaw);
        Assert.Equal(8, dependency.MinimumLatencyCycles);
        IrBasicBlockSchedule block = Assert.Single(result.FinalSchedule.BlockSchedules);
        Assert.True(block.GetCycleForInstruction(consumer.Index) - block.GetCycleForInstruction(producer.Index) >= 8);
        IrBasicBlockBundlingResult bundled = Assert.Single(result.FinalBundles.BlockResults);
        Assert.Equal(block.ScheduleLength, bundled.Bundles.Count);
    }

    [Fact]
    public void PrecoloredValueRetainsItsFixedArchitecturalRegister()
    {
        Subject subject = BuildSubject(
            [new(HybridCpuOpcode.ADDI, ["fixed"], []), new(HybridCpuOpcode.ADDI, [], ["fixed"])],
            new Dictionary<string, ValueConstraint>(StringComparer.Ordinal) { ["fixed"] = new([7], 7) });

        IrRegisterAllocationResultV1 result = Allocate(subject);

        IrRegisterAssignmentV1 assignment = Assert.Single(Assert.IsType<IrRegisterAllocationWitnessV1>(result.Witness).Assignments);
        Assert.True(assignment.IsPrecolored);
        Assert.Equal(7, assignment.RegisterId);
    }

    [Fact]
    public void CallBoundariesUseAbiLocationsAndPreserveLiveCalleeStateWithARealFrame()
    {
        InstructionSpec[] instructions =
        [
            new(HybridCpuOpcode.ADDI, ["live"], []),
            new(HybridCpuOpcode.ADDI, ["arg"], []),
            new(HybridCpuOpcode.ADDI, ["callResult"], ["arg"], IsCall: true),
            new(HybridCpuOpcode.ADDI, [], ["live", "callResult"]),
            new(HybridCpuOpcode.JALR, [], [])
        ];
        Subject subject = BuildSubject(instructions);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, result.Reason);
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(result.Witness);
        Assert.Equal(10, witness.Assignments.Single(static assignment => assignment.ValueId == "arg").RegisterId);
        Assert.Equal(10, witness.Assignments.Single(static assignment => assignment.ValueId == "callResult").RegisterId);
        IrRegisterAssignmentV1 live = witness.Assignments.Single(static assignment => assignment.ValueId == "live");
        Assert.True(live.LiveAcrossCall);
        Assert.Contains(live.RegisterId, HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters);
        Assert.Contains(live.RegisterId, witness.Frame.SavedRegisters);
        Assert.Equal(0, witness.Frame.FrameSizeBytes % HybridCpuNativeAbiContractV2.StackAlignmentBytes);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.Prologue);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.CalleeSave);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.CalleeRestore);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.Epilogue);

        HybridCpuInstructionWord[] lowered = LowerWords(result.FinalBundles);
        HybridCpuInstructionWord prologue = Assert.Single(lowered, word =>
            word.OpCode == (uint)HybridCpuOpcode.ADDI && unchecked((short)word.Immediate) < 0);
        Assert.True(HybridCpuInstructionWord.TryUnpackArchRegs(prologue.Word1,
            out byte stackRd, out byte stackRs1, out _));
        Assert.Equal(HybridCpuNativeAbiContractV2.StackPointerRegister, stackRd);
        Assert.Equal(HybridCpuNativeAbiContractV2.StackPointerRegister, stackRs1);
        Assert.Contains(lowered, word => word.OpCode == (uint)HybridCpuOpcode.LD &&
            HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out _, out byte rs1, out _) &&
            rs1 == HybridCpuNativeAbiContractV2.StackPointerRegister);
        Assert.Contains(lowered, word => word.OpCode == (uint)HybridCpuOpcode.SD &&
            HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out _, out byte rs1, out byte rs2) &&
            rs1 == HybridCpuNativeAbiContractV2.StackPointerRegister && rs2 != HybridCpuInstructionWord.NoArchReg);
        Assert.Equal(HybridCpuOpcode.ADDI, result.FinalSchedule.Program.Instructions[0].Opcode);
        Assert.Contains(result.FinalBundles.BlockResults.SelectMany(static block => block.Bundles)
            .SelectMany(static bundle => bundle.Slots), static slot =>
                slot.Instruction?.Opcode == HybridCpuOpcode.JALR && slot.SlotIndex is 6 or 7);
        HybridCpuInstructionWord returnWord = Assert.Single(lowered,
            static word => word.OpCode == (uint)HybridCpuOpcode.JALR);
        Assert.True(HybridCpuInstructionWord.TryUnpackArchRegs(returnWord.Word1,
            out byte returnRd, out byte returnRs1, out byte returnRs2));
        Assert.Equal(0, returnRd);
        Assert.Equal(HybridCpuNativeAbiContractV2.ReturnAddressRegister, returnRs1);
        Assert.Equal(HybridCpuInstructionWord.NoArchReg, returnRs2);

        IrInstruction returnInstruction = result.FinalSchedule.Program.Instructions.Single(static instruction =>
            instruction.Opcode == HybridCpuOpcode.JALR);
        Assert.Contains(returnInstruction.Annotation.Uses, static operand =>
            operand.Kind == IrOperandKind.ArchitecturalRegister &&
            operand.Value == HybridCpuNativeAbiContractV2.ReturnAddressRegister);
        IrInstruction returnAddressRestore = result.FinalSchedule.Program.Instructions.Last(instruction =>
            instruction.Index < returnInstruction.Index &&
            instruction.Opcode == HybridCpuOpcode.LD &&
            instruction.Annotation.Defs.Any(static operand =>
                operand.Kind == IrOperandKind.ArchitecturalRegister &&
                operand.Value == HybridCpuNativeAbiContractV2.ReturnAddressRegister));
        IrInstructionDependency returnDependency = Assert.Single(
            result.FinalSchedule.DependencyGraph.BlockGraphs.SelectMany(static graph => graph.Dependencies),
            dependency => dependency.ProducerInstructionIndex == returnAddressRestore.Index &&
                dependency.ConsumerInstructionIndex == returnInstruction.Index &&
                dependency.Kind == IrInstructionDependencyKind.RegisterRaw);
        Assert.Equal(8, returnDependency.MinimumLatencyCycles);
        IrBasicBlockSchedule returnBlock = Assert.Single(result.FinalSchedule.BlockSchedules,
            block => block.BlockId == result.FinalSchedule.Program.ControlFlowGraph.Blocks.Single(candidate =>
                candidate.Instructions.Any(instruction => instruction.Index == returnInstruction.Index)).Id);
        Assert.True(returnBlock.GetCycleForInstruction(returnInstruction.Index) -
            returnBlock.GetCycleForInstruction(returnAddressRestore.Index) >= 8);
    }

    [Fact]
    public void HighPressureUsesDeterministicRealSpillsAndBalancedFrameLowering()
    {
        const int valueCount = 27;
        List<InstructionSpec> specs = new List<InstructionSpec>();
        for (int index = 0; index < valueCount; index++) specs.Add(new(HybridCpuOpcode.ADDI, [$"v{index:D2}"], []));
        specs.Add(new(HybridCpuOpcode.FENCE, [], []));
        for (int index = 0; index < valueCount; index++) specs.Add(new(HybridCpuOpcode.ADDI, [], [$"v{index:D2}"]));
        specs.Add(new(HybridCpuOpcode.JALR, [], []));
        Subject subject = BuildSubject(specs);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, result.Reason);
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(result.Witness);
        Assert.NotEmpty(witness.Spills);
        Assert.All(witness.Spills.SelectMany(static spill => spill.Accesses), static access =>
            Assert.InRange(access.ScratchRegisterId, 28, 31));
        Assert.True(witness.Frame.FrameSizeBytes > 0);
        Assert.Equal(0, witness.Frame.FrameSizeBytes % HybridCpuNativeAbiContractV2.StackAlignmentBytes);
        Assert.Contains(result.FinalSchedule.Program.Instructions, static instruction =>
            instruction.Opcode is HybridCpuOpcode.LD or HybridCpuOpcode.LW or HybridCpuOpcode.LH or HybridCpuOpcode.LB);
        Assert.Contains(result.FinalSchedule.Program.Instructions, static instruction =>
            instruction.Opcode is HybridCpuOpcode.SD or HybridCpuOpcode.SW or HybridCpuOpcode.SH or HybridCpuOpcode.SB);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.Prologue);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.Epilogue);
    }

    [Fact]
    public void ResourceBottleneckRejectsMutatedScheduleAndPreservesExactInput()
    {
        Subject subject = BuildSubject(
            [
                new(HybridCpuOpcode.ADDI, ["a"], []),
                new(HybridCpuOpcode.ADDI, ["b"], []),
                new(HybridCpuOpcode.ADDI, [], ["a", "b"]),
                new(HybridCpuOpcode.JALR, [], [])
            ]);
        HybridCpuMiiResourceModelV1 narrow = HybridCpuMiiResourceModelV1.Create(
            new HybridCpuMachineTopologyV1(1, 1, 4, 8, 2, 16), 8);

        IrRegisterAllocationResultV1 result = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            subject.Schedule, subject.Bundles, resourceModel: narrow,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);

        Assert.True(result.Status == IrRegisterAllocationStatusV1.SafeFallback, result.Reason);
        Assert.True(result.UsedExactFallback);
        Assert.Contains("topology resource", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicBudgetsAreFailClosedAndWallClockFree()
    {
        Subject subject = BuildSubject([new(HybridCpuOpcode.ADDI, ["v"], []), new(HybridCpuOpcode.ADDI, [], ["v"])]);
        HybridCpuRegisterAllocationOptionsV1 options = HybridCpuRegisterAllocationOptionsV1.Create(true, true,
            new(1, 1, 32, 32, 2, 16));

        IrRegisterAllocationResultV1 result = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            subject.Schedule, subject.Bundles, resourceModel: KnownModel(), options: options);

        Assert.Equal(IrRegisterAllocationStatusV1.BudgetExhausted, result.Status);
        Assert.True(result.UsedExactFallback);
        Assert.DoesNotContain("time", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(HybridCpuRegisterAllocationBudgetsV1).GetProperties(), property =>
            property.Name.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Millisecond", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepeatedAllocationIsByteIdentityDeterministic()
    {
        Subject subject = BuildSubject(
        [
            new(HybridCpuOpcode.ADDI, ["a"], []),
            new(HybridCpuOpcode.ADDI, ["b"], []),
            new(HybridCpuOpcode.ADDI, ["c"], []),
            new(HybridCpuOpcode.ADDI, [], ["a", "b"]),
            new(HybridCpuOpcode.ADDI, [], ["c"]),
            new(HybridCpuOpcode.JALR, [], [])
        ]);
        HybridCpuScheduleAwareRegisterAllocatorV1 allocator = new HybridCpuScheduleAwareRegisterAllocatorV1();

        IrRegisterAllocationResultV1 first = Allocate(subject, allocator);
        IrRegisterAllocationResultV1 second = Allocate(subject, allocator);

        Assert.True(first.Status == IrRegisterAllocationStatusV1.Allocated, first.Reason);
        Assert.True(second.Status == IrRegisterAllocationStatusV1.Allocated, second.Reason);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        Assert.Equal(first.Witness?.WitnessDigest, second.Witness?.WitnessDigest);
        Assert.Equal(Project(first.FinalSchedule, first.FinalBundles), Project(second.FinalSchedule, second.FinalBundles));
    }

    [Fact]
    public void NonOverlappingIntervalsDeterministicallyReuseAnArchitecturalRegister()
    {
        Subject subject = BuildSubject(
        [
            new(HybridCpuOpcode.ADDI, ["a"], []),
            new(HybridCpuOpcode.ADDI, [], ["a"]),
            new(HybridCpuOpcode.FENCE, [], []),
            new(HybridCpuOpcode.ADDI, ["b"], []),
            new(HybridCpuOpcode.ADDI, [], ["b"])
        ]);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        IrRegisterAssignmentV1[] assignments = Assert.IsType<IrRegisterAllocationWitnessV1>(result.Witness)
            .Assignments.OrderBy(static assignment => assignment.ValueId, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, assignments.Length);
        Assert.Equal(assignments[0].RegisterId, assignments[1].RegisterId);
        Assert.True(assignments[0].ScheduledEndExclusive <= assignments[1].ScheduledStart);
    }

    [Fact]
    public void CandidateContainerOrderDoesNotChangeAssignmentOrWitness()
    {
        InstructionSpec[] specs = [new(HybridCpuOpcode.ADDI, ["v"], []), new(HybridCpuOpcode.ADDI, [], ["v"])];
        int[] ascending = HybridCpuNativeAbiContractV2.Default.AllocatableRegisters.ToArray();
        int[] descending = ascending.Reverse().ToArray();
        Subject firstSubject = BuildSubject(specs,
            new Dictionary<string, ValueConstraint>(StringComparer.Ordinal) { ["v"] = new(ascending, null) });
        Subject secondSubject = BuildSubject(specs,
            new Dictionary<string, ValueConstraint>(StringComparer.Ordinal) { ["v"] = new(descending, null) });

        IrRegisterAllocationResultV1 first = Allocate(firstSubject);
        IrRegisterAllocationResultV1 second = Allocate(secondSubject);

        Assert.Equal(first.Witness?.Assignments, second.Witness?.Assignments);
        Assert.Equal(first.Witness?.WitnessDigest, second.Witness?.WitnessDigest);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
    }

    [Fact]
    public void SpillFrameWithoutArchitecturalReturnIsUnsupportedExactFallback()
    {
        int[] callerAndScratch = HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters
            .Intersect(HybridCpuNativeAbiContractV2.Default.AllocatableRegisters).Order().ToArray();
        const int valueCount = 16;
        List<InstructionSpec> specs = new List<InstructionSpec>();
        Dictionary<string, ValueConstraint> constraints = new Dictionary<string, ValueConstraint>(StringComparer.Ordinal);
        for (int index = 0; index < valueCount; index++)
        {
            string id = $"v{index:D2}";
            specs.Add(new(HybridCpuOpcode.ADDI, [id], []));
            constraints.Add(id, new(callerAndScratch, null));
        }
        specs.Add(new(HybridCpuOpcode.FENCE, [], []));
        for (int index = 0; index < valueCount; index++) specs.Add(new(HybridCpuOpcode.ADDI, [], [$"v{index:D2}"]));
        Subject subject = BuildSubject(specs, constraints);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.Equal(IrRegisterAllocationStatusV1.Unsupported, result.Status);
        Assert.True(result.UsedExactFallback);
        Assert.Contains("non-empty fixed frame", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SameBankRepairConflictFallsBackBeforeBundleOrEncoderRepair()
    {
        Subject subject = BuildSubject(
        [
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.ADDI, [], [], MemoryReadAddress: 0x1000),
            new(HybridCpuOpcode.JALR, [], [])
        ]);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.Equal(IrRegisterAllocationStatusV1.SafeFallback, result.Status);
        Assert.True(result.UsedExactFallback);
        Assert.Contains("bank", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpecialArchitecturalStateIsNeverAllocatedAsACompilerPhysicalRegister()
    {
        Subject subject = BuildSubject(
            [new(HybridCpuOpcode.ADDI, ["special"], []), new(HybridCpuOpcode.ADDI, [], ["special"])],
            new Dictionary<string, ValueConstraint>(StringComparer.Ordinal)
            {
                ["special"] = new([], null, IsSpecialState: true)
            });

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.NotEqual(IrRegisterAllocationStatusV1.Allocated, result.Status);
        Assert.True(result.UsedExactFallback);
        Assert.Null(result.Witness);
    }

    [Fact]
    public void SelectedUpstreamPlanIdentitiesAreSortedAndBoundIntoTheWitness()
    {
        Subject subject = BuildSubject([new(HybridCpuOpcode.ADDI, ["v"], []), new(HybridCpuOpcode.ADDI, [], ["v"])]);
        string a = new('a', 64);
        string b = new('b', 64);
        HybridCpuScheduleAwareRegisterAllocatorV1 allocator = new HybridCpuScheduleAwareRegisterAllocatorV1();
        IrRegisterAllocationResultV1 first = allocator.Allocate(subject.Schedule, subject.Bundles,
            new([b, a], [], [], []), KnownModel(), HybridCpuRegisterAllocationOptionsV1.Qualification);
        IrRegisterAllocationResultV1 second = allocator.Allocate(subject.Schedule, subject.Bundles,
            new([a, b], [], [], []), KnownModel(), HybridCpuRegisterAllocationOptionsV1.Qualification);

        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, first.Status);
        Assert.Equal(first.Witness?.SelectedPlansDigest, second.Witness?.SelectedPlansDigest);
        Assert.Equal(first.Witness?.WitnessDigest, second.Witness?.WitnessDigest);
    }

    [Fact]
    public void LlvmFrontendVirtualValuesAllocateThroughCoreAndLowerToNativeRegisterRoles()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hybridcpu-phase20-{Guid.NewGuid():N}.ll");
        try
        {
            File.WriteAllText(path, """
                target datalayout = "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64"
                target triple = "hybridcpuv2-unknown-none"
                define void @kernel() {
                entry:
                  %sum = add i32 1, 2
                  %product = mul i32 %sum, 4
                  ret void
                }
                !llvm.ident = !{!0}
                !0 = !{!"LLVM 20.1.2"}
                """, new UTF8Encoding(false));
            LlvmSemanticMappingResultV1 mapped = new LlvmSemanticMapperV1().MapFile(path, LlvmInputKind.TextIr);
            if (!PinnedRuntimeAvailable())
            {
                Assert.Equal(LlvmSemanticMappingStatusV1.Unavailable, mapped.Status);
                Assert.Null(mapped.Program);
                Assert.Equal("HCLL0002", Assert.Single(mapped.Diagnostics).Code);
                return;
            }
            Assert.True(mapped.Status == LlvmSemanticMappingStatusV1.Success,
                string.Join('|', mapped.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
            IrProgram program = Assert.IsType<IrProgram>(mapped.Program);
            IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
            Subject subject = new(schedule, new HybridCpuBundleFormer().BundleProgram(schedule));

            IrRegisterAllocationResultV1 result = Allocate(subject);

            Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, result.Reason);
            Assert.Equal(2, Assert.IsType<IrRegisterAllocationWitnessV1>(result.Witness).Assignments.Count);
            HybridCpuInstructionWord[] words = LowerWords(result.FinalBundles);
            HybridCpuInstructionWord add = Assert.Single(words, static word => word.OpCode == (uint)HybridCpuOpcode.ADD);
            HybridCpuInstructionWord multiply = Assert.Single(words, static word => word.OpCode == (uint)HybridCpuOpcode.MUL);
            Assert.True(HybridCpuInstructionWord.TryUnpackArchRegs(add.Word1, out byte addRd, out _, out _));
            Assert.True(HybridCpuInstructionWord.TryUnpackArchRegs(multiply.Word1, out byte mulRd, out byte mulRs1, out _));
            Assert.NotEqual(HybridCpuInstructionWord.NoArchReg, addRd);
            Assert.Equal(addRd, mulRs1);
            Assert.NotEqual(HybridCpuInstructionWord.NoArchReg, mulRd);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ModuloLifetimeRetainsPhiIdentityAndReceivesFreshDistanceDagAndMiiProof()
    {
        Subject subject = BuildLoopSubject();

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, result.Reason);
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(result.Witness);
        Assert.Contains(witness.Assignments, static assignment => assignment.ValueId == "loop");
        IrLoopAllocationRebuildV1 loop = Assert.Single(witness.Rebuild.Loops);
        Assert.Equal(IrCanonicalLoopStatusV1.Qualified, loop.CanonicalStatus);
        Assert.Equal(IrLoopMiiEligibilityV1.EligibleLowerBound, loop.MiiEligibility);
        Assert.NotNull(loop.ProvenLowerBoundIi);
        Assert.Equal(64, loop.DistanceDagDigest.Length);
        Assert.Equal(64, loop.MiiProofDigest?.Length);
    }

    [Fact]
    public void HighPressureLoopSpillCannotManufactureFreshModuloProofAndSelectsExactFallback()
    {
        Subject subject = BuildHighPressureLoopSubject();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 originalLoop = Assert.Single(analyzer.Canonicalize(subject.Schedule.Program, KnownModel()).Loops);
        IrLoopMiiReportV1 originalMii = analyzer.ComputeMii(subject.Schedule.Program, originalLoop, KnownModel());
        Assert.NotEqual(IrLoopMiiEligibilityV1.StaleProof, originalMii.Eligibility);

        IrRegisterAllocationResultV1 result = Allocate(subject);

        Assert.Equal(IrRegisterAllocationStatusV1.SafeFallback, result.Status);
        Assert.True(result.UsedExactFallback);
        Assert.Null(result.Witness);
        Assert.Contains("fresh distance-DAG/MII evidence", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AllocationSurfaceDoesNotClaimRuntimeLegalityPublicationCommitOrRetireAuthority()
    {
        string[] forbidden = ["Rename", "FreeList", "Scoreboard", "Publication", "Commit", "Retire", "RuntimeLegality"];
        Type[] types =
        [
            typeof(HybridCpuScheduleAwareRegisterAllocatorV1),
            typeof(IrRegisterAllocationWitnessV1),
            typeof(IrAllocationProofRebuildV1),
            typeof(HybridCpuRegisterAllocationContractV1)
        ];

        Assert.All(types.SelectMany(static type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)), property =>
            Assert.DoesNotContain(forbidden, token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        Assert.All(types.SelectMany(static type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public)), method =>
            Assert.DoesNotContain(forbidden, token => method.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(typeof(HybridCpuScheduleAwareRegisterAllocatorV1).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "HybridCPU_ISE" or "HybridCPU_Compiler");
    }

    private static IrRegisterAllocationResultV1 Allocate(
        Subject subject,
        HybridCpuScheduleAwareRegisterAllocatorV1? allocator = null) =>
        (allocator ?? new HybridCpuScheduleAwareRegisterAllocatorV1()).Allocate(
            subject.Schedule, subject.Bundles,
            resourceModel: KnownModel(),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);

    private static HybridCpuMiiResourceModelV1 KnownModel() => HybridCpuMiiResourceModelV1.Create(
        new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    private static bool PinnedRuntimeAvailable() => new LlvmModuleImporterV1().ProbeRuntime().IsAvailable;

    private static HybridCpuInstructionWord[] LowerWords(IrProgramBundlingResult bundles) =>
        new HybridCpuBundleLowerer().LowerProgram(bundles)
            .SelectMany(static bundle => Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount)
                .Select(bundle.GetInstruction))
            .Where(static word => word.OpCode != (uint)HybridCpuOpcode.Nope)
            .ToArray();

    private static Subject BuildSubject(
        IReadOnlyList<InstructionSpec> specs,
        IReadOnlyDictionary<string, ValueConstraint>? constraints = null)
    {
        HybridCpuInstructionWord[] words = specs.Select(static spec => Word(spec.Opcode)).ToArray();
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, words);
        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        SortedSet<string> identities = new SortedSet<string>(StringComparer.Ordinal);
        IrInstruction[] instructions = original.Instructions.Select((instruction, index) =>
        {
            InstructionSpec spec = specs[index];
            IrOperand[] virtualDefs = spec.Defs.Select(static id => VirtualOperand(id, "def")).ToArray();
            IrOperand[] virtualUses = spec.Uses.Select(static id => VirtualOperand(id, "use")).ToArray();
            foreach (string id in spec.Defs)
            {
                identities.Add(id);
                accesses.Add(new(id, index, IrValueAccessKind.Def));
            }
            foreach (string id in spec.Uses)
            {
                identities.Add(id);
                accesses.Add(new(id, index, IrValueAccessKind.Use));
            }
            IrMemoryRegion? memoryRead = spec.MemoryReadAddress.HasValue
                ? new(spec.MemoryReadAddress.Value, 8, IsWrite: false) : null;
            IrSideEffectSummaryV1 sideEffects = spec.IsCall
                ? instruction.SideEffects with
                {
                    ArchitecturalEffects = instruction.SideEffects.ArchitecturalEffects | IrArchitecturalEffectKind.Call
                }
                : instruction.SideEffects;
            if (memoryRead is not null)
                sideEffects = sideEffects with
                {
                    Memory = new(IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic,
                        IrMemoryOrdering.NotAtomic, memoryRead, null)
                };
            return instruction with
            {
                Operands = instruction.Operands.Concat(virtualDefs).Concat(virtualUses).ToArray(),
                Annotation = instruction.Annotation with
                {
                    Defs = instruction.Annotation.Defs.Concat(virtualDefs).ToArray(),
                    Uses = instruction.Annotation.Uses.Concat(virtualUses).ToArray(),
                    MemoryReadRegion = memoryRead
                },
                SideEffects = sideEffects
            };
        }).ToArray();
        Dictionary<int, IrInstruction> byIndex = instructions.ToDictionary(static instruction => instruction.Index);
        IrBasicBlock[] blocks = original.BasicBlocks.Select(block => block with
        {
            Instructions = block.Instructions.Select(instruction => byIndex[instruction.Index]).ToArray()
        }).ToArray();
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;
        int[] defaultRegisters = HybridCpuNativeAbiContractV2.Default.AllocatableRegisters.ToArray();
        IrVirtualValueV1[] values = identities.Select(id =>
        {
            ValueConstraint constraint = constraints is not null && constraints.TryGetValue(id, out ValueConstraint? found)
                ? found : new(defaultRegisters, null);
            int[] groups = constraint.LegalRegisters.Select(register =>
                target.ArchitecturalRegisters[register].RegisterGroup).Distinct().Order().ToArray();
            return new IrVirtualValueV1(id,
                new(IrCanonicalValueKind.Integer, 64, IsSigned: false),
                IrVirtualValueClass.ScalarInteger,
                new(64, 1, false, !constraint.IsSpecialState,
                    constraint.IsSpecialState ? null : HybridCpuArchitecturalRegisterClass.ScalarInteger64,
                    constraint.IsSpecialState ? HybridCpuSpecialStateClass.ControlStatus : null,
                    constraint.FixedRegister, 0, ["native-scalar"], null,
                    constraint.LegalRegisters, groups, target.ContractDigest, null));
        }).ToArray();
        IrProgram program = original with
        {
            Instructions = instructions,
            ControlFlowGraph = new(blocks, original.ControlFlowGraph.Edges),
            ValueFlow = new("hybridcpu.value-flow/v1", 1, values, accesses)
        };
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        return new(schedule, new HybridCpuBundleFormer().BundleProgram(schedule));
    }

    private static Subject BuildLoopSubject()
    {
        Subject linear = BuildSubject(
        [
            new(HybridCpuOpcode.ADDI, ["entry"], []),
            new(HybridCpuOpcode.ADDI, ["loop"], ["entry"]),
            new(HybridCpuOpcode.ADDI, [], ["loop"]),
            new(HybridCpuOpcode.JALR, [], [])
        ]);
        IrProgram original = linear.Schedule.Program;
        IrInstruction[] instructions = original.Instructions.ToArray();
        IrBasicBlock preheader = new IrBasicBlock(
            0, 0, 0, instructions[0].EncodedAddress, instructions[0].EncodedAddress,
            false, [instructions[0]], [], [1], false, false, null, [], null, null);
        IrBasicBlock loop = new IrBasicBlock(
            1, 1, 2, instructions[1].EncodedAddress, instructions[2].EncodedAddress,
            false, instructions[1..3], [0, 1], [1, 2], false, false, null, [], null, null);
        IrBasicBlock exit = new IrBasicBlock(
            2, 3, 3, instructions[3].EncodedAddress, instructions[3].EncodedAddress,
            false, [instructions[3]], [1], [], false, false, null, [], null, null);
        IrProgram program = original with
        {
            ControlFlowGraph = new(
                [preheader, loop, exit],
                [
                    new(0, 1, IrControlFlowEdgeKind.Fallthrough),
                    new(1, 1, IrControlFlowEdgeKind.Branch),
                    new(1, 2, IrControlFlowEdgeKind.Fallthrough)
                ]),
            ValueFlow = original.ValueFlow with
            {
                Accesses = [.. original.ValueFlow.Accesses, new("loop", 1, IrValueAccessKind.PhiEdgeUse, 1, 1)]
            }
        };
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        return new(schedule, new HybridCpuBundleFormer().BundleProgram(schedule));
    }

    private static Subject BuildHighPressureLoopSubject()
    {
        const int valueCount = 27;
        List<InstructionSpec> specs = new List<InstructionSpec> { new(HybridCpuOpcode.Nope, [], []) };
        for (int index = 0; index < valueCount; index++) specs.Add(new(HybridCpuOpcode.ADDI, [$"loop.v{index:D2}"], []));
        specs.Add(new(HybridCpuOpcode.FENCE, [], []));
        for (int index = 0; index < valueCount; index++) specs.Add(new(HybridCpuOpcode.ADDI, [], [$"loop.v{index:D2}"]));
        specs.Add(new(HybridCpuOpcode.JALR, [], []));
        Subject linear = BuildSubject(specs);
        IrProgram original = linear.Schedule.Program;
        IrInstruction[] instructions = original.Instructions.ToArray();
        int exitIndex = instructions.Length - 1;
        IrBasicBlock preheader = new IrBasicBlock(
            0, 0, 0, instructions[0].EncodedAddress, instructions[0].EncodedAddress,
            false, [instructions[0]], [], [1], false, false, null, [], null, null);
        IrBasicBlock loop = new IrBasicBlock(
            1, 1, exitIndex - 1, instructions[1].EncodedAddress, instructions[exitIndex - 1].EncodedAddress,
            false, instructions[1..exitIndex], [0, 1], [1, 2], false, false, null, [], null, null);
        IrBasicBlock exit = new IrBasicBlock(
            2, exitIndex, exitIndex, instructions[exitIndex].EncodedAddress, instructions[exitIndex].EncodedAddress,
            false, [instructions[exitIndex]], [1], [], false, false, null, [], null, null);
        IrProgram program = original with
        {
            ControlFlowGraph = new(
                [preheader, loop, exit],
                [
                    new(0, 1, IrControlFlowEdgeKind.Fallthrough),
                    new(1, 1, IrControlFlowEdgeKind.Branch),
                    new(1, 2, IrControlFlowEdgeKind.Fallthrough)
                ])
        };
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        return new(schedule, new HybridCpuBundleFormer().BundleProgram(schedule));
    }

    private static HybridCpuInstructionWord Word(HybridCpuOpcode opcode) => new()
    {
        OpCode = (uint)opcode,
        DataTypeValue = HybridCpuDataType.INT64,
        PredicateMask = byte.MaxValue,
        VirtualThreadId = 0,
        Word1 = opcode == HybridCpuOpcode.JALR
            ? HybridCpuInstructionWord.PackArchRegs(0, 1, HybridCpuInstructionWord.NoArchReg)
            : HybridCpuInstructionWord.PackArchRegs(
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg)
    };

    private static IrOperand VirtualOperand(string identity, string role) =>
        new(IrOperandKind.VirtualValue, 0, identity);

    private static string Project(IrProgramSchedule schedule, IrProgramBundlingResult bundles) => string.Join('|',
        schedule.Program.Instructions.Select(static instruction =>
            $"{instruction.Index}:{instruction.Opcode}:{string.Join(',', instruction.Annotation.Defs.Select(Operand))}:{string.Join(',', instruction.Annotation.Uses.Select(Operand))}"),
        bundles.BlockResults.SelectMany(static block => block.Bundles.Select(bundle =>
            $"{block.BlockId}:{bundle.Cycle}:{string.Join(',', bundle.Slots.Select(slot => slot.Instruction?.Index ?? -1))}")));

    private static string Operand(IrOperand operand) => $"{operand.Kind}:{operand.Value}:{operand.Name}";

    private sealed record InstructionSpec(
        HybridCpuOpcode Opcode,
        IReadOnlyList<string> Defs,
        IReadOnlyList<string> Uses,
        bool IsCall = false,
        ulong? MemoryReadAddress = null);

    private sealed record ValueConstraint(
        IReadOnlyList<int> LegalRegisters,
        int? FixedRegister,
        bool IsSpecialState = false);

    private sealed record Subject(IrProgramSchedule Schedule, IrProgramBundlingResult Bundles);
}
