using System.Buffers.Binary;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase25AManagedMetadataFinalizationTests
{
    [Fact]
    public void ContractIsAllocationAndManagedAbiBoundWithProductionDisabled()
    {
        HybridCpuManagedMetadataContractV1 contract = HybridCpuManagedMetadataContractV1.Default;

        Assert.Equal("hybridcpu.managed-metadata-finalization/v1", HybridCpuManagedMetadataContractV1.SchemaId);
        Assert.Equal(HybridCpuManagedAbiFamilyV1.Default.ContractDigest, contract.ManagedAbiDigest);
        Assert.Equal(HybridCpuRegisterAllocationContractV1.Default.ContractDigest, contract.AllocationContractDigest);
        Assert.False(HybridCpuManagedMetadataOptionsV1.Production.EnableFinalization);
        Assert.True(HybridCpuManagedMetadataOptionsV1.Qualification.EnableFinalization);
        Assert.True(HybridCpuManagedMetadataOptionsV1.Qualification.EnableObjectReferences);
        Assert.Equal(64, contract.ContractDigest.Length);
        Assert.Equal(64, contract.ProductionOptionsDigest.Length);
        Assert.Equal(64, contract.QualificationOptionsDigest.Length);
    }

    [Fact]
    public void ProductionDefaultReturnsNoMetadata()
    {
        AllocatedSubject subject = AllocateSubject("root");

        HybridCpuManagedMetadataArtifactV1 result = Finalizer().Finalize(Request(subject, "root"));

        Assert.Equal(HybridCpuManagedMetadataStatusV1.Disabled, result.Status);
        Assert.Empty(result.GcInfo);
        Assert.Empty(result.CodeManagerMetadata);
        Assert.Null(result.AllocationWitnessDigest);
    }

    [Fact]
    public void QualificationUsesFinalPhysicalRegisterAndExactBundleOffset()
    {
        AllocatedSubject subject = AllocateSubject("root");

        HybridCpuManagedMetadataArtifactV1 result = Finalize(Request(subject, "root"));

        Assert.True(result.Status == HybridCpuManagedMetadataStatusV1.Finalized, result.Reason);
        HybridCpuSafepointRecordV1 point = Assert.Single(result.Safepoints);
        HybridCpuGcReferenceLocationV1 location = Assert.Single(point.LiveReferences);
        IrRegisterAssignmentV1 assignment = subject.Result.Witness!.Assignments.Single(item => item.ValueId == "root");
        Assert.Equal(HybridCpuGcLocationKindV1.Register, location.LocationKind);
        Assert.Equal(assignment.RegisterId, location.RegisterId);
        Assert.Null(location.StackOffsetBytes);
        IrMaterializedBundle[] bundles = subject.Result.FinalBundles.BlockResults
            .OrderBy(static block => block.Block.StartInstructionIndex)
            .SelectMany(static block => block.Bundles.OrderBy(static bundle => bundle.Cycle)).ToArray();
        int callBundle = Array.FindIndex(bundles, bundle => bundle.Slots.Any(slot =>
            string.Equals(slot.Instruction?.StableIdentity, subject.CallIdentity, StringComparison.Ordinal)));
        Assert.True(callBundle >= 0);
        Assert.Equal(callBundle * HybridCpuBundleSerializer.BundleSizeBytes, point.CodeOffsetBytes);
        Assert.Equal(bundles.Length * HybridCpuBundleSerializer.BundleSizeBytes,
            BinaryPrimitives.ReadInt32LittleEndian(result.CodeManagerMetadata.AsSpan(24)));
        Assert.NotEmpty(result.GcInfo);
        Assert.NotEmpty(result.CodeManagerMetadata);
        Assert.Equal(subject.Result.Witness.WitnessDigest, result.AllocationWitnessDigest);
        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.Compatible,
            new HybridCpuManagedAbiConsumerV1().ValidateGcInfoHeader(
                HybridCpuManagedAbiFamilyV1.Default.CreateEnvelope(), result.GcInfo));
    }

    [Fact]
    public void ReferenceOrderDoesNotChangeMetadataOrDigest()
    {
        AllocatedSubject subject = AllocateSubject("alpha", "beta");
        HybridCpuManagedMetadataRequestV1 first = Request(subject, "alpha", "beta");
        HybridCpuManagedMetadataRequestV1 second = Request(subject, "beta", "alpha");

        HybridCpuManagedMetadataArtifactV1 a = Finalize(first);
        HybridCpuManagedMetadataArtifactV1 b = Finalize(second);

        Assert.True(a.Status == HybridCpuManagedMetadataStatusV1.Finalized, a.Reason);
        Assert.Equal(a.GcInfo, b.GcInfo);
        Assert.Equal(a.CodeManagerMetadata, b.CodeManagerMetadata);
        Assert.Equal(a.ResultDigest, b.ResultDigest);
        Assert.Equal(["alpha", "beta"], a.Safepoints.Single().LiveReferences
            .Select(static item => item.ValueIdentity).ToArray());
    }

    [Fact]
    public void ManagedReferenceKind_IsPreservedForRegisterAndSpillWitnessLocations()
    {
        string[] roots = Enumerable.Range(0, 36).Select(static index => $"root-{index:D2}").ToArray();
        AllocatedSubject subject = AllocateSubject(roots);
        IrRegisterAllocationWitnessV1 witness = subject.Result.Witness!;

        Assert.NotEmpty(witness.Spills);
        Assert.All(witness.Assignments.Select(static assignment => assignment.ValueId)
            .Concat(witness.Spills.Select(static spill => spill.ValueId)), valueId =>
        {
            IrAllocatedSemanticValueV1 semantic = witness.SemanticValues.Single(value => value.ValueId == valueId);
            Assert.Equal(IrCanonicalValueKind.ManagedObjectReference, semantic.ValueKind.Kind);
            Assert.Equal(IrVirtualValueClass.ManagedObjectReference, semantic.VirtualClass);
        });
        HybridCpuManagedMetadataArtifactV1 metadata = Finalize(Request(subject, roots));
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, metadata.Status);
        Assert.Contains(metadata.Safepoints.Single().LiveReferences, static location =>
            location.LocationKind == HybridCpuGcLocationKindV1.Stack);
    }

    [Fact]
    public void MissingOrDeadReferenceFailsUnknown()
    {
        AllocatedSubject subject = AllocateSubject("root");
        HybridCpuManagedMetadataRequestV1 missing = Request(subject, "absent");
        IrRegisterAllocationWitnessV1 witness = subject.Result.Witness!;
        IrRegisterAssignmentV1 assignment = witness.Assignments.Single(item => item.ValueId == "root");
        IrRegisterAllocationWitnessV1 deadWitness = witness with
        {
            Assignments = witness.Assignments.Select(item => item.ValueId == "root"
                ? item with { ScheduledEndExclusive = item.ScheduledStart }
                : item).ToArray()
        };
        IrRegisterAllocationResultV1 deadAllocation = subject.Result with { Witness = deadWitness };
        HybridCpuManagedMetadataRequestV1 dead = Request(subject, "root") with { Allocation = deadAllocation };

        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unsupported, Finalize(missing).Status);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unknown, Finalize(dead).Status);
    }

    [Fact]
    public void PrimitiveValueCannotBeMisreportedAsMovableReference()
    {
        AllocatedSubject subject = AllocateSubjectCore(managedReferences: false, "integer");

        HybridCpuManagedMetadataArtifactV1 result = Finalize(Request(subject, "integer"));

        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unsupported, result.Status);
        Assert.Empty(result.GcInfo);
    }

    [Fact]
    public void MissingActuallyLiveRootIsRejectedBeforeEmission()
    {
        AllocatedSubject subject = AllocateSubject("alpha", "beta");

        HybridCpuManagedMetadataArtifactV1 result = Finalize(Request(subject, "alpha"));

        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unsupported, result.Status);
        Assert.Contains("coverage", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.GcInfo);
    }

    [Theory]
    [InlineData(HybridCpuGcReferenceKindV1.ManagedByRef)]
    [InlineData(HybridCpuGcReferenceKindV1.InteriorReference)]
    public void ByRefAndInteriorReferenceRemainUnsupported(HybridCpuGcReferenceKindV1 kind)
    {
        AllocatedSubject subject = AllocateSubject("root");
        HybridCpuManagedMetadataRequestV1 request = Request(subject, "root");
        request = request with
        {
            Safepoints = [request.Safepoints[0] with
            {
                LiveReferences = [new("root", kind)]
            }]
        };

        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unsupported, Finalize(request).Status);
    }

    [Fact]
    public void StalePlacementAndTamperedWitnessFailClosed()
    {
        AllocatedSubject subject = AllocateSubject("root");
        HybridCpuManagedMetadataRequestV1 absentSite = Request(subject, "root") with
        {
            Safepoints = [new("not-in-final-placement", HybridCpuSafepointCategoryV1.CallSite,
                [new("root", HybridCpuGcReferenceKindV1.ObjectReference)])]
        };
        IrRegisterAllocationWitnessV1 witness = subject.Result.Witness!;
        IrRegisterAllocationWitnessV1 tamperedWitness = witness with
        {
            Assignments = witness.Assignments.Select(item => item.ValueId == "root"
                ? item with { RegisterId = item.RegisterId == 5 ? 6 : 5 }
                : item).ToArray()
        };
        HybridCpuManagedMetadataRequestV1 tampered = Request(subject, "root") with
        {
            Allocation = subject.Result with { Witness = tamperedWitness }
        };

        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unknown, Finalize(absentSite).Status);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Unknown, Finalize(tampered).Status);
    }

    [Fact]
    public void OptionsAndBudgetsAreDeterministicFailClosedInputs()
    {
        AllocatedSubject subject = AllocateSubject("root");
        HybridCpuManagedMetadataOptionsV1 forged = HybridCpuManagedMetadataOptionsV1.Qualification with
        {
            OptionsDigest = new('0', 64)
        };
        HybridCpuManagedMetadataOptionsV1 exhausted = HybridCpuManagedMetadataOptionsV1.Create(
            true, true, new(1, 1));
        HybridCpuManagedMetadataRequestV1 twoPoints = Request(subject, "root") with
        {
            Safepoints =
            [
                Request(subject, "root").Safepoints[0],
                Request(subject, "root").Safepoints[0] with { InstructionIdentity = subject.AfterIdentity }
            ]
        };

        Assert.Equal(HybridCpuManagedMetadataStatusV1.InvalidInput,
            Finalizer().Finalize(Request(subject, "root"), forged).Status);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.BudgetExhausted,
            Finalizer().Finalize(twoPoints, exhausted).Status);
    }

    [Fact]
    public void CorruptedGcMetadataIsRejectedByExistingManagedAbiConsumer()
    {
        HybridCpuManagedMetadataArtifactV1 result = Finalize(Request(AllocateSubject("root"), "root"));
        byte[] corrupted = result.GcInfo.ToArray();
        corrupted[0] ^= 0xff;

        Assert.Equal(HybridCpuManagedAbiCompatibilityV1.InvalidMetadata,
            new HybridCpuManagedAbiConsumerV1().ValidateGcInfoHeader(
                HybridCpuManagedAbiFamilyV1.Default.CreateEnvelope(), corrupted));
    }

    private static HybridCpuManagedMetadataFinalizerV1 Finalizer() => new();

    internal static HybridCpuManagedMetadataArtifactV1 Finalize(HybridCpuManagedMetadataRequestV1 request) =>
        Finalizer().Finalize(request, HybridCpuManagedMetadataOptionsV1.Qualification);

    internal static HybridCpuManagedMetadataRequestV1 Request(AllocatedSubject subject, params string[] references) => new(
        "Phase25A.ManagedMethod",
        0,
        subject.Result,
        [new(subject.CallIdentity, HybridCpuSafepointCategoryV1.CallSite,
            references.Select(static identity => new HybridCpuManagedLiveReferenceRequestV1(
                identity, HybridCpuGcReferenceKindV1.ObjectReference)).ToArray())]);

    internal static AllocatedSubject AllocateSubject(params string[] roots) =>
        AllocateSubjectCore(managedReferences: true, roots);

    internal static AllocatedSubject AllocateIndirectCallSubject(params string[] roots) =>
        AllocateSubjectCore(managedReferences: true, indirectCall: true, roots);

    private static AllocatedSubject AllocateSubjectCore(bool managedReferences, params string[] roots)
        => AllocateSubjectCore(managedReferences, indirectCall: false, roots);

    private static AllocatedSubject AllocateSubjectCore(bool managedReferences, bool indirectCall, params string[] roots)
    {
        List<InstructionSpec> specs = new List<InstructionSpec>();
        if (indirectCall) specs.Add(new(HybridCpuOpcode.ADDI, ["indirect-target"], []));
        foreach (string root in roots) specs.Add(new(HybridCpuOpcode.ADDI, [root], []));
        specs.Add(new(indirectCall ? HybridCpuOpcode.JALR : HybridCpuOpcode.ADDI, [],
            indirectCall ? ["indirect-target"] : [], IsCall: true));
        foreach (string root in roots) specs.Add(new(HybridCpuOpcode.ADDI, [], [root]));
        specs.Add(new(HybridCpuOpcode.JALR, [], []));
        HybridCpuInstructionWord[] words = specs.Select(static spec => Word(spec.IsCall ? HybridCpuOpcode.ADDI : spec.Opcode)).ToArray();
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, words);
        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        IrInstruction[] instructions = original.Instructions.Select((instruction, index) =>
        {
            InstructionSpec spec = specs[index];
            IrOperand[] defs = spec.Defs.Select(static identity => new IrOperand(
                IrOperandKind.VirtualValue, 0, identity)).ToArray();
            IrOperand[] uses = spec.Uses.Select(static identity => new IrOperand(
                IrOperandKind.VirtualValue, 0, identity)).ToArray();
            accesses.AddRange(spec.Defs.Select(identity => new IrValueAccessV1(identity, index, IrValueAccessKind.Def)));
            accesses.AddRange(spec.Uses.Select(identity => new IrValueAccessV1(identity, index, IrValueAccessKind.Use)));
            bool indirectCarrier = spec.IsCall && indirectCall;
            IrOperand[] retainedOperands = indirectCarrier
                ? instruction.Operands.Where(static operand => operand.Kind != IrOperandKind.ArchitecturalRegister).ToArray()
                : instruction.Operands.ToArray();
            IrOperand[] retainedDefs = indirectCarrier
                ? instruction.Annotation.Defs.Where(static operand => operand.Kind != IrOperandKind.ArchitecturalRegister).ToArray()
                : instruction.Annotation.Defs.ToArray();
            IrOperand[] retainedUses = indirectCarrier
                ? instruction.Annotation.Uses.Where(static operand => operand.Kind != IrOperandKind.ArchitecturalRegister).ToArray()
                : instruction.Annotation.Uses.ToArray();
            return instruction with
            {
                Opcode = indirectCarrier ? HybridCpuOpcode.JALR : instruction.Opcode,
                Operands = retainedOperands.Concat(defs).Concat(uses).ToArray(),
                Immediate = indirectCarrier ? (ushort)0 : instruction.Immediate,
                Annotation = instruction.Annotation with
                {
                    Defs = retainedDefs.Concat(defs).ToArray(),
                    Uses = retainedUses.Concat(uses).ToArray(),
                    ControlFlowKind = indirectCarrier ? IrControlFlowKind.Call : instruction.Annotation.ControlFlowKind,
                    BranchTargetSymbolName = indirectCarrier ? null : instruction.Annotation.BranchTargetSymbolName
                },
                SideEffects = spec.IsCall
                    ? instruction.SideEffects with
                    {
                        ArchitecturalEffects = indirectCarrier
                            ? (instruction.SideEffects.ArchitecturalEffects | IrArchitecturalEffectKind.Call) & ~IrArchitecturalEffectKind.Return
                            : instruction.SideEffects.ArchitecturalEffects | IrArchitecturalEffectKind.Call
                    }
                    : instruction.SideEffects
            };
        }).ToArray();
        Dictionary<int, IrInstruction> byIndex = instructions.ToDictionary(static item => item.Index);
        IrBasicBlock[] blocks = original.BasicBlocks.Select(block => block with
        {
            Instructions = block.Instructions.Select(item => byIndex[item.Index]).ToArray()
        }).ToArray();
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;
        int[] registers = HybridCpuNativeAbiContractV2.Default.AllocatableRegisters.ToArray();
        IrVirtualValueV1[] values = roots.Concat(indirectCall ? ["indirect-target"] : []).Order(StringComparer.Ordinal).Select(identity => new IrVirtualValueV1(
            identity,
            new(managedReferences && identity != "indirect-target" ? IrCanonicalValueKind.ManagedObjectReference : IrCanonicalValueKind.Integer, 64, IsSigned: false),
            managedReferences && identity != "indirect-target" ? IrVirtualValueClass.ManagedObjectReference : IrVirtualValueClass.ScalarInteger,
            new(64, 1, false, true, HybridCpuArchitecturalRegisterClass.ScalarInteger64, null,
                identity == "indirect-target" ? 5 : null, 0,
                managedReferences && identity != "indirect-target" ? ["managed-object-reference"] : ["native-scalar"], null, registers,
                registers.Select(register => target.ArchitecturalRegisters[register].RegisterGroup)
                    .Distinct().Order().ToArray(), target.ContractDigest, null))).ToArray();
        IrProgram program = original with
        {
            Instructions = instructions,
            ControlFlowGraph = new(blocks, original.ControlFlowGraph.Edges),
            ValueFlow = new("hybridcpu.value-flow/v1", 1, values, accesses)
        };
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 result = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles,
            resourceModel: HybridCpuMiiResourceModelV1.Create(new(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.True(result.Status == IrRegisterAllocationStatusV1.Allocated, $"{result.Status}: {result.Reason}");
        IrInstruction call = instructions.Single(item => item.SideEffects.ArchitecturalEffects
            .HasFlag(IrArchitecturalEffectKind.Call));
        IrInstruction after = instructions.First(item => roots.Any(root => item.Annotation.Uses
            .Any(operand => operand.Name == root)));
        return new(result, call.StableIdentity, after.StableIdentity);
    }

    private static HybridCpuInstructionWord Word(HybridCpuOpcode opcode) => new()
    {
        OpCode = (uint)opcode,
        DataTypeValue = HybridCpuDataType.INT64,
        PredicateMask = byte.MaxValue,
        VirtualThreadId = 0,
        Word1 = opcode == HybridCpuOpcode.JALR
            ? HybridCpuInstructionWord.PackArchRegs(0, 1, HybridCpuInstructionWord.NoArchReg)
            : HybridCpuInstructionWord.PackArchRegs(HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg)
    };

    private sealed record InstructionSpec(
        HybridCpuOpcode Opcode,
        IReadOnlyList<string> Defs,
        IReadOnlyList<string> Uses,
        bool IsCall = false);

    internal sealed record AllocatedSubject(
        IrRegisterAllocationResultV1 Result,
        string CallIdentity,
        string AfterIdentity);
}
