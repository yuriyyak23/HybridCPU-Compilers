using System.Collections;
using HybridCPU.Compiler.Core.IR;

internal static class SerializerCancellationSmoke
{
    public static void Run()
    {
        HybridCpuBundleSerializer serializer = new HybridCpuBundleSerializer();
        foreach (int size in new[] { 0, 1, 16, 128 })
        {
            HybridCpuInstructionBundle[] bundles = Enumerable.Range(0, size).Select(index => {
                HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
                bundle.SetInstruction(0, new HybridCpuInstructionWord {
                    OpCode = (uint)HybridCpuOpcode.ADDI, DataTypeValue = HybridCpuDataType.INT32,
                    PredicateMask = byte.MaxValue, Immediate = (ushort)index,
                    Word1 = HybridCpuInstructionWord.PackArchRegs(10, 10, HybridCpuInstructionWord.NoArchReg)
                }); return bundle;
            }).ToArray();
            byte[] baseline = serializer.SerializeProgram(bundles);
            using CancellationTokenSource active = new CancellationTokenSource();
            void Equal(byte[] image)
            {
                if (image.Length != size * HybridCpuBundleSerializer.BundleSizeBytes || !baseline.SequenceEqual(image))
                    throw new Exception("Serializer size/bytes drift.");
            }
            Equal(serializer.SerializeProgram(CancellationToken.None, bundles));
            Equal(serializer.SerializeProgram(active.Token, bundles));
            Equal(HybridCpuPostBundleCompiler.Serialize(active.Token, bundles));
            using CancellationTokenSource pre = new CancellationTokenSource(); pre.Cancel();
            CancellingBundles unread = new CancellingBundles(bundles, pre, 1);
            Cancel(() => serializer.SerializeProgram(pre.Token, unread), pre.Token);
            if (unread.Reads != 0) throw new Exception("Pre-cancel serialization read a bundle.");
            if (size == 0) continue;
            using CancellationTokenSource during = new CancellationTokenSource();
            int ordinal = Math.Min(7, size);
            CancellingBundles observed = new CancellingBundles(bundles, during, ordinal);
            Cancel(() => HybridCpuPostBundleCompiler.Serialize(during.Token, observed), during.Token);
            if (observed.Reads != ordinal) throw new Exception("Cancelled serialization read beyond its checkpoint.");
            Equal(serializer.SerializeProgram(bundles));
        }
        foreach (bool empty in new[] { false, true })
        {
            using CancellationTokenSource count = new CancellationTokenSource();
            CountCancellation rows = new CountCancellation(count, empty);
            Cancel(() => serializer.SerializeProgram(count.Token, rows), count.Token);
        }
        Console.WriteLine("PASS serializer None/active/empty size+bytes, pre/count/mid/final-read cancellation, shared token forwarding and reuse");
    }

    private static void Cancel(Action action, CancellationToken token)
    {
        try { action(); throw new Exception("Partial image returned after cancellation."); }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { }
    }

    private sealed class CancellingBundles(IReadOnlyList<HybridCpuInstructionBundle> values,
        CancellationTokenSource source, int ordinal) : IReadOnlyList<HybridCpuInstructionBundle>
    {
        public int Reads { get; private set; }
        public int Count => values.Count;
        public HybridCpuInstructionBundle this[int index]
        {
            get { if (++Reads == ordinal) source.Cancel(); return values[index]; }
        }
        public IEnumerator<HybridCpuInstructionBundle> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountCancellation(CancellationTokenSource source, bool empty)
        : IReadOnlyList<HybridCpuInstructionBundle>
    {
        private int _reads;
        public int Count { get { if (++_reads == (empty ? 1 : 2)) source.Cancel(); return empty ? 0 : 1; } }
        public HybridCpuInstructionBundle this[int index] => throw new Exception("Count cancellation reached a write.");
        public IEnumerator<HybridCpuInstructionBundle> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
