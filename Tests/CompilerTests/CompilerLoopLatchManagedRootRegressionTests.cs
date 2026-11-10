using System.Runtime.CompilerServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerLoopLatchManagedRootRegressionTests
{
    [Fact]
    public void LoopLocalCallResult_IsNotARootAfterItsLastUseAtLatchSafepoints()
    {
        Type fixture = typeof(LoopLatchManagedRootFixture);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            File.ReadAllBytes(fixture.Assembly.Location),
            "loop-latch-managed-root-regression",
            [new(fixture.FullName!, nameof(LoopLatchManagedRootFixture.Run))],
            []));
        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", graph.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}:{diagnostic.Message}")));

        ManagedCompiledMethodV1 method = graph.Methods.Single(compiled =>
            compiled.Identity.DeclaringType == fixture.FullName &&
            compiled.Identity.MethodName == nameof(LoopLatchManagedRootFixture.Run));
        IrProgram program = method.Import.Program!;
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule,
            new HybridCpuBundleFormer().BundleProgram(schedule),
            resourceModel: HybridCpuMiiResourceModelV1.Create(new(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.True(allocation.Status == IrRegisterAllocationStatusV1.Allocated,
            $"{allocation.Status}: {allocation.Reason}");

        string[] producedObjects = allocation.Witness!.SemanticValues
            .Where(static value =>
                value.VirtualClass == IrVirtualValueClass.ManagedObjectReference &&
                value.ValueKind.Kind == IrCanonicalValueKind.ManagedObjectReference &&
                value.ValueId.EndsWith(":value", StringComparison.Ordinal))
            .Select(static value => value.ValueId)
            .Where(valueId => program.Instructions.Any(instruction =>
                instruction.StableIdentity.EndsWith(":call-result-copy", StringComparison.Ordinal) &&
                instruction.Annotation.Defs.Any(operand =>
                    operand.Kind == IrOperandKind.VirtualValue && operand.Name == valueId)))
            .OrderBy(ExtractCilOffset)
            .ToArray();
        Assert.Equal(2, producedObjects.Length);
        string keepAlive = producedObjects[0];
        string loopLocal = producedObjects[1];

        Dictionary<string, int> lastUses = program.ValueFlow.Accesses
            .Where(static access => access.Kind != IrValueAccessKind.Def)
            .GroupBy(static access => access.ValueId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key,
                static group => group.Max(access => access.InstructionIndex), StringComparer.Ordinal);
        int localLastUse = lastUses[loopLocal];
        int keepAliveLastUse = lastUses[keepAlive];
        Assert.True(localLastUse < keepAliveLastUse);

        HybridCpuManagedMetadataArtifactV1 metadata = new HybridCpuManagedMetadataFinalizerV1()
            .FinalizeRequiredCallSites(method.Identity.StableIdentity, 0, allocation,
                HybridCpuManagedMetadataOptionsV1.Qualification);
        Assert.True(metadata.Status == HybridCpuManagedMetadataStatusV1.Finalized,
            $"{metadata.Status}: {metadata.Reason}");

        IrMaterializedBundle[] bundles = allocation.FinalBundles.BlockResults
            .OrderBy(static block => block.Block.StartInstructionIndex)
            .SelectMany(static block => block.Bundles.OrderBy(static bundle => bundle.Cycle))
            .ToArray();
        Dictionary<int, IrInstruction> safepointInstructions = metadata.Safepoints.ToDictionary(
            static point => point.CodeOffsetBytes,
            point => bundles[point.CodeOffsetBytes / HybridCpuManagedMetadataContractV1.EncodedBundleBytes]
                .Slots.Single(slot => slot.Instruction?.Annotation.IsManagedGcSafepoint == true).Instruction!);
        int OriginalIndex(HybridCpuSafepointRecordV1 point) => program.Instructions.Single(instruction =>
            instruction.StableIdentity == safepointInstructions[point.CodeOffsetBytes].StableIdentity).Index;
        HybridCpuSafepointRecordV1 latchSafepoint = metadata.Safepoints
            .Where(point =>
            {
                int index = OriginalIndex(point);
                return index > localLastUse && index < keepAliveLastUse;
            })
            .MaxBy(OriginalIndex)!;
        Assert.Contains(latchSafepoint.LiveReferences, root => root.ValueIdentity == keepAlive);
        Assert.DoesNotContain(latchSafepoint.LiveReferences, root => root.ValueIdentity == loopLocal);
        Assert.Contains(metadata.Safepoints, point => point.LiveReferences.Any(root =>
            root.ValueIdentity == loopLocal));
    }

    private static int ExtractCilOffset(string identity)
    {
        int marker = identity.LastIndexOf(":il_", StringComparison.Ordinal);
        Assert.True(marker >= 0, identity);
        int start = marker + 4;
        int end = identity.IndexOf(':', start);
        return Convert.ToInt32(end < 0 ? identity[start..] : identity[start..end], 16);
    }
}

public static class LoopLatchManagedRootFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static RootBox Produce(int value) => new(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Consume(RootBox value) => value.Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Latch(int value) => value + 1;

    public static int Run(int count)
    {
        RootBox keepAlive = Produce(7);
        int sum = 0;
        for (int index = 0; index < count; index++)
        {
            RootBox loopLocal = Produce(index);
            for (int inner = 0; inner <= index; inner++)
                sum += Consume(loopLocal);
            sum += Latch(index);
        }
        return sum + Consume(keepAlive);
    }

    public sealed class RootBox(int value)
    {
        public int Value { get; } = value;
    }
}
