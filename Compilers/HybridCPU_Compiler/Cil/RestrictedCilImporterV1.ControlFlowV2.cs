using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private const string ControlFlowV2ContractText =
        "hybridcpu.cil-cfg-ssa/v1|decode-offsets|blocks|verified-cfg|typed-fixpoint|ssa-phi|dominance|reducible-loops|critical-edge-split|parallel-copy|ordinary-loop-fallback|exact-aggregate-analysis-only|aggregate-pre-ir-gate|pop|static-scalar-getter-projection-cctor-preserved|exact-static-single-field-i4-value-projection|exact-single-field-i4-signature-and-field-projection|unsigned-division-width-nonzero-proof|signed-division-width-zero-overflow-proof|signed-unsigned-remainder-zero-overflow-proof|conv-i1-i2-truncate-sign-extend|conv-i8-source-aware-extension|conv-u1-u2-truncate-zero-extend|integer-bitwise-and-or-xor|integer-negation-wrap|typed-leaf-receiver-loan|exact-native-constant-materialization|nested-aggregate-identities|reference-free-instance-field-analysis|shift-word-wide-signedness-count-mask|primitive-array-u4-carrier|primitive-array-i2-u2-carriers|fused-value-array-ldelema-initobj|bounded-switch-lowering|complete-signed-unsigned-relational-branches|mutable-argument-ssa|eight-register-argument-abi|generation-26";

private static readonly string ControlFlowV2ContractDigest = Hash(ControlFlowV2ContractText + "|nullable-int32-selected-external-i4-helper-default/v137-exact-signed-struct-scalar-default-zero|" +
"|narrow-helper-return-stack-i4|typed-throw-helper-lowering-native-eh-image-gate|implicit-array-allocation-access-native-eh-image-gate|exact-factory-and-copy-stores-definitely-initialized-local-pure-getter-i4-projection/v4|immutable-exact-parameter-pure-getter-i4/v1|owned-external-scalar-definition-getter/v1|budgeted-external-parameter-getter-graph-admission/v1|owned-external-receiver-local/v1|source-owned-exact-i8-factory-local-getter/v1|immutable-source-owned-exact-i8-parameter-getter/v1|exact-i8-newobj-local-copy-pure-getter/v1|typed-inplace-i8-scalar-arguments/v62-primary-private-nullable-int32-parameter-helper-default|erased-receiver-stack-accounting/v1|integer-complement-xori/v1");

    private RestrictedCilImportResultV1 ImportControlFlowV2(
        MetadataReader metadata,
        IReadOnlyList<DecodedInstruction> instructions,
        MethodSignature signature,
        LocalSignature locals,
        int declaredMaxStack,
        RestrictedCilProvenanceV1 provenance,
        ManagedEhMethodPlanV1? ehPlan = null,
        ManagedEhTypedDataflowV1? ehTypedDataflow = null)
    {
        RestrictedCilImportResultV1? receiverFailure = ValidateReceiverBody(signature, instructions, provenance);
        if (receiverFailure is not null) return receiverFailure;
        if ((ehPlan is null) != (ehTypedDataflow is null))
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL2810",
                "EH mapping requires both the PE-bound plan and its verified typed states.", provenance.MethodIdentity, provenance);
        HashSet<int> continuationTargets = ehPlan?.FinallyContinuations.Continuations.Select(static row => row.TargetOffset).ToHashSet() ?? [];
        int[] handlerRoots = ehPlan?.HandlerEntries.Select(static entry => entry.IlOffset).Distinct().Order().ToArray() ?? [];
        V2GraphBuild graphBuild = BuildV2Graph(instructions, provenance, handlerRoots.Concat(continuationTargets).ToArray());
        if (graphBuild.Failure is not null) return graphBuild.Failure;
        V2Graph graph = graphBuild.Graph!;
        IReadOnlyDictionary<int, ManagedReceiverCallerStoragePlanV1>? receiverStorage =
            ReceiverLocalStoragePlans(metadata, instructions, locals);
        if (receiverStorage is null)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844",
                "Caller-owned receiver storage requires exactly one reference-free <=16-byte local used only through ldloca.s.",
                provenance.MethodIdentity, provenance);
        if (receiverStorage.Count != 0 && ehPlan is not null)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844",
                "Caller-owned receiver storage is bounded to a non-EH lifetime; handler or unwind crossing is not qualified.",
                provenance.MethodIdentity, provenance);
        IReadOnlyDictionary<int, V2State>? seededEntries = ehTypedDataflow is null ? null :
            ehTypedDataflow.Entries.Where(entry => entry.IlOffset == instructions[0].Offset || handlerRoots.Contains(entry.IlOffset) || continuationTargets.Contains(entry.IlOffset))
                .ToDictionary(static entry => entry.IlOffset, entry => ConvertEhState(entry,
                    entry.IlOffset == instructions[0].Offset ? null : ehPlan));
        V2DataflowBuild dataflowBuild = RunV2Dataflow(metadata, graph, signature, locals, declaredMaxStack,
            provenance, receiverStorage, seededEntries, continuationTargets);
        if (dataflowBuild.Failure is not null) return dataflowBuild.Failure;
        V2Dataflow dataflow = dataflowBuild.Dataflow!;
        DecodedInstruction? throwing = instructions.FirstOrDefault(static instruction => instruction.Encoding == 0x7a);
        // Exact aggregate analysis is not a scalar representation or an ABI lowering.
        // Keep this gate before all IR/GC-map construction until value-buffer lifetimes exist.
        if (dataflow.HasAggregates || signature.ReturnType == RestrictedCilTypeV1.Aggregate || signature.Parameters.Contains(RestrictedCilTypeV1.Aggregate) ||
            locals.Types.Select((type, index) => (type, index)).Any(item => item.type == RestrictedCilTypeV1.Aggregate && !receiverStorage.ContainsKey(item.index)) ||
            instructions.Any(static instruction => instruction.Encoding == 0xa4))
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1810",
                "Exact aggregate dataflow is validated; value-buffer lifetime, call ABI and element-copy lowering are not qualified.",
                provenance.MethodIdentity, provenance);
        V2LoopBuild loopBuild = AnalyzeV2Loops(graph, provenance);
        if (loopBuild.Failure is not null) return loopBuild.Failure;
        if (receiverStorage.Count != 0 && loopBuild.Loops is { Count: > 0 })
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844",
                "Caller-owned receiver storage is bounded to an acyclic lifetime; loop-carried receiver storage is not qualified.",
                provenance.MethodIdentity, provenance);

        ScalarControlFlowV2AnalysisV1 analysis = CreateV2Analysis(graph, dataflow, loopBuild.Loops!, provenance);
        analysis.EnsureWellFormed();
        ManagedEhHomeAccessPlanV1? homeAccesses = ehPlan is null ? null :
            ManagedEhFrameHomesV1.CreateAccessPlan(ehPlan, ehTypedDataflow!);
        RestrictedCilImportResultV1 result = MapControlFlowV2(metadata, graph, dataflow, signature, locals, provenance, analysis,
            receiverStorage, homeAccesses, ehPlan?.HandlerEntries, ehPlan);
        if (result.Status != RestrictedCilImportStatusV1.Success) return result;
        ManagedReceiverCallerStoragePlanV1[] completedStorage = receiverStorage.Values.Select(plan => plan with
        {
            Calls = dataflow.ReceiverCalls.Where(call => call.Value.FrameSlotIdentity == plan.FrameSlotIdentity)
                .OrderBy(static call => call.Key).Select(call => new ManagedReceiverCallerStorageCallV1(
                    call.Key, call.Value.CalleeIdentity, call.Value.CalleePlanDigest,
                    call.Value.AbiLayoutDigest,
                    Hash($"receiver-call/v1|{plan.StorageProofDigest}|{call.Key}|{call.Value.CalleeIdentity}|" +
                        $"{call.Value.CalleePlanDigest}|{call.Value.AbiLayoutDigest}")))
                .ToArray()
        }).ToArray();
        if (completedStorage.Any(static plan => plan.Calls.Count == 0))
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1845",
                "Caller-owned receiver storage must be consumed by at least one exact receiver-loan call.",
                provenance.MethodIdentity, provenance);
        return result with
        { ReceiverAbi = signature.Receiver, ReceiverCallerStoragePlans = completedStorage,
            RequiresNativeExceptionTransfer = throwing is not null || dataflow.Helpers.Values.Any(helper => IsCheckedScalarArithmetic(helper) || IsCheckedStringAccess(helper)) || instructions.Any(static instruction =>
                instruction.Encoding == 0x73 || // newobj allocation can produce managed OOM
                instruction.Encoding == NullableCheckedValue ||
                instruction.Encoding is >= 0x8d and <= 0xa4) };
    }

    private V2GraphBuild BuildV2Graph(IReadOnlyList<DecodedInstruction> instructions, RestrictedCilProvenanceV1 provenance,
        IReadOnlyCollection<int>? additionalRoots = null)
    {
        SortedSet<int> leaders = new SortedSet<int> { instructions[0].Offset };
        if (additionalRoots is not null)
            foreach (int root in additionalRoots) leaders.Add(root);
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction instruction = instructions[index];
            foreach (int target in ControlFlowTargets(instruction)) leaders.Add(target);
            if (V2EndsBlock(instruction.Encoding) && index + 1 < instructions.Count)
                leaders.Add(instructions[index + 1].Offset);
        }
        if (leaders.Count > _budgets.MaximumBasicBlocks)
            return new(null, Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2101",
                "ScalarControlFlowV2 basic-block budget was exhausted.", provenance.MethodIdentity, provenance));

        int[] starts = leaders.ToArray();
        Dictionary<int, int> offsetToInstruction = instructions.Select((item, index) => (item.Offset, index)).ToDictionary(static item => item.Offset, static item => item.index);
        List<V2Block> blocks = new List<V2Block>(starts.Length);
        for (int id = 0; id < starts.Length; id++)
        {
            int startIndex = offsetToInstruction[starts[id]];
            int endIndex = id + 1 < starts.Length ? offsetToInstruction[starts[id + 1]] - 1 : instructions.Count - 1;
            blocks.Add(new(id, startIndex, endIndex, starts[id], instructions[endIndex].Offset + instructions[endIndex].Size));
        }
        Dictionary<int, int> offsetToBlock = blocks.ToDictionary(static block => block.StartOffset, static block => block.Id);
        foreach (V2Block block in blocks)
        {
            DecodedInstruction tail = instructions[block.EndInstructionIndex];
            if (tail.Encoding is 0x2a or 0x7a or 0xfe1a or 0xdc) continue;
            foreach (int target in ControlFlowTargets(tail))
                block.Successors.Add(offsetToBlock[target]);
            if (!V2IsUnconditionalBranch(tail.Encoding))
            {
                if (block.Id + 1 >= blocks.Count)
                    return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1101",
                        "A reachable non-return block falls off the end of the method.", OffsetIdentity(provenance, tail.Offset), provenance));
                block.Successors.Add(block.Id + 1);
            }
            block.Successors.Sort();
            for (int index = block.Successors.Count - 1; index > 0; index--)
                if (block.Successors[index] == block.Successors[index - 1]) block.Successors.RemoveAt(index);
        }
        foreach (V2Block block in blocks)
            foreach (int successor in block.Successors)
                blocks[successor].Predecessors.Add(block.Id);
        foreach (V2Block block in blocks) block.Predecessors.Sort();

        HashSet<int> reachable = new HashSet<int>();
        Stack<int> pending = new Stack<int>();
        pending.Push(0);
        if (additionalRoots is not null)
            foreach (int root in additionalRoots)
                if (offsetToBlock.TryGetValue(root, out int rootBlock)) pending.Push(rootBlock);
        while (pending.Count != 0)
        {
            int id = pending.Pop();
            if (!reachable.Add(id)) continue;
            foreach (int successor in blocks[id].Successors.OrderByDescending(static value => value)) pending.Push(successor);
        }
        if (reachable.Count != blocks.Count)
        {
            if (additionalRoots is null || additionalRoots.Count == 0)
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1102",
                    "ScalarControlFlowV2 rejects unreachable CIL blocks.", provenance.MethodIdentity, provenance));
            // C# EH emission may retain an unreachable leave immediately after a terminal
            // throw/rethrow. The PE-bound EH verifier has already validated every byte;
            // dead blocks must not become native code or invented handler roots.
            int[] retained = reachable.Order().ToArray();
            Dictionary<int, int> remap = retained.Select((oldId, newId) => (oldId, newId)).ToDictionary(static row => row.oldId, static row => row.newId);
            V2Block[] filtered = retained.Select((oldId, newId) =>
            {
                V2Block old = blocks[oldId];
                return new V2Block(newId, old.StartInstructionIndex, old.EndInstructionIndex, old.StartOffset, old.EndOffsetExclusive);
            }).ToArray();
            foreach (int oldId in retained)
            {
                V2Block target = filtered[remap[oldId]];
                target.Successors.AddRange(blocks[oldId].Successors.Where(remap.ContainsKey).Select(successor => remap[successor]));
                target.Predecessors.AddRange(blocks[oldId].Predecessors.Where(remap.ContainsKey).Select(predecessor => remap[predecessor]));
            }
            return new(new(instructions, filtered), null);
        }
        return new(new(instructions, blocks), null);
    }

    private V2DataflowBuild RunV2Dataflow(
        MetadataReader metadata,
        V2Graph graph,
        MethodSignature signature,
        LocalSignature locals,
        int declaredMaxStack,
        RestrictedCilProvenanceV1 provenance,
        IReadOnlyDictionary<int, ManagedReceiverCallerStoragePlanV1> receiverStorage,
        IReadOnlyDictionary<int, V2State>? seededEntries = null,
        IReadOnlySet<int>? continuationTargets = null,
        bool enforceStackBudget = true,
        bool enforceConstructorReferences = false)
    {
        V2State entry = new V2State(Array.Empty<V2Value>(), Enumerable.Repeat(V2Value.Uninitialized, locals.Types.Count).ToArray(),
            Enumerable.Range(0, signature.Parameters.Count)
                .Select(index => AnalysisArgument(signature, index, provenance.CanonicalMethodLocalIdentity)));
        graph.Blocks[0].Entry = entry;
        if (seededEntries is not null)
            foreach (KeyValuePair<int, V2State> seed in seededEntries)
            {
                V2Block block = graph.Blocks.Single(candidate => candidate.StartOffset == seed.Key);
                if (block.Predecessors.Count != 0 && block.Id != 0 && continuationTargets?.Contains(seed.Key) != true)
                    return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL2811",
                        "An EH handler entry with a normal predecessor requires explicit entry-phi lowering.",
                        BlockIdentity(provenance, block), provenance));
                block.Entry = seed.Value.Clone();
            }
        Dictionary<(int Block, string Slot), V2Phi> phis = new Dictionary<(int Block, string Slot), V2Phi>();
        Dictionary<int, ResolvedHelper> helpers = new Dictionary<int, ResolvedHelper>();
        Dictionary<int, RestrictedCilFieldLayoutBindingV1> fields = new Dictionary<int, RestrictedCilFieldLayoutBindingV1>();
        Dictionary<int, V2Allocation> allocations = new Dictionary<int, V2Allocation>();
        Dictionary<int, RestrictedCilArrayTypeBindingV1> arrays = new Dictionary<int, RestrictedCilArrayTypeBindingV1>();
        Dictionary<int, RestrictedCilStringLiteralBindingV1> strings = new Dictionary<int, RestrictedCilStringLiteralBindingV1>();
        Dictionary<int, RestrictedCilFieldDataBindingV1> fieldData = new Dictionary<int, RestrictedCilFieldDataBindingV1>();
        Dictionary<int, RestrictedCilValueTypeBindingV1> values = new Dictionary<int, RestrictedCilValueTypeBindingV1>();
        Dictionary<int, RestrictedCilTypeTestBindingV1> typeTests = new Dictionary<int, RestrictedCilTypeTestBindingV1>();
        Dictionary<int, RestrictedCilFunctionPointerBindingV1> functionPointers = new Dictionary<int, RestrictedCilFunctionPointerBindingV1>();
        Dictionary<int, RestrictedCilCalliBindingV1> callSites = new Dictionary<int, RestrictedCilCalliBindingV1>();
        Dictionary<int, RestrictedCilDelegateCreationBindingV1> delegateCreations = new Dictionary<int, RestrictedCilDelegateCreationBindingV1>();
        Dictionary<int, RestrictedCilDelegateInvokeBindingV1> delegateInvokes = new Dictionary<int, RestrictedCilDelegateInvokeBindingV1>();
        Dictionary<int, V2ReceiverCall> receiverCalls = new Dictionary<int, V2ReceiverCall>();
        SortedSet<int> work = new SortedSet<int>(graph.Blocks.Where(static block => block.Entry is not null).Select(static block => block.Id));
        int peakStack = 0;
int callCount = graph.Instructions.Count(row => row.Encoding is NullableInt32New or NullableInt32Value or NullableInt32HasValue or NullableInt32Default or NullableInt32Initialize or NullableInitializeStack or NullableCheckedValue or NullableDefaultStack or NullableInt32ParameterValue or NullableInt32ParameterPresence);
        if (callCount > _budgets.MaximumCalls)
            return new(null, Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "Nullable scalar operation call budget was exhausted.", provenance.MethodIdentity, provenance));
        int iterations = 0;
        bool hasAggregates = false;
        int maximumIterations = checked(Math.Max(1, graph.Blocks.Count *
            (graph.Instructions.Count + locals.Types.Count + signature.Parameters.Count + declaredMaxStack + 2)));
        while (work.Count != 0)
        {
            if (++iterations > maximumIterations)
                return new(null, Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL2102",
                    "ScalarControlFlowV2 abstract-state fixpoint did not converge within its deterministic bound.", provenance.MethodIdentity, provenance));
            int blockId = work.Min;
            work.Remove(blockId);
            V2Block block = graph.Blocks[blockId];
            V2State state = block.Entry!.Clone();
            for (int index = block.StartInstructionIndex; index <= block.EndInstructionIndex; index++)
            {
                RestrictedCilImportResultV1? failure = TransferV2(metadata, graph.Instructions[index], signature, locals,
                    state, helpers, fields, allocations, arrays, strings, values, typeTests, functionPointers,
                    callSites, delegateCreations, delegateInvokes, fieldData, receiverStorage, receiverCalls,
                    ref callCount, provenance, enforceConstructorReferences);
                if (failure is not null) return new(null, failure);
                if (state.Stack.Concat(state.Locals).Concat(state.Arguments)
                    .Any(static value => value.Type == RestrictedCilTypeV1.Aggregate && value.AggregateIdentity is null))
                    return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1814",
                        "An aggregate producer lacks an exact scoped value-type identity.", OffsetIdentity(provenance, graph.Instructions[index].Offset), provenance));
                hasAggregates |= state.Stack.Any(static value => value.Type == RestrictedCilTypeV1.Aggregate);
                peakStack = Math.Max(peakStack, state.Stack.Count + graph.Instructions[index].ErasedReceiverDepth);
                if (enforceStackBudget && peakStack > _budgets.MaximumEvaluationStack)
                    return new(null, Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2103",
                        "ScalarControlFlowV2 evaluation-stack budget was exhausted.", OffsetIdentity(provenance, graph.Instructions[index].Offset), provenance));
            }
            if (block.Exit is not null && block.Exit.Equivalent(state)) continue;
            block.Exit = state;
            foreach (int successor in block.Successors)
            {
                // Finally resumes through fixed homes; normal leave edges carry pre-finally SSA.
                if (continuationTargets?.Contains(graph.Blocks[successor].StartOffset) == true) continue;
                V2StateMerge merge = MergeV2Entry(graph.Blocks[successor], graph.Blocks, signature, locals, phis, provenance);
                if (merge.Failure is not null) return new(null, merge.Failure);
                if (merge.State is not null && (graph.Blocks[successor].Entry is null || !graph.Blocks[successor].Entry!.Equivalent(merge.State)))
                {
                    graph.Blocks[successor].Entry = merge.State;
                    work.Add(successor);
                }
            }
        }
        if (peakStack > declaredMaxStack)
            return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0009",
                "The method body understates its maximum stack.", provenance.MethodIdentity, provenance));
        if (graph.Blocks.Any(static block => block.Entry is null || block.Exit is null))
            return new(null, Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL2104",
                "ScalarControlFlowV2 left a reachable block without an abstract state.", provenance.MethodIdentity, provenance));

        foreach (V2Phi phi in phis.Values)
        {
            V2Block target = graph.Blocks[phi.BlockId];
            foreach (int predecessor in target.Predecessors)
            {
                if (!phi.Incoming.ContainsKey(predecessor))
                    return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1106",
                        $"A phi lacks an incoming value for reachable predecessor b{predecessor}.", phi.Id, provenance));
            }
        }
        return new(new(phis.Values.OrderBy(static phi => phi.BlockId).ThenBy(static phi => phi.Slot, StringComparer.Ordinal).ToArray(),
            helpers, fields, allocations, arrays, strings, values, typeTests, functionPointers,
            callSites, delegateCreations, delegateInvokes, fieldData, receiverCalls, hasAggregates), null);
    }

    private RestrictedCilImportResultV1? TransferV2(
        MetadataReader metadata,
        DecodedInstruction instruction,
        MethodSignature method,
        LocalSignature locals,
        V2State state,
        IDictionary<int, ResolvedHelper> helpers,
        IDictionary<int, RestrictedCilFieldLayoutBindingV1> fields,
        IDictionary<int, V2Allocation> allocations,
        IDictionary<int, RestrictedCilArrayTypeBindingV1> arrays,
        IDictionary<int, RestrictedCilStringLiteralBindingV1> strings,
        IDictionary<int, RestrictedCilValueTypeBindingV1> values,
        IDictionary<int, RestrictedCilTypeTestBindingV1> typeTests,
        IDictionary<int, RestrictedCilFunctionPointerBindingV1> functionPointers,
        IDictionary<int, RestrictedCilCalliBindingV1> callSites,
        IDictionary<int, RestrictedCilDelegateCreationBindingV1> delegateCreations,
        IDictionary<int, RestrictedCilDelegateInvokeBindingV1> delegateInvokes,
        IDictionary<int, RestrictedCilFieldDataBindingV1> fieldData,
        IReadOnlyDictionary<int, ManagedReceiverCallerStoragePlanV1> receiverStorage,
        IDictionary<int, V2ReceiverCall> receiverCalls,
        ref int callCount,
        RestrictedCilProvenanceV1 provenance,
        bool enforceConstructorReferences = false)
    {
        string identity = OffsetIdentity(provenance, instruction.Offset);
        V2Value Pop(string code, string message, out RestrictedCilImportResultV1? failure)
        {
            if (state.Stack.Count == 0)
            {
                failure = Reject(RestrictedCilImportStatusV1.InvalidInput, code, message, identity, provenance);
                return V2Value.Uninitialized;
            }
            V2Value value = state.Stack[^1];
            state.Stack.RemoveAt(state.Stack.Count - 1);
            if (IsNullableLocal(value.AggregateIdentity) && instruction.Encoding is not (>= 0x0a and <= 0x0d or 0x13 or 0x26 or 0x28) &&
                !(instruction.Encoding == 0x2a && _nullableInt32ReturnIngress && method.AggregateReturn == NullableInt32Identity && value.AggregateIdentity == NullableInt32Identity) &&
                !(instruction.Encoding == 0x10 && instruction.Literal == 0 && _nullableInt32ReturnIngress && _nullableInt32ParameterIngress &&
                  method.AggregateReturn == NullableInt32Identity && method.AggregateParameters?.ElementAtOrDefault(0) == NullableInt32Identity && value.AggregateIdentity == NullableInt32Identity)) {
                failure = Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1845", "Nullable local carrier may only be copied to an exact nullable local or read by a qualified accessor.", identity, provenance);
                return V2Value.Uninitialized;
            }
            failure = null;
            return value;
        }
        switch (instruction.Encoding)
        {
            case NullableInt32Zero when instruction.NullableParameter:
                if (!_nullableInt32ReturnIngress || !_nullableInt32ParameterIngress || instruction.Literal != 0 ||
                    method.Parameters.Count == 0 || method.Parameters[0] != RestrictedCilTypeV1.Int64 ||
                    method.AggregateReturn != NullableInt32Identity || method.AggregateParameters?.ElementAtOrDefault(0) != NullableInt32Identity)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1847", "Nullable argument zero requires exact private map ingress.", identity, provenance);
                state.Arguments[0] = V2Value.Constant(0, RestrictedCilTypeV1.Int64, identity) with { AggregateIdentity = NullableInt32Identity };
                return null;
            case NullableInt32ParameterValue:
            case NullableInt32ParameterPresence:
                {
                    int argument = checked((int)instruction.Literal);
                    if (argument < 0 || argument >= method.Parameters.Count || method.Parameters[argument] != RestrictedCilTypeV1.Int64 ||
                        method.AggregateParameters?.ElementAtOrDefault(argument) != NullableInt32Identity ||
                        state.Arguments[argument].AggregateIdentity != NullableInt32Identity)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1847", "Nullable getter requires exact packed argument ingress.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.Int32, identity));
                    return null;
                }
            case NullableInt32New:
            case NullableInitializeStack:
                {
                    V2Value value = Pop("HCCIL1845", "Nullable constructor stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (!ValidNullableInput(value, instruction.NullableWide))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1845", "Nullable<int> construction requires an exact I4 value.", identity, provenance);
                    V2Value constructed = instruction.NullableWide ? value with { Type = RestrictedCilTypeV1.Int64, AggregateIdentity = NullableIdentity(instruction),
                        NullableHasValue = V2Value.Constant(1, RestrictedCilTypeV1.Int32, identity) } :
                        V2Value.Definition(RestrictedCilTypeV1.Int64, identity) with { AggregateIdentity = NullableIdentity(instruction) };
                    if (instruction.Encoding == NullableInitializeStack && instruction.NullableParameter) {
                        if (!_nullableInt32ReturnIngress || !_nullableInt32ParameterIngress || instruction.Literal != 0 ||
                            method.Parameters.Count == 0 || method.Parameters[0] != RestrictedCilTypeV1.Int64 ||
                            method.AggregateReturn != NullableInt32Identity || method.AggregateParameters?.ElementAtOrDefault(0) != NullableInt32Identity ||
                            constructed.AggregateIdentity != NullableInt32Identity)
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1847", "Nullable argument initialization requires exact private map ingress.", identity, provenance);
                        state.Arguments[0] = constructed;
                    }
                    else if (instruction.Encoding == NullableInitializeStack) state.Locals[(int)instruction.Literal] = constructed;
                    else state.Stack.Add(constructed);
                    return null;
                }
            case NullableInt32Zero:
            case NullableInt32Value:
            case NullableInt32HasValue:
            case NullableInt32Default:
            case NullableDefaultStack:
            case NullableInt32Initialize:
            case NullableCheckedValue:
                {
                    int local = checked((int)instruction.Literal);
                    string nullableIdentity = NullableIdentity(instruction);
                    if (instruction.NullableParameter) {
                        if (local < 0 || local >= method.Parameters.Count ||
                            method.Parameters[local] != RestrictedCilTypeV1.Int64 || method.AggregateParameters?.ElementAtOrDefault(local) != NullableInt32Identity ||
                            state.Arguments[local].AggregateIdentity != NullableInt32Identity)
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1847", "Nullable default requires exact packed parameter ingress.", identity, provenance);
                        if (instruction.Encoding == NullableDefaultStack && (IsNullableInt32ComputedDefaultSignature(method) ||
                            _nullableInt32ReturnIngress && _nullableInt32ParameterIngress && method.AggregateReturn == NullableInt32Identity)) {
                            V2Value argumentInput = Pop("HCCIL1847", "Nullable parameter default stack underflow.", out RestrictedCilImportResultV1? parameterFailure);
                            if (parameterFailure is not null) return parameterFailure;
                            if (!ValidNullableInput(argumentInput, wide:false))
                                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1847", "Nullable parameter computed default requires primitive I4.", identity, provenance);
                        } else if (instruction.Encoding != NullableInt32Default || instruction.NullableDefault is not { } parameterProducer ||
                            !(method.Parameters.Count == 1 && parameterProducer.Encoding is >= 0x15 and <= 0x20 ||
                              IsNullableInt32FallbackParameter(method) && parameterProducer.Encoding == 0x03 ||
                              _nullableInt32ReturnIngress && _nullableInt32ParameterIngress && IsNullableInt32MapDefaultInput(method, parameterProducer)) ||
                            !ValidNullableInput(NullableDefaultValue(parameterProducer, state), wide:false))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1847", "Nullable parameter default requires exact packed ingress and an immutable I4 input.", identity, provenance);
                        state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.Int32, identity));
                        return null;
                    }
                    if (local < 0 || local >= locals.Types.Count || locals.Aggregates?.ElementAtOrDefault(local) != nullableIdentity)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1845", "Nullable accessor requires an exact local declaration.", identity, provenance);
                    if (instruction.Encoding == NullableInt32Zero) {
                        state.Locals[local] = V2Value.Constant(0, RestrictedCilTypeV1.Int64, identity) with { AggregateIdentity = nullableIdentity,
                            NullableHasValue = instruction.NullableWide ? V2Value.Constant(0, RestrictedCilTypeV1.Int32, identity) : null };
                        return null;
                    }
                    if (instruction.Encoding == NullableInt32Initialize) {
                        if (instruction.NullableDefault is not { } initializerProducer || !ValidNullableInput(NullableDefaultValue(initializerProducer, state), instruction.NullableWide))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1845", "Nullable initialization requires an initialized exact I4 producer.", identity, provenance);
                        state.Locals[local] = instruction.NullableWide ? NullableDefaultValue(instruction.NullableDefault!, state) with {
                            Type = RestrictedCilTypeV1.Int64, AggregateIdentity = nullableIdentity, NullableHasValue = V2Value.Constant(1, RestrictedCilTypeV1.Int32, identity) } :
                            V2Value.Definition(RestrictedCilTypeV1.Int64, identity) with { AggregateIdentity = NullableIdentity(instruction) };
                        return null;
                    }
                    if (!state.Locals[local].Initialized || state.Locals[local].AggregateIdentity != nullableIdentity ||
                        instruction.NullableWide && state.Locals[local].NullableHasValue is null)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1845", "Nullable accessor reads an uninitialized or mismatched local.", identity, provenance);
                    if (instruction.Encoding == NullableDefaultStack) {
                        V2Value fallback = Pop("HCCIL1845", "Nullable default argument underflows the evaluation stack.", out RestrictedCilImportResultV1? failure);
                        if (failure is not null) return failure;
                        if (!ValidNullableInput(fallback, instruction.NullableWide))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1845", "Nullable default requires an exact scalar argument.", identity, provenance);
                    }
                    if (instruction.Encoding == NullableInt32Default && (instruction.NullableDefault is not { } producer ||
                        !ValidNullableInput(NullableDefaultValue(producer, state), instruction.NullableWide)))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1845", "Nullable default requires an initialized exact I4 producer.", identity, provenance);
                    if (instruction.Encoding == NullableCheckedValue) {
                        state.Stack.Add(V2Value.Definition(instruction.NullableWide ? RestrictedCilTypeV1.Int64 : RestrictedCilTypeV1.Int32, identity));
                        return null;
                    }
                    state.Stack.Add(instruction.NullableWide && instruction.Encoding != NullableInt32HasValue
                        ? instruction.Encoding == NullableInt32Value ? state.Locals[local] with { AggregateIdentity = null, NullableHasValue = null }
                            : V2Value.Definition(RestrictedCilTypeV1.Int64, identity)
                        : instruction.NullableWide ? state.Locals[local].NullableHasValue! : V2Value.Definition(RestrictedCilTypeV1.Int32, identity));
                    return null;
                }
            case 0x00: return null;
            case 0x26:
                Pop("HCCIL1602", "pop evaluation-stack underflow.", out RestrictedCilImportResultV1? popFailure);
                return popFailure;
            case 0x25:
                if (state.Stack.Count == 0)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1601", "dup evaluation-stack underflow.", identity, provenance);
                state.Stack.Add(state.Stack[^1]);
                return null;
            case >= 0x02 and <= 0x05:
                {
                    int argument = instruction.Encoding - 0x02;
                    if (argument >= method.Parameters.Count)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0011", "ldarg references an absent argument.", identity, provenance);
                    state.Stack.Add(state.Arguments[argument]);
                    return null;
                }
            case 0x0e:
                {
                    int argument = checked((int)instruction.Literal);
                    if (argument >= method.Parameters.Count)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0011", "ldarg.s references an absent argument.", identity, provenance);
                    state.Stack.Add(state.Arguments[argument]);
                    return null;
                }
            case 0x10:
                {
                    int argument = checked((int)instruction.Literal);
                    V2Value value = Pop("HCCIL1866", "starg.s references an absent argument or underflows the evaluation stack.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null || argument >= method.Parameters.Count)
                        return failure ?? Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1866", "starg.s references an absent argument.", identity, provenance);
                    if (argument == 0 && method.Receiver is not null)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1867", "starg.s may not replace a typed receiver loan.", identity, provenance);
                    if (!ExactStackCompatible(value, method.Parameters[argument], method.AggregateParameters?.ElementAtOrDefault(argument)))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1868", "starg.s value does not match the exact parameter type.", identity, provenance);
                    if (enforceConstructorReferences && method.Parameters[argument] == RestrictedCilTypeV1.ObjectReference &&
                        !MatchesConstructorReferenceStore(value, method.ConstructorReferenceParameters?.ElementAtOrDefault(argument) ?? ConstructorReferenceKind.None))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844", "Reference argument store lacks a matching declared carrier.", identity, provenance);
                    state.Arguments[argument] = value;
                    return null;
                }
            case >= 0x06 and <= 0x09:
                {
                    int local = instruction.Encoding - 0x06;
                    if (local >= locals.Types.Count)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0031", "ldloc references an absent local.", identity, provenance);
                    if (!state.Locals[local].Initialized)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0032", "ldloc reads a local not definitely assigned on every incoming edge.", identity, provenance);
                    state.Stack.Add(state.Locals[local]);
                    return null;
                }
            case 0x11:
                {
                    int local = checked((int)instruction.Literal);
                    if (local >= locals.Types.Count)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0031", "ldloc.s references an absent local.", identity, provenance);
                    if (!state.Locals[local].Initialized)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0032", "ldloc.s reads a local not definitely assigned on every incoming edge.", identity, provenance);
                    state.Stack.Add(state.Locals[local]);
                    return null;
                }
            case 0x12:
                {
                    int local = checked((int)instruction.Literal);
                    if (!receiverStorage.TryGetValue(local, out ManagedReceiverCallerStoragePlanV1? storage) ||
                        locals.Aggregates?.ElementAtOrDefault(local) != storage.ScopedTypeIdentity)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844",
                            $"ldloca.s local {local} requires one exact compiler-owned receiver frame slot; " +
                            $"local identity='{locals.Aggregates?.ElementAtOrDefault(local) ?? "<none>"}', " +
                            $"planned locals='{string.Join(',', receiverStorage.Keys.Order())}'.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.ManagedByRef, identity) with
                    {
                        ReceiverIdentity = storage.ScopedTypeIdentity,
                        ReceiverStorageSlotIdentity = storage.FrameSlotIdentity
                    });
                    return null;
                }
            case >= 0x0a and <= 0x0d:
                {
                    int local = instruction.Encoding - 0x0a;
                    V2Value value = Pop("HCCIL0033", "stloc references an absent local or underflows the evaluation stack.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null || local >= locals.Types.Count) return failure ?? Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0033", "stloc references an absent local.", identity, provenance);
                    if (!LocalStackCompatible(value, locals.Types[local], locals.Aggregates?.ElementAtOrDefault(local)))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0034", "stloc value does not match the declared local type.", identity, provenance);
                    if (enforceConstructorReferences && locals.Types[local] == RestrictedCilTypeV1.ObjectReference &&
                        !MatchesConstructorReferenceStore(value, locals.ConstructorReferences?.ElementAtOrDefault(local) ?? ConstructorReferenceKind.None))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844", "Reference local store lacks a matching declared carrier.", identity, provenance);
                    state.Locals[local] = NormalizeLocalValue(value, locals.Types[local], identity);
                    return null;
                }
            case 0x13:
                {
                    int local = checked((int)instruction.Literal);
                    V2Value value = Pop("HCCIL0033", "stloc.s references an absent local or underflows the evaluation stack.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null || local >= locals.Types.Count) return failure ?? Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0033", "stloc.s references an absent local.", identity, provenance);
                    if (!LocalStackCompatible(value, locals.Types[local], locals.Aggregates?.ElementAtOrDefault(local)))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0034", "stloc.s value does not match the declared local type.", identity, provenance);
                    if (enforceConstructorReferences && locals.Types[local] == RestrictedCilTypeV1.ObjectReference &&
                        !MatchesConstructorReferenceStore(value, locals.ConstructorReferences?.ElementAtOrDefault(local) ?? ConstructorReferenceKind.None))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844", "Reference local store lacks a matching declared carrier.", identity, provenance);
                    state.Locals[local] = NormalizeLocalValue(value, locals.Types[local], identity);
                    return null;
                }
            case 0x14:
                state.Stack.Add(V2Value.Constant(0, RestrictedCilTypeV1.ObjectReference, identity) with {
                    ConstructorReference = enforceConstructorReferences ? ConstructorReferenceKind.Null : ConstructorReferenceKind.None });
                return null;
            case >= 0x15 and <= 0x21:
                state.Stack.Add(V2Value.Constant(instruction.Literal, instruction.Encoding == 0x21 ? RestrictedCilTypeV1.Int64 : RestrictedCilTypeV1.Int32, identity));
                return null;
            case 0x67:
            case 0x68:
                {
                    bool i1 = instruction.Encoding == 0x67;
                    V2Value value = Pop(i1 ? "HCCIL1879" : "HCCIL1877",
                        i1 ? "conv.i1 evaluation-stack underflow." : "conv.i2 evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (value.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                        RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, i1 ? "HCCIL1878" : "HCCIL1876",
                            i1 ? "conv.i1 requires an I4, I8 or native-integer operand in the restricted profile." :
                                "conv.i2 requires an I4, I8 or native-integer operand in the restricted profile.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.Int32, identity));
                    return null;
                }
            case 0xd1:
            case 0xd2:
                {
                    bool u2 = instruction.Encoding == 0xd1;
                    V2Value value = Pop(u2 ? "HCCIL1875" : "HCCIL1871",
                        u2 ? "conv.u2 evaluation-stack underflow." : "conv.u1 evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (value.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                        RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, u2 ? "HCCIL1874" : "HCCIL1870",
                            u2 ? "conv.u2 requires an I4, I8 or native-integer operand in the restricted profile." :
                                "conv.u1 requires an I4, I8 or native-integer operand in the restricted profile.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.Int32, identity));
                    return null;
                }
            case 0x65:
            case 0x66:
                {
                    string operation = instruction.Encoding == 0x66 ? "not" : "neg";
                    V2Value value = Pop("HCCIL1873", operation + " evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (value.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                        RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1872",
                            operation + " requires an I4, I8 or native-integer operand in the restricted profile.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(value.Type == RestrictedCilTypeV1.UInt32
                        ? RestrictedCilTypeV1.Int32 : value.Type, identity));
                    return null;
                }
            case 0x45:
                {
                    V2Value selector = Pop("HCCIL1865", "switch selector stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (selector.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1864",
                            "switch requires an I4 or native-integer selector.", identity, provenance);
                    return null;
                }
            case 0x62:
            case 0x63:
            case 0x64:
                {
                    string operation = instruction.Encoding == 0x62 ? "shl" : instruction.Encoding == 0x63 ? "shr" : "shr.un";
                    V2Value count = Pop("HCCIL1851", operation + " count stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value value = Pop("HCCIL1851", operation + " value stack underflow.", out failure);
                    if (failure is not null) return failure;
                    if (value.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.Int64 or
                        RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt) ||
                        count.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1850",
                            operation + " requires an I4/I8/native integer value and an I4/native integer count.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(value.Type, identity));
                    return null;
                }
            case 0x5b:
            case 0x5c:
            case 0x5d:
            case 0x5e:
                {
                    string operation = instruction.Encoding switch
                    {
                        0x5b => "div", 0x5c => "div.un", 0x5d => "rem", _ => "rem.un"
                    };
                    V2Value right = Pop("HCCIL0012", operation + " evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value left = Pop("HCCIL0012", operation + " evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    int width = UnsignedDivisionWidth(left.Type, right.Type);
                    if (width == 0) return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1831",
                        operation + " requires matching I4 or I8 operands.", identity, provenance);
                    if (instruction.Encoding is 0x5b or 0x5d && !ProvenSafeSignedDivision(left, right, width))
                    {
                        if (instruction.Encoding == 0x5d && width == 64 &&
                            (left.Type != RestrictedCilTypeV1.Int64 || right.Type != RestrictedCilTypeV1.Int64))
                            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1832",
                                operation + " requires proven DivideByZeroException and signed-minimum/-1 OverflowException exclusion or an exact checked helper for this width/operation.", identity, provenance);
                        if (!helpers.ContainsKey(instruction.Offset))
                        {
                            if (++callCount > _budgets.MaximumCalls)
                                return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007",
                                    "Checked signed division exhausted the call budget.", identity, provenance);
                            string helperIdentity = instruction.Encoding == 0x5d ? (width == 32 ? "checked-rem-i4" : "checked-rem-i8") :
                                width == 32 ? "checked-div-i4" : "checked-div-i8";
                            RestrictedCilHelperContractV1 contract = new RestrictedCilHelperContractV1(helperIdentity,
                                [left.Type, right.Type], left.Type, "runtime-helper",
                                $"Exact Int{width} {(instruction.Encoding == 0x5d ? "remainder" : "division")}; zero raises DivideByZeroException and MinValue/-1 raises OverflowException.",
                                IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, IrArchitecturalEffectKind.Control);
                            helpers[instruction.Offset] = new(helperIdentity, contract, false);
                        }
                    }
                    if (instruction.Encoding is 0x5c or 0x5e && !ProvenNonzeroDivisor(right, width))
                    {
                        if (!helpers.ContainsKey(instruction.Offset))
                        {
                            if (++callCount > _budgets.MaximumCalls)
                                return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007",
                                    "Checked unsigned division exhausted the call budget.", identity, provenance);
                            string unsignedHelper = instruction.Encoding == 0x5e ? (width == 32 ? "checked-remu-i4" : "checked-remu-i8") :
                                width == 32 ? "checked-divu-i4" : "checked-divu-i8";
                            RestrictedCilHelperContractV1 contract = new RestrictedCilHelperContractV1(unsignedHelper,
                                [left.Type, right.Type], left.Type, "runtime-helper",
                                $"Exact UInt{width} {(instruction.Encoding == 0x5e ? "remainder" : "division")}; zero divisor raises managed System.DivideByZeroException and never returns an ISA fallback value.",
                                IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
                                IrArchitecturalEffectKind.Control);
                            helpers[instruction.Offset] = new(unsignedHelper, contract, false);
                        }
                    }
                    state.Stack.Add(V2Value.Definition(left.Type, identity));
                    return null;
                }
            case 0x58:
            case 0x59:
            case 0x5a:
            case 0x5f:
            case 0x60:
            case 0x61:
                {
                    V2Value right = Pop("HCCIL0012", "Arithmetic evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value left = Pop("HCCIL0012", "Arithmetic evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    if ((left.Type != right.Type && UnsignedDivisionWidth(left.Type, right.Type) == 0) || !IsArithmeticType(left.Type))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0013", "Arithmetic operands have incompatible CIL types.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(left.Type, identity));
                    return null;
                }
            case 0xfe01:
            case >= 0xfe02 and <= 0xfe05:
                {
                    V2Value right = Pop("HCCIL0035", "Comparison evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value left = Pop("HCCIL0035", "Comparison evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    bool nullObjectInequality = instruction.Encoding == 0xfe03 &&
                        left.Type == RestrictedCilTypeV1.ObjectReference &&
                        right.Type == RestrictedCilTypeV1.ObjectReference &&
                        (left.Operand is { Kind: IrOperandKind.Constant, Value: 0 } ||
                         right.Operand is { Kind: IrOperandKind.Constant, Value: 0 });
                    bool nativeZeroEquality = instruction.Encoding == 0xfe01 &&
                        ((left.Type is RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt &&
                          right.Type == RestrictedCilTypeV1.Int32 && right.Operand is { Kind: IrOperandKind.Constant, Value: 0 }) ||
                         (right.Type is RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt &&
                          left.Type == RestrictedCilTypeV1.Int32 && left.Operand is { Kind: IrOperandKind.Constant, Value: 0 }));
                    if ((left.Type != right.Type && UnsignedDivisionWidth(left.Type, right.Type) == 0 && !nativeZeroEquality) ||
                        (instruction.Encoding == 0xfe01 ? !IsEqualityType(left.Type) :
                            !IsArithmeticType(left.Type) && !nullObjectInequality))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0036",
                            $"Comparison operands have incompatible CIL types: left={left.Type}, right={right.Type}.",
                            identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.Int32, identity));
                    return null;
                }
            case 0x2c:
            case 0x2d:
            case 0x39:
            case 0x3a:
                {
                    V2Value condition = Pop("HCCIL0014", "Conditional branch evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    return IsConditionType(condition.Type) ? null : Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0015", "Conditional branch requires a qualified integer or object reference.", identity, provenance);
                }
            case >= 0x2e and <= 0x37:
            case >= 0x3b and <= 0x44:
                {
                    V2Value right = Pop("HCCIL1110", "Relational branch evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value left = Pop("HCCIL1110", "Relational branch evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    ushort normalized = instruction.Encoding >= 0x3b ? (ushort)(instruction.Encoding - 0x0d) : instruction.Encoding;
                    bool equality = normalized is 0x2e or 0x33;
                    bool exactTypes = left.Type == right.Type &&
                        (equality ? IsEqualityType(left.Type) : IsArithmeticType(left.Type));
                    // CLI I4/I8 equality compares bits; integer constants have signed
                    // carriers even for uint/ulong operands. Keep nominal values out.
                    bool scalarIntegerEquality = equality && left.AggregateIdentity is null && right.AggregateIdentity is null &&
                        UnsignedDivisionWidth(left.Type, right.Type) != 0;
                    bool unsignedI4Constant = normalized is >= 0x34 and <= 0x37 &&
                        ((left.Type == RestrictedCilTypeV1.UInt32 && IsNonNegativeI4Constant(right)) ||
                         (right.Type == RestrictedCilTypeV1.UInt32 && IsNonNegativeI4Constant(left)));
                    return exactTypes || scalarIntegerEquality || unsignedI4Constant
                        ? null
                        : Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1111",
                            equality ? "Equality branch operands have incompatible CIL types." : "Relational branch operands have incompatible CIL types.",
                            identity, provenance);
                }
            case 0x2b:
            case 0x38: return null;
            case 0xdd:
            case 0xde:
                state.Stack.Clear();
                return null;
            case 0xfe1a:
            case 0xdc:
                return state.Stack.Count == 0 ? null : Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0819",
                    "rethrow/endfinally require an empty evaluation stack.", identity, provenance);
            case 0x28:
            case 0x6f:
                {
                    if (instruction.Encoding == 0x6f &&
                        _delegateInvokeBindings.TryGetValue(instruction.Token, out RestrictedCilDelegateInvokeBindingV1? invoke))
                    {
                        if (!ValidDelegateInvokeBinding(invoke))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1620",
                                "Delegate Invoke binding is malformed.", identity, provenance);
                        for (int parameter = invoke.ParameterTypes.Count - 1; parameter >= 0; parameter--)
                        {
                            V2Value actual = Pop("HCCIL1621", "Delegate Invoke evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                            if (failure is not null) return failure;
                            if (!StackCompatible(actual.Type, invoke.ParameterTypes[parameter]))
                                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1622",
                                    "Delegate Invoke argument type mismatch.", identity, provenance);
                        }
                        if (++callCount > _budgets.MaximumCalls)
                            return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007",
                                "The call budget was exhausted.", identity, provenance);
                        if (invoke.ReturnType != RestrictedCilTypeV1.Void)
                            state.Stack.Add(V2Value.Definition(invoke.ReturnType, identity));
                        delegateInvokes[instruction.Offset] = invoke;
                        return null;
                    }
                    if (!helpers.ContainsKey(instruction.Offset) && ++callCount > _budgets.MaximumCalls)
                        return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    HelperResolution resolution = instruction.Encoding == 0x6f
                        ? ResolveCallvirt(metadata, instruction.Token)
                        : ResolveHelper(metadata, instruction.Token);
                    if (resolution.Failure is not null) return resolution.Failure with { Provenance = provenance };
                    ResolvedHelper helper = resolution.Helper!;
                    if (helper.EmptyArrayTypeIdentity is string emptyArrayIdentity &&
                        ResolveEmptyArrayBinding(helper) is null)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1462",
                            $"Array.Empty requires an exact runtime SZARRAY binding for '{emptyArrayIdentity}'.",
                            identity, provenance);
                    for (int parameter = helper.Contract.ParameterTypes.Count - 1; parameter >= 0; parameter--)
                    {
                        V2Value actual = Pop("HCCIL0016", "Helper call evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                        if (failure is not null) return failure;
                        if (enforceConstructorReferences && parameter == 0 && IsCheckedStringAccess(helper) &&
                            actual.ConstructorReference is not (ConstructorReferenceKind.String or ConstructorReferenceKind.Null))
                            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844", "The string accessor operand lacks an exact string declaration fact.", identity, provenance);
                        string? nominalParameter = helper.AggregateParameters?.ElementAtOrDefault(parameter);
                        bool exactInt64ValueArgument = helper.Contract.ParameterTypes[parameter] != RestrictedCilTypeV1.Int64 ||
                            !IsProvenInt64ScalarIdentity(metadata, nominalParameter) || actual.AggregateIdentity == nominalParameter;
                        if (!CallStackCompatible(actual, helper.Contract.ParameterTypes[parameter], nominalParameter) || !exactInt64ValueArgument)
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0017", "Helper call argument type mismatch.", identity, provenance);
                        if (parameter == 0 && helper.Contract.ParameterTypes[parameter] == RestrictedCilTypeV1.ManagedByRef)
                        {
                            if (helper.Receiver is not { } receiver || actual.ReceiverStorageSlotIdentity is null ||
                                actual.ReceiverIdentity != receiver.ScopedTypeIdentity)
                                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1846",
                                    "Receiver call lacks exact caller-owned frame-slot provenance.", identity, provenance);
                            ManagedReceiverCallerStoragePlanV1 storage = receiverStorage.Values.Single(plan =>
                                plan.FrameSlotIdentity == actual.ReceiverStorageSlotIdentity);
                            HybridCpuManagedCallLayoutV1 abi = ClassifyReceiverCall(storage, receiver, helper.Contract);
                            if (abi.Status != HybridCpuPlatformFactStatus.Supported || abi.NativeLayout is null)
                                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1848",
                                    "Receiver call failed the exact caller-storage ABI classifier: " + abi.Reason,
                                    identity, provenance);
                            V2ReceiverCall call = new V2ReceiverCall(actual.ReceiverStorageSlotIdentity,
                                helper.Identity, receiver.PlanDigest, abi.Digest);
                            if (receiverCalls.TryGetValue(instruction.Offset, out V2ReceiverCall? prior) && prior != call)
                                return Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL1847",
                                    "Receiver call proof changed across dataflow iterations.", identity, provenance);
                            receiverCalls[instruction.Offset] = call;
                        }
                    }
                    if (helper.Contract.ReturnType != RestrictedCilTypeV1.Void)
                        state.Stack.Add(V2Value.Definition(StackType(helper.Contract.ReturnType), identity) with
                        { AggregateIdentity = helper.AggregateReturn });
                    helpers[instruction.Offset] = helper;
                    return null;
                }
            case 0xfe06:
            case 0xfe07:
                {
                    if (!_functionPointerBindings.TryGetValue(instruction.Token, out RestrictedCilFunctionPointerBindingV1? binding) ||
                        !ValidFunctionPointerBinding(binding))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1602",
                            "ldftn/ldvirtftn require an exact managed method and signature binding.", identity, provenance);
                    MethodSignature targetSignature = ParseManagedMethodTokenSignature(metadata, instruction.Token);
                    if (targetSignature.Status != SignatureStatus.Success ||
                        targetSignature.HasThis != binding.IsInstanceMethod ||
                        !targetSignature.Parameters.Select(StackType).SequenceEqual(binding.ParameterTypes) ||
                        StackType(targetSignature.ReturnType) != binding.ReturnType ||
                        (instruction.Encoding == 0xfe07) != binding.VirtualSlotMetadataToken.HasValue)
                        return Reject(targetSignature.Status == SignatureStatus.Invalid
                                ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.Unsupported,
                            "HCCIL1605", targetSignature.Status == SignatureStatus.Success
                                ? "The function-pointer binding does not match the exact target metadata signature or transfer form."
                                : targetSignature.Message, identity, provenance);
                    if (instruction.Encoding == 0xfe07)
                    {
                        V2Value receiver = Pop("HCCIL1603", "ldvirtftn evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                        if (failure is not null) return failure;
                        if (receiver.Type != RestrictedCilTypeV1.ObjectReference || !binding.IsInstanceMethod ||
                            !_dispatchBindings.ContainsKey(binding.VirtualSlotMetadataToken ?? instruction.Token))
                            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1604",
                                "ldvirtftn requires an object receiver and exact dispatch slot binding.", identity, provenance);
                    }
                    if (++callCount > _budgets.MaximumCalls)
                        return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.NativeUInt, identity,
                        binding.SignatureId));
                    functionPointers[instruction.Offset] = binding;
                    return null;
                }
            case 0x29:
                {
                    MethodSignature callSiteSignature = ParseManagedCallSiteSignature(metadata, instruction.Token);
                    if (callSiteSignature.Status != SignatureStatus.Success)
                        return Reject(callSiteSignature.Status == SignatureStatus.Invalid
                                ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.Unsupported,
                            "HCCIL1615", callSiteSignature.Message, identity, provenance);
                    if (!_calliBindings.TryGetValue(instruction.Token, out RestrictedCilCalliBindingV1? binding) ||
                        !ValidCalliBinding(binding) ||
                        !callSiteSignature.Parameters.Select(StackType).SequenceEqual(binding.ParameterTypes) ||
                        StackType(callSiteSignature.ReturnType) != binding.ReturnType)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1610",
                            "calli requires a binding identical to its exact managed StandAloneSig metadata.", identity, provenance);
                    V2Value pointer = Pop("HCCIL1611", "calli function-pointer stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (pointer.Type != RestrictedCilTypeV1.NativeUInt)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1612",
                            "calli target must be a managed native-uint function pointer.", identity, provenance);
                    for (int parameter = binding.ParameterTypes.Count - 1; parameter >= 0; parameter--)
                    {
                        V2Value actual = Pop("HCCIL1613", "calli argument stack underflow.", out failure);
                        if (failure is not null) return failure;
                        if (!StackCompatible(actual.Type, binding.ParameterTypes[parameter]))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1614",
                                "calli argument type mismatch.", identity, provenance);
                    }
                    if (++callCount > _budgets.MaximumCalls)
                        return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    if (binding.ReturnType != RestrictedCilTypeV1.Void)
                        state.Stack.Add(V2Value.Definition(binding.ReturnType, identity));
                    callSites[instruction.Offset] = binding;
                    return null;
                }
            case 0x74:
            case 0x75:
                {
                    V2Value receiver = Pop("HCCIL1510", "Type-test evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (receiver.Type != RestrictedCilTypeV1.ObjectReference)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1511",
                            "castclass/isinst require an object-reference operand.", identity, provenance);
                    if (!_typeTestBindings.TryGetValue(instruction.Token, out RestrictedCilTypeTestBindingV1? binding) ||
                        !ValidTypeTestBinding(binding))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1512",
                            "castclass/isinst require an exact runtime type binding.", identity, provenance);
                    if (!typeTests.ContainsKey(instruction.Offset) && ++callCount > _budgets.MaximumCalls)
                        return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007",
                            "The call budget was exhausted.", identity, provenance);
                    typeTests[instruction.Offset] = binding;
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.ObjectReference, identity));
                    return null;
                }
            case 0x69:
            case 0x6d:
                {
                    V2Value source = Pop("HCCIL1440", "conv.i4 evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (source.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                        RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1441", "conv.i4 requires an I4, I8 or native-integer operand.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.Int32, identity));
                    return null;
                }
            case 0x6a:
            case 0x6e:
                {
                    V2Value source = Pop("HCCIL1881", "conv.i8 evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (source.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                        RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                        RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1880",
                            "conv.i8 requires an I4, I8 or native-integer operand in the restricted profile.", identity, provenance);
                    state.Stack.Add(instruction.Encoding == 0x6a && source.Type == RestrictedCilTypeV1.Int32 &&
                        source.Operand.Kind == IrOperandKind.Constant
                        ? V2Value.Constant(unchecked((long)(int)(uint)source.Operand.Value), RestrictedCilTypeV1.Int64, identity)
                        : V2Value.Definition(RestrictedCilTypeV1.Int64, identity));
                    return null;
                }
            case 0x73:
                {
                    if (_delegateCreationBindings.TryGetValue((CurrentMethodToken(provenance), instruction.Token,
                            instruction.Offset),
                            out RestrictedCilDelegateCreationBindingV1? delegateCreation))
                    {
                        if (!ValidDelegateCreationBinding(delegateCreation))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1630",
                                "Delegate creation binding is malformed.", identity, provenance);
                        V2Value pointer = Pop("HCCIL1631", "Delegate constructor function-pointer stack underflow.", out RestrictedCilImportResultV1? failure);
                        if (failure is not null) return failure;
                        V2Value target = Pop("HCCIL1632", "Delegate constructor target stack underflow.", out failure);
                        if (failure is not null) return failure;
                        if (pointer.Type != RestrictedCilTypeV1.NativeUInt || target.Type != RestrictedCilTypeV1.ObjectReference)
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1633",
                                "Delegate constructor requires object-reference target and managed native-uint pointer.", identity, provenance);
                        if (pointer.ManagedFunctionPointerSignatureId != delegateCreation.SignatureId)
                            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1634",
                                "Delegate construction requires a compiler-proven function pointer with the exact delegate signature.",
                                identity, provenance);
                        if (++callCount > _budgets.MaximumCalls)
                            return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                        state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.ObjectReference, identity));
                        delegateCreations[instruction.Offset] = delegateCreation;
                        return null;
                    }
                    AllocationResolution resolution = ResolveAllocation(metadata, instruction.Token);
                    if (resolution.Failure is not null) return resolution.Failure with { Provenance = provenance };
                    ResolvedHelper constructor = resolution.Constructor!;
                    bool scalarValueProjection = resolution.Allocation!.IsScalarValueProjection;
                    int requiredCalls = scalarValueProjection ? 0 : 2;
                    if (callCount > _budgets.MaximumCalls - requiredCalls)
                        return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007",
                            "newobj allocation and constructor calls exhaust the call budget.", identity, provenance);
                    callCount += requiredCalls;
                    V2Value? scalarArgument = null;
                    for (int parameter = constructor.Contract.ParameterTypes.Count - 1; parameter >= 1; parameter--)
                    {
                        V2Value actual = Pop("HCCIL1305", "newobj constructor argument stack underflow.",
                            out RestrictedCilImportResultV1? failure);
                        if (failure is not null) return failure;
                        if (!StackCompatible(actual.Type, constructor.Contract.ParameterTypes[parameter]))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1306",
                                "newobj constructor argument type mismatch.", identity, provenance);
                        scalarArgument = actual;
                    }
                    EntityHandle constructorHandle = MetadataTokens.EntityHandle(instruction.Token);
                    EntityHandle constructorOwner = constructorHandle.Kind == HandleKind.MethodDefinition
                        ? metadata.GetMethodDefinition((MethodDefinitionHandle)constructorHandle).GetDeclaringType()
                        : metadata.GetMemberReference((MemberReferenceHandle)constructorHandle).Parent;
                    string? scalarAggregateIdentity = scalarValueProjection ? AggregateIdentity(metadata, constructorOwner) : null;
                    V2Value constructed = scalarValueProjection && instruction.Name == ProjectedLocalConstructorName
                        ? scalarArgument!
                        : V2Value.Definition(scalarValueProjection
                        ? resolution.Allocation.ScalarValueType
                        : RestrictedCilTypeV1.ObjectReference, identity);
                    state.Stack.Add(constructed with
                    {
                        AggregateIdentity = scalarAggregateIdentity
                    });
                    allocations[instruction.Offset] = new(resolution.Allocation!, constructor, scalarAggregateIdentity);
                    return null;
                }
            case 0x72:
                {
                    string literal;
                    try { literal = metadata.GetUserString(MetadataTokens.UserStringHandle(instruction.Token)); }
                    catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
                    { return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1410", "ldstr token is invalid.", identity, provenance); }
                    if (!_stringBindings.TryGetValue(literal, out RestrictedCilStringLiteralBindingV1? binding) || !ValidStringBinding(binding))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1411", "ldstr requires an exact immutable UTF-16 runtime literal binding.", identity, provenance);
                    if (!strings.ContainsKey(instruction.Offset) && ++callCount > _budgets.MaximumCalls)
                        return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    strings[instruction.Offset] = binding;
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.ObjectReference, identity));
                    return null;
                }
            case 0xd0:
                {
                    if (!_fieldDataBindings.TryGetValue(instruction.Token,
                            out RestrictedCilFieldDataBindingV1? binding) || binding.DataHandle == 0 ||
                        binding.Data is null || binding.Data.Length == 0)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1460",
                            "ldtoken requires one exact non-empty FieldRVA data binding.", identity, provenance);
                    fieldData[instruction.Offset] = binding;
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.NativeUInt, identity));
                    return null;
                }
            case 0x8d:
                {
                    if (!_arrayBindings.TryGetValue(instruction.Token, out RestrictedCilArrayTypeBindingV1? binding) || !ValidArrayBinding(binding))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1401", "newarr requires an exact runtime SZARRAY binding for its element token.", identity, provenance);
                    V2Value length = Pop("HCCIL1402", "newarr length stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (length.Type != RestrictedCilTypeV1.Int32)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1403", "newarr length must be int32.", identity, provenance);
                    if (!arrays.ContainsKey(instruction.Offset) && ++callCount > _budgets.MaximumCalls)
                        return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    arrays[instruction.Offset] = binding;
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.ObjectReference, identity));
                    return null;
                }
            case 0x8e:
                {
                    V2Value array = Pop("HCCIL1404", "ldlen evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (array.Type != RestrictedCilTypeV1.ObjectReference)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1405", "ldlen requires an object reference.", identity, provenance);
                    if (enforceConstructorReferences && !MatchesConstructorArrayReference(array.ConstructorReference, instruction.Encoding))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844", "ldlen operand lacks an exact array declaration fact.", identity, provenance);
                    if (++callCount > _budgets.MaximumCalls) return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    state.Stack.Add(V2Value.Definition(RestrictedCilTypeV1.NativeUInt, identity));
                    return null;
                }
            case 0x90:
            case 0x91:
            case 0x92:
            case 0x93:
            case 0x94:
            case 0x95:
            case 0x96:
            case 0x9a:
                {
                    V2Value index = Pop("HCCIL1406", "ldelem evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value array = Pop("HCCIL1406", "ldelem evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    if (!IsI4ArrayIndex(index.Type) || array.Type != RestrictedCilTypeV1.ObjectReference)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1407", "ldelem requires object-reference and an I4 index carrier.", identity, provenance);
                    if (enforceConstructorReferences && !MatchesConstructorArrayReference(array.ConstructorReference, instruction.Encoding))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1844", "ldelem operand declaration does not match its signed/width carrier.", identity, provenance);
                    if (++callCount > _budgets.MaximumCalls) return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    RestrictedCilTypeV1 resultType = instruction.Encoding switch
                    {
                        0x95 => RestrictedCilTypeV1.UInt32,
                        0x96 => RestrictedCilTypeV1.Int64,
                        0x9a => RestrictedCilTypeV1.ObjectReference,
                        _ => RestrictedCilTypeV1.Int32
                    };
                    state.Stack.Add(V2Value.Definition(resultType, identity));
                    return null;
                }
            case 0x9c:
            case 0x9d:
            case 0x9e:
            case 0xa2:
                {
                    V2Value value = Pop("HCCIL1406", "stelem evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value index = Pop("HCCIL1406", "stelem evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    V2Value array = Pop("HCCIL1406", "stelem evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    RestrictedCilTypeV1 expected = instruction.Encoding != 0xa2 ? RestrictedCilTypeV1.Int32 : RestrictedCilTypeV1.ObjectReference;
                    bool exactI4Value = StackCompatible(value.Type, expected) ||
                        instruction.Encoding == 0x9e && value.Type == RestrictedCilTypeV1.UInt32;
                    if (!IsI4ArrayIndex(index.Type) || array.Type != RestrictedCilTypeV1.ObjectReference || !exactI4Value)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1407", "stelem operands do not match the selected element opcode.", identity, provenance);
                    if (++callCount > _budgets.MaximumCalls) return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    return null;
                }
            case 0xfe15:
                {
                    if (instruction.ArrayZero is null)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1860", "initobj requires an exact immediately consumed array-element destination; general managed-byref storage is not qualified.", identity, provenance);
                    RestrictedCilArrayTypeBindingV1? binding = ResolveArrayZero(instruction);
                    if (binding is null)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1862", "Array initobj requires exact PE-bound reference-free element and SZARRAY descriptors/type handle.", identity, provenance);
                    V2Value index = Pop("HCCIL1861", "Array initobj index stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value array = Pop("HCCIL1861", "Array initobj reference stack underflow.", out failure);
                    if (failure is not null) return failure;
                    if (!IsI4ArrayIndex(index.Type) || array.Type != RestrictedCilTypeV1.ObjectReference)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1861", "Array initobj requires an object reference and I4 index.", identity, provenance);
                    return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1863",
                        "Exact array-element initobj is validated, but managed ABI v1.20 has no native zero-value helper definition; IR/publication remain closed.",
                        identity, provenance);
                }
            case 0x8f:
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1408", "ldelema requires managed interior-reference lifetime and root-map semantics that are not qualified.", identity, provenance);
            case 0x8c:
            case 0xa5:
                {
                    if (!_valueTypeBindings.TryGetValue(instruction.Token, out RestrictedCilValueTypeBindingV1? binding) || !ValidValueBinding(binding))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1420", "box/unbox.any requires an exact fixed-layout blittable scalar binding.", identity, provenance);
                    V2Value source = Pop("HCCIL1421", "box/unbox.any evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    RestrictedCilTypeV1 expected = instruction.Encoding == 0x8c ? binding.ValueType : RestrictedCilTypeV1.ObjectReference;
                    if (!StackCompatible(source.Type, expected)) return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1422", "box/unbox.any input type does not match its binding.", identity, provenance);
                    if (!values.ContainsKey(instruction.Offset) && ++callCount > _budgets.MaximumCalls) return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                    values[instruction.Offset] = binding;
                    state.Stack.Add(V2Value.Definition(instruction.Encoding == 0x8c ? RestrictedCilTypeV1.ObjectReference : binding.ValueType, identity));
                    return null;
                }
            case 0x7e:
            case 0x80:
                {
                    FieldResolution resolution = ResolveField(metadata, instruction.Token, requireStatic: true, instruction.Projection);
                    if (resolution.Failure is not null) return resolution.Failure with { Provenance = provenance };
                    RestrictedCilFieldLayoutBindingV1 field = resolution.Field!;
                    if (!_typeInitializationBindings.TryGetValue(field.DeclaringType, out RestrictedCilTypeInitializationBindingV1? init) || !ValidInitializationBinding(init, field.TypeDescriptor))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1430", "Static field access requires an exact type-initialization binding.", identity, provenance);
                    if (field.FieldType is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.ObjectReference))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1433", "Phase 04 static field helpers support exact i4 and object-reference layouts only.", identity, provenance);
                    if (instruction.Encoding == 0x80)
                    {
                        V2Value value = Pop("HCCIL1431", "stsfld evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                        if (failure is not null) return failure;
                        if (!ExactStackCompatible(value, field.FieldType, resolution.AggregateIdentity))
                            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1432", "stsfld value does not match the runtime field layout.", identity, provenance);
                    }
                    else state.Stack.Add(V2Value.Definition(field.FieldType, identity) with
                    { AggregateIdentity = resolution.AggregateIdentity });
                    if (!fields.ContainsKey(instruction.Offset))
                    {
                        if (callCount > _budgets.MaximumCalls - 2) return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "Static field helper calls exhaust the call budget.", identity, provenance);
                        callCount += 2;
                    }
                    fields[instruction.Offset] = field;
                    return null;
                }
            case 0x7b:
                {
                    FieldResolution resolution = method.Receiver is null ? ResolveField(metadata, instruction.Token, requireStatic: false, allowAggregateAnalysis: true) :
                        ResolveReceiverField(metadata, instruction.Token, method, false, provenance);
                    if (resolution.Failure is not null) return resolution.Failure with { Provenance = provenance };
                    V2Value receiver = Pop("HCCIL1201", "ldfld evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    bool scalarReceiver = method.Receiver is null && ValidScalarValueReceiver(receiver, resolution.Field!);
                    if (method.Receiver is not null ? !ValidReceiverUse(receiver, method) :
                        !scalarReceiver && (receiver.Type != RestrictedCilTypeV1.ObjectReference || resolution.Field!.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.ValueType))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1202",
                            $"ldfld receiver must match its exact object or typed payload loan; carrier={receiver.Type}, " +
                            $"aggregate='{receiver.AggregateIdentity ?? "<none>"}', owner='{resolution.Field!.TypeDescriptor.StableIdentity}'.",
                            identity, provenance);
                    RestrictedCilFieldLayoutBindingV1 field = resolution.Field!;
                    state.Stack.Add(V2Value.Definition(StackType(field.FieldType), identity) with { AggregateIdentity = resolution.AggregateIdentity });
                    fields[instruction.Offset] = scalarReceiver ? field with { IsScalarValueProjection = true } : field;
                    return null;
                }
            case 0x7d:
                {
                    FieldResolution resolution = method.Receiver is null ? ResolveField(metadata, instruction.Token, requireStatic: false, allowAggregateAnalysis: true) :
                        ResolveReceiverField(metadata, instruction.Token, method, true, provenance);
                    if (resolution.Failure is not null) return resolution.Failure with { Provenance = provenance };
                    V2Value value = Pop("HCCIL1203", "stfld evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value receiver = Pop("HCCIL1203", "stfld evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    RestrictedCilFieldLayoutBindingV1 field = resolution.Field!;
                    if ((method.Receiver is not null ? !ValidReceiverUse(receiver, method) :
                        receiver.Type != RestrictedCilTypeV1.ObjectReference || field.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.ValueType) ||
                        !ExactStackCompatible(value, field.FieldType, resolution.AggregateIdentity))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1204", "stfld receiver or value type is incompatible with the runtime layout.", identity, provenance);
                    if (method.Receiver is null && field.FieldType != RestrictedCilTypeV1.Aggregate &&
                        field.TypeDescriptor.InstanceFields.Single(row => row.Identity == field.FieldName).SizeBytes is not (1 or 2 or 4 or 8))
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1210",
                            $"Instance field store '{field.DeclaringType}::{field.FieldName}' requires qualified scalar storage width " +
                            $"{field.TypeDescriptor.InstanceFields.Single(row => row.Identity == field.FieldName).SizeBytes} bytes.", identity, provenance);
                    fields[instruction.Offset] = field;
                    return null;
                }
            case 0x7f:
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1820",
                    "ldsflda requires typed static managed-byref provenance/lifetime; no exact scalar getter projection applies.", identity, provenance);
            case 0xa4:
                {
                    string? expected = AggregateIdentity(metadata, MetadataTokens.EntityHandle(instruction.Token));
                    if (expected is null)
                        return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1812",
                            "Typed stelem requires an exact non-generic value-type identity in aggregate analysis.", identity, provenance);
                    V2Value value = Pop("HCCIL1406", "stelem evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    V2Value index = Pop("HCCIL1406", "stelem evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    V2Value array = Pop("HCCIL1406", "stelem evaluation-stack underflow.", out failure);
                    if (failure is not null) return failure;
                    return array.Type == RestrictedCilTypeV1.ObjectReference && IsI4ArrayIndex(index.Type) &&
                        ExactStackCompatible(value, RestrictedCilTypeV1.Aggregate, expected) ? null :
                        Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1813",
                            $"stelem requires the exact aggregate '{expected}', an array reference and an int32 index.", identity, provenance);
                }
            case 0x7a:
                {
                    V2Value exception = Pop("HCCIL0882", "throw evaluation-stack underflow.", out RestrictedCilImportResultV1? failure);
                    if (failure is not null) return failure;
                    if (exception.Type != RestrictedCilTypeV1.ObjectReference)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0883",
                            "throw requires an object-reference operand.", identity, provenance);
                    state.Stack.Clear();
                    return null;
                }
            case 0x2a:
                if (method.ReturnType == RestrictedCilTypeV1.Void)
                    return state.Stack.Count == 0 ? null : Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0018", "Void return requires an empty evaluation stack.", identity, provenance);
                V2Value returned = Pop("HCCIL0019", "Return value does not match the method signature.", out RestrictedCilImportResultV1? returnFailure);
                bool exactInt64ValueReturn = method.ReturnType != RestrictedCilTypeV1.Int64 || method.AggregateReturn is null ||
                    !IsProvenInt64ScalarIdentity(metadata, method.AggregateReturn) ||
                    returned.AggregateIdentity == method.AggregateReturn;
                bool exactNullableReturn = method.AggregateReturn != NullableInt32Identity ||
                    _nullableInt32ReturnIngress && returned.AggregateIdentity == NullableInt32Identity;
                if (returnFailure is not null || !exactNullableReturn || !ExactStackCompatible(returned, method.ReturnType, method.AggregateReturn) ||
                    !exactInt64ValueReturn || state.Stack.Count != 0)
                    return returnFailure ?? Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0019", "Return value does not match the method signature.", identity, provenance);
                return null;
            default:
                return Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL0020", "The verified decoder produced an unknown semantic opcode.", identity, provenance);
        }
    }

    private V2StateMerge MergeV2Entry(
        V2Block target,
        IReadOnlyList<V2Block> blocks,
        MethodSignature signature,
        LocalSignature locals,
        IDictionary<(int Block, string Slot), V2Phi> phis,
        RestrictedCilProvenanceV1 provenance)
    {
        V2Block[] predecessors = target.Predecessors.Select(id => blocks[id]).Where(static block => block.Exit is not null).OrderBy(static block => block.Id).ToArray();
        if (predecessors.Length == 0) return new(null, null);
        int stackCount = predecessors[0].Exit!.Stack.Count;
        if (predecessors.Any(block => block.Exit!.Stack.Count != stackCount))
            return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1103", "Evaluation-stack heights differ at a CFG join.", BlockIdentity(provenance, target), provenance));
        V2Value[] stack = new V2Value[stackCount];
        V2Value[] mergedLocals = new V2Value[locals.Types.Count];
        V2Value[] mergedArguments = new V2Value[signature.Parameters.Count];
        for (int slot = 0; slot < stackCount; slot++)
        {
            V2ValueMerge merge = MergeV2Values(target, predecessors, $"stack:{slot}", block => block.Exit!.Stack[slot], phis, provenance);
            if (merge.Failure is not null) return new(null, merge.Failure);
            stack[slot] = merge.Value;
        }
        for (int local = 0; local < locals.Types.Count; local++)
        {
            V2ValueMerge merge = MergeV2Values(target, predecessors, $"local:{local}", block => block.Exit!.Locals[local], phis, provenance);
            if (merge.Failure is not null) return new(null, merge.Failure);
            mergedLocals[local] = merge.Value;
        }
        for (int argument = 0; argument < signature.Parameters.Count; argument++)
        {
            V2ValueMerge merge = MergeV2Values(target, predecessors, $"arg:{argument}", block => block.Exit!.Arguments[argument], phis, provenance);
            if (merge.Failure is not null) return new(null, merge.Failure);
            mergedArguments[argument] = merge.Value;
        }
        return new(new(stack, mergedLocals, mergedArguments), null);
    }

    private V2ValueMerge MergeV2Values(
        V2Block target,
        IReadOnlyList<V2Block> predecessors,
        string slot,
        Func<V2Block, V2Value> select,
        IDictionary<(int Block, string Slot), V2Phi> phis,
        RestrictedCilProvenanceV1 provenance)
    {
        V2Value[] values = predecessors.Select(select).ToArray();
        if (values.Any(static value => !value.Initialized))
        {
            phis.Remove((target.Id, slot));
            return new(V2Value.Uninitialized, null);
        }
        RestrictedCilTypeV1 type = values[0].Type;
        if (values.Any(value => value.Type != type || value.AggregateIdentity != values[0].AggregateIdentity ||
                value.ReceiverIdentity != values[0].ReceiverIdentity ||
                value.ReceiverStorageSlotIdentity != values[0].ReceiverStorageSlotIdentity) ||
            type == RestrictedCilTypeV1.Aggregate && values[0].AggregateIdentity is null)
            return new(V2Value.Uninitialized, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1104",
                $"Exact CIL types differ at a CFG join: {string.Join(';', values.Select(static value => $"{value.Type}:{value.Id}:aggregate={value.AggregateIdentity ?? "null"}:receiver={value.ReceiverIdentity ?? "null"}:slot={value.ReceiverStorageSlotIdentity ?? "null"}"))}.",
                $"{BlockIdentity(provenance, target)}:{slot}", provenance));
        // Null is compatible with one exact declared carrier, never with an unknown
        // or a heterogeneous pair. Facts exist only in the isolated constructor proof.
        ConstructorReferenceKind joinedReference = ConstructorReferenceKind.Null;
        foreach (V2Value value in values) {
            if (value.ConstructorReference == ConstructorReferenceKind.Null) continue;
            if (joinedReference == ConstructorReferenceKind.Null) joinedReference = value.ConstructorReference;
            else if (joinedReference != value.ConstructorReference) { joinedReference = ConstructorReferenceKind.None; break; }
        }
        V2Value? nullableFlag = null;
        if (IsWideNullable(values[0].AggregateIdentity)) {
            if (values.Any(value => value.NullableHasValue is null))
                return new(V2Value.Uninitialized, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1845", "Nullable I8 join lacks its presence component.", BlockIdentity(provenance, target), provenance));
            V2ValueMerge flag = MergeV2Values(target, predecessors, slot + ":nullable-presence", block => select(block).NullableHasValue!, phis, provenance);
            if (flag.Failure is not null) return flag;
            nullableFlag = flag.Value;
        }
        V2Value ReferenceValue(V2Value value) => value with { ConstructorReference = joinedReference, NullableHasValue = nullableFlag };
        if (phis.TryGetValue((target.Id, slot), out V2Phi? existingPhi))
        {
            foreach ((V2Block predecessor, V2Value value) in predecessors.Zip(values))
                existingPhi.Incoming[predecessor.Id] = value;
            return new(ReferenceValue(existingPhi.Value), null);
        }
        if (values.All(value => value.Id == values[0].Id)) return new(ReferenceValue(values[0]), null);
        if (!phis.TryGetValue((target.Id, slot), out V2Phi? phi))
        {
            string id = $"cil:{provenance.CanonicalMethodLocalIdentity}:phi:b{target.Id}:{slot.Replace(':', '_')}";
            phi = new(id, target.Id, slot, type, values[0].AggregateIdentity, values[0].ReceiverIdentity,
                values[0].ReceiverStorageSlotIdentity);
            phis.Add((target.Id, slot), phi);
        }
        foreach ((V2Block predecessor, V2Value value) in predecessors.Zip(values)) phi.Incoming[predecessor.Id] = value;
        return new(ReferenceValue(phi.Value), null);
    }

    private V2LoopBuild AnalyzeV2Loops(V2Graph graph, RestrictedCilProvenanceV1 provenance)
    {
        Dictionary<int, HashSet<int>> dominators = ComputeV2Dominators(graph.Blocks);
        (int Source, int Target)[] backedges = graph.Blocks.SelectMany(block => block.Successors.Select(successor => (Source: block.Id, Target: successor)))
            .Where(edge => dominators[edge.Source].Contains(edge.Target)).OrderBy(static edge => edge.Target).ThenBy(static edge => edge.Source).ToArray();
        foreach (int[] component in StronglyConnectedV2(graph.Blocks).Where(component => component.Length > 1 || graph.Blocks[component[0]].Successors.Contains(component[0])))
        {
            HashSet<int> members = component.ToHashSet();
            int[] entries = component.Where(id => graph.Blocks[id].Predecessors.Any(predecessor => !members.Contains(predecessor))).Order().ToArray();
            if ((members.Contains(0) && entries.Length != 0) || (!members.Contains(0) && entries.Length != 1))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1201", "Irreducible control flow is outside ScalarControlFlowV2.", provenance.MethodIdentity, provenance));
        }
        SortedDictionary<int, SortedSet<int>> byHeader = new SortedDictionary<int, SortedSet<int>>();
        foreach ((int source, int header) in backedges)
        {
            if (!byHeader.TryGetValue(header, out SortedSet<int>? latches)) byHeader.Add(header, latches = []);
            latches.Add(source);
        }
        List<(int Header, int[] Latches, int[] Blocks)> raw = new List<(int Header, int[] Latches, int[] Blocks)>();
        foreach ((int header, SortedSet<int> latches) in byHeader)
        {
            SortedSet<int> members = new SortedSet<int> { header };
            Stack<int> pending = new Stack<int>(latches.Reverse());
            while (pending.Count != 0)
            {
                int current = pending.Pop();
                if (!members.Add(current)) continue;
                foreach (int predecessor in graph.Blocks[current].Predecessors.OrderByDescending(static id => id)) pending.Push(predecessor);
            }
            raw.Add((header, latches.ToArray(), members.ToArray()));
        }
        ScalarControlFlowV2LoopV1[] loops = raw.Select(loop =>
        {
            int? parent = raw.Where(other => other.Header != loop.Header && loop.Blocks.All(other.Blocks.Contains))
                .OrderBy(static other => other.Blocks.Length).ThenBy(static other => other.Header).Select(static other => (int?)other.Header).FirstOrDefault();
            string id = $"cil:{provenance.CanonicalMethodLocalIdentity}:loop:h{loop.Header}:l{string.Join('_', loop.Latches)}";
            return new ScalarControlFlowV2LoopV1(id, loop.Header, loop.Latches, loop.Blocks, parent, true);
        }).OrderBy(static loop => loop.HeaderBlockId).ToArray();
        return new(loops, null);
    }

    private ScalarControlFlowV2AnalysisV1 CreateV2Analysis(
        V2Graph graph,
        V2Dataflow dataflow,
        IReadOnlyList<ScalarControlFlowV2LoopV1> loops,
        RestrictedCilProvenanceV1 provenance)
    {
        HashSet<(int latch, int HeaderBlockId)> backedges = loops.SelectMany(loop => loop.LatchBlockIds.Select(latch => (latch, loop.HeaderBlockId))).ToHashSet();
        ScalarControlFlowV2BlockV1[] blocks = graph.Blocks.Select(block => new ScalarControlFlowV2BlockV1(
            block.Id, block.StartOffset, block.EndOffsetExclusive, block.Predecessors.ToArray(), block.Successors.ToArray(),
            StateDigest(block.Entry!), StateDigest(block.Exit!))).ToArray();
        ScalarControlFlowV2EdgeV1[] edges = graph.Blocks.SelectMany(block => block.Successors.Select(target =>
        {
            bool critical = block.Successors.Count > 1 && graph.Blocks[target].Predecessors.Count > 1;
            ScalarControlFlowV2EdgeKindV1 kind = backedges.Contains((block.Id, target)) ? ScalarControlFlowV2EdgeKindV1.Backedge
                : critical ? ScalarControlFlowV2EdgeKindV1.CriticalSplit
                : target == block.Id + 1 ? ScalarControlFlowV2EdgeKindV1.Fallthrough : ScalarControlFlowV2EdgeKindV1.Branch;
            return new ScalarControlFlowV2EdgeV1($"b{block.Id}->b{target}", block.Id, target, kind, critical);
        })).OrderBy(static edge => edge.SourceBlockId).ThenBy(static edge => edge.TargetBlockId).ToArray();
        ScalarControlFlowV2PhiV1[] phis = dataflow.Phis.Select(phi => new ScalarControlFlowV2PhiV1(phi.Id, phi.BlockId, phi.Slot, phi.Type,
            phi.Incoming.OrderBy(static item => item.Key).Select(static item => new ScalarControlFlowV2PhiIncomingV1(item.Key, item.Value.Id)).ToArray())).ToArray();
        ScalarControlFlowV2ParallelCopyV1[] copies = PlanV2ParallelCopies(graph, dataflow.Phis, provenance);
        string graphDigest = Hash(string.Join('|', blocks.Select(static block => $"{block.Id}:{block.StartOffset}:{block.EndOffsetExclusive}:{string.Join(',', block.PredecessorIds)}:{string.Join(',', block.SuccessorIds)}")));
        string stateDigest = Hash(string.Join('|', blocks.Select(static block => $"{block.Id}:{block.EntryStateDigest}:{block.ExitStateDigest}")));
        string ssaDigest = Hash(string.Join('|', phis.Select(phi => $"{phi.StableId}:{phi.Type}:{string.Join(',', phi.Incoming.Select(static incoming => $"{incoming.SourceBlockId}={incoming.ValueId}"))}")));
        return new("hybridcpu.cil-cfg-ssa/v1", 1, 1, provenance.CanonicalMethodLocalIdentity, blocks, edges, phis, loops, copies,
            graphDigest, stateDigest, ssaDigest, ControlFlowV2ContractDigest);
    }

    private static ScalarControlFlowV2ParallelCopyV1[] PlanV2ParallelCopies(V2Graph graph, IReadOnlyList<V2Phi> phis, RestrictedCilProvenanceV1 provenance)
    {
        List<ScalarControlFlowV2ParallelCopyV1> result = new List<ScalarControlFlowV2ParallelCopyV1>();
        foreach (IGrouping<(int Source, int Target), (int Source, int Target, string From, string To)>? edgeGroup in phis.SelectMany(phi => phi.Incoming.Select(item => (Source: item.Key, Target: phi.BlockId, From: item.Value.Id, To: phi.Id)))
                     .GroupBy(static item => (item.Source, item.Target)).OrderBy(static group => group.Key.Source).ThenBy(static group => group.Key.Target))
        {
            bool split = graph.Blocks[edgeGroup.Key.Source].Successors.Count > 1 && graph.Blocks[edgeGroup.Key.Target].Predecessors.Count > 1;
            List<(int Source, int Target, string From, string To)> moves = edgeGroup.Where(static move => move.From != move.To).OrderBy(static move => move.To, StringComparer.Ordinal).ToList();
            int sequence = 0;
            while (moves.Count != 0)
            {
                int ready = moves.FindIndex(move => moves.All(other => other.From != move.To));
                if (ready >= 0)
                {
                    (int Source, int Target, string From, string To) move = moves[ready];
                    moves.RemoveAt(ready);
                    result.Add(new($"cil:{provenance.CanonicalMethodLocalIdentity}:copy:b{move.Source}:b{move.Target}:{sequence}", move.Source, move.Target, move.From, move.To, sequence++, false, split));
                    continue;
                }
                (int Source, int Target, string From, string To) cycle = moves[0];
                string temporary = $"cil:{provenance.CanonicalMethodLocalIdentity}:pcopy-temp:b{cycle.Source}:b{cycle.Target}:{sequence}";
                result.Add(new($"cil:{provenance.CanonicalMethodLocalIdentity}:copy:b{cycle.Source}:b{cycle.Target}:{sequence}", cycle.Source, cycle.Target, cycle.From, temporary, sequence++, true, split));
                for (int index = 0; index < moves.Count; index++)
                    if (moves[index].From == cycle.From) moves[index] = moves[index] with { From = temporary };
            }
        }
        return result.ToArray();
    }

    private RestrictedCilImportResultV1 MapControlFlowV2(
        MetadataReader metadata,
        V2Graph graph,
        V2Dataflow dataflow,
        MethodSignature signature,
        LocalSignature locals,
        RestrictedCilProvenanceV1 provenance,
        ScalarControlFlowV2AnalysisV1 analysis,
        IReadOnlyDictionary<int, ManagedReceiverCallerStoragePlanV1> receiverStorage,
        ManagedEhHomeAccessPlanV1? ehHomeAccesses = null,
        IReadOnlyList<ManagedEhHandlerEntryV1>? ehHandlerEntries = null,
        ManagedEhMethodPlanV1? ehPlan = null)
    {
        // Mapping is deliberately shared with the Canonical/Core path. The legacy mapper cannot
        // represent loop-carried state, so the Phase 01 mapper is implemented below as a second
        // front-end projection, not as a scheduler or backend.
        return MapV2ToCanonicalIr(metadata, graph, dataflow, signature, locals, provenance, analysis,
            receiverStorage, ehHomeAccesses, ehHandlerEntries, ehPlan);
    }

    private RestrictedCilImportResultV1 MapV2ToCanonicalIr(
        MetadataReader metadata,
        V2Graph graph,
        V2Dataflow dataflow,
        MethodSignature signature,
        LocalSignature locals,
        RestrictedCilProvenanceV1 provenance,
        ScalarControlFlowV2AnalysisV1 analysis,
        IReadOnlyDictionary<int, ManagedReceiverCallerStoragePlanV1> receiverStorage,
        ManagedEhHomeAccessPlanV1? ehHomeAccesses = null,
        IReadOnlyList<ManagedEhHandlerEntryV1>? ehHandlerEntries = null,
        ManagedEhMethodPlanV1? ehPlan = null)
    {
        List<Emission> emissions = new List<Emission>();
        Dictionary<(int Offset, string Slot), V2Value> mappedHomeValues = new Dictionary<(int Offset, string Slot), V2Value>();
        bool initializedFinallyToken = false;
        foreach (V2Block block in graph.Blocks)
        {
            V2State state = block.Entry!.Clone();
            for (int instructionIndex = block.StartInstructionIndex; instructionIndex <= block.EndInstructionIndex; instructionIndex++)
            {
                DecodedInstruction source = graph.Instructions[instructionIndex];
                string identity = OffsetIdentity(provenance, source.Offset);
                if (!initializedFinallyToken && ehPlan?.Clauses.Any(static clause =>
                        clause.Kind == HybridCpuManagedEhClauseKindV1.Finally) == true)
                {
                    IrOperand sp = new IrOperand(IrOperandKind.ArchitecturalRegister, 2, identity + ":finally-token-init-sp");
                    IrOperand zero = new IrOperand(IrOperandKind.ArchitecturalRegister, 0, identity + ":finally-token-init-zero");
                    emissions.Add(new(source.Offset, HybridCpuOpcode.SD, RestrictedCilTypeV1.NativeUInt,
                        [sp, zero], [], -1, identity + ":finally-token-init", null,
                        IrMemoryEffectKind.Write, false, ManagedEhFrameHomesV1.FinallyContinuationTokenSlot));
                    initializedFinallyToken = true;
                }
                V2Value Pop()
                {
                    V2Value value = state.Stack[^1];
                    state.Stack.RemoveAt(state.Stack.Count - 1);
                    return value;
                }
                bool WordPair(V2Value left, V2Value right) =>
                    (left.Type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32) &&
                    (right.Type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32);
                V2Value ComparisonWord(V2Value value, bool unsigned, string suffix)
                {
                    // CIL compares logical low32 bits. Native comparisons use full
                    // architectural registers, whose upper bits may differ after ALU operations.
                    if(value.Type is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32))return value;
                    RestrictedCilTypeV1 type=unsigned?RestrictedCilTypeV1.UInt64:RestrictedCilTypeV1.Int64;
                    string name=identity+":word-compare:"+suffix;
                    if(value.Operand.Kind==IrOperandKind.Constant)return V2Value.Constant(unsigned
                        ? (long)unchecked((uint)value.Operand.Value) : unchecked((int)value.Operand.Value),type,name);
                    V2Value result=V2Value.Definition(type,name);
                    if(unsigned){
                        V2Value upper=V2Value.Definition(RestrictedCilTypeV1.UInt64,name+":upper");
                        emissions.Add(new(source.Offset,HybridCpuOpcode.SLLI,RestrictedCilTypeV1.UInt64,
                            [value.Operand,new(IrOperandKind.Constant,32,name+":32")],[upper.Operand],-1,name+":upper"));
                        emissions.Add(new(source.Offset,HybridCpuOpcode.SRLI,RestrictedCilTypeV1.UInt64,
                            [upper.Operand,new(IrOperandKind.Constant,32,name+":32")],[result.Operand],-1,name));
                    }else emissions.Add(new(source.Offset,HybridCpuOpcode.ADDIW,RestrictedCilTypeV1.Int64,
                        [value.Operand,new(IrOperandKind.Constant,0,name+":zero")],[result.Operand],-1,name));
                    return result;
                }
                void StoreLocal(int local)
                {
                    V2Value value = Pop();
                    if (value.Type == RestrictedCilTypeV1.UInt32 && locals.Types[local] == RestrictedCilTypeV1.Int32)
                    {
                        V2Value normalized = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":i4-local");
                        emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                            [value.Operand, new(IrOperandKind.Constant, 0, identity + ":i4-local-zero")],
                            [normalized.Operand], -1, identity + ":i4-local"));
                        state.Locals[local] = normalized;
                        return;
                    }
                    state.Locals[local] = value;
                }
                V2Value? EmitRuntimeCall(string suffix, string symbol, IReadOnlyList<V2Value> arguments,
                    RestrictedCilTypeV1 returnType, IrMemoryEffectKind memoryEffects, string? resultIdentity = null)
                {
                    V2Value[] abiArguments = arguments.Select((argument, parameter) =>
                        V2Value.Definition(argument.Type, $"{identity}:{suffix}:call-arg-abi:{parameter}")).ToArray();
                    for (int parameter = 0; parameter < arguments.Count; parameter++)
                    {
                        V2Value argument = arguments[parameter];
                        IrOperand[] uses = argument.Operand.Kind == IrOperandKind.Constant
                            ? [new(IrOperandKind.ArchitecturalRegister, 0, $"{identity}:{suffix}:zero:{parameter}"), argument.Operand]
                            : [argument.Operand, new(IrOperandKind.Constant, 0, $"{identity}:{suffix}:zero:{parameter}")];
                        emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, argument.Type, uses,
                            [abiArguments[parameter].Operand], -1, $"{identity}:{suffix}:arg-copy:{parameter}"));
                    }
                    V2Value? abiResult = returnType == RestrictedCilTypeV1.Void ? null :
                        V2Value.Definition(returnType, $"{identity}:{suffix}:call-result-abi");
                    emissions.Add(new(source.Offset, HybridCpuOpcode.JAL,
                        returnType == RestrictedCilTypeV1.Void ? RestrictedCilTypeV1.NativeUInt : returnType,
                        abiArguments.Select(static item => item.Operand).ToArray(), abiResult is null ? [] : [abiResult.Operand],
                        -1, $"{identity}:{suffix}:call", symbol, memoryEffects));
                    if (abiResult is null) return null;
                    V2Value result = V2Value.Definition(StackType(returnType), resultIdentity ?? $"{identity}:{suffix}:result");
                    // Keep the helper ABI narrow, but CIL loads small return values as I4.
                    // Explicit extension also avoids depending on unspecified upper ABI bits.
                    if (StackType(returnType) != returnType)
                    {
                        if (returnType is not (RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.Int16))
                        {
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ANDI, RestrictedCilTypeV1.Int32,
                                [abiResult.Operand, new(IrOperandKind.Constant,
                                    returnType == RestrictedCilTypeV1.UInt16 ? 0xffffUL : 0xffUL,
                                    $"{identity}:{suffix}:return-mask")],
                                [result.Operand], -1, $"{identity}:{suffix}:result-copy"));
                            return result;
                        }
                        ulong shift = returnType is RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16 ? 48UL : 56UL;
                        V2Value shifted = V2Value.Definition(RestrictedCilTypeV1.Int32, $"{identity}:{suffix}:return-left");
                        emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int32,
                            [abiResult.Operand, new(IrOperandKind.Constant, shift, $"{identity}:{suffix}:return-left-count")],
                            [shifted.Operand], -1, $"{identity}:{suffix}:return-left"));
                        emissions.Add(new(source.Offset,
                            HybridCpuOpcode.SRAI,
                            RestrictedCilTypeV1.Int32,
                            [shifted.Operand, new(IrOperandKind.Constant, shift, $"{identity}:{suffix}:return-right-count")],
                            [result.Operand], -1, $"{identity}:{suffix}:result-copy"));
                        return result;
                    }
                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, returnType,
                        [abiResult.Operand, new(IrOperandKind.Constant, 0, $"{identity}:{suffix}:result-zero")],
                        [result.Operand], -1, $"{identity}:{suffix}:result-copy"));
                    return result;
                }
                    switch (source.Encoding)
                    {
                    case NullableInt32New:
                    case NullableInt32Initialize:
                    case NullableInitializeStack:
                        {
                            V2Value value = source.Encoding is NullableInt32New or NullableInitializeStack ? Pop() : NullableDefaultValue(source.NullableDefault!, state);
                            if (source.NullableWide) {
                                V2Value wide = value with { Type = RestrictedCilTypeV1.Int64, AggregateIdentity = NullableIdentity(source), NullableHasValue = V2Value.Constant(1, RestrictedCilTypeV1.Int32, identity) };
                                if (source.Encoding == NullableInt32New) state.Stack.Add(wide);
                                else state.Locals[(int)source.Literal] = wide;
                                break;
                            }
                            V2Value left = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":nullable-left");
                            V2Value bits = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":nullable-bits");
                            V2Value packed = V2Value.Definition(RestrictedCilTypeV1.Int64, identity) with { AggregateIdentity = NullableIdentity(source) };
                            emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int64,
                                [value.Operand, new(IrOperandKind.Constant, (ulong)(source.NullableNarrowBits != 0 ? 64 - source.NullableNarrowBits : 32), identity + ":left-shift")], [left.Operand], -1, identity + ":nullable-left"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.SRLI, RestrictedCilTypeV1.Int64,
                                [left.Operand, new(IrOperandKind.Constant, (ulong)(source.NullableNarrowBits != 0 ? 63 - source.NullableNarrowBits : 31), identity + ":right-shift")], [bits.Operand], -1, identity + ":nullable-bits"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ORI, RestrictedCilTypeV1.Int64,
                                [bits.Operand, new(IrOperandKind.Constant, 1, identity + ":present")], [packed.Operand], -1, identity + ":nullable-pack"));
                            if (source.Encoding == NullableInt32New) state.Stack.Add(packed);
                            else if (source.NullableParameter) state.Arguments[(int)source.Literal] = packed;
                            else state.Locals[(int)source.Literal] = packed;
                            break;
                        }
                    case NullableInt32Zero:
                        if (source.NullableParameter) {
                            state.Arguments[(int)source.Literal] = V2Value.Constant(0, RestrictedCilTypeV1.Int64, identity) with { AggregateIdentity = NullableInt32Identity };
                            break;
                        }
                        state.Locals[(int)source.Literal] = V2Value.Constant(0, RestrictedCilTypeV1.Int64, identity) with {
                            AggregateIdentity = NullableIdentity(source),
                            NullableHasValue = source.NullableWide ? V2Value.Constant(0, RestrictedCilTypeV1.Int32, identity) : null };
                        break;
                    case NullableInt32ParameterValue:
                    case NullableInt32ParameterPresence:
                        {
                            V2Value packed = state.Arguments[(int)source.Literal];
                            if (source.Encoding == NullableInt32ParameterPresence) {
                                V2Value presence = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                                emissions.Add(new(source.Offset, HybridCpuOpcode.AND, RestrictedCilTypeV1.Int32,
                                    [packed.Operand, new(IrOperandKind.Constant, 1, identity + ":present")], [presence.Operand], -1, identity));
                                state.Stack.Add(presence);
                                break;
                            }
                            V2Value bits = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":nullable-unpack");
                            V2Value value = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                            emissions.Add(new(source.Offset, HybridCpuOpcode.SRL, RestrictedCilTypeV1.Int64,
                                [packed.Operand, new(IrOperandKind.Constant, 1, identity + ":1")], [bits.Operand], -1, identity + ":nullable-unpack"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                                [bits.Operand, new(IrOperandKind.Constant, 0, identity + ":zero")], [value.Operand], -1, identity));
                            state.Stack.Add(value);
                            break;
                        }
                    case NullableInt32Value:
                    case NullableInt32HasValue:
                    case NullableInt32Default:
                    case NullableDefaultStack:
                    case NullableCheckedValue:
                        {
                            V2Value packed = source.NullableParameter ? state.Arguments[(int)source.Literal] : state.Locals[(int)source.Literal];
                            V2Value? defaultInput = source.Encoding == NullableDefaultStack ? Pop() :
                                source.Encoding == NullableInt32Default ? NullableDefaultValue(source.NullableDefault!, state) : null;
                            if (source.NullableNarrowBits != 0 && defaultInput is not null) {
                                V2Value left = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":default-left");
                                V2Value narrow = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":default-u2");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int64,
                                    [defaultInput.Operand, new(IrOperandKind.Constant, (ulong)(64 - source.NullableNarrowBits), identity + ":48")], [left.Operand], -1, identity + ":default-left"));
                                emissions.Add(new(source.Offset, source.NullableUnsigned ? HybridCpuOpcode.SRLI : HybridCpuOpcode.SRAI, RestrictedCilTypeV1.Int64,
                                    [left.Operand, new(IrOperandKind.Constant, (ulong)(64 - source.NullableNarrowBits), identity + ":48")], [narrow.Operand], -1, identity + ":default-u2"));
                                defaultInput = narrow;
                            }
                            if (source.Encoding == NullableCheckedValue) {
                                V2Value payload = packed with { AggregateIdentity = null, NullableHasValue = null };
                                V2Value present;
                                if (source.NullableWide) present = packed.NullableHasValue!;
                                else {
                                    present = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":present");
                                    V2Value bits = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":bits");
                                    V2Value word = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":word");
                                    payload = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":payload");
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ANDI, RestrictedCilTypeV1.Int32,
                                        [packed.Operand, new(IrOperandKind.Constant, 1, identity + ":1")], [present.Operand], -1, identity + ":present"));
                                    if (source.NullableNarrowBits != 0 && !source.NullableUnsigned) {
                                        V2Value shifted = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":signed-left");
                                        emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int64,
                                            [packed.Operand, new(IrOperandKind.Constant, (ulong)(63 - source.NullableNarrowBits), identity + ":47")], [shifted.Operand], -1, identity + ":signed-left"));
                                        emissions.Add(new(source.Offset, HybridCpuOpcode.SRAI, RestrictedCilTypeV1.Int64,
                                            [shifted.Operand, new(IrOperandKind.Constant, (ulong)(64 - source.NullableNarrowBits), identity + ":48")], [bits.Operand], -1, identity + ":unpack"));
                                    } else emissions.Add(new(source.Offset, HybridCpuOpcode.SRLI, RestrictedCilTypeV1.Int64,
                                        [packed.Operand, new(IrOperandKind.Constant, 1, identity + ":1")], [bits.Operand], -1, identity + ":unpack"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                                        [bits.Operand, new(IrOperandKind.Constant, 0, identity + ":0")], [word.Operand], -1, identity + ":word"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.Int64,
                                        [word.Operand, new(IrOperandKind.Constant, 0, identity + ":0")], [payload.Operand], -1, identity + ":payload"));
                                }
                                V2Value checkedValue = EmitRuntimeCall("nullable-value", "__hybridcpu_managed_nullable_value_i8_checked", [payload, present],
                                    RestrictedCilTypeV1.Int64, IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
                                    source.NullableWide ? identity : identity + ":checked")!;
                                if (source.NullableWide) state.Stack.Add(checkedValue);
                                else {
                                    V2Value checkedWord = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                                        [checkedValue.Operand, new(IrOperandKind.Constant, 0, identity + ":0")], [checkedWord.Operand], -1, identity));
                                    state.Stack.Add(checkedWord);
                                }
                                break;
                            }
                            if (source.NullableWide) {
                                if (source.Encoding == NullableInt32HasValue) state.Stack.Add(packed.NullableHasValue!);
                                else if (source.Encoding == NullableInt32Value) state.Stack.Add(packed with { AggregateIdentity = null, NullableHasValue = null });
                                else {
                                    V2Value missing = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":missing");
                                    V2Value fallback = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":fallback");
                                    V2Value resultWide = V2Value.Definition(RestrictedCilTypeV1.Int64, identity);
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.XORI, RestrictedCilTypeV1.Int32,
                                        [packed.NullableHasValue!.Operand, new(IrOperandKind.Constant, 1, identity + ":1")], [missing.Operand], -1, identity + ":missing"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.MUL, RestrictedCilTypeV1.Int64,
                                        [defaultInput!.Operand, missing.Operand], [fallback.Operand], -1, identity + ":fallback"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADD, RestrictedCilTypeV1.Int64,
                                        [packed.Operand, fallback.Operand], [resultWide.Operand], -1, identity));
                                    state.Stack.Add(resultWide);
                                }
                                break;
                            }
                            V2Value result = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                            if (source.Encoding == NullableInt32HasValue) {
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ANDI, RestrictedCilTypeV1.Int32,
                                    [packed.Operand, new(IrOperandKind.Constant, 1, identity + ":present")], [result.Operand], -1, identity));
                            } else {
                                V2Value bits = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":nullable-unpack");
                                if (source.NullableNarrowBits != 0 && !source.NullableUnsigned) {
                                    V2Value shifted = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":signed-left");
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int64,
                                        [packed.Operand, new(IrOperandKind.Constant, (ulong)(63 - source.NullableNarrowBits), identity + ":47")], [shifted.Operand], -1, identity + ":signed-left"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.SRAI, RestrictedCilTypeV1.Int64,
                                        [shifted.Operand, new(IrOperandKind.Constant, (ulong)(64 - source.NullableNarrowBits), identity + ":48")], [bits.Operand], -1, identity + ":nullable-unpack"));
                                } else emissions.Add(new(source.Offset, HybridCpuOpcode.SRLI, RestrictedCilTypeV1.Int64,
                                    [packed.Operand, new(IrOperandKind.Constant, 1, identity + ":1")], [bits.Operand], -1, identity + ":nullable-unpack"));
                                V2Value value = source.Encoding is NullableInt32Default or NullableDefaultStack ? V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":nullable-value") : result;
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                                    [bits.Operand, new(IrOperandKind.Constant, 0, identity + ":zero")], [value.Operand], -1, identity + ":nullable-value"));
                                if (source.Encoding is NullableInt32Default or NullableDefaultStack) {
                                    V2Value present = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":present");
                                    V2Value missing = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":missing");
                                    V2Value fallback = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":fallback");
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ANDI, RestrictedCilTypeV1.Int32,
                                        [packed.Operand, new(IrOperandKind.Constant, 1, identity + ":1")], [present.Operand], -1, identity + ":present"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.XORI, RestrictedCilTypeV1.Int32,
                                        [present.Operand, new(IrOperandKind.Constant, 1, identity + ":1")], [missing.Operand], -1, identity + ":missing"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.MULW, RestrictedCilTypeV1.Int32,
                                        [defaultInput!.Operand, missing.Operand], [fallback.Operand], -1, identity + ":fallback"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDW, RestrictedCilTypeV1.Int32,
                                        [value.Operand, fallback.Operand], [result.Operand], -1, identity));
                                }
                            }
                            state.Stack.Add(result);
                            break;
                        }
                    case 0x00: break;
                    case 0x7a:
                        EmitRuntimeCall("throw", "__hybridcpu_managed_throw", [Pop()], RestrictedCilTypeV1.Void,
                            IrMemoryEffectKind.Read);
                        state.Stack.Clear();
                        break;
                        case 0x26: Pop(); break;
                        case 0x25:
                            state.Stack.Add(state.Stack[^1]);
                            break;
                    case >= 0x02 and <= 0x05:
                        {
                            int argument = source.Encoding - 0x02;
                            state.Stack.Add(state.Arguments[argument]);
                            break;
                        }
                    case 0x0e:
                        {
                            int argument = checked((int)source.Literal);
                            state.Stack.Add(state.Arguments[argument]);
                            break;
                        }
                    case 0x10: state.Arguments[checked((int)source.Literal)] = Pop(); break;
                    case >= 0x06 and <= 0x09: state.Stack.Add(state.Locals[source.Encoding - 0x06]); break;
                    case 0x11: state.Stack.Add(state.Locals[checked((int)source.Literal)]); break;
                    case 0x12:
                        {
                            int local = checked((int)source.Literal);
                            ManagedReceiverCallerStoragePlanV1 storage = receiverStorage[local];
                            V2Value address = V2Value.Definition(RestrictedCilTypeV1.ManagedByRef, identity) with
                            {
                                ReceiverIdentity = storage.ScopedTypeIdentity,
                                ReceiverStorageSlotIdentity = storage.FrameSlotIdentity
                            };
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.ManagedByRef,
                                [new(IrOperandKind.ArchitecturalRegister, 2, identity + ":sp"),
                                 new(IrOperandKind.Constant, 0, identity + ":unresolved-frame-offset")],
                                [address.Operand], -1, identity + ":receiver-local-address", null,
                                IrMemoryEffectKind.None, false, storage.FrameSlotIdentity));
                            state.Stack.Add(address);
                            break;
                        }
                    case >= 0x0a and <= 0x0d:
                        StoreLocal(source.Encoding - 0x0a);
                        break;
                    case 0x13:
                        StoreLocal(checked((int)source.Literal));
                        break;
                    case 0x14:
                        state.Stack.Add(V2Value.Constant(0, RestrictedCilTypeV1.ObjectReference, identity));
                        break;
                    case >= 0x15 and <= 0x21:
                        state.Stack.Add(V2Value.Constant(source.Literal, source.Encoding == 0x21 ? RestrictedCilTypeV1.Int64 : RestrictedCilTypeV1.Int32, identity));
                        break;
                    case 0x67:
                    case 0x68:
                        {
                            V2Value value = Pop();
                            ulong shift = source.Encoding == 0x67 ? 56UL : 48UL;
                            V2Value shifted = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":left");
                            emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int32,
                                [value.Operand, new(IrOperandKind.Constant, shift, identity + ":left-count")],
                                [shifted.Operand], -1, identity + ":left"));
                            V2Value definition = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                            emissions.Add(new(source.Offset, HybridCpuOpcode.SRAI, RestrictedCilTypeV1.Int32,
                                [shifted.Operand, new(IrOperandKind.Constant, shift, identity + ":right-count")],
                                [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0x6e:
                        {
                            V2Value value = Pop();
                            V2Value definition = V2Value.Definition(RestrictedCilTypeV1.Int64, identity);
                            if (value.Type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32) {
                                V2Value shifted = V2Value.Definition(RestrictedCilTypeV1.Int64, identity + ":u8-left");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int64,
                                    [value.Operand, new(IrOperandKind.Constant, 32, identity + ":u8-left-count")],
                                    [shifted.Operand], -1, identity + ":u8-left"));
                                emissions.Add(new(source.Offset, HybridCpuOpcode.SRLI, RestrictedCilTypeV1.Int64,
                                    [shifted.Operand, new(IrOperandKind.Constant, 32, identity + ":u8-right-count")],
                                    [definition.Operand], -1, identity));
                            } else {
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.Int64,
                                    [value.Operand, new(IrOperandKind.Constant, 0, identity + ":u8-preserve")],
                                    [definition.Operand], -1, identity));
                            }
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0x6a:
                        {
                            V2Value value = Pop();
                            V2Value definition = V2Value.Definition(RestrictedCilTypeV1.Int64, identity);
                            HybridCpuOpcode opcode = value.Type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32
                                ? HybridCpuOpcode.ADDIW : HybridCpuOpcode.ADDI;
                            emissions.Add(new(source.Offset, opcode, RestrictedCilTypeV1.Int64,
                                [value.Operand, new(IrOperandKind.Constant, 0, identity + ":extend")],
                                [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0xd1:
                    case 0xd2:
                        {
                            V2Value value = Pop();
                            V2Value definition = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ANDI, RestrictedCilTypeV1.Int32,
                                [value.Operand, new(IrOperandKind.Constant, source.Encoding == 0xd1 ? 0xffffUL : 0xffUL,
                                    identity + (source.Encoding == 0xd1 ? ":u2-mask" : ":u1-mask"))],
                                [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0x65:
                    case 0x66:
                        {
                            V2Value value = Pop();
                            RestrictedCilTypeV1 resultType = value.Type == RestrictedCilTypeV1.UInt32
                                ? RestrictedCilTypeV1.Int32 : value.Type;
                            V2Value definition = V2Value.Definition(resultType, identity);
                            emissions.Add(new(source.Offset, source.Encoding == 0x66 ? HybridCpuOpcode.XORI : HybridCpuOpcode.SUB, resultType,
                                source.Encoding == 0x66 ? [value.Operand, new(IrOperandKind.Constant, ulong.MaxValue, identity + ":not-mask")] :
                                [new(IrOperandKind.ArchitecturalRegister, 0, "zero"), value.Operand],
                                [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0x62:
                    case 0x63:
                    case 0x64:
                        {
                            V2Value count = Pop(); V2Value value = Pop();
                            V2Value result = V2Value.Definition(value.Type, identity);
                            bool word = value.Type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32;
                            HybridCpuOpcode opcode = source.Encoding switch
                            {
                                0x62 => word ? HybridCpuOpcode.SLLW : HybridCpuOpcode.SLL,
                                0x63 => word ? HybridCpuOpcode.SRAW : HybridCpuOpcode.SRA,
                                0x64 => word ? HybridCpuOpcode.SRLW : HybridCpuOpcode.SRL,
                                _ => throw new InvalidOperationException("Verified shift is outside the mapping table.")
                            };
                            // Word forms apply exact I4 normalization; wide forms retain 64 bits.
                            // Existing ISA masks the count by 31/63.
                            // Keep register form so the shared materializer owns literal operands.
                            emissions.Add(new(source.Offset, opcode,
                                value.Type, [value.Operand, count.Operand], [result.Operand], -1, identity));
                            state.Stack.Add(result);
                            break;
                        }
                    case 0x5b:
                    case 0x5c:
                    case 0x5d:
                    case 0x5e:
                        {
                            V2Value right = Pop(); V2Value left = Pop();
                            int width = UnsignedDivisionWidth(left.Type, right.Type);
                            if (dataflow.Helpers.TryGetValue(source.Offset, out ResolvedHelper? checkedDivision) &&
                                checkedDivision.Contract.CanonicalExpansion == "runtime-helper")
                            {
                                string operation = checkedDivision.Contract.StableIdentity;
                                if (operation == "checked-remu-i4") {
                                    // UInt32 helper reads the canonical low32 carrier. Its zero
                                    // quotient creates a true dependency before unsigned word rem.
                                    V2Value wordProbe = EmitRuntimeCall("u4-zero-probe", "__hybridcpu_managed_divide_u4_checked",
                                        [V2Value.Constant(0, left.Type, identity + ":probe-zero"), right], left.Type,
                                        IrMemoryEffectKind.Read | IrMemoryEffectKind.Write)!;
                                    V2Value wordDivisor = V2Value.Definition(right.Type, identity + ":validated-word-divisor");
                                    V2Value wordResult = V2Value.Definition(left.Type, identity);
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDW, right.Type,
                                        [right.Operand, wordProbe.Operand], [wordDivisor.Operand], -1, identity + ":validated-word-divisor"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.REMUW, left.Type,
                                        [left.Operand, wordDivisor.Operand], [wordResult.Operand], -1, identity));
                                    state.Stack.Add(wordResult); break;
                                }
                                if (operation is "checked-divu-i8" or "checked-remu-i8") {
                                    // 0 / signedBits(d) faults exactly when d==0; it cannot overflow.
                                    // Its zero result participates in the divisor, creating a real
                                    // data dependency so DIVU/REMU cannot precede the checked call.
                                    V2Value probe = EmitRuntimeCall("u8-zero-probe", "__hybridcpu_managed_divide_i8_checked",
                                        [V2Value.Constant(0, RestrictedCilTypeV1.Int64, identity + ":probe-zero"), right],
                                        RestrictedCilTypeV1.Int64, IrMemoryEffectKind.Read | IrMemoryEffectKind.Write)!;
                                    V2Value divisor = V2Value.Definition(right.Type, identity + ":validated-divisor");
                                    V2Value unsignedResult = V2Value.Definition(left.Type, identity);
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADD, right.Type,
                                        [right.Operand, probe.Operand], [divisor.Operand], -1, identity + ":validated-divisor"));
                                    emissions.Add(new(source.Offset, operation == "checked-divu-i8" ? HybridCpuOpcode.DIVU : HybridCpuOpcode.REMU, left.Type,
                                        [left.Operand, divisor.Operand], [unsignedResult.Operand], -1, identity));
                                    state.Stack.Add(unsignedResult); break;
                                }
                                string helper = checkedDivision.Contract.StableIdentity switch
                                {
                                    "checked-div-i4" => "__hybridcpu_managed_divide_i4_checked",
                                    "checked-div-i8" or "checked-rem-i8" => "__hybridcpu_managed_divide_i8_checked",
                                    "checked-rem-i4" => "__hybridcpu_managed_remainder_i4_checked",
                                    _ => "__hybridcpu_managed_divide_u4_checked"
                                };
                                bool wideRemainder = operation == "checked-rem-i8";
                                V2Value quotient = EmitRuntimeCall(operation, helper, [left, right], left.Type,
                                    IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
                                    resultIdentity: wideRemainder ? identity + ":quotient" : identity)!;
                                if (wideRemainder) {
                                    // Checked division establishes both CIL fault conditions before
                                    // computing n - trunc(n/d)*d. Original operands stay scalar SSA
                                    // values across the call; normal RA preserves them under its ABI.
                                    V2Value product = V2Value.Definition(left.Type, identity + ":product");
                                    V2Value remainderValue = V2Value.Definition(left.Type, identity);
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.MUL, left.Type,
                                        [quotient.Operand, right.Operand], [product.Operand], -1, identity + ":product"));
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.SUB, left.Type,
                                        [left.Operand, product.Operand], [remainderValue.Operand], -1, identity));
                                    state.Stack.Add(remainderValue);
                                } else state.Stack.Add(quotient);
                                break;
                            }
                            // Dataflow has proved nonzero at the exact operation width; do not
                            // depend on the ISA's different zero-divisor behavior.
                            IrOperand[] uses = new[] { left.Operand, right.Operand };
                            for (int operand = 0; operand < uses.Length; operand++)
                            {
                                if (uses[operand].Kind != IrOperandKind.Constant) continue;
                                V2Value temporary = V2Value.Definition(left.Type, $"{identity}:divisor-materialize:{operand}");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, left.Type,
                                    [new(IrOperandKind.ArchitecturalRegister, 0, "zero"), uses[operand]], [temporary.Operand], -1, $"{identity}:div-constant:{operand}"));
                                uses[operand] = temporary.Operand;
                            }
                            V2Value definition = V2Value.Definition(left.Type, identity);
                            bool remainder = source.Encoding is 0x5d or 0x5e;
                            bool signed = source.Encoding is 0x5b or 0x5d;
                            HybridCpuOpcode opcode = width == 32
                                ? remainder ? signed ? HybridCpuOpcode.REMW : HybridCpuOpcode.REMUW
                                    : signed ? HybridCpuOpcode.DIVW : HybridCpuOpcode.DIVUW
                                : remainder ? signed ? HybridCpuOpcode.REM : HybridCpuOpcode.REMU
                                    : signed ? HybridCpuOpcode.DIV : HybridCpuOpcode.DIVU;
                            emissions.Add(new(source.Offset, opcode,
                                left.Type, uses, [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0x58:
                    case 0x59:
                    case 0x5a:
                    case 0x5f:
                    case 0x60:
                    case 0x61:
                        {
                            V2Value right = Pop();
                            V2Value left = Pop();
                            V2Value definition = V2Value.Definition(left.Type, identity);
                            HybridCpuOpcode opcode = source.Encoding switch
                            {
                                0x58 => HybridCpuOpcode.ADD,
                                0x59 => HybridCpuOpcode.SUB,
                                0x5a => HybridCpuOpcode.MUL,
                                0x5f => HybridCpuOpcode.AND,
                                0x60 => HybridCpuOpcode.OR,
                                _ => HybridCpuOpcode.XOR
                            };
                            IrOperand[] uses = [left.Operand, right.Operand];
                            if (opcode is HybridCpuOpcode.ADD or HybridCpuOpcode.AND or HybridCpuOpcode.OR or HybridCpuOpcode.XOR &&
                                left.Operand.Kind == IrOperandKind.Constant && right.Operand.Kind != IrOperandKind.Constant)
                                uses = [right.Operand, left.Operand];
                            if (opcode == HybridCpuOpcode.ADD && uses[1].Kind == IrOperandKind.Constant)
                                opcode = HybridCpuOpcode.ADDI;
                            else if (opcode == HybridCpuOpcode.SUB && right.Operand.Kind == IrOperandKind.Constant)
                            {
                                opcode = HybridCpuOpcode.ADDI;
                                uses = [left.Operand, right.Operand with { Value = unchecked(0UL - right.Operand.Value) }];
                            }
                            else if (opcode == HybridCpuOpcode.AND && uses[1].Kind == IrOperandKind.Constant)
                                opcode = HybridCpuOpcode.ANDI;
                            else if (opcode == HybridCpuOpcode.OR && uses[1].Kind == IrOperandKind.Constant)
                                opcode = HybridCpuOpcode.ORI;
                            else if (opcode == HybridCpuOpcode.XOR && uses[1].Kind == IrOperandKind.Constant)
                                opcode = HybridCpuOpcode.XORI;
                            else if (opcode == HybridCpuOpcode.MUL)
                            {
                                for (int operand = 0; operand < uses.Length; operand++)
                                {
                                    if (uses[operand].Kind != IrOperandKind.Constant) continue;
                                    V2Value materialized = V2Value.Definition(left.Type,
                                        $"{identity}:mul-constant:{operand}");
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, left.Type,
                                        [new(IrOperandKind.ArchitecturalRegister, 0, "zero"), uses[operand]],
                                        [materialized.Operand], -1, $"{identity}:mul-constant:{operand}:materialize"));
                                    uses[operand] = materialized.Operand;
                                }
                            }
                            emissions.Add(new(source.Offset, opcode, left.Type, uses, [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0xfe01:
                        {
                            V2Value right = Pop();
                            V2Value left = Pop();
                            if(WordPair(left,right)){left=ComparisonWord(left,false,"left");right=ComparisonWord(right,false,"right");}
                            V2Value xor = V2Value.Definition(left.Type, $"{identity}:ceq-xor");
                            V2Value definition = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                            IrOperand[] xorUses = [left.Operand, right.Operand];
                            HybridCpuOpcode xorOpcode = HybridCpuOpcode.XOR;
                            if (left.Operand.Kind == IrOperandKind.Constant && right.Operand.Kind != IrOperandKind.Constant)
                                xorUses = [right.Operand, left.Operand];
                            if (xorUses[1].Kind == IrOperandKind.Constant)
                                xorOpcode = HybridCpuOpcode.XORI;
                            emissions.Add(new(source.Offset, xorOpcode, left.Type, xorUses, [xor.Operand], -1, $"{identity}:ceq-xor"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.SLTIU, RestrictedCilTypeV1.Int32,
                                [xor.Operand, new(IrOperandKind.Constant, 1, $"{identity}:one")], [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case >= 0xfe02 and <= 0xfe05:
                        {
                            V2Value right = Pop();
                            V2Value left = Pop();
                            if(WordPair(left,right)){bool unsigned=source.Encoding is 0xfe03 or 0xfe05;left=ComparisonWord(left,unsigned,"left");right=ComparisonWord(right,unsigned,"right");}
                            V2Value definition = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                            HybridCpuOpcode opcode = source.Encoding is 0xfe03 or 0xfe05 ? HybridCpuOpcode.SLTU : HybridCpuOpcode.SLT;
                            IrOperand[] uses = source.Encoding is 0xfe02 or 0xfe03 ? [right.Operand, left.Operand] : [left.Operand, right.Operand];
                            emissions.Add(new(source.Offset, opcode, RestrictedCilTypeV1.Int32, uses, [definition.Operand], -1, identity));
                            state.Stack.Add(definition);
                            break;
                        }
                    case 0x28:
                    case 0x6f:
                        {
                            if (source.Encoding == 0x6f && dataflow.DelegateInvokes.TryGetValue(source.Offset,
                                    out RestrictedCilDelegateInvokeBindingV1? delegateInvoke))
                            {
                                V2Value[] delegateArguments = new V2Value[delegateInvoke.ParameterTypes.Count];
                                for (int parameter = delegateArguments.Length - 1; parameter >= 0; parameter--) delegateArguments[parameter] = Pop();
                                V2Value delegateSignature = V2Value.Constant(unchecked((long)delegateInvoke.SignatureId),
                                    RestrictedCilTypeV1.NativeUInt, $"{identity}:delegate-signature");
                                V2Value target = EmitRuntimeCall("delegate-resolve", "__hybridcpu_managed_resolve_delegate",
                                    [delegateArguments[0], delegateSignature], RestrictedCilTypeV1.NativeUInt, IrMemoryEffectKind.Read)!;
                                V2Value targetAbi = V2Value.Definition(RestrictedCilTypeV1.NativeUInt,
                                    $"{identity}:indirect-target-abi");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                                    [target.Operand, new(IrOperandKind.Constant, 0, $"{identity}:target-zero")],
                                    [targetAbi.Operand], -1, $"{identity}:delegate-target-copy"));
                                V2Value[] abiArguments = delegateArguments.Select((argument, parameter) =>
                                    V2Value.Definition(argument.Type, $"{identity}:call-arg-abi:{parameter}")).ToArray();
                                for (int parameter = 0; parameter < delegateArguments.Length; parameter++)
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, delegateArguments[parameter].Type,
                                        [delegateArguments[parameter].Operand, new(IrOperandKind.Constant, 0, $"{identity}:delegate-arg-zero:{parameter}")],
                                        [abiArguments[parameter].Operand], -1, $"{identity}:delegate-arg-copy:{parameter}"));
                                V2Value? result = delegateInvoke.ReturnType == RestrictedCilTypeV1.Void ? null :
                                    V2Value.Definition(delegateInvoke.ReturnType, identity);
                                V2Value? abiResult = result is null ? null : V2Value.Definition(result.Type, $"{identity}:call-result-abi");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.JALR,
                                    delegateInvoke.ReturnType == RestrictedCilTypeV1.Void ? RestrictedCilTypeV1.NativeUInt : delegateInvoke.ReturnType,
                                    [targetAbi.Operand, .. abiArguments.Select(static argument => argument.Operand)],
                                    abiResult is null ? [] : [abiResult.Operand], -1, identity, null, IrMemoryEffectKind.Read, true));
                                if (result is not null)
                                {
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, result.Type,
                                        [abiResult!.Operand, new(IrOperandKind.Constant, 0, $"{identity}:delegate-result-zero")],
                                        [result.Operand], -1, $"{identity}:delegate-result-copy"));
                                    state.Stack.Add(result);
                                }
                                break;
                            }
                            ResolvedHelper helper = dataflow.Helpers[source.Offset];
                            V2Value[] arguments = new V2Value[helper.Contract.ParameterTypes.Count];
                            for (int parameter = arguments.Length - 1; parameter >= 0; parameter--) arguments[parameter] = Pop();
                            for (int parameter = 0; parameter < arguments.Length; parameter++)
                            {
                                if (arguments[parameter].Type != RestrictedCilTypeV1.UInt32 ||
                                    helper.Contract.ParameterTypes[parameter] != RestrictedCilTypeV1.Int32) continue;
                                V2Value normalized = V2Value.Definition(RestrictedCilTypeV1.Int32,
                                    $"{identity}:call-i4:{parameter}");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                                    [arguments[parameter].Operand, new(IrOperandKind.Constant, 0,
                                        $"{identity}:call-i4-zero:{parameter}")], [normalized.Operand], -1,
                                    $"{identity}:call-i4:{parameter}"));
                                arguments[parameter] = normalized;
                            }
                            V2Value? definition = helper.Contract.ReturnType == RestrictedCilTypeV1.Void
                                ? null
                                : V2Value.Definition(StackType(helper.Contract.ReturnType), identity) with
                                { AggregateIdentity = helper.AggregateReturn };
                            if (helper.Dispatch is { } dispatch)
                            {
                                V2Value receiver = arguments[0];
                                EmitRuntimeCall("callvirt-null-check", "__hybridcpu_managed_null_check",
                                    [receiver], RestrictedCilTypeV1.Void, IrMemoryEffectKind.Read);
                                if (dispatch.ExactImplementationIdentity is { } exactImplementation)
                                {
                                    V2Value[] directAbiArguments = arguments.Select((argument, parameter) =>
                                        V2Value.Definition(argument.Type, $"{identity}:exact-dispatch-arg-abi:{parameter}")).ToArray();
                                    for (int parameter = 0; parameter < arguments.Length; parameter++)
                                        emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, arguments[parameter].Type,
                                            [arguments[parameter].Operand, new(IrOperandKind.Constant, 0, $"{identity}:exact-dispatch-zero:{parameter}")],
                                            [directAbiArguments[parameter].Operand], -1, $"{identity}:exact-dispatch-copy:{parameter}"));
                                    V2Value? directAbiResult = definition is null ? null :
                                        V2Value.Definition(definition.Type, $"{identity}:exact-dispatch-result-abi");
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.JAL,
                                        helper.Contract.ReturnType == RestrictedCilTypeV1.Void ? RestrictedCilTypeV1.NativeUInt : helper.Contract.ReturnType,
                                        directAbiArguments.Select(static argument => argument.Operand).ToArray(),
                                        directAbiResult is null ? [] : [directAbiResult.Operand], -1, identity,
                                        exactImplementation, IrMemoryEffectKind.Read));
                                    if (definition is not null)
                                    {
                                        emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, definition.Type,
                                            [directAbiResult!.Operand, new(IrOperandKind.Constant, 0, $"{identity}:exact-dispatch-result-zero")],
                                            [definition.Operand], -1, $"{identity}:exact-dispatch-result-copy"));
                                        state.Stack.Add(definition);
                                    }
                                    break;
                                }
                                V2Value slot = V2Value.Constant(unchecked((long)dispatch.SlotId),
                                    RestrictedCilTypeV1.NativeUInt, $"{identity}:dispatch-slot");
                                V2Value? interfaceType = dispatch.InterfaceTypeId is ulong interfaceId
                                    ? V2Value.Constant(unchecked((long)interfaceId), RestrictedCilTypeV1.NativeUInt,
                                        $"{identity}:dispatch-interface")
                                    : null;
                                V2Value target = EmitRuntimeCall("dispatch-resolve",
                                    dispatch.Kind == RestrictedCilDispatchKindV1.Virtual
                                        ? "__hybridcpu_managed_resolve_virtual"
                                        : "__hybridcpu_managed_resolve_interface",
                                    interfaceType is null ? [receiver, slot] : [receiver, interfaceType, slot],
                                    RestrictedCilTypeV1.NativeUInt, IrMemoryEffectKind.Read)!;
                                V2Value targetAbi = V2Value.Definition(RestrictedCilTypeV1.NativeUInt,
                                    $"{identity}:indirect-target-abi");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                                    [target.Operand, new(IrOperandKind.Constant, 0, $"{identity}:target-zero")],
                                    [targetAbi.Operand], -1, $"{identity}:indirect-target-copy"));
                                V2Value[] abiArguments = arguments.Select((argument, parameter) =>
                                    V2Value.Definition(argument.Type, $"{identity}:call-arg-abi:{parameter}")).ToArray();
                                for (int parameter = 0; parameter < arguments.Length; parameter++)
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, arguments[parameter].Type,
                                        [arguments[parameter].Operand, new(IrOperandKind.Constant, 0, $"{identity}:dispatch-arg-zero:{parameter}")],
                                        [abiArguments[parameter].Operand], -1, $"{identity}:dispatch-arg-copy:{parameter}"));
                                V2Value? abiResult = definition is null ? null :
                                    V2Value.Definition(definition.Type, $"{identity}:call-result-abi");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.JALR,
                                    helper.Contract.ReturnType == RestrictedCilTypeV1.Void ? RestrictedCilTypeV1.NativeUInt : helper.Contract.ReturnType,
                                    [targetAbi.Operand, .. abiArguments.Select(static argument => argument.Operand)],
                                    abiResult is null ? [] : [abiResult.Operand], -1, identity,
                                    null, IrMemoryEffectKind.Read, true));
                                if (definition is not null)
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, definition.Type,
                                        [abiResult!.Operand, new(IrOperandKind.Constant, 0, $"{identity}:dispatch-result-zero")],
                                        [definition.Operand], -1, $"{identity}:dispatch-result-copy"));
                            }
                            else if (helper.IsManagedCall)
                            {
                                if (source.Encoding == 0x6f)
                                    EmitRuntimeCall("callvirt-null-check", "__hybridcpu_managed_null_check",
                                        [arguments[0]], RestrictedCilTypeV1.Void, IrMemoryEffectKind.Read);
                                V2Value[] abiArguments = arguments.Select((argument, parameter) =>
                                    V2Value.Definition(argument.Type, $"{identity}:call-arg-abi:{parameter}")).ToArray();
                                for (int parameter = 0; parameter < arguments.Length; parameter++)
                                {
                                    IrOperand[] copyUses = arguments[parameter].Operand.Kind == IrOperandKind.Constant
                                        ? [new(IrOperandKind.ArchitecturalRegister, 0, $"{identity}:call-arg-zero:{parameter}"),
                                            arguments[parameter].Operand]
                                        : [arguments[parameter].Operand,
                                            new(IrOperandKind.Constant, 0, $"{identity}:call-arg-zero:{parameter}")];
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, arguments[parameter].Type,
                                        copyUses,
                                        [abiArguments[parameter].Operand], -1, $"{identity}:call-arg-copy:{parameter}"));
                                }
                                V2Value? abiResult = definition is null
                                    ? null
                                    : V2Value.Definition(definition.Type, $"{identity}:call-result-abi");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.JAL,
                                    helper.Contract.ReturnType == RestrictedCilTypeV1.Void ? RestrictedCilTypeV1.NativeUInt : helper.Contract.ReturnType,
                                    abiArguments.Select(static argument => argument.Operand).ToArray(),
                                    abiResult is null ? [] : [abiResult.Operand], -1, identity, helper.Identity,
                                    IsManagedGcSafepoint: helper.Receiver is null));
                                if (definition is not null)
                                {
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, definition.Type,
                                        [abiResult!.Operand, new(IrOperandKind.Constant, 0, $"{identity}:call-result-zero")],
                                        [definition.Operand], -1, $"{identity}:call-result-copy"));
                                }
                            }
                            else if (string.Equals(helper.Contract.CallingConvention, "runtime-helper", StringComparison.Ordinal))
                            {
                                if (helper.EmptyArrayTypeIdentity is string emptyArrayIdentity)
                                    arguments = [V2Value.Constant(unchecked((long)ResolveEmptyArrayBinding(helper)!.TypeHandle),
                                        RestrictedCilTypeV1.NativeUInt, $"{identity}:empty-array-type-handle")];
                                V2Value? runtimeResult = EmitRuntimeCall("intrinsic", helper.Contract.CanonicalExpansion,
                                    arguments, helper.Contract.ReturnType, helper.Contract.MemoryEffects, resultIdentity: identity);
                                definition = runtimeResult;
                            }
                            else
                            {
                                if (!string.Equals(helper.Contract.CanonicalExpansion, "intrinsic-noop",
                                        StringComparison.Ordinal))
                                {
                                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, helper.Contract.ReturnType,
                                        [arguments[0].Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")],
                                        definition is null ? [] : [definition.Operand], -1, identity));
                                }
                            }
                            if (definition is not null) state.Stack.Add(definition);
                            break;
                        }
                    case 0xfe06:
                    case 0xfe07:
                        {
                            RestrictedCilFunctionPointerBindingV1 binding = dataflow.FunctionPointers[source.Offset];
                             V2Value functionPointerSignature = V2Value.Constant(unchecked((long)binding.SignatureId),
                                RestrictedCilTypeV1.NativeUInt, $"{identity}:fptr-signature");
                            V2Value pointer;
                            if (source.Encoding == 0xfe06)
                            {
                                V2Value method = V2Value.Constant(unchecked((long)binding.MethodId),
                                    RestrictedCilTypeV1.NativeUInt, $"{identity}:fptr-method");
                                pointer = EmitRuntimeCall("fptr-resolve", "__hybridcpu_managed_get_function_pointer",
                                    [method, functionPointerSignature], RestrictedCilTypeV1.NativeUInt, IrMemoryEffectKind.Read)!;
                            }
                            else
                            {
                                V2Value receiver = Pop();
                                RestrictedCilDispatchBindingV1 dispatch = _dispatchBindings[
                                    binding.VirtualSlotMetadataToken ?? source.Token];
                                V2Value slot = V2Value.Constant(unchecked((long)dispatch.SlotId),
                                    RestrictedCilTypeV1.NativeUInt, $"{identity}:fptr-slot");
                                pointer = EmitRuntimeCall("fptr-virtual-resolve", "__hybridcpu_managed_get_virtual_function_pointer",
                                    [receiver, slot, functionPointerSignature], RestrictedCilTypeV1.NativeUInt, IrMemoryEffectKind.Read)!;
                            }
                            pointer = pointer with { ManagedFunctionPointerSignatureId = binding.SignatureId };
                            state.Stack.Add(pointer);
                            break;
                        }
                    case 0x29:
                        {
                            RestrictedCilCalliBindingV1 binding = dataflow.CallSites[source.Offset];
                            V2Value pointer = Pop();
                             V2Value[] calliArguments = new V2Value[binding.ParameterTypes.Count];
                             for (int parameter = calliArguments.Length - 1; parameter >= 0; parameter--) calliArguments[parameter] = Pop();
                             V2Value[] preservedArguments = calliArguments.Select((argument, parameter) =>
                                 V2Value.Definition(argument.Type, $"{identity}:calli-preserved-arg:{parameter}")).ToArray();
                             for (int parameter = 0; parameter < calliArguments.Length; parameter++)
                                 emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, calliArguments[parameter].Type,
                                     [calliArguments[parameter].Operand,
                                         new(IrOperandKind.Constant, 0, $"{identity}:calli-preserve-zero:{parameter}")],
                                     [preservedArguments[parameter].Operand], -1,
                                     $"{identity}:calli-preserve-copy:{parameter}"));
                             V2Value calliSignature = V2Value.Constant(unchecked((long)binding.SignatureId),
                                RestrictedCilTypeV1.NativeUInt, $"{identity}:calli-signature");
                             V2Value target = pointer.ManagedFunctionPointerSignatureId == binding.SignatureId
                                 ? pointer
                                 : EmitRuntimeCall("fptr-validate", "__hybridcpu_managed_validate_function_pointer",
                                     [pointer, calliSignature], RestrictedCilTypeV1.NativeUInt, IrMemoryEffectKind.Read)!;
                            V2Value targetAbi = V2Value.Definition(RestrictedCilTypeV1.NativeUInt,
                                $"{identity}:indirect-target-abi");
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                                [target.Operand, new(IrOperandKind.Constant, 0, $"{identity}:target-zero")],
                                [targetAbi.Operand], -1, $"{identity}:calli-target-copy"));
                             V2Value[] abiArguments = preservedArguments.Select((argument, parameter) =>
                                 V2Value.Definition(argument.Type, $"{identity}:call-arg-abi:{parameter}")).ToArray();
                             for (int parameter = 0; parameter < preservedArguments.Length; parameter++)
                                 emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, preservedArguments[parameter].Type,
                                     [preservedArguments[parameter].Operand, new(IrOperandKind.Constant, 0, $"{identity}:calli-arg-zero:{parameter}")],
                                    [abiArguments[parameter].Operand], -1, $"{identity}:calli-arg-copy:{parameter}"));
                            V2Value? result = binding.ReturnType == RestrictedCilTypeV1.Void ? null :
                                V2Value.Definition(binding.ReturnType, identity);
                            V2Value? abiResult = result is null ? null : V2Value.Definition(result.Type, $"{identity}:call-result-abi");
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JALR,
                                binding.ReturnType == RestrictedCilTypeV1.Void ? RestrictedCilTypeV1.NativeUInt : binding.ReturnType,
                                [targetAbi.Operand, .. abiArguments.Select(static argument => argument.Operand)],
                                abiResult is null ? [] : [abiResult.Operand], -1, identity, null, IrMemoryEffectKind.Read, true));
                            if (result is not null)
                            {
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, result.Type,
                                    [abiResult!.Operand, new(IrOperandKind.Constant, 0, $"{identity}:calli-result-zero")],
                                    [result.Operand], -1, $"{identity}:calli-result-copy"));
                                state.Stack.Add(result);
                            }
                            break;
                        }
                    case 0x74:
                    case 0x75:
                        {
                            V2Value receiver = Pop();
                            RestrictedCilTypeTestBindingV1 binding = dataflow.TypeTests[source.Offset];
                            V2Value typeHandle = V2Value.Constant(unchecked((long)binding.TypeHandle),
                                RestrictedCilTypeV1.NativeUInt, $"{identity}:type-test-handle");
                            state.Stack.Add(EmitRuntimeCall(source.Encoding == 0x74 ? "castclass" : "isinst",
                                source.Encoding == 0x74 ? "__hybridcpu_managed_castclass" : "__hybridcpu_managed_isinst",
                                [receiver, typeHandle], RestrictedCilTypeV1.ObjectReference, IrMemoryEffectKind.Read,
                                resultIdentity: identity)!);
                            break;
                        }
                    case 0x69:
                    case 0x6d:
                        {
                            V2Value value = Pop();
                            V2Value converted = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                            emissions.Add(new(source.Offset,
                                source.Encoding == 0x6d || value.Type is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 ? HybridCpuOpcode.ADDIW : HybridCpuOpcode.ADDI,
                                RestrictedCilTypeV1.Int32,
                                [value.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], [converted.Operand], -1, identity));
                            state.Stack.Add(converted);
                            break;
                        }
                    case 0x73:
                        {
                            if (dataflow.DelegateCreations.TryGetValue(source.Offset,
                                    out RestrictedCilDelegateCreationBindingV1? delegateCreation))
                            {
                                V2Value pointer = Pop();
                                V2Value target = Pop();
                                V2Value typeHandle = V2Value.Constant(unchecked((long)delegateCreation.DelegateTypeHandle),
                                    RestrictedCilTypeV1.NativeUInt, $"{identity}:delegate-type");
                                V2Value delegateSignatureIdValue = V2Value.Constant(unchecked((long)delegateCreation.SignatureId),
                                    RestrictedCilTypeV1.NativeUInt, $"{identity}:delegate-signature");
                                V2Value kind = V2Value.Constant((long)delegateCreation.Kind,
                                    RestrictedCilTypeV1.Int32, $"{identity}:delegate-kind");
                                state.Stack.Add(EmitRuntimeCall("delegate-create", "__hybridcpu_managed_create_delegate",
                                    [typeHandle, target, pointer, delegateSignatureIdValue, kind], RestrictedCilTypeV1.ObjectReference,
                                    IrMemoryEffectKind.Read | IrMemoryEffectKind.Write)!);
                                break;
                            }
                            V2Allocation allocation = dataflow.Allocations[source.Offset];
                            int explicitCount = allocation.Constructor.Contract.ParameterTypes.Count - 1;
                            V2Value[] explicitArguments = new V2Value[explicitCount];
                            for (int parameter = explicitCount - 1; parameter >= 0; parameter--)
                                explicitArguments[parameter] = Pop();

                            if (allocation.Binding.IsScalarValueProjection)
                            {
                                if (source.Name == ProjectedLocalConstructorName)
                                {
                                    // The qualified local/copy proof erased this exact pure-copy construction.
                                    state.Stack.Add(explicitArguments[0] with { AggregateIdentity = allocation.AggregateIdentity });
                                    break;
                                }
                                V2Value projected = V2Value.Definition(allocation.Binding.ScalarValueType,
                                    $"{identity}:scalar-value-constructor") with
                                {
                                    AggregateIdentity = allocation.AggregateIdentity
                                };
                                V2Value argument = explicitArguments[0];
                                IrOperand[] uses = argument.Operand.Kind == IrOperandKind.Constant
                                    ? [new(IrOperandKind.ArchitecturalRegister, 0, $"{identity}:zero"), argument.Operand]
                                    : [argument.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")];
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI,
                                    allocation.Binding.ScalarValueType, uses, [projected.Operand], -1,
                                    $"{identity}:scalar-value-constructor-copy"));
                                state.Stack.Add(projected);
                                break;
                            }

                            if (allocation.Constructor.Contract.CanonicalExpansion == "__hybridcpu_managed_string_from_utf16_array")
                            {
                                V2Value stringTypeHandle = V2Value.Constant(unchecked((long)allocation.Binding.TypeHandle),
                                    RestrictedCilTypeV1.NativeUInt, $"{identity}:string-type-handle");
                                state.Stack.Add(EmitRuntimeCall("string-from-utf16-array",
                                    "__hybridcpu_managed_string_from_utf16_array", [stringTypeHandle, explicitArguments[0]],
                                    RestrictedCilTypeV1.ObjectReference, IrMemoryEffectKind.Read | IrMemoryEffectKind.Write)!);
                                break;
                            }

                            V2Value typeHandleAbi = V2Value.Definition(RestrictedCilTypeV1.NativeUInt,
                                $"{identity}:alloc-type-handle-abi");
                            V2Value allocatedAbi = V2Value.Definition(RestrictedCilTypeV1.ObjectReference,
                                $"{identity}:alloc-result-abi");
                            V2Value allocated = V2Value.Definition(RestrictedCilTypeV1.ObjectReference,
                                $"{identity}:allocated-object");
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                                [new(IrOperandKind.ArchitecturalRegister, 0, $"{identity}:zero"),
                                    new(IrOperandKind.Constant, allocation.Binding.TypeHandle, $"{identity}:type-handle")],
                                [typeHandleAbi.Operand], -1, $"{identity}:alloc-type-handle-copy"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JAL, RestrictedCilTypeV1.ObjectReference,
                                [typeHandleAbi.Operand], [allocatedAbi.Operand], -1,
                                $"{identity}:allocate", "__hybridcpu_managed_alloc", IrMemoryEffectKind.Write));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.ObjectReference,
                                [allocatedAbi.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")],
                                [allocated.Operand], -1, $"{identity}:alloc-result-copy"));

                            V2Value[] constructorArguments = [allocated, .. explicitArguments];
                            V2Value[] abiArguments = constructorArguments.Select((argument, parameter) =>
                                V2Value.Definition(argument.Type, $"{identity}:ctor-arg-abi:{parameter}")).ToArray();
                            for (int parameter = 0; parameter < constructorArguments.Length; parameter++)
                            {
                                V2Value argument = constructorArguments[parameter];
                                IrOperand[] uses = argument.Operand.Kind == IrOperandKind.Constant
                                    ? [new(IrOperandKind.ArchitecturalRegister, 0, $"{identity}:ctor-zero:{parameter}"), argument.Operand]
                                    : [argument.Operand, new(IrOperandKind.Constant, 0, $"{identity}:ctor-zero:{parameter}")];
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, argument.Type, uses,
                                    [abiArguments[parameter].Operand], -1, $"{identity}:ctor-arg-copy:{parameter}"));
                            }
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JAL, RestrictedCilTypeV1.NativeUInt,
                                abiArguments.Select(static argument => argument.Operand).ToArray(), [], -1,
                                $"{identity}:constructor", allocation.Constructor.IsManagedCall
                                    ? allocation.Constructor.Identity : allocation.Constructor.Contract.CanonicalExpansion,
                                allocation.Constructor.Contract.MemoryEffects));
                            // newobj produces the allocated reference, not the constructor's void ABI result.
                            // Materialize the canonical CIL value after the constructor so a value crossing a
                            // basic-block boundary has an exact definition while the allocation result remains
                            // live across the constructor call.
                            V2Value result = V2Value.Definition(RestrictedCilTypeV1.ObjectReference, identity);
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.ObjectReference,
                                [allocated.Operand, new(IrOperandKind.Constant, 0, $"{identity}:ctor-result-zero")],
                                [result.Operand], -1, $"{identity}:ctor-result-copy"));
                            state.Stack.Add(result);
                            break;
                        }
                    case 0x72:
                        {
                            RestrictedCilStringLiteralBindingV1 binding = dataflow.Strings[source.Offset];
                            V2Value handle = V2Value.Constant(unchecked((long)binding.LiteralHandle), RestrictedCilTypeV1.NativeUInt, $"{identity}:literal-handle");
                            state.Stack.Add(EmitRuntimeCall("ldstr", "__hybridcpu_managed_ldstr", [handle],
                                RestrictedCilTypeV1.ObjectReference, IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
                                resultIdentity: identity)!);
                            break;
                        }
                    case 0xd0:
                        {
                            RestrictedCilFieldDataBindingV1 binding = dataflow.FieldData[source.Offset];
                            state.Stack.Add(V2Value.Constant(unchecked((long)binding.DataHandle),
                                RestrictedCilTypeV1.NativeUInt, $"{identity}:field-data-handle"));
                            break;
                        }
                    case 0x8d:
                        {
                            V2Value length = Pop();
                            RestrictedCilArrayTypeBindingV1 binding = dataflow.Arrays[source.Offset];
                            V2Value handle = V2Value.Constant(unchecked((long)binding.TypeHandle), RestrictedCilTypeV1.NativeUInt, $"{identity}:array-type-handle");
                            state.Stack.Add(EmitRuntimeCall("newarr", "__hybridcpu_managed_newarr", [handle, length],
                                RestrictedCilTypeV1.ObjectReference, IrMemoryEffectKind.Write, resultIdentity: identity)!);
                            break;
                        }
                    case 0x8e:
                        {
                            V2Value array = Pop();
                            state.Stack.Add(EmitRuntimeCall("ldlen", "__hybridcpu_managed_array_length", [array],
                                RestrictedCilTypeV1.NativeUInt, IrMemoryEffectKind.Read)!);
                            break;
                        }
                    case 0x90:
                    case 0x91:
                    case 0x92:
                    case 0x93:
                    case 0x94:
                    case 0x95:
                    case 0x96:
                    case 0x9a:
                        {
                            V2Value index = Pop(); V2Value array = Pop();
                            if (index.Type == RestrictedCilTypeV1.UInt32)
                            {
                                V2Value canonicalIndex = V2Value.Definition(RestrictedCilTypeV1.Int32, $"{identity}:index-i4");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                                    [index.Operand, new(IrOperandKind.Constant, 0, $"{identity}:index-zero")],
                                    [canonicalIndex.Operand], -1, $"{identity}:index-i4"));
                                index = canonicalIndex;
                            }
                            RestrictedCilTypeV1 resultType = source.Encoding switch
                            {
                                0x95 => RestrictedCilTypeV1.UInt32,
                                0x96 => RestrictedCilTypeV1.Int64,
                                0x9a => RestrictedCilTypeV1.ObjectReference,
                                _ => RestrictedCilTypeV1.Int32
                            };
                            string helper = source.Encoding switch { 0x90 or 0x91 => "__hybridcpu_managed_array_load_u1",
                                0x92 => "__hybridcpu_managed_array_load_i2", 0x93 => "__hybridcpu_managed_array_load_u2",
                                0x96 => "__hybridcpu_managed_array_load_i8",
                                0x94 or 0x95 => "__hybridcpu_managed_array_load_i4", _ => "__hybridcpu_managed_array_load_ref" };
                            V2Value loaded = EmitRuntimeCall("ldelem", helper, [array, index], resultType, IrMemoryEffectKind.Read,
                                resultIdentity: source.Encoding == 0x90 ? identity + ":unsigned-byte" : identity)!;
                            if (source.Encoding == 0x90) {
                                V2Value shifted = V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":signed-byte-left");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.SLLI, RestrictedCilTypeV1.Int32,
                                    [loaded.Operand, new(IrOperandKind.Constant, 56, identity + ":signed-byte-left-count")],
                                    [shifted.Operand], -1, identity + ":signed-byte-left"));
                                // Preserve the abstract producer identity used by CFG locals/phis.
                                V2Value signed = V2Value.Definition(RestrictedCilTypeV1.Int32, identity);
                                emissions.Add(new(source.Offset, HybridCpuOpcode.SRAI, RestrictedCilTypeV1.Int32,
                                    [shifted.Operand, new(IrOperandKind.Constant, 56, identity + ":signed-byte-right-count")],
                                    [signed.Operand], -1, identity));
                                loaded = signed;
                            }
                            state.Stack.Add(loaded);
                            break;
                        }
                    case 0x9c:
                    case 0x9d:
                    case 0x9e:
                    case 0xa2:
                        {
                            V2Value value = Pop(); V2Value index = Pop(); V2Value array = Pop();
                            if (index.Type == RestrictedCilTypeV1.UInt32)
                            {
                                V2Value canonicalIndex = V2Value.Definition(RestrictedCilTypeV1.Int32, $"{identity}:index-i4");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDIW, RestrictedCilTypeV1.Int32,
                                    [index.Operand, new(IrOperandKind.Constant, 0, $"{identity}:index-zero")],
                                    [canonicalIndex.Operand], -1, $"{identity}:index-i4"));
                                index = canonicalIndex;
                            }
                            string helper = source.Encoding switch { 0x9c => "__hybridcpu_managed_array_store_i1", 0x9d => "__hybridcpu_managed_array_store_i2",
                                0x9e => "__hybridcpu_managed_array_store_i4", _ => "__hybridcpu_managed_array_store_ref" };
                            EmitRuntimeCall("stelem", helper, [array, index, value], RestrictedCilTypeV1.Void, IrMemoryEffectKind.Write);
                            break;
                        }
                    case 0x8c:
                    case 0xa5:
                        {
                            V2Value sourceValue = Pop(); RestrictedCilValueTypeBindingV1 binding = dataflow.Values[source.Offset];
                            V2Value handle = V2Value.Constant(unchecked((long)binding.TypeHandle), RestrictedCilTypeV1.NativeUInt, $"{identity}:value-type-handle");
                            string width = binding.TypeDescriptor.ValueTypeShape!.PayloadSizeBytes == 4 ? "i4" : "i8";
                            if (source.Encoding == 0x8c)
                                state.Stack.Add(EmitRuntimeCall("box", $"__hybridcpu_managed_box_{width}", [handle, sourceValue], RestrictedCilTypeV1.ObjectReference, IrMemoryEffectKind.Write)!);
                            else
                                state.Stack.Add(EmitRuntimeCall("unbox", $"__hybridcpu_managed_unbox_{width}", [handle, sourceValue], binding.ValueType, IrMemoryEffectKind.Read)!);
                            break;
                        }
                    case 0x7e:
                    case 0x80:
                        {
                            RestrictedCilFieldLayoutBindingV1 field = dataflow.Fields[source.Offset];
                            RestrictedCilTypeInitializationBindingV1 init = _typeInitializationBindings[field.DeclaringType];
                            HybridCpuManagedFieldLayoutV1 layout = field.TypeDescriptor.StaticLayout.Fields.Single(row => row.Identity == field.FieldName);
                            V2Value handle = V2Value.Constant(unchecked((long)init.TypeHandle), RestrictedCilTypeV1.NativeUInt, $"{identity}:static-type-handle");
                            EmitRuntimeCall("type-init", "__hybridcpu_managed_ensure_type_initialized", [handle], RestrictedCilTypeV1.Void,
                                IrMemoryEffectKind.Read | IrMemoryEffectKind.Write);
                            V2Value offset = V2Value.Constant(layout.OffsetBytes, RestrictedCilTypeV1.NativeUInt, $"{identity}:static-offset");
                            bool reference = field.FieldType == RestrictedCilTypeV1.ObjectReference;
                            if (source.Encoding == 0x7e)
                            {
                                string helper = reference ? "__hybridcpu_managed_static_load_ref" : "__hybridcpu_managed_static_load_i4";
                                // Successor block states name the CIL definition, not the helper's temporary.
                                state.Stack.Add(EmitRuntimeCall("static-load", helper, [handle, offset], field.FieldType,
                                    IrMemoryEffectKind.Read, resultIdentity: identity)!);
                            }
                            else
                            {
                                V2Value value = Pop();
                                string helper = reference ? "__hybridcpu_managed_static_store_ref" : "__hybridcpu_managed_static_store_i4";
                                EmitRuntimeCall("static-store", helper, [handle, offset, value], RestrictedCilTypeV1.Void, IrMemoryEffectKind.Write);
                            }
                            break;
                        }
                    case 0x7b:
                        {
                            RestrictedCilFieldLayoutBindingV1 binding = dataflow.Fields[source.Offset];
                            if (signature.Receiver is not null)
                            {
                                V2Value payload = Pop();
                                V2Value loaded = V2Value.Definition(StackType(binding.FieldType), identity);
                                EmitReceiverField(emissions, source, identity, binding, payload, null, loaded);
                                state.Stack.Add(loaded);
                                break;
                            }
                            if (binding.IsScalarValueProjection && state.Stack[^1].Type != RestrictedCilTypeV1.ObjectReference)
                            {
                                V2Value scalar = Pop();
                                V2Value loaded = V2Value.Definition(binding.FieldType, identity + ":scalar-field");
                                emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, binding.FieldType,
                                    [scalar.Operand, new(IrOperandKind.Constant, 0, identity + ":zero")],
                                    [loaded.Operand], -1, identity + ":scalar-field-copy"));
                                state.Stack.Add(loaded);
                                break;
                            }
                            HybridCpuManagedFieldLoweringPlanV1 plan = new HybridCpuManagedFieldLoweringV1().Lower(
                                binding.TypeDescriptor, binding.FieldName, false, HybridCpuManagedFieldAccessKindV1.Load);
                            V2Value receiver = Pop();
                            V2Value abiInput = V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"{identity}:null-check-arg-abi");
                            V2Value abiResult = V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"{identity}:null-check-result-abi");
                            V2Value checkedReceiver = V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"{identity}:checked-receiver");
                            V2Value address = V2Value.Definition(RestrictedCilTypeV1.NativeUInt, $"{identity}:field-address");
                            V2Value result = V2Value.Definition(binding.FieldType, identity);
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, receiver.Type,
                                [receiver.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], [abiInput.Operand], -1,
                                $"{identity}:null-check-arg-copy"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JAL, receiver.Type, [abiInput.Operand], [abiResult.Operand],
                                -1, $"{identity}:null-check", "__hybridcpu_managed_null_check"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, receiver.Type,
                                [abiResult.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], [checkedReceiver.Operand], -1,
                                $"{identity}:null-check-result-copy"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                                [checkedReceiver.Operand, new(IrOperandKind.Constant, checked((ulong)plan.Steps[1].Immediate), $"{identity}:field-offset")],
                                [address.Operand], -1, $"{identity}:field-address"));
                            HybridCpuOpcode load = plan.Steps[2].SizeBytes switch
                            {
                                1 when IsSigned(binding.FieldType) => HybridCpuOpcode.LB,
                                1 => HybridCpuOpcode.LBU,
                                2 when IsSigned(binding.FieldType) => HybridCpuOpcode.LH,
                                2 => HybridCpuOpcode.LHU,
                                8 => HybridCpuOpcode.LD,
                                4 when IsSigned(binding.FieldType) => HybridCpuOpcode.LW,
                                4 => HybridCpuOpcode.LWU,
                                _ => throw new InvalidOperationException($"Verified field layout for '{binding.DeclaringType}::{binding.FieldName}' has no qualified scalar load width ({plan.Steps[2].SizeBytes} bytes).")
                            };
                            emissions.Add(new(source.Offset, load, binding.FieldType, [address.Operand], [result.Operand], -1,
                                identity, null, IrMemoryEffectKind.Read));
                            state.Stack.Add(result);
                            break;
                        }
                    case 0x7d:
                        {
                            RestrictedCilFieldLayoutBindingV1 binding = dataflow.Fields[source.Offset];
                            if (signature.Receiver is not null)
                            {
                                V2Value stored = Pop();
                                V2Value payload = Pop();
                                EmitReceiverField(emissions, source, identity, binding, payload, stored, null);
                                break;
                            }
                            HybridCpuManagedFieldLoweringPlanV1 plan = new HybridCpuManagedFieldLoweringV1().Lower(
                                binding.TypeDescriptor, binding.FieldName, false, HybridCpuManagedFieldAccessKindV1.Store);
                            V2Value value = Pop();
                            V2Value receiver = Pop();
                            V2Value abiInput = V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"{identity}:null-check-arg-abi");
                            V2Value abiResult = V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"{identity}:null-check-result-abi");
                            V2Value checkedReceiver = V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"{identity}:checked-receiver");
                            V2Value address = V2Value.Definition(RestrictedCilTypeV1.NativeUInt, $"{identity}:field-address");
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, receiver.Type,
                                [receiver.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], [abiInput.Operand], -1,
                                $"{identity}:null-check-arg-copy"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JAL, receiver.Type, [abiInput.Operand], [abiResult.Operand],
                                -1, $"{identity}:null-check", "__hybridcpu_managed_null_check"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, receiver.Type,
                                [abiResult.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], [checkedReceiver.Operand], -1,
                                $"{identity}:null-check-result-copy"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                                [checkedReceiver.Operand, new(IrOperandKind.Constant, checked((ulong)plan.Steps[1].Immediate), $"{identity}:field-offset")],
                                [address.Operand], -1, $"{identity}:field-address"));
                            HybridCpuOpcode store = plan.Steps[2].SizeBytes switch
                            {
                                1 => HybridCpuOpcode.SB,
                                2 => HybridCpuOpcode.SH,
                                8 => HybridCpuOpcode.SD,
                                4 => HybridCpuOpcode.SW,
                                _ => throw new InvalidOperationException($"Verified field layout for '{binding.DeclaringType}::{binding.FieldName}' has no qualified scalar store width ({plan.Steps[2].SizeBytes} bytes).")
                            };
                            emissions.Add(new(source.Offset, store, binding.FieldType, [address.Operand, value.Operand], [], -1,
                                identity, null, IrMemoryEffectKind.Write));
                            break;
                        }
                    case 0x2b:
                    case 0x38:
                    case 0xdd:
                    case 0xde:
                        if (ehPlan?.FinallyContinuations.Continuations.SingleOrDefault(
                                continuation => continuation.LeaveOffset == source.Offset) is { } continuation)
                        {
                            V2Value token = V2Value.Definition(RestrictedCilTypeV1.NativeUInt, identity + ":finally-token");
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                                [new(IrOperandKind.ArchitecturalRegister, 0, identity + ":finally-token-zero"),
                                 new(IrOperandKind.Constant, checked((ulong)continuation.Token), identity + ":finally-token-value")],
                                [token.Operand], -1, identity + ":finally-token"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.SD, RestrictedCilTypeV1.NativeUInt,
                                [new(IrOperandKind.ArchitecturalRegister, 2, identity + ":finally-token-sp"), token.Operand],
                                [], -1, identity + ":finally-token-store", null, IrMemoryEffectKind.Write,
                                false, ManagedEhFrameHomesV1.FinallyContinuationTokenSlot));
                            for (int release = 0; release < continuation.Steps[0].ReleaseCatchScopesBeforeEntry; release++)
                                EmitRuntimeCall($"leave-catch-before-finally-{release}", HybridCpuManagedEhScopeEmitterV1.LeaveCatchSymbol, [],
                                    RestrictedCilTypeV1.Void, IrMemoryEffectKind.Write);
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JAL, RestrictedCilTypeV1.NativeUInt,
                                [], [], continuation.Steps[0].HandlerOffset, identity + ":enter-finally"));
                        }
                        else
                        {
                            if (ehPlan?.LeaveTransfers.SingleOrDefault(transfer => transfer.IlOffset == source.Offset) is { } leave &&
                                leave.Actions.Any(action => action.Kind == ManagedEhLeaveActionKindV1.ReleaseCatchScope))
                                EmitRuntimeCall("leave-catch", HybridCpuManagedEhScopeEmitterV1.LeaveCatchSymbol, [],
                                    RestrictedCilTypeV1.Void, IrMemoryEffectKind.Write);
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JAL, RestrictedCilTypeV1.NativeUInt, [], [], source.BranchTarget, identity));
                        }
                        break;
                    case 0xfe1a:
                        ManagedEhClausePlanV1 enclosingCatch = ehPlan!.Clauses.Where(clause =>
                            clause.Kind == HybridCpuManagedEhClauseKindV1.Catch &&
                            source.Offset >= clause.HandlerOffset && source.Offset < clause.HandlerOffset + clause.HandlerLength)
                            .OrderBy(static clause => clause.HandlerLength).First();
                        if (ehHandlerEntries?.SingleOrDefault(entry => entry.IlOffset == enclosingCatch.HandlerOffset)
                            ?.ExceptionReferenceRegister != 10)
                            throw new InvalidOperationException("Rethrow lacks the exact enclosing catch ABI entry.");
                        // An inner handler replaces/clears the native current root. Use the
                        // original lexical catch definition; its SSA use keeps it GC-live.
                        V2Value caughtReference = CatchExceptionValue(enclosingCatch.HandlerOffset);
                        V2Value ehAddress = V2Value.Definition(RestrictedCilTypeV1.NativeUInt, identity + ":rethrow-state-address");
                        V2Value ehState = V2Value.Definition(RestrictedCilTypeV1.NativeUInt, identity + ":rethrow-state");
                        emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeUInt,
                            [new(IrOperandKind.ArchitecturalRegister, 3, identity + ":gp"),
                             new(IrOperandKind.Constant, (ulong)HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset, identity + ":state-offset")],
                            [ehAddress.Operand], -1, identity + ":rethrow-state-address"));
                        emissions.Add(new(source.Offset, HybridCpuOpcode.LD, RestrictedCilTypeV1.NativeUInt,
                            [ehAddress.Operand], [ehState.Operand], -1, identity + ":rethrow-state", null, IrMemoryEffectKind.Read));
                        emissions.Add(new(source.Offset, HybridCpuOpcode.SD, RestrictedCilTypeV1.ObjectReference,
                            [ehState.Operand, caughtReference.Operand], [], -1, identity + ":rethrow-original-root", null, IrMemoryEffectKind.Write));
                        EmitRuntimeCall("rethrow", "__hybridcpu_managed_rethrow", [], RestrictedCilTypeV1.Void,
                            IrMemoryEffectKind.Read);
                        break;
                    case 0xdc:
                        V2Value finallyToken = V2Value.Definition(RestrictedCilTypeV1.NativeUInt, identity + ":finally-token-load");
                        emissions.Add(new(source.Offset, HybridCpuOpcode.LD, RestrictedCilTypeV1.NativeUInt,
                            [new(IrOperandKind.ArchitecturalRegister, 2, identity + ":finally-token-load-sp")],
                            [finallyToken.Operand], -1, identity + ":finally-token-load", null,
                            IrMemoryEffectKind.Read, false, ManagedEhFrameHomesV1.FinallyContinuationTokenSlot));
                        EmitRuntimeCall("endfinally", "__hybridcpu_managed_endfinally", [finallyToken], RestrictedCilTypeV1.Void,
                            IrMemoryEffectKind.Read);
                        break;
                    case 0x2c:
                    case 0x2d:
                    case 0x39:
                    case 0x3a:
                        {
                            V2Value condition = Pop();
                            condition=ComparisonWord(condition,false,"condition");
                            emissions.Add(new(source.Offset, source.Encoding is 0x2d or 0x3a ? HybridCpuOpcode.BNE : HybridCpuOpcode.BEQ,
                                condition.Type, [condition.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], [], source.BranchTarget, identity));
                            break;
                        }
                    case >= 0x2e and <= 0x37:
                    case >= 0x3b and <= 0x44:
                        {
                            V2Value right = Pop();
                            V2Value left = Pop();
                            ushort normalized = source.Encoding >= 0x3b ? (ushort)(source.Encoding - 0x0d) : source.Encoding;
                            if(WordPair(left,right)){bool unsigned=normalized is >=0x34 and <=0x37;left=ComparisonWord(left,unsigned,"left");right=ComparisonWord(right,unsigned,"right");}
                            HybridCpuOpcode opcode = normalized switch
                            {
                                0x2e => HybridCpuOpcode.BEQ,
                                0x2f => HybridCpuOpcode.BGE,
                                0x30 => HybridCpuOpcode.BLT,
                                0x31 => HybridCpuOpcode.BGE,
                                0x32 => HybridCpuOpcode.BLT,
                                0x33 => HybridCpuOpcode.BNE,
                                0x34 => HybridCpuOpcode.BGEU,
                                0x35 => HybridCpuOpcode.BLTU,
                                0x36 => HybridCpuOpcode.BGEU,
                                0x37 => HybridCpuOpcode.BLTU,
                                _ => throw new InvalidOperationException("Verified relational branch is outside the mapping table.")
                            };
                            IrOperand[] uses = normalized is 0x30 or 0x31 or 0x35 or 0x36
                                ? [right.Operand, left.Operand] : [left.Operand, right.Operand];
                            emissions.Add(new(source.Offset, opcode, left.Type, uses, [], source.BranchTarget, identity));
                            break;
                        }
                    case 0x45:
                        {
                            V2Value selector = Pop();
                            selector=ComparisonWord(selector,false,"selector");
                            for (int caseIndex = 0; caseIndex < source.SwitchTargets!.Count; caseIndex++)
                            {
                                string caseIdentity = $"{identity}:case-{caseIndex}";
                                emissions.Add(new(source.Offset, HybridCpuOpcode.BEQ, selector.Type,
                                    [selector.Operand, new(IrOperandKind.Constant, checked((ulong)caseIndex), $"{caseIdentity}:index")], [],
                                    source.SwitchTargets[caseIndex], caseIdentity));
                            }
                            break;
                        }
                    case 0x2a:
                        if (signature.ReturnType == RestrictedCilTypeV1.Void)
                        {
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JALR, StackType(signature.ReturnType), [], [], -1, identity));
                        }
                        else
                        {
                            V2Value returned = Pop();
                            V2Value abiReturn = V2Value.Definition(returned.Type, $"{identity}:return-abi");
                            IrOperand[] returnUses = returned.Operand.Kind == IrOperandKind.Constant
                                ? [new(IrOperandKind.ArchitecturalRegister, 0, $"{identity}:return-zero"), returned.Operand]
                                : [returned.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")];
                            emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, returned.Type,
                                returnUses, [abiReturn.Operand], -1, $"{identity}:return-copy"));
                            emissions.Add(new(source.Offset, HybridCpuOpcode.JALR, returned.Type, [abiReturn.Operand], [], -1, identity));
                        }
                        break;
                }
                // Bind stores to mapped SSA after each definition, including continuation reloads.
                foreach (ManagedEhHomeAccessV1? access in ehHomeAccesses?.Accesses.Where(row => row.Kind == ManagedEhHomeAccessKindV1.StoreAfterDefinition && row.IlOffset == source.Offset) ?? [])
                {
                    int homeIndex = int.Parse(access.Slot.AsSpan(access.Slot.IndexOf(':') + 1), CultureInfo.InvariantCulture);
                    V2Value value = access.Slot.StartsWith("local:", StringComparison.Ordinal) ? state.Locals[homeIndex] : state.Arguments[homeIndex];
                    if (!value.Initialized || value.Type != access.Type)
                        throw new InvalidOperationException("Mapped EH home definition differs from the exact typed home.");
                    mappedHomeValues.Add((source.Offset, access.Slot), value);
                }
            }
        }
        if (emissions.Count == 0)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1006", "The method has no Canonical IR operations after mapping.", provenance.MethodIdentity, provenance);

        List<Emission> argumentCopies = new List<Emission>();
        for (int parameter = 0; parameter < signature.Parameters.Count; parameter++)
        {
            V2Value argument = V2Value.Argument(parameter, StackType(signature.Parameters[parameter]), provenance.CanonicalMethodLocalIdentity);
            string abiIdentity = $"{argument.Id}:abi";
            argumentCopies.Add(new(graph.Blocks[0].StartOffset, HybridCpuOpcode.ADDI, argument.Type,
                [new(IrOperandKind.VirtualValue, 0, abiIdentity), new(IrOperandKind.Constant, 0, $"{abiIdentity}:zero")],
                [argument.Operand], -1, $"{argument.Id}:argument-copy"));
        }
        emissions.InsertRange(0, argumentCopies);
        if (ehHomeAccesses is not null)
            emissions = InsertEhHomeEmissions(emissions, ehHomeAccesses, ehHandlerEntries ?? [], mappedHomeValues);
        emissions = LowerV2ParallelCopies(emissions, graph, dataflow, analysis, signature, provenance);
        emissions = MaterializeV2Constants(emissions);
        emissions = ExpandV2ManagedDirectCalls(emissions);
        Dictionary<int, int> relaxationTargets = BuildV2TargetIndices(emissions, graph, provenance, ehPlan?.FinallyContinuations.Continuations.Select(static row => row.TargetOffset));
        emissions = RelaxV2SymbolicBranches(emissions, relaxationTargets, minimumInstructionSpan: 0);
        if (emissions.Count > _graphBudgets.MaximumIlInstructionsPerMethod * 32)
            return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2110", "Expanded native instruction budget exhausted.", provenance.MethodIdentity, provenance);

        Dictionary<int, int> targetIndices = BuildV2TargetIndices(emissions, graph, provenance, ehPlan?.FinallyContinuations.Continuations.Select(static row => row.TargetOffset));
        HybridCpuInstructionWord[] words = emissions.Select(static emission => new HybridCpuInstructionWord
        {
            OpCode = (uint)(emission.LongBranchPart == LongBranchRelocationPart.PcRelativeLow
                ? HybridCpuOpcode.JAL
                : emission.CallTargetIdentity is null && !emission.IsIndirectCall ? emission.Opcode : HybridCpuOpcode.ADDI),
            DataTypeValue = ToDataType(emission.Type),
            PredicateMask = byte.MaxValue,
            VirtualThreadId = 0
        }).ToArray();
        IrLabelDeclaration[] labels = targetIndices.OrderBy(static pair => pair.Key).Select(pair => new IrLabelDeclaration($"cil_{pair.Key:x4}", pair.Value))
            .Concat(emissions.Select((emission, index) => (emission, index))
                .Where(static pair => pair.emission.Identity.EndsWith(":long-branch-skip", StringComparison.Ordinal))
                .Select(static pair => new IrLabelDeclaration(pair.emission.Identity, pair.index)))
            .ToArray();
        IrControlFlowTargetReference[] references = emissions.Select((emission, index) => (emission, index))
            .Where(static pair => pair.emission.BranchTarget >= 0 ||
                pair.emission.BranchTargetSymbolName is not null && pair.emission.LongBranchPart is not
                    (LongBranchRelocationPart.PcRelativeHigh or LongBranchRelocationPart.ManagedCallPcRelativeHigh))
            .Select(pair => new IrControlFlowTargetReference(pair.index,
                pair.emission.BranchTargetSymbolName ?? $"cil_{pair.emission.BranchTarget:x4}",
                pair.emission.Opcode == HybridCpuOpcode.JAL || pair.emission.LongBranchPart == LongBranchRelocationPart.PcRelativeLow
                    ? IrControlTransferKind.Branch : IrControlTransferKind.ConditionalBranch)).ToArray();
        IrProgram shape;
        try { shape = new HybridCpuIrBuilder().BuildProgram(0, words, labelDeclarations: labels, controlFlowTargetReferences: references); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL0022",
                $"Core rejected the mapped target instruction shape: {exception.Message}", provenance.MethodIdentity, provenance);
        }

        SortedDictionary<string, IrVirtualValueV1> values = new SortedDictionary<string, IrVirtualValueV1>(StringComparer.Ordinal);
        for (int parameter = 0; parameter < signature.Parameters.Count; parameter++)
        {
            V2Value argument = V2Value.Argument(parameter, StackType(signature.Parameters[parameter]), provenance.CanonicalMethodLocalIdentity);
            values[$"{argument.Id}:abi"] = CreateValue($"{argument.Id}:abi", argument.Type,
                parameter < HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Count
                    ? HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[parameter] : null);
            values[argument.Id] = CreateValue(argument.Id, argument.Type);
        }
        foreach (V2Phi phi in dataflow.Phis) values[phi.Id] = CreateValue(phi.Id, phi.Type);
        HashSet<string> definedValues = new HashSet<string>(values.Keys, StringComparer.Ordinal);
        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        List<IrInstruction> mappedInstructions = new List<IrInstruction>(emissions.Count);
        for (int index = 0; index < emissions.Count; index++)
        {
            Emission emission = emissions[index];
            foreach (IrOperand use in emission.Uses.Where(static operand => operand.Kind == IrOperandKind.VirtualValue))
            {
                if (!values.ContainsKey(use.Name))
                    values[use.Name] = CreateValue(use.Name, TypeForValue(use.Name, dataflow, signature, provenance),
                        FixedCallRegister(use.Name));
                accesses.Add(new(use.Name, index, IrValueAccessKind.Use));
            }
            foreach (IrOperand definition in emission.Defs)
            {
                bool phiOrCopyDefinition = dataflow.Phis.Any(phi => phi.Id == definition.Name) ||
                    definition.Name.Contains(":pcopy-temp:", StringComparison.Ordinal) ||
                    emission.Identity.EndsWith(":argument-copy", StringComparison.Ordinal);
                values.TryAdd(definition.Name, CreateValue(definition.Name, emission.Type,
                    FixedCallRegister(definition.Name)));
                if (!definedValues.Add(definition.Name) && !phiOrCopyDefinition)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0024",
                        $"Mapped value definition '{definition.Name}' is duplicated.", emission.Identity, provenance);
                accesses.Add(new(definition.Name, index, IrValueAccessKind.Def));
            }
            IrInstruction native = shape.Instructions[index];
            IrControlFlowKind controlFlowKind = emission.CallTargetIdentity is null && !emission.IsIndirectCall
                ? native.Annotation.ControlFlowKind
                : IrControlFlowKind.Call;
            IrOperand[] architecturalUses = ArchitecturalAliases(emission.Uses, values, "abi-use");
            IrOperand[] architecturalDefs = ArchitecturalAliases(emission.Defs, values, "abi-def");
            if (emission.CallTargetIdentity is not null || emission.IsIndirectCall)
            {
                architecturalDefs = architecturalDefs.Concat(
                        HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters
                            .Select(static register => new IrOperand(IrOperandKind.ArchitecturalRegister,
                                checked((ulong)register), $"call-clobber:x{register}")))
                    .DistinctBy(static operand => operand.Value)
                    .OrderBy(static operand => operand.Value)
                    .ToArray();
            }
            mappedInstructions.Add(native with
            {
                Opcode = emission.Opcode,
                Operands = emission.CallTargetIdentity is null && !emission.IsIndirectCall
                    ? [.. emission.Uses, .. emission.Defs]
                    : [.. emission.Uses, .. emission.Defs,
                        .. HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters
                            .Select(static register => new IrOperand(IrOperandKind.ArchitecturalRegister,
                                checked((ulong)register), $"call-clobber:x{register}"))],
                Immediate = ImmediateFor(emission),
                Annotation = emission.CallTargetIdentity is null && !emission.IsIndirectCall
                    ? native.Annotation with
                    {
                        Serialization = emission.LongBranchPart == LongBranchRelocationPart.ManagedCallPcRelativeHigh
                            ? IrSerializationKind.ControlFlowBoundary
                            : native.Annotation.Serialization,
                        Uses = [.. emission.Uses, .. architecturalUses],
                        Defs = [.. emission.Defs, .. architecturalDefs],
                        BranchTargetSymbolName = emission.BranchTargetSymbolName ?? native.Annotation.BranchTargetSymbolName
                    }
                    : native.Annotation with
                    {
                        ResourceClass = IrResourceClass.ControlFlow,
                        LatencyClass = IrLatencyClass.ControlFlow,
                        MinimumLatencyCycles = 1,
                        LegalSlots = IrIssueSlotMask.Control,
                        Serialization = IrSerializationKind.ControlFlowBoundary | IrSerializationKind.ExclusiveCycle,
                        StructuralResources = IrStructuralResource.BranchResolver | IrStructuralResource.ControlSequencer,
                        Uses = [.. emission.Uses, .. architecturalUses],
                        Defs = [.. emission.Defs, .. architecturalDefs],
                        ControlFlowKind = controlFlowKind,
                        RequiredSlotClass = IrSlotClass.BranchControl,
                        BindingKind = IrSlotBindingKind.HardPinned,
                        BranchTargetSymbolName = emission.CallTargetIdentity,
                        ResolvedBranchTargetInstructionIndex = null,
                        EncodedBranchTarget = null
                    },
                StableIdentity = emission.Identity,
                SourceSpan = new IrSourceSpan(provenance.MethodIdentity, 1,
                    checked(emission.CilOffset + 1), 1, checked(emission.CilOffset + 1),
                    emission.CilOffset, 0),
                CanonicalType = ToCanonicalType(emission.Type),
                Semantics = new(
                    emission.Type != RestrictedCilTypeV1.ManagedByRef && emission.Opcode is HybridCpuOpcode.ADD or HybridCpuOpcode.SUB or HybridCpuOpcode.MUL or HybridCpuOpcode.ADDI
                        ? IrIntegerOverflowSemantics.Wrap : IrIntegerOverflowSemantics.NotApplicable,
                    IrShiftSemantics.NotApplicable, emission.Type == RestrictedCilTypeV1.ManagedByRef ? IrPointerArithmeticSemantics.BoundsChecked : IrPointerArithmeticSemantics.NotApplicable,
                    IrUndefinedValueSemantics.NotApplicable, false, emission.MemoryEffects != IrMemoryEffectKind.None),
                OriginChain = new(1, [new(emission.Identity, IrSourceOriginKind.Cil, "HybridCPU.Compiler.Cil", "6.1", null, IrFrontendEvidenceTrust.ValidatedStructural)]),
                SideEffects = new(emission.MemoryEffects == IrMemoryEffectKind.None
                        ? IrCanonicalMemoryEffectV1.None
                        : new IrCanonicalMemoryEffectV1(emission.MemoryEffects, IrAddressSpaceIdentity.Generic,
                            IrMemoryOrdering.NotAtomic, null, null),
                    emission.CallTargetIdentity is not null || emission.IsIndirectCall ? IrArchitecturalEffectKind.Control | IrArchitecturalEffectKind.Call
                    : emission.Opcode == HybridCpuOpcode.JALR && emission.LongBranchPart != LongBranchRelocationPart.PcRelativeLow
                        ? IrArchitecturalEffectKind.Control | IrArchitecturalEffectKind.Return
                    : emission.BranchTarget >= 0 ? IrArchitecturalEffectKind.Control : IrArchitecturalEffectKind.None)
            });
            if (emission.FixedFrameSlotIdentity is not null)
                mappedInstructions[^1] = mappedInstructions[^1] with
                {
                    Annotation = mappedInstructions[^1].Annotation with
                    { FixedFrameSlotIdentity = emission.FixedFrameSlotIdentity }
                };
            if (!emission.IsManagedGcSafepoint)
                mappedInstructions[^1] = mappedInstructions[^1] with
                {
                    Annotation = mappedInstructions[^1].Annotation with { IsManagedGcSafepoint = false }
                };
        }
        IrBasicBlock[] coreBlocks = shape.BasicBlocks.Select(block => block with
        {
            Instructions = mappedInstructions.Where(instruction => instruction.Index >= block.StartInstructionIndex && instruction.Index <= block.EndInstructionIndex).ToArray(),
            FunctionName = provenance.CanonicalMethodLocalIdentity
        }).ToArray();
        string evidencePayload = Hash(string.Join('|', analysis.GraphDigest, analysis.StateDigest, analysis.SsaDigest, analysis.ContractDigest,
            signature.Receiver?.PlanDigest ?? string.Empty));
        IrProgram program = shape with
        {
            Instructions = mappedInstructions,
            ControlFlowGraph = shape.ControlFlowGraph with { Blocks = coreBlocks },
            ValueFlow = new("hybridcpu.value-flow/v1", 1, values.Values.ToArray(), accesses
                .OrderBy(static access => access.InstructionIndex).ThenBy(static access => access.Kind).ThenBy(static access => access.ValueId, StringComparer.Ordinal).ToArray()),
            FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Create([
                new($"cil:{provenance.CanonicalMethodLocalIdentity}:cfg-ssa", IrAnalysisEvidenceKind.Loop, IrAnalysisEvidenceTrust.FrontendStaticEvidence,
                    IrAliasEvidencePrecision.NotApplicable, "HybridCPU.Compiler.Cil", "6.1", evidencePayload, false, true)
            ]),
            Contract = shape.Contract with
            {
                RequiredCapabilities =
                [
                    "frontend.scalar-control-flow-v2/v1",
                    .. (signature.Receiver is null ? Array.Empty<string>() : new[] { "backend.managed-receiver-callsite-proof-required/v1" }),
                    "optimization.loop-mii-optional-ordinary-fallback/v1",
                    .. (emissions.Any(static emission => emission.CallTargetIdentity is not null || emission.IsIndirectCall)
                        ? new[] { "backend.managed-call-lowering-required/v1" }
                        : Array.Empty<string>()),
                    "isa.hybridcpu-w8-native-v1"
                ],
                DerivedFacts = new(new(analysis.Generation), null, null, null, null, null)
            }
        };
        IrFrontendAdapterResultV1 boundary = CanonicalIrFrontendBoundaryV1.Validate(program);
        return boundary.Status == IrFrontendAdapterStatus.Success
            ? new(RestrictedCilImportStatusV1.Success, program, [], provenance, analysis)
            : new(boundary.Status == IrFrontendAdapterStatus.InvalidInput ? RestrictedCilImportStatusV1.InvalidInput
                : boundary.Status == IrFrontendAdapterStatus.Unsupported ? RestrictedCilImportStatusV1.Unsupported
                : RestrictedCilImportStatusV1.UnknownSemantics, null, boundary.Diagnostics, provenance, analysis);
    }

    private static V2Value CatchExceptionValue(int offset) =>
        V2Value.Definition(RestrictedCilTypeV1.ObjectReference, $"eh:exception:{offset}");

    private static List<Emission> InsertEhHomeEmissions(IReadOnlyList<Emission> source,
        ManagedEhHomeAccessPlanV1 plan, IReadOnlyList<ManagedEhHandlerEntryV1> handlerEntries,
        IReadOnlyDictionary<(int Offset, string Slot), V2Value> mappedHomeValues)
    {
        Dictionary<int, ManagedEhHomeAccessV1[]> before = plan.Accesses.Where(static access => access.Kind != ManagedEhHomeAccessKindV1.StoreAfterDefinition)
            .GroupBy(static access => access.IlOffset).ToDictionary(static group => group.Key, static group => group.ToArray());
        Dictionary<int, ManagedEhHomeAccessV1[]> after = plan.Accesses.Where(static access => access.Kind == ManagedEhHomeAccessKindV1.StoreAfterDefinition)
            .GroupBy(static access => access.IlOffset).ToDictionary(static group => group.Key, static group => group.ToArray());
        List<Emission> result = new List<Emission>(source.Count + plan.Accesses.Count + handlerEntries.Count);
        Dictionary<int, Emission[]> sourceGroups = source.GroupBy(static emission => emission.CilOffset)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        foreach (int offset in sourceGroups.Keys.Concat(plan.Accesses.Select(static access => access.IlOffset))
                     .Concat(handlerEntries.Select(static entry => entry.IlOffset)).Distinct().Order())
        {
            Emission[] rows = sourceGroups.GetValueOrDefault(offset) ?? [];
            Emission[] abiCopies = rows.Where(static emission => emission.Identity.EndsWith(":argument-copy", StringComparison.Ordinal)).ToArray();
            result.AddRange(abiCopies);
            ManagedEhHandlerEntryV1? handler = handlerEntries.SingleOrDefault(entry => entry.IlOffset == offset);
            if (handler?.ExceptionReferenceRegister is int exceptionRegister)
            {
                string abiIdentity = $"eh:handler:il_{offset:x4}:exception-abi";
                result.Add(new(offset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.ObjectReference,
                    [new(IrOperandKind.ArchitecturalRegister, checked((ulong)exceptionRegister), abiIdentity),
                        new(IrOperandKind.Constant, 0, abiIdentity + ":zero")],
                    [CatchExceptionValue(offset).Operand], -1,
                    $"eh:handler:il_{offset:x4}:exception-copy"));
                if (exceptionRegister != 10)
                    throw new InvalidOperationException("Managed catch entry is not bound to the exception ABI register x10.");
            }
            if (before.TryGetValue(offset, out ManagedEhHomeAccessV1[]? leading))
                result.AddRange(leading.Select(HomeEmission));
            result.AddRange(rows.Where(static emission => !emission.Identity.EndsWith(":argument-copy", StringComparison.Ordinal)));
            if (after.TryGetValue(offset, out ManagedEhHomeAccessV1[]? trailing))
                result.AddRange(trailing.Select(HomeEmission));
        }
        return result;

        Emission HomeEmission(ManagedEhHomeAccessV1 access)
        {
            string identity = $"eh-home:{access.Kind}:{access.Slot}:il_{access.IlOffset:x4}";
            string slot = $"eh-home:{access.Slot}";
            IrOperand sp = new IrOperand(IrOperandKind.ArchitecturalRegister, 2, identity + ":sp");
            if (access.Kind == ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry)
            {
                IrOperand definition = new IrOperand(IrOperandKind.VirtualValue, 0, access.ValueIdentity);
                return new(access.IlOffset, HybridCpuOpcode.LD, access.Type, [sp], [definition], -1,
                    identity, null, IrMemoryEffectKind.Read, false, slot);
            }
            IrOperand value = access.Kind == ManagedEhHomeAccessKindV1.StoreAfterDefinition
                ? mappedHomeValues[(access.IlOffset, access.Slot)].Operand : access.ValueKind switch
            {
                ManagedEhHomeValueKindV1.ZeroConstant => new(IrOperandKind.ArchitecturalRegister, 0, access.ValueIdentity),
                ManagedEhHomeValueKindV1.NonZeroConstant when access.ConstantValue is ulong constant =>
                    new(IrOperandKind.Constant, constant, access.ValueIdentity),
                ManagedEhHomeValueKindV1.ExistingSsaValue => new(IrOperandKind.VirtualValue, 0, access.ValueIdentity),
                _ => throw new InvalidOperationException("EH home store lacks exact scalar value provenance.")
            };
            return new(access.IlOffset, HybridCpuOpcode.SD, access.Type, [sp, value], [], -1,
                identity, null, IrMemoryEffectKind.Write, false, slot);
        }
    }

    private static ushort ImmediateFor(Emission emission)
    {
        if (emission.LongBranchPart != LongBranchRelocationPart.None)
            return 0;
        if (emission.Opcode == HybridCpuOpcode.JALR)
            return emission.IsIndirectCall ? (ushort)0 :
                (ushort)HybridCPU.Compiler.Core.Target.HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes;
        if (emission.Opcode is not (HybridCpuOpcode.ADDI or HybridCpuOpcode.ANDI or HybridCpuOpcode.ORI or HybridCpuOpcode.XORI or HybridCpuOpcode.SLTI or HybridCpuOpcode.SLTIU or HybridCpuOpcode.SLLI or HybridCpuOpcode.SRAI or HybridCpuOpcode.SRLI))
            return 0;
        IrOperand? immediate = emission.Uses.LastOrDefault(static operand => operand.Kind == IrOperandKind.Constant);
        return immediate is null ? (ushort)0 : unchecked((ushort)(short)immediate.Value);
    }

    private static IrOperand[] ArchitecturalAliases(
        IReadOnlyList<IrOperand> operands,
        IReadOnlyDictionary<string, IrVirtualValueV1> values,
        string role) => operands
        .Where(static operand => operand.Kind == IrOperandKind.VirtualValue)
        .Select(operand => values.TryGetValue(operand.Name, out IrVirtualValueV1? value)
            ? value.Allocation.FixedRegisterId
            : null)
        .Where(static register => register.HasValue)
        .Select(register => new IrOperand(IrOperandKind.ArchitecturalRegister,
            checked((ulong)register!.Value), $"{role}:x{register.Value}"))
        .DistinctBy(static operand => operand.Value)
        .OrderBy(static operand => operand.Value)
        .ToArray();

    private static int? FixedCallRegister(string valueIdentity)
    {
        foreach (string argumentMarker in new[] { ":call-arg-abi:", ":exact-dispatch-arg-abi:", ":ctor-arg-abi:" })
        {
            int marker = valueIdentity.LastIndexOf(argumentMarker, StringComparison.Ordinal);
            string ordinalText = marker < 0 ? string.Empty : valueIdentity[(marker + argumentMarker.Length)..].Split(':')[0];
            if (marker >= 0 && int.TryParse(ordinalText, out int parameter) && parameter >= 0 &&
                parameter < HybridCPU.Compiler.Core.Target.HybridCpuNativeCallControlContractV1.MaximumRegisterArguments)
                return HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[parameter];
        }
        if (valueIdentity.EndsWith("-arg-abi:value", StringComparison.Ordinal))
            return HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0];
        if (valueIdentity.EndsWith(":alloc-type-handle-abi:value", StringComparison.Ordinal) ||
            valueIdentity.EndsWith(":alloc-result-abi:value", StringComparison.Ordinal))
            return HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0];
        if (valueIdentity.Contains(":long-branch-target-abi:value", StringComparison.Ordinal) ||
            valueIdentity.Contains(":long-call-target-abi:value", StringComparison.Ordinal))
            return 5;
        if (valueIdentity.EndsWith(":return-abi:value", StringComparison.Ordinal))
            return HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0];
        return valueIdentity.Contains(":call-result-abi:value", StringComparison.Ordinal) ||
               valueIdentity.EndsWith("-result-abi:value", StringComparison.Ordinal)
            ? HybridCPU.Compiler.Core.Target.HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0]
            : valueIdentity.Contains(":indirect-target-abi:value", StringComparison.Ordinal)
                ? 5
            : null;
    }

    private static List<Emission> RelaxV2SymbolicBranches(IReadOnlyList<Emission> source,
        IReadOnlyDictionary<int, int> targetIndices, int minimumInstructionSpan)
    {
        List<Emission> result = new List<Emission>(checked(source.Count * 2));
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            Emission emission = source[sourceIndex];
            int targetIndex = emission.BranchTarget >= 0 && targetIndices.TryGetValue(emission.BranchTarget, out int mappedTarget)
                ? mappedTarget : -1;
            if (emission.BranchTarget < 0 || emission.CallTargetIdentity is not null || emission.IsIndirectCall)
            {
                result.Add(emission);
                continue;
            }
            if (targetIndex < 0 || Math.Abs(targetIndex - sourceIndex) < minimumInstructionSpan)
            {
                result.Add(emission);
                continue;
            }

            string target = $"cil_{emission.BranchTarget:x4}";
            string valueIdentity = $"{emission.Identity}:long-branch-target-abi:value";
            IrOperand temporary = new IrOperand(IrOperandKind.VirtualValue, 0, valueIdentity);
            if (emission.Opcode != HybridCpuOpcode.JAL)
            {
                string skip = emission.Identity + ":long-branch-skip";
                result.Add(emission with
                {
                    Opcode = InvertBranch(emission.Opcode),
                    BranchTarget = -1,
                    BranchTargetSymbolName = skip,
                    Identity = emission.Identity
                });
                result.Add(new(emission.CilOffset, HybridCpuOpcode.AUIPC, RestrictedCilTypeV1.NativeInt,
                    [], [temporary], -1, emission.Identity + ":long-branch-high",
                    BranchTargetSymbolName: target, LongBranchPart: LongBranchRelocationPart.PcRelativeHigh));
                result.Add(new(emission.CilOffset, HybridCpuOpcode.JALR, RestrictedCilTypeV1.NativeInt,
                    [temporary], [], -1, emission.Identity + ":long-branch-low",
                    BranchTargetSymbolName: target, LongBranchPart: LongBranchRelocationPart.PcRelativeLow));
                result.Add(new(emission.CilOffset, HybridCpuOpcode.ADDI, RestrictedCilTypeV1.NativeInt,
                    [new(IrOperandKind.ArchitecturalRegister, 0, skip + ":zero"), new(IrOperandKind.Constant, 0, skip + ":constant")],
                    [], -1, skip));
            }
            else
            {
                result.Add(new(emission.CilOffset, HybridCpuOpcode.AUIPC, RestrictedCilTypeV1.NativeInt,
                    [], [temporary], -1, emission.Identity + ":long-branch-high",
                    BranchTargetSymbolName: target, LongBranchPart: LongBranchRelocationPart.PcRelativeHigh));
                result.Add(new(emission.CilOffset, HybridCpuOpcode.JALR, RestrictedCilTypeV1.NativeInt,
                    [temporary], [], -1, emission.Identity,
                    BranchTargetSymbolName: target, LongBranchPart: LongBranchRelocationPart.PcRelativeLow));
            }
        }
        return result;

        static HybridCpuOpcode InvertBranch(HybridCpuOpcode opcode) => opcode switch
        {
            HybridCpuOpcode.BEQ => HybridCpuOpcode.BNE,
            HybridCpuOpcode.BNE => HybridCpuOpcode.BEQ,
            HybridCpuOpcode.BLT => HybridCpuOpcode.BGE,
            HybridCpuOpcode.BGE => HybridCpuOpcode.BLT,
            HybridCpuOpcode.BLTU => HybridCpuOpcode.BGEU,
            HybridCpuOpcode.BGEU => HybridCpuOpcode.BLTU,
            _ => throw new InvalidOperationException($"Symbolic conditional branch opcode '{opcode}' has no exact inverse.")
        };
    }

    private static List<Emission> ExpandV2ManagedDirectCalls(IReadOnlyList<Emission> source)
    {
        List<Emission> result = new List<Emission>(source.Count * 2);
        foreach (Emission emission in source)
        {
            if (emission.CallTargetIdentity is null || emission.IsIndirectCall)
            {
                result.Add(emission);
                continue;
            }
            string targetValueIdentity = emission.Identity + ":long-call-target-abi:value";
            IrOperand targetValue = new IrOperand(IrOperandKind.VirtualValue, 0, targetValueIdentity);
            result.Add(new(emission.CilOffset, HybridCpuOpcode.AUIPC, RestrictedCilTypeV1.NativeInt,
                [], [targetValue], -1, emission.Identity + ":long-call-high",
                LongBranchPart: LongBranchRelocationPart.ManagedCallPcRelativeHigh));
            result.Add(emission with
            {
                Opcode = HybridCpuOpcode.JALR,
                Uses = [targetValue, .. emission.Uses],
                LongBranchPart = LongBranchRelocationPart.ManagedCallPcRelativeLow
            });
        }
        return result;
    }

    private static Dictionary<int, int> BuildV2TargetIndices(IReadOnlyList<Emission> emissions,
        V2Graph graph, RestrictedCilProvenanceV1 provenance, IEnumerable<int>? runtimeTargets = null)
    {
        int[] symbolicTargetOffsets = emissions
            .Where(static emission => emission.BranchTargetSymbolName?.StartsWith("cil_", StringComparison.Ordinal) == true)
            .Select(static emission => Convert.ToInt32(emission.BranchTargetSymbolName![4..], 16))
            .ToArray();
        Dictionary<int, int> result = new Dictionary<int, int>();
        foreach (int targetOffset in emissions.Where(static emission => emission.BranchTarget >= 0)
                     .Select(static emission => emission.BranchTarget).Concat(symbolicTargetOffsets).Concat(runtimeTargets ?? []).Distinct())
        {
            int mapped = -1;
            // A CIL block may emit only edge copies (for example a ldloc arm of a
            // conditional expression). Those copies have synthetic identities, but
            // are still the block entry and must execute before reaching its join.
            V2Block? targetBlock = graph.Blocks.FirstOrDefault(block => block.StartOffset == targetOffset);
            if (targetBlock is not null)
                mapped = emissions.ToList().FindIndex(emission =>
                    emission.CilOffset >= targetBlock.StartOffset &&
                    emission.CilOffset < targetBlock.EndOffsetExclusive &&
                    !emission.Identity.EndsWith(":argument-copy", StringComparison.Ordinal));
            foreach (DecodedInstruction source in graph.Instructions.Where(instruction => instruction.Offset >= targetOffset)
                         .OrderBy(instruction => instruction.Offset))
            {
                if (mapped >= 0) break;
                string sourceIdentity = OffsetIdentity(provenance, source.Offset);
                mapped = emissions.ToList().FindIndex(emission =>
                    (string.Equals(emission.Identity, sourceIdentity, StringComparison.Ordinal) ||
                     emission.Identity.StartsWith(sourceIdentity + ":", StringComparison.Ordinal)) &&
                    !emission.Identity.EndsWith(":argument-copy", StringComparison.Ordinal));
                if (mapped >= 0) break;
            }
            if (mapped < 0)
                mapped = emissions.ToList().FindIndex(emission => emission.CilOffset == targetOffset &&
                    !emission.Identity.EndsWith(":argument-copy", StringComparison.Ordinal));
            if (mapped < 0)
                throw new InvalidOperationException($"Branch target IL_{targetOffset:x4} has no Canonical IR operation.");
            result.Add(targetOffset, mapped);
        }
        return result;
    }

    private static List<Emission> LowerV2ParallelCopies(
        IReadOnlyList<Emission> sourceEmissions,
        V2Graph graph,
        V2Dataflow dataflow,
        ScalarControlFlowV2AnalysisV1 analysis,
        MethodSignature signature,
        RestrictedCilProvenanceV1 provenance)
    {
        Dictionary<string, RestrictedCilTypeV1> typeById = new Dictionary<string, RestrictedCilTypeV1>(StringComparer.Ordinal);
        for (int argument = 0; argument < signature.Parameters.Count; argument++)
            typeById[V2Value.Argument(argument, StackType(signature.Parameters[argument]), provenance.CanonicalMethodLocalIdentity).Id] = StackType(signature.Parameters[argument]);
        foreach (V2Phi phi in dataflow.Phis)
        {
            typeById[phi.Id] = phi.Type;
            foreach (V2Value incoming in phi.Incoming.Values) typeById[incoming.Id] = incoming.Type;
        }
        foreach (Emission emission in sourceEmissions)
            foreach (IrOperand definition in emission.Defs) typeById[definition.Name] = emission.Type;

        Dictionary<(int SourceBlockId, int TargetBlockId), ScalarControlFlowV2ParallelCopyV1[]> copiesByEdge = analysis.ParallelCopies.GroupBy(static copy => (copy.SourceBlockId, copy.TargetBlockId))
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static copy => copy.Sequence).ToArray());
        Dictionary<(int SourceBlockId, int TargetBlockId), int> splitKeys = copiesByEdge.Where(pair => pair.Value.Any(static copy => copy.EdgeWasSplit))
            .OrderBy(static pair => pair.Key.SourceBlockId).ThenBy(static pair => pair.Key.TargetBlockId)
            .Select((pair, ordinal) => (Edge: pair.Key, SplitKey: checked(0x10000000 + ordinal)))
            .ToDictionary(static item => item.Edge, static item => item.SplitKey);
        List<Emission> lowered = new List<Emission>();

        foreach (V2Block block in graph.Blocks)
        {
            List<Emission> blockEmissions = sourceEmissions
                .Where(emission => emission.CilOffset >= block.StartOffset && emission.CilOffset < block.EndOffsetExclusive).ToList();
            foreach (int target in block.Successors)
            {
                if (!copiesByEdge.TryGetValue((block.Id, target), out ScalarControlFlowV2ParallelCopyV1[]? copies) || copies.Length == 0) continue;
                if (copies[0].EdgeWasSplit)
                {
                    int splitKey = splitKeys[(block.Id, target)];
                    int targetOffset = graph.Blocks[target].StartOffset;
                    int branchIndex = blockEmissions.FindLastIndex(emission => emission.BranchTarget == targetOffset);
                    if (branchIndex >= 0)
                        blockEmissions[branchIndex] = blockEmissions[branchIndex] with { BranchTarget = splitKey };
                    else
                        blockEmissions.Add(new(block.EndOffsetExclusive - 1, HybridCpuOpcode.JAL, RestrictedCilTypeV1.NativeUInt,
                            [], [], splitKey, $"cil:{provenance.CanonicalMethodLocalIdentity}:split-dispatch:b{block.Id}:b{target}"));
                }
                else
                {
                    int insertion = blockEmissions.Count;
                    if (insertion != 0 && blockEmissions[^1].CallTargetIdentity is null &&
                        V2EndsBlockByOpcode(blockEmissions[^1].Opcode)) insertion--;
                    blockEmissions.InsertRange(insertion, copies.Select(copy => CopyEmission(copy, typeById, provenance, block.EndOffsetExclusive - 1)));
                }
            }
            lowered.AddRange(blockEmissions);
        }

        foreach (KeyValuePair<(int SourceBlockId, int TargetBlockId), int> pair in splitKeys.OrderBy(static pair => pair.Value))
        {
            (int source, int target) = pair.Key;
            int splitKey = pair.Value;
            (int SourceBlockId, int TargetBlockId) edge = pair.Key;
            ScalarControlFlowV2ParallelCopyV1[] copies = copiesByEdge[edge];
            lowered.AddRange(copies.Select(copy => CopyEmission(copy, typeById, provenance, splitKey)));
            lowered.Add(new(splitKey, HybridCpuOpcode.JAL, RestrictedCilTypeV1.NativeUInt, [], [], graph.Blocks[target].StartOffset,
                $"cil:{provenance.CanonicalMethodLocalIdentity}:split-return:b{source}:b{target}"));
        }
        return lowered;
    }

    private static Emission CopyEmission(
        ScalarControlFlowV2ParallelCopyV1 copy,
        IDictionary<string, RestrictedCilTypeV1> typeById,
        RestrictedCilProvenanceV1 provenance,
        int offset)
    {
        RestrictedCilTypeV1 type = typeById.TryGetValue(copy.SourceValueId, out RestrictedCilTypeV1 sourceType)
            ? sourceType : typeById[copy.TargetValueId];
        typeById[copy.TargetValueId] = type;
        IrOperand source = CopyOperand(copy.SourceValueId, copy.StableId);
        IrOperand target = new IrOperand(IrOperandKind.VirtualValue, 0, copy.TargetValueId);
        IrOperand[] uses = source.Kind == IrOperandKind.Constant
            ? [new(IrOperandKind.ArchitecturalRegister, 0, $"{copy.StableId}:zero"), source]
            : [source, new(IrOperandKind.Constant, 0, $"{copy.StableId}:zero")];
        return new(offset, HybridCpuOpcode.ADDI, type,
            uses, [target], -1, copy.StableId);
    }

    private static IrOperand CopyOperand(string valueId, string identity)
    {
        if (valueId.StartsWith("const:", StringComparison.Ordinal))
        {
            int separator = valueId.LastIndexOf(':');
            long value = long.Parse(valueId[(separator + 1)..], CultureInfo.InvariantCulture);
            return new(IrOperandKind.Constant, unchecked((ulong)value), $"{identity}:constant");
        }
        return new(IrOperandKind.VirtualValue, 0, valueId);
    }

    private static bool V2EndsBlockByOpcode(HybridCpuOpcode opcode) => opcode is HybridCpuOpcode.JAL or HybridCpuOpcode.JALR or
        HybridCpuOpcode.BEQ or HybridCpuOpcode.BNE or HybridCpuOpcode.BLT or HybridCpuOpcode.BGE or HybridCpuOpcode.BLTU or HybridCpuOpcode.BGEU;

    private static RestrictedCilTypeV1 TypeForValue(string id, V2Dataflow dataflow, MethodSignature signature, RestrictedCilProvenanceV1 provenance)
    {
        V2Phi? phi = dataflow.Phis.FirstOrDefault(candidate => candidate.Id == id);
        if (phi is not null) return phi.Type;
        string prefix = $"cil:{provenance.CanonicalMethodLocalIdentity}:arg:";
        if (id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id[prefix.Length..], out int argument)) return StackType(signature.Parameters[argument]);
        foreach (V2Phi candidate in dataflow.Phis)
            foreach (V2Value incoming in candidate.Incoming.Values)
                if (incoming.Id == id) return incoming.Type;
        return RestrictedCilTypeV1.Int32;
    }

    private static bool TryGetControlFlowV2Opcode(ushort encoding, out RestrictedCilOpcodeContractV1? row)
    {
        if (encoding == 0x12)
        {
            row = new(encoding, "ldloca.s", "[] -> [managed-byref]",
                "one exact compiler-owned reference-free receiver frame slot; no escape and no safepoints",
                "fixed-frame-slot address materialization", RestrictedCilMatrixSupportV1.Supported, "HCCIL1844");
            return true;
        }
        if (encoding == 0xfe15)
        {
            row = new(encoding, "initobj", "[typed destination] -> []", "only fused exact ldelema/initobj on a reference-free SZARRAY element",
                "checked runtime element zero helper; no interior pointer escapes", RestrictedCilMatrixSupportV1.Supported, "HCCIL1860");
            return true;
        }
        if (encoding == 0x62)
        {
            row = new(encoding, "shl", "[I4/I8/native,I4/native] -> [same value type]",
                "mask count by operand width; preserve I4 truncation/normalization", "SLLW or SLL",
                RestrictedCilMatrixSupportV1.Supported, "HCCIL1850");
            return true;
        }
        if (encoding is 0x63 or 0x64)
        {
            row = new(encoding, encoding == 0x63 ? "shr" : "shr.un", "[I4/I8/native,I4/native] -> [same value type]",
                "mask count by operand width; preserve signed/unsigned I4/I8 semantics",
                encoding == 0x63 ? "SRAW or SRA" : "SRLW or SRL",
                RestrictedCilMatrixSupportV1.Supported, "HCCIL1850");
            return true;
        }
        if (encoding == 0x45)
        {
            row = new(encoding, "switch", "[I4/native] -> []", "bounded exact target table and fallthrough",
                "ordered BEQ case chain", RestrictedCilMatrixSupportV1.Supported, "HCCIL1864");
            return true;
        }
        if (encoding is >= 0x5b and <= 0x5e)
        {
            bool remainder = encoding is 0x5d or 0x5e;
            bool signed = encoding is 0x5b or 0x5d;
            string operationName = remainder ? signed ? "rem" : "rem.un" : signed ? "div" : "div.un";
            row = new(encoding, operationName, "[I4,I4] -> [I4] or [I8,I8] -> [I8]",
                signed ? "nonzero divisor and signed-minimum/-1 overflow proof at operation width" : "nonzero divisor proof at operation width",
                remainder ? signed ? "REMW or REM; no zero-divisor fallback" : "REMUW or REMU; no zero-divisor fallback" :
                    signed ? "DIVW or DIV; no zero/overflow fallback" : "DIVUW or DIVU; no zero-divisor fallback",
                RestrictedCilMatrixSupportV1.Supported, signed ? "HCCIL1832" : "HCCIL1830");
            return true;
        }
        if (encoding == 0x96)
        {
            row = new(encoding, "ldelem.i8", "[array,index] -> [int64]",
                "eight-byte primitive SZARRAY; exact I8 bits", "versioned checked primitive-array helper",
                RestrictedCilMatrixSupportV1.Supported, string.Empty);
            return true;
        }
        if (encoding is 0x90 or 0x91 or 0x92 or 0x93 or 0x95 or 0x9c or 0x9d)
        {
            row = new(encoding, encoding == 0x90 ? "ldelem.i1" : encoding == 0x91 ? "ldelem.u1" :
                    encoding == 0x92 ? "ldelem.i2" : encoding == 0x93 ? "ldelem.u2" :
                    encoding == 0x95 ? "ldelem.u4" : encoding == 0x9c ? "stelem.i1" : "stelem.i2",
                encoding is 0x9c or 0x9d ? "[array,index,int32] -> []" :
                    encoding == 0x95 ? "[array,index] -> [uint32]" : "[array,index] -> [int32]",
                encoding == 0x95 ? "four-byte primitive SZARRAY; preserve unsigned I4 carrier" :
                    encoding is 0x92 or 0x93 or 0x9d ? "two-byte primitive SZARRAY; truncate store / signed or unsigned extend load" :
                    "one-byte primitive SZARRAY; truncate store / signed or unsigned extend load",
                "versioned checked primitive-array helper", RestrictedCilMatrixSupportV1.Supported, string.Empty);
            return true;
        }
        if (encoding is 0x67 or 0x68)
        {
            bool i1 = encoding == 0x67;
            row = new(encoding, i1 ? "conv.i1" : "conv.i2", "[I4/I8/native] -> [I4]",
                i1 ? "truncate to 8 bits then sign-extend" : "truncate to 16 bits then sign-extend",
                i1 ? "SLLI 56 then SRAI 56" : "SLLI 48 then SRAI 48",
                RestrictedCilMatrixSupportV1.Supported, i1 ? "HCCIL1878" : "HCCIL1876");
            return true;
        }
        if (encoding is 0x6d or 0x6e)
        {
            bool u8 = encoding == 0x6e;
            row = new(encoding, u8 ? "conv.u8" : "conv.u4", u8 ? "[I4/I8/native] -> [I8]" : "[I4/I8/native] -> [I4]",
                u8 ? "zero-extend I4 stack bits; preserve 64-bit/native bits" : "truncate to I4 stack bits",
                u8 ? "SLLI 32 then SRLI 32 for I4; otherwise ADDI zero" : "ADDIW zero or ADDI zero",
                RestrictedCilMatrixSupportV1.Supported, u8 ? "HCCIL1880" : "HCCIL1441");
            return true;
        }
        if (encoding == 0x6a)
        {
            row = new(encoding, "conv.i8", "[I4/I8/native] -> [I8]",
                "sign-extend every I4 stack carrier; preserve 64-bit/native bits",
                "ADDIW zero or ADDI zero",
                RestrictedCilMatrixSupportV1.Supported, "HCCIL1880");
            return true;
        }
        if (encoding is 0xd1 or 0xd2)
        {
            bool u2 = encoding == 0xd1;
            row = new(encoding, u2 ? "conv.u2" : "conv.u1", "[I4/I8/native] -> [I4]",
                u2 ? "truncate to 16 bits then zero-extend" : "truncate to 8 bits then zero-extend",
                u2 ? "materialize 0xffff then AND" : "ANDI 0xff", RestrictedCilMatrixSupportV1.Supported,
                u2 ? "HCCIL1874" : "HCCIL1870");
            return true;
        }
        if (encoding is 0x65 or 0x66)
        {
            row = new(encoding, encoding == 0x66 ? "not" : "neg", "[I4/I8/native] -> [same]",
                encoding == 0x66 ? "bitwise complement" : "two's-complement wrapping negation",
                encoding == 0x66 ? "XORI value,-1" : "SUB zero,value", RestrictedCilMatrixSupportV1.Supported, "HCCIL1872");
            return true;
        }
        if (encoding == 0xa4)
        {
            row = new(encoding, "stelem", "[array,index,value] -> []", "exact scoped value identity",
                "analysis only; aggregate ABI/lifetime gate before IR", RestrictedCilMatrixSupportV1.Supported, "HCCIL1810");
            return true;
        }
        string? name = encoding switch
        {
            0x0e => "ldarg.s",
            0x10 => "starg.s",
            0x11 => "ldloc.s",
            0x13 => "stloc.s",
            0x26 => "pop",
            0x5f => "and",
            0x60 => "or",
            0x61 => "xor",
            0xfe01 => "ceq",
            0x2e => "beq.s",
            0x2f => "bge.s",
            0x30 => "bgt.s",
            0x31 => "ble.s",
            0x32 => "blt.s",
            0x33 => "bne.un.s",
            0x34 => "bge.un.s",
            0x35 => "bgt.un.s",
            0x36 => "ble.un.s",
            0x37 => "blt.un.s",
            0x3b => "beq",
            0x3c => "bge",
            0x3d => "bgt",
            0x3e => "ble",
            0x3f => "blt",
            0x40 => "bne.un",
            0x41 => "bge.un",
            0x42 => "bgt.un",
            0x43 => "ble.un",
            0x44 => "blt.un",
            0x7a => "throw",
            0xd0 => "ldtoken",
            0x7f => "ldsflda",
            0xdc => "endfinally",
            0xdd => "leave",
            0xde => "leave.s",
            0xfe1a => "rethrow",
            _ => null
        };
        row = name is null ? null : new(encoding, name, "[T,T] -> []", "same qualified scalar integer type", "Core branch", RestrictedCilMatrixSupportV1.Supported, string.Empty);
        return row is not null;
    }

    private static bool V2EndsBlock(ushort encoding) => encoding is 0x2a or 0x7a or 0xfe1a or 0xdc || V2IsUnconditionalBranch(encoding) || V2IsConditionalBranch(encoding);
    private static bool V2IsUnconditionalBranch(ushort encoding) => encoding is 0x2b or 0x38 or 0xdd or 0xde;
    private static bool V2IsConditionalBranch(ushort encoding) => encoding == 0x45 || encoding is >= 0x2c and <= 0x37 or >= 0x39 and <= 0x44;

    private static Dictionary<int, HashSet<int>> ComputeV2Dominators(IReadOnlyList<V2Block> blocks,
        CancellationToken cancellationToken = default)
    {
        HashSet<int> all = blocks.Select(static block => block.Id).ToHashSet();
        Dictionary<int, HashSet<int>> result = blocks.ToDictionary(static block => block.Id, block => block.Id == 0 ? new HashSet<int> { 0 } : new HashSet<int>(all));
        bool changed;
        do
        {
            changed = false;
            foreach (V2Block block in blocks.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.Predecessors.Count == 0)
                {
                    HashSet<int> root = new HashSet<int> { block.Id };
                    if (!root.SetEquals(result[block.Id])) { result[block.Id] = root; changed = true; }
                    continue;
                }
                HashSet<int> next = new HashSet<int>(result[block.Predecessors[0]]);
                foreach (int predecessor in block.Predecessors.Skip(1)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    next.IntersectWith(result[predecessor]);
                }
                next.Add(block.Id);
                if (!next.SetEquals(result[block.Id])) { result[block.Id] = next; changed = true; }
            }
        } while (changed);
        return result;
    }

    private static IReadOnlyList<int[]> StronglyConnectedV2(IReadOnlyList<V2Block> blocks)
    {
        int nextIndex = 0;
        Stack<int> stack = new Stack<int>();
        HashSet<int> onStack = new HashSet<int>();
        Dictionary<int, int> indexes = new Dictionary<int, int>();
        Dictionary<int, int> low = new Dictionary<int, int>();
        List<int[]> result = new List<int[]>();
        void Visit(int id)
        {
            indexes[id] = low[id] = nextIndex++;
            stack.Push(id); onStack.Add(id);
            foreach (int successor in blocks[id].Successors)
            {
                if (!indexes.ContainsKey(successor)) { Visit(successor); low[id] = Math.Min(low[id], low[successor]); }
                else if (onStack.Contains(successor)) low[id] = Math.Min(low[id], indexes[successor]);
            }
            if (low[id] != indexes[id]) return;
            List<int> component = new List<int>();
            int current;
            do { current = stack.Pop(); onStack.Remove(current); component.Add(current); } while (current != id);
            result.Add(component.Order().ToArray());
        }
        foreach (V2Block block in blocks) if (!indexes.ContainsKey(block.Id)) Visit(block.Id);
        return result.OrderBy(static component => component[0]).ToArray();
    }

    private static string StateDigest(V2State state) => Hash(string.Join('|',
        "stack=" + string.Join(',', state.Stack.Select(static value => value.Id)),
        "locals=" + string.Join(',', state.Locals.Select(static value => value.Id)),
        "arguments=" + string.Join(',', state.Arguments.Select(static value => value.Id))));
    private static bool ValidDescriptor(HybridCpuManagedTypeDescriptorV1 descriptor) =>
        descriptor.SchemaId == HybridCpuManagedTypeDescriptorContractV1.SchemaId &&
        descriptor.SchemaMajor == HybridCpuManagedTypeDescriptorContractV1.SchemaMajor &&
        descriptor.SchemaMinor <= HybridCpuManagedTypeDescriptorContractV1.SchemaMinor &&
        descriptor.TypeId != 0 && descriptor.DescriptorDigest == HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor);
    private static bool IsI4ArrayIndex(RestrictedCilTypeV1 type) =>
        type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32;
    private static bool ValidScalarValueReceiver(V2Value value, RestrictedCilFieldLayoutBindingV1 field)
    {
        HybridCpuManagedFieldLayoutV1? layout = field.TypeDescriptor.InstanceFields.SingleOrDefault(candidate =>
            candidate.Identity == field.FieldName);
        return value.AggregateIdentity is { } identity &&
            identity.EndsWith("]" + field.TypeDescriptor.StableIdentity, StringComparison.Ordinal) &&
            value.Type == field.FieldType && value.Type is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 &&
            field.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.ValueType &&
            field.TypeDescriptor.ValueTypeShape is { PayloadSizeBytes: 4, PayloadAlignmentBytes: 4,
                ObjectReferenceOffsets.Count: 0 } &&
            field.TypeDescriptor.InstanceFields.Count == 1 &&
            layout is { OffsetBytes: 0, SizeBytes: 4, AlignmentBytes: 4,
                StorageKind: HybridCpuManagedStorageKindV1.Primitive };
    }
    private static bool ValidArrayBinding(RestrictedCilArrayTypeBindingV1 binding) =>
        binding.ElementTypeMetadataToken != 0 && binding.TypeHandle != 0 && ValidDescriptor(binding.TypeDescriptor) &&
        binding.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.SzArray && binding.TypeDescriptor.ArrayShape is { IsSzArray: true };
    private static bool ValidStringBinding(RestrictedCilStringLiteralBindingV1 binding) =>
        binding.Literal is not null && binding.LiteralHandle != 0 && binding.TypeHandle != 0 && ValidDescriptor(binding.TypeDescriptor) &&
        binding.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.String && binding.TypeDescriptor.StringShape is { CharacterSizeBytes: 2, IsImmutable: true };
    private static bool ValidValueBinding(RestrictedCilValueTypeBindingV1 binding) =>
        binding.TypeMetadataToken != 0 && binding.TypeHandle != 0 &&
        binding.ValueType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt &&
        ValidDescriptor(binding.TypeDescriptor) && binding.TypeDescriptor.Kind == HybridCpuManagedTypeKindV1.ValueType &&
        binding.TypeDescriptor.ValueTypeShape is { ObjectReferenceOffsets.Count: 0, PayloadSizeBytes: 4 or 8 } shape &&
        shape.PayloadSizeBytes == (binding.ValueType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 ? 4 : 8);
    private static bool ValidInitializationBinding(RestrictedCilTypeInitializationBindingV1 binding,
        HybridCpuManagedTypeDescriptorV1 descriptor) =>
        (binding.RuntimePreinitialized ? binding.InitializerMetadataToken == 0 : binding.InitializerMetadataToken != 0) && binding.TypeHandle != 0 &&
        ValidDescriptor(binding.TypeDescriptor) && binding.TypeDescriptor.TypeId == descriptor.TypeId;
    private static bool ValidFunctionPointerBinding(RestrictedCilFunctionPointerBindingV1 binding) =>
        binding.MethodMetadataToken != 0 && binding.MethodId != 0 && binding.SignatureId != 0 &&
        binding.ReturnType is not (RestrictedCilTypeV1.Invalid or RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef) &&
        binding.ParameterTypes.All(static type => type is not (RestrictedCilTypeV1.Void or RestrictedCilTypeV1.Invalid or
            RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef)) &&
        (!binding.IsInstanceMethod || binding.ParameterTypes.Count != 0 && binding.ParameterTypes[0] == RestrictedCilTypeV1.ObjectReference) &&
        binding.SignatureId == ManagedSignatureId(binding.SignatureParameterTypes ?? binding.ParameterTypes,
            binding.ReturnType);
    private static bool ValidCalliBinding(RestrictedCilCalliBindingV1 binding) =>
        binding.SignatureMetadataToken != 0 && binding.SignatureId != 0 &&
        binding.ReturnType is not (RestrictedCilTypeV1.Invalid or RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef) &&
        binding.ParameterTypes.All(static type => type is not (RestrictedCilTypeV1.Void or RestrictedCilTypeV1.Invalid or
            RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef)) &&
        binding.SignatureId == ManagedSignatureId(binding.ParameterTypes, binding.ReturnType);
    private static bool ValidDelegateCreationBinding(RestrictedCilDelegateCreationBindingV1 binding) =>
        binding.ContainingMethodMetadataToken != 0 && binding.ConstructorMetadataToken != 0 &&
        binding.CilOffset >= 0 && binding.DelegateTypeHandle != 0 &&
        binding.SignatureId != 0 && Enum.IsDefined(binding.Kind);
    private static bool ValidDelegateInvokeBinding(RestrictedCilDelegateInvokeBindingV1 binding) =>
        binding.InvokeMetadataToken != 0 && binding.SignatureId != 0 && binding.ParameterTypes.Count != 0 &&
        binding.ParameterTypes[0] == RestrictedCilTypeV1.ObjectReference &&
        binding.ReturnType is not (RestrictedCilTypeV1.Invalid or RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef) &&
        binding.ParameterTypes.All(static type => type is not (RestrictedCilTypeV1.Void or RestrictedCilTypeV1.Invalid or
            RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef)) &&
        binding.SignatureId == ManagedSignatureId(binding.ParameterTypes.Skip(1).ToArray(), binding.ReturnType);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static int CurrentMethodToken(RestrictedCilProvenanceV1 provenance) => int.Parse(
        provenance.CilMethodToken.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static string BlockIdentity(RestrictedCilProvenanceV1 provenance, V2Block block) =>
        $"cil:{provenance.CanonicalMethodLocalIdentity}:block:{block.Id.ToString(CultureInfo.InvariantCulture)}:IL_{block.StartOffset:x4}";

    private static V2State ConvertEhState(ManagedEhTypedStateV1 state, ManagedEhMethodPlanV1? handlerPlan)
    {
        V2Value[] locals = state.Locals.Select(ConvertEhValue).ToArray();
        V2Value[] arguments = state.Arguments.Select(ConvertEhValue).ToArray();
        if (handlerPlan?.ControlFlow is { } flow)
            foreach (ManagedEhStateHomeV1 home in flow.StateHomes)
            {
                if (!handlerPlan.HandlerEntries.Any(row => row.IlOffset == state.IlOffset) &&
                    !flow.LiveStateAtEntry[state.IlOffset].Contains(home.Slot)) continue;
                int separator = home.Slot.IndexOf(':');
                int index = int.Parse(home.Slot.AsSpan(separator + 1), CultureInfo.InvariantCulture);
                string identity = $"eh:reload:{home.Slot}:il_{state.IlOffset:x4}";
                V2Value reload = new V2Value(home.Type, identity,
                    new(IrOperandKind.VirtualValue, 0, identity), true);
                if (home.Slot.StartsWith("local:", StringComparison.Ordinal)) locals[index] = reload;
                else arguments[index] = reload;
            }
        return new(state.Stack.Select(ConvertEhValue), locals, arguments);
    }

    private static V2Value ConvertEhValue(ManagedEhTypedValueV1 value)
    {
        if (!value.Initialized) return V2Value.Uninitialized;
        if (value.Constant is ulong constant)
            return V2Value.Constant(unchecked((long)constant), value.Type, value.Identity) with
            {
                AggregateIdentity = value.AggregateIdentity,
                ReceiverIdentity = value.ReceiverIdentity,
                ManagedFunctionPointerSignatureId = value.FunctionPointerSignatureId
            };
        return new(value.Type, value.Identity, new(IrOperandKind.VirtualValue, 0, value.Identity), true,
            value.FunctionPointerSignatureId, value.AggregateIdentity, value.ReceiverIdentity);
    }

    private sealed class V2Block(int id, int startInstructionIndex, int endInstructionIndex, int startOffset, int endOffsetExclusive)
    {
        public int Id { get; } = id;
        public int StartInstructionIndex { get; } = startInstructionIndex;
        public int EndInstructionIndex { get; } = endInstructionIndex;
        public int StartOffset { get; } = startOffset;
        public int EndOffsetExclusive { get; } = endOffsetExclusive;
        public List<int> Predecessors { get; } = [];
        public List<int> Successors { get; } = [];
        public V2State? Entry { get; set; }
        public V2State? Exit { get; set; }
    }

    private sealed record V2Graph(IReadOnlyList<DecodedInstruction> Instructions, IReadOnlyList<V2Block> Blocks);
    private sealed record V2GraphBuild(V2Graph? Graph, RestrictedCilImportResultV1? Failure);
    private sealed record V2Dataflow(IReadOnlyList<V2Phi> Phis, IReadOnlyDictionary<int, ResolvedHelper> Helpers,
        IReadOnlyDictionary<int, RestrictedCilFieldLayoutBindingV1> Fields,
        IReadOnlyDictionary<int, V2Allocation> Allocations,
        IReadOnlyDictionary<int, RestrictedCilArrayTypeBindingV1> Arrays,
        IReadOnlyDictionary<int, RestrictedCilStringLiteralBindingV1> Strings,
        IReadOnlyDictionary<int, RestrictedCilValueTypeBindingV1> Values,
        IReadOnlyDictionary<int, RestrictedCilTypeTestBindingV1> TypeTests,
        IReadOnlyDictionary<int, RestrictedCilFunctionPointerBindingV1> FunctionPointers,
        IReadOnlyDictionary<int, RestrictedCilCalliBindingV1> CallSites,
        IReadOnlyDictionary<int, RestrictedCilDelegateCreationBindingV1> DelegateCreations,
        IReadOnlyDictionary<int, RestrictedCilDelegateInvokeBindingV1> DelegateInvokes,
        IReadOnlyDictionary<int, RestrictedCilFieldDataBindingV1> FieldData,
        IReadOnlyDictionary<int, V2ReceiverCall> ReceiverCalls, bool HasAggregates);
    private sealed record V2ReceiverCall(string FrameSlotIdentity, string CalleeIdentity,
        string CalleePlanDigest, string AbiLayoutDigest);
    private sealed record V2Allocation(RestrictedCilAllocationBindingV1 Binding, ResolvedHelper Constructor, string? AggregateIdentity);
    private sealed record V2DataflowBuild(V2Dataflow? Dataflow, RestrictedCilImportResultV1? Failure);
    private sealed record V2LoopBuild(IReadOnlyList<ScalarControlFlowV2LoopV1>? Loops, RestrictedCilImportResultV1? Failure);
    private sealed record V2StateMerge(V2State? State, RestrictedCilImportResultV1? Failure);
    private sealed record V2ValueMerge(V2Value Value, RestrictedCilImportResultV1? Failure);

    private sealed class V2State(IEnumerable<V2Value> stack, IEnumerable<V2Value> locals, IEnumerable<V2Value> arguments)
    {
        public List<V2Value> Stack { get; } = [.. stack];
        public V2Value[] Locals { get; } = [.. locals];
        public V2Value[] Arguments { get; } = [.. arguments];
        public V2State Clone() => new(Stack, Locals, Arguments);
        public bool Equivalent(V2State other) => Stack.Select(static value => (value.Id, value.ConstructorReference, value.NullableHasValue?.Id)).SequenceEqual(other.Stack.Select(static value => (value.Id, value.ConstructorReference, value.NullableHasValue?.Id))) &&
            Locals.Select(static value => (value.Id, value.ConstructorReference, value.NullableHasValue?.Id)).SequenceEqual(other.Locals.Select(static value => (value.Id, value.ConstructorReference, value.NullableHasValue?.Id))) &&
            Arguments.Select(static value => (value.Id, value.ConstructorReference, value.NullableHasValue?.Id)).SequenceEqual(other.Arguments.Select(static value => (value.Id, value.ConstructorReference, value.NullableHasValue?.Id)));
    }

    private sealed record V2Value(RestrictedCilTypeV1 Type, string Id, IrOperand Operand, bool Initialized,
        ulong? ManagedFunctionPointerSignatureId = null, string? AggregateIdentity = null,
        string? ReceiverIdentity = null, string? ReceiverStorageSlotIdentity = null,
        ConstructorReferenceKind ConstructorReference = ConstructorReferenceKind.None, V2Value? NullableHasValue = null)
    {
        public static V2Value Uninitialized { get; } = new(RestrictedCilTypeV1.Invalid, "<uninitialized>", new(IrOperandKind.Constant, 0, "uninitialized"), false);
        public static V2Value Argument(int index, RestrictedCilTypeV1 type, string method) =>
            new(type, $"cil:{method}:arg:{index}", new(IrOperandKind.VirtualValue, 0, $"cil:{method}:arg:{index}"), true);
        public static V2Value Constant(long value, RestrictedCilTypeV1 type, string identity) =>
            new(type, $"const:{type}:{value}", new(IrOperandKind.Constant, unchecked((ulong)value), $"{identity}:constant"), true);
        public static V2Value Definition(RestrictedCilTypeV1 type, string identity,
            ulong? managedFunctionPointerSignatureId = null) =>
            new(type, $"{identity}:value", new(IrOperandKind.VirtualValue, 0, $"{identity}:value"), true,
                managedFunctionPointerSignatureId);
        public static V2Value Phi(RestrictedCilTypeV1 type, string id) => new(type, id, new(IrOperandKind.VirtualValue, 0, id), true);
    }

    private sealed class V2Phi(string id, int blockId, string slot, RestrictedCilTypeV1 type,
        string? aggregateIdentity = null, string? receiverIdentity = null,
        string? receiverStorageSlotIdentity = null)
    {
        public string Id { get; } = id;
        public int BlockId { get; } = blockId;
        public string Slot { get; } = slot;
        public RestrictedCilTypeV1 Type { get; } = type;
        public V2Value Value { get; } = V2Value.Phi(type, id) with
        { AggregateIdentity = aggregateIdentity, ReceiverIdentity = receiverIdentity,
            ReceiverStorageSlotIdentity = receiverStorageSlotIdentity };
        public SortedDictionary<int, V2Value> Incoming { get; } = [];
    }
}
