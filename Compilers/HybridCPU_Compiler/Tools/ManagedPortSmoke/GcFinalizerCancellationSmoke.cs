using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;

internal static class GcFinalizerCancellationSmoke
{
    public static void Run(string identity, IrRegisterAllocationResultV1 allocation,
        IReadOnlyList<HybridCpuManagedFixedRootRequestV1> fixedRoots)
    {
        HybridCpuManagedMetadataFinalizerV1 finalizer = new HybridCpuManagedMetadataFinalizerV1();
        HybridCpuManagedMetadataOptionsV1 options = HybridCpuManagedMetadataOptionsV1.Qualification;
        string Projection(HybridCpuManagedMetadataArtifactV1 value) =>
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        HybridCpuManagedMetadataArtifactV1 baseline = finalizer.FinalizeRequiredCallSites(identity, 0, allocation, options, fixedRoots);
        if (baseline.Status != HybridCpuManagedMetadataStatusV1.Finalized)
            throw new Exception("GC cancellation fixture requires authentic placement/frame/root metadata.");
        using CancellationTokenSource active = new CancellationTokenSource();
        if (Projection(baseline) != Projection(finalizer.FinalizeRequiredCallSites(CancellationToken.None, identity, 0, allocation, options, fixedRoots)) ||
            Projection(baseline) != Projection(finalizer.FinalizeRequiredCallSites(active.Token, identity, 0, allocation, options, fixedRoots)))
            throw new Exception("GC call-site None/active artifact drift.");
        Dictionary<string, int> offsets = allocation.FinalBundles.BlockResults.OrderBy(block => block.Block.StartInstructionIndex)
            .SelectMany(block => block.Bundles.OrderBy(bundle => bundle.Cycle))
            .SelectMany((bundle, index) => bundle.Slots.Where(slot => slot.Instruction is not null)
                .Select(slot => (Identity: slot.Instruction!.StableIdentity,
                    Offset: index * HybridCpuManagedMetadataContractV1.EncodedBundleBytes)))
            .ToDictionary(row => row.Identity, row => row.Offset);
        HashSet<string> rootIdentities = fixedRoots.Select(root => root.RootIdentity).ToHashSet(StringComparer.Ordinal);
        HybridCpuManagedSafepointRequestV1[] points = allocation.OriginalSchedule.Program.Instructions
            .Where(instruction => instruction.Annotation.IsManagedGcSafepoint &&
                (instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call ||
                 instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call)))
            .Select(instruction => new HybridCpuManagedSafepointRequestV1(
                instruction.StableIdentity, HybridCpuSafepointCategoryV1.CallSite,
                baseline.Safepoints.Single(point => point.CodeOffsetBytes == offsets[instruction.StableIdentity])
                    .LiveReferences.Where(reference => !rootIdentities.Contains(reference.ValueIdentity))
                    .Select(reference => new HybridCpuManagedLiveReferenceRequestV1(
                        reference.ValueIdentity, reference.ReferenceKind)).ToArray())).ToArray();
        HybridCpuManagedMetadataRequestV1 request = new HybridCpuManagedMetadataRequestV1(identity, 0, allocation, points, fixedRoots);
        if (Projection(baseline) != Projection(finalizer.Finalize(request, options)) ||
            Projection(baseline) != Projection(finalizer.Finalize(active.Token, request, options)))
            throw new Exception("GC direct finalizer artifact drift.");
        foreach (HybridCpuManagedMetadataOptionsV1? control in new[] { options, HybridCpuManagedMetadataOptionsV1.Production,
            HybridCpuManagedMetadataOptionsV1.Create(true, true, new(0, 0)) })
            if (Projection(finalizer.Finalize(request, control)) !=
                Projection(finalizer.Finalize(active.Token, request, control)))
                throw new Exception("GC active token changed disabled/budget admission.");
        using CancellationTokenSource pre = new CancellationTokenSource(); pre.Cancel();
        Cancel(() => finalizer.FinalizeRequiredCallSites(pre.Token, identity, 0, allocation, options, fixedRoots), pre.Token);
        Cancel(() => finalizer.Finalize(pre.Token, request, options), pre.Token);
        using CancellationTokenSource during = new CancellationTokenSource();
        IrBasicBlockBundlingResult[] blocks = allocation.FinalBundles.BlockResults.ToArray();
        CancellingRows rows = new CancellingRows(blocks[0].Bundles, during);
        blocks[0] = new(blocks[0].BlockSchedule, rows);
        IrRegisterAllocationResultV1 observed = allocation with { FinalBundles = new(allocation.FinalSchedule, blocks) };
        rows.Armed = true;
        Cancel(() => finalizer.FinalizeRequiredCallSites(during.Token, identity, 0, observed, options, fixedRoots), during.Token);
        if (rows.Reads != rows.Count) throw new Exception("GC placement cancellation escaped its sorted block pass.");
        rows.Armed = false;
        if (Projection(baseline) != Projection(finalizer.FinalizeRequiredCallSites(identity, 0, observed, options, fixedRoots)))
            throw new Exception("GC cancellation contaminated reusable allocation.");
        Console.WriteLine($"PASS GC finalizer None/active/direct, disabled/budget, pre/mid cancellation and reuse; reads={rows.Reads}");
    }

    private static void Cancel(Func<HybridCpuManagedMetadataArtifactV1> action, CancellationToken token)
    {
        try { _ = action(); throw new Exception("GC metadata artifact returned after cancellation."); }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { }
    }

    private sealed class CancellingRows(IReadOnlyList<IrMaterializedBundle> values, CancellationTokenSource source)
        : IReadOnlyList<IrMaterializedBundle>
    {
        public bool Armed { get; set; }
        public int Reads { get; private set; }
        public int Count => values.Count;
        public IrMaterializedBundle this[int index] => values[index];
        public IEnumerator<IrMaterializedBundle> GetEnumerator()
        {
            foreach (IrMaterializedBundle value in values)
            {
                if (Armed && ++Reads == 1) source.Cancel();
                yield return value;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
