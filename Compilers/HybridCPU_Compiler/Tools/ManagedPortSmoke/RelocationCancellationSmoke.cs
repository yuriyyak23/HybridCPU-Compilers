using System.Collections;
using HybridCPU.Compiler.Core.IR;

internal static class RelocationCancellationSmoke
{
    public static void Run(IrProgramBundlingResult layout, IReadOnlyList<HybridCpuInstructionBundle> lowered)
    {
        byte[] input = HybridCpuPostBundleCompiler.Serialize(lowered);
        byte[] Bytes(IReadOnlyList<HybridCpuInstructionBundle> value) => HybridCpuPostBundleCompiler.Serialize(value);
        byte[] baseline = Bytes(HybridCpuControlFlowRelocationResolver.ApplyRelocations(layout, lowered));
        using CancellationTokenSource active = new CancellationTokenSource();
        if (!baseline.SequenceEqual(Bytes(HybridCpuControlFlowRelocationResolver.ApplyRelocations(CancellationToken.None, layout, lowered))) ||
            !baseline.SequenceEqual(Bytes(HybridCpuControlFlowRelocationResolver.ApplyRelocations(active.Token, layout, lowered))))
            throw new Exception("Relocation token changed encoded targets.");
        using CancellationTokenSource pre = new CancellationTokenSource(); pre.Cancel();
        Cancel(() => HybridCpuControlFlowRelocationResolver.ApplyRelocations(pre.Token, layout, lowered), pre.Token);
        using CancellationTokenSource copy = new CancellationTokenSource();
        CopyCancellation copied = new CopyCancellation(lowered, copy);
        Cancel(() => HybridCpuControlFlowRelocationResolver.ApplyRelocations(copy.Token, layout, copied), copy.Token);
        if (copied.Reads != 3) throw new Exception("Relocation copy read beyond cancellation.");
        using CancellationTokenSource entries = new CancellationTokenSource();
        CancellingRows<IrBasicBlockBundlingResult> entryRows = new CancellingRows<IrBasicBlockBundlingResult>(layout.BlockResults, entries, 1, 2);
        IrProgramBundlingResult entryLayout = new IrProgramBundlingResult(layout.ProgramSchedule, entryRows);
        entryRows.Armed = true;
        Cancel(() => HybridCpuControlFlowRelocationResolver.ApplyRelocations(entries.Token, entryLayout, lowered), entries.Token);
        using CancellationTokenSource placement = new CancellationTokenSource();
        IrBasicBlockBundlingResult block = layout.BlockResults[0];
        CancellingRows<IrMaterializedBundle> bundleRows = new CancellingRows<IrMaterializedBundle>(block.Bundles, placement, 2, 1);
        IrBasicBlockBundlingResult observedBlock = new IrBasicBlockBundlingResult(block.BlockSchedule, bundleRows);
        IrProgramBundlingResult placementLayout = new IrProgramBundlingResult(layout.ProgramSchedule,
            layout.BlockResults.Select(value => value == block ? observedBlock : value).ToArray());
        bundleRows.Armed = true;
        Cancel(() => HybridCpuControlFlowRelocationResolver.ApplyRelocations(placement.Token, placementLayout, lowered), placement.Token);
        using CancellationTokenSource patch = new CancellationTokenSource();
        CancellingRows<IrInstruction> instructions = new CancellingRows<IrInstruction>(layout.Program.Instructions, patch, 1,
            Math.Max(2, layout.Program.Instructions.Count / 2));
        IrProgram program = layout.Program with { Instructions = instructions };
        IrProgramSchedule schedule = new IrProgramSchedule(program, layout.ProgramSchedule.DependencyGraph, layout.ProgramSchedule.BlockSchedules);
        IrProgramBundlingResult patchLayout = new IrProgramBundlingResult(schedule, layout.BlockResults);
        instructions.Armed = true;
        Cancel(() => HybridCpuControlFlowRelocationResolver.ApplyRelocations(patch.Token, patchLayout, lowered), patch.Token);
        if (!input.SequenceEqual(Bytes(lowered)) || !baseline.SequenceEqual(Bytes(
            HybridCpuControlFlowRelocationResolver.ApplyRelocations(layout, lowered))))
            throw new Exception("Cancelled relocation mutated input or reuse.");
        Console.WriteLine("PASS relocation None/active encoded targets, pre/copy/entry/placement/patch cancellation, input ownership and reuse");
    }

    private static void Cancel(Action action, CancellationToken token)
    {
        try { action(); throw new Exception("Partial relocated image returned after cancellation."); }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { }
    }

    private sealed class CopyCancellation(IReadOnlyList<HybridCpuInstructionBundle> values, CancellationTokenSource source)
        : IReadOnlyList<HybridCpuInstructionBundle>
    {
        public int Reads { get; private set; }
        public int Count => values.Count;
        public HybridCpuInstructionBundle this[int index] { get { if (++Reads == 3) source.Cancel(); return values[index]; } }
        public IEnumerator<HybridCpuInstructionBundle> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingRows<T>(IReadOnlyList<T> values, CancellationTokenSource source,
        int enumeration, int ordinal) : IReadOnlyList<T>
    {
        private int _enumerations;
        public bool Armed { get; set; }
        public int Count => values.Count;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator()
        {
            int current = Armed ? ++_enumerations : 0, index = 0;
            foreach (T value in values)
            {
                if (Armed && current == enumeration && ++index == ordinal) source.Cancel();
                yield return value;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
