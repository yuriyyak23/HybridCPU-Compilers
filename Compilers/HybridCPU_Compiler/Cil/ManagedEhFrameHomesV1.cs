using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

/// <summary>Fixed word-sized scalar homes, not an aggregate layout or byref ABI.
/// Allocation reserves storage only; store/reload emission and GC maps remain required.</summary>
public static class ManagedEhFrameHomesV1
{
    public const string FinallyContinuationTokenSlot = "eh-finally-continuation-token";

    public static IReadOnlyList<HybridCpuFrameSlotRequestV2> CreateRequests(ManagedEhMethodPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ManagedEhControlFlowV1 flow = plan.ControlFlow ?? throw new InvalidOperationException("EH frame homes require PE-validated state analysis.");
        if (!flow.RequiredStateHomes.SequenceEqual(flow.StateHomes.Select(home => home.Slot)) ||
            flow.StateHomes.Any(home => home.Type is not (RestrictedCilTypeV1.Boolean or
                RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.UInt8 or RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16 or
                RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt or RestrictedCilTypeV1.ObjectReference)))
            throw new InvalidOperationException("EH homes require exact scalar/reference state; aggregate/byref storage is not qualified.");
        List<HybridCpuFrameSlotRequestV2> requests = flow.StateHomes.Select(home => new HybridCpuFrameSlotRequestV2($"eh-home:{home.Slot}", 8, 8)).ToList();
        if (plan.Clauses.Any(static clause => clause.Kind == HybridCpuManagedEhClauseKindV1.Finally))
            requests.Add(new(FinallyContinuationTokenSlot, 8, 8));
        return requests;
    }

    public static ManagedEhHomeAccessPlanV1 CreateAccessPlan(ManagedEhMethodPlanV1 plan,
        ManagedEhTypedDataflowV1 dataflow)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dataflow);
        ManagedEhControlFlowV1 flow = plan.ControlFlow ?? throw new InvalidOperationException("EH home access plan requires validated control flow.");
        if (flow.StateHomes.Count == 0) return new([], HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.Hash(
            $"managed-eh-home-access/v1|{plan.ContractDigest}|{dataflow.Digest}|empty"));
        Dictionary<int, ManagedEhTypedStateV1> entries = dataflow.Entries.ToDictionary(row => row.IlOffset);
        Dictionary<ManagedEhEdgeV1, ManagedEhTypedEdgeStateV1> edgeStates = dataflow.EdgeStates.ToDictionary(row => row.Edge);
        int methodEntry = flow.InstructionOffsets[0];
        List<ManagedEhHomeAccessV1> result = new List<ManagedEhHomeAccessV1>();
        foreach (ManagedEhStateHomeV1 home in flow.StateHomes)
        {
            ManagedEhTypedValueV1 Initial() => Value(entries[methodEntry], home.Slot);
            if (home.InitializedAtMethodEntry)
            {
                ManagedEhTypedValueV1 value = Initial();
                result.Add(new(ManagedEhHomeAccessKindV1.Initialize, home.Slot, methodEntry,
                    home.Type, home.IsObjectRoot, value.Identity, ValueKind(value), value.Constant));
            }
            else if (home.IsObjectRoot)
            {
                // A GC-visible fixed slot must never contain uninitialized stack bytes.
                // This zero is storage initialization only; it does not make the CIL local
                // definitely assigned and handler verification remains unchanged.
                result.Add(new(ManagedEhHomeAccessKindV1.Initialize, home.Slot, methodEntry,
                    home.Type, true, $"eh:root-zero:{home.Slot}", ManagedEhHomeValueKindV1.ZeroConstant, 0));
            }
            foreach (ManagedEhEdgeV1 edge in flow.Edges.Where(edge => edge.Kind != ManagedEhEdgeKindV1.ExceptionDispatchCandidate)
                         .OrderBy(edge => edge.SourceOffset).ThenBy(edge => edge.TargetOffset))
            {
                if (!edgeStates.TryGetValue(edge, out ManagedEhTypedEdgeStateV1? typed) ||
                    !entries.TryGetValue(edge.SourceOffset, out ManagedEhTypedStateV1? source)) continue;
                ManagedEhTypedValueV1 before = Value(source, home.Slot);
                ManagedEhTypedValueV1 after = Value(typed.State, home.Slot);
                if (after.Initialized && after.Identity != before.Identity)
                    result.Add(new(ManagedEhHomeAccessKindV1.StoreAfterDefinition, home.Slot,
                        edge.SourceOffset, home.Type, home.IsObjectRoot, after.Identity, ValueKind(after), after.Constant));
            }
            foreach (int reloadOffset in plan.HandlerEntries.Select(static row => row.IlOffset)
                         .Concat(plan.FinallyContinuations.Continuations.Select(static row => row.TargetOffset)).Distinct().Order())
            {
                if (!plan.HandlerEntries.Any(row => row.IlOffset == reloadOffset) &&
                    !flow.LiveStateAtEntry[reloadOffset].Contains(home.Slot)) continue;
                ManagedEhTypedValueV1 value = Value(entries[reloadOffset], home.Slot);
                if (!value.Initialized)
                    throw new InvalidOperationException($"Handler IL_{reloadOffset:x4} requires uninitialized {home.Slot}.");
                result.Add(new(ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry, home.Slot,
                    reloadOffset, home.Type, home.IsObjectRoot,
                    $"eh:reload:{home.Slot}:il_{reloadOffset:x4}", ManagedEhHomeValueKindV1.ReloadDefinition, null));
            }
        }
        ManagedEhHomeAccessV1[] ordered = result.Distinct().OrderBy(row => row.IlOffset).ThenBy(row => row.Kind)
            .ThenBy(row => row.Slot, StringComparer.Ordinal).ToArray();
        string digest = HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.Hash(string.Join('|',
            "managed-eh-home-access/v1", plan.ContractDigest, dataflow.Digest,
                string.Join(';', ordered.Select(row => $"{row.Kind}:{row.Slot}:{row.IlOffset}:{row.Type}:{row.IsObjectRoot}:{row.ValueIdentity}:{row.ValueKind}:{row.ConstantValue}"))));
        return new(ordered, digest);

        static ManagedEhTypedValueV1 Value(ManagedEhTypedStateV1 state, string slot)
        {
            int separator = slot.IndexOf(':');
            int index = int.Parse(slot.AsSpan(separator + 1), System.Globalization.CultureInfo.InvariantCulture);
            return slot.StartsWith("local:", StringComparison.Ordinal) ? state.Locals[index] : state.Arguments[index];
        }
        static ManagedEhHomeValueKindV1 ValueKind(ManagedEhTypedValueV1 value) => value.Constant switch
        {
            0 => ManagedEhHomeValueKindV1.ZeroConstant,
            not null => ManagedEhHomeValueKindV1.NonZeroConstant,
            _ => ManagedEhHomeValueKindV1.ExistingSsaValue
        };
    }

    public static IrOperand ResolveExactOperand(ManagedEhHomeAccessV1 access,
        IReadOnlyDictionary<string, IrOperand> existingValues)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(existingValues);
        return access.ValueKind switch
        {
            ManagedEhHomeValueKindV1.ZeroConstant => new(IrOperandKind.ArchitecturalRegister, 0, access.ValueIdentity),
            ManagedEhHomeValueKindV1.ReloadDefinition => new(IrOperandKind.VirtualValue, 0, access.ValueIdentity),
            ManagedEhHomeValueKindV1.ExistingSsaValue when existingValues.TryGetValue(access.ValueIdentity, out IrOperand? value) &&
                value.Kind is IrOperandKind.VirtualValue or IrOperandKind.ArchitecturalRegister => value,
            ManagedEhHomeValueKindV1.NonZeroConstant => throw new InvalidOperationException(
                "A nonzero EH-home constant requires explicit target constant materialization before SD."),
            _ => throw new InvalidOperationException($"EH-home SSA value '{access.ValueIdentity}' has no exact IR operand.")
        };
    }

    public static IReadOnlyList<ManagedEhHomeInsertionV1> MaterializeSymbolic(
        ManagedEhHomeAccessPlanV1 plan, IReadOnlyDictionary<int, IrInstruction> ilOrigins,
        Func<ManagedEhHomeAccessV1, IrOperand> resolveOperand)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ilOrigins);
        ArgumentNullException.ThrowIfNull(resolveOperand);
        HybridCpuScheduleAwareRegisterAllocatorV1 allocator = new HybridCpuScheduleAwareRegisterAllocatorV1();
        List<ManagedEhHomeInsertionV1> result = new List<ManagedEhHomeInsertionV1>(plan.Accesses.Count);
        foreach (ManagedEhHomeAccessV1 access in plan.Accesses)
        {
            if (!ilOrigins.TryGetValue(access.IlOffset, out IrInstruction? origin))
                throw new InvalidOperationException($"EH home access has no exact IR anchor at IL_{access.IlOffset:x4}.");
            IrOperand operand = resolveOperand(access);
            if (operand is null || operand.Kind is not (IrOperandKind.VirtualValue or IrOperandKind.ArchitecturalRegister) ||
                operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value > 31)
                throw new InvalidOperationException("EH home access resolver did not return a scalar register value.");
            bool load = access.Kind == ManagedEhHomeAccessKindV1.ReloadAtHandlerEntry;
            ManagedEhInsertionPlacementV1 placement = access.Kind == ManagedEhHomeAccessKindV1.StoreAfterDefinition
                ? ManagedEhInsertionPlacementV1.After : ManagedEhInsertionPlacementV1.Before;
            string identity = $"eh-home:{access.Kind}:{access.Slot}:il_{access.IlOffset:x4}";
            IrInstruction instruction = allocator.CreateSymbolicFixedFrameAccess(origin,
                $"eh-home:{access.Slot}", load, operand, identity);
            result.Add(new(access.IlOffset, placement, access, instruction));
        }
        return result;
    }
}
