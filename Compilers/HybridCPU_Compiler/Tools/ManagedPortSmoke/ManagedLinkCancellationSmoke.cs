using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Cil;

internal static class ManagedLinkCancellationSmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("ManagedLinkCancellation"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("fixture").DefineType("Fixture", TypeAttributes.Public);
        MethodBuilder helper = type.DefineMethod("Helper", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
        MethodBuilder root = type.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
        ILGenerator il = helper.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Neg); il.Emit(OpCodes.Ret);
        il = root.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, helper); il.Emit(OpCodes.Ret);
        type.CreateType(); using MemoryStream stream = new MemoryStream(); assembly.Save(stream);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, stream.ToArray(),
                "managed-link", [new("Fixture", "Read")], []));
        if (graph.Status != RestrictedCilImportStatusV1.Success) throw new Exception(string.Join(';', graph.Diagnostics));
        string entry = graph.Graph!.RootIdentities.Single();
        ScalarControlFlowV2ObjectLinkerV1 linker = new ScalarControlFlowV2ObjectLinkerV1();
        string Projection(ScalarControlFlowV2LinkedProgramV1 value) =>
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        ScalarControlFlowV2LinkedProgramV1 legacy = linker.Link(graph, entry);
        if (legacy.Status != ScalarControlFlowV2LinkStatusV1.Success) throw new Exception(string.Join(';', legacy.Diagnostics));
        string baseline = Projection(legacy);
        if (baseline != Projection(linker.Link(CancellationToken.None, graph, entry))) throw new Exception("Managed link None artifact drift.");
        using CancellationTokenSource active = new CancellationTokenSource();
        if (baseline != Projection(linker.Link(active.Token, graph, entry))) throw new Exception("Active token artifact drift.");
        using CancellationTokenSource cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Cancel(() => linker.Link(cancelled.Token, graph, entry), cancelled.Token);
        try { linker.Link(cancelled.Token, null!, entry); throw new Exception("Null graph admitted."); }
        catch (ArgumentNullException e) when (e.ParamName == "compilation") { }
        ScalarControlFlowV2LinkedProgramV1 oldInvalid = linker.Link(graph, "");
        ScalarControlFlowV2LinkedProgramV1 newInvalid = linker.Link(CancellationToken.None, graph, "");
        if (oldInvalid.Status != ScalarControlFlowV2LinkStatusV1.InvalidInput || Projection(oldInvalid) != Projection(newInvalid))
            throw new Exception("Managed link invalid-entry diagnostic drift.");
        using CancellationTokenSource mid = new CancellationTokenSource();
        CancellingOrder order = new CancellingOrder(graph.Graph.CompilationOrder, mid);
        ManagedCallGraphCompilationV1 wrapped = graph with { Graph = graph.Graph with { CompilationOrder = order } };
        Cancel(() => linker.Link(mid.Token, wrapped, entry), mid.Token);
        if (order.Reads != 2) throw new Exception("Second-method cancellation was not reached.");
        if (baseline != Projection(linker.Link(graph, entry))) throw new Exception("Managed linker reuse drift.");
        Console.WriteLine("PASS managed linker legacy/None/active full artifact equality, pre/second-method cancellation, null/diagnostic precedence and reuse");
    }
    private static void Cancel(Action action, CancellationToken token)
    {
        try { action(); throw new Exception("Cancelled managed link returned partial result."); }
        catch (OperationCanceledException e) when (e.CancellationToken == token) { }
    }
    private sealed class CancellingOrder(IReadOnlyList<string> values, CancellationTokenSource source) : IReadOnlyList<string>
    {
        public int Reads { get; private set; }
        public int Count => values.Count;
        public string this[int index] { get { Reads++; if (index == 1) source.Cancel(); return values[index]; } }
        public IEnumerator<string> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
