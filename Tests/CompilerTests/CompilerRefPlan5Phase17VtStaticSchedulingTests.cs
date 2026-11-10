using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Scheduling.Vt;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase17VtStaticSchedulingTests
{
    [Fact]
    public void Contract_IsPinnedBoundedDefaultOffAndRuntimeAuthorityExplicit()
    {
        HybridCpuVtSchedulingContractV1 contract = HybridCpuVtSchedulingContractV1.Default;
        Assert.Equal("hybridcpu.vt-static-scheduling/v1", HybridCpuVtSchedulingContractV1.SchemaId);
        Assert.Equal("hybridcpu.cross-vt-relationship-proof/v1",
            HybridCpuVtSchedulingContractV1.RelationshipProofSchemaId);
        Assert.Equal("71424c03759953fead971b3a8cbfedc21d617ce46cd40014447791fa1cd9d6f0",
            contract.ContractDigest);
        Assert.Equal("11c1d870d772e2d248db25c39c9e73352d11c8f4714798d13b218c39d532c404",
            contract.ProductionOptionsDigest);
        Assert.Equal("bdb6293f2bfc95b7d12dd9683bbda6d3ca01b1bc0f98effd38516ecd61152db3",
            contract.QualificationOptionsDigest);
        Assert.Equal(IrVtStaticPlanningSwitchV1.Disabled,
            HybridCpuVtStaticPlanningOptionsV1.Production.PlanningSwitch);
        Assert.Equal(new HybridCpuVtStaticPlanningBudgetsV1(4, 256, 1024, 4096),
            HybridCpuVtStaticPlanningOptionsV1.Production.Budgets);
        Assert.Equal(IrVtRuntimeStageDispositionV1.RequiredAtRuntime,
            HybridCpuVtSchedulingContractV1.RuntimeBoundary.StageAClassAdmission);
        Assert.Equal(IrVtRuntimeStageDispositionV1.RequiredAtRuntime,
            HybridCpuVtSchedulingContractV1.RuntimeBoundary.StageBLaneMaterialization);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim,
            HybridCpuVtSchedulingContractV1.EvidenceOnlyHeader.ExecutionClaim);
        Assert.True(HybridCpuVtSchedulingContractV1.EvidenceOnlyHeader.RuntimeAuthorityDependency.HasFlag(
            CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));
    }

    [Fact]
    public void ProductionKillSwitch_ReturnsExactVtLocalFallbackAndPreservesNativeBytes()
    {
        IrVtLocalScheduleEvidenceV1[] streams = [Stream(0), Stream(1)];
        byte[] before = HybridCpuCanonicalCompiler.CompileProgram(
            0, Words(0, 1)).ProgramImage;
        IrVtStaticPlanV1 plan = new HybridCpuVtStaticPlannerV1().Plan(streams);
        byte[] after = HybridCpuCanonicalCompiler.CompileProgram(
            0, Words(0, 1)).ProgramImage;

        Assert.Equal(IrVtStaticPlanStatusV1.DisabledFallback, plan.Status);
        Assert.Empty(plan.JointCycles);
        Assert.Equal(plan.LocalCarrierCount, plan.PlannedStaticCarrierCount);
        Assert.Equal(0, plan.SavedStaticCarrierCount);
        Assert.Equal(streams.Select(static stream => stream.ScheduleDigest),
            plan.LocalFallbackSchedules.Select(static stream => stream.ScheduleDigest));
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OneToFourActiveVts_ProduceBoundedExactW8StaticEvidence(int activeVts)
    {
        IrVtLocalScheduleEvidenceV1[] streams = Enumerable.Range(0, activeVts)
            .Select(vt => Stream(checked((byte)vt))).ToArray();
        IrVtStaticPlanV1 plan = Plan(streams);

        Assert.True(plan.Status == IrVtStaticPlanStatusV1.Accepted,
            $"status={plan.Status}; counters={plan.Counters}; diagnostics={string.Join('|', plan.Diagnostics.Select(static diagnostic => diagnostic.Message))}");
        Assert.Equal(activeVts, plan.LocalCarrierCount);
        Assert.Single(plan.JointCycles);
        Assert.Equal(activeVts - 1, plan.SavedStaticCarrierCount);
        Assert.Equal(activeVts, plan.JointCycles[0].Members.Count);
        Assert.Equal(activeVts,
            plan.JointCycles[0].Members.Select(static member => member.StructuralSlot).Distinct().Count());
        Assert.Equal(IrVtSlotCompatibilityEvidenceV1.ExactW8StructuralPlacement,
            plan.JointCycles[0].SlotCompatibilityEvidence);
    }

    [Fact]
    public void PerVtRegisterGroupsRemainSeparateDuringStaticComposition()
    {
        IrVtLocalScheduleEvidenceV1[] streams =
        [
            Stream(0, instruction => WithRegisters(instruction, 1, 2)),
            Stream(1, instruction => WithRegisters(instruction, 1, 2))
        ];
        IrVtStaticPlanV1 plan = Plan(streams);

        Assert.Equal(IrVtStaticPlanStatusV1.Accepted, plan.Status);
        Assert.Single(plan.JointCycles);
        Assert.Equal([0, 1], plan.JointCycles[0].Members
            .Select(static member => (int)member.VirtualThreadId).Order());
    }

    [Fact]
    public void WholeLocalCycleGroupsAndPerVtOrderArePreserved()
    {
        IrVtLocalScheduleEvidenceV1[] streams =
        [
            Stream(0, instruction => WithSlot(instruction, IrIssueSlotMask.Slot0, IrSlotClass.AluClass), 2),
            Stream(1, instruction => WithSlot(instruction, IrIssueSlotMask.Slot1, IrSlotClass.AluClass), 2)
        ];
        IrVtStaticPlanV1 plan = Plan(streams);

        Assert.Equal(IrVtStaticPlanStatusV1.Accepted, plan.Status);
        Assert.Equal(4, plan.LocalCarrierCount);
        Assert.Equal(2, plan.PlannedStaticCarrierCount);
        foreach (byte virtualThreadId in new byte[] { 0, 1 })
            Assert.Equal([0, 1], plan.JointCycles.SelectMany(static cycle => cycle.Members)
                .Where(member => member.VirtualThreadId == virtualThreadId)
                .Select(static member => member.LocalCarrierOrdinal));
    }

    [Fact]
    public void SharedBankCollisionIsObservedButExactLocalSchedulesRemainUsable()
    {
        IrVtLocalScheduleEvidenceV1[] streams =
        [
            Stream(0, instruction => WithMemory(instruction, 0x1000, write: true)),
            Stream(1, instruction => WithMemory(instruction, 0x2000, write: true))
        ];
        HybridCpuTopologyResourceModelV1 topology = new HybridCpuTopologyResourceModelV1(
            new HybridCpuMachineTopologyV1(memoryBankCount: 1, memoryBankWidthBytes: 64));
        IrVtStaticPlanV1 plan = new HybridCpuVtStaticPlannerV1().Plan(
            streams, Proofs(streams), topology, HybridCpuVtStaticPlanningOptionsV1.Qualification);

        Assert.Equal(IrVtStaticPlanStatusV1.Accepted, plan.Status);
        Assert.Equal(2, plan.PlannedStaticCarrierCount);
        Assert.Equal(0, plan.SavedStaticCarrierCount);
        Assert.True(plan.Counters.SharedResourceRejects > 0);
        Assert.Equal(streams.Select(static stream => stream.ScheduleDigest),
            plan.LocalFallbackSchedules.Select(static stream => stream.ScheduleDigest));
    }

    [Fact]
    public void Lane6AndLane7CanShareOnlyTheirDistinctStructuralContours()
    {
        IrVtLocalScheduleEvidenceV1[] distinct =
        [
            Stream(0, instruction => WithSlot(instruction, IrIssueSlotMask.Slot6, IrSlotClass.DmaStreamClass)),
            Stream(1, instruction => WithSlot(instruction, IrIssueSlotMask.Slot7, IrSlotClass.BranchControl))
        ];
        IrVtStaticPlanV1 accepted = Plan(distinct);
        Assert.Equal(IrVtStaticPlanStatusV1.Accepted, accepted.Status);
        Assert.Single(accepted.JointCycles);
        Assert.Equal([6, 7], accepted.JointCycles[0].Members
            .Select(static member => member.StructuralSlot).Order());

        IrVtLocalScheduleEvidenceV1[] colliding =
        [
            Stream(0, instruction => WithSlot(instruction, IrIssueSlotMask.Slot6, IrSlotClass.DmaStreamClass)),
            Stream(1, instruction => WithSlot(instruction, IrIssueSlotMask.Slot6, IrSlotClass.DmaStreamClass))
        ];
        IrVtStaticPlanV1 separated = Plan(colliding);
        Assert.Equal(IrVtStaticPlanStatusV1.Accepted, separated.Status);
        Assert.Equal(2, separated.PlannedStaticCarrierCount);
        Assert.True(separated.Counters.StructuralRejects + separated.Counters.SharedResourceRejects > 0);
    }

    [Fact]
    public void UnsupportedSynchronizationAndAliasingMemoryFailClosed()
    {
        IrVtLocalScheduleEvidenceV1[] atomic =
        [
            Stream(0, instruction => WithAtomic(instruction, 0x1000)),
            Stream(1)
        ];
        Assert.Equal(IrVtStaticPlanStatusV1.Ineligible, Plan(atomic).Status);

        IrVtLocalScheduleEvidenceV1[] alias =
        [
            Stream(0, instruction => WithMemory(instruction, 0x1000, write: true)),
            Stream(1, instruction => WithMemory(instruction, 0x1000, write: false))
        ];
        IrVtStaticPlanV1 aliasPlan = Plan(alias);
        Assert.Equal(IrVtStaticPlanStatusV1.Ineligible, aliasPlan.Status);
        Assert.Empty(aliasPlan.JointCycles);
        Assert.Equal(aliasPlan.LocalCarrierCount, aliasPlan.PlannedStaticCarrierCount);
    }

    [Fact]
    public void MissingStaleOrDuplicateVtProofsCannotCreateCrossVtEvidence()
    {
        IrVtLocalScheduleEvidenceV1[] streams = [Stream(0), Stream(1)];
        HybridCpuVtStaticPlannerV1 planner = new HybridCpuVtStaticPlannerV1();
        Assert.Equal(IrVtStaticPlanStatusV1.StaleProof,
            planner.Plan(
                streams,
                options: HybridCpuVtStaticPlanningOptionsV1.Qualification).Status);

        IrCrossVtRelationshipProofV1 stale = Proofs(streams)[0] with
        {
            LeftScheduleDigest = new string('0', 64)
        };
        Assert.Equal(IrVtStaticPlanStatusV1.StaleProof,
            planner.Plan(
                streams,
                [stale],
                options: HybridCpuVtStaticPlanningOptionsV1.Qualification).Status);

        IrVtLocalScheduleEvidenceV1 duplicate = Stream(0);
        Assert.Equal(IrVtStaticPlanStatusV1.InvalidModel,
            planner.Plan(
                [streams[0], duplicate],
                options: HybridCpuVtStaticPlanningOptionsV1.Qualification).Status);

        IrVtLocalScheduleEvidenceV1 staleLocal = streams[0] with
        {
            ProgramDigest = new string('f', 64)
        };
        Assert.Equal(IrVtStaticPlanStatusV1.StaleProof,
            planner.Plan(
                [staleLocal],
                options: HybridCpuVtStaticPlanningOptionsV1.Qualification).Status);
    }

    [Fact]
    public void UnknownRegisterEvidenceFailsClosedWithoutPartialPlan()
    {
        IrVtLocalScheduleEvidenceV1 stream = Stream(0, instruction => instruction with
        {
            Annotation = instruction.Annotation with
            {
                Uses = [new(IrOperandKind.Tile, 0, "unknown-tile-register-domain")]
            }
        });
        IrVtStaticPlanV1 plan = Plan([stream]);

        Assert.Equal(IrVtStaticPlanStatusV1.UnknownFallback, plan.Status);
        Assert.Empty(plan.JointCycles);
        Assert.Equal(plan.LocalCarrierCount, plan.PlannedStaticCarrierCount);
    }

    [Fact]
    public void DeterministicBudgetExhaustionKeepsWholeLocalFallback()
    {
        IrVtLocalScheduleEvidenceV1[] streams = [Stream(0), Stream(1), Stream(2), Stream(3)];
        HybridCpuVtStaticPlanningOptionsV1 tiny = HybridCpuVtStaticPlanningOptionsV1.Create(
            IrVtStaticPlanningSwitchV1.ExplicitQualificationOnly,
            HybridCpuVtStaticPlanningBudgetsV1.Production with { MaximumSubsetEvaluations = 1 });
        IrVtStaticPlanV1 plan = new HybridCpuVtStaticPlannerV1().Plan(
            streams, Proofs(streams), options: tiny);

        Assert.Equal(IrVtStaticPlanStatusV1.BudgetExhausted, plan.Status);
        Assert.Empty(plan.JointCycles);
        Assert.Equal(plan.LocalCarrierCount, plan.PlannedStaticCarrierCount);
        Assert.Equal(0, plan.SavedStaticCarrierCount);
    }

    [Fact]
    public void InputAndCultureOrderDoNotChangeStaticPlan()
    {
        IrVtLocalScheduleEvidenceV1[] streams = [Stream(0), Stream(1), Stream(2), Stream(3)];
        IrCrossVtRelationshipProofV1[] proofs = Proofs(streams);
        IrVtStaticPlanV1 first = Plan(streams);
        CultureInfo original = CultureInfo.CurrentCulture;
        IrVtStaticPlanV1 second;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            second = new HybridCpuVtStaticPlannerV1().Plan(
                streams.Reverse().ToArray(),
                proofs.Reverse().ToArray(),
                options: HybridCpuVtStaticPlanningOptionsV1.Qualification);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(first.PlanDigest, second.PlanDigest);
        Assert.Equal(first.LocalFallbackDigest, second.LocalFallbackDigest);
        Assert.Equal(first.Counters, second.Counters);
    }

    [Fact]
    public void StaticEvidenceCannotSerializeRuntimeFreshnessOrBypassStageB()
    {
        IrVtStaticPlanV1 plan = Plan([Stream(0), Stream(1)]);
        string json = JsonSerializer.Serialize(plan);
        string[] forbidden = ["epoch", "generation", "ownerToken", "laneLease", "LegalityDecision"];
        Assert.DoesNotContain(forbidden, token => json.Contains(token, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(IrVtRuntimeStageDispositionV1.RequiredAtRuntime,
            plan.RuntimeAuthorityBoundary.StageAClassAdmission);
        Assert.Equal(IrVtRuntimeStageDispositionV1.RequiredAtRuntime,
            plan.RuntimeAuthorityBoundary.StageBLaneMaterialization);
        Assert.True(plan.AuthorityHeader.RuntimeAuthorityDependency.HasFlag(
            CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(plan.AuthorityHeader.RuntimeAuthorityDependency.HasFlag(
            CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
    }

    [Fact]
    public void PlannerStaysCoreOnlyAndHasNoProductionCaller()
    {
        Assembly core = typeof(HybridCpuVtStaticPlannerV1).Assembly;
        Assert.Equal("HybridCPU.Compiler.Core", core.GetName().Name);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("LLVM", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("HybridCPU_ISE", StringComparison.Ordinal));

        string root = FindRepositoryRoot();
        string[] production = Directory.EnumerateFiles(
                Path.Combine(root, "Compilers", "HybridCPU_Compiler"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("HybridCpuVtStaticPlannerV1.cs", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("HybridCpuVtSchedulingContractsV1.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.DoesNotContain(production, path => File.ReadAllText(path)
            .Contains("HybridCpuVtStaticPlannerV1", StringComparison.Ordinal));
    }

    private static IrVtStaticPlanV1 Plan(IrVtLocalScheduleEvidenceV1[] streams) =>
        new HybridCpuVtStaticPlannerV1().Plan(
            streams,
            Proofs(streams),
            options: HybridCpuVtStaticPlanningOptionsV1.Qualification);

    private static IrCrossVtRelationshipProofV1[] Proofs(IrVtLocalScheduleEvidenceV1[] streams)
    {
        List<IrCrossVtRelationshipProofV1> proofs = new List<IrCrossVtRelationshipProofV1>();
        for (int left = 0; left < streams.Length; left++)
            for (int right = left + 1; right < streams.Length; right++)
                proofs.Add(IrCrossVtRelationshipProofV1.CreateIndependent(streams[left], streams[right]));
        return proofs.ToArray();
    }

    private static IrVtLocalScheduleEvidenceV1 Stream(
        byte virtualThreadId,
        Func<IrInstruction, IrInstruction>? transform = null,
        int instructionCount = 1)
    {
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(
            virtualThreadId, Words(virtualThreadId, instructionCount));
        program = Transform(program, instruction =>
        {
            IrInstruction normalized = instruction with
            {
                Operands = Array.Empty<IrOperand>(),
                Annotation = instruction.Annotation with
                {
                    Uses = Array.Empty<IrOperand>(),
                    Defs = Array.Empty<IrOperand>()
                }
            };
            return transform is null ? normalized : transform(normalized);
        });
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        return IrVtLocalScheduleEvidenceV1.Create(virtualThreadId, schedule);
    }

    private static HybridCpuInstructionWord[] Words(byte virtualThreadId, int count) =>
        Enumerable.Range(0, count).Select(_ => new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.Nope,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            VirtualThreadId = virtualThreadId,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg)
        }).ToArray();

    private static IrProgram Transform(IrProgram program, Func<IrInstruction, IrInstruction> transform)
    {
        Dictionary<int, IrInstruction> replacements = program.Instructions
            .ToDictionary(static instruction => instruction.Index, transform);
        return program with
        {
            Instructions = program.Instructions.Select(instruction => replacements[instruction.Index]).ToArray(),
            ControlFlowGraph = program.ControlFlowGraph with
            {
                Blocks = program.BasicBlocks.Select(block => block with
                {
                    Instructions = block.Instructions.Select(instruction => replacements[instruction.Index]).ToArray()
                }).ToArray()
            }
        };
    }

    private static IrInstruction WithRegisters(IrInstruction instruction, ulong use, ulong definition) =>
        instruction with
        {
            Operands =
            [
                new(IrOperandKind.Pointer, definition, $"x{definition}"),
                new(IrOperandKind.Pointer, use, $"x{use}")
            ],
            Annotation = instruction.Annotation with
            {
                Uses = [new(IrOperandKind.Pointer, use, $"x{use}")],
                Defs = [new(IrOperandKind.Pointer, definition, $"x{definition}")]
            }
        };

#pragma warning disable CS0618
    private static IrInstruction WithSlot(
        IrInstruction instruction,
        IrIssueSlotMask slots,
        IrSlotClass slotClass) => instruction with
        {
            Annotation = instruction.Annotation with
            {
                LegalSlots = slots,
                RequiredSlotClass = slotClass
            }
        };
#pragma warning restore CS0618

    private static IrInstruction WithMemory(IrInstruction instruction, ulong address, bool write)
    {
        IrMemoryRegion region = new IrMemoryRegion(address, 8, write);
        IrMemoryEffectKind kind = write ? IrMemoryEffectKind.Write : IrMemoryEffectKind.Read;
        return instruction with
        {
            Annotation = instruction.Annotation with
            {
                MemoryReadRegion = write ? null : region,
                MemoryWriteRegion = write ? region : null
            },
            SideEffects = new(
                new(kind, IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic,
                    write ? null : region, write ? region : null),
                IrArchitecturalEffectKind.None)
        };
    }

    private static IrInstruction WithAtomic(IrInstruction instruction, ulong address)
    {
        IrMemoryRegion region = new(address, 8, true);
        return instruction with
        {
            Annotation = instruction.Annotation with { MemoryWriteRegion = region },
            SideEffects = new(
                new(IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Write,
                    IrAddressSpaceIdentity.Generic,
                    IrMemoryOrdering.SequentiallyConsistent,
                    null,
                    region),
                IrArchitecturalEffectKind.None)
        };
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Compilers", "HybridCPU_Compiler")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
