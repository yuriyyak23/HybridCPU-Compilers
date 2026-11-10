using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU_ISE.Arch;
using System.Reflection;
using System.Security.Cryptography;
using YAKSys_Hybrid_CPU;

namespace HybridCPU_ISE.Tests.CompilerTests;

[Collection("RefPlan6 ISE Execution")]
public sealed class CompilerRefPlan6Phase03NativeCallAbiTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(ManagedCallGraphFixtures).Assembly.Location);
    private static readonly string FixtureType = typeof(ManagedCallGraphFixtures).FullName!;
    private static readonly HybridCpuMiiResourceModelV1 ResourceModel =
        HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    [Fact]
    public void CallControlExtension_BindsExactIseLinkAndBundleCompensationWithoutRuntimeAuthority()
    {
        HybridCpuNativeCallControlContractV1 contract = HybridCpuNativeCallControlContractV1.Default;

        Assert.Equal(HybridCpuNativeAbiContractV2.Default.ContractDigest, contract.NativeAbiDigest);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, contract.TargetMachineDigest);
        Assert.Equal(4, HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
        Assert.Equal(HybridCpuBundleSerializer.BundleSizeBytes,
            HybridCpuNativeCallControlContractV1.SequentialBundleStrideBytes);
        Assert.Equal(252, HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Count,
            HybridCpuNativeCallControlContractV1.MaximumRegisterArguments);
        Assert.Equal(0x3fff04UL, contract.BiasReturnSentinel(0x400000));
        Assert.True(contract.MatchesExecutionContract(4, HybridCpuBundleSerializer.BundleSizeBytes));
        Assert.False(contract.MatchesExecutionContract(8, HybridCpuBundleSerializer.BundleSizeBytes));
        Assert.False(contract.MatchesExecutionContract(4, HybridCpuBundleSerializer.BundleSizeBytes / 2));
        Assert.DoesNotContain(typeof(HybridCpuNativeCallControlContractV1).GetMembers(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly), static member =>
            member.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("Retire", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("Publish", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportedCalls_HavePreRaAbiCopiesFixedLocationsAndExplicitClobberOperands()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.PrimitiveRoot));
        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        IrProgram root = graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.PrimitiveRoot)).Import.Program!;
        IrInstruction call = Assert.Single(root.Instructions,
            static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);

        IrVirtualValueV1[] callValues = call.Annotation.Uses.Where(static operand => operand.Kind == IrOperandKind.VirtualValue)
            .Select(operand => root.ValueFlow.Values.Single(value => value.StableId == operand.Name)).ToArray();
        Assert.Equal(3, callValues.Length);
        Assert.Equal(5, callValues[0].Allocation.FixedRegisterId);
        Assert.Equal([HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0],
            HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[1]],
            callValues.Skip(1).Select(static value => value.Allocation.FixedRegisterId));
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters,
            call.Operands.Where(static operand => operand.Name.StartsWith("call-clobber:x", StringComparison.Ordinal))
                .Select(static operand => checked((int)operand.Value)));
        Assert.Contains(root.Instructions, static instruction => instruction.StableIdentity.Contains(":call-result-copy", StringComparison.Ordinal));
    }

    [Fact]
    public void NonLeafAllocation_SavesReturnAddressAfterRaAndRebuildsAllAffectedFacts()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.Root));
        CompiledMethod root = Compile(graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.Root)));
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(root.Allocation.Witness);

        Assert.Contains(HybridCpuNativeAbiContractV2.ReturnAddressRegister, witness.Frame.SavedRegisters);
        Assert.Contains(witness.Frame.Slots, static slot => slot.Identity == "saved:x1");
        Assert.True(witness.Rebuild.DependenciesCurrent);
        Assert.True(witness.Rebuild.LivenessCurrent);
        Assert.True(witness.Rebuild.PressureCurrent);
        Assert.True(witness.Rebuild.ExactW8PlacementRecomputed);
        Assert.All(root.Allocation.FinalSchedule.Program.Instructions.SelectMany(static instruction =>
            instruction.Annotation.Defs.Concat(instruction.Annotation.Uses)),
            static operand => Assert.NotEqual(IrOperandKind.VirtualValue, operand.Kind));
        Assert.Contains(root.Allocation.FinalSchedule.Program.Instructions, static instruction =>
            instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
            instruction.Operands.Any(static operand => operand.Name == "rd" && operand.Value == 1));
        Assert.All(root.Allocation.FinalSchedule.Program.Instructions.Where(static instruction =>
            instruction.Annotation.ControlFlowKind == IrControlFlowKind.Return), static instruction =>
            Assert.Equal(HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes, instruction.Immediate));
    }

    [Fact]
    public void RegisterArgumentBudget_FailsClosedBeforePhysicalEmission()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.NineArgumentRoot));
        Assert.Equal(RestrictedCilImportStatusV1.BudgetExhausted, graph.Status);
        Assert.Equal("HCCIL2002", Assert.Single(graph.Diagnostics).Code);
        Assert.Empty(graph.Methods);
        Assert.Null(graph.Graph);

        ManagedCallGraphCompilationV1 withinCurrentRegisterSurface = Import(nameof(ManagedCallGraphFixtures.FiveArgumentRoot));
        Assert.Equal(RestrictedCilImportStatusV1.Success, withinCurrentRegisterSurface.Status);
        Assert.NotNull(withinCurrentRegisterSurface.Graph);
        Assert.Empty(withinCurrentRegisterSurface.Diagnostics);
    }

    [Fact]
    public void ZeroAndFourRegisterArgumentCalls_AllocateWithExactVoidAndResultShapes()
    {
        ManagedCallGraphCompilationV1 zero = Import(nameof(ManagedCallGraphFixtures.NoArgumentVoidRoot));
        Assert.Equal(RestrictedCilImportStatusV1.Success, zero.Status);
        CompiledMethod zeroRoot = Compile(zero.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.NoArgumentVoidRoot)));
        IrInstruction zeroCall = Assert.Single(zeroRoot.Allocation.FinalSchedule.Program.Instructions,
            static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        Assert.DoesNotContain(zeroCall.Annotation.Uses, static operand => operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value is >= 10 and <= 17);
        Assert.DoesNotContain(zeroCall.Annotation.Defs,
            static operand => operand.Kind == IrOperandKind.VirtualValue);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters,
            zeroCall.Annotation.Defs.Where(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister)
                .Select(static operand => checked((int)operand.Value)));

        ManagedCallGraphCompilationV1 maximum = Import(nameof(ManagedCallGraphFixtures.FourArgumentRoot));
        Assert.Equal(RestrictedCilImportStatusV1.Success, maximum.Status);
        CompiledMethod maximumRoot = Compile(maximum.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.FourArgumentRoot)));
        IrInstruction maximumCall = Assert.Single(maximumRoot.Allocation.FinalSchedule.Program.Instructions,
            static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        Assert.Equal([5, .. HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Take(4)],
            maximumCall.Annotation.Uses.Where(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister)
                .Select(static operand => checked((int)operand.Value)).Distinct());
        Assert.Single(maximumCall.Annotation.Defs,
            static operand => operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 10 &&
                operand.Name.Contains(":call-result-abi:value", StringComparison.Ordinal));
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters,
            maximumCall.Annotation.Defs.Where(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister)
                .Select(static operand => checked((int)operand.Value)).Distinct().Order());
        IrRegisterAllocationWitnessV1 maximumWitness = Assert.IsType<IrRegisterAllocationWitnessV1>(maximumRoot.Allocation.Witness);
        Assert.All(maximumWitness.Assignments.Where(static assignment =>
                assignment.ValueId.Contains(":call-arg-abi:", StringComparison.Ordinal) ||
                assignment.ValueId.Contains(":call-result-abi:", StringComparison.Ordinal)),
            static assignment => Assert.True(assignment.IsPrecolored));
    }

    [Fact]
    public void LiveAcrossCallPressure_ForcesSpillsAndProtectsValues()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.PressureRoot));
        CompiledMethod root = Compile(graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.PressureRoot)));
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(root.Allocation.Witness);

        Assert.NotEmpty(witness.Spills);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.SpillReload);
        Assert.Contains(witness.Mutations, static mutation => mutation.Kind == IrAllocationMutationKindV1.SpillStore);
        Assert.DoesNotContain(witness.Spills, static spill =>
            spill.ValueId.Contains(":call-result-abi:", StringComparison.Ordinal) ||
            spill.ValueId.Contains(":return-abi:", StringComparison.Ordinal));
        Assert.All(witness.Assignments.Where(static assignment => assignment.LiveAcrossCall), assignment =>
            Assert.Contains(assignment.RegisterId, HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters));
    }

    [Fact]
    public void ValueReusedAsArgumentAfterVoidCall_IsAllocatedToCalleeSavedStorage()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.ReuseAcrossVoidCallsRoot));
        CompiledMethod root = Compile(graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.ReuseAcrossVoidCallsRoot)));
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(root.Allocation.Witness);
        Assert.All(witness.Assignments.Where(static assignment => assignment.LiveAcrossCall),
            static assignment => Assert.Contains(assignment.RegisterId,
                HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters));
    }

    [Fact]
    public void DiscardedCallResultBeforeVoidReturn_PreservesArchitecturalReturnAfterCall()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.DiscardedCallResultVoidRoot));
        CompiledMethod root = Compile(graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.DiscardedCallResultVoidRoot)));
        IrInstruction[] controls = root.Allocation.FinalSchedule.Program.Instructions
            .Where(static instruction => instruction.Annotation.ControlFlowKind is IrControlFlowKind.Call or IrControlFlowKind.Return)
            .ToArray();

        Assert.Equal(2, controls.Length);
        Assert.Equal(IrControlFlowKind.Call, controls[0].Annotation.ControlFlowKind);
        Assert.Equal(IrControlFlowKind.Return, controls[1].Annotation.ControlFlowKind);
        Assert.True(controls[1].Index > controls[0].Index);
    }

    [Fact]
    public void ArrayStoreConstructorAndDiscardedCallResult_PreserveVoidReturn()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.InitPlayerShapeRoot));
        CompiledMethod method = Compile(graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.ControllerFixture.InitPlayer)));
        IrInstruction[] controls = method.Allocation.FinalSchedule.Program.Instructions
            .Where(static instruction => instruction.Annotation.ControlFlowKind is IrControlFlowKind.Call or IrControlFlowKind.Return)
            .ToArray();

        Assert.NotEmpty(controls);
        Assert.Equal(IrControlFlowKind.Return, controls[^1].Annotation.ControlFlowKind);
        Assert.True(controls[^1].Index > controls.Last(static instruction =>
            instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call).Index);
    }

    [Fact]
    public void LoopBackedgeToManagedCall_ReentersArgumentCopiesAndCallTargetMaterialization()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.LoopCallingLeafRoot));
        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        IrProgram root = graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.LoopCallingLeafRoot)).Import.Program!;
        IrInstruction call = Assert.Single(root.Instructions,
            static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        IrInstruction callArgumentCopy = Assert.Single(root.Instructions,
            instruction => instruction.StableIdentity.StartsWith(call.StableIdentity + ":call-arg-copy:",
                StringComparison.Ordinal));
        IrInstruction backedge = Assert.Single(root.Instructions,
            instruction => instruction.Annotation.ControlFlowKind is IrControlFlowKind.ConditionalBranch or IrControlFlowKind.UnconditionalBranch &&
                instruction.Annotation.ResolvedBranchTargetInstructionIndex is int target && target <= call.Index);

        int targetIndex = Assert.IsType<int>(backedge.Annotation.ResolvedBranchTargetInstructionIndex);
        Assert.Equal(callArgumentCopy.Index, targetIndex);
        Assert.True(targetIndex < call.Index);

        CompiledMethod compiled = Compile(graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.LoopCallingLeafRoot)));
        IrProgram final = compiled.Allocation.FinalSchedule.Program;
        IrInstruction finalCall = Assert.Single(final.Instructions,
            static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        IrInstruction finalArgumentCopy = Assert.Single(final.Instructions,
            instruction => instruction.StableIdentity.StartsWith(finalCall.StableIdentity + ":call-arg-copy:",
                StringComparison.Ordinal));
        IrInstruction finalBackedge = Assert.Single(final.Instructions,
            instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.UnconditionalBranch &&
                instruction.StableIdentity.EndsWith(":long-branch-low", StringComparison.Ordinal) &&
                instruction.Annotation.ResolvedBranchTargetInstructionIndex is int finalTarget &&
                finalTarget <= finalCall.Index);

        int finalTargetIndex = Assert.IsType<int>(finalBackedge.Annotation.ResolvedBranchTargetInstructionIndex);
        Assert.Equal(finalArgumentCopy.Index, finalTargetIndex);
        Assert.True(finalTargetIndex < finalCall.Index);
    }

    [Fact]
    public void AllocationAndLinkedQualificationImage_AreByteDeterministic()
    {
        ManagedCallGraphCompilationV1 firstGraph = Import(nameof(ManagedCallGraphFixtures.Root));
        ManagedCallGraphCompilationV1 secondGraph = Import(nameof(ManagedCallGraphFixtures.Root));
        CompiledImage first = CompileImage(firstGraph);
        CompiledImage second = CompileImage(secondGraph);

        Assert.Equal(first.RootAddress, second.RootAddress);
        Assert.Equal(SHA256.HashData(first.Bytes), SHA256.HashData(second.Bytes));
        Assert.Equal(firstGraph.Graph!.GraphDigest, secondGraph.Graph!.GraphDigest);
    }

    private static ManagedCallGraphCompilationV1 Import(string root) =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase03-fixture.dll",
            [new(FixtureType, root)], []));

    private static CompiledMethod Compile(ManagedCompiledMethodV1 method)
    {
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(method.Import.Program!);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles, resourceModel: ResourceModel,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.True(allocation.Status == IrRegisterAllocationStatusV1.Allocated,
            $"{method.Identity.StableIdentity}: {allocation.Status}: {allocation.Reason}");
        IReadOnlyList<HybridCpuInstructionBundle> lowered = HybridCpuControlFlowRelocationResolver.ApplyRelocations(
            allocation.FinalBundles, new HybridCpuBundleLowerer().LowerProgram(allocation.FinalBundles));
        return new(method.Identity.StableIdentity, allocation, lowered.ToArray());
    }

    private static CompiledImage CompileImage(ManagedCallGraphCompilationV1 graph)
    {
        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        CompiledMethod[] methods = graph.Methods.OrderBy(method =>
            graph.Graph!.CompilationOrder.ToList().IndexOf(method.Identity.StableIdentity)).Select(Compile).ToArray();
        Dictionary<string, int> addresses = new Dictionary<string, int>(StringComparer.Ordinal);
        int nextBundle = 0;
        foreach (CompiledMethod method in methods)
        {
            addresses.Add(method.Identity, checked(nextBundle * HybridCpuBundleSerializer.BundleSizeBytes));
            nextBundle += method.Bundles.Length;
        }

        List<HybridCpuInstructionBundle> all = new List<HybridCpuInstructionBundle>(nextBundle);
        foreach (CompiledMethod method in methods)
        {
            int methodBaseBundle = all.Count;
            int localBundle = 0;
            foreach (IrBasicBlockBundlingResult block in method.Allocation.FinalBundles.BlockResults)
                foreach (IrMaterializedBundle materialized in block.Bundles)
                {
                    HybridCpuInstructionBundle bundle = method.Bundles[localBundle];
                    foreach (IrMaterializedBundleSlot slot in materialized.Slots.Where(static slot =>
                        slot.Instruction?.Annotation.ControlFlowKind == IrControlFlowKind.Call))
                    {
                        IrInstruction call = slot.Instruction!;
                        int callAddress = checked((methodBaseBundle + localBundle) * HybridCpuBundleSerializer.BundleSizeBytes);
                        int displacement = checked(addresses[call.Annotation.BranchTargetSymbolName!] - callAddress);
                        Assert.InRange(displacement, short.MinValue, short.MaxValue);
                        HybridCpuInstructionWord word = bundle.GetInstruction(slot.SlotIndex);
                        word.Immediate = unchecked((ushort)(short)displacement);
                        bundle.SetInstruction(slot.SlotIndex, word);
                    }
                    all.Add(bundle);
                    localBundle++;
                }
        }
        string root = graph.Graph!.RootIdentities.Single();
        return new(new HybridCpuBundleSerializer().SerializeProgram(all), checked((ulong)addresses[root]));
    }

    private sealed record CompiledMethod(string Identity, IrRegisterAllocationResultV1 Allocation,
        HybridCpuInstructionBundle[] Bundles);
    private sealed record CompiledImage(byte[] Bytes, ulong RootAddress);
}
