using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR;

internal static class PostBundleCancellationSmoke
{
    public static void Run()
    {
        foreach (int size in new[] { 1, 16, 128 })
        {
            HybridCpuInstructionWord[] words = Enumerable.Repeat(new HybridCpuInstructionWord {
                OpCode = (uint)HybridCpuOpcode.ADDI, DataTypeValue = HybridCpuDataType.INT32,
                PredicateMask = byte.MaxValue, Immediate = 1,
                Word1 = HybridCpuInstructionWord.PackArchRegs(10, 10, HybridCpuInstructionWord.NoArchReg)
            }, size).ToArray();
            IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, words);
            IrProgramSchedule schedule = new HybridCpuProgramOrderLocalScheduler().ScheduleProgram(program);
            IrProgramBundlingResult layout = new HybridCpuBundleFormer().BundleProgram(schedule);
            string Projection(CancellationToken token)
            {
                (IReadOnlyList<HybridCpuInstructionBundle> LoweredBundles, IReadOnlyList<IrBundleAnnotations> LoweredBundleAnnotations, byte[] ProgramImage) result = HybridCpuPostBundleCompiler.Compile(token, layout, true, true);
                return Convert.ToHexString(SHA256.HashData(result.ProgramImage)) + JsonSerializer.Serialize(result.LoweredBundleAnnotations);
            }
            (IReadOnlyList<HybridCpuInstructionBundle> LoweredBundles, IReadOnlyList<IrBundleAnnotations> LoweredBundleAnnotations, byte[] ProgramImage) old = HybridCpuPostBundleCompiler.Compile(layout, true, true);
            string baseline = Convert.ToHexString(SHA256.HashData(old.ProgramImage)) + JsonSerializer.Serialize(old.LoweredBundleAnnotations);
            using CancellationTokenSource active = new CancellationTokenSource();
            if (baseline != Projection(CancellationToken.None) || baseline != Projection(active.Token))
                throw new Exception("Post-bundle None/active bytes or annotations drift.");
            using CancellationTokenSource pre = new CancellationTokenSource(); pre.Cancel();
            Cancel(() => HybridCpuPostBundleCompiler.Compile(pre.Token, layout, true, true), pre.Token);
            using CancellationTokenSource during = new CancellationTokenSource();
            IrBasicBlockBundlingResult block = layout.BlockResults[0];
            CancellingRows rows = new CancellingRows(block.Bundles, during);
            IrBasicBlockBundlingResult observedBlock = new IrBasicBlockBundlingResult(block.BlockSchedule, rows);
            IrProgramBundlingResult observed = new IrProgramBundlingResult(schedule, [observedBlock]);
            rows.Armed = true;
            bool completed = false;
            Cancel(() => HybridCpuPostBundleCompiler.Compile(during.Token, observed, true, true,
                (stage, _) => { if (stage is "Lowering" or "Serialization") completed = true; }), during.Token);
            if (completed || rows.Reads == 0) throw new Exception("Partial lowering reached later completion stages.");
            rows.Armed = false;
            if (!HybridCpuPostBundleCompiler.Compile(observed, true, true).ProgramImage.SequenceEqual(old.ProgramImage))
                throw new Exception("Cancelled lowering contaminated reusable input.");
            using CancellationTokenSource callback = new CancellationTokenSource();
            Cancel(() => HybridCpuIrProgramCompiler.Compile(callback.Token, program, _ => schedule,
                new HybridCpuBundleFormer(), (stage, _) => { if (stage == "LoweringStarting") callback.Cancel(); }), callback.Token);
            HybridCpuBundleLowerer lowerer = new HybridCpuBundleLowerer();
            string annotations = JsonSerializer.Serialize(old.LoweredBundleAnnotations);
            if (annotations != JsonSerializer.Serialize(lowerer.EmitAnnotationsForProgram(layout)) ||
                annotations != JsonSerializer.Serialize(lowerer.EmitAnnotationsForProgram(CancellationToken.None, layout)) ||
                annotations != JsonSerializer.Serialize(lowerer.EmitAnnotationsForProgram(active.Token, layout)))
                throw new Exception("Annotation token changed sidebands.");
            Cancel(() => lowerer.EmitAnnotationsForProgram(pre.Token, layout), pre.Token);
            using CancellationTokenSource annotation = new CancellationTokenSource();
            CancellingRows annotationRows = new CancellingRows(block.Bundles, annotation);
            IrProgramBundlingResult annotationLayout = new IrProgramBundlingResult(schedule,
                [new IrBasicBlockBundlingResult(block.BlockSchedule, annotationRows)]);
            annotationRows.Armed = true;
            Cancel(() => lowerer.EmitAnnotationsForProgram(annotation.Token, annotationLayout), annotation.Token);
            if (annotationRows.Reads != Math.Min(7, block.Bundles.Count))
                throw new Exception("Annotation emission read beyond cancellation.");
            annotationRows.Armed = false;
            if (annotations != JsonSerializer.Serialize(lowerer.EmitAnnotationsForProgram(annotationLayout)))
                throw new Exception("Cancelled annotation emission contaminated reuse.");
            using CancellationTokenSource forwarding = new CancellationTokenSource();
            CancellingRows secondPass = new CancellingRows(block.Bundles, forwarding, 2);
            IrProgramBundlingResult forwardedLayout = new IrProgramBundlingResult(schedule,
                [new IrBasicBlockBundlingResult(block.BlockSchedule, secondPass)]);
            secondPass.Armed = true; completed = false;
            Cancel(() => HybridCpuPostBundleCompiler.Compile(forwarding.Token, forwardedLayout, false, true,
                (stage, _) => { if (stage is "Lowering" or "Serialization") completed = true; }), forwarding.Token);
            if (completed || secondPass.Reads != Math.Min(7, block.Bundles.Count))
                throw new Exception("Shared completion did not cancel within annotation pass.");
            HybridCpuBundleBuilder builder = new HybridCpuBundleBuilder();
            string agreement = JsonSerializer.Serialize(builder.BuildAgreement(layout));
            if (agreement != JsonSerializer.Serialize(builder.BuildAgreement(CancellationToken.None, layout)) ||
                agreement != JsonSerializer.Serialize(builder.BuildAgreement(active.Token, layout)))
                throw new Exception("Agreement token changed classifications/facts/diagnostics.");
            Cancel(() => builder.BuildAgreement(pre.Token, layout), pre.Token);
            using CancellationTokenSource admission = new CancellationTokenSource();
            CancellingRows admissionRows = new CancellingRows(block.Bundles, admission);
            IrProgramBundlingResult admissionLayout = new IrProgramBundlingResult(schedule,
                [new IrBasicBlockBundlingResult(block.BlockSchedule, admissionRows)]);
            admissionRows.Armed = true;
            Cancel(() => builder.BuildAgreement(admission.Token, admissionLayout), admission.Token);
            if (admissionRows.Reads != Math.Min(7, block.Bundles.Count))
                throw new Exception("Agreement aggregation read beyond cancellation.");
            admissionRows.Armed = false;
            if (agreement != JsonSerializer.Serialize(builder.BuildAgreement(admissionLayout)))
                throw new Exception("Cancelled agreement contaminated builder/input reuse.");
            using CancellationTokenSource shared = new CancellationTokenSource();
            CancellingRows completionRows = new CancellingRows(block.Bundles, shared);
            IrBasicBlockBundlingResult completionBlock = new IrBasicBlockBundlingResult(block.BlockSchedule, completionRows);
            IrProgramBundlingResult completionLayout = new IrProgramBundlingResult(schedule, [completionBlock]);
            bool reachedLater = false;
            completionRows.Armed = true;
            MethodInfo complete = typeof(HybridCpuIrProgramCompiler).GetMethod("Complete", BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                complete.Invoke(null, [shared.Token, schedule, completionLayout,
                    new Action<string, string>((_, _) => reachedLater = true)]);
                throw new Exception("Partial agreement returned through shared completion.");
            }
            catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException cancelled &&
                cancelled.CancellationToken == shared.Token) { }
            if (reachedLater || completionRows.Reads != Math.Min(7, block.Bundles.Count))
                throw new Exception("Shared completion did not cancel within agreement aggregation.");
        }
        Console.WriteLine("PASS shared completion lower/annotations/agreement None/active, pre/mid/callback/pass cancellation and reusable inputs");
    }

    private static void Cancel(Action action, CancellationToken token)
    {
        try { action(); throw new Exception("Partial lowering success after cancellation."); }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { }
    }

    private sealed class CancellingRows(IReadOnlyList<IrMaterializedBundle> values, CancellationTokenSource source, int enumeration = 1)
        : IReadOnlyList<IrMaterializedBundle>
    {
        public bool Armed { get; set; }
        public int Reads { get; private set; }
        private int _enumerations;
        public int Count => values.Count;
        public IrMaterializedBundle this[int index] => values[index];
        public IEnumerator<IrMaterializedBundle> GetEnumerator()
        {
            int current = Armed ? ++_enumerations : 0;
            foreach (IrMaterializedBundle value in values)
            {
                if (Armed && current == enumeration && ++Reads == Math.Min(7, values.Count)) source.Cancel();
                yield return value;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
