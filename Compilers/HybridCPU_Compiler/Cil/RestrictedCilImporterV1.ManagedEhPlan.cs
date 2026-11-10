using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> ManagedEhOpcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value));

    public ManagedEhImportResultV1 ImportManagedEhPlan(
        ReadOnlyMemory<byte> peImage,
        RestrictedCilMethodSelectorV1 selector)
        => ImportManagedEhPlan(peImage, selector, CancellationToken.None);

    public ManagedEhImportResultV1 ImportManagedEhPlan(
        ReadOnlyMemory<byte> peImage,
        RestrictedCilMethodSelectorV1 selector,
        CancellationToken cancellationToken)
        => ImportManagedEhPlanCore(peImage, selector, cancellationToken, null);

    // Borrowed module readers remain owned by the synchronous BodyWorld lifetime.
    private ManagedEhImportResultV1 ImportManagedEhPlanCore(
        ReadOnlyMemory<byte> peImage, RestrictedCilMethodSelectorV1 selector,
        CancellationToken cancellationToken, ManagedModuleContext? module)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(selector);
        if (_mode != RestrictedCilImportModeV1.ScalarControlFlowV2)
            return EhFailure(ManagedEhImportStatusV1.Unsupported, "HCCIL1801", "Managed EH requires ScalarControlFlowV2.");
        if (peImage.Length == 0 || peImage.Length > _budgets.MaximumPeBytes)
            return EhFailure(ManagedEhImportStatusV1.BudgetExhausted, "HCCIL2801", "Managed PE input exceeds the deterministic budget.");
        try
        {
            using MemoryStream? stream = module is null ? new MemoryStream(peImage.ToArray(), writable: false) : null;
            using PEReader? ownedReader = module is null ? new PEReader(stream!, PEStreamOptions.LeaveOpen) : null;
            PEReader pe = module?.Reader ?? ownedReader!;
            if (!pe.HasMetadata) return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0801", "Input has no managed metadata.");
            MetadataReader metadata = module?.Metadata ?? pe.GetMetadataReader();
            cancellationToken.ThrowIfCancellationRequested();
            MethodSelection selection = SelectMethod(metadata, selector);
            if (selection.Failure is not null)
                return EhFailure(ManagedEhImportStatusV1.InvalidInput, selection.Failure.Diagnostics[0].Code,
                    selection.Failure.Diagnostics[0].Message);
            MethodDefinition method = metadata.GetMethodDefinition(selection.Method);
            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (body.ExceptionRegions.Length == 0)
                return EhFailure(ManagedEhImportStatusV1.Unsupported, "HCCIL1802", "The selected method has no EH clauses.");
            if (body.ExceptionRegions.Length > HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod)
                return EhFailure(ManagedEhImportStatusV1.BudgetExhausted, "HCCIL2802", "The EH clause budget was exhausted.");
            if (body.ExceptionRegions.Any(static region =>
                    region.Kind is ExceptionRegionKind.Filter or ExceptionRegionKind.Fault))
                return EhFailure(ManagedEhImportStatusV1.Unsupported, "HCCIL1803",
                    "Filter and fault clauses are outside managed EH V1.");

            IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments = selector.GenericTypeArguments ?? [];
            IReadOnlyList<RestrictedCilGenericArgumentV1> methodArguments = selector.GenericMethodArguments ?? [];
            MethodSignature signature = ParseMethodSignature(metadata, method.Signature, typeArguments, methodArguments,
                allowAggregates: true);
            signature = ProjectScalarValueSignature(metadata, BindValueReceiver(metadata, selection.Type, signature));
            LocalSignature locals = ProjectScalarValueLocals(metadata,
                ParseLocalSignature(metadata, body.LocalSignature, typeArguments, methodArguments, allowAggregates: true));
            if (signature.Status != SignatureStatus.Success || locals.Status != SignatureStatus.Success ||
                signature.AggregateReturn == NullableInt32Identity ||
                signature.AggregateParameters?.Contains(NullableInt32Identity) == true)
                return EhFailure(ManagedEhImportStatusV1.Unsupported, "HCCIL1805",
                    "EH state requires exact supported method and local signatures.");
            ManagedMethodIdentityV1 identity = CreateManagedMethodIdentity(metadata, selection.Type, selection.Method,
                signature, typeArguments, methodArguments);
            byte[] il = body.GetILBytes() ?? [];
            if (il.Length > _budgets.MaximumMethodBodyBytes)
                return EhFailure(ManagedEhImportStatusV1.BudgetExhausted, "HCCIL2805",
                    "The managed EH method body exceeds the deterministic byte budget.");
            RestrictedCilProvenanceV1 provenance = new RestrictedCilProvenanceV1("managed-eh-plan", module?.ImageDigest ?? HashBytes(peImage.Span),
                identity.StableIdentity, $"0x{identity.MetadataToken:x8}", _matrix.ContractDigest,
                RestrictedCilSupportMatrixV1.OptionsDigest(_budgets), $"{identity.DeclaringType}.{identity.MethodName}");
            DecodeResult decoded = DecodeManagedEhBody(il, provenance);
            cancellationToken.ThrowIfCancellationRequested();
            if (decoded.Failure is not null)
                return EhFailure(decoded.Failure.Status == RestrictedCilImportStatusV1.InvalidInput
                    ? ManagedEhImportStatusV1.InvalidInput : ManagedEhImportStatusV1.Unsupported,
                    decoded.Failure.Diagnostics[0].Code, decoded.Failure.Diagnostics[0].Message);

            List<ManagedEhClausePlanV1> clauses = new List<ManagedEhClausePlanV1>(body.ExceptionRegions.Length);
            HashSet<int> boundaries = decoded.Instructions.Select(static instruction => instruction.Offset).ToHashSet();
            boundaries.Add(il.Length);
            int ordinal = 0;
            foreach (ExceptionRegion region in body.ExceptionRegions.OrderBy(static row => row.TryOffset)
                         .ThenBy(static row => row.TryLength).ThenBy(static row => row.HandlerOffset))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (region.Kind is not (ExceptionRegionKind.Catch or ExceptionRegionKind.Finally) ||
                    region.TryOffset < 0 || region.TryLength <= 0 || region.HandlerOffset < 0 || region.HandlerLength <= 0 ||
                    region.TryOffset > il.Length - region.TryLength || region.HandlerOffset > il.Length - region.HandlerLength ||
                    !boundaries.Contains(region.TryOffset) || !boundaries.Contains(region.TryOffset + region.TryLength) ||
                    !boundaries.Contains(region.HandlerOffset) || !boundaries.Contains(region.HandlerOffset + region.HandlerLength) ||
                    region.TryOffset < region.HandlerOffset + region.HandlerLength && region.HandlerOffset < region.TryOffset + region.TryLength)
                    return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0802", "EH clause ranges are malformed.");
                string? catchIdentity = null;
                ulong catchTypeId = 0;
                if (region.Kind == ExceptionRegionKind.Catch)
                {
                    catchIdentity = ExactTypeHandleName(metadata, region.CatchType);
                    if (string.IsNullOrWhiteSpace(catchIdentity))
                        return EhFailure(ManagedEhImportStatusV1.Unsupported, "HCCIL1804", "Catch type identity is not exact.");
                    catchTypeId = ManagedTypeId(catchIdentity);
                }
                clauses.Add(new(region.Kind == ExceptionRegionKind.Catch
                        ? HybridCpuManagedEhClauseKindV1.Catch : HybridCpuManagedEhClauseKindV1.Finally,
                    region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength,
                    catchIdentity, catchTypeId, ordinal++));
            }

            ManagedEhOperationPlanV1[] operations = decoded.Instructions.Where(static row =>
                    row.Encoding is 0x7a or 0xfe1a or 0xdd or 0xde or 0xdc)
                .Select(static row => new ManagedEhOperationPlanV1(row.Encoding switch
                    {
                        0x7a => ManagedEhOperationKindV1.Throw,
                        0xfe1a => ManagedEhOperationKindV1.Rethrow,
                        0xdc => ManagedEhOperationKindV1.EndFinally,
                        _ => ManagedEhOperationKindV1.Leave
                    }, row.Offset, row.BranchTarget,
                    row.Encoding == 0x7a ? "__hybridcpu_managed_throw" :
                    row.Encoding == 0xfe1a ? "__hybridcpu_managed_rethrow" :
                    row.Encoding == 0xdc ? "__hybridcpu_managed_endfinally" : string.Empty))
                .OrderBy(static row => row.IlOffset).ToArray();
            // Ordinary branches cannot perform an EH transfer. Keep protected-region
            // membership exact, including switch edges, before constructing handler SSA.
            int[] RegionsAt(int offset) => clauses.SelectMany(clause => new[]
                {
                    (Id: clause.Ordinal * 2, Start: clause.TryOffset, Length: clause.TryLength),
                    (Id: clause.Ordinal * 2 + 1, Start: clause.HandlerOffset, Length: clause.HandlerLength)
                }).Where(region => offset >= region.Start && offset < region.Start + region.Length)
                .Select(region => region.Id).ToArray();
            foreach (DecodedInstruction instruction in decoded.Instructions)
            {
                int[] sourceRegions = RegionsAt(instruction.Offset);
                if (instruction.Encoding == 0x2a && sourceRegions.Length != 0)
                    return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0815",
                        "ret cannot exit a protected region; leave must perform the EH transfer.");
                if (instruction.Encoding is 0xdd or 0xde) continue;
                foreach (int target in ControlFlowTargets(instruction))
                    if (!sourceRegions.SequenceEqual(RegionsAt(target)))
                        return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0816",
                            "An ordinary branch cannot cross a try or handler boundary.");
            }
            foreach (ManagedEhOperationPlanV1 operation in operations)
            {
                static bool Within(int offset, int start, int length) => offset >= start && offset < start + length;
                ManagedEhClausePlanV1? handler = clauses.Where(clause => Within(operation.IlOffset, clause.HandlerOffset, clause.HandlerLength))
                    .OrderBy(static clause => clause.HandlerLength).FirstOrDefault();
                if (operation.Kind == ManagedEhOperationKindV1.Rethrow && handler?.Kind != HybridCpuManagedEhClauseKindV1.Catch)
                    return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0812", "rethrow requires an enclosing catch handler.");
                if (operation.Kind == ManagedEhOperationKindV1.EndFinally && handler?.Kind != HybridCpuManagedEhClauseKindV1.Finally)
                    return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0813", "endfinally requires an enclosing finally handler.");
                if (operation.Kind == ManagedEhOperationKindV1.Leave &&
                    (handler?.Kind == HybridCpuManagedEhClauseKindV1.Finally &&
                        !Within(operation.LeaveTarget, handler.HandlerOffset, handler.HandlerLength) || clauses.Any(clause =>
                        Within(operation.LeaveTarget, clause.TryOffset, clause.TryLength) && !Within(operation.IlOffset, clause.TryOffset, clause.TryLength) ||
                        Within(operation.LeaveTarget, clause.HandlerOffset, clause.HandlerLength) && !Within(operation.IlOffset, clause.HandlerOffset, clause.HandlerLength))))
                    return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0814", "leave cannot exit a finally or enter a new protected region.");
            }
            string instructionPrefix = $"cil:{provenance.PeSha256}:{provenance.CanonicalMethodLocalIdentity}:il_";
            string digest = ManagedEhPlanContractV1.ComputeDigest(identity.StableIdentity, instructionPrefix,
                il.Length, clauses, operations);
            ManagedEhControlFlowV1? graph = BuildManagedEhControlFlow(decoded.Instructions, clauses, digest);
            if (graph is null)
                return EhFailure(ManagedEhImportStatusV1.BudgetExhausted, "HCCIL2803",
                    "The conservative exceptional control-flow edge budget was exhausted.");
            ManagedEhImportResultV1? stateFailure = BindManagedEhState(decoded.Instructions, signature, locals,
                body.LocalVariablesInitialized, ref graph);
            if (stateFailure is not null) return stateFailure;
            if (!BindManagedEhBlocks(decoded.Instructions, clauses, ref graph))
                return EhFailure(ManagedEhImportStatusV1.BudgetExhausted, "HCCIL2804",
                    "EH basic-block count exceeds the configured budget.");
            cancellationToken.ThrowIfCancellationRequested();
            return new(ManagedEhImportStatusV1.Success,
                new(identity.StableIdentity,
                    instructionPrefix,
                    il.Length, clauses, operations, digest) { ControlFlow = graph }, string.Empty, string.Empty) { ControlFlow = graph };
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException or OverflowException)
        {
            return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0803", exception.Message);
        }
    }

    private static ManagedEhControlFlowV1? BuildManagedEhControlFlow(
        IReadOnlyList<DecodedInstruction> instructions, IReadOnlyList<ManagedEhClausePlanV1> clauses,
        string planDigest)
    {
        const int maximumEdges = 65536;
        HashSet<ManagedEhEdgeV1> edges = new HashSet<ManagedEhEdgeV1>();
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction instruction = instructions[index];
            bool leave = instruction.Encoding is 0xdd or 0xde;
            foreach (int target in ControlFlowTargets(instruction))
                edges.Add(new(instruction.Offset, target,
                    leave ? ManagedEhEdgeKindV1.LeaveContinuation : ManagedEhEdgeKindV1.Normal));
            FlowControl flow = ManagedEhOpcodes[instruction.Encoding].FlowControl;
            if (!leave && flow is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw) &&
                index + 1 < instructions.Count)
                edges.Add(new(instruction.Offset, instructions[index + 1].Offset, ManagedEhEdgeKindV1.Normal));
            // Conservatively retain pre-instruction local/argument state at every point
            // in a protected try. Stack values are discarded, not merged into handlers.
            foreach (ManagedEhClausePlanV1 clause in clauses)
                if (instruction.Offset >= clause.TryOffset && instruction.Offset < clause.TryOffset + clause.TryLength)
                    edges.Add(new(instruction.Offset, clause.HandlerOffset,
                        ManagedEhEdgeKindV1.ExceptionDispatchCandidate, clause.Ordinal));
            if (edges.Count > maximumEdges) return null;
        }
        ManagedEhEdgeV1[] ordered = edges.OrderBy(edge => edge.SourceOffset).ThenBy(edge => edge.Kind)
            .ThenBy(edge => edge.TargetOffset).ThenBy(edge => edge.ClauseOrdinal).ToArray();
        int[] offsets = instructions.Select(instruction => instruction.Offset).ToArray();
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', "hybridcpu.eh-cfg/v1", planDigest,
            string.Join(',', offsets), string.Join(';', ordered.Select(edge =>
                $"{edge.SourceOffset}:{edge.TargetOffset}:{edge.Kind}:{edge.ClauseOrdinal}"))));
        Dictionary<int, string?> uses = instructions.ToDictionary(instruction => instruction.Offset, StateUse);
        Dictionary<int, string?> definitions = instructions.ToDictionary(instruction => instruction.Offset, StateDefinition);
        Dictionary<int, HashSet<string>> live = offsets.ToDictionary(offset => offset, _ => new HashSet<string>(StringComparer.Ordinal));
        ILookup<int, ManagedEhEdgeV1> outgoing = ordered.ToLookup(edge => edge.SourceOffset);
        // Slots whose address escapes cannot be reasoned about using scalar stloc kills.
        HashSet<string> addressed = instructions.Where(instruction => instruction.Encoding is 0x0f or 0x12 or 0xfe0a or 0xfe0d)
            .Select(StateUse).OfType<string>().ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (DecodedInstruction? instruction in instructions.Reverse())
            {
                HashSet<string> next = new HashSet<string>(StringComparer.Ordinal);
                foreach (ManagedEhEdgeV1? edge in outgoing[instruction.Offset])
                    foreach (string slot in live[edge.TargetOffset])
                        if (edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate ||
                            slot != definitions[instruction.Offset] || addressed.Contains(slot))
                            next.Add(slot);
                if (uses[instruction.Offset] is string used) next.Add(used);
                // endfinally has a runtime continuation, not a statically unique successor.
                // Conservatively retain all method state for finally methods until that
                // continuation is represented in typed SSA.
                if (instruction.Encoding == 0xdc)
                    next.UnionWith(uses.Values.OfType<string>());
                if (!live[instruction.Offset].SetEquals(next))
                {
                    live[instruction.Offset] = next;
                    changed = true;
                }
            }
        } while (changed);
        string[] homes = ordered.Where(edge => edge.Kind == ManagedEhEdgeKindV1.ExceptionDispatchCandidate)
            .SelectMany(edge => live[edge.TargetOffset]).Concat(addressed).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        Dictionary<int, IReadOnlyList<string>> liveEntries = live.ToDictionary(row => row.Key,
            row => (IReadOnlyList<string>)row.Value.Order(StringComparer.Ordinal).ToArray());
        digest = HybridCpuPlatformContractV1.Hash(string.Join('|', digest, "eh-state-liveness/v1",
            string.Join(',', homes), string.Join(';', liveEntries.OrderBy(row => row.Key)
                .Select(row => $"{row.Key}:{string.Join(',', row.Value)}"))));
        return new(offsets, ordered, digest) { RequiredStateHomes = homes, LiveStateAtEntry = liveEntries };

    }

    private static string? StateUse(DecodedInstruction instruction) => instruction.Encoding switch
        {
            >= 0x02 and <= 0x05 => $"arg:{instruction.Encoding - 0x02}",
            >= 0x06 and <= 0x09 => $"local:{instruction.Encoding - 0x06}",
            0x0e or 0x0f or 0xfe09 or 0xfe0a => $"arg:{instruction.Literal}",
            0x11 or 0x12 or 0xfe0c or 0xfe0d => $"local:{instruction.Literal}",
            _ => null
        };
    private static string? StateDefinition(DecodedInstruction instruction) => instruction.Encoding switch
        {
            >= 0x0a and <= 0x0d => $"local:{instruction.Encoding - 0x0a}",
            0x10 or 0xfe0b => $"arg:{instruction.Literal}",
            0x13 or 0xfe0e => $"local:{instruction.Literal}",
            _ => null
        };

    private bool BindManagedEhBlocks(IReadOnlyList<DecodedInstruction> instructions,
        IReadOnlyList<ManagedEhClausePlanV1> clauses, ref ManagedEhControlFlowV1 graph)
    {
        int end = instructions[^1].Offset + instructions[^1].Size;
        SortedSet<int> leaders = new SortedSet<int> { instructions[0].Offset };
        foreach (ManagedEhClausePlanV1 clause in clauses)
            foreach (int boundary in new[] { clause.TryOffset, clause.TryOffset + clause.TryLength,
                         clause.HandlerOffset, clause.HandlerOffset + clause.HandlerLength })
                if (boundary < end) leaders.Add(boundary);
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction instruction = instructions[index];
            foreach (int target in ControlFlowTargets(instruction)) leaders.Add(target);
            if (ManagedEhOpcodes[instruction.Encoding].FlowControl is
                FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
                if (index + 1 < instructions.Count) leaders.Add(instructions[index + 1].Offset);
        }
        if (leaders.Count > _budgets.MaximumBasicBlocks) return false;
        int[] starts = leaders.ToArray();
        List<ManagedEhBasicBlockV1> blocks = new List<ManagedEhBasicBlockV1>(starts.Length);
        Dictionary<int, int> owner = new Dictionary<int, int>();
        int cursor = 0;
        for (int id = 0; id < starts.Length; id++)
        {
            int limit = id + 1 < starts.Length ? starts[id + 1] : end;
            List<int> offsets = new List<int>();
            while (cursor < instructions.Count && instructions[cursor].Offset < limit)
            {
                int offset = instructions[cursor++].Offset;
                offsets.Add(offset);
                owner.Add(offset, id);
            }
            blocks.Add(new(id, starts[id], limit, offsets.ToArray(),
                clauses.Where(clause => clause.HandlerOffset == starts[id]).Select(clause => clause.Ordinal).ToArray()));
        }
        ManagedEhBlockEdgeV1[] edges = graph.Edges.Where(edge => owner[edge.SourceOffset] != owner[edge.TargetOffset] ||
                edge.Kind != ManagedEhEdgeKindV1.Normal ||
                blocks[owner[edge.SourceOffset]].InstructionOffsets[^1] == edge.SourceOffset)
            .Select(edge => new ManagedEhBlockEdgeV1(owner[edge.SourceOffset], owner[edge.TargetOffset],
                edge.SourceOffset, edge.Kind, edge.ClauseOrdinal)).ToArray();
        graph = graph with
        {
            Blocks = blocks.ToArray(), BlockEdges = edges,
            Digest = HybridCpuPlatformContractV1.Hash(string.Join('|', graph.Digest, "eh-basic-blocks/v1",
                string.Join(';', blocks.Select(block => $"{block.Id}:{block.StartOffset}:{block.EndOffsetExclusive}:{string.Join(',', block.HandlerClauseOrdinals)}")),
                string.Join(';', edges.Select(edge => $"{edge.SourceBlock}:{edge.TargetBlock}:{edge.SourceOffset}:{edge.Kind}:{edge.ClauseOrdinal}"))))
        };
        return true;
    }

    private static ManagedEhImportResultV1? BindManagedEhState(
        IReadOnlyList<DecodedInstruction> instructions, MethodSignature signature, LocalSignature locals,
        bool initLocals, ref ManagedEhControlFlowV1 graph)
    {
        Dictionary<string, ManagedEhStateHomeV1> slots = new Dictionary<string, ManagedEhStateHomeV1>(StringComparer.Ordinal);
        for (int index = 0; index < signature.Parameters.Count; index++)
            slots.Add($"arg:{index}", new($"arg:{index}", signature.Parameters[index],
                signature.AggregateParameters?.ElementAtOrDefault(index), true));
        for (int index = 0; index < locals.Types.Count; index++)
            slots.Add($"local:{index}", new($"local:{index}", locals.Types[index],
                locals.Aggregates?.ElementAtOrDefault(index), initLocals));
        foreach (DecodedInstruction instruction in instructions)
            foreach (string slot in new[] { StateUse(instruction), StateDefinition(instruction) }.OfType<string>())
                if (!slots.ContainsKey(slot))
                    return EhFailure(ManagedEhImportStatusV1.InvalidInput, "HCCIL0817",
                        $"EH state access at IL_{instruction.Offset:x4} references undeclared {slot}.");

        // Must-analysis: exceptional edges carry state BEFORE the throwing instruction;
        // normal/leave edges carry state after a successful assignment. Seed only the
        // real method entry; an EH handler is not a fresh zero-initialized invocation.
        ILookup<int, ManagedEhEdgeV1> outgoing = graph.Edges.ToLookup(edge => edge.SourceOffset);
        Dictionary<int, DecodedInstruction> byOffset = instructions.ToDictionary(instruction => instruction.Offset);
        Dictionary<int, HashSet<string>> assigned = new Dictionary<int, HashSet<string>>
        {
            [instructions[0].Offset] = slots.Values.Where(slot => slot.InitializedAtMethodEntry)
                .Select(slot => slot.Slot).ToHashSet(StringComparer.Ordinal)
        };
        SortedSet<int> work = new SortedSet<int> { instructions[0].Offset };
        while (work.Count != 0)
        {
            int offset = work.Min;
            work.Remove(offset);
            foreach (ManagedEhEdgeV1? edge in outgoing[offset])
            {
                HashSet<string> candidate = new HashSet<string>(assigned[offset], StringComparer.Ordinal);
                if (edge.Kind != ManagedEhEdgeKindV1.ExceptionDispatchCandidate &&
                    StateDefinition(byOffset[offset]) is string defined) candidate.Add(defined);
                if (!assigned.TryGetValue(edge.TargetOffset, out HashSet<string>? current))
                {
                    assigned.Add(edge.TargetOffset, candidate);
                    work.Add(edge.TargetOffset);
                }
                else
                {
                    int previousCount = current.Count;
                    current.IntersectWith(candidate);
                    if (current.Count != previousCount) work.Add(edge.TargetOffset);
                }
            }
        }
        foreach (DecodedInstruction instruction in instructions)
            if (assigned.TryGetValue(instruction.Offset, out HashSet<string>? state) && StateUse(instruction) is string used &&
                instruction.Encoding is not (0x0f or 0x12 or 0xfe0a or 0xfe0d) && !state.Contains(used))
                return EhFailure(ManagedEhImportStatusV1.Unsupported, "HCCIL1818",
                    $"Conservative EH analysis cannot prove initialization of {used} at IL_{instruction.Offset:x4}.");
        ManagedEhStateHomeV1[] homes = graph.RequiredStateHomes.Select(slot => slots[slot]).ToArray();
        graph = graph with
        {
            StateHomes = homes,
            Digest = HybridCpuPlatformContractV1.Hash(string.Join('|', graph.Digest, "typed-eh-homes/v1", initLocals,
                string.Join(';', homes.Select(home =>
                    $"{home.Slot}:{home.Type}:{home.AggregateIdentity}:{home.InitializedAtMethodEntry}"))))
        };
        return null;
    }

    private DecodeResult DecodeManagedEhBody(byte[] bytes, RestrictedCilProvenanceV1 provenance)
    {
        List<DecodedInstruction> instructions = new List<DecodedInstruction>();
        List<int> targets = new List<int>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (instructions.Count >= _budgets.MaximumDecodedInstructions)
                return new([], Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2004",
                    "The decoded instruction budget was exhausted.", OffsetIdentity(provenance, offset), provenance));
            int start = offset;
            ushort encoding = bytes[offset++] is byte first && first == 0xfe
                ? offset < bytes.Length ? (ushort)(0xfe00 | bytes[offset++]) : ushort.MaxValue
                : first;
            if (!ManagedEhOpcodes.TryGetValue(encoding, out OpCode opcode))
                return InvalidDecode($"Opcode encoding 0x{encoding:x4} is invalid or reserved.", provenance, start);

            int operandStart = offset;
            int operandBytes;
            try
            {
                operandBytes = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                    OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                    OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch when bytes.Length - offset >= 4 => checked(4 +
                        4 * BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4))),
                    _ => -1
                };
            }
            catch (OverflowException)
            {
                return InvalidDecode($"Operand for {opcode.Name} overflowed.", provenance, start);
            }
            if (operandBytes < 0 || bytes.Length - offset < operandBytes)
                return InvalidDecode($"Operand for {opcode.Name} is truncated.", provenance, start);
            offset += operandBytes;

            int branchTarget = -1;
            int[]? switchTargets = null;
            if (opcode.OperandType == OperandType.ShortInlineBrTarget)
            {
                branchTarget = checked(offset + unchecked((sbyte)bytes[operandStart]));
                targets.Add(branchTarget);
            }
            else if (opcode.OperandType == OperandType.InlineBrTarget)
            {
                branchTarget = checked(offset + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(operandStart, 4)));
                targets.Add(branchTarget);
            }
            else if (opcode.OperandType == OperandType.InlineSwitch)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(operandStart, 4));
                if (count < 0) return InvalidDecode("Switch target count is negative.", provenance, start);
                switchTargets = new int[count];
                for (int index = 0; index < count; index++)
                {
                    switchTargets[index] = checked(offset + BinaryPrimitives.ReadInt32LittleEndian(
                        bytes.AsSpan(operandStart + 4 + index * 4, 4)));
                    targets.Add(switchTargets[index]);
                }
            }
            int token = opcode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
                OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(operandStart, 4)) : 0;
            instructions.Add(new(start, offset - start, encoding, opcode.Name ?? $"0x{encoding:x4}",
                opcode.OperandType == OperandType.ShortInlineVar ? bytes[operandStart] :
                    opcode.OperandType == OperandType.InlineVar ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(operandStart, 2)) : 0,
                branchTarget, token, SwitchTargets: switchTargets));
        }
        if (instructions.Count == 0) return InvalidDecode("The selected method has an empty CIL body.", provenance, 0);
        HashSet<int> boundaries = instructions.Select(static instruction => instruction.Offset).ToHashSet();
        if (targets.Any(target => !boundaries.Contains(target)))
            return InvalidDecode("A branch target is not a CIL instruction boundary.", provenance, 0);
        return new(instructions, null);
    }

    private static ManagedEhImportResultV1 EhFailure(ManagedEhImportStatusV1 status, string code, string reason) =>
        new(status, null, code, reason);

    private static ulong ManagedTypeId(string identity)
    {
        ulong result = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hybridcpu.managed-type-id/v1|{identity}")));
        return result == 0 ? 1 : result;
    }
}
