using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.NativeAot;

internal static class NativeAotCompileCancellationSmoke
{
    public static void Run()
    {
        Assembly adapter = typeof(NativeAotSeamBaselineV1).Assembly;
        MethodInfo compile = adapter.GetType("HybridCPU.Compiler.NativeAot.Program", true)!
            .GetMethod("Compile", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo write = adapter.GetType("HybridCPU.Compiler.NativeAot.NativeAotSeamOutputV1", true)!
            .GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!;
        string directory = Path.Combine(Path.GetTempPath(), "nativeaot-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("NativeAotCancel"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("fixture").DefineType("Fixture", TypeAttributes.Public);
        MethodBuilder read = type.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static, typeof(int), []);
        ILGenerator il = read.GetILGenerator(); il.Emit(OpCodes.Ldc_I4_S, (sbyte)42); il.Emit(OpCodes.Ret);
        type.CreateType(); using MemoryStream stream = new MemoryStream(); assembly.Save(stream);
        string pe = Path.Combine(directory, "fixture.dll"); File.WriteAllBytes(pe, stream.ToArray());
        string[] Request(string command, string output) => [command, "--assembly", pe, "--type", "Fixture",
            "--method", "Read", "--out", output, "--source-commit", NativeAotSeamBaselineV1.SourceCommit,
            "--patch-digest", NativeAotSeamBaselineV1.PatchDigest];
        string? pcl = Environment.GetEnvironmentVariable("HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1");
        string? presentation = Environment.GetEnvironmentVariable("HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1");
        try
        {
            Environment.SetEnvironmentVariable("HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1", null);
            Environment.SetEnvironmentVariable("HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1", null);
            using CancellationTokenSource cancelled = new CancellationTokenSource(); cancelled.Cancel();
            string absent = Path.Combine(directory, "cancelled");
            Cancel(() => compile.Invoke(null, [cancelled.Token, Request("compile", absent)]), cancelled.Token);
            if (File.Exists(absent) || File.Exists(absent + ".json")) throw new Exception("Pre-cancel compile wrote outputs.");
            TextWriter oldError = Console.Error;
            using StringWriter errors = new StringWriter();
            try
            {
                Console.SetError(errors);
                if ((int)compile.Invoke(null, [cancelled.Token, new string[] { "compile" }])! != 2 ||
                    !errors.ToString().Contains("HCNAOT0001")) throw new Exception("Parse precedence drift.");
            }
            finally { Console.SetError(oldError); }
            foreach (string command in new[] { "compile", "compile-object", "compile-image" })
            {
                string oldPath = Path.Combine(directory, command + "-none"), newPath = Path.Combine(directory, command + "-active");
                using CancellationTokenSource active = new CancellationTokenSource();
                if ((int)compile.Invoke(null, [CancellationToken.None, Request(command, oldPath)])! != 0 ||
                    (int)compile.Invoke(null, [active.Token, Request(command, newPath)])! != 0 ||
                    !File.ReadAllBytes(oldPath).SequenceEqual(File.ReadAllBytes(newPath)) ||
                    !File.ReadAllBytes(oldPath + ".json").SequenceEqual(File.ReadAllBytes(newPath + ".json")))
                    throw new Exception("NativeAot active/None pair equality drift.");
            }
            string cutoff = Path.Combine(directory, "cutoff");
            File.WriteAllText(cutoff, "prior"); File.WriteAllText(cutoff + ".json", "prior-json");
            Cancel(() => write.Invoke(null, [cutoff, new byte[] { 42 }, new { Value = 42 }, null, cancelled.Token]), cancelled.Token);
            if (File.ReadAllText(cutoff) != "prior" || File.ReadAllText(cutoff + ".json") != "prior-json")
                throw new Exception("Pre-write cancellation mutated existing pair.");
            using CancellationTokenSource late = new CancellationTokenSource();
            if ((int)write.Invoke(null, [cutoff, new byte[] { 42 }, new LateManifest(late), null, late.Token])! != 0 ||
                !late.IsCancellationRequested || !File.ReadAllBytes(cutoff).SequenceEqual(new byte[] { 42 }) ||
                !File.ReadAllText(cutoff + ".json").Contains("42"))
                throw new Exception("Late cancellation interrupted synchronous write sequence.");
            Console.WriteLine("PASS NativeAot owned compile token: parse/pre-cancel, three None/active pairs, pre-write preservation and late-write cutoff");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1", pcl);
            Environment.SetEnvironmentVariable("HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1", presentation);
        }
    }
    private static void Cancel(Action action, CancellationToken token)
    {
        try { action(); throw new Exception("Cancelled seam returned success."); }
        catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException cancellation &&
            cancellation.CancellationToken == token) { }
    }
    private sealed class LateManifest(CancellationTokenSource cancellation)
    {
        public int Value { get { cancellation.Cancel(); return 42; } }
    }
}
