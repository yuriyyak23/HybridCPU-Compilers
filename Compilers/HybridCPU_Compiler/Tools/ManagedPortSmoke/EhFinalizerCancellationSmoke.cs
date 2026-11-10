using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;

internal static class EhFinalizerCancellationSmoke
{
    public static void Run(ManagedEhMethodPlanV1 plan, IrRegisterAllocationResultV1 allocation)
    {
        ManagedEhMetadataFinalizerV1 finalizer = new ManagedEhMetadataFinalizerV1();
        string Projection(ManagedEhFinalizationArtifactV1 value) =>
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        ManagedEhFinalizationArtifactV1 baseline = finalizer.Finalize(plan, allocation);
        if (baseline.Status != HybridCpuManagedMetadataStatusV1.Finalized)
            throw new Exception("EH cancellation fixture requires authentic final placement/frame metadata.");
        using CancellationTokenSource active = new CancellationTokenSource();
        if (Projection(baseline) != Projection(finalizer.Finalize(CancellationToken.None, plan, allocation)) ||
            Projection(baseline) != Projection(finalizer.Finalize(active.Token, plan, allocation)))
            throw new Exception("EH finalizer None/active artifact bytes or digest drift.");
        ManagedEhMethodPlanV1 malformed = plan with { ContractDigest = "stale" };
        if (Projection(finalizer.Finalize(malformed, allocation)) !=
            Projection(finalizer.Finalize(active.Token, malformed, allocation)))
            throw new Exception("EH malformed admission diagnostics changed with active token.");
        using CancellationTokenSource pre = new CancellationTokenSource(); pre.Cancel();
        Cancel(() => finalizer.Finalize(pre.Token, plan, allocation), pre.Token);
        using CancellationTokenSource during = new CancellationTokenSource();
        IrBasicBlockBundlingResult[] blocks = allocation.FinalBundles.BlockResults.ToArray();
        CancellingRows rows = new CancellingRows(blocks[0].Bundles, during);
        blocks[0] = new(blocks[0].BlockSchedule, rows);
        IrRegisterAllocationResultV1 observed = allocation with { FinalBundles = new(allocation.FinalSchedule, blocks) };
        rows.Armed = true;
        Cancel(() => finalizer.Finalize(during.Token, plan, observed), during.Token);
        // OrderBy buffers this block before the first per-bundle checkpoint.
        if (rows.Reads != rows.Count)
            throw new Exception("EH cancellation ran beyond the placement sorting pass.");
        rows.Armed = false;
        if (Projection(baseline) != Projection(finalizer.Finalize(plan, observed)))
            throw new Exception("EH cancellation contaminated reusable allocation.");
        Console.WriteLine($"PASS EH finalizer None/active artifact parity, malformed, pre/mid cancellation and reuse; reads={rows.Reads}");
    }

    private static void Cancel(Func<ManagedEhFinalizationArtifactV1> action, CancellationToken token)
    {
        try { _ = action(); throw new Exception("EH finalization returned an artifact after cancellation."); }
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
