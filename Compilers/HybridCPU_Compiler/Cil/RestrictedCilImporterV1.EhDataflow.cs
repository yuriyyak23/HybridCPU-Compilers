using System.Reflection.Metadata;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // Uses the same typed instruction transfer as ordinary V2. Exceptional joins
    // discard the source evaluation stack and observe pre-instruction local state.
    // The resulting values are verification identities, not emitted phi copies.
    private RestrictedCilImportResultV1? VerifyEhDataflow(MetadataReader metadata,
        IReadOnlyList<DecodedInstruction> instructions, MethodSignature signature, LocalSignature locals,
        int maxStack, bool initLocals, ManagedEhMethodPlanV1 plan, RestrictedCilProvenanceV1 provenance,
        out ManagedEhTypedDataflowV1? typedDataflow)
    {
        typedDataflow = null;
        ManagedEhControlFlowV1 graph = plan.ControlFlow!;
        Dictionary<int, DecodedInstruction> byOffset = instructions.ToDictionary(row => row.Offset);
        ILookup<int, ManagedEhEdgeV1> outgoing = graph.Edges.ToLookup(edge => edge.SourceOffset);
        ILookup<int, ManagedEhEdgeV1> incomingEdges = graph.Edges.ToLookup(edge => edge.TargetOffset);
        Dictionary<ManagedEhEdgeV1, V2State> edgeStates = new Dictionary<ManagedEhEdgeV1, V2State>();
        Dictionary<int, V2State> states = new Dictionary<int, V2State>();
        states.Add(instructions[0].Offset, new([], Enumerable.Range(0, locals.Types.Count).Select(index =>
            initLocals && locals.Types[index] != RestrictedCilTypeV1.Aggregate
                ? V2Value.Constant(0, locals.Types[index], $"eh:init:{index}") : V2Value.Uninitialized),
            Enumerable.Range(0, signature.Parameters.Count).Select(index =>
                AnalysisArgument(signature, index, provenance.CanonicalMethodLocalIdentity))));
        V2State methodEntry = states[instructions[0].Offset].Clone();
        Dictionary<int, ResolvedHelper> helpers = new Dictionary<int, ResolvedHelper>();
        Dictionary<int, RestrictedCilFieldLayoutBindingV1> fields = new Dictionary<int, RestrictedCilFieldLayoutBindingV1>();
        Dictionary<int, V2Allocation> allocations = new Dictionary<int, V2Allocation>();
        Dictionary<int, RestrictedCilArrayTypeBindingV1> arrays = new Dictionary<int, RestrictedCilArrayTypeBindingV1>();
        Dictionary<int, RestrictedCilStringLiteralBindingV1> strings = new Dictionary<int, RestrictedCilStringLiteralBindingV1>();
        Dictionary<int, RestrictedCilValueTypeBindingV1> values = new Dictionary<int, RestrictedCilValueTypeBindingV1>();
        Dictionary<int, RestrictedCilTypeTestBindingV1> typeTests = new Dictionary<int, RestrictedCilTypeTestBindingV1>();
        Dictionary<int, RestrictedCilFunctionPointerBindingV1> functionPointers = new Dictionary<int, RestrictedCilFunctionPointerBindingV1>();
        Dictionary<int, RestrictedCilCalliBindingV1> callSites = new Dictionary<int, RestrictedCilCalliBindingV1>();
        Dictionary<int, RestrictedCilDelegateCreationBindingV1> delegateCreations = new Dictionary<int, RestrictedCilDelegateCreationBindingV1>();
        Dictionary<int, RestrictedCilDelegateInvokeBindingV1> delegateInvokes = new Dictionary<int, RestrictedCilDelegateInvokeBindingV1>();
        Dictionary<int, RestrictedCilFieldDataBindingV1> fieldData = new Dictionary<int, RestrictedCilFieldDataBindingV1>();
        Dictionary<int, ManagedReceiverCallerStoragePlanV1> receiverStorage = new Dictionary<int, ManagedReceiverCallerStoragePlanV1>();
        Dictionary<int, V2ReceiverCall> receiverCalls = new Dictionary<int, V2ReceiverCall>();
        SortedSet<int> pending = new SortedSet<int> { instructions[0].Offset };
        int calls = 0, steps = 0;
        long bound = (long)instructions.Count * (instructions.Count + locals.Types.Count + signature.Parameters.Count + maxStack + 2);
        while (pending.Count != 0)
        {
            if (++steps > bound)
                return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2805",
                    "EH typed-state fixpoint exceeded its deterministic bound.", provenance.MethodIdentity, provenance);
            int offset = pending.Min;
            pending.Remove(offset);
            DecodedInstruction instruction = byOffset[offset];
            V2State before = states[offset];
            V2State after = before.Clone();
            RestrictedCilImportResultV1? failure;
            if (instruction.Encoding is 0xdd or 0xde) after.Stack.Clear();
            else if (instruction.Encoding is 0xfe1a or 0xdc)
            {
                if (after.Stack.Count != 0)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0819",
                        "rethrow/endfinally require an empty evaluation stack.", OffsetIdentity(provenance, offset), provenance);
            }
            else if ((failure = TransferV2(metadata, instruction, signature, locals, after,
                helpers, fields, allocations, arrays, strings, values, typeTests, functionPointers, callSites,
                delegateCreations, delegateInvokes, fieldData, receiverStorage, receiverCalls,
                ref calls, provenance)) is not null) return failure;
            if (Math.Max(before.Stack.Count, after.Stack.Count) > maxStack)
                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0009",
                    "EH method understates its maximum evaluation stack.", OffsetIdentity(provenance, offset), provenance);
            foreach (ManagedEhEdgeV1? edge in outgoing[offset])
            {
                V2State incoming = edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate ? before.Clone() : after.Clone();
                if (edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate)
                {
                    incoming.Stack.Clear();
                    ManagedEhHandlerEntryV1 entry = plan.HandlerEntries.Single(row => row.ClauseOrdinal == edge.ClauseOrdinal);
                    if (entry.ExceptionReferenceRegister is not null)
                        incoming.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"eh:exception:{entry.IlOffset}"));
                }
                edgeStates[edge] = incoming;
                // Rebuild from current edge states; folding a new edge into an old
                // merged value would retain stale phi inputs after loop convergence.
                List<V2State> sources = incomingEdges[edge.TargetOffset].Where(edgeStates.ContainsKey)
                    .Select(source => edgeStates[source]).ToList();
                if (edge.TargetOffset == instructions[0].Offset) sources.Insert(0, methodEntry);
                V2State first = sources[0];
                if (sources.Any(source => source.Stack.Count != first.Stack.Count))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1103",
                        "EH evaluation-stack heights differ at a join.", OffsetIdentity(provenance, edge.TargetOffset), provenance);
                bool incompatible = false;
                V2Value Merge(Func<V2State, V2Value> select, string slot)
                {
                    V2Value[] incomingValues = sources.Select(select).ToArray();
                    if (incomingValues.Any(value => !value.Initialized)) return V2Value.Uninitialized;
                    V2Value left = incomingValues[0];
                    if (incomingValues.Any(right => left.Type != right.Type || left.AggregateIdentity != right.AggregateIdentity ||
                        left.ReceiverIdentity != right.ReceiverIdentity || left.ManagedFunctionPointerSignatureId != right.ManagedFunctionPointerSignatureId))
                        incompatible = true;
                    return incomingValues.All(value => value.Id == left.Id) ? left :
                        V2Value.Phi(left.Type, $"eh:join:{provenance.MethodIdentity}:{edge.TargetOffset}:{slot}") with
                    { AggregateIdentity = left.AggregateIdentity, ReceiverIdentity = left.ReceiverIdentity,
                        ManagedFunctionPointerSignatureId = left.ManagedFunctionPointerSignatureId };
                }
                V2State merged = new V2State(first.Stack.Select((_, index) => Merge(source => source.Stack[index], $"stack:{index}")),
                    first.Locals.Select((_, index) => Merge(source => source.Locals[index], $"local:{index}")),
                    first.Arguments.Select((_, index) => Merge(source => source.Arguments[index], $"arg:{index}")));
                if (incompatible)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1104",
                        "Exact CIL types differ at an EH state join.", OffsetIdentity(provenance, edge.TargetOffset), provenance);
                if (!states.TryGetValue(edge.TargetOffset, out V2State? previous) || !previous.Equivalent(merged))
                { states[edge.TargetOffset] = merged; pending.Add(edge.TargetOffset); }
            }
        }
        static ManagedEhTypedValueV1 Value(V2Value value) => new(value.Id, value.Type, value.Initialized,
            value.Initialized && value.Operand.Kind == IrOperandKind.Constant ? value.Operand.Value : null,
            value.AggregateIdentity, value.ReceiverIdentity, value.ManagedFunctionPointerSignatureId);
        static ManagedEhTypedStateV1 Snapshot(int offset, V2State state) => new(offset,
            state.Stack.Select(Value).ToArray(), state.Locals.Select(Value).ToArray(), state.Arguments.Select(Value).ToArray());
        ManagedEhTypedStateV1[] entries = states.OrderBy(row => row.Key).Select(row => Snapshot(row.Key, row.Value)).ToArray();
        ManagedEhTypedEdgeStateV1[] edges = graph.Edges.Where(edgeStates.ContainsKey)
            .Select(edge => new ManagedEhTypedEdgeStateV1(edge, Snapshot(edge.TargetOffset, edgeStates[edge]))).ToArray();
        typedDataflow = new(entries, edges, Hash("eh-typed-dataflow/v1|" + graph.Digest + "|" +
            System.Text.Json.JsonSerializer.Serialize(new { entries, edges })));
        return null;
    }
}
