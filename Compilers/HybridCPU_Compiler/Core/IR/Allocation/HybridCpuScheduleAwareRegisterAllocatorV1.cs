using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public sealed class HybridCpuScheduleAwareRegisterAllocatorV1
{
    private const int EncodedInstructionSizeBytes = 32;
    private static readonly int[] SpillScratchRegisterPool = [28, 29, 30, 31];

    private readonly HybridCpuNativeAbiContractV2 _abi = HybridCpuNativeAbiContractV2.Default;
    private readonly HybridCpuTargetMachineContractV1 _target = HybridCpuTargetMachineContractV1.Default;

    public IrRegisterAllocationResultV1 Allocate(
        IrProgramSchedule selectedSchedule,
        IrProgramBundlingResult selectedBundles,
        IrSelectedAllocationPlansV1? selectedPlans = null,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuRegisterAllocationOptionsV1? options = null,
        IReadOnlyList<HybridCpuFrameSlotRequestV2>? fixedFrameSlots = null)
        => Allocate(CancellationToken.None, selectedSchedule, selectedBundles, selectedPlans, resourceModel, options, fixedFrameSlots);

    /// <summary>Checks caller cancellation at allocation phase boundaries; phase internals remain synchronous.</summary>
    public IrRegisterAllocationResultV1 Allocate(
        CancellationToken cancellationToken,
        IrProgramSchedule selectedSchedule,
        IrProgramBundlingResult selectedBundles,
        IrSelectedAllocationPlansV1? selectedPlans = null,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuRegisterAllocationOptionsV1? options = null,
        IReadOnlyList<HybridCpuFrameSlotRequestV2>? fixedFrameSlots = null)
    {
        ArgumentNullException.ThrowIfNull(selectedSchedule);
        ArgumentNullException.ThrowIfNull(selectedBundles);
        cancellationToken.ThrowIfCancellationRequested();
        selectedPlans ??= IrSelectedAllocationPlansV1.BasicBlockOnly;
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuRegisterAllocationOptionsV1.Production;
        fixedFrameSlots ??= [];

        string inputScheduleDigest = DigestSchedule(selectedSchedule, selectedBundles);
        cancellationToken.ThrowIfCancellationRequested();
        bool planCollectionsValid = selectedPlans.RegionPlanDigests is not null &&
            selectedPlans.LoopOrModuloPlanDigests is not null &&
            selectedPlans.VirtualThreadPlanDigests is not null &&
            selectedPlans.FspOrPrefetchPlanDigests is not null;
        string selectedPlansDigest = planCollectionsValid
            ? DigestSelectedPlans(selectedPlans)
            : HybridCpuRegisterAllocationContractV1.Hash("selected-plans/v1|invalid");
        cancellationToken.ThrowIfCancellationRequested();
        if (fixedFrameSlots.Any(slot => slot is null || string.IsNullOrWhiteSpace(slot.Identity) ||
                slot.Identity.StartsWith("saved:", StringComparison.Ordinal) || slot.Identity.StartsWith("spill:", StringComparison.Ordinal)) ||
            fixedFrameSlots.Select(slot => slot.Identity).Distinct(StringComparer.Ordinal).Count() != fixedFrameSlots.Count)
            return PreserveExactPath(IrRegisterAllocationStatusV1.InvalidInput,
                "Fixed frame slots have absent, duplicate or allocator-reserved identities.", selectedSchedule, selectedBundles,
                inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        if (!ReferenceEquals(selectedBundles.ProgramSchedule, selectedSchedule))
            return PreserveExactPath(IrRegisterAllocationStatusV1.InvalidInput,
                "Selected bundles do not belong to the selected schedule.", selectedSchedule, selectedBundles,
                inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        if (!ValidateOptions(options))
            return PreserveExactPath(IrRegisterAllocationStatusV1.InvalidInput,
                "Allocation options do not bind valid deterministic budgets.", selectedSchedule, selectedBundles,
                inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        if (!options.EnableAllocation)
            return PreserveExactPath(IrRegisterAllocationStatusV1.SafeFallback,
                "Production allocation is disabled; the exact selected schedule and bundles are preserved.",
                selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        if (!planCollectionsValid || !selectedPlans.AllDigests().All(IsSha256))
            return PreserveExactPath(IrRegisterAllocationStatusV1.Unknown,
                "A selected upstream plan identity is absent or malformed.", selectedSchedule, selectedBundles,
                inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        if (!HasCompleteResourceModel(resourceModel))
            return PreserveExactPath(IrRegisterAllocationStatusV1.Unknown,
                "PRF ports, memory geometry and certificate capacity must be known for allocation repair.",
                selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);

        IrProgram inputProgram = selectedSchedule.Program;
        if (inputProgram.Instructions.Count > options.Budgets.MaximumInstructions ||
            inputProgram.ValueFlow.Values.Count > options.Budgets.MaximumValues)
            return PreserveExactPath(IrRegisterAllocationStatusV1.BudgetExhausted,
                "The deterministic allocation input budget was exceeded.", selectedSchedule, selectedBundles,
                inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        if (selectedSchedule.ValueAnalysis.Status != IrValueAnalysisStatus.Complete ||
            !string.Equals(selectedSchedule.ValueAnalysis.TargetContractDigest, _target.ContractDigest, StringComparison.Ordinal))
            return PreserveExactPath(IrRegisterAllocationStatusV1.Unknown,
                "The selected schedule lacks current Phase 07 target-bound liveness/pressure facts.",
                selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Register-role constants require explicit materialization; silently ignoring them
            // changes code. Mixed-role shifts retain their immediate count operand.
            if (inputProgram.Instructions.Any(static instruction =>
                instruction.Annotation.Uses.Select((operand, index) => (operand, index)).Any(pair =>
                    pair.operand.Kind == IrOperandKind.Constant &&
                    HybridCpuConstantMaterializationPlanV1.RequiresRegisterConstantMaterialization(instruction.Opcode, pair.index))))
                return PreserveExactPath(IrRegisterAllocationStatusV1.Unsupported,
                    "Register arithmetic constant operands require explicit materialization before allocation.",
                    selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);
            AllocationSubject subject = BuildSubject(selectedSchedule);
            cancellationToken.ThrowIfCancellationRequested();
            if (!subject.IsSupported)
                return PreserveExactPath(subject.Status, subject.Reason, selectedSchedule, selectedBundles,
                    inputScheduleDigest, selectedPlansDigest, options, resourceModel);

            ColoringOutcome coloring = Color(subject, options);
            cancellationToken.ThrowIfCancellationRequested();
            if (coloring.Status != IrRegisterAllocationStatusV1.Allocated)
                return PreserveExactPath(coloring.Status, coloring.Reason, selectedSchedule, selectedBundles,
                    inputScheduleDigest, selectedPlansDigest, options, resourceModel);

            ScratchOutcome scratch = PlanScratchRegisters(subject, coloring);
            cancellationToken.ThrowIfCancellationRequested();
            if (scratch.Status != IrRegisterAllocationStatusV1.Allocated)
                return PreserveExactPath(scratch.Status, scratch.Reason, selectedSchedule, selectedBundles,
                    inputScheduleDigest, selectedPlansDigest, options, resourceModel);

            bool hasReturn = inputProgram.Instructions.Any(IsReturn);
            bool hasCall = inputProgram.Instructions.Any(IsCall);
            bool hasExceptionTransfer = inputProgram.Instructions.Any(IsExceptionTransfer);
            bool hasTerminalPath = HasTerminalPath(inputProgram);
            if (hasCall && hasTerminalPath && !hasReturn && !hasExceptionTransfer)
                return PreserveExactPath(IrRegisterAllocationStatusV1.Unsupported,
                    "Non-returning call frames require an exact unwind-preserved return address and terminal-transfer frame contract.",
                    selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);
            int[] savedRegisters = hasReturn || hasExceptionTransfer
                ? coloring.Assignments.Values.Select(static assignment => assignment.RegisterId)
                    .Where(_abi.CalleeSavedRegisters.Contains)
                    .Append(hasCall ? HybridCpuNativeAbiContractV2.ReturnAddressRegister : -1)
                    .Where(static register => register >= 0).Distinct().Order().ToArray()
                : Array.Empty<int>();
            if (hasTerminalPath && !hasReturn && !hasExceptionTransfer && coloring.Assignments.Values.Any(assignment =>
                    _abi.CalleeSavedRegisters.Contains(assignment.RegisterId)))
                return PreserveExactPath(IrRegisterAllocationStatusV1.Unsupported,
                    "A function without an architectural return cannot prove callee-save restoration.",
                    selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);
            if ((coloring.SpilledValues.Count != 0 || savedRegisters.Length != 0 || fixedFrameSlots.Count != 0) && inputProgram.Functions.Count > 1)
                return PreserveExactPath(IrRegisterAllocationStatusV1.Unsupported,
                    "Per-function multi-frame lowering is outside the bounded Phase 20 v1 repair slice.",
                    selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);

            HybridCpuFrameSlotRequestV2[] spillSlots = coloring.SpilledValues
                .Select(value => new HybridCpuFrameSlotRequestV2(
                    SpillSlot(value.Value.StableId), SizeBytes(value.Value), AlignmentBytes(value.Value)))
                .Concat(fixedFrameSlots).OrderBy(static slot => slot.Identity, StringComparer.Ordinal).ToArray();
            HybridCpuFrameLayoutV2 frame = _abi.LayoutFrame(new(spillSlots, savedRegisters,
                SavesReturnAddress: hasCall && (hasReturn || hasExceptionTransfer)));
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Status != HybridCpuPlatformFactStatus.Supported)
                return PreserveExactPath(ToAllocationStatus(frame.Status), frame.Reason, selectedSchedule, selectedBundles,
                    inputScheduleDigest, selectedPlansDigest, options, resourceModel);
            if (frame.FrameSizeBytes > short.MaxValue ||
                frame.Slots.Any(static slot => slot.OffsetFromAdjustedStackPointerBytes > short.MaxValue))
                return PreserveExactPath(IrRegisterAllocationStatusV1.Unsupported,
                    "A frame index exceeds the signed 16-bit native memory displacement.", selectedSchedule,
                    selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);
            if (frame.FrameSizeBytes != 0 && !HasBalancedFrameExits(inputProgram))
                return PreserveExactPath(IrRegisterAllocationStatusV1.Unsupported,
                    "A non-empty fixed frame requires a return or exact unwind-preserving exception transfer on every terminal path.",
                    selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);

            LoweringOutcome lowering = Lower(
                inputProgram, subject, coloring, scratch, frame, savedRegisters, options.Budgets);
            cancellationToken.ThrowIfCancellationRequested();
            if (lowering.Status != IrRegisterAllocationStatusV1.Allocated || lowering.Program is null)
                return PreserveExactPath(lowering.Status, lowering.Reason, selectedSchedule, selectedBundles,
                    inputScheduleDigest, selectedPlansDigest, options, resourceModel);
            if (options.Budgets.MaximumRepairStages < 2)
                return PreserveExactPath(IrRegisterAllocationStatusV1.BudgetExhausted,
                    "Two deterministic proof-rebuild stages are required after allocation mutation.",
                    selectedSchedule, selectedBundles, inputScheduleDigest, selectedPlansDigest, options, resourceModel);

            bool allowOrdinaryLoopFallback = inputProgram.Contract.RequiredCapabilities.Contains(
                "optimization.loop-mii-optional-ordinary-fallback/v1", StringComparer.Ordinal);
            RebuildOutcome rebuild = RebuildAllProofs(
                lowering.Program, inputProgram.Contract.DerivedFacts.ProgramMutation, resourceModel,
                allowOrdinaryLoopFallback, lowering.TopologyOrderRanks, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (rebuild.Status != IrRegisterAllocationStatusV1.Allocated ||
                rebuild.Schedule is null || rebuild.Bundles is null || rebuild.Proof is null)
                return PreserveExactPath(rebuild.Status, rebuild.Reason, selectedSchedule, selectedBundles,
                    inputScheduleDigest, selectedPlansDigest, options, resourceModel);

            IrRegisterAssignmentV1[] assignments = coloring.Assignments.Values
                .OrderBy(static assignment => assignment.ValueId, StringComparer.Ordinal).ToArray();
            IrSpillDecisionV1[] spills = BuildSpillWitnesses(subject, coloring, scratch);
            string[] locatedValueIds = assignments.Select(static assignment => assignment.ValueId)
                .Concat(spills.Select(static spill => spill.ValueId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Dictionary<string, IrVirtualValueV1> inputValues = inputProgram.ValueFlow.Values
                .ToDictionary(static value => value.StableId, StringComparer.Ordinal);
            IrAllocatedSemanticValueV1[] semanticValues = locatedValueIds.Select(valueId =>
            {
                IrVirtualValueV1 value = inputValues[valueId];
                return new IrAllocatedSemanticValueV1(valueId, value.ValueKind, value.VirtualClass);
            }).ToArray();
            IrAllocationMutationRecordV1[] mutations = lowering.Mutations
                .OrderBy(static mutation => mutation.FinalInstructionIndex).ThenBy(static mutation => mutation.Kind).ToArray();
            string witnessDigest = DigestWitness(options, resourceModel, inputScheduleDigest, selectedPlansDigest,
                assignments, spills, semanticValues, frame, mutations, rebuild.Proof);
            IrRegisterAllocationWitnessV1 witness = new IrRegisterAllocationWitnessV1(
                HybridCpuRegisterAllocationContractV1.SchemaId,
                HybridCpuRegisterAllocationContractV1.Default.ContractDigest,
                options.OptionsDigest,
                _target.ContractDigest,
                _abi.ContractDigest,
                resourceModel.Topology.ContractDigest,
                resourceModel.ModelDigest,
                inputScheduleDigest,
                selectedPlansDigest,
                assignments,
                spills,
                semanticValues,
                frame,
                mutations,
                rebuild.Proof,
                RepairStages: 2,
                witnessDigest);
            string resultDigest = HybridCpuRegisterAllocationContractV1.Hash(
                $"allocated|{witnessDigest}|{DigestSchedule(rebuild.Schedule, rebuild.Bundles)}");
            cancellationToken.ThrowIfCancellationRequested();
            return new(IrRegisterAllocationStatusV1.Allocated,
                "Architectural allocation, frame lowering and all affected proofs were rebuilt.",
                selectedSchedule, selectedBundles, rebuild.Schedule, rebuild.Bundles, witness,
                Array.Empty<string>(), resultDigest);
        }
        catch (OverflowException)
        {
            return PreserveExactPath(IrRegisterAllocationStatusV1.BudgetExhausted,
                "Allocation arithmetic exceeded deterministic integer bounds.", selectedSchedule, selectedBundles,
                inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        }
        catch (InvalidOperationException exception)
        {
            return PreserveExactPath(IrRegisterAllocationStatusV1.SafeFallback,
                $"Bounded allocation repair failed closed: {exception.Message}", selectedSchedule, selectedBundles,
                inputScheduleDigest, selectedPlansDigest, options, resourceModel);
        }
    }

    private AllocationSubject BuildSubject(IrProgramSchedule schedule)
    {
        IrProgram program = schedule.Program;
        Dictionary<string, IrVirtualValueV1> programValues = program.ValueFlow.Values
            .ToDictionary(static value => value.StableId, StringComparer.Ordinal);
        foreach (IrInstruction instruction in program.Instructions)
        {
            int virtualDefs = instruction.Annotation.Defs.Count(static operand => operand.Kind == IrOperandKind.VirtualValue);
            int virtualUses = instruction.Annotation.Uses.Count(static operand => operand.Kind == IrOperandKind.VirtualValue);
            bool retainedRd = instruction.Operands.Any(static operand =>
                (operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer) && operand.Name == "rd");
            int retainedSources = instruction.Operands.Count(static operand =>
                (operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer) &&
                operand.Name is "rs1" or "rs2");
            bool call = IsCall(instruction);
            bool jalrCall = call && instruction.Opcode == HybridCpuOpcode.JALR;
            bool exactJalrTarget = !jalrCall || HasExactJalrCallTarget(instruction, programValues);
            int callArgumentUses = checked(virtualUses - (jalrCall && exactJalrTarget ? 1 : 0));
            int maximumUses = call ? HybridCpuNativeCallControlContractV1.MaximumRegisterArguments : 2;
            int maximumDefs = call ? HybridCpuNativeCallControlContractV1.MaximumRegisterReturns : 1;
            if (!exactJalrTarget || virtualDefs > maximumDefs || callArgumentUses > maximumUses ||
                virtualDefs != 0 && retainedRd ||
                !call && virtualUses + retainedSources > 2)
                return AllocationSubject.Failure(IrRegisterAllocationStatusV1.Unsupported,
                    $"Instruction {instruction.Index} ({instruction.Opcode}, identity={instruction.StableIdentity}, " +
                    $"IL={instruction.SourceSpan?.StartOffset.ToString(CultureInfo.InvariantCulture) ?? "absent"}) has no " +
                    $"unambiguous native rd/rs1/rs2 encoding after allocation: call={call}, " +
                    $"virtual-defs={virtualDefs}/{maximumDefs}, virtual-uses={virtualUses}, " +
                    $"call-argument-uses={callArgumentUses}/{maximumUses}, exact-jalr-target={exactJalrTarget}, " +
                    $"retained-rd={retainedRd}, retained-sources={retainedSources}, " +
                    $"operands={DescribeOperands(instruction.Operands)}, " +
                    $"defs={DescribeOperands(instruction.Annotation.Defs)}, " +
                    $"uses={DescribeOperands(instruction.Annotation.Uses)}.");
        }
        Dictionary<int, int> positions = BuildScheduledPositions(schedule, out Dictionary<int, (int Start, int End)> blockPositions);
        Dictionary<int, IrInstruction> instructions = program.Instructions.ToDictionary(static instruction => instruction.Index);
        Dictionary<string, List<IrValueAccessV1>> accesses = program.ValueFlow.Accesses
            .GroupBy(static access => access.ValueId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static item => item.InstructionIndex).ToList(), StringComparer.Ordinal);
        Dictionary<int, IrBlockLivenessV1> blockLiveness = schedule.ValueAnalysis.Blocks.ToDictionary(static block => block.BlockId);
        Dictionary<int, int> instructionBlocks = program.BasicBlocks
            .SelectMany(static block => block.Instructions.Select(instruction => (instruction.Index, block.Id)))
            .ToDictionary(static item => item.Index, static item => item.Id);
        List<ValueLifetime> lifetimes = new List<ValueLifetime>();

        static string DescribeOperand(IrOperand operand) =>
            $"{operand.Kind}:{operand.Name}:{operand.Value.ToString(CultureInfo.InvariantCulture)}";

        static string DescribeOperands(IReadOnlyList<IrOperand> operands)
        {
            const int maximumPresented = 6;
            string suffix = operands.Count > maximumPresented
                ? $",...(+{(operands.Count - maximumPresented).ToString(CultureInfo.InvariantCulture)})"
                : string.Empty;
            return $"count={operands.Count.ToString(CultureInfo.InvariantCulture)}:[" +
                string.Join(',', operands.Take(maximumPresented).Select(DescribeOperand)) + suffix + ']';
        }

        foreach (IrVirtualValueV1 value in program.ValueFlow.Values.OrderBy(static value => value.StableId, StringComparer.Ordinal))
        {
            if (!accesses.TryGetValue(value.StableId, out List<IrValueAccessV1>? valueAccesses) || valueAccesses.Count == 0)
                continue;
            if (!value.Allocation.IsAllocatable || value.Allocation.SpecialStateClass.HasValue ||
                value.Allocation.ArchitecturalClass != HybridCpuArchitecturalRegisterClass.ScalarInteger64 ||
                value.Allocation.WidthBits is not (8 or 16 or 32 or 64))
                return AllocationSubject.Failure(IrRegisterAllocationStatusV1.Unsupported,
                    $"Value '{value.StableId}' has no allocatable scalar architectural class.");
            int start = valueAccesses.Min(access => AccessPosition(access, positions));
            int end = checked(valueAccesses.Max(access => AccessPosition(access, positions)) + 1);
            bool methodIngress = value.StableId.EndsWith(":abi", StringComparison.Ordinal) &&
                value.Allocation.FixedRegisterId is int ingressRegister && valueAccesses.Count == 1 &&
                valueAccesses[0].Kind == IrValueAccessKind.Use &&
                instructions[valueAccesses[0].InstructionIndex] is IrInstruction ingressCopy &&
                ingressCopy.StableIdentity.EndsWith(":argument-copy", StringComparison.Ordinal) &&
                ingressCopy.Annotation.Uses.Any(operand => operand.Kind == IrOperandKind.ArchitecturalRegister &&
                    operand.Value == checked((ulong)ingressRegister));
            bool callArgumentBridge = value.StableId.EndsWith("-arg-abi:value", StringComparison.Ordinal) &&
                valueAccesses.Count == 2 && valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Def) is IrValueAccessV1 argumentDefinition &&
                valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Use) is IrValueAccessV1 argumentUse &&
                instructions[argumentDefinition.InstructionIndex] is IrInstruction argumentCopy &&
                argumentCopy.StableIdentity.EndsWith("-arg-copy", StringComparison.Ordinal) &&
                IsCall(instructions[argumentUse.InstructionIndex]);
            bool callResultBridge = (value.StableId.EndsWith("-result-abi:value", StringComparison.Ordinal) ||
                    value.StableId.Contains(":call-result-abi:value", StringComparison.Ordinal)) &&
                valueAccesses.Count == 2 && valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Def) is IrValueAccessV1 resultDefinition &&
                valueAccesses.SingleOrDefault(static access =>
                    access.Kind == IrValueAccessKind.Use) is IrValueAccessV1 resultUse &&
                IsCall(instructions[resultDefinition.InstructionIndex]) &&
                (instructions[resultUse.InstructionIndex].StableIdentity.EndsWith("-result-copy", StringComparison.Ordinal) ||
                 instructions[resultUse.InstructionIndex].StableIdentity.EndsWith(":result-copy", StringComparison.Ordinal));
            bool exactAbiBridge = methodIngress || callArgumentBridge || callResultBridge;
            // Incoming ABI registers already contain live values at method entry.
            // A copy scheduled in a later bundle must not allow an earlier copy's
            // destination to reuse its source register before that first use.
            if (methodIngress) start = 0;
            foreach ((int blockId, IrBlockLivenessV1 liveness) in blockLiveness)
            {
                if (exactAbiBridge) break;
                if (liveness.LiveIn.Contains(value.StableId, StringComparer.Ordinal))
                    start = Math.Min(start, blockPositions[blockId].Start);
                if (liveness.LiveOut.Contains(value.StableId, StringComparer.Ordinal))
                    end = Math.Max(end, blockPositions[blockId].End);
            }

            int[] candidates = CandidateRegisters(value);
            if (candidates.Length == 0)
                return AllocationSubject.Failure(IrRegisterAllocationStatusV1.Unknown,
                    $"Value '{value.StableId}' has no ABI-legal architectural register.");
            bool liveAcrossCall = !exactAbiBridge && instructions.Values.Any(instruction => IsCall(instruction) &&
                positions[instruction.Index] >= start && positions[instruction.Index] < end &&
                !valueAccesses.Any(access => access.InstructionIndex == instruction.Index));
            if (liveAcrossCall)
                candidates = candidates.Except(_abi.CallerSavedRegisters).ToArray();

            bool abiConstrained = false;
            foreach (IrValueAccessV1 access in valueAccesses)
            {
                IrInstruction instruction = instructions[access.InstructionIndex];
                int ordinal = VirtualAccessOrdinal(instruction, value.StableId, access.Kind);
                if (IsCall(instruction))
                {
                    abiConstrained = true;
                    bool indirectTarget = instruction.Opcode == HybridCpuOpcode.JALR &&
                        access.Kind == IrValueAccessKind.Use && ordinal == 0;
                    int argumentOrdinal = instruction.Opcode == HybridCpuOpcode.JALR &&
                        access.Kind == IrValueAccessKind.Use ? ordinal - 1 : ordinal;
                    int[] required = indirectTarget ? [5]
                        : access.Kind == IrValueAccessKind.Use
                        ? argumentOrdinal >= 0 && argumentOrdinal < _abi.ArgumentRegisters.Count
                            ? [_abi.ArgumentRegisters[argumentOrdinal]] : Array.Empty<int>()
                        : ordinal < _abi.ReturnRegisters.Count ? [_abi.ReturnRegisters[ordinal]] : Array.Empty<int>();
                    candidates = candidates.Intersect(required).ToArray();
                }
                else if (IsReturn(instruction) && access.Kind == IrValueAccessKind.Use)
                {
                    abiConstrained = true;
                    int[] required = ordinal < _abi.ReturnRegisters.Count ? [_abi.ReturnRegisters[ordinal]] : Array.Empty<int>();
                    candidates = candidates.Intersect(required).ToArray();
                }
            }
            if (candidates.Length == 0)
                return AllocationSubject.Failure(IrRegisterAllocationStatusV1.Unsupported,
                    $"Value '{value.StableId}' cannot satisfy the native call/return location contract " +
                    $"(liveAcrossCall={liveAcrossCall}; crossingCalls={string.Join(',', instructions.Values.Where(instruction =>
                        IsCall(instruction) && positions[instruction.Index] >= start && positions[instruction.Index] < end &&
                        !valueAccesses.Any(access => access.InstructionIndex == instruction.Index)).Select(instruction => instruction.Index))}; " +
                    $"accesses={string.Join(',', valueAccesses.Select(access =>
                    {
                        IrInstruction instruction = instructions[access.InstructionIndex];
                        return $"{access.InstructionIndex}:{access.Kind}:call={IsCall(instruction)}:" +
                            $"return={IsReturn(instruction)}:ordinal={VirtualAccessOrdinal(instruction, value.StableId, access.Kind)}";
                    }))}).");

            int liveBlocks = valueAccesses.Select(access => instructionBlocks[access.InstructionIndex]).Distinct().Count();
            int useCount = valueAccesses.Count(static access => access.Kind != IrValueAccessKind.Def);
            long spillCost = checked((long)Math.Max(1, useCount) * 1000 + (end - start) + (long)liveBlocks * 100);
            lifetimes.Add(new(value, valueAccesses, start, end, candidates.Order().ToArray(),
                value.Allocation.FixedRegisterId.HasValue || abiConstrained,
                valueAccesses.All(static access => access.Kind != IrValueAccessKind.PhiEdgeUse),
                liveAcrossCall, spillCost));
        }

        return new(true, IrRegisterAllocationStatusV1.Allocated, "supported", lifetimes,
            positions, instructions, blockPositions);
    }

    private static bool HasExactJalrCallTarget(
        IrInstruction instruction,
        IReadOnlyDictionary<string, IrVirtualValueV1> values)
    {
        IrOperand[] virtualUses = instruction.Annotation.Uses
            .Where(static operand => operand.Kind == IrOperandKind.VirtualValue).ToArray();
        return virtualUses.Length != 0 &&
            values.TryGetValue(virtualUses[0].Name, out IrVirtualValueV1? target) &&
            target.Allocation.FixedRegisterId == 5 &&
            instruction.Operands.FirstOrDefault(static operand => operand.Kind == IrOperandKind.VirtualValue) is
                IrOperand encodedTarget &&
            string.Equals(encodedTarget.Name, virtualUses[0].Name, StringComparison.Ordinal);
    }

    private ColoringOutcome Color(AllocationSubject subject, HybridCpuRegisterAllocationOptionsV1 options)
    {
        List<ValueLifetime> spilled = new List<ValueLifetime>();
        for (int spillAttempt = 0; spillAttempt <= options.Budgets.MaximumSpillAlternatives; spillAttempt++)
        {
            List<ValueLifetime> activeValues = subject.Lifetimes.Except(spilled).ToList();
            Dictionary<string, IrRegisterAssignmentV1> assignments = [];
            ValueLifetime? failed = null;
            foreach (ValueLifetime value in activeValues
                         .OrderByDescending(static value => value.IsPrecolored)
                         .ThenByDescending(value => activeValues.Count(other => !ReferenceEquals(value, other) && Overlaps(value, other)))
                         .ThenBy(static value => value.Start)
                         .ThenBy(static value => value.Value.StableId, StringComparer.Ordinal))
            {
                IrRegisterAssignmentV1[] overlaps = assignments.Values
                    .Where(assignment => Overlaps(value.Start, value.End, assignment.ScheduledStart, assignment.ScheduledEndExclusive))
                    .ToArray();
                int[] candidates = value.Candidates
                    .Where(candidate => spilled.Count == 0 || !SpillScratchRegisterPool.Contains(candidate))
                    .Where(candidate => overlaps.All(assignment => assignment.RegisterId != candidate))
                    .Take(options.Budgets.MaximumAllocationCandidates).ToArray();
                if (candidates.Length == 0)
                {
                    failed = value;
                    break;
                }
                int selected = candidates
                    .OrderBy(candidate => assignments.Values.Any(assignment =>
                        assignment.RegisterId == candidate &&
                        !Overlaps(value.Start, value.End, assignment.ScheduledStart, assignment.ScheduledEndExclusive)) ? 0 : 1)
                    .ThenBy(candidate => overlaps.Count(assignment => assignment.RegisterGroup == candidate / HybridCpuMachineTopologyV1.RegistersPerGroup))
                    .ThenBy(candidate => assignments.Values.Count(assignment => assignment.RegisterGroup == candidate / HybridCpuMachineTopologyV1.RegistersPerGroup))
                    .ThenBy(static candidate => candidate)
                    .First();
                assignments.Add(value.Value.StableId, new(
                    value.Value.StableId, selected,
                    selected / HybridCpuMachineTopologyV1.RegistersPerGroup,
                    value.IsPrecolored, value.LiveAcrossCall, value.Start, value.End));
            }

            if (failed is null)
                return new(IrRegisterAllocationStatusV1.Allocated, "colored", assignments, spilled);
            if (!options.EnableSpills)
                return ColoringOutcome.Failure(IrRegisterAllocationStatusV1.SafeFallback,
                    $"Value '{failed.Value.StableId}' cannot be colored and spills are disabled.");

            ValueLifetime[] candidatesToSpill = activeValues
                .Where(value => value.CanSpill && !value.IsPrecolored &&
                    value.Value.Allocation.LegalRegisterIds.Any(SpillScratchRegisterPool.Contains) &&
                    (ReferenceEquals(value, failed) || Overlaps(value, failed)))
                .OrderBy(static value => value.SpillCost)
                .ThenBy(static value => value.Value.StableId, StringComparer.Ordinal).ToArray();
            if (candidatesToSpill.Length == 0)
            {
                string conflicts = string.Join(',', assignments.Values
                    .Where(assignment => failed.Candidates.Contains(assignment.RegisterId) &&
                        Overlaps(failed.Start, failed.End, assignment.ScheduledStart, assignment.ScheduledEndExclusive))
                    .OrderBy(static assignment => assignment.RegisterId)
                    .ThenBy(static assignment => assignment.ValueId, StringComparer.Ordinal)
                    .Select(static assignment => $"{assignment.ValueId}@x{assignment.RegisterId}" +
                        $"[{assignment.ScheduledStart},{assignment.ScheduledEndExclusive})"));
                return ColoringOutcome.Failure(IrRegisterAllocationStatusV1.Unsupported,
                    $"Precolored value '{failed.Value.StableId}'[{failed.Start},{failed.End}) conflicts with the native ABI: {conflicts}.");
            }
            spilled.Add(candidatesToSpill[0]);
        }

        return ColoringOutcome.Failure(IrRegisterAllocationStatusV1.BudgetExhausted,
            "The deterministic spill-alternative budget was exhausted.");
    }

    private ScratchOutcome PlanScratchRegisters(AllocationSubject subject, ColoringOutcome coloring)
    {
        Dictionary<(string ValueId, int InstructionIndex), int> result = new Dictionary<(string ValueId, int InstructionIndex), int>();
        Dictionary<int, HashSet<int>> usedByInstruction = new Dictionary<int, HashSet<int>>();
        foreach (ValueLifetime spill in coloring.SpilledValues.OrderBy(static value => value.Value.StableId, StringComparer.Ordinal))
        {
            foreach (IrValueAccessV1 access in spill.Accesses.OrderBy(static access => access.InstructionIndex).ThenBy(static access => access.Kind))
            {
                IrInstruction instruction = subject.Instructions[access.InstructionIndex];
                if (IsCall(instruction))
                    return ScratchOutcome.Failure(IrRegisterAllocationStatusV1.Unsupported,
                        "Spilling directly at a call boundary requires an explicit call-sequence lowering contract.");
                if (!usedByInstruction.TryGetValue(access.InstructionIndex, out HashSet<int>? used))
                {
                    used = instruction.Annotation.Uses.Concat(instruction.Annotation.Defs)
                        .Where(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister)
                        .Select(static operand => checked((int)operand.Value)).ToHashSet();
                    usedByInstruction.Add(access.InstructionIndex, used);
                }
                int position = subject.Positions[access.InstructionIndex];
                HashSet<int> occupied = coloring.Assignments.Values
                    .Where(assignment => position >= assignment.ScheduledStart && position < assignment.ScheduledEndExclusive)
                    .Select(static assignment => assignment.RegisterId).ToHashSet();
                int scratch = spill.Value.Allocation.LegalRegisterIds.Intersect(SpillScratchRegisterPool)
                    .Where(candidate => !used.Contains(candidate) && !occupied.Contains(candidate))
                    .OrderBy(static candidate => candidate / HybridCpuMachineTopologyV1.RegistersPerGroup)
                    .ThenBy(static candidate => candidate).FirstOrDefault(-1);
                if (scratch < 0)
                    return ScratchOutcome.Failure(IrRegisterAllocationStatusV1.SafeFallback,
                        $"No verified scratch register exists for spilled value '{spill.Value.StableId}'.");
                result[(spill.Value.StableId, access.InstructionIndex)] = scratch;
                used.Add(scratch);
            }
        }
        return new(IrRegisterAllocationStatusV1.Allocated, "scratch-planned", result);
    }

    private LoweringOutcome Lower(
        IrProgram program,
        AllocationSubject subject,
        ColoringOutcome coloring,
        ScratchOutcome scratch,
        HybridCpuFrameLayoutV2 frame,
        IReadOnlyList<int> savedRegisters,
        HybridCpuRegisterAllocationBudgetsV1 budgets)
    {
        Dictionary<string, ValueLifetime> spills = coloring.SpilledValues
            .ToDictionary(static value => value.Value.StableId, StringComparer.Ordinal);
        Dictionary<string, HybridCpuFrameSlotV2> slots = frame.Slots.ToDictionary(static slot => slot.Identity, StringComparer.Ordinal);
        List<PendingInstruction> pending = new List<PendingInstruction>();
        int entryBlockId = program.BasicBlocks.OrderBy(static block => block.Id).FirstOrDefault()?.Id ?? 0;

        foreach (IrBasicBlock block in program.BasicBlocks.OrderBy(static block => block.StartInstructionIndex))
        {
            bool first = true;
            foreach (IrInstruction instruction in block.Instructions)
            {
                if (first && block.Id == entryBlockId && frame.FrameSizeBytes != 0)
                {
                    pending.Add(new(block.Id, CreateStackAdjustment(instruction, -frame.FrameSizeBytes, "prologue"),
                        IrAllocationMutationKindV1.Prologue, "phase20:prologue", null, instruction.Index));
                    foreach (int register in savedRegisters.Order())
                    {
                        HybridCpuFrameSlotV2 slot = slots[$"saved:x{register.ToString(CultureInfo.InvariantCulture)}"];
                        pending.Add(new(block.Id, CreateStackMemory(instruction, isLoad: false, register,
                            slot.OffsetFromAdjustedStackPointerBytes, 8, $"save:x{register}"),
                            IrAllocationMutationKindV1.CalleeSave, $"phase20:save:x{register}", null, instruction.Index));
                    }
                }
                first = false;

                string[] spilledUses = instruction.Annotation.Uses
                    .Where(operand => operand.Kind == IrOperandKind.VirtualValue && spills.ContainsKey(operand.Name))
                    .Select(static operand => operand.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                foreach (string valueId in spilledUses)
                {
                    ValueLifetime spill = spills[valueId];
                    int register = scratch.Registers[(valueId, instruction.Index)];
                    HybridCpuFrameSlotV2 slot = slots[SpillSlot(valueId)];
                    string identity = $"phase20:reload:{valueId}:{instruction.Index.ToString(CultureInfo.InvariantCulture)}";
                    pending.Add(new(block.Id, CreateStackMemory(instruction, true, register,
                        slot.OffsetFromAdjustedStackPointerBytes, SizeBytes(spill.Value), identity),
                        IrAllocationMutationKindV1.SpillReload, identity, valueId, instruction.Index));
                }

                if (IsReturn(instruction) && frame.FrameSizeBytes != 0)
                {
                    foreach (int register in savedRegisters.OrderByDescending(static register => register))
                    {
                        HybridCpuFrameSlotV2 slot = slots[$"saved:x{register.ToString(CultureInfo.InvariantCulture)}"];
                        pending.Add(new(block.Id, CreateStackMemory(instruction, true, register,
                            slot.OffsetFromAdjustedStackPointerBytes, 8, $"restore:x{register}"),
                            IrAllocationMutationKindV1.CalleeRestore, $"phase20:restore:x{register}", null, instruction.Index));
                    }
                    pending.Add(new(block.Id, CreateStackAdjustment(instruction, frame.FrameSizeBytes, "epilogue"),
                        IrAllocationMutationKindV1.Epilogue, "phase20:epilogue", null, instruction.Index));
                }

                IrInstruction rewritten = RewritePhysicalOperands(instruction, coloring, scratch);
                if (instruction.Annotation.FixedFrameSlotIdentity is { } fixedSlot)
                {
                    if (!slots.TryGetValue(fixedSlot, out HybridCpuFrameSlotV2? slot) ||
                        fixedSlot.StartsWith("saved:", StringComparison.Ordinal) || fixedSlot.StartsWith("spill:", StringComparison.Ordinal))
                        return LoweringOutcome.Failure(IrRegisterAllocationStatusV1.InvalidInput,
                            "Symbolic fixed-frame operation does not name a reserved user slot.");
                    if (instruction.Opcode == HybridCpuOpcode.ADDI)
                    {
                        IrOperand[] definitions = rewritten.Annotation.Defs.Where(static operand =>
                            operand.Kind == IrOperandKind.ArchitecturalRegister).ToArray();
                        if (definitions.Length != 1 || definitions[0].Value is 0 or > 31 ||
                            definitions[0].Value == HybridCpuNativeAbiContractV2.StackPointerRegister ||
                            slot.OffsetFromAdjustedStackPointerBytes is < 0 or > short.MaxValue ||
                            (long)slot.OffsetFromAdjustedStackPointerBytes + slot.SizeBytes > frame.FrameSizeBytes)
                            return LoweringOutcome.Failure(IrRegisterAllocationStatusV1.InvalidInput,
                                "Symbolic fixed-frame address requires one allocated register and a bounded signed-16 frame slot.");
                        rewritten = CreateStackAddress(rewritten, checked((int)definitions[0].Value),
                            slot.OffsetFromAdjustedStackPointerBytes, instruction.StableIdentity);
                        pending.Add(new(block.Id, rewritten, IrAllocationMutationKindV1.PhysicalOperandLowering,
                            $"phase20:physical:{instruction.StableIdentity}", null, instruction.Index));
                        continue;
                    }
                    bool load = instruction.Opcode == HybridCpuOpcode.LD;
                    if (instruction.Opcode is not (HybridCpuOpcode.LD or HybridCpuOpcode.SD) ||
                        slot.SizeBytes != 8 || slot.AlignmentBytes < 8)
                        return LoweringOutcome.Failure(IrRegisterAllocationStatusV1.InvalidInput,
                            "Symbolic fixed-frame access does not name a reserved word slot.");
                    IrOperand[] data = (load ? rewritten.Annotation.Defs : rewritten.Annotation.Uses)
                        .Where(operand => operand.Kind == IrOperandKind.ArchitecturalRegister &&
                            operand.Value != HybridCpuNativeAbiContractV2.StackPointerRegister).ToArray();
                    if (data.Length != 1 || data[0].Value > 31 || load && data[0].Value == 0)
                        return LoweringOutcome.Failure(IrRegisterAllocationStatusV1.InvalidInput,
                            "Symbolic fixed-frame access requires one allocated data register.");
                    rewritten = CreateStackMemory(rewritten, load, (int)data[0].Value,
                        slot.OffsetFromAdjustedStackPointerBytes, 8, instruction.StableIdentity);
                }
                pending.Add(new(block.Id, rewritten, IrAllocationMutationKindV1.PhysicalOperandLowering,
                    $"phase20:physical:{instruction.StableIdentity}", null, instruction.Index));

                string[] spilledDefs = instruction.Annotation.Defs
                    .Where(operand => operand.Kind == IrOperandKind.VirtualValue && spills.ContainsKey(operand.Name))
                    .Select(static operand => operand.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                foreach (string valueId in spilledDefs)
                {
                    ValueLifetime spill = spills[valueId];
                    int register = scratch.Registers[(valueId, instruction.Index)];
                    HybridCpuFrameSlotV2 slot = slots[SpillSlot(valueId)];
                    string identity = $"phase20:spill:{valueId}:{instruction.Index.ToString(CultureInfo.InvariantCulture)}";
                    pending.Add(new(block.Id, CreateStackMemory(instruction, false, register,
                        slot.OffsetFromAdjustedStackPointerBytes, SizeBytes(spill.Value), identity),
                        IrAllocationMutationKindV1.SpillStore, identity, valueId, instruction.Index));
                }
            }
        }

        int inserted = pending.Count - program.Instructions.Count;
        if (inserted > budgets.MaximumInsertedInstructions || pending.Count > budgets.MaximumInstructions + budgets.MaximumInsertedInstructions)
            return LoweringOutcome.Failure(IrRegisterAllocationStatusV1.BudgetExhausted,
                "Spill/frame lowering exceeded the deterministic inserted-instruction budget.");

        return ReindexProgram(program, pending, subject.Positions);
    }

    private IrInstruction RewritePhysicalOperands(
        IrInstruction instruction,
        ColoringOutcome coloring,
        ScratchOutcome scratch)
    {
        IrOperand RewriteIdentity(IrOperand operand)
        {
            if (operand.Kind != IrOperandKind.VirtualValue) return operand;
            int register = coloring.Assignments.TryGetValue(operand.Name, out IrRegisterAssignmentV1? assignment)
                ? assignment.RegisterId
                : scratch.Registers[(operand.Name, instruction.Index)];
            return new(IrOperandKind.ArchitecturalRegister, checked((ulong)register), operand.Name);
        }

        IrOperand[] defs = instruction.Annotation.Defs.Select(RewriteIdentity).ToArray();
        IrOperand[] uses = instruction.Annotation.Uses.Select(RewriteIdentity).ToArray();
        IrOperand[] retained = instruction.Operands.Where(static operand => operand.Kind != IrOperandKind.VirtualValue).ToArray();
        bool call = IsCall(instruction);
        IrOperand[] encodedUses = IsReturn(instruction)
            ? [new IrOperand(IrOperandKind.ArchitecturalRegister,
                HybridCpuNativeAbiContractV2.ReturnAddressRegister, "rs1")]
            : call && instruction.Opcode == HybridCpuOpcode.JALR
                ? [new IrOperand(IrOperandKind.ArchitecturalRegister, 5, "rs1")]
            : call ? []
            : uses.Where(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister)
                .Select((operand, ordinal) => operand with { Name = ordinal == 0 ? "rs1" : "rs2" }).ToArray();
        IrOperand[] encodedDefs = call
            ? [new IrOperand(IrOperandKind.ArchitecturalRegister,
                HybridCpuNativeAbiContractV2.ReturnAddressRegister, "rd")]
            : defs.Where(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister)
            .Select(static operand => operand with { Name = "rd" }).ToArray();
        IrOperand[] dependencyUses = IsReturn(instruction)
            ? [.. uses.Where(static operand =>
                    operand.Kind != IrOperandKind.ArchitecturalRegister ||
                    operand.Value != HybridCpuNativeAbiContractV2.ReturnAddressRegister),
                new IrOperand(IrOperandKind.ArchitecturalRegister,
                    HybridCpuNativeAbiContractV2.ReturnAddressRegister, "rs1")]
            : uses;
        IrOperand[] operands = [.. retained, .. encodedUses, .. encodedDefs];
        return instruction with
        {
            Operands = operands,
            Annotation = instruction.Annotation with { Defs = defs, Uses = dependencyUses }
        };
    }

    private LoweringOutcome ReindexProgram(
        IrProgram original,
        IReadOnlyList<PendingInstruction> pending,
        IReadOnlyDictionary<int, int> selectedPositions)
    {
        Dictionary<int, int> oldToPhysicalNew = new Dictionary<int, int>();
        List<(int BlockId, PendingInstruction Pending, IrInstruction Instruction)> indexed = new List<(int BlockId, PendingInstruction Pending, IrInstruction Instruction)>(pending.Count);
        for (int index = 0; index < pending.Count; index++)
        {
            PendingInstruction item = pending[index];
            IrInstruction instruction = item.Instruction with
            {
                Index = index,
                EncodedAddress = checked((ulong)index * EncodedInstructionSizeBytes)
            };
            indexed.Add((item.BlockId, item, instruction));
            if (item.OriginalInstructionIndex.HasValue && item.Kind == IrAllocationMutationKindV1.PhysicalOperandLowering)
                oldToPhysicalNew[item.OriginalInstructionIndex.Value] = index;
        }
        if (oldToPhysicalNew.Count != original.Instructions.Count)
            return LoweringOutcome.Failure(IrRegisterAllocationStatusV1.InvalidInput,
                "Not every original instruction was retained during allocation lowering.");

        Dictionary<int, int> oldToFirstNew = indexed.Where(static item => item.Pending.OriginalInstructionIndex.HasValue)
            .GroupBy(static item => item.Pending.OriginalInstructionIndex!.Value)
            .ToDictionary(static group => group.Key, static group => group.Min(item => item.Instruction.Index));
        Dictionary<int, int> oldToLastNew = indexed.Where(static item => item.Pending.OriginalInstructionIndex.HasValue)
            .GroupBy(static item => item.Pending.OriginalInstructionIndex!.Value)
            .ToDictionary(static group => group.Key, static group => group.Max(item => item.Instruction.Index));

        indexed = indexed.Select(item =>
        {
            int? target = item.Instruction.Annotation.ResolvedBranchTargetInstructionIndex;
            IrInstruction rewritten = target.HasValue
                ? item.Instruction with
                {
                    Annotation = item.Instruction.Annotation with
                    {
                        ResolvedBranchTargetInstructionIndex = oldToFirstNew[target.Value]
                    }
                }
                : item.Instruction;
            return (item.BlockId, item.Pending, rewritten);
        }).ToList();

        Dictionary<int, IrBasicBlock> originalBlocks = original.BasicBlocks.ToDictionary(static block => block.Id);
        IrBasicBlock[] blocks = indexed.GroupBy(static item => item.BlockId)
            .OrderBy(group => originalBlocks[group.Key].StartInstructionIndex)
            .Select(group =>
            {
                IrInstruction[] instructions = group.Select(static item => item.Instruction).ToArray();
                IrBasicBlock old = originalBlocks[group.Key];
                return old with
                {
                    StartInstructionIndex = instructions[0].Index,
                    EndInstructionIndex = instructions[^1].Index,
                    StartAddress = instructions[0].EncodedAddress,
                    EndAddress = instructions[^1].EncodedAddress,
                    Instructions = instructions
                };
            }).ToArray();
        ControlFlowGraph cfg = new(blocks, original.ControlFlowGraph.Edges);

        IrProgramLabel[] labels = original.Labels.Select(label => label with
        {
            InstructionIndex = oldToFirstNew[label.InstructionIndex],
            Address = checked((ulong)oldToFirstNew[label.InstructionIndex] * EncodedInstructionSizeBytes)
        }).ToArray();
        IrEntryPointMetadata[] entries = original.EntryPoints.Select(entry => entry with
        {
            InstructionIndex = oldToFirstNew[entry.InstructionIndex],
            Address = checked((ulong)oldToFirstNew[entry.InstructionIndex] * EncodedInstructionSizeBytes)
        }).ToArray();
        IrSection[] sections = original.Sections.Select(section => section with
        {
            StartInstructionIndex = oldToFirstNew[section.StartInstructionIndex],
            EndInstructionIndex = oldToLastNew[section.EndInstructionIndex]
        }).ToArray();
        IrFunction[] functions = original.Functions.Select(function => function with
        {
            EntryInstructionIndex = oldToFirstNew[function.EntryInstructionIndex],
            EndInstructionIndex = oldToLastNew[function.EndInstructionIndex]
        }).ToArray();
        IrProgramSymbols symbols = RebuildSymbols(labels, entries, sections, functions, blocks);
        IrMutationStamp mutation = original.Contract.DerivedFacts.ProgramMutation.Next();
        IrCanonicalProgramContractV1 initialContract = original.Contract with
        {
            DerivedFacts = new(mutation, null, null, null, null, null)
        };
        IrProgram program = original with
        {
            Instructions = indexed.Select(static item => item.Instruction).ToArray(),
            ControlFlowGraph = cfg,
            Labels = labels,
            EntryPoints = entries,
            Sections = sections,
            Functions = functions,
            Symbols = symbols,
            Contract = initialContract,
            ValueFlow = IrValueFlowGraphV1.Empty
        };
        program = program with { ValueFlow = HybridCpuNativeValueFlowFactoryV1.Create(program) };
        IrAllocationMutationRecordV1[] mutations = indexed
            .Select(item => new IrAllocationMutationRecordV1(item.Pending.Kind, item.Pending.Identity,
                item.Instruction.Index, item.Pending.ResponsibleValueId)).ToArray();
        Dictionary<int, int> localOrdinals = new Dictionary<int, int>();
        Dictionary<int, long> topologyOrderRanks = new Dictionary<int, long>();
        int minimumSelectedPosition = selectedPositions.Values.Min();
        int prologueOrdinal = 0;
        foreach ((int BlockId, PendingInstruction Pending, IrInstruction Instruction) item in indexed.OrderBy(static item => item.Instruction.Index))
        {
            if (item.Pending.OriginalInstructionIndex is not int originalIndex ||
                !selectedPositions.TryGetValue(originalIndex, out int selectedPosition))
                return LoweringOutcome.Failure(IrRegisterAllocationStatusV1.InvalidInput,
                    "An allocation mutation lacks its selected pre-RA schedule position.");
            int localOrdinal = localOrdinals.TryGetValue(originalIndex, out int existing) ? existing : 0;
            localOrdinals[originalIndex] = checked(localOrdinal + 1);
            topologyOrderRanks[item.Instruction.Index] = item.Pending.Kind switch
            {
                IrAllocationMutationKindV1.Prologue or IrAllocationMutationKindV1.CalleeSave =>
                    checked(((long)minimumSelectedPosition - 1L) * 1_000_000L + prologueOrdinal++),
                _ => checked((long)selectedPosition * 1_000_000L + localOrdinal)
            };
        }
        return new(IrRegisterAllocationStatusV1.Allocated, "lowered", program, mutations, topologyOrderRanks);
    }

    private RebuildOutcome RebuildAllProofs(
        IrProgram mutated,
        IrMutationStamp originalMutationStamp,
        HybridCpuMiiResourceModelV1 resourceModel,
        bool allowOrdinaryLoopFallback,
        IReadOnlyDictionary<int, long> topologyOrderRanks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        IrProgramDependencyGraph firstDependencies = BuildPhysicalTopologyDependencies(
            mutated, resourceModel.Topology, topologyOrderRanks, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IrProgramSchedule firstSchedule = scheduler.ScheduleProgram(mutated, firstDependencies, cancellationToken);
        IrProgramBundlingResult firstBundles = new HybridCpuBundleFormer(useClassFirstBinding: true).BundleProgram(cancellationToken, firstSchedule);
        if (!ValidateResourceCaps(firstSchedule, resourceModel, out string resourceReason))
            return RebuildOutcome.Failure(IrRegisterAllocationStatusV1.SafeFallback, resourceReason);
        if (!ValidateExactPlacement(firstBundles))
            return RebuildOutcome.Failure(IrRegisterAllocationStatusV1.SafeFallback,
                "Exact W=8 placement validation failed after allocation mutation.");

        HybridCpuLoopMiiAnalyzerV1 loopAnalyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrLoopCanonicalizationResultV1 canonical = loopAnalyzer.Canonicalize(firstSchedule.Program, resourceModel);
        cancellationToken.ThrowIfCancellationRequested();
        List<IrLoopAllocationRebuildV1> loops = new List<IrLoopAllocationRebuildV1>();
        foreach (IrCanonicalLoopV1 loop in canonical.Loops.OrderBy(static loop => loop.HeaderBlockId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IrLoopMiiReportV1? mii = loop.Status == IrCanonicalLoopStatusV1.Qualified
                ? loopAnalyzer.ComputeMii(firstSchedule.Program, loop, resourceModel)
                : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (loop.Status == IrCanonicalLoopStatusV1.Qualified &&
                mii!.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound)
                return RebuildOutcome.Failure(mii!.Components.Any(static component =>
                        component.Reason is "recurrence-cycle-budget-exhausted" or "recurrence-relaxation-budget-exhausted")
                        ? IrRegisterAllocationStatusV1.BudgetExhausted : IrRegisterAllocationStatusV1.SafeFallback,
                    $"Loop {loop.LoopId} lacks a fresh complete MII proof after allocation mutation: " +
                    $"eligibility={mii.Eligibility}; components=" + string.Join(";", mii.Components
                        .Where(static component => component.Status is IrMiiComponentStatusV1.Unknown or IrMiiComponentStatusV1.Unsupported)
                        .Select(static component => $"{component.Component}:{component.Status}:{component.Reason}")) +
                    "; diagnostics=" + string.Join(";", mii.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            if (loop.Status == IrCanonicalLoopStatusV1.BudgetExhausted)
                return RebuildOutcome.Failure(IrRegisterAllocationStatusV1.BudgetExhausted,
                    $"Loop {loop.LoopId} exhausted the deterministic loop-analysis budget after allocation mutation: " +
                    $"reason={loop.Reason}; blocks={loop.BlockIds.Count}/{HybridCpuLoopMiiBudgetsV1.Production.MaximumBlocksPerLoop}; " +
                    $"distanceEdges={loop.DistanceDependencies.Count}/{HybridCpuLoopMiiBudgetsV1.Production.MaximumDistanceEdges}.");
            if (loop.Status != IrCanonicalLoopStatusV1.Qualified && !allowOrdinaryLoopFallback)
                return RebuildOutcome.Failure(IrRegisterAllocationStatusV1.SafeFallback,
                    $"Loop {loop.LoopId} cannot provide fresh distance-DAG/MII evidence after allocation mutation: {loop.Status}.");
            loops.Add(new(loop.LoopId, loop.Status, mii?.Eligibility, mii?.ProvenLowerBoundIi,
                loop.DistanceDagDigest, mii?.ProofStamp.ProofDigest));
        }
        if (canonical.Status == IrCanonicalLoopStatusV1.BudgetExhausted)
            return RebuildOutcome.Failure(IrRegisterAllocationStatusV1.BudgetExhausted,
                "Loop/MII recomputation exhausted its deterministic budget.");

        bool allLoopsHaveMii = loops.All(static loop => loop.CanonicalStatus == IrCanonicalLoopStatusV1.Qualified);
        IrDerivedFactVersionsV1 facts = firstSchedule.Program.Contract.DerivedFacts;
        IrProgram stamped = firstSchedule.Program with
        {
            Contract = firstSchedule.Program.Contract with
            {
                DerivedFacts = facts with
                {
                    Dependency = facts.ProgramMutation,
                    Liveness = facts.ProgramMutation,
                    Pressure = facts.ProgramMutation,
                    Mii = allLoopsHaveMii ? facts.ProgramMutation : null,
                    Placement = facts.ProgramMutation
                }
            }
        };
        // Stamping derived facts above changes neither instructions, CFG nor value
        // flow. Reuse their exact dependency graph, rather than materializing the
        // same potentially dense graph a second time while the first is still live.
        // Scheduling, liveness, placement and resource validation below still run.
        if (!ReferenceEquals(stamped.Instructions, mutated.Instructions) ||
            !ReferenceEquals(stamped.ControlFlowGraph, mutated.ControlFlowGraph) ||
            !ReferenceEquals(stamped.ValueFlow, mutated.ValueFlow))
            return RebuildOutcome.Failure(IrRegisterAllocationStatusV1.InvalidInput,
                "Fact stamping unexpectedly mutated the dependency inputs.");
        IrProgramDependencyGraph finalDependencies = firstDependencies;
        IrProgramSchedule finalSchedule = scheduler.ScheduleProgram(stamped, finalDependencies, cancellationToken);
        IrProgramBundlingResult finalBundles = new HybridCpuBundleFormer(useClassFirstBinding: true).BundleProgram(cancellationToken, finalSchedule);
        if (!ValidateResourceCaps(finalSchedule, resourceModel, out resourceReason) ||
            !ValidateExactPlacement(finalBundles))
            return RebuildOutcome.Failure(IrRegisterAllocationStatusV1.SafeFallback,
                string.IsNullOrWhiteSpace(resourceReason) ? "Final exact placement revalidation failed." : resourceReason);

        IrDerivedFactVersionsV1 finalFacts = finalSchedule.Program.Contract.DerivedFacts;
        string dependencyDigest = DigestDependencies(finalSchedule.DependencyGraph);
        IrAllocationProofRebuildV1 proof = new IrAllocationProofRebuildV1(
            originalMutationStamp,
            finalFacts.ProgramMutation,
            dependencyDigest,
            finalSchedule.ValueAnalysis.ValueFlowDigest,
            DigestScheduleOnly(finalSchedule),
            DigestPlacement(finalBundles),
            loops,
            finalFacts.IsCurrent(finalFacts.Dependency),
            finalFacts.IsCurrent(finalFacts.Liveness),
            finalFacts.IsCurrent(finalFacts.Pressure),
            ResourceFactsCurrent: true,
            MiiRecomputed: finalFacts.IsCurrent(finalFacts.Mii),
            ExactW8PlacementRecomputed: finalFacts.IsCurrent(finalFacts.Placement));
        cancellationToken.ThrowIfCancellationRequested();
        return new(IrRegisterAllocationStatusV1.Allocated, "rebuilt", finalSchedule, finalBundles, proof);
    }

    private static IrProgramDependencyGraph BuildPhysicalTopologyDependencies(
        IrProgram program,
        HybridCpuMachineTopologyV1 topology,
        IReadOnlyDictionary<int, long> topologyOrderRanks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IrProgramDependencyGraph precise = new HybridCpuProgramDependencyAnalyzer().AnalyzeProgram(program);
        cancellationToken.ThrowIfCancellationRequested();
        IrBasicBlockDependencyGraph[] blocks = precise.BlockGraphs.Select(block =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<int, IrInstruction> instructionsByIndex = block.Instructions
                .ToDictionary(static instruction => instruction.Index);
            IrInstructionDependency[] nonRegisterDependencies = block.Dependencies.Where(dependency =>
                dependency.Kind is not (IrInstructionDependencyKind.RegisterRaw or
                    IrInstructionDependencyKind.RegisterWar or IrInstructionDependencyKind.RegisterWaw)).Distinct().ToArray();
            Dictionary<int, int> compatibleOrder = BuildDependencyCompatibleTopologyOrder(
                block, nonRegisterDependencies, topologyOrderRanks, cancellationToken);
            HashSet<IrInstructionDependency> dependencies = new HashSet<IrInstructionDependency>(nonRegisterDependencies);
            foreach (IrInstructionDependency dependency in block.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dependency.Kind is not (IrInstructionDependencyKind.RegisterRaw or
                    IrInstructionDependencyKind.RegisterWar or IrInstructionDependencyKind.RegisterWaw))
                    continue;
                IrInstruction left = instructionsByIndex[dependency.ProducerInstructionIndex];
                IrInstruction right = instructionsByIndex[dependency.ConsumerInstructionIndex];
                IrInstruction producer = compatibleOrder[left.Index] <= compatibleOrder[right.Index] ? left : right;
                IrInstruction consumer = producer.Index == left.Index ? right : left;
                dependencies.Add(dependency with
                {
                    ProducerInstructionIndex = producer.Index,
                    ConsumerInstructionIndex = consumer.Index
                });
            }
            // Masks and ordering ranks are immutable for this build. Computing them
            // once preserves every pairwise edge and its ordering, without repeated
            // LINQ operand walks/dictionary lookups for each pair.
            int instructionCount = block.Instructions.Count;
            ushort[] reads = new ushort[instructionCount];
            ushort[] writes = new ushort[instructionCount];
            long[] ranks = new long[instructionCount];
            for (int index = 0; index < instructionCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IrInstruction instruction = block.Instructions[index];
                reads[index] = PhysicalRegisterGroupMask(instruction.Annotation.Uses, topology);
                writes[index] = PhysicalRegisterGroupMask(instruction.Annotation.Defs, topology);
                ranks[index] = compatibleOrder[instruction.Index];
            }
            // Exact hazard frontier for each physical register group. The previous
            // all-pairs loop emitted every transitive RAW/WAR/WAW serialization edge
            // (O(V^2) objects). Last-writer plus readers-since-write is the canonical
            // dependency frontier: it preserves the same topology order and forbids
            // exactly the same conflicting instructions from sharing/reordering a
            // cycle, while read/read remains unconstrained.
            int[] orderedOrdinals = Enumerable.Range(0, instructionCount)
                .OrderBy(index => ranks[index])
                .ThenBy(index => block.Instructions[index].Index).ToArray();
            foreach ((int producerOrdinal, int consumerOrdinal) in
                     BuildRegisterGroupSerializationFrontier(reads, writes, orderedOrdinals, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                dependencies.Add(new(IrInstructionDependencyKind.Serialization,
                    block.Instructions[producerOrdinal].Index,
                    block.Instructions[consumerOrdinal].Index,
                    MinimumLatencyCycles: 1,
                    DominantEffectKind: IrHazardEffectKind.RegisterData));
            }
            IrInstructionDependency[] ordered = dependencies
                .OrderBy(static dependency => dependency.ProducerInstructionIndex)
                .ThenBy(static dependency => dependency.ConsumerInstructionIndex)
                .ThenBy(static dependency => dependency.Kind)
                .ThenBy(static dependency => dependency.RelatedOperandKind)
                .ThenBy(static dependency => dependency.RelatedOperandValue)
                .ToArray();
            IrBasicBlockDependencyGraph result = new IrBasicBlockDependencyGraph(block.BlockId, block.Instructions, ordered);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new IrProgramDependencyGraph(blocks, precise.InterBlockGraph);
    }

    private static Dictionary<int, int> BuildDependencyCompatibleTopologyOrder(
        IrBasicBlockDependencyGraph block,
        IReadOnlyList<IrInstructionDependency> dependencies,
        IReadOnlyDictionary<int, long> preferredRanks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<int, int> indegrees = block.Instructions.ToDictionary(static instruction => instruction.Index, static _ => 0);
        Dictionary<int, List<int>> outgoing = block.Instructions.ToDictionary(static instruction => instruction.Index,
            static _ => new List<int>());
        foreach ((int producer, int consumer) in dependencies
                     .Select(static dependency => (dependency.ProducerInstructionIndex, dependency.ConsumerInstructionIndex))
                     .Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indegrees.ContainsKey(producer) || !indegrees.ContainsKey(consumer))
                throw new InvalidOperationException($"Allocation rebuild block {block.BlockId} has an invalid non-register dependency.");
            outgoing[producer].Add(consumer);
            indegrees[consumer] = checked(indegrees[consumer] + 1);
        }
        SortedSet<(long Rank, int InstructionIndex)> ready = new SortedSet<(long Rank, int InstructionIndex)>();
        foreach (IrInstruction instruction in block.Instructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (indegrees[instruction.Index] == 0)
                ready.Add((preferredRanks[instruction.Index], instruction.Index));
        }
        Dictionary<int, int> result = new Dictionary<int, int>(block.Instructions.Count);
        while (ready.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (long Rank, int InstructionIndex) next = ready.Min;
            ready.Remove(next);
            int instructionIndex = next.InstructionIndex;
            result.Add(instructionIndex, result.Count);
            foreach (int consumer in outgoing[instructionIndex].Distinct().Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (--indegrees[consumer] == 0)
                    ready.Add((preferredRanks[consumer], consumer));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Count != block.Instructions.Count)
            throw new InvalidOperationException(
                $"Allocation rebuild block {block.BlockId} has a cycle in its non-register dependency contract.");
        return result;
    }

    private static IReadOnlyList<(int ProducerOrdinal, int ConsumerOrdinal)>
        BuildRegisterGroupSerializationFrontier(
            IReadOnlyList<ushort> reads,
            IReadOnlyList<ushort> writes,
            IReadOnlyList<int> orderedOrdinals,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reads.Count != writes.Count || orderedOrdinals.Count != reads.Count ||
            orderedOrdinals.Distinct().Count() != reads.Count ||
            orderedOrdinals.Any(ordinal => ordinal < 0 || ordinal >= reads.Count))
            throw new InvalidOperationException("Physical register-group frontier input is malformed.");
        HashSet<(int ProducerOrdinal, int ConsumerOrdinal)> result = new HashSet<(int ProducerOrdinal, int ConsumerOrdinal)>();
        for (int group = 0; group < HybridCpuMachineTopologyV1.RegisterGroupCount; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lastWriter = -1;
            List<int> readers = new List<int>();
            ushort groupBit = checked((ushort)(1 << group));
            foreach (int ordinal in orderedOrdinals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool readsGroup = (reads[ordinal] & groupBit) != 0;
                bool writesGroup = (writes[ordinal] & groupBit) != 0;
                if (!readsGroup && !writesGroup) continue;
                if (lastWriter >= 0) result.Add((lastWriter, ordinal));
                if (writesGroup)
                {
                    foreach (int reader in readers)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.Add((reader, ordinal));
                    }
                    readers.Clear();
                    lastWriter = ordinal;
                }
                else if (readsGroup)
                {
                    readers.Add(ordinal);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.OrderBy(static edge => edge.ProducerOrdinal)
            .ThenBy(static edge => edge.ConsumerOrdinal).ToArray();
    }

    private static int CompareTopologyOrder(
        IrInstruction left,
        IrInstruction right,
        IReadOnlyDictionary<int, long> topologyOrderRanks)
    {
        long leftRank = topologyOrderRanks[left.Index];
        long rightRank = topologyOrderRanks[right.Index];
        int result = leftRank.CompareTo(rightRank);
        return result != 0 ? result : left.Index.CompareTo(right.Index);
    }

    private static ushort PhysicalRegisterGroupMask(
        IReadOnlyList<IrOperand> operands,
        HybridCpuMachineTopologyV1 topology)
    {
        ushort mask = 0;
        foreach (IrOperand operand in operands.Where(static operand =>
                     operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer))
        {
            if (operand.Value > HybridCpuMachineTopologyV1.MaximumRepresentableRegisterId) continue;
            mask |= checked((ushort)(1 << topology.GetRegisterGroup(checked((int)operand.Value))));
        }
        return mask;
    }

    private IrInstruction CreateStackAdjustment(IrInstruction origin, int adjustment, string suffix)
    {
        IrOperand destination = new IrOperand(IrOperandKind.ArchitecturalRegister,
            HybridCpuNativeAbiContractV2.StackPointerRegister, "rd");
        IrOperand source = new IrOperand(IrOperandKind.ArchitecturalRegister,
            HybridCpuNativeAbiContractV2.StackPointerRegister, "rs1");
        return SyntheticInstruction(origin, HybridCpuOpcode.ADDI, HybridCpuDataType.INT64,
            unchecked((ushort)(short)adjustment),
            [destination, source, new(IrOperandKind.Immediate, unchecked((ushort)(short)adjustment), "stackAdjustment")],
            [destination], [source], IrResourceClass.ScalarAlu, IrLatencyClass.SingleCycle, 1,
            IrIssueSlotMask.Scalar, IrStructuralResource.None, memory: null,
            $"phase20:{suffix}:{origin.Index.ToString(CultureInfo.InvariantCulture)}");
    }

    public IrInstruction CreateSymbolicFixedFrameAccess(IrInstruction origin, string slotIdentity,
        bool isLoad, IrOperand data, string identity)
    {
        if (string.IsNullOrWhiteSpace(slotIdentity) || string.IsNullOrWhiteSpace(identity) ||
            data.Kind is not (IrOperandKind.VirtualValue or IrOperandKind.ArchitecturalRegister) ||
            data.Kind == IrOperandKind.ArchitecturalRegister && (data.Value > 31 || data.Value == 2 || isLoad && data.Value == 0))
            throw new ArgumentException("A symbolic home access requires an exact slot, identity and scalar data operand.");
        IrInstruction instruction = CreateStackMemory(origin, isLoad, 0, 0, 8, identity);
        IrOperand sp = new IrOperand(IrOperandKind.ArchitecturalRegister, 2, "rs1");
        return instruction with
        {
            Operands = [data, sp, new(IrOperandKind.MemoryAddress, 0, "unresolved-frame-offset")],
            Annotation = instruction.Annotation with
            {
                FixedFrameSlotIdentity = slotIdentity,
                Defs = isLoad ? [data] : [], Uses = isLoad ? [sp] : [sp, data]
            }
        };
    }

    /// <summary>Creates a word-sized access to a reserved fixed home using final frame
    /// offsets. The caller must insert it before rebuilding schedule/dependencies/GC maps;
    /// this instruction alone grants no publication or exception-transfer authority.</summary>
    public IrInstruction CreateFixedFrameAccess(IrRegisterAllocationResultV1 allocation,
        IrInstruction origin, string slotIdentity, bool isLoad, int register, string identity)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(origin);
        if (allocation.Status != IrRegisterAllocationStatusV1.Allocated || allocation.Witness is not { } witness ||
            !witness.Rebuild.DependenciesCurrent || !witness.Rebuild.LivenessCurrent ||
            !witness.Rebuild.PressureCurrent || !witness.Rebuild.ResourceFactsCurrent || !witness.Rebuild.ExactW8PlacementRecomputed)
            throw new InvalidOperationException("Fixed home access requires final allocation/frame proof.");
        if (string.IsNullOrWhiteSpace(slotIdentity) || string.IsNullOrWhiteSpace(identity) ||
            slotIdentity.StartsWith("saved:", StringComparison.Ordinal) || slotIdentity.StartsWith("spill:", StringComparison.Ordinal) ||
            register is < 0 or > 31 || register == HybridCpuNativeAbiContractV2.StackPointerRegister || isLoad && register == 0)
            throw new ArgumentException("Fixed home access has an invalid identity or data register.");
        HybridCpuFrameLayoutV2 frame = witness.Frame;
        HybridCpuFrameSlotV2? slot = frame.Slots.SingleOrDefault(row => row.Identity == slotIdentity);
        if (frame.Status != HybridCpuPlatformFactStatus.Supported || slot is null || slot.SizeBytes != 8 ||
            slot.AlignmentBytes < 8 || slot.OffsetFromAdjustedStackPointerBytes < 0 ||
            slot.OffsetFromAdjustedStackPointerBytes % 8 != 0 || slot.OffsetFromAdjustedStackPointerBytes > short.MaxValue ||
            (long)slot.OffsetFromAdjustedStackPointerBytes + 8 > frame.FrameSizeBytes)
            throw new InvalidOperationException("Fixed home must be a bounded aligned word slot in the final frame.");
        return CreateStackMemory(origin, isLoad, register, slot.OffsetFromAdjustedStackPointerBytes, 8, identity);
    }

    private IrInstruction CreateStackMemory(
        IrInstruction origin,
        bool isLoad,
        int register,
        int offset,
        int sizeBytes,
        string suffix)
    {
        HybridCpuOpcode opcode = MemoryOpcode(isLoad, sizeBytes);
        HybridCpuDataType dataType = DataType(sizeBytes);
        IrOperand value = new IrOperand(IrOperandKind.ArchitecturalRegister, checked((ulong)register), isLoad ? "rd" : "rs2");
        IrOperand sp = new IrOperand(IrOperandKind.ArchitecturalRegister,
            HybridCpuNativeAbiContractV2.StackPointerRegister, "rs1");
        IrOperand address = new IrOperand(IrOperandKind.MemoryAddress, checked((ulong)offset), "frameOffset");
        IrMemoryRegion region = new(checked((ulong)offset), checked((uint)sizeBytes), IsWrite: !isLoad);
        return SyntheticInstruction(origin, opcode, dataType, checked((ushort)offset), [value, sp, address],
            isLoad ? [value] : Array.Empty<IrOperand>(),
            isLoad ? [sp] : [sp, value],
            IrResourceClass.LoadStore, IrLatencyClass.LoadUse, isLoad ? (byte)3 : (byte)1,
            IrIssueSlotMask.Lsu,
            IrStructuralResource.AddressGenerationUnit | (isLoad ? IrStructuralResource.LoadDataPort : IrStructuralResource.StoreDataPort),
            region,
            suffix);
    }

    private IrInstruction CreateStackAddress(IrInstruction origin, int register, int offset, string identity)
    {
        IrOperand destination = new IrOperand(IrOperandKind.ArchitecturalRegister, checked((ulong)register), "rd");
        IrOperand sp = new IrOperand(IrOperandKind.ArchitecturalRegister,
            HybridCpuNativeAbiContractV2.StackPointerRegister, "rs1");
        IrOperand immediate = new IrOperand(IrOperandKind.Immediate, unchecked((ushort)(short)offset), "frameOffset");
        IrInstruction result = SyntheticInstruction(origin, HybridCpuOpcode.ADDI, HybridCpuDataType.UINT64,
            unchecked((ushort)(short)offset), [destination, sp, immediate], [destination], [sp],
            IrResourceClass.ScalarAlu, IrLatencyClass.SingleCycle, 1, IrIssueSlotMask.Scalar,
            IrStructuralResource.None, null, identity);
        return result with
        {
            CanonicalType = new(IrCanonicalValueKind.ManagedByRef, 64, IsSigned: false),
            Semantics = IrInstructionSemanticsV1.Native(mayFault: false) with
            {
                PointerArithmetic = IrPointerArithmeticSemantics.BoundsChecked
            }
        };
    }

    private static IrInstruction SyntheticInstruction(
        IrInstruction origin,
        HybridCpuOpcode opcode,
        HybridCpuDataType dataType,
        ushort immediate,
        IReadOnlyList<IrOperand> operands,
        IReadOnlyList<IrOperand> defs,
        IReadOnlyList<IrOperand> uses,
        IrResourceClass resourceClass,
        IrLatencyClass latency,
        byte minimumLatency,
        IrIssueSlotMask legalSlots,
        IrStructuralResource structural,
        IrMemoryRegion? memory,
        string identity)
    {
        IrMemoryEffectKind memoryKind = memory is null
            ? IrMemoryEffectKind.None
            : memory.IsWrite ? IrMemoryEffectKind.Write : IrMemoryEffectKind.Read;
        IrSerializationKind serialization = memory is null
            ? IrSerializationKind.None
            : IrSerializationKind.ExclusiveCycle;
        IrInstructionAnnotation annotation = new IrInstructionAnnotation(
            resourceClass, latency, minimumLatency, legalSlots, serialization, structural,
            IrControlFlowKind.None, IsBarrierLike: false, MayTrap: memory is not null,
            EncodedBranchTarget: null, ResolvedBranchTargetInstructionIndex: null,
            MemoryReadRegion: memory is { IsWrite: false } ? memory : null,
            MemoryWriteRegion: memory is { IsWrite: true } ? memory : null,
            defs, uses, IrSlotClassMapping.ToSlotClass(resourceClass),
            IrSlotClassMapping.DerivePinningKind(resourceClass, serialization),
            origin.Annotation.DomainTag);
        IrSourceOriginLinkV1 generated = new(identity, IrSourceOriginKind.Generated,
            "HybridCPU.Compiler.Core", "20", origin.SourceSpan, IrFrontendEvidenceTrust.ValidatedStructural);
        return new(-1, origin.VirtualThreadId, 0, opcode, dataType, byte.MaxValue, immediate,
            0, 0, 0, false, false, false, false, false, operands, annotation, origin.SourceSpan)
        {
            InstructionClass = resourceClass == IrResourceClass.LoadStore
                ? HybridCpuInstructionClass.Memory : HybridCpuInstructionClass.ScalarAlu,
            SerializationClass = memory is null
                ? HybridCpuSerializationClass.Free : HybridCpuSerializationClass.MemoryOrdered,
            StableIdentity = identity,
            CanonicalType = new(IrCanonicalValueKind.Integer, sizeBits(dataType), IsSigned: false),
            Semantics = IrInstructionSemanticsV1.Native(mayFault: memory is not null),
            OriginChain = new(1, new[] { generated }.Concat(origin.OriginChain.Links).ToArray()),
            SideEffects = new(new(memoryKind, memory is null ? IrAddressSpaceIdentity.Generic : IrAddressSpaceIdentity.Stack,
                IrMemoryOrdering.NotAtomic,
                memory is { IsWrite: false } ? memory : null,
                memory is { IsWrite: true } ? memory : null), IrArchitecturalEffectKind.None)
        };

        static int sizeBits(HybridCpuDataType value) => HybridCpuDataTypes.SizeOf(value) * 8;
    }

    private static IrProgramSymbols RebuildSymbols(
        IReadOnlyList<IrProgramLabel> labels,
        IReadOnlyList<IrEntryPointMetadata> entries,
        IReadOnlyList<IrSection> sections,
        IReadOnlyList<IrFunction> functions,
        IReadOnlyList<IrBasicBlock> blocks)
    {
        IrSectionSymbolGroup[] sectionGroups = sections.Select(section => new IrSectionSymbolGroup(
            section,
            blocks.Where(block => section.BlockIds.Contains(block.Id)).ToArray(),
            functions.Where(function => string.Equals(function.SectionName, section.Name, StringComparison.Ordinal)).ToArray(),
            labels.Where(label => string.Equals(label.SectionName, section.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.SectionName, section.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        IrFunctionSymbolGroup[] functionGroups = functions.Select(function => new IrFunctionSymbolGroup(
            function,
            blocks.Where(block => function.BlockIds.Contains(block.Id)).ToArray(),
            labels.Where(label => string.Equals(label.FunctionName, function.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.FunctionName, function.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        return new(labels, entries, sections, functions, sectionGroups, functionGroups);
    }

    private int[] CandidateRegisters(IrVirtualValueV1 value)
    {
        if (value.Allocation.FixedRegisterId is int fixedRegister)
            return value.Allocation.LegalRegisterIds.Contains(fixedRegister) ? [fixedRegister] : Array.Empty<int>();
        return value.Allocation.LegalRegisterIds.Intersect(_abi.AllocatableRegisters).Order().ToArray();
    }

    private static Dictionary<int, int> BuildScheduledPositions(
        IrProgramSchedule schedule,
        out Dictionary<int, (int Start, int End)> blockPositions)
    {
        Dictionary<int, int> positions = new Dictionary<int, int>();
        blockPositions = [];
        int blockBase = 0;
        foreach (IrBasicBlockSchedule block in schedule.BlockSchedules.OrderBy(static block => block.Block.StartInstructionIndex))
        {
            int start = checked(blockBase * 2);
            foreach (IrScheduledInstruction instruction in block.ScheduledInstructions)
                positions[instruction.InstructionIndex] = checked((blockBase + instruction.Cycle * 8 + instruction.OrderInCycle) * 2);
            int end = checked((blockBase + Math.Max(1, block.ScheduleLength) * 8) * 2);
            blockPositions[block.BlockId] = (start, end);
            blockBase = checked(end / 2 + 8);
        }
        return positions;
    }

    private static int AccessPosition(IrValueAccessV1 access, IReadOnlyDictionary<int, int> positions) =>
        checked(positions[access.InstructionIndex] + (access.Kind == IrValueAccessKind.Def ? 1 : 0));

    private static bool ValidateResourceCaps(
        IrProgramSchedule schedule,
        HybridCpuMiiResourceModelV1 resourceModel,
        out string reason)
    {
        HybridCpuMachineTopologyV1 topology = resourceModel.Topology;
        HybridCpuTopologyResourceModelV1 topologyModel = new HybridCpuTopologyResourceModelV1(topology);
        foreach (IrScheduleCycleGroup cycle in schedule.BlockSchedules.SelectMany(static block => block.CycleGroups))
        {
            HybridCpuTopologyCycleStateV1 state = HybridCpuTopologyCycleStateV1.Empty(topology);
            int certificateCount = 0;
            foreach (IrInstruction instruction in cycle.Instructions.OrderBy(static instruction => instruction.Index))
            {
                IrTopologyResourceFootprintV1 observed = IrTopologyResourceFootprintBuilderV1.Build(instruction, topology);
                ushort reads = RegisterGroupMask(instruction.Annotation.Uses, topology);
                ushort writes = RegisterGroupMask(instruction.Annotation.Defs, topology);
                IrTopologyResourceFootprintV1 footprint = observed with
                {
                    RegisterReadGroupMask = reads,
                    RegisterWriteGroupMask = writes,
                    RequiredPrfReadPorts = instruction.Annotation.Uses.Count(IsRegister),
                    RequiredPrfWritePorts = instruction.Annotation.Defs.Count(IsRegister),
                    RegisterPrecision = IrResourceFactPrecisionV1.Exact,
                    PrfPortPrecision = IrResourceFactPrecisionV1.Exact
                };
                if (!HasExactFiniteEvidence(footprint.Banks) || !HasExactFiniteEvidence(footprint.Channels))
                {
                    reason = $"Repaired cycle {cycle.Cycle} has Unknown/All bank or channel evidence.";
                    return false;
                }
                if (HasAddressOverlap(state.ReservedFootprints.Select(static item => item.Banks), footprint.Banks))
                {
                    reason = $"Repaired cycle {cycle.Cycle} violates compiler topology resource 'banks' (finite-set overlap).";
                    return false;
                }
                if (HasAddressOverlap(state.ReservedFootprints.Select(static item => item.Channels), footprint.Channels))
                {
                    reason = $"Repaired cycle {cycle.Cycle} violates compiler topology resource 'channels' (finite-set overlap).";
                    return false;
                }
                HybridCpuTopologyReservationResultV1 reservation = topologyModel.CanReserve(state, footprint);
                if (reservation.Decision != CompilerTopologyShadowDecisionV1.Allowed)
                {
                    string peers = string.Join(',', cycle.Instructions
                        .Where(peer => peer.Index != instruction.Index &&
                            state.ReservedFootprints.Any(item => item.InstructionIndex == peer.Index))
                        .Select(peer => $"{peer.Index}:{peer.Opcode}:{peer.StableIdentity}"));
                    reason = $"Repaired cycle {cycle.Cycle} instruction {instruction.Index}:{instruction.Opcode}:" +
                        $"{instruction.StableIdentity} with peers [{peers}] " +
                        $"violates compiler topology resource '{reservation.ResourceFamily}' ({reservation.Reason}).";
                    return false;
                }
                state = state.Append(footprint);
                if (footprint.CertificateClass != CompilerCertificateClassV1.None) certificateCount++;
            }
            if (certificateCount > resourceModel.StructuralCertificateCapacity)
            {
                reason = $"Repaired cycle {cycle.Cycle} exceeds the structural certificate capacity ({certificateCount}).";
                return false;
            }
        }
        reason = string.Empty;
        return true;

        static bool IsRegister(IrOperand operand) =>
            operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer or IrOperandKind.VirtualValue;

        static ushort RegisterGroupMask(IReadOnlyList<IrOperand> operands, HybridCpuMachineTopologyV1 topology)
        {
            ushort mask = 0;
            foreach (IrOperand operand in operands.Where(IsRegister))
            {
                if (operand.Value > HybridCpuMachineTopologyV1.MaximumRepresentableRegisterId) continue;
                mask |= checked((ushort)(1 << topology.GetRegisterGroup(checked((int)operand.Value))));
            }
            return mask;
        }

        static bool HasExactFiniteEvidence(IrAddressResourceEvidenceV1 evidence) =>
            evidence.Precision is IrAddressEvidenceKindV1.Exact or IrAddressEvidenceKindV1.FiniteSet;

        static bool HasAddressOverlap(
            IEnumerable<IrAddressResourceEvidenceV1> existing,
            IrAddressResourceEvidenceV1 candidate)
        {
            if (candidate.ResourceIds.Count == 0) return false;
            HashSet<int> ids = new HashSet<int>(candidate.ResourceIds);
            return existing.Any(item => item.ResourceIds.Any(ids.Contains));
        }
    }

    private static bool ValidateExactPlacement(IrProgramBundlingResult bundles) =>
        bundles.BlockResults.SelectMany(static block => block.Bundles).All(bundle =>
            bundle.Slots.Count == 8 && bundle.SlotAssignment.HasStructuralPlacement &&
            bundle.Slots.All(static slot => slot.IsStructuralPlacement));

    private static bool HasCompleteResourceModel(HybridCpuMiiResourceModelV1 model) =>
        model.Topology.PrfReadPortCapacity.HasValue && model.Topology.PrfWritePortCapacity.HasValue &&
        model.Topology.MemoryBankCount.HasValue && model.Topology.MemoryBankWidthBytes.HasValue &&
        model.Topology.MemoryChannelCount.HasValue && model.Topology.MemoryChannelWidthBytes.HasValue &&
        model.StructuralCertificateCapacity.HasValue;

    private static bool ValidateOptions(HybridCpuRegisterAllocationOptionsV1 options)
    {
        if (options.Budgets is null) return false;
        HybridCpuRegisterAllocationBudgetsV1 b = options.Budgets;
        if (b.MaximumInstructions <= 0 || b.MaximumValues <= 0 || b.MaximumAllocationCandidates <= 0 ||
            b.MaximumSpillAlternatives < 0 || b.MaximumRepairStages <= 0 || b.MaximumInsertedInstructions < 0)
            return false;
        return string.Equals(options.OptionsDigest,
            HybridCpuRegisterAllocationOptionsV1.Create(options.EnableAllocation, options.EnableSpills, b).OptionsDigest,
            StringComparison.Ordinal);
    }

    private static IrRegisterAllocationResultV1 PreserveExactPath(
        IrRegisterAllocationStatusV1 status,
        string reason,
        IrProgramSchedule schedule,
        IrProgramBundlingResult bundles,
        string inputScheduleDigest,
        string selectedPlansDigest,
        HybridCpuRegisterAllocationOptionsV1 options,
        HybridCpuMiiResourceModelV1 model) =>
        new(status, reason, schedule, bundles, schedule, bundles, null, [reason],
            HybridCpuRegisterAllocationContractV1.Hash(string.Join('|', "fallback", status, reason,
                inputScheduleDigest, selectedPlansDigest, options.OptionsDigest, model.ModelDigest)));

    private static IrRegisterAllocationStatusV1 ToAllocationStatus(HybridCpuPlatformFactStatus status) => status switch
    {
        HybridCpuPlatformFactStatus.Unknown => IrRegisterAllocationStatusV1.Unknown,
        HybridCpuPlatformFactStatus.Unsupported => IrRegisterAllocationStatusV1.Unsupported,
        HybridCpuPlatformFactStatus.Invalid => IrRegisterAllocationStatusV1.InvalidInput,
        _ => IrRegisterAllocationStatusV1.Allocated
    };

    private static int VirtualAccessOrdinal(IrInstruction instruction, string valueId, IrValueAccessKind kind)
    {
        IReadOnlyList<IrOperand> operands = kind == IrValueAccessKind.Def
            ? instruction.Annotation.Defs : instruction.Annotation.Uses;
        int ordinal = 0;
        foreach (IrOperand operand in operands.Where(static operand => operand.Kind == IrOperandKind.VirtualValue))
        {
            if (string.Equals(operand.Name, valueId, StringComparison.Ordinal)) return ordinal;
            ordinal++;
        }
        return int.MaxValue;
    }

    private static bool IsCall(IrInstruction instruction) =>
        instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call) ||
        instruction.Opcode == HybridCpuOpcode.JAL && instruction.Annotation.Defs.Any(operand =>
            operand.Kind == IrOperandKind.ArchitecturalRegister &&
            operand.Value == HybridCpuNativeAbiContractV2.ReturnAddressRegister);

    private static bool IsReturn(IrInstruction instruction) =>
        instruction.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Return) ||
        instruction.Opcode == HybridCpuOpcode.JALR && instruction.Annotation.Uses.Any(operand =>
            operand.Kind == IrOperandKind.ArchitecturalRegister &&
            operand.Value == HybridCpuNativeAbiContractV2.ReturnAddressRegister) &&
        instruction.Annotation.Defs.All(operand => operand.Kind != IrOperandKind.ArchitecturalRegister || operand.Value == 0);

    private static bool HasBalancedFrameExits(IrProgram program) =>
        program.BasicBlocks.Where(static block => block.SuccessorBlockIds.Count == 0)
            .All(static block => block.Instructions.Count != 0 &&
                (IsReturn(block.Instructions[^1]) || IsExceptionTransfer(block.Instructions[^1])));

    // An exact closed CFG has no observable method exit: its entry RA/callee-save state and
    // persistent spill frame never transfer back to a caller. Any actual terminal block keeps
    // the normal return/unwind checks fail-closed.
    private static bool HasTerminalPath(IrProgram program) =>
        program.BasicBlocks.Any(static block => block.SuccessorBlockIds.Count == 0);

    // This is a non-returning managed call, not a trap or an ordinary tailcall.
    // Its frame remains intact for the runtime unwinder; only return paths run epilogues.
    private static bool IsExceptionTransfer(IrInstruction instruction) =>
        (instruction.Opcode is HybridCpuOpcode.JAL or HybridCpuOpcode.JALR) && IsCall(instruction) &&
        instruction.Annotation.BranchTargetSymbolName is "__hybridcpu_managed_throw" or
            "__hybridcpu_managed_rethrow" or "__hybridcpu_managed_endfinally";

    private static bool Overlaps(ValueLifetime left, ValueLifetime right) =>
        Overlaps(left.Start, left.End, right.Start, right.End);

    private static bool Overlaps(int leftStart, int leftEnd, int rightStart, int rightEnd) =>
        leftStart < rightEnd && rightStart < leftEnd;

    private static int SizeBytes(IrVirtualValueV1 value) => checked((value.Allocation.WidthBits + 7) / 8);
    private static int AlignmentBytes(IrVirtualValueV1 value) => Math.Min(8, Math.Max(1, SizeBytes(value)));
    private static string SpillSlot(string valueId) => $"spill:{valueId}";

    private static HybridCpuOpcode MemoryOpcode(bool load, int size) => (load, size) switch
    {
        (true, 1) => HybridCpuOpcode.LB,
        (true, 2) => HybridCpuOpcode.LH,
        (true, 4) => HybridCpuOpcode.LW,
        (true, 8) => HybridCpuOpcode.LD,
        (false, 1) => HybridCpuOpcode.SB,
        (false, 2) => HybridCpuOpcode.SH,
        (false, 4) => HybridCpuOpcode.SW,
        (false, 8) => HybridCpuOpcode.SD,
        _ => throw new InvalidOperationException($"Unsupported spill width {size.ToString(CultureInfo.InvariantCulture)} bytes.")
    };

    private static HybridCpuDataType DataType(int size) => size switch
    {
        1 => HybridCpuDataType.UINT8,
        2 => HybridCpuDataType.UINT16,
        4 => HybridCpuDataType.UINT32,
        8 => HybridCpuDataType.UINT64,
        _ => throw new InvalidOperationException($"Unsupported spill width {size.ToString(CultureInfo.InvariantCulture)} bytes.")
    };

    private static IrSpillDecisionV1[] BuildSpillWitnesses(
        AllocationSubject subject,
        ColoringOutcome coloring,
        ScratchOutcome scratch) => coloring.SpilledValues
        .OrderBy(static value => value.Value.StableId, StringComparer.Ordinal)
        .Select(value => new IrSpillDecisionV1(
            value.Value.StableId,
            SpillSlot(value.Value.StableId),
            SizeBytes(value.Value),
            AlignmentBytes(value.Value),
            value.SpillCost,
            value.Accesses.OrderBy(static access => access.InstructionIndex).ThenBy(static access => access.Kind)
                .Select(access => new IrSpillAccessV1(access.InstructionIndex, access.Kind,
                    scratch.Registers[(value.Value.StableId, access.InstructionIndex)],
                    $"phase20:{(access.Kind == IrValueAccessKind.Def ? "spill" : "reload")}:{value.Value.StableId}:{access.InstructionIndex.ToString(CultureInfo.InvariantCulture)}"))
                .ToArray())).ToArray();

    private static string DigestSelectedPlans(IrSelectedAllocationPlansV1 plans) =>
        HybridCpuRegisterAllocationContractV1.Hash(string.Join('|', "selected-plans/v1",
            string.Join(',', plans.RegionPlanDigests.Order(StringComparer.Ordinal)),
            string.Join(',', plans.LoopOrModuloPlanDigests.Order(StringComparer.Ordinal)),
            string.Join(',', plans.VirtualThreadPlanDigests.Order(StringComparer.Ordinal)),
            string.Join(',', plans.FspOrPrefetchPlanDigests.Order(StringComparer.Ordinal))));

    private static string DigestSchedule(IrProgramSchedule schedule, IrProgramBundlingResult bundles)
    {
        string basis = $"{DigestScheduleOnly(schedule)}|{DigestPlacement(bundles)}";
        IrInstruction[] fixedAccesses = schedule.Program.Instructions.Where(instruction => instruction.Annotation.FixedFrameSlotIdentity is not null)
            .OrderBy(instruction => instruction.Index).ToArray();
        if (fixedAccesses.Length != 0)
            basis += "|fixed-frame-access/v1|" + string.Join(';', fixedAccesses.Select(instruction =>
                $"{instruction.Index}:{instruction.StableIdentity}:{instruction.Annotation.FixedFrameSlotIdentity}"));
        return HybridCpuRegisterAllocationContractV1.Hash(basis);
    }

    private static string DigestScheduleOnly(IrProgramSchedule schedule) =>
        HybridCpuRegisterAllocationContractV1.Hash(string.Join('|', "schedule/v1",
            schedule.Program.Contract.DerivedFacts.ProgramMutation.Value,
            string.Join(';', schedule.BlockSchedules.OrderBy(static block => block.BlockId)
                .SelectMany(block => block.ScheduledInstructions.OrderBy(static instruction => instruction.InstructionIndex)
                    .Select(instruction => $"{block.BlockId}:{instruction.InstructionIndex}:{instruction.Cycle}:{instruction.OrderInCycle}")))));

    private static string DigestPlacement(IrProgramBundlingResult bundles) =>
        HybridCpuRegisterAllocationContractV1.Hash(string.Join('|', "placement/v1",
            string.Join(';', bundles.BlockResults.OrderBy(static block => block.BlockId)
                .SelectMany(block => block.Bundles.OrderBy(static bundle => bundle.Cycle)
                    .Select(bundle => $"{block.BlockId}:{bundle.Cycle}:{string.Join(',', bundle.Slots.Select(slot => slot.Instruction?.Index ?? -1))}")))));

    private static string DigestDependencies(IrProgramDependencyGraph graph)
    {
        // This is byte-for-byte the previous UTF-8 string.Join('|', header,
        // string.Join(';', intra), string.Join(';', inter)) input, streamed so a
        // dense but bounded graph cannot require a second multi-gigabyte string.
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("dependencies/v1|");
        bool first = true;
        foreach (IrBasicBlockDependencyGraph block in graph.BlockGraphs.OrderBy(static block => block.BlockId))
        {
            foreach (IrInstructionDependency dependency in block.Dependencies)
            {
                if (!first) Append(";");
                first = false;
                Append($"{block.BlockId}:{dependency.ProducerInstructionIndex}:{dependency.ConsumerInstructionIndex}:{dependency.Kind}:{dependency.MinimumLatencyCycles}:{dependency.RelatedOperandKind}:{dependency.RelatedOperandValue}");
            }
        }
        Append("|");
        first = true;
        foreach (IrInterBlockDependency dependency in graph.InterBlockGraph.Dependencies)
        {
            if (!first) Append(";");
            first = false;
            Append($"{dependency.SourceBlockId}:{dependency.TargetBlockId}:{dependency.Dependency.ProducerInstructionIndex}:{dependency.Dependency.ConsumerInstructionIndex}:{dependency.Dependency.Kind}");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    private static string DigestWitness(
        HybridCpuRegisterAllocationOptionsV1 options,
        HybridCpuMiiResourceModelV1 model,
        string inputScheduleDigest,
        string selectedPlansDigest,
        IReadOnlyList<IrRegisterAssignmentV1> assignments,
        IReadOnlyList<IrSpillDecisionV1> spills,
        IReadOnlyList<IrAllocatedSemanticValueV1> semanticValues,
        HybridCpuFrameLayoutV2 frame,
        IReadOnlyList<IrAllocationMutationRecordV1> mutations,
        IrAllocationProofRebuildV1 rebuild) =>
        HybridCpuRegisterAllocationContractV1.Hash(string.Join('|',
            HybridCpuRegisterAllocationContractV1.SchemaId,
            HybridCpuRegisterAllocationContractV1.Default.ContractDigest,
            options.OptionsDigest, model.ModelDigest, inputScheduleDigest, selectedPlansDigest,
            string.Join(';', assignments.Select(static assignment =>
                $"{assignment.ValueId}:{assignment.RegisterId}:{assignment.ScheduledStart}:{assignment.ScheduledEndExclusive}:{assignment.LiveAcrossCall}")),
            string.Join(';', spills.Select(spill => $"{spill.ValueId}:{spill.FrameSlotIdentity}:{spill.SpillCost}:" +
                string.Join(',', spill.Accesses.Select(static access => $"{access.OriginalInstructionIndex}:{access.AccessKind}:{access.ScratchRegisterId}")))),
            string.Join(';', semanticValues.Select(static value =>
                $"{value.ValueId}:{value.ValueKind.Kind}:{value.ValueKind.BitWidth}:{value.ValueKind.IsSigned}:{value.ValueKind.LaneCount}:{value.VirtualClass}")),
            frame.Digest,
            string.Join(';', mutations.Select(static mutation => $"{mutation.Kind}:{mutation.Identity}:{mutation.FinalInstructionIndex}")),
            rebuild.DependencyDigest, rebuild.ValueFlowDigest, rebuild.ScheduleDigest, rebuild.PlacementDigest,
            string.Join(';', rebuild.Loops.Select(static loop =>
                $"{loop.LoopId}:{loop.CanonicalStatus}:{loop.MiiEligibility}:{loop.ProvenLowerBoundIi}:{loop.DistanceDagDigest}:{loop.MiiProofDigest}"))));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record ValueLifetime(
        IrVirtualValueV1 Value,
        IReadOnlyList<IrValueAccessV1> Accesses,
        int Start,
        int End,
        int[] Candidates,
        bool IsPrecolored,
        bool CanSpill,
        bool LiveAcrossCall,
        long SpillCost);

    private sealed record AllocationSubject(
        bool IsSupported,
        IrRegisterAllocationStatusV1 Status,
        string Reason,
        IReadOnlyList<ValueLifetime> Lifetimes,
        IReadOnlyDictionary<int, int> Positions,
        IReadOnlyDictionary<int, IrInstruction> Instructions,
        IReadOnlyDictionary<int, (int Start, int End)> BlockPositions)
    {
        public static AllocationSubject Failure(IrRegisterAllocationStatusV1 status, string reason) =>
            new(false, status, reason, Array.Empty<ValueLifetime>(),
                new Dictionary<int, int>(), new Dictionary<int, IrInstruction>(),
                new Dictionary<int, (int Start, int End)>());
    }

    private sealed record ColoringOutcome(
        IrRegisterAllocationStatusV1 Status,
        string Reason,
        IReadOnlyDictionary<string, IrRegisterAssignmentV1> Assignments,
        IReadOnlyList<ValueLifetime> SpilledValues)
    {
        public static ColoringOutcome Failure(IrRegisterAllocationStatusV1 status, string reason) =>
            new(status, reason, new Dictionary<string, IrRegisterAssignmentV1>(), Array.Empty<ValueLifetime>());
    }

    private sealed record ScratchOutcome(
        IrRegisterAllocationStatusV1 Status,
        string Reason,
        IReadOnlyDictionary<(string ValueId, int InstructionIndex), int> Registers)
    {
        public static ScratchOutcome Failure(IrRegisterAllocationStatusV1 status, string reason) =>
            new(status, reason, new Dictionary<(string ValueId, int InstructionIndex), int>());
    }

    private sealed record PendingInstruction(
        int BlockId,
        IrInstruction Instruction,
        IrAllocationMutationKindV1 Kind,
        string Identity,
        string? ResponsibleValueId,
        int? OriginalInstructionIndex);

    private sealed record LoweringOutcome(
        IrRegisterAllocationStatusV1 Status,
        string Reason,
        IrProgram? Program,
        IReadOnlyList<IrAllocationMutationRecordV1> Mutations,
        IReadOnlyDictionary<int, long> TopologyOrderRanks)
    {
        public static LoweringOutcome Failure(IrRegisterAllocationStatusV1 status, string reason) =>
            new(status, reason, null, Array.Empty<IrAllocationMutationRecordV1>(),
                new Dictionary<int, long>());
    }

    private sealed record RebuildOutcome(
        IrRegisterAllocationStatusV1 Status,
        string Reason,
        IrProgramSchedule? Schedule,
        IrProgramBundlingResult? Bundles,
        IrAllocationProofRebuildV1? Proof)
    {
        public static RebuildOutcome Failure(IrRegisterAllocationStatusV1 status, string reason) =>
            new(status, reason, null, null, null);
    }
}
