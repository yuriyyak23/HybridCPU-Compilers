using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

/// <summary>Synchronous managed ingress with constructor-owned binding-table membership.</summary>
/// <remarks>
/// Binding rows and their nested payloads remain caller-owned and must stay stable.
/// PE/reference inputs must remain stable for each call. Sequential reuse is supported;
/// callers must serialize access to one instance. Cancellation is call-local and observed
/// at safe boundaries; returned diagnostics/results do not confer runtime authority.
/// </remarks>
public sealed partial class RestrictedCilImporterV1
{
    private readonly RestrictedCilImportBudgetsV1 _budgets;
    private readonly RestrictedCilSupportMatrixV1 _matrix;
    private readonly RestrictedCilImportModeV1 _mode;
    private readonly IReadOnlyDictionary<int, RestrictedCilManagedCallTargetV1> _managedCallTargets;
    private readonly ScalarControlFlowV2Budgets _graphBudgets;
    private readonly IReadOnlyDictionary<string, RestrictedCilFieldLayoutBindingV1> _fieldLayouts;
    private readonly IReadOnlyDictionary<int, RestrictedCilAllocationBindingV1> _allocationBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilArrayTypeBindingV1> _arrayBindings;
    private readonly IReadOnlyDictionary<string, RestrictedCilStringLiteralBindingV1> _stringBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilFieldDataBindingV1> _fieldDataBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilValueTypeBindingV1> _valueTypeBindings;
    private readonly IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1> _typeInitializationBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilDispatchBindingV1> _dispatchBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilTypeTestBindingV1> _typeTestBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilFunctionPointerBindingV1> _functionPointerBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilCalliBindingV1> _calliBindings;
    private readonly IReadOnlyDictionary<(int MethodToken, int ConstructorToken, int Offset),
        RestrictedCilDelegateCreationBindingV1> _delegateCreationBindings;
    private readonly IReadOnlyDictionary<int, RestrictedCilDelegateInvokeBindingV1> _delegateInvokeBindings;
    private readonly IReadOnlyDictionary<string, RestrictedCilRuntimeExternalBindingV1> _runtimeExternalBindings;

    public RestrictedCilImporterV1(
        RestrictedCilImportBudgetsV1? budgets = null,
        RestrictedCilSupportMatrixV1? matrix = null,
        RestrictedCilImportModeV1 mode = RestrictedCilImportModeV1.RestrictedScalarV1,
        IReadOnlyDictionary<int, RestrictedCilManagedCallTargetV1>? managedCallTargets = null,
        ScalarControlFlowV2Budgets? graphBudgets = null,
        IReadOnlyList<RestrictedCilFieldLayoutBindingV1>? fieldLayouts = null,
        IReadOnlyList<RestrictedCilAllocationBindingV1>? allocationBindings = null,
        IReadOnlyList<RestrictedCilArrayTypeBindingV1>? arrayBindings = null,
        IReadOnlyList<RestrictedCilStringLiteralBindingV1>? stringBindings = null,
        IReadOnlyList<RestrictedCilValueTypeBindingV1>? valueTypeBindings = null,
        IReadOnlyList<RestrictedCilTypeInitializationBindingV1>? typeInitializationBindings = null,
        IReadOnlyList<RestrictedCilDispatchBindingV1>? dispatchBindings = null,
        IReadOnlyList<RestrictedCilTypeTestBindingV1>? typeTestBindings = null,
        IReadOnlyList<RestrictedCilFunctionPointerBindingV1>? functionPointerBindings = null,
        IReadOnlyList<RestrictedCilCalliBindingV1>? calliBindings = null,
        IReadOnlyList<RestrictedCilDelegateCreationBindingV1>? delegateCreationBindings = null,
        IReadOnlyList<RestrictedCilDelegateInvokeBindingV1>? delegateInvokeBindings = null,
        IReadOnlyList<RestrictedCilFieldDataBindingV1>? fieldDataBindings = null,
        IReadOnlyList<RestrictedCilRuntimeExternalBindingV1>? runtimeExternalBindings = null)
    {
        _budgets = budgets ?? (mode == RestrictedCilImportModeV1.ScalarControlFlowV2
            ? CreateScalarControlFlowV2Budgets()
            : RestrictedCilImportBudgetsV1.Production);
        _matrix = matrix ?? RestrictedCilSupportMatrixV1.Default;
        _mode = mode;
        _managedCallTargets = managedCallTargets?.ToDictionary(static row => row.Key, static row => row.Value)
            ?? new Dictionary<int, RestrictedCilManagedCallTargetV1>();
        _graphBudgets = graphBudgets ?? ScalarControlFlowV2ProfileContractV1.Default.Budgets;
        _fieldLayouts = (fieldLayouts ?? []).ToDictionary(
            static row => $"{row.DeclaringType}::{row.FieldName}", StringComparer.Ordinal);
        _allocationBindings = (allocationBindings ?? []).ToDictionary(
            static row => row.ConstructorMetadataToken);
        _arrayBindings = (arrayBindings ?? []).ToDictionary(static row => row.ElementTypeMetadataToken);
        _stringBindings = (stringBindings ?? []).ToDictionary(static row => row.Literal, StringComparer.Ordinal);
        _fieldDataBindings = (fieldDataBindings ?? []).ToDictionary(static row => row.FieldMetadataToken);
        _valueTypeBindings = (valueTypeBindings ?? []).ToDictionary(static row => row.TypeMetadataToken);
        _typeInitializationBindings = (typeInitializationBindings ?? []).ToDictionary(static row => row.DeclaringType, StringComparer.Ordinal);
        _dispatchBindings = (dispatchBindings ?? []).ToDictionary(static row => row.MetadataToken);
        _typeTestBindings = (typeTestBindings ?? []).ToDictionary(static row => row.TypeMetadataToken);
        _functionPointerBindings = (functionPointerBindings ?? []).ToDictionary(static row => row.MethodMetadataToken);
        _calliBindings = (calliBindings ?? []).ToDictionary(static row => row.SignatureMetadataToken);
        _delegateCreationBindings = (delegateCreationBindings ?? []).ToDictionary(static row =>
            (row.ContainingMethodMetadataToken, row.ConstructorMetadataToken, row.CilOffset));
        _delegateInvokeBindings = (delegateInvokeBindings ?? []).ToDictionary(static row => row.InvokeMetadataToken);
        _runtimeExternalBindings = (runtimeExternalBindings ?? []).ToDictionary(static row => row.StableIdentity, StringComparer.Ordinal);
    }

    private static RestrictedCilImportBudgetsV1 CreateScalarControlFlowV2Budgets()
    {
        ScalarControlFlowV2Budgets source = ScalarControlFlowV2ProfileContractV1.Default.Budgets;
        return new(source.MaximumPeBytes, source.MaximumMethodBodyBytes, source.MaximumIlInstructionsPerMethod,
            source.MaximumBasicBlocksPerMethod, source.MaximumArgumentsPerMethod, source.MaximumLocalsPerMethod,
            source.MaximumEvaluationStack, source.MaximumOutgoingCallsPerMethod);
    }

    public RestrictedCilImportResultV1 ImportFile(string path, RestrictedCilMethodSelectorV1 selector)
        => ImportFile(path, selector, CancellationToken.None);

    public RestrictedCilImportResultV1 ImportFile(string path, RestrictedCilMethodSelectorV1 selector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || selector is null)
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0001", "A PE path and method selector are required.");
        if (!_budgets.IsValid)
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0002", "CIL import budgets must be positive and deterministic.");
        try
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0003", "The input PE file does not exist.", selector.MethodName);
            if (info.Length > _budgets.MaximumPeBytes)
                return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2001", "The input PE exceeds the configured byte budget.", selector.MethodName);
            cancellationToken.ThrowIfCancellationRequested();
            return ImportImage(File.ReadAllBytes(path), selector, cancellationToken, Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0003", "The input PE file could not be read.", selector.MethodName);
        }
    }

    public RestrictedCilImportResultV1 ImportImage(
        ReadOnlyMemory<byte> peImage,
        RestrictedCilMethodSelectorV1 selector,
        string sourceIdentity = "cil-image",
        ManagedEhMethodPlanV1? managedEhPlan = null)
        => ImportImage(peImage, selector, CancellationToken.None, sourceIdentity, managedEhPlan);

    /// <summary>
    /// Cancellation is observed at ingress/stage boundaries and before returning success.
    /// Inputs/bindings must remain stable for the synchronous call; cancellation publishes no result.
    /// </summary>
    public RestrictedCilImportResultV1 ImportImage(
        ReadOnlyMemory<byte> peImage,
        RestrictedCilMethodSelectorV1 selector,
        CancellationToken cancellationToken,
        string sourceIdentity = "cil-image",
        ManagedEhMethodPlanV1? managedEhPlan = null)
        => ImportImageCore(peImage, selector, cancellationToken, sourceIdentity, managedEhPlan, null);

    // Borrowed reader is owned by the synchronous BodyWorld module context; never retained or disposed here.
    private RestrictedCilImportResultV1 ImportImageCore(
        ReadOnlyMemory<byte> peImage, RestrictedCilMethodSelectorV1 selector,
        CancellationToken cancellationToken, string sourceIdentity,
        ManagedEhMethodPlanV1? managedEhPlan, ManagedModuleContext? module)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selector is null || string.IsNullOrWhiteSpace(selector.TypeName) || string.IsNullOrWhiteSpace(selector.MethodName) ||
            string.IsNullOrWhiteSpace(sourceIdentity))
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0001", "A stable source identity and complete method selector are required.");
        if (!_budgets.IsValid)
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0002", "CIL import budgets must be positive and deterministic.");
        if (peImage.Length == 0)
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0004", "The input PE image is empty.", selector.MethodName);
        if (peImage.Length > _budgets.MaximumPeBytes)
            return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2001", "The input PE exceeds the configured byte budget.", selector.MethodName);

        string peDigest = module?.ImageDigest ?? Convert.ToHexString(SHA256.HashData(peImage.Span)).ToLowerInvariant();
        string optionsDigest = WithExternalScalarOptions(WithExternalEnumOptions(RestrictedCilSupportMatrixV1.OptionsDigest(_budgets)));
        if (_nullableInt32ParameterIngress)
            optionsDigest = HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Hash(
                HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Serialize(new {
Schema = "hybridcpu.cil-private-nullable-i4-ingress/v6", Original = optionsDigest }));
        if (_nullableInt32ReturnIngress || _nullableInt32ReturnCalls)
            optionsDigest = HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Hash(
                HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Serialize(new {
                    Schema = "hybridcpu.cil-private-nullable-i4-return/v1", Producer = _nullableInt32ReturnIngress,
                    Calls = _nullableInt32ReturnCalls, Original = optionsDigest }));
        try
        {
            using MemoryStream? stream = module is null ? new MemoryStream(peImage.ToArray(), writable: false) : null;
            using PEReader? ownedReader = module is null ? new PEReader(stream!, PEStreamOptions.LeaveOpen) : null;
            PEReader pe = module?.Reader ?? ownedReader!;
            if (!pe.HasMetadata)
                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0005", "The input is not a managed PE image.", selector.MethodName);
            MetadataReader metadata = module?.Metadata ?? pe.GetMetadataReader();
            cancellationToken.ThrowIfCancellationRequested();
            MethodSelection selection = SelectMethod(metadata, selector);
            if (selection.Failure is not null) return selection.Failure;
            MethodDefinition method = metadata.GetMethodDefinition(selection.Method);
            string methodToken = $"0x{MetadataTokens.GetToken(selection.Method):x8}";
            IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments = selector.GenericTypeArguments ?? [];
            IReadOnlyList<RestrictedCilGenericArgumentV1> methodArguments = selector.GenericMethodArguments ?? [];
            MethodSignature signature = ParseMethodSignature(metadata, method.Signature, typeArguments, methodArguments,
                allowAggregates: _mode == RestrictedCilImportModeV1.ScalarControlFlowV2);
            signature = BindValueReceiver(metadata, selection.Type, signature);
            signature = ProjectScalarValueSignature(metadata, signature);
            ManagedMethodIdentityV1 managedIdentity = CreateManagedMethodIdentity(metadata, selection.Type, selection.Method,
                signature, typeArguments, methodArguments);
            string methodIdentity = managedIdentity.StableIdentity;
            string methodLocalIdentity = $"{FullTypeName(metadata, selection.Type)}.{metadata.GetString(method.Name)}";
            RestrictedCilProvenanceV1 provenance = new RestrictedCilProvenanceV1(sourceIdentity, peDigest, methodIdentity, methodToken,
                _matrix.ContractDigest, optionsDigest, methodLocalIdentity);
            IReadOnlyList<string> sourceProfiles = ManagedProfileClosureContractV1.ReadMarkers(
                metadata, selection.Type, selection.Method);
            RestrictedCilImportResultV1 StampProfiles(RestrictedCilImportResultV1 result)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return result.Status == RestrictedCilImportStatusV1.Success
                    ? result with { SourceProfileMarkers = sourceProfiles }
                    : result;
            }

            RestrictedCilImportResultV1? headerFailure = ValidateMethodEnvelope(metadata, selection.Type, method, provenance,
                typeArguments, methodArguments);
            if (headerFailure is not null) return headerFailure;
            if (signature.Status != SignatureStatus.Success)
                return Reject(signature.Status == SignatureStatus.Invalid
                        ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.Unsupported,
                    signature.Status == SignatureStatus.Invalid ? "HCCIL0006" : "HCCIL1010",
                    signature.Message, methodIdentity, provenance);
            if (signature.AggregateReturn == NullableInt32Identity && !_nullableInt32ReturnIngress)
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1010",
                    "Managed references, floating point, aggregates and extended signature types are not qualified.", methodIdentity, provenance);
            if (signature.Parameters.Count > _budgets.MaximumArguments)
                return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2002", "The method exceeds the argument budget.", methodIdentity, provenance);
            if (signature.Parameters.Count > HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Count)
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1002",
                    "The method exceeds the exact native register-argument ABI x10..x17.", methodIdentity, provenance);

            MethodBodyBlock body;
            try { body = pe.GetMethodBody(method.RelativeVirtualAddress); }
            catch (BadImageFormatException)
            {
                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0007", "The method body is malformed.", methodIdentity, provenance);
            }
            byte[] ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
            if (ilBytes.Length > _budgets.MaximumMethodBodyBytes)
                return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2003", "The method body exceeds the byte budget.", methodIdentity, provenance);
            if (signature.AggregateReturn == NullableInt32Identity &&
                !IsExactNullableInt32Return(metadata, pe, selection.Method, signature, typeArguments, methodArguments))
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1890",
                    "Nullable<int> return requires an exact private primary non-root non-EH scalar/nullable-input producer.", methodIdentity, provenance);
            if (signature.AggregateParameters?.Contains(NullableInt32Identity) == true) {
                RestrictedCilImportResultV1? qualificationFailure = null;
                bool qualified = _nullableInt32ParameterIngress &&
                    (_nullableInt32ReturnIngress && signature.AggregateReturn == NullableInt32Identity ||
                     IsExactNullableInt32ParameterGetter(metadata, pe, selection.Method, signature, provenance, cancellationToken, out qualificationFailure));
                if (qualificationFailure is not null) return qualificationFailure;
                if (!qualified) return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1847",
                    "Nullable<int> parameter ingress requires a primary private pure getter helper, not a root or general nullable ABI.", methodIdentity, provenance);
            }
            if (body.ExceptionRegions.Length != 0)
            {
                if (managedEhPlan is null)
                    return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1011",
                        "Exception handling regions require an exact graph-owned managed EH plan.", methodIdentity, provenance);
                if (!string.Equals(managedEhPlan.MethodIdentity, methodIdentity, StringComparison.Ordinal) ||
                    managedEhPlan.MethodBodySize != ilBytes.Length ||
                    managedEhPlan.Clauses.Count != body.ExceptionRegions.Length)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0810",
                        "The graph-owned managed EH plan does not match the selected method body.", methodIdentity, provenance);
                // A matching method name/size/clause count does not bind handler ranges,
                // operations, state homes or GC types. Re-derive from the exact PE; never
                // let caller-provided analysis become a handler-entry lowering input.
                ManagedEhImportResultV1 exactEh = ImportManagedEhPlanCore(peImage, selector, CancellationToken.None, module);
                if (exactEh.Status != ManagedEhImportStatusV1.Success || exactEh.Plan is null ||
                    !string.Equals(managedEhPlan.ContractDigest, exactEh.Plan.ContractDigest, StringComparison.Ordinal) ||
                    !string.Equals(ManagedEhPlanContractV1.ComputeDigest(managedEhPlan.MethodIdentity,
                        managedEhPlan.InstructionIdentityPrefix, managedEhPlan.MethodBodySize,
                        managedEhPlan.Clauses, managedEhPlan.Operations), exactEh.Plan.ContractDigest, StringComparison.Ordinal))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0810",
                        "The graph-owned managed EH plan is not bound to the exact PE clauses and operations.", methodIdentity, provenance);
                LocalSignature ehLocals = ProjectScalarValueLocals(metadata,
                    ParseLocalSignature(metadata, body.LocalSignature, typeArguments, methodArguments, allowAggregates: true));
                DecodeResult ehDecoded = Decode(ilBytes, provenance, metadata, pe, false);
                if (ehDecoded.Failure is not null) return ehDecoded.Failure;
                RestrictedCilImportResultV1? ehStateFailure = VerifyEhDataflow(metadata, ehDecoded.Instructions,
                    signature, ehLocals, body.MaxStack, body.LocalVariablesInitialized, exactEh.Plan, provenance, out ManagedEhTypedDataflowV1? ehTypedDataflow);
                if (ehStateFailure is not null) return ehStateFailure;
                RestrictedCilImportResultV1 ehMapped = ImportControlFlowV2(metadata, ehDecoded.Instructions,
                    signature, ehLocals, body.MaxStack, provenance, exactEh.Plan, ehTypedDataflow);
                if (ehMapped.Status != RestrictedCilImportStatusV1.Success)
                    return ehMapped with { ManagedEhAnalysis = exactEh.Plan, ManagedEhTypedDataflow = ehTypedDataflow };
                ManagedEhHomeAccessPlanV1 accessPlan = ManagedEhFrameHomesV1.CreateAccessPlan(exactEh.Plan, ehTypedDataflow!);
                IrInstruction[] fixedAccesses = ehMapped.Program!.Instructions
                    .Where(static instruction => instruction.Annotation.FixedFrameSlotIdentity is not null).ToArray();
                int catchCopies = ehMapped.Program.Instructions.Count(static instruction =>
                    instruction.StableIdentity.StartsWith("eh:handler:il_", StringComparison.Ordinal) &&
                    instruction.StableIdentity.EndsWith(":exception-copy", StringComparison.Ordinal));
                int leaveTransfers = ehMapped.Program.Instructions.Count(instruction => exactEh.Plan.Operations.Any(operation =>
                    operation.Kind == ManagedEhOperationKindV1.Leave &&
                    (instruction.StableIdentity == $"{exactEh.Plan.InstructionIdentityPrefix}{operation.IlOffset:x4}" ||
                     instruction.StableIdentity == $"{exactEh.Plan.InstructionIdentityPrefix}{operation.IlOffset:x4}:enter-finally")));
                int rethrowTransfers = ehMapped.Program.Instructions.Count(static instruction =>
                    instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_rethrow");
                int throwTransfers = ehMapped.Program.Instructions.Count(static instruction =>
                    instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_throw");
                int endFinallyTransfers = ehMapped.Program.Instructions.Count(static instruction =>
                    instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_endfinally");
                HashSet<int> reachableEhOffsets = ehTypedDataflow!.Entries.Select(static entry => entry.IlOffset).ToHashSet();
                int expectedCatchCopies = exactEh.Plan.HandlerEntries.Count(static entry => entry.ExceptionReferenceRegister is not null);
                int expectedLeaves = exactEh.Plan.LeaveTransfers.Count(transfer => reachableEhOffsets.Contains(transfer.IlOffset));
                int expectedRethrows = exactEh.Plan.Operations.Count(operation => operation.Kind == ManagedEhOperationKindV1.Rethrow &&
                    reachableEhOffsets.Contains(operation.IlOffset));
                int expectedThrows = exactEh.Plan.Operations.Count(operation => operation.Kind == ManagedEhOperationKindV1.Throw &&
                    reachableEhOffsets.Contains(operation.IlOffset));
                int expectedEndFinally = exactEh.Plan.Operations.Count(operation => operation.Kind == ManagedEhOperationKindV1.EndFinally &&
                    reachableEhOffsets.Contains(operation.IlOffset));
                // Continuation-token accesses are separately owned by the exact finally plan,
                // not scalar state-home accesses. Validate both inventories without conflating them.
                Dictionary<string, HybridCpuOpcode> expectedTokens = new Dictionary<string, HybridCpuOpcode>(StringComparer.Ordinal);
                if (exactEh.Plan.Clauses.Any(static clause => clause.Kind == HybridCpuManagedEhClauseKindV1.Finally))
                {
                    int entryOffset = exactEh.Plan.ControlFlow!.InstructionOffsets[0];
                    expectedTokens.Add($"{exactEh.Plan.InstructionIdentityPrefix}{entryOffset:x4}:finally-token-init", HybridCpuOpcode.SD);
                    foreach (ManagedEhFinallyContinuationV1? continuation in exactEh.Plan.FinallyContinuations.Continuations.Where(row => reachableEhOffsets.Contains(row.LeaveOffset)))
                        expectedTokens.Add($"{exactEh.Plan.InstructionIdentityPrefix}{continuation.LeaveOffset:x4}:finally-token-store", HybridCpuOpcode.SD);
                    foreach (ManagedEhOperationPlanV1? operation in exactEh.Plan.Operations.Where(row => row.Kind == ManagedEhOperationKindV1.EndFinally && reachableEhOffsets.Contains(row.IlOffset)))
                        expectedTokens.Add($"{exactEh.Plan.InstructionIdentityPrefix}{operation.IlOffset:x4}:finally-token-load", HybridCpuOpcode.LD);
                }
                IrInstruction[] tokenAccesses = fixedAccesses.Where(static instruction => instruction.Annotation.FixedFrameSlotIdentity == ManagedEhFrameHomesV1.FinallyContinuationTokenSlot).ToArray();
                bool tokensExact = tokenAccesses.Length == expectedTokens.Count &&
                    tokenAccesses.Select(static instruction => instruction.StableIdentity).Distinct(StringComparer.Ordinal).Count() == expectedTokens.Count &&
                    tokenAccesses.All(instruction => expectedTokens.TryGetValue(instruction.StableIdentity, out HybridCpuOpcode opcode) && instruction.Opcode == opcode);
                int stateHomeAccesses = fixedAccesses.Length - tokenAccesses.Length;
                if (!tokensExact || stateHomeAccesses != accessPlan.Accesses.Count ||
                    fixedAccesses.Any(static instruction => instruction.Opcode is not (HybridCpuOpcode.LD or HybridCpuOpcode.SD)) ||
                    catchCopies != expectedCatchCopies || leaveTransfers != expectedLeaves ||
                    throwTransfers != expectedThrows || rethrowTransfers != expectedRethrows ||
                    endFinallyTransfers != expectedEndFinally)
                    return Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL2812",
                        $"EH mapper did not retain every exact operation: homes {fixedAccesses.Length}/{accessPlan.Accesses.Count}, " +
                        $"catches {catchCopies}/{expectedCatchCopies}, leaves {leaveTransfers}/{expectedLeaves}, " +
                        $"throws {throwTransfers}/{expectedThrows}, rethrows {rethrowTransfers}/{expectedRethrows}, " +
                        $"endfinally {endFinallyTransfers}/{expectedEndFinally}.",
                        methodIdentity, provenance) with { ManagedEhAnalysis = exactEh.Plan, ManagedEhTypedDataflow = ehTypedDataflow };
                ManagedEhLoweringEvidenceV1 loweringEvidence = new ManagedEhLoweringEvidenceV1(fixedAccesses.Length, catchCopies,
                    leaveTransfers, throwTransfers, rethrowTransfers, endFinallyTransfers,
                    HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.Hash(string.Join('|',
                        "managed-eh-lowering-evidence/v2", exactEh.Plan.ContractDigest, accessPlan.Digest,
                        string.Join(';', fixedAccesses.Select(static instruction =>
                            $"{instruction.StableIdentity}:{instruction.Annotation.FixedFrameSlotIdentity}:{instruction.Opcode}")),
                        catchCopies, leaveTransfers, throwTransfers, rethrowTransfers, endFinallyTransfers)));
                return StampProfiles(ehMapped with
                {
                    ManagedEhAnalysis = exactEh.Plan,
                    ManagedEhTypedDataflow = ehTypedDataflow,
                    ManagedEhLoweringEvidence = loweringEvidence,
                    RequiresNativeExceptionTransfer = true
                });
            }
            if (managedEhPlan is not null)
                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0811",
                    "A managed EH plan was supplied for a method without exception regions.", methodIdentity, provenance);
            LocalSignature locals = ParseLocalSignature(metadata, body.LocalSignature, typeArguments, methodArguments,
                allowAggregates: _mode == RestrictedCilImportModeV1.ScalarControlFlowV2, enumDeclarations: _externalEnumCarriers);
            if (locals.Status != SignatureStatus.Success)
                return Reject(locals.Status == SignatureStatus.Invalid
                        ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.Unsupported,
                    locals.Status == SignatureStatus.Invalid ? "HCCIL0029" : "HCCIL1003",
                    locals.Message, methodIdentity, provenance);
            if (locals.Types.Count > _budgets.MaximumLocals)
                return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2008", "The local-variable budget was exhausted.", methodIdentity, provenance);

            DecodeResult decoded = Decode(ilBytes, provenance, metadata, pe, body.ExceptionRegions.Length == 0, signature, projectionLocals: locals);
            cancellationToken.ThrowIfCancellationRequested();
            if (decoded.Failure is not null) return decoded.Failure;
            if (signature.AggregateReturn == NullableInt32Identity && !IsPureNullableInt32ReturnBody(decoded.Instructions, metadata, pe, signature, locals, provenance, cancellationToken))
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1890",
                    "Nullable<int> producer requires pure scalar/nullable construction and forward or exactly proven counted control flow.", methodIdentity, provenance);
            if (_mode == RestrictedCilImportModeV1.ScalarControlFlowV2 && body.ExceptionRegions.Length == 0)
            {
                decoded = ProjectParameterScalarReads(decoded, signature, provenance, cancellationToken);
                if (decoded.Failure is not null) return decoded.Failure;
            }
            if (_mode == RestrictedCilImportModeV1.ScalarControlFlowV2 && body.ExceptionRegions.Length == 0)
            {
                decoded = decoded with { Instructions = ProjectLocalScalarReads(metadata, pe, decoded.Instructions, locals, signature, provenance, cancellationToken, out RestrictedCilImportResultV1? projectionFailure) };
                if (projectionFailure is not null) return projectionFailure;
            }
            if (_mode == RestrictedCilImportModeV1.ScalarControlFlowV2)
                locals = ProjectScalarValueLocals(metadata, locals, decoded.Instructions);
            foreach (DecodedInstruction instruction in decoded.Instructions.Where(static row => row.Encoding == 0xd0))
            {
                if (!_fieldDataBindings.TryGetValue(instruction.Token, out RestrictedCilFieldDataBindingV1? binding)) continue;
                EntityHandle token = MetadataTokens.EntityHandle(instruction.Token);
                if (token.Kind != HandleKind.FieldDefinition || binding.DataHandle is 0 or > 4096 ||
                    binding.Data is not { Length: > 0 and <= 1_048_576 })
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1463",
                        "The FieldRVA binding token, handle or byte budget is invalid.", OffsetIdentity(provenance, instruction.Offset), provenance);
                FieldDefinition field = metadata.GetFieldDefinition((FieldDefinitionHandle)token);
                int rva = field.GetRelativeVirtualAddress();
                int size = MetadataFieldDataSize(metadata, field.Signature);
                if (rva == 0 || !field.Attributes.HasFlag(FieldAttributes.Static) || size != binding.Data.Length ||
                    !pe.GetSectionData(rva).GetContent(0, size).AsSpan().SequenceEqual(binding.Data))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1463",
                        "The FieldRVA binding must exactly match the selected module's static data.",
                        OffsetIdentity(provenance, instruction.Offset), provenance);
            }
            if (_mode == RestrictedCilImportModeV1.ScalarControlFlowV2)
                return StampProfiles(ImportControlFlowV2(metadata, decoded.Instructions, signature, locals, body.MaxStack, provenance));
            VerifyResult verified = Verify(metadata, decoded.Instructions, signature, locals, body.MaxStack, provenance);
            if (verified.Failure is not null) return verified.Failure;
            return StampProfiles(MapToCanonicalIr(metadata, decoded.Instructions, signature, locals, verified.Helpers, provenance));
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentOutOfRangeException)
        {
            return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0005", "The managed PE metadata is malformed.", selector.MethodName);
        }
    }

    private MethodSelection SelectMethod(MetadataReader metadata, RestrictedCilMethodSelectorV1 selector)
    {
        if (selector.MetadataToken is int token)
        {
            EntityHandle entity;
            try { entity = MetadataTokens.EntityHandle(token); }
            catch (ArgumentException)
            {
                return new(default, default, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0008",
                    "The selected method token is invalid.", token.ToString("x8", CultureInfo.InvariantCulture)));
            }
            if (entity.Kind != HandleKind.MethodDefinition)
                return new(default, default, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0008",
                    "The selected token is not a method definition.", token.ToString("x8", CultureInfo.InvariantCulture)));
            MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)entity;
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            string typeName = FindDeclaringType(metadata, methodHandle);
            if (!string.Equals(typeName, selector.TypeName, StringComparison.Ordinal) ||
                !string.Equals(metadata.GetString(method.Name), selector.MethodName, StringComparison.Ordinal))
                return new(default, default, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0008",
                    "The selected method token does not match the selector.", token.ToString("x8", CultureInfo.InvariantCulture)));
            TypeDefinitionHandle declaringType = metadata.TypeDefinitions.Single(handle =>
                string.Equals(FullTypeName(metadata, handle), typeName, StringComparison.Ordinal) &&
                metadata.GetTypeDefinition(handle).GetMethods().Contains(methodHandle));
            return new(declaringType, methodHandle, null);
        }
        List<(TypeDefinitionHandle Type, MethodDefinitionHandle Method)> matches = new List<(TypeDefinitionHandle Type, MethodDefinitionHandle Method)>();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            if (!string.Equals(FullTypeName(metadata, typeHandle), selector.TypeName, StringComparison.Ordinal)) continue;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                if (string.Equals(metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name), selector.MethodName, StringComparison.Ordinal))
                    matches.Add((typeHandle, methodHandle));
            }
        }
        if (matches.Count != 1)
            return new(default, default, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0008",
                matches.Count == 0 ? "The selected method was not found." : "The selected method name is ambiguous; overloads are outside the v1 selector.",
                $"{selector.TypeName}.{selector.MethodName}"));
        return new(matches[0].Type, matches[0].Method, null);
    }

    private RestrictedCilImportResultV1? ValidateMethodEnvelope(
        MetadataReader metadata,
        TypeDefinitionHandle typeHandle,
        MethodDefinition method,
        RestrictedCilProvenanceV1 provenance,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? exactTypeArguments = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? exactMethodArguments = null,
        IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1>? typeInitializationBindings = null,
        bool discoverNullableGenericConstraint = false)
    {
        string identity = provenance.MethodIdentity;
        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
        string declaringTypeIdentity = FullTypeName(metadata, typeHandle);
        int typeArity = type.GetGenericParameters().Count;
        int methodArity = method.GetGenericParameters().Count;
        exactTypeArguments ??= [];
        exactMethodArguments ??= [];
        if (typeArity != exactTypeArguments.Count || methodArity != exactMethodArguments.Count)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1012",
                "Generic bodies require one exact closed argument for every type and method parameter.", identity, provenance);
        if ((typeArity != 0 || methodArity != 0) && _mode != RestrictedCilImportModeV1.ScalarControlFlowV2)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1012",
                "Exact AOT generic instantiations require ScalarControlFlowV2.", identity, provenance);
        if (exactTypeArguments.Concat(exactMethodArguments).Any(static argument =>
                argument is null || string.IsNullOrWhiteSpace(argument.StableTypeIdentity) ||
                argument.CarrierType is RestrictedCilTypeV1.Invalid or RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.Aggregate or
                    RestrictedCilTypeV1.ManagedByRef or RestrictedCilTypeV1.Void))
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1012",
                "Open, byref, aggregate or malformed generic arguments are not admitted by exact AOT generics V1.", identity, provenance);
        foreach (GenericParameterHandle parameterHandle in type.GetGenericParameters().Concat(method.GetGenericParameters()))
        {
            GenericParameter parameter = metadata.GetGenericParameter(parameterHandle);
            if ((parameter.Attributes != GenericParameterAttributes.None || parameter.GetConstraints().Count != 0) &&
                !(method.GetGenericParameters().Contains(parameterHandle) && (_nullableInt32ReturnIngress || discoverNullableGenericConstraint) &&
                  IsExactNullableInt32GenericCopyConstraint(metadata, method, exactTypeArguments, exactMethodArguments)) &&
                !(method.GetGenericParameters().Contains(parameterHandle) && (_exactStructScalarMethodIngress || discoverNullableGenericConstraint) &&
                  IsExactStructScalarMethod(metadata, method, exactTypeArguments, exactMethodArguments)) &&
                !(type.GetGenericParameters().Contains(parameterHandle) && (_exactStructOwnerIngress || discoverNullableGenericConstraint) &&
                  method.Attributes.HasFlag(MethodAttributes.Static) && IsExactSignedOwnerContext(metadata, type, exactTypeArguments, requireStructConstraint: true)))
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1702",
                    "Generic constraints are outside exact AOT generics V1.", identity, provenance);
        }
        if ((method.Attributes & MethodAttributes.Static) == 0 && _mode != RestrictedCilImportModeV1.ScalarControlFlowV2)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1004", "Instance methods require the ScalarControlFlowV2 profile.", identity, provenance);
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1015", "P/Invoke is not qualified.", identity, provenance);
        if (method.RelativeVirtualAddress == 0 || (method.Attributes & MethodAttributes.Abstract) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1005", "The selected method must have a managed CIL body.", identity, provenance);
        foreach (MethodDefinitionHandle candidateHandle in type.GetMethods())
        {
            MethodDefinition candidate = metadata.GetMethodDefinition(candidateHandle);
            IReadOnlyDictionary<string, RestrictedCilTypeInitializationBindingV1> bindings =
                typeInitializationBindings ?? _typeInitializationBindings;
            if (string.Equals(metadata.GetString(candidate.Name), ".cctor", StringComparison.Ordinal) &&
                (!bindings.TryGetValue(declaringTypeIdentity, out RestrictedCilTypeInitializationBindingV1? binding) ||
                 binding.InitializerMetadataToken != MetadataTokens.GetToken(candidateHandle)))
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1016", "Declaring types with a static constructor are not qualified.", identity, provenance);
        }
        if ((method.Attributes & MethodAttributes.Static) == 0 &&
            !type.BaseType.IsNil &&
            ExactTypeHandleName(metadata, type.BaseType) is "System.ValueType" or "System.Enum" &&
            (_mode != RestrictedCilImportModeV1.ScalarControlFlowV2 || ReceiverPlan(metadata, typeHandle) is null))
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1840",
                "Value-type receiver requires an exact non-generic, reference-free sequential payload; ObjectReference this is not valid.", identity, provenance);
        if ((method.ImplAttributes & MethodImplAttributes.Synchronized) != 0)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1025",
                "Implicit method synchronization is not qualified.", identity, provenance);
        return null;
    }

    private DecodeResult Decode(byte[] bytes, RestrictedCilProvenanceV1 provenance, MetadataReader metadata, PEReader pe,
        bool allowProjection, MethodSignature? scalarSignature = null, Func<int, string, RestrictedCilTypeV1, bool>? externalGetterProof = null,
        LocalSignature? projectionLocals = null, bool allowNullableParameterAddress = false, bool deferNullableArgumentCalls = false)
    {
        List<DecodedInstruction> instructions = new List<DecodedInstruction>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (instructions.Count >= _budgets.MaximumDecodedInstructions)
                return new([], Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2004", "The decoded instruction budget was exhausted.", OffsetIdentity(provenance, offset), provenance));
            int start = offset;
            byte first = bytes[offset++];
            ushort encoding;
            if (first == 0xfe)
            {
                if (offset >= bytes.Length)
                    return InvalidDecode("A two-byte opcode prefix is truncated.", provenance, start);
                encoding = (ushort)(0xfe00 | bytes[offset++]);
            }
            else
            {
                encoding = first;
            }
            if (!_matrix.TryGetOpcode(encoding, out RestrictedCilOpcodeContractV1? row) || row is null)
            {
                if (_mode == RestrictedCilImportModeV1.ScalarControlFlowV2 && (allowProjection || allowNullableParameterAddress) && encoding == 0x0f &&
                    scalarSignature is not null && bytes.Length - offset >= 6 &&
                    bytes[offset] < scalarSignature.Parameters.Count &&
                    scalarSignature.Parameters[bytes[offset]] is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64 &&
                    scalarSignature.AggregateParameters?.ElementAtOrDefault(bytes[offset]) is { } argumentIdentity &&
                    (bytes[offset + 1] == 0x28 && (IsExactScalarGetter(metadata, pe, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 2, 4)),
                        argumentIdentity, scalarSignature.Parameters[bytes[offset]]) ||
                     externalGetterProof?.Invoke(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 2, 4)),
                         argumentIdentity, scalarSignature.Parameters[bytes[offset]]) == true ||
                     argumentIdentity == NullableInt32Identity && NullableInt32Member(metadata,
                         BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 2, 4))) is NullableInt32Value or NullableInt32HasValue) ||
                     argumentIdentity == NullableInt32Identity && (TryNullableInt32ParameterDefault(metadata, bytes, offset + 1, scalarSignature, out _) ||
                         bytes[offset] == 0 && (IsNullableInt32FallbackParameter(scalarSignature) || scalarSignature.Parameters.Count == 1 ||
                             scalarSignature.AggregateReturn == NullableInt32Identity))))
                {
                    row = new(encoding, "ldarga.s", "[] -> [exact scalar parameter address]",
                        "only an exact pure scalar/nullable getter projection; no general byref",
                        "qualified parameter read projection", RestrictedCilMatrixSupportV1.Supported, "HCCIL1001");
                }
                else if (_mode == RestrictedCilImportModeV1.ScalarControlFlowV2 && TryGetControlFlowV2Opcode(encoding, out row))
                {
                    // The opt-in profile extends decoding without changing the frozen legacy v1 matrix.
                }
                else
                {
                    if (encoding == 0xff || encoding is 0x24 or 0x77 or 0x78 or 0xa6 or 0xfe08 or 0xfe10 or 0xfe1b)
                        return InvalidDecode($"Opcode encoding 0x{encoding:x4} is invalid or reserved.", provenance, start);
                    return new([], Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1001",
                        $"CIL opcode 0x{encoding:x4} is outside the closed v1 matrix.", OffsetIdentity(provenance, start), provenance));
                }
            }
            long literal = 0;
            int branchTarget = -1;
            int token = 0;
            IReadOnlyList<int>? switchTargets = null;
            if (encoding == 0x45)
            {
                if (bytes.Length - offset < 4)
                    return InvalidDecode("switch target count is truncated.", provenance, start);
                uint targetCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                if (targetCount > _budgets.MaximumBasicBlocks)
                    return new([], Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2101",
                        "switch target count exceeds the ScalarControlFlowV2 basic-block budget.", OffsetIdentity(provenance, start), provenance));
                long tableBytes = 4L + 4L * targetCount;
                if (tableBytes > bytes.Length - offset)
                    return InvalidDecode("switch target table is truncated.", provenance, start);
                int nextOffset = checked(offset + (int)tableBytes);
                int[] targets = new int[targetCount];
                for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                {
                    long target = (long)nextOffset + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4 + targetIndex * 4, 4));
                    if (target is < 0 or > int.MaxValue)
                        return InvalidDecode("switch target is outside the method address domain.", provenance, start);
                    targets[targetIndex] = (int)target;
                }
                switchTargets = targets;
                offset = nextOffset;
            }
            int operandBytes = encoding switch
            {
                0x0e or 0x0f or 0x10 or 0x11 or 0x12 or 0x13 or 0x1f or 0xde or (>= 0x2b and <= 0x37) => 1,
                0x20 or 0x28 or 0x29 or 0x6f or 0x72 or 0x73 or 0x74 or 0x75 or 0x7b or 0x7d or 0x7e or 0x7f or 0x80 or 0x8c or 0x8d or 0x8f or 0xa4 or 0xa5 or 0xd0 or 0xdd or 0xfe06 or 0xfe07 or 0xfe15 or (>= 0x38 and <= 0x44) => 4,
                0x21 => 8,
                _ => 0
            };
            if (encoding == 0x45) operandBytes = 0;
            if (bytes.Length - offset < operandBytes)
                return InvalidDecode($"Operand for {row!.Name} is truncated.", provenance, start);
            if (encoding is 0x0e or 0x0f or 0x10 or 0x11 or 0x12 or 0x13) literal = bytes[offset];
            else if (encoding == 0x1f) literal = unchecked((sbyte)bytes[offset]);
            else if (encoding == 0x20) literal = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            else if (encoding == 0x21) literal = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
            else if (encoding is 0x28 or 0x29 or 0x6f or 0x72 or 0x73 or 0x74 or 0x75 or 0x7b or 0x7d or 0x7e or 0x7f or 0x80 or 0x8c or 0x8d or 0x8f or 0xa4 or 0xa5 or 0xd0 or 0xfe06 or 0xfe07 or 0xfe15) token = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += operandBytes;
            if (encoding is >= 0x2b and <= 0x37 or 0xde)
                branchTarget = checked(offset + unchecked((sbyte)bytes[offset - 1]));
            else if (encoding is >= 0x38 and <= 0x44 or 0xdd)
                branchTarget = checked(offset + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset - 4, 4)));
            if (encoding is >= 0x15 and <= 0x1e) literal = encoding == 0x15 ? -1 : encoding - 0x16;
            instructions.Add(new(start, offset - start, encoding, row!.Name, literal, branchTarget, token, SwitchTargets: switchTargets));
        }
        if (instructions.Count == 0)
            return InvalidDecode("The selected method has an empty CIL body.", provenance, 0);
        HashSet<int> boundaries = instructions.Select(static instruction => instruction.Offset).ToHashSet();
        foreach (DecodedInstruction instruction in instructions)
        {
            foreach (int target in ControlFlowTargets(instruction))
                if (!boundaries.Contains(target))
                    return InvalidDecode("A branch target is not a CIL instruction boundary.", provenance, instruction.Offset);
        }
        if (_mode == RestrictedCilImportModeV1.ScalarControlFlowV2 && allowProjection) {
            if (scalarSignature is not null) {
                instructions = ProjectExactGenericInt32LocalZero(metadata, instructions, scalarSignature, projectionLocals,
                    _exactStructScalarMethodIngress).ToList();
                DecodeResult parameters = ProjectNullableInt32ParameterReads(metadata, pe, instructions, scalarSignature, provenance, deferNullableArgumentCalls, projectionLocals);
                if (parameters.Failure is not null) return parameters;
                instructions = parameters.Instructions.ToList();
            }
            IReadOnlyList<DecodedInstruction> projected = ProjectNullableInt32(metadata, pe, ProjectArrayZero(metadata, ProjectStaticScalarReads(metadata, pe, instructions)),
                projectionLocals, scalarSignature, provenance, out RestrictedCilImportResultV1? projectionFailure);
            return new(projected, projectionFailure);
        }
        return new(instructions, null);
    }

    private VerifyResult Verify(
        MetadataReader metadata,
        IReadOnlyList<DecodedInstruction> instructions,
        MethodSignature signature,
        LocalSignature locals,
        int declaredMaxStack,
        RestrictedCilProvenanceV1 provenance)
    {
        Dictionary<int, ResolvedHelper> helpers = new Dictionary<int, ResolvedHelper>();
        SortedSet<int> leaders = new SortedSet<int> { instructions[0].Offset };
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction instruction = instructions[index];
            if (instruction.BranchTarget >= 0) leaders.Add(instruction.BranchTarget);
            if (IsTerminator(instruction.Encoding) && index + 1 < instructions.Count) leaders.Add(instructions[index + 1].Offset);
        }
        if (leaders.Count > _budgets.MaximumBasicBlocks)
            return new(helpers, Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2005", "The basic-block budget was exhausted.", provenance.MethodIdentity, provenance));
        HashSet<int> leaderSet = leaders.ToHashSet();
        int[] localStores = new int[locals.Types.Count];
        foreach (DecodedInstruction instruction in instructions.Where(static instruction => instruction.Encoding is >= 0x0a and <= 0x0d))
        {
            int local = instruction.Encoding - 0x0a;
            if (local >= locals.Types.Count)
                return new(helpers, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0030", "stloc references an absent local.", OffsetIdentity(provenance, instruction.Offset), provenance));
            if (++localStores[local] > 1)
                return new(helpers, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1021", "The v1 profile requires single-assignment locals.", OffsetIdentity(provenance, instruction.Offset), provenance));
        }
        foreach (DecodedInstruction branch in instructions.Where(static instruction => instruction.BranchTarget >= 0))
        {
            if (branch.BranchTarget <= branch.Offset)
                return new(helpers, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1022", "Backward CIL branches require the later loop/phi contract.", OffsetIdentity(provenance, branch.Offset), provenance));
            foreach (DecodedInstruction store in instructions.Where(static instruction => instruction.Encoding is >= 0x0a and <= 0x0d))
            {
                if (branch.Offset < store.Offset && branch.BranchTarget > store.Offset)
                    return new(helpers, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1023", "A branch may not bypass a qualified local definition.", OffsetIdentity(provenance, branch.Offset), provenance));
            }
        }
        int peakStack = 0;
        int callCount = 0;
        bool[] assignedLocals = new bool[locals.Types.Count];
        List<RestrictedCilTypeV1> stack = new List<RestrictedCilTypeV1>();
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction instruction = instructions[index];
            if (leaderSet.Contains(instruction.Offset)) stack.Clear();
            RestrictedCilImportResultV1? failure = Simulate(metadata, instruction, signature, locals, assignedLocals, stack, helpers, ref callCount, provenance);
            if (failure is not null) return new(helpers, failure);
            peakStack = Math.Max(peakStack, stack.Count);
            if (peakStack > _budgets.MaximumEvaluationStack)
                return new(helpers, Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2006", "The evaluation-stack budget was exhausted.", OffsetIdentity(provenance, instruction.Offset), provenance));
            bool boundary = IsTerminator(instruction.Encoding) || (index + 1 < instructions.Count && leaderSet.Contains(instructions[index + 1].Offset));
            if (boundary && stack.Count != 0)
                return new(helpers, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1020",
                    "The v1 profile requires an empty evaluation stack at every basic-block boundary.", OffsetIdentity(provenance, instruction.Offset), provenance));
        }
        if (peakStack > declaredMaxStack)
            return new(helpers, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0009", "The method body understates its maximum stack.", provenance.MethodIdentity, provenance));
        if (instructions[^1].Encoding != 0x2a)
            return new(helpers, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0010", "The method body does not terminate with ret.", provenance.MethodIdentity, provenance));
        return new(helpers, null);
    }

    private RestrictedCilImportResultV1? Simulate(
        MetadataReader metadata,
        DecodedInstruction instruction,
        MethodSignature method,
        LocalSignature locals,
        bool[] assignedLocals,
        List<RestrictedCilTypeV1> stack,
        IDictionary<int, ResolvedHelper> helpers,
        ref int callCount,
        RestrictedCilProvenanceV1 provenance)
    {
        string identity = OffsetIdentity(provenance, instruction.Offset);
        switch (instruction.Encoding)
        {
            case 0x00: return null;
            case >= 0x02 and <= 0x05:
                int argument = instruction.Encoding - 0x02;
                if (argument >= method.Parameters.Count)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0011", "ldarg references an absent argument.", identity, provenance);
                stack.Add(StackType(method.Parameters[argument]));
                return null;
            case >= 0x06 and <= 0x09:
                int loadedLocal = instruction.Encoding - 0x06;
                if (loadedLocal >= locals.Types.Count)
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0031", "ldloc references an absent local.", identity, provenance);
                if (!assignedLocals[loadedLocal])
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0032", "ldloc reads an unassigned local.", identity, provenance);
                stack.Add(StackType(locals.Types[loadedLocal]));
                return null;
            case >= 0x0a and <= 0x0d:
                int storedLocal = instruction.Encoding - 0x0a;
                if (storedLocal >= locals.Types.Count || !TryPop(stack, out RestrictedCilTypeV1 storedType))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0033", "stloc references an absent local or underflows the evaluation stack.", identity, provenance);
                if (!StackCompatible(storedType, locals.Types[storedLocal]))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0034", "stloc value does not match the declared local type.", identity, provenance);
                assignedLocals[storedLocal] = true;
                return null;
            case 0x14: stack.Add(RestrictedCilTypeV1.ObjectReference); return null;
            case >= 0x15 and <= 0x20: stack.Add(RestrictedCilTypeV1.Int32); return null;
            case 0x21: stack.Add(RestrictedCilTypeV1.Int64); return null;
            case 0x58:
            case 0x59:
            case 0x5a:
                if (!TryPop(stack, out RestrictedCilTypeV1 right) || !TryPop(stack, out RestrictedCilTypeV1 left))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0012", "Arithmetic evaluation-stack underflow.", identity, provenance);
                if (left != right || !IsArithmeticType(left))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0013", "Arithmetic operands have incompatible CIL types.", identity, provenance);
                stack.Add(left);
                return null;
            case 0xfe02:
            case 0xfe03:
            case 0xfe04:
            case 0xfe05:
                if (!TryPop(stack, out RestrictedCilTypeV1 comparisonRight) || !TryPop(stack, out RestrictedCilTypeV1 comparisonLeft))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0035", "Comparison evaluation-stack underflow.", identity, provenance);
                if (comparisonLeft != comparisonRight || !IsArithmeticType(comparisonLeft))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0036", "Comparison operands have incompatible CIL types.", identity, provenance);
                stack.Add(RestrictedCilTypeV1.Int32);
                return null;
            case 0x2c:
            case 0x2d:
            case 0x39:
            case 0x3a:
                if (!TryPop(stack, out RestrictedCilTypeV1 condition))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0014", "Conditional branch evaluation-stack underflow.", identity, provenance);
                if (!IsConditionType(condition))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0015", "Conditional branch requires a qualified integer or object reference.", identity, provenance);
                return null;
            case 0x2b:
            case 0x38:
                return null;
            case 0x28:
                callCount++;
                if (callCount > _budgets.MaximumCalls)
                    return Reject(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL2007", "The call budget was exhausted.", identity, provenance);
                HelperResolution resolution = ResolveHelper(metadata, instruction.Token);
                if (resolution.Failure is not null) return resolution.Failure with { Provenance = provenance };
                ResolvedHelper helper = resolution.Helper!;
                for (int parameter = helper.Contract.ParameterTypes.Count - 1; parameter >= 0; parameter--)
                {
                    if (!TryPop(stack, out RestrictedCilTypeV1 actual))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0016", "Helper call evaluation-stack underflow.", identity, provenance);
                    if (!StackCompatible(actual, helper.Contract.ParameterTypes[parameter]))
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0017", "Helper call argument type mismatch.", identity, provenance);
                }
                if (helper.Contract.ReturnType != RestrictedCilTypeV1.Void) stack.Add(helper.Contract.ReturnType);
                helpers.Add(instruction.Offset, helper);
                return null;
            case 0x6f:
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1001",
                    "callvirt runtime intrinsics require the ScalarControlFlowV2 profile and an exact helper binding.", identity, provenance);
            case 0x8f:
                return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1408",
                    "ldelema requires managed interior-reference lifetime and root-map semantics that are not qualified.", identity, provenance);
            case 0x2a:
                if (method.ReturnType == RestrictedCilTypeV1.Void)
                {
                    if (stack.Count != 0)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0018", "Void return requires an empty evaluation stack.", identity, provenance);
                }
                else
                {
                    if (!TryPop(stack, out RestrictedCilTypeV1 returned) || !StackCompatible(returned, method.ReturnType) || stack.Count != 0)
                        return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0019", "Return value does not match the method signature.", identity, provenance);
                }
                return null;
            default:
                return Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL0020", "The verified decoder produced an unknown semantic opcode.", identity, provenance);
        }
    }

    private RestrictedCilImportResultV1 MapToCanonicalIr(
        MetadataReader metadata,
        IReadOnlyList<DecodedInstruction> instructions,
        MethodSignature signature,
        LocalSignature locals,
        IReadOnlyDictionary<int, ResolvedHelper> helpers,
        RestrictedCilProvenanceV1 provenance)
    {
        List<Emission> emissions = new List<Emission>();
        List<ValueRef> stack = new List<ValueRef>();
        ValueRef?[] localValues = new ValueRef?[locals.Types.Count];
        HashSet<int> leaders = new HashSet<int> { instructions[0].Offset };
        foreach (DecodedInstruction instruction in instructions)
        {
            if (instruction.BranchTarget >= 0) leaders.Add(instruction.BranchTarget);
        }
        for (int index = 0; index < instructions.Count; index++)
        {
            DecodedInstruction source = instructions[index];
            if (leaders.Contains(source.Offset)) stack.Clear();
            string identity = OffsetIdentity(provenance, source.Offset);
            switch (source.Encoding)
            {
                case 0x00: break;
                case >= 0x02 and <= 0x05:
                    int argument = source.Encoding - 0x02;
                    stack.Add(ValueRef.Argument(argument, StackType(signature.Parameters[argument]), provenance.CanonicalMethodLocalIdentity));
                    break;
                case >= 0x06 and <= 0x09:
                    stack.Add(localValues[source.Encoding - 0x06]!);
                    break;
                case >= 0x0a and <= 0x0d:
                    int local = source.Encoding - 0x0a;
                    ValueRef stored = Pop(stack);
                    localValues[local] = stored;
                    break;
                case 0x14:
                    stack.Add(ValueRef.Constant(0, RestrictedCilTypeV1.ObjectReference, identity));
                    break;
                case >= 0x15 and <= 0x21:
                    stack.Add(ValueRef.Constant(source.Literal, source.Encoding == 0x21 ? RestrictedCilTypeV1.Int64 : RestrictedCilTypeV1.Int32, identity));
                    break;
                case 0x58:
                case 0x59:
                case 0x5a:
                    ValueRef right = Pop(stack);
                    ValueRef left = Pop(stack);
                    ValueRef definition = ValueRef.Definition(left.Type, identity);
                    emissions.Add(new(source.Offset, source.Encoding switch { 0x58 => HybridCpuOpcode.ADD, 0x59 => HybridCpuOpcode.SUB, _ => HybridCpuOpcode.MUL }, left.Type,
                        [left.Operand, right.Operand], [definition.Operand], -1, identity));
                    stack.Add(definition);
                    break;
                case 0xfe02:
                case 0xfe03:
                case 0xfe04:
                case 0xfe05:
                    ValueRef comparisonRight = Pop(stack);
                    ValueRef comparisonLeft = Pop(stack);
                    ValueRef predicate = ValueRef.Definition(RestrictedCilTypeV1.Int32, identity);
                    HybridCpuOpcode comparisonOpcode = source.Encoding switch
                    {
                        0xfe03 or 0xfe05 => HybridCpuOpcode.SLTU,
                        _ => HybridCpuOpcode.SLT
                    };
                    IrOperand[] comparisonUses = source.Encoding is 0xfe02 or 0xfe03
                        ? [comparisonRight.Operand, comparisonLeft.Operand]
                        : [comparisonLeft.Operand, comparisonRight.Operand];
                    emissions.Add(new(source.Offset, comparisonOpcode, RestrictedCilTypeV1.Int32,
                        comparisonUses, [predicate.Operand], -1, identity));
                    stack.Add(predicate);
                    break;
                case 0x28:
                    ResolvedHelper helper = helpers[source.Offset];
                    ValueRef[] arguments = new ValueRef[helper.Contract.ParameterTypes.Count];
                    for (int parameter = arguments.Length - 1; parameter >= 0; parameter--) arguments[parameter] = Pop(stack);
                    ValueRef helperDefinition = ValueRef.Definition(helper.Contract.ReturnType, identity);
                    emissions.Add(new(source.Offset, HybridCpuOpcode.ADDI, helper.Contract.ReturnType,
                        [arguments[0].Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], [helperDefinition.Operand], -1, identity));
                    stack.Add(helperDefinition);
                    break;
                case 0x2b:
                case 0x38:
                    emissions.Add(new(source.Offset, HybridCpuOpcode.JAL, RestrictedCilTypeV1.NativeUInt,
                        Array.Empty<IrOperand>(), Array.Empty<IrOperand>(), source.BranchTarget, identity));
                    break;
                case 0x2c:
                case 0x2d:
                case 0x39:
                case 0x3a:
                    ValueRef condition = Pop(stack);
                    emissions.Add(new(source.Offset, source.Encoding is 0x2d or 0x3a ? HybridCpuOpcode.BNE : HybridCpuOpcode.BEQ,
                        condition.Type, [condition.Operand, new(IrOperandKind.Constant, 0, $"{identity}:zero")], Array.Empty<IrOperand>(), source.BranchTarget, identity));
                    break;
                case 0x2a:
                    IrOperand[] returnUses = signature.ReturnType == RestrictedCilTypeV1.Void ? [] : [Pop(stack).Operand];
                    emissions.Add(new(source.Offset, HybridCpuOpcode.JALR, StackType(signature.ReturnType),
                        returnUses, Array.Empty<IrOperand>(), -1, identity));
                    break;
            }
        }
        if (emissions.Count == 0)
            return Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1006", "The method has no Canonical IR operations after mapping.", provenance.MethodIdentity, provenance);

        Dictionary<int, int> targetIndices = new Dictionary<int, int>();
        foreach (int targetOffset in emissions.Where(static emission => emission.BranchTarget >= 0).Select(static emission => emission.BranchTarget).Distinct())
        {
            int mapped = emissions.FindIndex(emission => emission.CilOffset >= targetOffset);
            if (mapped < 0)
                return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0021", "A branch target has no Canonical IR operation.", OffsetIdentity(provenance, targetOffset), provenance);
            targetIndices.Add(targetOffset, mapped);
        }
        HybridCpuInstructionWord[] words = emissions.Select(static emission => new HybridCpuInstructionWord
        {
            OpCode = (uint)emission.Opcode,
            DataTypeValue = ToDataType(emission.Type),
            PredicateMask = byte.MaxValue,
            VirtualThreadId = 0
        }).ToArray();
        IrLabelDeclaration[] labels = targetIndices.OrderBy(static pair => pair.Key)
            .Select(pair => new IrLabelDeclaration($"cil_{pair.Key:x4}", pair.Value)).ToArray();
        IrControlFlowTargetReference[] references = emissions.Select((emission, index) => (emission, index))
            .Where(static pair => pair.emission.BranchTarget >= 0)
            .Select(pair => new IrControlFlowTargetReference(pair.index, $"cil_{pair.emission.BranchTarget:x4}",
                pair.emission.Opcode == HybridCpuOpcode.JAL ? IrControlTransferKind.Branch : IrControlTransferKind.ConditionalBranch))
            .ToArray();
        IrProgram shape;
        try
        {
            shape = new HybridCpuIrBuilder().BuildProgram(0, words, labelDeclarations: labels, controlFlowTargetReferences: references);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Reject(RestrictedCilImportStatusV1.UnknownSemantics, "HCCIL0022", "Core rejected the mapped target instruction shape.", provenance.MethodIdentity, provenance);
        }

        SortedDictionary<string, IrVirtualValueV1> values = new SortedDictionary<string, IrVirtualValueV1>(StringComparer.Ordinal);
        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        for (int parameter = 0; parameter < signature.Parameters.Count; parameter++)
        {
            ValueRef argument = ValueRef.Argument(parameter, StackType(signature.Parameters[parameter]), provenance.CanonicalMethodLocalIdentity);
            values.Add(argument.Operand.Name, CreateValue(
                argument.Operand.Name,
                argument.Type,
                parameter < HybridCpuNativeAbiContractV2.Default.ArgumentRegisters.Count
                    ? HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[parameter]
                    : null));
        }
        List<IrInstruction> mappedInstructions = new List<IrInstruction>(emissions.Count);
        for (int index = 0; index < emissions.Count; index++)
        {
            Emission emission = emissions[index];
            IrInstruction native = shape.Instructions[index];
            foreach (IrOperand use in emission.Uses.Where(static operand => operand.Kind == IrOperandKind.VirtualValue))
            {
                if (!values.ContainsKey(use.Name))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0023", "A mapped use has no value definition.", emission.Identity, provenance);
                accesses.Add(new(use.Name, index, IrValueAccessKind.Use));
            }
            foreach (IrOperand definition in emission.Defs)
            {
                if (!values.TryAdd(definition.Name, CreateValue(definition.Name, emission.Type)))
                    return Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0024", "A mapped value definition is duplicated.", emission.Identity, provenance);
                accesses.Add(new(definition.Name, index, IrValueAccessKind.Def));
            }
            IrSourceOriginChainV1 origin = new IrSourceOriginChainV1(1,
            [
                new(emission.Identity, IrSourceOriginKind.Cil, "HybridCPU.Compiler.Cil", "1", null,
                    IrFrontendEvidenceTrust.ValidatedStructural)
            ]);
            IrArchitecturalEffectKind effects = emission.Opcode == HybridCpuOpcode.JALR
                ? IrArchitecturalEffectKind.Control | IrArchitecturalEffectKind.Return
                : emission.BranchTarget >= 0 ? IrArchitecturalEffectKind.Control : IrArchitecturalEffectKind.None;
            mappedInstructions.Add(native with
            {
                Operands = [.. emission.Uses, .. emission.Defs],
                Annotation = native.Annotation with { Uses = emission.Uses, Defs = emission.Defs },
                StableIdentity = emission.Identity,
                CanonicalType = ToCanonicalType(emission.Type),
                Semantics = new(
                    emission.Opcode is HybridCpuOpcode.ADD or HybridCpuOpcode.SUB or HybridCpuOpcode.MUL or HybridCpuOpcode.ADDI
                        ? IrIntegerOverflowSemantics.Wrap : IrIntegerOverflowSemantics.NotApplicable,
                    IrShiftSemantics.NotApplicable,
                    IrPointerArithmeticSemantics.NotApplicable,
                    IrUndefinedValueSemantics.NotApplicable,
                    ConversionMayTrap: false,
                    OperationMayFault: false),
                OriginChain = origin,
                SideEffects = new(IrCanonicalMemoryEffectV1.None, effects)
            });
        }
        IrBasicBlock[] blocks = shape.BasicBlocks.Select(block => block with
        {
            Instructions = mappedInstructions.Where(instruction => instruction.Index >= block.StartInstructionIndex && instruction.Index <= block.EndInstructionIndex).ToArray(),
            FunctionName = provenance.CanonicalMethodLocalIdentity
        }).ToArray();
        IrProgram program = shape with
        {
            Instructions = mappedInstructions,
            ControlFlowGraph = shape.ControlFlowGraph with { Blocks = blocks },
            ValueFlow = new("hybridcpu.value-flow/v1", 1, values.Values.ToArray(),
                accesses.OrderBy(static access => access.InstructionIndex).ThenBy(static access => access.Kind).ThenBy(static access => access.ValueId, StringComparer.Ordinal).ToArray()),
            FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Empty,
            Contract = shape.Contract with
            {
                RequiredCapabilities = ["frontend.restricted-cil/v1", "isa.hybridcpu-w8-native-v1"]
            }
        };
        IrFrontendAdapterResultV1 boundary = CanonicalIrFrontendBoundaryV1.Validate(program);
        return boundary.Status == IrFrontendAdapterStatus.Success
            ? new(RestrictedCilImportStatusV1.Success, program, Array.Empty<IrFrontendDiagnosticV1>(), provenance)
            : new(boundary.Status switch
            {
                IrFrontendAdapterStatus.InvalidInput => RestrictedCilImportStatusV1.InvalidInput,
                IrFrontendAdapterStatus.Unsupported => RestrictedCilImportStatusV1.Unsupported,
                _ => RestrictedCilImportStatusV1.UnknownSemantics
            }, null, boundary.Diagnostics, provenance);
    }

    private HelperResolution ResolveHelper(MetadataReader metadata, int token)
    {
        if (_managedCallTargets.TryGetValue(token, out RestrictedCilManagedCallTargetV1? managedTarget))
        {
            if (managedTarget.AggregateReturn == NullableInt32Identity && !_nullableInt32ReturnCalls)
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1890",
                    "Nullable<int> return calls require the exact primary private BodyWorld producer."));
            bool exactReceiverLoan = managedTarget.Receiver is { } receiver &&
                managedTarget.ParameterTypes.Count != 0 &&
                managedTarget.ParameterTypes[0] == RestrictedCilTypeV1.ManagedByRef &&
                managedTarget.AggregateParameters?.ElementAtOrDefault(0) == receiver.ScopedTypeIdentity + "&" &&
                !managedTarget.ParameterTypes.Skip(1).Contains(RestrictedCilTypeV1.ManagedByRef) &&
                managedTarget.ReturnType != RestrictedCilTypeV1.ManagedByRef;
            if ((managedTarget.ParameterTypes.Contains(RestrictedCilTypeV1.ManagedByRef) ||
                 managedTarget.ReturnType == RestrictedCilTypeV1.ManagedByRef) && !exactReceiverLoan)
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1843",
                    $"Managed-byref target '{managedTarget.StableIdentity}' (token 0x{token:x8}; parameters " +
                    $"[{string.Join(',', managedTarget.ParameterTypes)}]; return {managedTarget.ReturnType}) requires caller-owned storage, exact loan identity, non-null bounds and lifetime proofs."));
            RestrictedCilHelperContractV1 managedContract = new RestrictedCilHelperContractV1(
                managedTarget.StableIdentity,
                managedTarget.ParameterTypes,
                managedTarget.ReturnType,
                "managed-direct-call",
                "HybridCPU native managed-to-managed ABI pending Phase 03 lowering",
                IrMemoryEffectKind.None,
                IrArchitecturalEffectKind.Control);
            return new(new(managedTarget.StableIdentity, managedContract, true, AggregateReturn: managedTarget.AggregateReturn,
                AggregateParameters: managedTarget.AggregateParameters, Receiver: managedTarget.Receiver), null);
        }
        EntityHandle handle;
        try { handle = MetadataTokens.EntityHandle(token); }
        catch (ArgumentException)
        {
            return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0025", "The call metadata token is invalid."));
        }
        if (handle.Kind == HandleKind.MethodSpecification)
            return ResolveArrayEmptyHelper(metadata, (MethodSpecificationHandle)handle);
        string typeName;
        string methodName;
        BlobHandle signatureHandle;
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)handle;
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                return ResolvePInvokeDeclaration(metadata, methodHandle);
            typeName = FindDeclaringType(metadata, methodHandle);
            methodName = metadata.GetString(method.Name);
            signatureHandle = method.Signature;
        }
        else if (handle.Kind == HandleKind.MemberReference)
        {
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            if (member.GetKind() != MemberReferenceKind.Method)
                return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0026", "The call token does not reference a method."));
            typeName = ParentTypeName(metadata, member.Parent);
            methodName = metadata.GetString(member.Name);
            signatureHandle = member.Signature;
        }
        else
        {
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1008", "Method specifications and indirect calls are not qualified."));
        }
        if (typeName == "System.String" && methodName is "get_Length" or "get_Chars" && !IsExactStringAccessorDeclaration(metadata, token))
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1478",
                "Only exact CoreLib String.Length/Chars declarations are qualified by checked UTF-16 helpers."));
        if (typeName == "System.ArgumentNullException" && methodName == ".ctor")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib") ||
                !metadata.GetBlobBytes(signatureHandle).AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x0e }))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1470",
                    "Only exact CoreLib ArgumentNullException(string paramName) is qualified by the invariant-resource helper."));
        }
        if (typeName == "System.ArgumentOutOfRangeException" && methodName == ".ctor")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib") ||
                !metadata.GetBlobBytes(signatureHandle).AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x0e }))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1471",
                    "Only exact CoreLib ArgumentOutOfRangeException(string paramName) is qualified by the invariant-resource helper."));
        }
        if (typeName == "System.String" && methodName == ".ctor")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib") ||
                !metadata.GetBlobBytes(signatureHandle).AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x1d, 0x03 }))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1472",
                    "Only exact CoreLib String(char[]) is qualified by the immutable UTF-16 factory helper."));
        }
        if (typeName == "System.String" && methodName is "op_Equality" or "op_Inequality")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib"))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1477",
                    "Only exact CoreLib String equality operators are qualified by immutable UTF-16 comparison helpers."));
        }
        if (typeName == "System.Array" && methodName == "Copy")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib"))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1473",
                    "Only the exact CoreLib Array.Copy(Array,int,Array,int,int) helper family is qualified."));
        }
        if (typeName == "System.Array" && methodName == "Clear")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib"))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1476",
                    "Only the exact CoreLib Array.Clear(Array) helper family is qualified."));
        }
        if (typeName == "System.Math" && methodName == "Abs")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib") ||
                !metadata.GetBlobBytes(signatureHandle).AsSpan().SequenceEqual(new byte[] { 0x00, 1, 0x0a, 0x0a }))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1474",
                    "Only exact CoreLib Math.Abs(Int64) is qualified by the checked signed-magnitude helper."));
        }
        if (typeName == "System.Math" && methodName == "Max")
        {
            string? assembly = handle.Kind == HandleKind.MethodDefinition
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Parent.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)metadata.GetMemberReference((MemberReferenceHandle)handle).Parent)
                    : null;
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib") ||
                !metadata.GetBlobBytes(signatureHandle).AsSpan().SequenceEqual(new byte[] { 0x00, 2, 0x08, 0x08, 0x08 }))
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1475",
                    "Only exact CoreLib Math.Max(Int32,Int32) is qualified by the total signed maximum helper."));
        }
        MethodSignature signature = ParseMethodSignature(metadata, signatureHandle);
        if (signature.Status != SignatureStatus.Success)
            return new(null, Reject(signature.Status == SignatureStatus.Invalid ? RestrictedCilImportStatusV1.InvalidInput : RestrictedCilImportStatusV1.Unsupported,
                signature.Status == SignatureStatus.Invalid ? "HCCIL0027" : "HCCIL1009", signature.Message));
        if (signature.HasThis && _mode != RestrictedCilImportModeV1.ScalarControlFlowV2)
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1007", "Instance helper calls require the ScalarControlFlowV2 profile."));
        string identity = $"{typeName}.{methodName}({string.Join(',', signature.Parameters.Select(TypeName))}):{TypeName(signature.ReturnType)}";
        if (!_matrix.TryGetHelper(identity, out RestrictedCilHelperContractV1? contract) || contract is null)
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1009", $"Call target '{identity}' is not in the v1 helper allowlist."));
        return new(new(identity, contract, false), null);
    }

    private HelperResolution ResolveDispatch(MetadataReader metadata, int token,
        RestrictedCilDispatchBindingV1? exactBinding = null)
    {
        _dispatchBindings.TryGetValue(token, out RestrictedCilDispatchBindingV1? binding);
        binding = exactBinding ?? binding;
        binding ??= TryCreateRuntimeExternalDispatch(metadata, token);
        if (binding is null || string.IsNullOrWhiteSpace(binding.StableIdentity) || binding.ParameterTypes is null ||
            binding.SlotId == 0 || !Enum.IsDefined(binding.Kind) ||
            (binding.Kind == RestrictedCilDispatchKindV1.Interface) != binding.InterfaceTypeId.HasValue ||
            binding.InterfaceTypeId == 0 ||
            (binding.CandidateMethodMetadataTokens is { Count: > 0 } && binding.ExactGenericCandidates is { Count: > 0 }) ||
            (binding.ExactGenericCandidates?.Any(static candidate => candidate is null ||
                candidate.MethodMetadataToken == 0) ?? false))
        {
            HelperResolution target = ResolveHelper(metadata, token);
            string targetIdentity = DescribeMethodReference(metadata, token);
            string detail = target.Failure is null ? string.Empty : " " +
                string.Join("; ", target.Failure.Diagnostics.Select(static row => row.Message));
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1501",
                $"callvirt target '{targetIdentity}' requires one exact virtual or interface dispatch binding.{detail}"));
        }
        EntityHandle handle;
        try { handle = MetadataTokens.EntityHandle(token); }
        catch (ArgumentException)
        { return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1502", "callvirt token is invalid.")); }
        BlobHandle signatureHandle = handle.Kind switch
        {
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Signature,
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)handle).Signature,
            _ => default
        };
        if (signatureHandle.IsNil)
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1502", "callvirt token form is not qualified."));
        MethodSignature signature = ParseMethodSignature(metadata, signatureHandle,
            binding.GenericTypeArguments, binding.GenericMethodArguments);
        if (signature.Status != SignatureStatus.Success || !signature.HasThis ||
            !signature.Parameters.Select(StackType).SequenceEqual(binding.ParameterTypes) ||
            StackType(signature.ReturnType) != binding.ReturnType || binding.ParameterTypes.Count == 0 ||
            binding.ParameterTypes[0] != RestrictedCilTypeV1.ObjectReference)
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1503",
                "callvirt metadata signature and dispatch binding are inconsistent."));
        RestrictedCilHelperContractV1 contract = new RestrictedCilHelperContractV1(binding.StableIdentity, binding.ParameterTypes,
            binding.ReturnType, "managed-indirect-dispatch", binding.Kind.ToString(), IrMemoryEffectKind.Read,
            IrArchitecturalEffectKind.Control);
        return new(new(binding.StableIdentity, contract, true, binding), null);
    }

    private RestrictedCilDispatchBindingV1? TryCreateRuntimeExternalDispatch(MetadataReader metadata, int token)
    {
        EntityHandle handle;
        try { handle = MetadataTokens.EntityHandle(token); }
        catch (ArgumentException) { return null; }
        TypeDefinitionHandle owner;
        BlobHandle signatureHandle;
        string methodName;
        int methodRow;
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)handle;
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            owner = FindDeclaringTypeHandle(metadata, methodHandle);
            signatureHandle = method.Signature;
            methodName = metadata.GetString(method.Name);
            methodRow = MetadataTokens.GetRowNumber(methodHandle);
        }
        else if (handle.Kind == HandleKind.MemberReference)
        {
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            if (member.GetKind() != MemberReferenceKind.Method || member.Parent.Kind != HandleKind.TypeDefinition)
                return null;
            owner = (TypeDefinitionHandle)member.Parent;
            signatureHandle = member.Signature;
            methodName = metadata.GetString(member.Name);
            methodRow = MetadataTokens.GetRowNumber((MemberReferenceHandle)handle);
        }
        else return null;
        TypeDefinition type = metadata.GetTypeDefinition(owner);
        if (!type.Attributes.HasFlag(TypeAttributes.Interface)) return null;
        int typeRow = MetadataTokens.GetRowNumber(owner);
        if (typeRow is <= 0 or > short.MaxValue || methodRow is <= 0 or > short.MaxValue) return null;
        MethodSignature signature = ParseMethodSignature(metadata, signatureHandle);
        if (signature.Status != SignatureStatus.Success || !signature.HasThis) return null;
        RestrictedCilTypeV1[] parameters = signature.Parameters.Select(StackType).ToArray();
        if (parameters.Length == 0 || parameters[0] != RestrictedCilTypeV1.ObjectReference) return null;
        string typeName = FullTypeName(metadata, owner);
        string stableIdentity = $"runtime-external-interface:{typeName}.{methodName}" +
            $"({string.Join(',', parameters.Select(TypeName))}):{TypeName(signature.ReturnType)}";
        if (_runtimeExternalBindings.TryGetValue(stableIdentity, out RestrictedCilRuntimeExternalBindingV1? binding) &&
            parameters.SequenceEqual(binding.ParameterTypes) && StackType(signature.ReturnType) == binding.ReturnType)
            return new(token, stableIdentity, parameters, binding.ReturnType, RestrictedCilDispatchKindV1.Interface,
                checked((ulong)methodRow), checked((ulong)typeRow), [], RuntimeExternal: false,
                ExactImplementationIdentity: binding.ExactImplementationIdentity);
        return new(token, stableIdentity, parameters, StackType(signature.ReturnType),
            RestrictedCilDispatchKindV1.Interface, checked((ulong)methodRow), checked((ulong)typeRow),
            [], RuntimeExternal: true);
    }

    internal static string DescribeMethodReference(MetadataReader metadata, int token)
    {
        try
        {
            EntityHandle handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)handle;
                return $"{FindDeclaringType(metadata, methodHandle)}.{metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name)}";
            }
            if (handle.Kind == HandleKind.MemberReference)
            {
                MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                return $"{ParentTypeName(metadata, member.Parent)}.{metadata.GetString(member.Name)}";
            }
            return $"token:0x{token:x8}";
        }
        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
        {
            return $"invalid-token:0x{token:x8}";
        }
    }

    private HelperResolution ResolveCallvirt(MetadataReader metadata, int token)
    {
        if (_dispatchBindings.ContainsKey(token) || TryCreateRuntimeExternalDispatch(metadata, token) is not null)
            return ResolveDispatch(metadata, token);
        HelperResolution exactHelper = ResolveHelper(metadata, token);
        if (exactHelper.Failure?.Diagnostics.Any(static diagnostic => diagnostic.Code == "HCCIL1478") == true)
            return exactHelper;
        // Exception.Message is virtual (ArgumentException adds ParamName, for example).
        // A base-field accessor is valid for an explicit base call, not as devirtualization proof.
        if (exactHelper.Helper?.Contract.CanonicalExpansion == "__hybridcpu_managed_exception_get_message")
            return ResolveDispatch(metadata, token);
        return exactHelper.Failure is null ? exactHelper : ResolveDispatch(metadata, token);
    }

    private FieldResolution ResolveField(MetadataReader metadata, int token, bool requireStatic, StaticScalarProjection? projection = null,
        bool allowAggregateAnalysis = false)
    {
        EntityHandle handle;
        try { handle = MetadataTokens.EntityHandle(token); }
        catch (ArgumentException)
        {
            return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1205", "The field metadata token is invalid."));
        }
        string declaringType;
        string fieldName;
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            FieldDefinitionHandle fieldHandle = (FieldDefinitionHandle)handle;
            FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
            fieldName = metadata.GetString(field.Name);
            TypeDefinitionHandle owner = metadata.TypeDefinitions.FirstOrDefault(type =>
                metadata.GetTypeDefinition(type).GetFields().Contains(fieldHandle));
            if (owner.IsNil) return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1205", "The field has no declaring type."));
            declaringType = FullTypeName(metadata, owner);
        }
        else if (handle.Kind == HandleKind.MemberReference)
        {
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            if (member.GetKind() != MemberReferenceKind.Field)
                return new(null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1205", "The field token does not reference a field."));
            declaringType = ParentTypeName(metadata, member.Parent);
            fieldName = metadata.GetString(member.Name);
        }
        else return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1206", "The field token form is not qualified."));

        if (!_fieldLayouts.TryGetValue($"{declaringType}::{fieldName}", out RestrictedCilFieldLayoutBindingV1? binding) ||
            binding.IsStatic != requireStatic)
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1207",
                $"Field '{declaringType}::{fieldName}' has no exact runtime-defined layout binding."));
        if (projection is not null)
        {
            HybridCpuManagedFieldLayoutV1? storage = binding.TypeDescriptor.StaticLayout.Fields.SingleOrDefault(field => field.Identity == binding.FieldName);
            if (!requireStatic || binding.DeclaringType != projection.OwnerType ||
                storage is not { StorageKind: HybridCpuManagedStorageKindV1.BlittableValue, SizeBytes: 4, AlignmentBytes: 4 })
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1821", "Static getter projection does not match its runtime layout."));
            binding = binding with { FieldType = RestrictedCilTypeV1.Int32 };
        }
        if (binding.IsScalarValueProjection)
        {
            HybridCpuManagedFieldLayoutV1? storage = requireStatic
                ? binding.TypeDescriptor.StaticLayout.Fields.SingleOrDefault(field => field.Identity == binding.FieldName)
                : binding.TypeDescriptor.InstanceFields.SingleOrDefault(field => field.Identity == binding.FieldName);
            if (binding.IsStatic != requireStatic ||
                (requireStatic ? binding.StaticStorageSymbol is null : binding.StaticStorageSymbol is not null) ||
                binding.FieldType is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32) ||
                storage is not { StorageKind: HybridCpuManagedStorageKindV1.BlittableValue, SizeBytes: 4, AlignmentBytes: 4 })
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1822",
                    "Scalar value field projection does not match its exact runtime layout."));
        }
        if (binding.FieldType is RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.Aggregate)
        {
            if (allowAggregateAnalysis && !requireStatic && handle.Kind == HandleKind.FieldDefinition)
            {
                FieldDefinition definition = metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
                BlobReader reader = metadata.GetBlobReader(definition.Signature);
                if (reader.ReadByte() == 0x06)
                {
                    RestrictedCilTypeV1 type = ReadAnalysisType(metadata, ref reader, [], [], true, out string? aggregate);
                    MetadataStorage? storage = new InlineStorageResolver([]).Field(metadata, definition.Signature);
                    HybridCpuManagedFieldLayoutV1? physical = binding.TypeDescriptor.InstanceFields.SingleOrDefault(f => f.Identity == fieldName);
                    if (reader.RemainingBytes == 0 && type == RestrictedCilTypeV1.Aggregate && aggregate is not null &&
                        storage is not null && physical is not null && ValidDescriptor(binding.TypeDescriptor) &&
                        physical.StorageKind == HybridCpuManagedStorageKindV1.BlittableValue &&
                        physical.SizeBytes == storage.Size && physical.AlignmentBytes == storage.Alignment)
                        return new(binding with { FieldType = RestrictedCilTypeV1.Aggregate }, null, aggregate);
                }
            }
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1209",
                $"Field '{declaringType}::{fieldName}' has inline value storage but requires aggregate/byref lowering."));
        }
        HybridCpuManagedFieldLoweringPlanV1 plan = new HybridCpuManagedFieldLoweringV1().Lower(
            binding.TypeDescriptor, binding.FieldName, binding.IsStatic, HybridCpuManagedFieldAccessKindV1.Address,
            binding.StaticStorageSymbol);
        if (plan.Status != HybridCpuManagedFieldLoweringStatusV1.Lowered)
            return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1208", plan.Reason));
        string? scalarIdentity = null;
        if (binding.IsScalarValueProjection)
        {
            BlobHandle fieldSignature = handle.Kind == HandleKind.FieldDefinition
                ? metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Signature
                : metadata.GetMemberReference((MemberReferenceHandle)handle).Signature;
            BlobReader scalarReader = metadata.GetBlobReader(fieldSignature);
            if (scalarReader.RemainingBytes == 0 || scalarReader.ReadByte() != 0x06 ||
                ReadAnalysisType(metadata, ref scalarReader, [], [], true, out scalarIdentity) != RestrictedCilTypeV1.Aggregate ||
                scalarIdentity is null || scalarReader.RemainingBytes != 0)
                return new(null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1822",
                    "Scalar value field projection lacks an exact metadata-scoped value identity."));
        }
        return new(binding, null, scalarIdentity);
    }

    private AllocationResolution ResolveAllocation(MetadataReader metadata, int token)
    {
        if (!_allocationBindings.TryGetValue(token, out RestrictedCilAllocationBindingV1? binding))
        {
            string target = DescribeMethodReference(metadata, token);
            int separator = target.LastIndexOf("..ctor", StringComparison.Ordinal);
            string missingDeclaringType = separator < 0 ? string.Empty : target[..separator];
            string[] available = _allocationBindings.Values
                .Where(candidate => string.Equals(candidate.TypeDescriptor.StableIdentity, missingDeclaringType,
                    StringComparison.Ordinal))
                .Select(static candidate => $"0x{candidate.ConstructorMetadataToken:x8}")
                .Order(StringComparer.Ordinal).ToArray();
            return new(null, null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1301",
                $"newobj target '{target}' (token 0x{token:x8}) requires an exact runtime-owned TypeDescriptor/type-handle binding; available exact constructor tokens for the declaring type: {(available.Length == 0 ? "none" : string.Join(",", available))}."));
        }
        HelperResolution constructor;
        if (binding.IsScalarValueProjection)
        {
            string identity = $"scalar-value-constructor:{binding.TypeDescriptor.StableIdentity}:0x{token:x8}";
            RestrictedCilHelperContractV1 contract = new RestrictedCilHelperContractV1(identity,
                [RestrictedCilTypeV1.ManagedByRef, binding.ScalarArgumentType], RestrictedCilTypeV1.Void,
                "scalar-value-constructor-projection", "Exact single-field value constructor projected to an SSA copy.",
                IrMemoryEffectKind.None, IrArchitecturalEffectKind.None);
            constructor = new(new(identity, contract, false), null);
        }
        else
        {
            constructor = ResolveHelper(metadata, token);
        }
        if (constructor.Failure is not null || constructor.Helper is not { } helper ||
            !binding.IsScalarValueProjection && !helper.IsManagedCall && !(helper.Contract.CallingConvention == "runtime-helper" &&
                helper.Contract.CanonicalExpansion is "__hybridcpu_managed_exception_ctor_message" or
                    "__hybridcpu_managed_argument_null_ctor_param_name" or
                    "__hybridcpu_managed_argument_out_of_range_ctor_param_name" ||
                helper.Contract.CallingConvention == "runtime-factory-helper" &&
                helper.Contract.CanonicalExpansion == "__hybridcpu_managed_string_from_utf16_array"))
            return new(null, null, constructor.Failure ?? Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1302",
                "newobj constructor is not an admitted direct managed call target."));

        EntityHandle handle;
        try { handle = MetadataTokens.EntityHandle(token); }
        catch (ArgumentException)
        {
            return new(null, null, Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL1303",
                "newobj constructor token is invalid."));
        }
        string declaringType;
        string methodName;
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)handle;
            declaringType = FindDeclaringType(metadata, methodHandle);
            methodName = metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name);
        }
        else if (handle.Kind == HandleKind.MemberReference)
        {
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            declaringType = ParentTypeName(metadata, member.Parent);
            methodName = metadata.GetString(member.Name);
        }
        else
            return new(null, null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1303",
                "newobj constructor token form is not qualified."));

        HybridCpuManagedTypeDescriptorV1 descriptor = binding.TypeDescriptor;
        bool stringFactory = helper.Contract.CanonicalExpansion == "__hybridcpu_managed_string_from_utf16_array";
        bool scalarValueProjection = binding.IsScalarValueProjection &&
            descriptor.Kind == HybridCpuManagedTypeKindV1.ValueType &&
            descriptor.ValueTypeShape is { ObjectReferenceOffsets.Count: 0, PayloadSizeBytes: 4 or 8 } shape &&
            binding.ScalarValueType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.NativeInt or
                RestrictedCilTypeV1.NativeUInt &&
            binding.ScalarArgumentType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 or
                RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.NativeInt or
                RestrictedCilTypeV1.NativeUInt &&
            shape.PayloadSizeBytes == (binding.ScalarValueType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 ? 4 : 8) &&
            (binding.ScalarValueType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32
                ? binding.ScalarArgumentType is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32
                : binding.ScalarArgumentType is RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or
                    RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt) &&
            helper.Contract.ParameterTypes.Count == 2 && helper.Contract.ParameterTypes[1] == binding.ScalarArgumentType;
        bool valid = binding.ConstructorMetadataToken == token && methodName == ".ctor" &&
            descriptor.SchemaId == HybridCpuManagedTypeDescriptorContractV1.SchemaId &&
            descriptor.SchemaMajor == HybridCpuManagedTypeDescriptorContractV1.SchemaMajor &&
            descriptor.SchemaMinor <= HybridCpuManagedTypeDescriptorContractV1.SchemaMinor &&
            (scalarValueProjection || descriptor.Kind == (stringFactory ? HybridCpuManagedTypeKindV1.String : HybridCpuManagedTypeKindV1.Class)) &&
            descriptor.StableIdentity == declaringType &&
            descriptor.DescriptorDigest == HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor) &&
            binding.TypeHandle != 0 && binding.TypeHandle <= (ulong)short.MaxValue &&
            helper.Contract.ReturnType == RestrictedCilTypeV1.Void && helper.Contract.ParameterTypes.Count != 0 &&
            (scalarValueProjection || helper.Contract.ParameterTypes[0] == RestrictedCilTypeV1.ObjectReference);
        return valid
            ? new(binding, helper, null)
            : new(null, null, Reject(RestrictedCilImportStatusV1.Unsupported, "HCCIL1304",
                "newobj binding, TypeDescriptor, type handle or constructor signature is inconsistent."));
    }

    private static bool ValidTypeTestBinding(RestrictedCilTypeTestBindingV1? binding) =>
        binding is not null && binding.TypeMetadataToken != 0 && binding.TypeHandle != 0 &&
        binding.TypeHandle <= (ulong)short.MaxValue &&
        binding.TypeDescriptor.SchemaId == HybridCpuManagedTypeDescriptorContractV1.SchemaId &&
        binding.TypeDescriptor.SchemaMajor == HybridCpuManagedTypeDescriptorContractV1.SchemaMajor &&
        binding.TypeDescriptor.SchemaMinor <= HybridCpuManagedTypeDescriptorContractV1.SchemaMinor &&
        binding.TypeDescriptor.TypeId != 0 &&
        binding.TypeDescriptor.DescriptorDigest == HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(binding.TypeDescriptor);

    private static MethodSignature ParseMethodSignature(
        MetadataReader metadata,
        BlobHandle handle,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null,
        bool allowAggregates = false)
    {
        try
        {
            BlobReader reader = metadata.GetBlobReader(handle);
            if (reader.RemainingBytes == 0) return MethodSignature.Invalid("The method signature blob is empty.");
            byte header = reader.ReadByte();
            if ((header & 0x80) != 0)
                return MethodSignature.Invalid("The method signature header contains a reserved bit.");
            int convention = header & 0x0f;
            if (convention > 0x05 && convention != 0x09)
                return MethodSignature.Invalid("The signature header does not encode a supported method signature kind.");
            int genericArity = (header & 0x10) != 0 ? reader.ReadCompressedInteger() : 0;
            if (genericArity < 0) return MethodSignature.Invalid("The generic method arity is malformed.");
            methodArguments ??= [];
            typeArguments ??= [];
            if (genericArity != methodArguments.Count)
                return MethodSignature.Unsupported("A generic method signature requires an exact closed method instantiation.");
            bool hasThis = (header & 0x20) != 0;
            if ((header & 0x40) != 0) return MethodSignature.Unsupported("Explicit-this method signatures are not qualified.");
            if (convention != 0)
                return MethodSignature.Unsupported("Vararg and unmanaged call conventions are not qualified.");
            int parameterCount = reader.ReadCompressedInteger();
            if (parameterCount < 0) return MethodSignature.Invalid("The method parameter count is malformed.");
            RestrictedCilTypeV1 returnType;
            string? aggregateReturn;
            if (allowAggregates && ReadNullableInt32(metadata, ref reader, methodArguments: methodArguments)) {
                returnType = RestrictedCilTypeV1.Int64;
                aggregateReturn = NullableInt32Identity;
            } else returnType = ReadAnalysisType(metadata, ref reader, typeArguments, methodArguments,
                allowAggregates, out aggregateReturn);
            if (parameterCount > reader.RemainingBytes)
                return MethodSignature.Invalid("The method parameter count exceeds the remaining signature bytes.");
            List<RestrictedCilTypeV1> parameters = new List<RestrictedCilTypeV1>(checked(parameterCount + (hasThis ? 1 : 0)));
            List<string?> aggregateParameters = new List<string?>();
            List<ConstructorReferenceKind> constructorReferenceParameters = new List<ConstructorReferenceKind>();
            if (hasThis) { parameters.Add(RestrictedCilTypeV1.ObjectReference); aggregateParameters.Add(null); constructorReferenceParameters.Add(ConstructorReferenceKind.None); }
            for (int index = 0; index < parameterCount; index++)
            {
                BlobReader declaration = reader;
                string? aggregate;
                if (allowAggregates && ReadNullableInt32(metadata, ref reader, methodArguments: methodArguments)) {
                    parameters.Add(RestrictedCilTypeV1.Int64);
                    aggregate = NullableInt32Identity;
                } else parameters.Add(ReadAnalysisType(metadata, ref reader, typeArguments, methodArguments, allowAggregates, out aggregate));
                aggregateParameters.Add(aggregate);
                // Declaration fact after normal type validation, not reference/memory authority.
                constructorReferenceParameters.Add(DeclaredConstructorReference(parameters[^1], declaration, reader.Offset));
            }
            if (reader.RemainingBytes != 0) return MethodSignature.Unsupported("Modified or extended signature forms are not qualified.");
            if (returnType == RestrictedCilTypeV1.Invalid || parameters.Contains(RestrictedCilTypeV1.Invalid))
                return MethodSignature.Invalid("The method signature contains an invalid type encoding.");
            if (returnType is RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef ||
                parameters.Any(static type => type is RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef))
                return MethodSignature.Unsupported("Managed references, floating point, aggregates and extended signature types are not qualified.");
            if (parameters.Contains(RestrictedCilTypeV1.Void)) return MethodSignature.Invalid("A method parameter cannot have type void.");
            return new(SignatureStatus.Success, returnType, parameters, hasThis, string.Empty, aggregateReturn, aggregateParameters,
                ConstructorReferenceParameters: constructorReferenceParameters, ExactMethodArguments: methodArguments);
        }
        catch (BadImageFormatException)
        {
            return MethodSignature.Invalid("The method signature blob is malformed.");
        }
    }

    private static MethodSignature ParseManagedCallSiteSignature(MetadataReader metadata, int token)
    {
        try
        {
            EntityHandle handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind != HandleKind.StandaloneSignature)
                return MethodSignature.Invalid("The calli token is not a standalone signature.");
            BlobHandle signature = metadata.GetStandaloneSignature((StandaloneSignatureHandle)handle).Signature;
            BlobReader headerReader = metadata.GetBlobReader(signature);
            if (headerReader.RemainingBytes == 0)
                return MethodSignature.Invalid("The calli signature blob is empty.");
            byte header = headerReader.ReadByte();
            if ((header & 0x80) != 0)
                return MethodSignature.Invalid("The calli signature header contains a reserved bit.");
            if ((header & 0x0f) != 0 || (header & 0x60) != 0)
                return MethodSignature.Unsupported(
                    "Only default managed calli signatures without implicit or explicit this are qualified.");
            return ParseMethodSignature(metadata, signature);
        }
        catch (ArgumentException)
        {
            return MethodSignature.Invalid("The calli standalone-signature token is malformed.");
        }
    }

    private static MethodSignature ParseManagedMethodTokenSignature(MetadataReader metadata, int token)
    {
        try
        {
            EntityHandle handle = MetadataTokens.EntityHandle(token);
            BlobHandle signature = handle.Kind switch
            {
                HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Signature,
                HandleKind.MemberReference when metadata.GetMemberReference((MemberReferenceHandle)handle).GetKind() ==
                                                MemberReferenceKind.Method =>
                    metadata.GetMemberReference((MemberReferenceHandle)handle).Signature,
                _ => default
            };
            return signature.IsNil
                ? MethodSignature.Unsupported("The function-pointer token form is not a qualified method reference.")
                : ParseMethodSignature(metadata, signature);
        }
        catch (ArgumentException)
        {
            return MethodSignature.Invalid("The function-pointer method token is malformed.");
        }
    }

    private static bool TryParseMethodSpecificationArguments(
        MetadataReader metadata,
        MethodSpecificationHandle handle,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeContext,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodContext,
        out IReadOnlyList<RestrictedCilGenericArgumentV1> arguments,
        out string reason)
    {
        try
        {
            BlobReader reader = metadata.GetBlobReader(metadata.GetMethodSpecification(handle).Signature);
            if (reader.RemainingBytes == 0 || reader.ReadByte() != 0x0a)
            {
                arguments = [];
                reason = "The MethodSpec instantiation signature header is malformed.";
                return false;
            }
            int count = reader.ReadCompressedInteger();
            if (count <= 0)
            {
                arguments = [];
                reason = "The MethodSpec must contain a non-empty exact argument list.";
                return false;
            }
            if (count > reader.RemainingBytes)
            {
                arguments = [];
                reason = "The MethodSpec argument count exceeds the remaining signature bytes.";
                return false;
            }
            List<RestrictedCilGenericArgumentV1> parsed = new List<RestrictedCilGenericArgumentV1>(count);
            for (int index = 0; index < count; index++)
            {
                RestrictedCilGenericArgumentV1? argument = ReadExactGenericArgument(metadata, ref reader,
                    typeContext, methodContext);
                if (argument is null)
                {
                    arguments = [];
                    reason = "The MethodSpec contains an open, aggregate, byref or unsupported exact argument.";
                    return false;
                }
                parsed.Add(argument);
            }
            if (reader.RemainingBytes != 0)
            {
                arguments = [];
                reason = "The MethodSpec contains trailing or modified signature data.";
                return false;
            }
            arguments = parsed;
            reason = string.Empty;
            return true;
        }
        catch (BadImageFormatException)
        {
            arguments = [];
            reason = "The MethodSpec instantiation signature is malformed.";
            return false;
        }
    }

    private static RestrictedCilGenericArgumentV1? ReadExactGenericArgument(
        MetadataReader metadata,
        ref BlobReader reader,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeContext = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodContext = null)
    {
        if (reader.RemainingBytes == 0) return null;
        byte element = reader.ReadByte();
        RestrictedCilTypeV1 primitive = element switch
        {
            0x02 => RestrictedCilTypeV1.Boolean,
            0x03 => RestrictedCilTypeV1.UInt16,
            0x04 => RestrictedCilTypeV1.Int8,
            0x05 => RestrictedCilTypeV1.UInt8,
            0x06 => RestrictedCilTypeV1.Int16,
            0x07 => RestrictedCilTypeV1.UInt16,
            0x08 => RestrictedCilTypeV1.Int32,
            0x09 => RestrictedCilTypeV1.UInt32,
            0x0a => RestrictedCilTypeV1.Int64,
            0x0b => RestrictedCilTypeV1.UInt64,
            0x0e => RestrictedCilTypeV1.ObjectReference,
            0x18 => RestrictedCilTypeV1.NativeInt,
            0x19 => RestrictedCilTypeV1.NativeUInt,
            0x1c => RestrictedCilTypeV1.ObjectReference,
            _ => RestrictedCilTypeV1.Invalid
        };
        if (primitive != RestrictedCilTypeV1.Invalid)
            return new(element == 0x0e ? "System.String" : TypeName(primitive), primitive);
        if (element is 0x13 or 0x1e)
        {
            int index = reader.ReadCompressedInteger();
            IReadOnlyList<RestrictedCilGenericArgumentV1> context = element == 0x13
                ? typeContext ?? []
                : methodContext ?? [];
            return index >= 0 && index < context.Count ? context[index] : null;
        }
        if (element is 0x11 or 0x12)
        {
            EntityHandle type = ReadTypeDefOrRefEncoded(ref reader);
            if (type.IsNil) return null;
            string identity = ExactTypeHandleName(metadata, type);
            return string.IsNullOrWhiteSpace(identity) || element == 0x11
                ? null
                : new(identity, RestrictedCilTypeV1.ObjectReference);
        }
        if (element == 0x1d)
        {
            RestrictedCilGenericArgumentV1? item = ReadExactGenericArgument(metadata, ref reader, typeContext, methodContext);
            return item is null ? null : new($"{item.StableTypeIdentity}[]", RestrictedCilTypeV1.ObjectReference);
        }
        if (element == 0x15)
        {
            if (reader.RemainingBytes == 0 || reader.ReadByte() != 0x12) return null;
            EntityHandle definition = ReadTypeDefOrRefEncoded(ref reader);
            if (definition.IsNil) return null;
            int count = reader.ReadCompressedInteger();
            if (count <= 0) return null;
            if (count > reader.RemainingBytes) return null;
            List<RestrictedCilGenericArgumentV1> nested = new List<RestrictedCilGenericArgumentV1>(count);
            for (int index = 0; index < count; index++)
            {
                RestrictedCilGenericArgumentV1? argument = ReadExactGenericArgument(metadata, ref reader, typeContext, methodContext);
                if (argument is null) return null;
                nested.Add(argument);
            }
            string name = ExactTypeHandleName(metadata, definition);
            return string.IsNullOrWhiteSpace(name) ? null : new($"{name}<{string.Join(',', nested.Select(static row => row.StableTypeIdentity))}>",
                RestrictedCilTypeV1.ObjectReference);
        }
        return null;
    }

    private static bool TryParseConstructedTypeSpecification(
        MetadataReader metadata,
        TypeSpecificationHandle handle,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeContext,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodContext,
        out string definitionIdentity,
        out EntityHandle definition,
        out IReadOnlyList<RestrictedCilGenericArgumentV1> arguments,
        out string reason)
    {
        definition = default;
        try
        {
            BlobReader reader = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
            if (reader.RemainingBytes == 0 || reader.ReadByte() != 0x15 || reader.RemainingBytes == 0)
            {
                definitionIdentity = string.Empty;
                arguments = [];
                reason = "The TypeSpec is not a constructed generic type.";
                return false;
            }
            byte kind = reader.ReadByte();
            definition = ReadTypeDefOrRefEncoded(ref reader);
            definitionIdentity = definition.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference
                ? NestedTypeName(metadata, definition) : string.Empty;
            int count = reader.ReadCompressedInteger();
            if (kind is not (0x11 or 0x12) || string.IsNullOrWhiteSpace(definitionIdentity) || count <= 0)
            {
                arguments = [];
                reason = "The constructed generic TypeSpec definition or arity is malformed.";
                return false;
            }
            if (count > reader.RemainingBytes)
            {
                arguments = [];
                reason = "The constructed generic TypeSpec argument count exceeds the remaining signature bytes.";
                return false;
            }
            List<RestrictedCilGenericArgumentV1> parsed = new List<RestrictedCilGenericArgumentV1>(count);
            for (int index = 0; index < count; index++)
            {
                RestrictedCilGenericArgumentV1? argument = ReadExactGenericArgument(metadata, ref reader,
                    typeContext, methodContext);
                if (argument is null)
                {
                    arguments = [];
                    reason = "The constructed generic TypeSpec contains an open or unsupported argument.";
                    return false;
                }
                parsed.Add(argument);
            }
            if (reader.RemainingBytes != 0)
            {
                arguments = [];
                reason = "The constructed generic TypeSpec contains trailing data.";
                return false;
            }
            arguments = parsed;
            reason = string.Empty;
            return true;
        }
        catch (BadImageFormatException)
        {
            definitionIdentity = string.Empty;
            arguments = [];
            reason = "The constructed generic TypeSpec is malformed.";
            return false;
        }
    }

    private static EntityHandle ReadTypeDefOrRefEncoded(ref BlobReader reader)
    {
        int coded = reader.ReadCompressedInteger();
        if (coded <= 0) return default;
        int row = coded >> 2;
        int token = (coded & 3) switch
        {
            0 => 0x02000000 | row,
            1 => 0x01000000 | row,
            2 => 0x1b000000 | row,
            _ => 0
        };
        return token == 0 ? default : MetadataTokens.EntityHandle(token);
    }

    private static string ExactTypeHandleName(MetadataReader metadata, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => FullTypeName(metadata, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => TypeReferenceName(metadata, (TypeReferenceHandle)handle),
        _ => string.Empty
    };

    private static ManagedMethodIdentityV1 CreateManagedMethodIdentity(
        MetadataReader metadata,
        TypeDefinitionHandle declaringType,
        MethodDefinitionHandle methodHandle,
        MethodSignature signature,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null)
    {
        AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
        string assemblyName = metadata.GetString(assembly.Name);
        string culture = assembly.Culture.IsNil ? "neutral" : metadata.GetString(assembly.Culture);
        string publicKey = assembly.PublicKey.IsNil
            ? "none"
            : Convert.ToHexString(SHA256.HashData(metadata.GetBlobBytes(assembly.PublicKey))).ToLowerInvariant();
        string assemblyIdentity = string.Join(',', assemblyName, assembly.Version, culture, publicKey);
        string moduleName = metadata.GetString(metadata.GetModuleDefinition().Name);
        MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
        string declaringTypeName = FullTypeName(metadata, declaringType);
        string methodName = metadata.GetString(method.Name);
        string canonicalSignature = signature.Status == SignatureStatus.Success
            ? $"{(signature.HasThis ? "instance" : string.Empty)}({string.Join(',', signature.Parameters.Select((type, index) => signature.AggregateParameters?.ElementAtOrDefault(index) ?? TypeName(type)))}):{signature.AggregateReturn ?? TypeName(signature.ReturnType)}"
            : $"<{signature.Status}:{signature.Message}>";
        int token = MetadataTokens.GetToken(methodHandle);
        typeArguments ??= [];
        methodArguments ??= [];
        string schema = typeArguments.Count == 0 && methodArguments.Count == 0
            ? "hybridcpu.managed-method/v1"
            : "hybridcpu.managed-method-exact-generic/v1";
        string exactArguments = $"type=[{string.Join(',', typeArguments.Select(static row => row.StableTypeIdentity))}]" +
            $";method=[{string.Join(',', methodArguments.Select(static row => row.StableTypeIdentity))}]";
        string descriptor = string.Join('|', schema, assemblyIdentity, moduleName,
            token.ToString("x8", CultureInfo.InvariantCulture), declaringTypeName, methodName, canonicalSignature);
        if (typeArguments.Count != 0 || methodArguments.Count != 0) descriptor = $"{descriptor}|{exactArguments}";
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor))).ToLowerInvariant();
        string stableIdentity = $"mmid:{digest}:{declaringTypeName}.{methodName}{canonicalSignature}";
        return new(schema, assemblyIdentity, moduleName, token, declaringTypeName,
            methodName, canonicalSignature, stableIdentity, digest, typeArguments, methodArguments);
    }

    private static LocalSignature ParseLocalSignature(
        MetadataReader metadata,
        StandaloneSignatureHandle handle,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null,
        bool allowAggregates = false, IReadOnlyDictionary<string, ExternalEnumCarrier>? enumDeclarations = null)
    {
        if (handle.IsNil) return new(SignatureStatus.Success, [], string.Empty, ExactMethodArguments: methodArguments);
        try
        {
            BlobReader reader = metadata.GetBlobReader(metadata.GetStandaloneSignature(handle).Signature);
            if (reader.RemainingBytes == 0 || reader.ReadByte() != 0x07)
                return LocalSignature.Invalid("The local signature header is malformed.");
            int count = reader.ReadCompressedInteger();
            if (count < 0) return LocalSignature.Invalid("The local count is malformed.");
            if (count > reader.RemainingBytes)
                return LocalSignature.Invalid("The local count exceeds the remaining signature bytes.");
            List<RestrictedCilTypeV1> types = new List<RestrictedCilTypeV1>(count);
            List<string?> aggregates = new List<string?>();
            List<ConstructorReferenceKind> constructorReferences = new List<ConstructorReferenceKind>();
            for (int index = 0; index < count; index++)
            {
                BlobReader declaration = reader;
                string? aggregate;
                if (allowAggregates && ReadNullableInt32(metadata, ref reader, methodArguments: methodArguments)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableInt32Identity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, unsigned: true)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableUInt32Identity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, wide: true)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableInt64Identity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, wide: true, unsigned: true)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableUInt64Identity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, unsigned: true, narrowBits: 8, logical: true)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableBooleanIdentity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, unsigned: true, narrowBits: 16, character: true)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableCharIdentity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, unsigned: true, narrowBits: 16)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableUInt16Identity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, unsigned: true, narrowBits: 8)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableUInt8Identity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, narrowBits: 8)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableInt8Identity;
                } else if (allowAggregates && ReadNullableInt32(metadata, ref reader, narrowBits: 16)) {
                    types.Add(RestrictedCilTypeV1.Int64); aggregate = NullableInt16Identity;
                } else if (allowAggregates && ReadNullableEnum(metadata, ref reader, out aggregate, enumDeclarations)) {
                    types.Add(RestrictedCilTypeV1.Int64);
                } else types.Add(ReadAnalysisType(metadata, ref reader, typeArguments, methodArguments, allowAggregates, out aggregate));
                aggregates.Add(aggregate);
                constructorReferences.Add(DeclaredConstructorReference(types[^1], declaration, reader.Offset));
            }
            if (reader.RemainingBytes != 0) return LocalSignature.Unsupported("Modified local signature forms are not qualified.");
            if (types.Contains(RestrictedCilTypeV1.Invalid)) return LocalSignature.Invalid("The local signature contains an invalid type encoding.");
            if (types.Any(static type => type is RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef or RestrictedCilTypeV1.Void))
                return LocalSignature.Unsupported("Only primitive integer and object-reference locals are qualified; managed byrefs are rejected.");
            return new(SignatureStatus.Success, types, string.Empty, aggregates, constructorReferences, enumDeclarations, methodArguments);
        }
        catch (BadImageFormatException)
        {
            return LocalSignature.Invalid("The local signature blob is malformed.");
        }
    }

    private enum ConstructorReferenceKind : byte { None, Int32Array, UInt32Array, Int64Array, UInt64Array, String, ByteArray, SByteArray, Int16Array, UInt16Array, CharArray, BooleanArray, Null }

    private static ConstructorReferenceKind DeclaredConstructorReference(RestrictedCilTypeV1 type, BlobReader declaration, int end)
    {
        if (type != RestrictedCilTypeV1.ObjectReference) return ConstructorReferenceKind.None;
        int length = end - declaration.Offset;
        if (length == 1 && declaration.ReadByte() == 0x0e) return ConstructorReferenceKind.String;
        return length == 2 && declaration.ReadByte() == 0x1d
            ? declaration.ReadByte() switch {
                0x02 => ConstructorReferenceKind.BooleanArray, 0x03 => ConstructorReferenceKind.CharArray, 0x04 => ConstructorReferenceKind.SByteArray, 0x05 => ConstructorReferenceKind.ByteArray,
                0x06 => ConstructorReferenceKind.Int16Array, 0x07 => ConstructorReferenceKind.UInt16Array,
                0x08 => ConstructorReferenceKind.Int32Array, 0x09 => ConstructorReferenceKind.UInt32Array,
                0x0a => ConstructorReferenceKind.Int64Array, 0x0b => ConstructorReferenceKind.UInt64Array, _ => ConstructorReferenceKind.None }
            : ConstructorReferenceKind.None;
    }

    private static RestrictedCilTypeV1 ReadType(
        MetadataReader metadata,
        ref BlobReader reader,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null,
        bool arrayElementCarrier = false)
    {
        if (reader.RemainingBytes == 0) return RestrictedCilTypeV1.Invalid;
        byte element = reader.ReadByte();
        if (element is 0x11 or 0x12)
        {
            EntityHandle type = ReadTypeDefOrRefEncoded(ref reader);
            if (type.IsNil) return RestrictedCilTypeV1.Invalid;
            if (element == 0x12 || arrayElementCarrier) return RestrictedCilTypeV1.ObjectReference;
            return ExactTypeHandleName(metadata, type) == "System.RuntimeFieldHandle"
                ? RestrictedCilTypeV1.NativeUInt
                : EnumCarrier(metadata, type);
        }
        if (element == 0x10)
        {
            RestrictedCilTypeV1 target = ReadType(metadata, ref reader, typeArguments, methodArguments);
            return target == RestrictedCilTypeV1.Invalid ? RestrictedCilTypeV1.Invalid : RestrictedCilTypeV1.ManagedByRef;
        }
        if (element == 0x1d)
        {
            RestrictedCilTypeV1 elementType = ReadType(metadata, ref reader, typeArguments, methodArguments,
                arrayElementCarrier: true);
            // The field/argument carrier of every well-formed SZARRAY is an object reference.
            // Its element layout is qualified separately by newarr/element-access contracts;
            // an aggregate value-type element must not make the array reference itself unsupported.
            return elementType is RestrictedCilTypeV1.Invalid or RestrictedCilTypeV1.Void or RestrictedCilTypeV1.ManagedByRef or RestrictedCilTypeV1.UnsupportedManaged
                ? RestrictedCilTypeV1.UnsupportedManaged
                : RestrictedCilTypeV1.ObjectReference;
        }
        if (element == 0x1b)
        {
            if (reader.RemainingBytes == 0) return RestrictedCilTypeV1.Invalid;
            byte header = reader.ReadByte();
            if ((header & 0x80) != 0)
                throw new BadImageFormatException("The function-pointer signature header contains a reserved bit.");
            if ((header & 0x70) != 0 || (header & 0x0f) != 0)
                return RestrictedCilTypeV1.UnsupportedManaged;
            int parameterCount = reader.ReadCompressedInteger();
            if (parameterCount < 0) return RestrictedCilTypeV1.Invalid;
            RestrictedCilTypeV1 returnType = ReadType(metadata, ref reader, typeArguments, methodArguments);
            if (parameterCount > reader.RemainingBytes)
                throw new BadImageFormatException("The function-pointer parameter count exceeds the remaining signature bytes.");
            RestrictedCilTypeV1[] parameters = new RestrictedCilTypeV1[parameterCount];
            for (int index = 0; index < parameterCount; index++) parameters[index] = ReadType(metadata, ref reader, typeArguments, methodArguments);
            return returnType is RestrictedCilTypeV1.Invalid or RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef ||
                parameters.Any(static type => type is RestrictedCilTypeV1.Void or RestrictedCilTypeV1.Invalid or
                    RestrictedCilTypeV1.UnsupportedManaged or RestrictedCilTypeV1.ManagedByRef)
                ? RestrictedCilTypeV1.UnsupportedManaged
                : RestrictedCilTypeV1.NativeUInt;
        }
        if (element is 0x13 or 0x1e)
        {
            int index = reader.ReadCompressedInteger();
            IReadOnlyList<RestrictedCilGenericArgumentV1> arguments = element == 0x13
                ? typeArguments ?? []
                : methodArguments ?? [];
            return index >= 0 && index < arguments.Count
                ? arguments[index].CarrierType == RestrictedCilTypeV1.Aggregate ? RestrictedCilTypeV1.UnsupportedManaged : arguments[index].CarrierType
                : RestrictedCilTypeV1.UnsupportedManaged;
        }
        if (element == 0x15)
        {
            if (reader.RemainingBytes == 0) return RestrictedCilTypeV1.Invalid;
            byte kind = reader.ReadByte();
            if (kind is not (0x11 or 0x12) || ReadTypeDefOrRefEncoded(ref reader).IsNil)
                return RestrictedCilTypeV1.Invalid;
            int count = reader.ReadCompressedInteger();
            if (count < 0) return RestrictedCilTypeV1.Invalid;
            for (int index = 0; index < count; index++)
                if (ReadType(metadata, ref reader, typeArguments, methodArguments) == RestrictedCilTypeV1.Invalid)
                    return RestrictedCilTypeV1.Invalid;
            return kind == 0x12 ? RestrictedCilTypeV1.ObjectReference : RestrictedCilTypeV1.UnsupportedManaged;
        }
        return element switch
        {
            0x01 => RestrictedCilTypeV1.Void,
            0x02 => RestrictedCilTypeV1.Boolean,
            0x03 => RestrictedCilTypeV1.UInt16,
            0x04 => RestrictedCilTypeV1.Int8,
            0x05 => RestrictedCilTypeV1.UInt8,
            0x06 => RestrictedCilTypeV1.Int16,
            0x07 => RestrictedCilTypeV1.UInt16,
            0x08 => RestrictedCilTypeV1.Int32,
            0x09 => RestrictedCilTypeV1.UInt32,
            0x0a => RestrictedCilTypeV1.Int64,
            0x0b => RestrictedCilTypeV1.UInt64,
            0x18 => RestrictedCilTypeV1.NativeInt,
            0x19 => RestrictedCilTypeV1.NativeUInt,
            0x0e or 0x1c => RestrictedCilTypeV1.ObjectReference,
            0x00 or 0x17 or 0x1a or 0x1c or 0x21 or 0x22 or 0x23 or 0x40 or 0x41 or 0x45 => RestrictedCilTypeV1.Invalid,
            _ => RestrictedCilTypeV1.UnsupportedManaged
        };
    }

    private static RestrictedCilTypeV1 EnumCarrier(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeDefinition) return RestrictedCilTypeV1.UnsupportedManaged;
        TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
        if (ExactTypeHandleName(metadata, type.BaseType) != "System.Enum")
            return RestrictedCilTypeV1.UnsupportedManaged;
        FieldDefinitionHandle[] valueFields = type.GetFields().Where(field =>
            string.Equals(metadata.GetString(metadata.GetFieldDefinition(field).Name), "value__", StringComparison.Ordinal)).ToArray();
        if (valueFields.Length != 1) return RestrictedCilTypeV1.UnsupportedManaged;
        BlobReader reader = metadata.GetBlobReader(metadata.GetFieldDefinition(valueFields[0]).Signature);
        if (reader.RemainingBytes != 2 || reader.ReadByte() != 0x06)
            return RestrictedCilTypeV1.UnsupportedManaged;
        RestrictedCilTypeV1 carrier = reader.ReadByte() switch
        {
            0x04 => RestrictedCilTypeV1.Int8,
            0x05 => RestrictedCilTypeV1.UInt8,
            0x06 => RestrictedCilTypeV1.Int16,
            0x07 => RestrictedCilTypeV1.UInt16,
            0x08 => RestrictedCilTypeV1.Int32,
            0x09 => RestrictedCilTypeV1.UInt32,
            0x0a => RestrictedCilTypeV1.Int64,
            0x0b => RestrictedCilTypeV1.UInt64,
            _ => RestrictedCilTypeV1.UnsupportedManaged
        };
        return carrier;
    }

    private static string FullTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        string ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static string FindDeclaringType(MetadataReader metadata, MethodDefinitionHandle method)
    {
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            if (metadata.GetTypeDefinition(typeHandle).GetMethods().Contains(method)) return FullTypeName(metadata, typeHandle);
        }
        return string.Empty;
    }

    private static string ParentTypeName(MetadataReader metadata, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeDefinition => FullTypeName(metadata, (TypeDefinitionHandle)parent),
        HandleKind.TypeReference => TypeReferenceName(metadata, (TypeReferenceHandle)parent),
        _ => string.Empty
    };

    private static string TypeReferenceName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        TypeReference type = metadata.GetTypeReference(handle);
        string name = metadata.GetString(type.Name);
        string ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static IrVirtualValueV1 CreateValue(
        string identity,
        RestrictedCilTypeV1 type,
        int? fixedRegisterId = null)
    {
        int width = Width(type);
        bool signed = IsSigned(type);
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;
        int[] registers = target.ArchitecturalRegisters.Where(static register => register.IsAllocatable)
            .Select(static register => register.Id).ToArray();
        int[] groups = registers.Select(id => target.ArchitecturalRegisters[id].RegisterGroup).Distinct().Order().ToArray();
        bool objectReference = type == RestrictedCilTypeV1.ObjectReference;
        bool managedByRef = type == RestrictedCilTypeV1.ManagedByRef;
        return new(identity,
            new(objectReference ? IrCanonicalValueKind.ManagedObjectReference : managedByRef ? IrCanonicalValueKind.ManagedByRef : IrCanonicalValueKind.Integer, width, signed),
            objectReference ? IrVirtualValueClass.ManagedObjectReference : managedByRef ? IrVirtualValueClass.ManagedByRef : IrVirtualValueClass.ScalarInteger,
            new(width, 1, false, true, HybridCpuArchitecturalRegisterClass.ScalarInteger64, null, fixedRegisterId, 0,
                objectReference ? ["restricted-cil-scalar", "managed-object-reference"] : managedByRef ? ["restricted-cil-scalar", "managed-byref-receiver-loan"] : ["restricted-cil-scalar"],
                null, registers, groups, target.ContractDigest, null));
    }

    private static IrCanonicalTypeV1 ToCanonicalType(RestrictedCilTypeV1 type) =>
        type == RestrictedCilTypeV1.Void
            ? new(IrCanonicalValueKind.Opaque, 0, false)
            : new(type == RestrictedCilTypeV1.ObjectReference ? IrCanonicalValueKind.ManagedObjectReference :
                type == RestrictedCilTypeV1.ManagedByRef ? IrCanonicalValueKind.ManagedByRef : IrCanonicalValueKind.Integer,
                Width(type), IsSigned(type));

    private static HybridCpuDataType ToDataType(RestrictedCilTypeV1 type) => type switch
    {
        RestrictedCilTypeV1.Boolean or RestrictedCilTypeV1.UInt8 => HybridCpuDataType.UINT8,
        RestrictedCilTypeV1.Int8 => HybridCpuDataType.INT8,
        RestrictedCilTypeV1.UInt16 => HybridCpuDataType.UINT16,
        RestrictedCilTypeV1.Int16 => HybridCpuDataType.INT16,
        RestrictedCilTypeV1.UInt32 => HybridCpuDataType.UINT32,
        RestrictedCilTypeV1.Int32 => HybridCpuDataType.INT32,
        RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.NativeInt => HybridCpuDataType.INT64,
        _ => HybridCpuDataType.UINT64
    };

    private static int Width(RestrictedCilTypeV1 type) => type switch
    {
        RestrictedCilTypeV1.Boolean or RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.UInt8 => 8,
        RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16 => 16,
        RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.UInt32 => 32,
        RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.NativeInt or RestrictedCilTypeV1.NativeUInt or RestrictedCilTypeV1.ObjectReference or RestrictedCilTypeV1.ManagedByRef => 64,
        _ => 0
    };

    private static bool IsSigned(RestrictedCilTypeV1 type) => type is RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.NativeInt;
    private static bool IsArithmeticType(RestrictedCilTypeV1 type) => type is >= RestrictedCilTypeV1.Boolean and <= RestrictedCilTypeV1.NativeUInt;
    private static bool IsConditionType(RestrictedCilTypeV1 type) => IsArithmeticType(type) || type == RestrictedCilTypeV1.ObjectReference;
    private static bool IsEqualityType(RestrictedCilTypeV1 type) => IsConditionType(type);
    private static bool StackCompatible(RestrictedCilTypeV1 actual, RestrictedCilTypeV1 declared) =>
        actual == declared || (actual == RestrictedCilTypeV1.Int64 && declared == RestrictedCilTypeV1.UInt64) ||
        (actual == RestrictedCilTypeV1.Int32 && declared is RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.Boolean or RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.UInt8 or RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16);
    private static RestrictedCilTypeV1 StackType(RestrictedCilTypeV1 type) => type is
        RestrictedCilTypeV1.Boolean or RestrictedCilTypeV1.Int8 or RestrictedCilTypeV1.UInt8 or
        RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt16 ? RestrictedCilTypeV1.Int32 : type;
    private static bool IsTerminator(ushort opcode) => opcode is 0x2a or 0x2b or 0x2c or 0x2d or 0x38 or 0x39 or 0x3a;
    private static bool TryPop<T>(List<T> stack, out T value) { if (stack.Count == 0) { value = default!; return false; } value = stack[^1]; stack.RemoveAt(stack.Count - 1); return true; }
    private static T Pop<T>(List<T> stack) { T value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
    private static string OffsetIdentity(RestrictedCilProvenanceV1 provenance, int offset) =>
        $"cil:{provenance.PeSha256}:{provenance.CanonicalMethodLocalIdentity}:il_{offset:x4}";
    private static string TypeName(RestrictedCilTypeV1 type) => type switch
    {
        RestrictedCilTypeV1.Int32 => "System.Int32",
        RestrictedCilTypeV1.UInt32 => "System.UInt32",
        RestrictedCilTypeV1.Int64 => "System.Int64",
        RestrictedCilTypeV1.UInt64 => "System.UInt64",
        RestrictedCilTypeV1.Int16 => "System.Int16",
        RestrictedCilTypeV1.UInt16 => "System.UInt16",
        RestrictedCilTypeV1.Int8 => "System.SByte",
        RestrictedCilTypeV1.UInt8 => "System.Byte",
        RestrictedCilTypeV1.Boolean => "System.Boolean",
        RestrictedCilTypeV1.NativeInt => "System.IntPtr",
        RestrictedCilTypeV1.NativeUInt => "System.UIntPtr",
        RestrictedCilTypeV1.Void => "System.Void",
        RestrictedCilTypeV1.ObjectReference => "System.Object",
        _ => "unsupported"
    };

    private static string ManagedSignatureTypeName(RestrictedCilTypeV1 type) => type switch
    {
        RestrictedCilTypeV1.Boolean => "boolean",
        RestrictedCilTypeV1.Int8 => "int8",
        RestrictedCilTypeV1.UInt8 => "uint8",
        RestrictedCilTypeV1.Int16 => "int16",
        RestrictedCilTypeV1.UInt16 => "uint16",
        RestrictedCilTypeV1.Int32 => "int32",
        RestrictedCilTypeV1.UInt32 => "uint32",
        RestrictedCilTypeV1.Int64 => "int64",
        RestrictedCilTypeV1.UInt64 => "uint64",
        RestrictedCilTypeV1.NativeInt => "native-int",
        RestrictedCilTypeV1.NativeUInt => "native-uint",
        RestrictedCilTypeV1.ObjectReference => "object-reference",
        RestrictedCilTypeV1.Void => "void",
        _ => "unsupported"
    };

    private static ulong ManagedSignatureId(IReadOnlyList<RestrictedCilTypeV1> parameters,
        RestrictedCilTypeV1 returnType) => HybridCpuPlatformContractV1.ComputeManagedCallSignatureId(
        $"{ManagedSignatureTypeName(returnType)}({string.Join(',', parameters.Select(ManagedSignatureTypeName))})");

    private static DecodeResult InvalidDecode(string message, RestrictedCilProvenanceV1 provenance, int offset) =>
        new([], Reject(RestrictedCilImportStatusV1.InvalidInput, "HCCIL0028", message, OffsetIdentity(provenance, offset), provenance));

    private static RestrictedCilImportResultV1 Reject(
        RestrictedCilImportStatusV1 status,
        string code,
        string message,
        string? identity = null,
        RestrictedCilProvenanceV1? provenance = null) =>
        new(status, null, [new(code, message, identity)], provenance);

    private sealed record MethodSelection(TypeDefinitionHandle Type, MethodDefinitionHandle Method, RestrictedCilImportResultV1? Failure);
    private static IEnumerable<int> ControlFlowTargets(DecodedInstruction instruction)
    {
        if (instruction.BranchTarget >= 0) yield return instruction.BranchTarget;
        if (instruction.SwitchTargets is not null)
            foreach (int target in instruction.SwitchTargets) yield return target;
    }

    private sealed record DecodedInstruction(int Offset, int Size, ushort Encoding, string Name, long Literal, int BranchTarget, int Token,
        StaticScalarProjection? Projection = null, ArrayZeroProjection? ArrayZero = null, IReadOnlyList<int>? SwitchTargets = null,
        int ErasedReceiverDepth = 0, DecodedInstruction? NullableDefault = null, bool NullableWide = false, bool NullableUnsigned = false, int NullableNarrowBits = 0, bool NullableChar = false, bool NullableBoolean = false, string? NullableEnumIdentity = null, bool NullableParameter = false);
    private sealed record DecodeResult(IReadOnlyList<DecodedInstruction> Instructions, RestrictedCilImportResultV1? Failure);
    private sealed record VerifyResult(IReadOnlyDictionary<int, ResolvedHelper> Helpers, RestrictedCilImportResultV1? Failure);
    private sealed record ResolvedHelper(string Identity, RestrictedCilHelperContractV1 Contract, bool IsManagedCall,
        RestrictedCilDispatchBindingV1? Dispatch = null, string? EmptyArrayTypeIdentity = null,
        int? EmptyValueElementToken = null, string? EmptyValueElementIdentity = null,
        MetadataStorage? EmptyValueStorage = null, string? AggregateReturn = null,
        IReadOnlyList<string?>? AggregateParameters = null, ManagedReceiverAbiPlanV1? Receiver = null);
    private sealed record HelperResolution(ResolvedHelper? Helper, RestrictedCilImportResultV1? Failure);
    private sealed record FieldResolution(RestrictedCilFieldLayoutBindingV1? Field, RestrictedCilImportResultV1? Failure,
        string? AggregateIdentity = null);
    private sealed record AllocationResolution(RestrictedCilAllocationBindingV1? Allocation, ResolvedHelper? Constructor,
        RestrictedCilImportResultV1? Failure);
    private enum SignatureStatus : byte { Success, Unsupported, Invalid }
    private sealed record MethodSignature(SignatureStatus Status, RestrictedCilTypeV1 ReturnType, IReadOnlyList<RestrictedCilTypeV1> Parameters, bool HasThis, string Message,
        string? AggregateReturn = null, IReadOnlyList<string?>? AggregateParameters = null, ManagedReceiverAbiPlanV1? Receiver = null,
        IReadOnlyList<ConstructorReferenceKind>? ConstructorReferenceParameters = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? ExactMethodArguments = null)
    {
        public static MethodSignature Invalid(string message) => new(SignatureStatus.Invalid, RestrictedCilTypeV1.Invalid, [], false, message);
        public static MethodSignature Unsupported(string message) => new(SignatureStatus.Unsupported, RestrictedCilTypeV1.UnsupportedManaged, [], false, message);
    }
    private sealed record LocalSignature(SignatureStatus Status, IReadOnlyList<RestrictedCilTypeV1> Types, string Message,
        IReadOnlyList<string?>? Aggregates = null, IReadOnlyList<ConstructorReferenceKind>? ConstructorReferences = null,
        IReadOnlyDictionary<string, ExternalEnumCarrier>? EnumDeclarations = null,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? ExactMethodArguments = null)
    {
        public static LocalSignature Invalid(string message) => new(SignatureStatus.Invalid, [], message);
        public static LocalSignature Unsupported(string message) => new(SignatureStatus.Unsupported, [], message);
    }
    private sealed record Emission(
        int CilOffset,
        HybridCpuOpcode Opcode,
        RestrictedCilTypeV1 Type,
        IReadOnlyList<IrOperand> Uses,
        IReadOnlyList<IrOperand> Defs,
        int BranchTarget,
        string Identity,
        string? CallTargetIdentity = null,
        IrMemoryEffectKind MemoryEffects = IrMemoryEffectKind.None,
        bool IsIndirectCall = false,
        string? FixedFrameSlotIdentity = null,
        string? BranchTargetSymbolName = null,
        LongBranchRelocationPart LongBranchPart = LongBranchRelocationPart.None,
        bool IsManagedGcSafepoint = true);
    private enum LongBranchRelocationPart : byte
    {
        None,
        PcRelativeHigh,
        PcRelativeLow,
        ManagedCallPcRelativeHigh,
        ManagedCallPcRelativeLow
    }
    private sealed record ValueRef(RestrictedCilTypeV1 Type, IrOperand Operand)
    {
        public static ValueRef Argument(int index, RestrictedCilTypeV1 type, string method) =>
            new(type, new(IrOperandKind.VirtualValue, StableNumber($"{method}:arg:{index}"), $"cil:{method}:arg:{index}"));
        public static ValueRef Constant(long value, RestrictedCilTypeV1 type, string identity) =>
            new(type, new(IrOperandKind.Constant, unchecked((ulong)value), $"{identity}:constant"));
        public static ValueRef Definition(RestrictedCilTypeV1 type, string identity) =>
            new(type, new(IrOperandKind.VirtualValue, StableNumber($"{identity}:result"), $"{identity}:result"));
        private static ulong StableNumber(string identity) => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
