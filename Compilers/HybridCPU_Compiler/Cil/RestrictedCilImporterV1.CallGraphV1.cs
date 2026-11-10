using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    public const int MaximumGraphDiagnosticUtf8Bytes = 2048;
    private const string ManagedCallGraphContractText =
        "hybridcpu.managed-call-graph/v1|stable-method-identity|bounded-body-world|direct-static-call|direct-constructor-edge|exact-bounded-dispatch-candidates|managed-function-pointer-address-reachability-nonexecution-edge|type-initializer-reachability-nonreentrant-edge|stable-worklist|ordered-edges|tarjan-scc|v1-recursion-reject|callee-first-order|metadata-only-32MiB-64modules|closed-interface-declaration-plans|nested-aggregate-signature-resolution|selected-body-constructed-type-scope/v1|aggregate-pre-ir-gate|no-runtime-authority";
    private const string ManagedBoundedRecursionExtensionText =
        "hybridcpu.managed-call-graph/v1+bounded-recursion-v1|int32-countdown-proof|constant-ingress|post-ra-stack-bound|compile-time-overflow-rejection|no-runtime-authority";

    public ManagedCallGraphCompilationV1 ImportBodyWorld(ManagedBodyWorldV1 bodyWorld)
        => ImportBodyWorld(bodyWorld, CancellationToken.None);

    /// <summary>Observes caller cancellation between graph/method stages; publishes no partial graph.</summary>
    public ManagedCallGraphCompilationV1 ImportBodyWorld(ManagedBodyWorldV1 bodyWorld, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bodyWorld);
        if (_mode != RestrictedCilImportModeV1.ScalarControlFlowV2)
            return GraphFailure(RestrictedCilImportStatusV1.InvalidInput, "HCSCF-BODYWORLD0001",
                "Managed body-world import requires ScalarControlFlowV2 mode.");
        if (!Enum.IsDefined(bodyWorld.Mode) || bodyWorld.PeImage.IsEmpty ||
            string.IsNullOrWhiteSpace(bodyWorld.SourceIdentity) || bodyWorld.Roots is null ||
            bodyWorld.PresentedMethods is null || bodyWorld.Roots.Count == 0)
            return GraphFailure(RestrictedCilImportStatusV1.InvalidInput, "HCSCF-BODYWORLD0002",
                "A non-empty PE image, stable source identity and at least one root are required.");

        ScalarControlFlowV2Budgets budgets = _graphBudgets;
        if (!budgets.IsValid)
            return GraphFailure(RestrictedCilImportStatusV1.InvalidInput, "HCSCF-BODYWORLD0004",
                "Managed call-graph budgets must be positive and deterministic.");
        long totalPeBytes = bodyWorld.PeImage.Length + (bodyWorld.DependencyModules ?? [])
            .Sum(static module => (long)module.PeImage.Length);
        if (totalPeBytes > budgets.MaximumPeBytes)
            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2001",
                "The body-world PE exceeds the deterministic byte budget.");
        if ((bodyWorld.MetadataOnlyModules?.Count ?? 0) > 64 ||
            (bodyWorld.MetadataOnlyModules ?? []).Sum(static module => (long)module.PeImage.Length) > 32 * 1024 * 1024)
            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2010",
                "Metadata-only inputs exceed 64 modules or 32 MiB; they have no body-world authority.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ManagedModuleContext primary = new ManagedModuleContext(bodyWorld.PeImage, bodyWorld.SourceIdentity);
            using ManagedModuleContextCollection dependencies = new ManagedModuleContextCollection((bodyWorld.DependencyModules ?? [])
                .Select(module => {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ManagedModuleContext(module.PeImage, module.SourceIdentity);
                }), cancellationToken);
            ManagedModuleContext[] modules = [primary, .. dependencies.Modules];
            using ManagedModuleContextCollection metadataOnly = new ManagedModuleContextCollection((bodyWorld.MetadataOnlyModules ?? [])
                .Select(module => {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ManagedModuleContext(module.PeImage, module.SourceIdentity);
                }), cancellationToken);
            ManagedModuleContext[] metadataModules = [.. modules, .. metadataOnly.Modules];
            if (metadataModules.Any(static module => !module.Reader.HasMetadata) ||
                metadataModules.Select(static module => module.AssemblyName).Distinct(StringComparer.Ordinal).Count() != metadataModules.Length)
                return GraphFailure(RestrictedCilImportStatusV1.InvalidInput, "HCSCF-BODYWORLD0003",
                    "Every body-world module must be a managed PE with a unique assembly identity.");
            ManagedMetadataBindingSet metadataBindings = BuildMetadataBindings(modules, metadataOnly.Modules);
            IReadOnlyDictionary<string, ExternalEnumCarrier> enumDeclarations = CollectEnumCarriers(metadataModules, cancellationToken);
            IReadOnlyDictionary<string, ScalarValueDeclaration> scalarDeclarations = CollectScalarValueDeclarations(modules, metadataBindings, cancellationToken);
            Dictionary<string, RestrictedCilFieldLayoutBindingV1> effectiveFields = new Dictionary<string, RestrictedCilFieldLayoutBindingV1>(
                metadataBindings.Fields, StringComparer.Ordinal);
            foreach ((string key, RestrictedCilFieldLayoutBindingV1 value) in _fieldLayouts)
                effectiveFields[key] = value;
            Dictionary<string, RestrictedCilTypeInitializationBindingV1> effectiveInitializers = new Dictionary<string, RestrictedCilTypeInitializationBindingV1>(
                metadataBindings.TypeInitializers, StringComparer.Ordinal);
            foreach ((string key, RestrictedCilTypeInitializationBindingV1 value) in _typeInitializationBindings)
                effectiveInitializers[key] = value;
            MetadataReader metadata = primary.Metadata;
            SortedDictionary<string, ManagedGraphNode> nodesByIdentity = new SortedDictionary<string, ManagedGraphNode>(StringComparer.Ordinal);
            Dictionary<string, IReadOnlyList<DecodedInstruction>> decodedByIdentity = new Dictionary<string, IReadOnlyList<DecodedInstruction>>(StringComparer.Ordinal);
            Dictionary<string, ManagedEhMethodPlanV1> ehPlansByIdentity = new Dictionary<string, ManagedEhMethodPlanV1>(StringComparer.Ordinal);
            ManagedVirtualSlotPlanV1? exceptionMessagePlan = null;
            Dictionary<ulong, (string Owner, string Declaration)> closedVirtualSlots = new Dictionary<ulong, (string Owner, string Declaration)>();
            Dictionary<ulong, ManagedVirtualSlotPlanV1> closedVirtualPlans = new Dictionary<ulong, ManagedVirtualSlotPlanV1>();
            Dictionary<(string Assembly, int Token), RestrictedCilDispatchBindingV1> metadataDispatch = new Dictionary<(string Assembly, int Token), RestrictedCilDispatchBindingV1>();
            List<ManagedDispatchCallPlanV1> dispatchCallPlans = new List<ManagedDispatchCallPlanV1>();
            HashSet<(string AssemblyName, int Token)> presentedTokens = new HashSet<(string AssemblyName, int Token)>();
            int resolutionSteps = 0;

            ManagedGraphNode? ResolveSelector(RestrictedCilMethodSelectorV1 selector, out ManagedCallGraphCompilationV1? failure)
            {
                if (++resolutionSteps > budgets.MaximumMetadataResolutionSteps)
                {
                    failure = GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2002",
                        "The metadata-resolution budget was exhausted.");
                    return null;
                }
                MethodSelection selection = SelectMethod(metadata, selector);
                if (selection.Failure is not null)
                {
                    failure = FromImportFailure(selection.Failure);
                    return null;
                }
                failure = null;
                return ResolveDefinition(primary, selection.Type, selection.Method,
                    selector.GenericTypeArguments ?? [], selector.GenericMethodArguments ?? []);
            }

            ManagedGraphNode ResolveDefinition(
                ManagedModuleContext module,
                TypeDefinitionHandle typeHandle,
                MethodDefinitionHandle methodHandle,
                IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments = null,
                IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments = null)
            {
                typeArguments ??= [];
                methodArguments ??= [];
                MetadataReader moduleMetadata = module.Metadata;
                MethodDefinition method = moduleMetadata.GetMethodDefinition(methodHandle);
                MethodSignature signature = ParseMethodSignature(moduleMetadata, method.Signature, typeArguments, methodArguments, allowAggregates: true);
                signature = BindValueReceiver(moduleMetadata, typeHandle, signature);
                signature = ProjectScalarValueSignature(moduleMetadata, signature,
                    metadataBindings.AllocationsByAssembly.GetValueOrDefault(module.AssemblyName) ?? [], enumDeclarations, scalarDeclarations);
                ManagedMethodIdentityV1 identity = CreateManagedMethodIdentity(moduleMetadata, typeHandle, methodHandle, signature,
                    typeArguments, methodArguments);
                if (nodesByIdentity.TryGetValue(identity.StableIdentity, out ManagedGraphNode? existing)) return existing;
                ManagedGraphNode node = new ManagedGraphNode(module, typeHandle, methodHandle, identity, signature, typeArguments, methodArguments);
                nodesByIdentity.Add(identity.StableIdentity, node);
                return node;
            }

            if (bodyWorld.Mode == ManagedBodyWorldModeV1.AdapterPresented)
            {
                foreach (RestrictedCilMethodSelectorV1 selector in bodyWorld.PresentedMethods)
                {
                    MethodSelection selection = SelectMethod(metadata, selector);
                    if (selection.Failure is not null) return FromImportFailure(selection.Failure);
                    presentedTokens.Add((primary.AssemblyName, MetadataTokens.GetToken(selection.Method)));
                }
                foreach (ManagedModuleContext dependency in dependencies.Modules)
                    foreach (MethodDefinitionHandle methodHandle in dependency.Metadata.MethodDefinitions)
                        if (dependency.Metadata.GetMethodDefinition(methodHandle).RelativeVirtualAddress != 0)
                            presentedTokens.Add((dependency.AssemblyName, MetadataTokens.GetToken(methodHandle)));
            }

            List<ManagedGraphNode> roots = new List<ManagedGraphNode>();
            foreach (RestrictedCilMethodSelectorV1 selector in bodyWorld.Roots)
            {
                ManagedGraphNode? root = ResolveSelector(selector, out ManagedCallGraphCompilationV1? failure);
                if (failure is not null) return failure;
                if (bodyWorld.Mode == ManagedBodyWorldModeV1.AdapterPresented &&
                    !presentedTokens.Contains((root!.Module.AssemblyName, root.Identity.MetadataToken)))
                    return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1001",
                        "A root was not presented by the adapter body world.", root.Identity.StableIdentity);
                roots.Add(root!);
            }

            SortedSet<string> pending = new SortedSet<string>(roots.Select(static root => root.Identity.StableIdentity), StringComparer.Ordinal);
            HashSet<string> processed = new HashSet<string>(StringComparer.Ordinal);
            List<ManagedCallGraphEdgeV1> edges = new List<ManagedCallGraphEdgeV1>();
            int totalInstructions = 0;
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string identity = pending.Min!;
                pending.Remove(identity);
                if (!processed.Add(identity)) continue;
                ManagedGraphNode caller = nodesByIdentity[identity];
                MetadataReader callerMetadata = caller.Module.Metadata;
                MethodDefinition method = callerMetadata.GetMethodDefinition(caller.MethodHandle);
                RestrictedCilProvenanceV1 provenance = new RestrictedCilProvenanceV1(caller.Module.SourceIdentity, caller.Module.ImageDigest,
                    caller.Identity.StableIdentity, $"0x{caller.Identity.MetadataToken:x8}", _matrix.ContractDigest,
                    RestrictedCilSupportMatrixV1.OptionsDigest(_budgets),
                    $"{caller.Identity.DeclaringType}.{caller.Identity.MethodName}");
                RestrictedCilImportResultV1? envelope = ValidateMethodEnvelope(callerMetadata, caller.TypeHandle, method, provenance,
                    caller.GenericTypeArguments, caller.GenericMethodArguments, effectiveInitializers,
                    discoverNullableGenericConstraint: caller.Module.ImageDigest == primary.ImageDigest &&
                        !roots.Any(root => root.Identity.StableIdentity == caller.Identity.StableIdentity));
                if (envelope is not null) return FromImportFailure(envelope);
                if (caller.Signature.Status != SignatureStatus.Success)
                    return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-CALL1001",
                        caller.Signature.Message, caller.Identity.StableIdentity);
                MethodBodyBlock body = caller.Module.Reader.GetMethodBody(method.RelativeVirtualAddress);
                if (caller.Signature.AggregateReturn == NullableInt32Identity &&
                    (caller.Module.ImageDigest != primary.ImageDigest || roots.Any(root => root.Identity.StableIdentity == caller.Identity.StableIdentity) ||
                     !IsExactNullableInt32Return(callerMetadata, caller.Module.Reader, caller.MethodHandle, caller.Signature, caller.GenericTypeArguments, caller.GenericMethodArguments)))
                    return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-CALL1001",
                        "Managed references, floating point, aggregates and extended signature types are not qualified.", caller.Identity.StableIdentity);
                if (caller.Signature.Receiver is not null && body.ExceptionRegions.Length != 0)
                    return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCCIL1841",
                        "Receiver loan cannot cross EH entries or safepoints without a byref GC/lifetime contract.", caller.Identity.StableIdentity);
                if (body.ExceptionRegions.Length != 0)
                {
                    RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1(caller.Identity.DeclaringType,
                        caller.Identity.MethodName, caller.Identity.MetadataToken,
                        caller.GenericTypeArguments, caller.GenericMethodArguments);
                    ManagedEhImportResultV1 ehImport = ImportManagedEhPlanCore(caller.Module.PeImage, selector, cancellationToken, caller.Module);
                    if (ehImport.Status != ManagedEhImportStatusV1.Success || ehImport.Plan is null)
                    {
                        return GraphFailure(ehImport.Status == ManagedEhImportStatusV1.BudgetExhausted
                                ? RestrictedCilImportStatusV1.BudgetExhausted
                                : ehImport.Status == ManagedEhImportStatusV1.InvalidInput
                                    ? RestrictedCilImportStatusV1.InvalidInput
                                    : RestrictedCilImportStatusV1.Unsupported,
                            string.IsNullOrWhiteSpace(ehImport.Code) ? "HCSCF-EH1800" : ehImport.Code,
                            string.IsNullOrWhiteSpace(ehImport.Reason)
                                ? "Managed EH plan import failed without an exact diagnostic."
                                : ehImport.Reason,
                            caller.Identity.StableIdentity);
                    }
                    ehPlansByIdentity.Add(caller.Identity.StableIdentity, ehImport.Plan);
                }
                ManagedCallGraphCompilationV1? getterProofFailure = null;
                Dictionary<(int Token, string Identity), bool> provenGetters = new Dictionary<(int Token, string Identity), bool>();
                bool ExternalGetterProof(int token, string identity, RestrictedCilTypeV1 carrier) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (carrier is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64) ||
                        !scalarDeclarations.TryGetValue(identity, out ScalarValueDeclaration? declaration) || declaration.Carrier != carrier) return false;
                    if (provenGetters.TryGetValue((token, identity), out bool previous)) return previous;
                    if (++resolutionSteps > budgets.MaximumMetadataResolutionSteps) {
                        getterProofFailure = GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2002",
                            "The metadata-resolution budget was exhausted.", caller.Identity.StableIdentity);
                        return false;
                    }
                    bool valid = TryResolveManagedDefinition(modules, caller.Module, token, caller.GenericTypeArguments,
                        caller.GenericMethodArguments, out ManagedModuleContext? getterModule, out TypeDefinitionHandle getterType, out MethodDefinitionHandle getterMethod,
                        out IReadOnlyList<RestrictedCilGenericArgumentV1>? getterTypeArguments, out IReadOnlyList<RestrictedCilGenericArgumentV1>? getterArguments, out _) &&
                        getterTypeArguments.Count == 0 && getterArguments.Count == 0 &&
                        getterModule.ImageDigest == declaration.DefinitionImageDigest &&
                        AggregateIdentity(getterModule.Metadata, getterType) == identity &&
                        IsExactLocalScalarGetter(getterModule.Metadata, getterModule.Reader, MetadataTokens.GetToken(getterMethod),
                            identity, carrier);
                    provenGetters[(token, identity)] = valid;
                    return valid;
                }
                DecodeResult decoded = Decode(body.GetILBytes() ?? [], provenance, callerMetadata, caller.Module.Reader,
                    body.ExceptionRegions.Length == 0, caller.Signature, ExternalGetterProof,
                    ParseLocalSignature(callerMetadata, body.LocalSignature, caller.GenericTypeArguments, caller.GenericMethodArguments, allowAggregates: true, enumDeclarations: enumDeclarations), deferNullableArgumentCalls: true);
                if (getterProofFailure is not null) return getterProofFailure;
                if (decoded.Failure is not null) return FromImportFailure(decoded.Failure);
                RestrictedCilImportResultV1? receiverBodyFailure = ValidateReceiverBody(caller.Signature, decoded.Instructions, provenance);
                if (receiverBodyFailure is not null) return FromImportFailure(receiverBodyFailure);
                decodedByIdentity.Add(caller.Identity.StableIdentity, decoded.Instructions);
                totalInstructions = checked(totalInstructions + decoded.Instructions.Count);
                if (totalInstructions > budgets.MaximumIlInstructionsProgram)
                    return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2003",
                        "The aggregate IL-instruction budget was exhausted.", caller.Identity.StableIdentity);

                string StaticFieldOwner(DecodedInstruction instruction)
                {
                    EntityHandle field = MetadataTokens.EntityHandle(instruction.Token);
                    if (field.Kind == HandleKind.MemberReference)
                    {
                        MemberReference member = callerMetadata.GetMemberReference((MemberReferenceHandle)field);
                        return member.GetKind() == MemberReferenceKind.Field ? ParentTypeName(callerMetadata, member.Parent) : string.Empty;
                    }
                    if (field.Kind != HandleKind.FieldDefinition) return string.Empty;
                    TypeDefinitionHandle owner = callerMetadata.TypeDefinitions.FirstOrDefault(type =>
                        callerMetadata.GetTypeDefinition(type).GetFields().Contains((FieldDefinitionHandle)field));
                    return owner.IsNil ? string.Empty : FullTypeName(callerMetadata, owner);
                }
                DecodedInstruction[] staticFields = decoded.Instructions.Where(static instruction => instruction.Encoding is 0x7e or 0x7f or 0x80).ToArray();
                if (staticFields.Length > budgets.MaximumMetadataResolutionSteps - resolutionSteps)
                    return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2002",
                        "Static initializer metadata-resolution budget was exhausted.", caller.Identity.StableIdentity);
                resolutionSteps += staticFields.Length;
                foreach (string initializerOwner in new[] { caller.Identity.DeclaringType }.Concat(decoded.Instructions
                    .Where(static instruction => instruction.Projection is not null).Select(static instruction => instruction.Projection!.OwnerType))
                    .Concat(staticFields.Select(StaticFieldOwner))
                    .Distinct(StringComparer.Ordinal))
                if (effectiveInitializers.TryGetValue(initializerOwner,
                        out RestrictedCilTypeInitializationBindingV1? initializer) &&
                    !initializer.RuntimePreinitialized)
                {
                        (ManagedModuleContext Module, TypeDefinitionHandle Type)[] owners = modules.SelectMany(module => module.Metadata.TypeDefinitions
                        .Where(type => FullTypeName(module.Metadata, type) == initializerOwner)
                        .Select(type => (Module: module, Type: type))).ToArray();
                    if (owners.Length != 1)
                        return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-TYPEINIT1001",
                            "A required static constructor has no unique admitted body owner.", caller.Identity.StableIdentity);
                        ManagedModuleContext initializerModule = owners[0].Module;
                    MethodDefinitionHandle initializerHandle = MetadataTokens.MethodDefinitionHandle(
                        initializer.InitializerMetadataToken & 0x00ffffff);
                    TypeDefinitionHandle initializerType = owners[0].Type;
                    if (!initializerModule.Metadata.GetTypeDefinition(initializerType).GetMethods().Contains(initializerHandle) ||
                        initializerModule.Metadata.GetString(initializerModule.Metadata.GetMethodDefinition(initializerHandle).Name) != ".cctor")
                        return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-TYPEINIT1001",
                            "Static constructor token does not belong to its admitted owner.", caller.Identity.StableIdentity);
                    if (ReferenceEquals(caller.Module, initializerModule) && caller.Identity.MetadataToken == initializer.InitializerMetadataToken)
                        continue;
                    ManagedGraphNode initializerNode = ResolveDefinition(initializerModule, initializerType,
                        initializerHandle);
                    if (bodyWorld.Mode == ManagedBodyWorldModeV1.AdapterPresented &&
                        !presentedTokens.Contains((initializerNode.Module.AssemblyName,
                            initializerNode.Identity.MetadataToken)))
                        return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1003",
                            "A required static-constructor body was not presented by the adapter.",
                            initializerNode.Identity.StableIdentity);
                    string initializerEdgeId =
                        $"{caller.Identity.IdentityDigest}:type-init->{initializerNode.Identity.IdentityDigest}";
                    edges.Add(new(initializerEdgeId, caller.Identity.StableIdentity,
                        initializerNode.Identity.StableIdentity, -1, initializer.InitializerMetadataToken,
                        IsTypeInitializerTarget: true));
                    pending.Add(initializerNode.Identity.StableIdentity);
                }

                DecodedInstruction[] calls = decoded.Instructions.Where(static instruction =>
                        instruction.Encoding is 0x28 or 0x6f or 0x73 or 0xfe06 or 0xfe07)
                    .OrderBy(static instruction => instruction.Offset).ToArray();
                if (calls.Length > budgets.MaximumOutgoingCallsPerMethod)
                    return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2004",
                        "The per-method outgoing-call budget was exhausted.", caller.Identity.StableIdentity);
                foreach (DecodedInstruction call in calls)
                {
                    if (call.Encoding == 0x6f && _delegateInvokeBindings.ContainsKey(call.Token))
                        continue;
                    if (call.Encoding == 0x73 && _delegateCreationBindings.ContainsKey(
                            (caller.Identity.MetadataToken, call.Token, call.Offset)))
                        continue;
                    if (call.Encoding is 0xfe06 or 0xfe07)
                    {
                        if (!_functionPointerBindings.TryGetValue(call.Token,
                                out RestrictedCilFunctionPointerBindingV1? functionPointer) ||
                            (call.Encoding == 0xfe07) != functionPointer.VirtualSlotMetadataToken.HasValue)
                            return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-CALL1010",
                                "ldftn/ldvirtftn requires one exact managed function-pointer binding.",
                                OffsetIdentity(provenance, call.Offset));
                        if (++resolutionSteps > budgets.MaximumMetadataResolutionSteps)
                            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2002",
                                "The metadata-resolution budget was exhausted.", caller.Identity.StableIdentity);
                        if (!TryResolveManagedDefinition(modules, caller.Module, call.Token, caller.GenericTypeArguments,
                                caller.GenericMethodArguments, out ManagedModuleContext pointerModule, out TypeDefinitionHandle pointerType,
                                out MethodDefinitionHandle pointerMethod, out IReadOnlyList<RestrictedCilGenericArgumentV1> pointerTypeArguments,
                                out IReadOnlyList<RestrictedCilGenericArgumentV1> pointerArguments,
                                out string pointerReason))
                            return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1002",
                                pointerReason, OffsetIdentity(provenance, call.Offset));
                        MethodDefinition pointerDefinition = pointerModule.Metadata.GetMethodDefinition(pointerMethod);
                        MethodSignature pointerSignature = ParseMethodSignature(pointerModule.Metadata, pointerDefinition.Signature, pointerTypeArguments, pointerArguments);
                        RestrictedCilTypeV1[] pointerParameters = pointerSignature.Parameters.Select(StackType).ToArray();
                        if (pointerSignature.Status != SignatureStatus.Success ||
                            pointerSignature.HasThis != functionPointer.IsInstanceMethod ||
                            !pointerParameters.SequenceEqual(functionPointer.ParameterTypes) ||
                            StackType(pointerSignature.ReturnType) != functionPointer.ReturnType)
                            return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-CALL1011",
                                "The managed function-pointer binding does not match the exact target signature.",
                                OffsetIdentity(provenance, call.Offset));
                        ManagedGraphNode pointerNode = ResolveDefinition(pointerModule, pointerType, pointerMethod, pointerTypeArguments, pointerArguments);
                        if (bodyWorld.Mode == ManagedBodyWorldModeV1.AdapterPresented &&
                            !presentedTokens.Contains((pointerNode.Module.AssemblyName, pointerNode.Identity.MetadataToken)))
                            return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1003",
                                "A managed function-pointer target body was not presented by the adapter.",
                                pointerNode.Identity.StableIdentity);
                        string pointerEdgeId = $"{caller.Identity.IdentityDigest}:{call.Offset:x4}:address->{pointerNode.Identity.IdentityDigest}";
                        edges.Add(new(pointerEdgeId, caller.Identity.StableIdentity, pointerNode.Identity.StableIdentity,
                            call.Offset, call.Token, false, true));
                        pending.Add(pointerNode.Identity.StableIdentity);
                        if (edges.Count > budgets.MaximumCallEdges)
                            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2005",
                                "The total reachability-edge budget was exhausted.");
                        if (nodesByIdentity.Count > budgets.MaximumReachableMethods)
                            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2006",
                                $"The reachable-method count {nodesByIdentity.Count} exceeds the closed limit {budgets.MaximumReachableMethods}.");
                        continue;
                    }
                    if (call.Encoding == 0x6f)
                    {
                        if (TryResolveManagedDefinition(modules, caller.Module, call.Token,
                                caller.GenericTypeArguments, caller.GenericMethodArguments,
                                out ManagedModuleContext exactModule, out TypeDefinitionHandle exactType,
                                out MethodDefinitionHandle exactMethod,
                                out IReadOnlyList<RestrictedCilGenericArgumentV1> exactTypeArguments,
                                out IReadOnlyList<RestrictedCilGenericArgumentV1> exactMethodArguments,
                                out _) && IsExactCallvirtTarget(exactModule.Metadata, exactType, exactMethod))
                        {
                            ManagedGraphNode exactCallee = ResolveDefinition(exactModule, exactType, exactMethod,
                                exactTypeArguments, exactMethodArguments);
                            if (bodyWorld.Mode == ManagedBodyWorldModeV1.AdapterPresented &&
                                !presentedTokens.Contains((exactCallee.Module.AssemblyName,
                                    exactCallee.Identity.MetadataToken)))
                                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1003",
                                    "An exact callvirt target body was not presented by the adapter.",
                                    exactCallee.Identity.StableIdentity);
                            string exactEdgeId = $"{caller.Identity.IdentityDigest}:{call.Offset:x4}:exact-callvirt->{exactCallee.Identity.IdentityDigest}";
                            edges.Add(new(exactEdgeId, caller.Identity.StableIdentity,
                                exactCallee.Identity.StableIdentity, call.Offset, call.Token));
                            pending.Add(exactCallee.Identity.StableIdentity);
                            if (edges.Count > budgets.MaximumCallEdges)
                                return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted,
                                    "HCSCF-BUDGET2005", "The total call-edge budget was exhausted.");
                            if (nodesByIdentity.Count > budgets.MaximumReachableMethods)
                                return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted,
                                    "HCSCF-BUDGET2006", $"The reachable-method count {nodesByIdentity.Count} exceeds the closed limit {budgets.MaximumReachableMethods}.");
                            continue;
                        }
                        if (!_dispatchBindings.ContainsKey(call.Token) && IsExceptionMessageReference(callerMetadata, call.Token))
                        {
                            try { exceptionMessagePlan ??= BuildExceptionMessageSlotPlan(modules); }
                            catch (NotSupportedException exception)
                            {
                                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-DISPATCH1011",
                                    exception.Message, OffsetIdentity(provenance, call.Offset));
                            }
                            metadataDispatch[(caller.Module.AssemblyName, call.Token)] = ExceptionMessageBinding(call.Token, exceptionMessagePlan);
                        }
                        if (!_dispatchBindings.ContainsKey(call.Token) &&
                            TryCreateTestOnlyClosedWorldIConsoleBinding(modules, metadataModules, caller.Module, call.Token,
                                metadataBindings, out RestrictedCilDispatchBindingV1 testOnlyBinding))
                            metadataDispatch[(caller.Module.AssemblyName, call.Token)] = testOnlyBinding;
                        if (!_dispatchBindings.ContainsKey(call.Token) &&
                            !metadataDispatch.ContainsKey((caller.Module.AssemblyName, call.Token)) &&
                            TryBindClosedVirtual(modules, caller.Module, call.Token, budgets.MaximumMetadataResolutionSteps,
                                ref resolutionSteps, out RestrictedCilDispatchBindingV1? closedVirtual, out string? closedOwner))
                        {
                            metadataDispatch[(caller.Module.AssemblyName, call.Token)] = closedVirtual;
                            if (closedVirtualSlots.TryGetValue(closedVirtual.SlotId, out (string Owner, string Declaration) previousSlot) &&
                                previousSlot != (closedOwner, closedVirtual.StableIdentity))
                                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-DISPATCH1012",
                                    "Closed virtual slot identity collision.", OffsetIdentity(provenance, call.Offset));
                            closedVirtualSlots[closedVirtual.SlotId] = (closedOwner, closedVirtual.StableIdentity);
                        }
                        HelperResolution dispatch = metadataDispatch.TryGetValue((caller.Module.AssemblyName, call.Token), out RestrictedCilDispatchBindingV1? automaticBinding)
                            ? ResolveDispatch(callerMetadata, call.Token, automaticBinding)
                            : ResolveCallvirt(callerMetadata, call.Token);
                        if (dispatch.Failure is not null) return FromImportFailure(dispatch.Failure with
                        {
                            Provenance = provenance,
                            Diagnostics = dispatch.Failure.Diagnostics.Select(diagnostic => diagnostic with
                            {
                                StableSourceIdentity = diagnostic.StableSourceIdentity ?? OffsetIdentity(provenance, call.Offset)
                            }).ToArray()
                        });
                        if (dispatch.Helper!.Dispatch is null) continue;
                        RestrictedCilDispatchBindingV1 dispatchBinding = dispatch.Helper!.Dispatch!;
                        RestrictedCilDispatchCandidateV1[] candidates = dispatchBinding.ExactGenericCandidates is { Count: > 0 }
                            ? dispatchBinding.ExactGenericCandidates.OrderBy(static row => row.MethodMetadataToken)
                                .ThenBy(static row => string.Join(',', (row.GenericTypeArguments ?? [])
                                    .Select(static argument => argument.StableTypeIdentity)), StringComparer.Ordinal)
                                .ThenBy(static row => string.Join(',', (row.GenericMethodArguments ?? [])
                                    .Select(static argument => argument.StableTypeIdentity)), StringComparer.Ordinal).ToArray()
                            : (dispatchBinding.CandidateMethodMetadataTokens ?? []).Distinct().Order()
                                .Select(static token => new RestrictedCilDispatchCandidateV1(token)).ToArray();
                        if (dispatchBinding.RuntimeExternal && candidates.Length == 0)
                        {
                            candidates = FindPrimitiveInterfaceCandidates(modules, metadataModules, caller.Module, call.Token).ToArray();
                            if (candidates.Length != 0)
                            {
                                dispatchBinding = BindPrimitiveInterfaceIds(caller.Module, call.Token, metadataBindings, modules, dispatchBinding, candidates);
                                metadataDispatch[(caller.Module.AssemblyName, call.Token)] = dispatchBinding;
                            }
                        }
                        if (dispatchBinding.RuntimeExternal && candidates.Length == 0)
                        {
                            string externalDigest = Hash(string.Join('|', "hybridcpu.external-dispatch-call/v1",
                                caller.Identity.StableIdentity, call.Offset, dispatchBinding.StableIdentity,
                                dispatchBinding.Kind, dispatchBinding.InterfaceTypeId, dispatchBinding.SlotId));
                            dispatchCallPlans.Add(new(caller.Identity.StableIdentity, call.Offset,
                                dispatchBinding.Kind, dispatchBinding.SlotId, dispatchBinding.InterfaceTypeId ?? 0,
                                [], externalDigest, dispatchBinding.StableIdentity, true));
                            continue;
                        }
                        if (!dispatchBinding.RuntimeExternal && candidates.Length == 0 &&
                            dispatchBinding.ExactImplementationIdentity is not null)
                        {
                            string exactServiceDigest = Hash(string.Join('|',
                                "hybridcpu.exact-external-service-dispatch/v1", caller.Identity.StableIdentity,
                                call.Offset, dispatchBinding.StableIdentity, dispatchBinding.ExactImplementationIdentity,
                                dispatchBinding.InterfaceTypeId, dispatchBinding.SlotId));
                            dispatchCallPlans.Add(new(caller.Identity.StableIdentity, call.Offset,
                                dispatchBinding.Kind, dispatchBinding.SlotId, dispatchBinding.InterfaceTypeId ?? 0,
                                [new("runtime-service", dispatchBinding.ExactImplementationIdentity)],
                                exactServiceDigest, dispatchBinding.StableIdentity, false));
                            continue;
                        }
                        if (candidates.Length == 0)
                            return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-DISPATCH1001",
                                "A dispatch call requires a non-empty exact candidate body set.", OffsetIdentity(provenance, call.Offset));
                        List<ManagedDispatchCandidatePlanV1> dispatchCandidates = new List<ManagedDispatchCandidatePlanV1>();
                        foreach (RestrictedCilDispatchCandidateV1 candidate in candidates)
                        {
                            int candidateToken = candidate.MethodMetadataToken;
                            if (++resolutionSteps > budgets.MaximumMetadataResolutionSteps)
                                return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2002",
                                    "The metadata-resolution budget was exhausted.", caller.Identity.StableIdentity);
                            ManagedModuleContext? candidateSource = candidate.AssemblyName is null ? caller.Module :
                                modules.SingleOrDefault(module => module.AssemblyName == candidate.AssemblyName);
                            if (candidateSource is null)
                                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-DISPATCH1002",
                                    $"Dispatch candidate module '{candidate.AssemblyName}' is absent.", OffsetIdentity(provenance, call.Offset));
                            if (!TryResolveManagedDefinition(modules, candidateSource, candidateToken,
                                    candidate.GenericTypeArguments ?? caller.GenericTypeArguments,
                                    candidate.GenericMethodArguments ?? caller.GenericMethodArguments,
                                    out ManagedModuleContext candidateModule, out TypeDefinitionHandle candidateType,
                                    out MethodDefinitionHandle candidateMethod, out IReadOnlyList<RestrictedCilGenericArgumentV1> candidateTypeArguments,
                                    out IReadOnlyList<RestrictedCilGenericArgumentV1> candidateArguments,
                                    out string candidateReason))
                                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-DISPATCH1002",
                                    candidateReason, OffsetIdentity(provenance, call.Offset));
                            MethodDefinition candidateDefinition = candidateModule.Metadata.GetMethodDefinition(candidateMethod);
                            MethodSignature candidateSignature = ParseMethodSignature(candidateModule.Metadata, candidateDefinition.Signature, candidateTypeArguments, candidateArguments);
                            if (candidateSignature.Status != SignatureStatus.Success || !candidateSignature.HasThis ||
                                candidateDefinition.Attributes.HasFlag(MethodAttributes.Abstract) ||
                                !candidateSignature.Parameters.Select(StackType).SequenceEqual(dispatch.Helper.Contract.ParameterTypes) ||
                                StackType(candidateSignature.ReturnType) != dispatch.Helper.Contract.ReturnType)
                                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-DISPATCH1003",
                                    "A dispatch candidate must be a concrete instance body with the exact bound signature.",
                                    OffsetIdentity(provenance, call.Offset));
                            ManagedGraphNode candidateNode = ResolveDefinition(candidateModule, candidateType, candidateMethod, candidateTypeArguments, candidateArguments);
                            if (bodyWorld.Mode == ManagedBodyWorldModeV1.AdapterPresented &&
                                !presentedTokens.Contains((candidateNode.Module.AssemblyName, candidateNode.Identity.MetadataToken)))
                                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1003",
                                    "A dispatch candidate body was not presented by the adapter.", candidateNode.Identity.StableIdentity);
                            string candidateEdgeId = $"{caller.Identity.IdentityDigest}:{call.Offset:x4}:dispatch->{candidateNode.Identity.IdentityDigest}";
                            edges.Add(new(candidateEdgeId, caller.Identity.StableIdentity, candidateNode.Identity.StableIdentity,
                                call.Offset, candidateToken, true));
                            dispatchCandidates.Add(new(candidate.RuntimeTypeIdentity ?? candidateNode.Identity.DeclaringType,
                                candidateNode.Identity.StableIdentity));
                            pending.Add(candidateNode.Identity.StableIdentity);
                        }
                        ManagedDispatchCandidatePlanV1[] dispatchRows = dispatchCandidates
                            .Distinct().OrderBy(static row => row.RuntimeTypeIdentity, StringComparer.Ordinal)
                            .ThenBy(static row => row.ImplementationIdentity, StringComparer.Ordinal).ToArray();
                        string dispatchDigest = Hash(string.Join('|', "hybridcpu.managed-dispatch-call-plan/v1",
                            caller.Identity.StableIdentity, call.Offset, dispatchBinding.Kind, dispatchBinding.SlotId,
                            dispatchBinding.InterfaceTypeId ?? 0,
                            dispatchBinding.StableIdentity, dispatchBinding.RuntimeExternal,
                            string.Join(';', dispatchRows.Select(static row => $"{row.RuntimeTypeIdentity}:{row.ImplementationIdentity}"))));
                        dispatchCallPlans.Add(new(caller.Identity.StableIdentity, call.Offset, dispatchBinding.Kind,
                            dispatchBinding.SlotId, dispatchBinding.InterfaceTypeId ?? 0, dispatchRows, dispatchDigest,
                            dispatchBinding.StableIdentity, dispatchBinding.RuntimeExternal));
                        if (closedVirtualSlots.TryGetValue(dispatchBinding.SlotId, out (string Owner, string Declaration) slotOwner))
                            closedVirtualPlans[dispatchBinding.SlotId] = new(dispatchBinding.SlotId,
                                slotOwner.Owner, dispatchBinding.StableIdentity,
                                string.Join(',', dispatchBinding.ParameterTypes) + "->" + dispatchBinding.ReturnType,
                                dispatchRows.Select(row => new ManagedVirtualSlotTargetV1(row.RuntimeTypeIdentity,
                                    row.ImplementationIdentity, null, null)).ToArray(), dispatchDigest);
                        if (edges.Count > budgets.MaximumCallEdges)
                            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2005",
                                "The total call-edge budget was exhausted.");
                        if (nodesByIdentity.Count > budgets.MaximumReachableMethods)
                            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2006",
                                $"The reachable-method count {nodesByIdentity.Count} exceeds the closed limit {budgets.MaximumReachableMethods}.");
                        continue;
                    }
                    if (++resolutionSteps > budgets.MaximumMetadataResolutionSteps)
                        return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2002",
                            "The metadata-resolution budget was exhausted.", caller.Identity.StableIdentity);
                    HelperResolution helper = ResolveHelper(callerMetadata, call.Token);
                    if (helper.Helper is { IsManagedCall: false }) continue;
                    if (!TryResolveManagedDefinition(modules, caller.Module, call.Token, caller.GenericTypeArguments,
                            caller.GenericMethodArguments, out ManagedModuleContext calleeModule, out TypeDefinitionHandle calleeType,
                            out MethodDefinitionHandle calleeMethod, out IReadOnlyList<RestrictedCilGenericArgumentV1> calleeTypeArguments,
                            out IReadOnlyList<RestrictedCilGenericArgumentV1> calleeArguments,
                            out string reason))
                    {
                        if (helper.Failure is not null)
                            reason = $"{reason} Helper resolution: {string.Join("; ", helper.Failure.Diagnostics.Select(static diagnostic => diagnostic.Message))}";
                        return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1002",
                            reason, OffsetIdentity(provenance, call.Offset));
                    }
                    ManagedGraphNode callee = ResolveDefinition(calleeModule, calleeType, calleeMethod, calleeTypeArguments, calleeArguments);
                    if (bodyWorld.Mode == ManagedBodyWorldModeV1.AdapterPresented &&
                        !presentedTokens.Contains((callee.Module.AssemblyName, callee.Identity.MetadataToken)))
                        return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-BODYWORLD1003",
                            "A direct managed callee body was not presented by the adapter.", callee.Identity.StableIdentity);
                    string edgeId = $"{caller.Identity.IdentityDigest}:{call.Offset:x4}->{callee.Identity.IdentityDigest}";
                    edges.Add(new(edgeId, caller.Identity.StableIdentity, callee.Identity.StableIdentity,
                        call.Offset, call.Token));
                    pending.Add(callee.Identity.StableIdentity);
                    if (edges.Count > budgets.MaximumCallEdges)
                        return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2005",
                            "The total call-edge budget was exhausted.");
                    if (nodesByIdentity.Count > budgets.MaximumReachableMethods)
                        return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2006",
                            $"The reachable-method count {nodesByIdentity.Count} exceeds the closed limit {budgets.MaximumReachableMethods}.");
                }
            }

            ManagedCallGraphEdgeV1[] orderedEdges = edges.OrderBy(static edge => edge.CallerIdentity, StringComparer.Ordinal)
                .ThenBy(static edge => edge.CallerIlOffset).ThenBy(static edge => edge.CalleeIdentity, StringComparer.Ordinal).ToArray();
            ManagedCallGraphEdgeV1[] executionEdges = orderedEdges
                .Where(static edge => !edge.IsFunctionPointerTarget).ToArray();
            // Type-initializer edges preserve reachability and compilation dependencies, but
            // they are not re-entrant managed calls. The exactly-once state machine suppresses
            // an ensure request while the same initialization chain is already in progress.
            // Including these synthetic edges in Tarjan SCCs therefore invents cycles such as
            // .cctor -> helper -> .cctor which cannot consume another managed stack frame.
            ManagedCallGraphEdgeV1[] callStackEdges = executionEdges
                .Where(static edge => !edge.IsTypeInitializerTarget).ToArray();
            ManagedCallGraphSccV1[] sccs = ComputeManagedSccs(processed, callStackEdges);
            ManagedCallGraphSccV1[] recursiveSccs = sccs.Where(static scc => scc.IsRecursive).ToArray();
            ManagedBoundedRecursionOptionsV1 recursionOptions = bodyWorld.BoundedRecursion ?? ManagedBoundedRecursionOptionsV1.Disabled;
            if (!recursionOptions.IsValid)
                return GraphFailure(RestrictedCilImportStatusV1.InvalidInput, "HCSCF-RECURSION0001",
                    "The bounded-recursion V1 options are invalid or their digest is stale.");
            if (recursiveSccs.Length != 0 && !recursionOptions.Enabled)
            {
                ManagedCallGraphSccV1 firstRecursiveScc = recursiveSccs[0];
                const int maximumDisplayedMembers = 4;
                string memberSummary = string.Join(", ", firstRecursiveScc.MethodIdentities.Take(maximumDisplayedMembers));
                string omittedSummary = firstRecursiveScc.MethodIdentities.Count > maximumDisplayedMembers
                    ? $", ... ({firstRecursiveScc.MethodIdentities.Count - maximumDisplayedMembers} omitted)"
                    : string.Empty;
                string cycleWitness = DescribeShortestCycles(firstRecursiveScc, callStackEdges);
                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-RECURSION1001",
                    $"Recursive managed SCCs ({recursiveSccs.Length}) require the explicit bounded-recursion V1 capability. Shortest cycles: {cycleWitness}. Members ({firstRecursiveScc.MethodIdentities.Count}): {memberSummary}{omittedSummary}.",
                    firstRecursiveScc.StableId);
            }
            if (recursiveSccs.Length > ManagedBoundedRecursionContractV1.MaximumRecursiveSccs)
            {
                ManagedCallGraphSccV1 secondRecursiveScc = recursiveSccs[1];
                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-RECURSION1002",
                    $"The V1 capability admits at most one recursive SCC; detected {recursiveSccs.Length}. Second SCC members: {secondRecursiveScc.MethodIdentities.Count}. Shortest cycles: {DescribeShortestCycles(secondRecursiveScc, callStackEdges)}.",
                    secondRecursiveScc.StableId);
            }

            ManagedRecursionProofV1[] recursionProofs = [];
            if (recursiveSccs.Length != 0)
            {
                ManagedRecursionProofResult proof = ProveBoundedRecursion(recursiveSccs[0], callStackEdges,
                    nodesByIdentity, decodedByIdentity, recursionOptions);
                if (proof.Failure is not null) return proof.Failure;
                recursionProofs = [proof.Proof!];
            }

            string[] compilationOrder = BuildCalleeFirstOrder(processed, executionEdges);
            int maximumDepth = ComputeCondensationDepth(sccs, callStackEdges, null);
            if (maximumDepth > budgets.MaximumAcyclicCallDepth)
                return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2007",
                    "The acyclic managed-call depth budget was exhausted.");
            int maximumDynamicDepth = ComputeCondensationDepth(sccs, callStackEdges,
                recursionProofs.ToDictionary(static proof => proof.SccStableId,
                    static proof => proof.MaximumSccInvocations, StringComparer.Ordinal));
            if (maximumDynamicDepth > recursionOptions.MaximumDynamicDepth && recursionProofs.Length != 0)
                return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-RECURSION2003",
                    $"The proven dynamic call depth {maximumDynamicDepth} exceeds the configured V1 limit {recursionOptions.MaximumDynamicDepth}.",
                    recursionProofs[0].SccStableId);

            string[] reachableLiterals = processed.SelectMany(method => decodedByIdentity[method]
                    .Where(static instruction => instruction.Encoding == 0x72)
                    .Select(instruction => nodesByIdentity[method].Module.Metadata.GetUserString(
                        MetadataTokens.UserStringHandle(instruction.Token))))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (reachableLiterals.Length > HybridCpuPlatformContractV1.MaximumManagedStringLiterals ||
                reachableLiterals.Sum(static literal => (long)literal.Length) > HybridCpuPlatformContractV1.MaximumManagedStringLiteralCodeUnits)
                return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCCIL1412",
                    "Reachable UTF-16 literal table exceeds the runtime registration budget.");
            ManagedStringLiteralPlanV1? literalPlan = reachableLiterals.Length == 0 ? null :
                CreateStringLiteralPlan(reachableLiterals, metadataBindings);
            Dictionary<string, RestrictedCilStringLiteralBindingV1> effectiveStrings = new Dictionary<string, RestrictedCilStringLiteralBindingV1>(_stringBindings, StringComparer.Ordinal);
            foreach (RestrictedCilStringLiteralBindingV1 binding in literalPlan?.Bindings ?? [])
            {
                if (effectiveStrings.TryGetValue(binding.Literal, out RestrictedCilStringLiteralBindingV1? supplied) &&
                    (supplied.LiteralHandle != binding.LiteralHandle || supplied.TypeHandle != binding.TypeHandle ||
                     supplied.TypeDescriptor.DescriptorDigest != binding.TypeDescriptor.DescriptorDigest))
                    return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCCIL1413",
                        "External literal binding conflicts with the image-owned reachable literal table.");
                effectiveStrings[binding.Literal] = binding;
            }

            List<ManagedCompiledMethodV1> compiled = new List<ManagedCompiledMethodV1>(compilationOrder.Length);
            List<ManagedAllocationTypeUseV1> allocationTypeUses = new List<ManagedAllocationTypeUseV1>();
            foreach (string methodIdentity in compilationOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ManagedGraphNode node = nodesByIdentity[methodIdentity];
                Dictionary<int, RestrictedCilManagedCallTargetV1> targets = orderedEdges
                    .Where(edge => edge.CallerIdentity == methodIdentity && !edge.IsDispatchCandidate &&
                        !edge.IsFunctionPointerTarget)
                    .GroupBy(static edge => edge.MetadataToken)
                    .ToDictionary(static group => group.Key, group =>
                    {
                        ManagedGraphNode callee = nodesByIdentity[group.First().CalleeIdentity];
                        return new RestrictedCilManagedCallTargetV1(group.Key, callee.Identity.StableIdentity,
                            callee.Signature.Parameters.Select(StackType).ToArray(), StackType(callee.Signature.ReturnType),
                            callee.Signature.AggregateReturn, callee.Signature.AggregateParameters, callee.Signature.Receiver);
                    });
                if (ehPlansByIdentity.ContainsKey(methodIdentity) && targets.Values.Any(target => target.AggregateReturn == NullableInt32Identity))
                    return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCCIL1890",
                        "Nullable<int> return calls require a non-EH primary caller lifetime.", methodIdentity);
                Dictionary<int, RestrictedCilDispatchBindingV1> nodeDispatch = new Dictionary<int, RestrictedCilDispatchBindingV1>(_dispatchBindings);
                foreach (KeyValuePair<(string Assembly, int Token), RestrictedCilDispatchBindingV1> binding in metadataDispatch.Where(row => row.Key.Assembly == node.Module.AssemblyName))
                    nodeDispatch[binding.Key.Token] = binding.Value;
                foreach (int token in nodeDispatch.Keys.ToArray())
                {
                    HashSet<int> callOffsets = decodedByIdentity[methodIdentity]
                        .Where(instruction => instruction.Token == token && instruction.Encoding == 0x6f)
                        .Select(static instruction => instruction.Offset).ToHashSet();
                    string[] exactTargets = orderedEdges.Where(edge => edge.CallerIdentity == methodIdentity &&
                            callOffsets.Contains(edge.CallerIlOffset) && edge.IsDispatchCandidate)
                        .Select(static edge => edge.CalleeIdentity).Distinct(StringComparer.Ordinal).ToArray();
                    if (exactTargets.Length == 1 && !nodeDispatch[token].RuntimeExternal)
                        nodeDispatch[token] = nodeDispatch[token] with { ExactImplementationIdentity = exactTargets[0] };
                }
                Dictionary<int, RestrictedCilAllocationBindingV1> nodeAllocations = (metadataBindings.AllocationsByAssembly
                        .GetValueOrDefault(node.Module.AssemblyName) ?? [])
                    .ToDictionary(static binding => binding.ConstructorMetadataToken);
                foreach ((int token, RestrictedCilAllocationBindingV1 binding) in _allocationBindings)
                    nodeAllocations[token] = binding;
                Dictionary<int, RestrictedCilArrayTypeBindingV1> nodeArrays = (metadataBindings.ArraysByAssembly
                        .GetValueOrDefault(node.Module.AssemblyName) ?? [])
                    .ToDictionary(static binding => binding.ElementTypeMetadataToken);
                foreach ((int token, RestrictedCilArrayTypeBindingV1 binding) in _arrayBindings)
                    nodeArrays[token] = binding;
                Dictionary<int, RestrictedCilTypeTestBindingV1> nodeTypeTests = (metadataBindings.TypeTestsByAssembly
                        .GetValueOrDefault(node.Module.AssemblyName) ?? [])
                    .ToDictionary(static binding => binding.TypeMetadataToken);
                foreach ((int token, RestrictedCilTypeTestBindingV1 binding) in _typeTestBindings)
                    nodeTypeTests[token] = binding;
                RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(_budgets, _matrix, RestrictedCilImportModeV1.ScalarControlFlowV2,
                    targets, _graphBudgets, effectiveFields.Values.ToArray(), nodeAllocations.Values.ToArray(),
                    nodeArrays.Values.ToArray(), effectiveStrings.Values.ToArray(), _valueTypeBindings.Values.ToArray(),
                    effectiveInitializers.Values.ToArray(), nodeDispatch.Values.ToArray(),
                    nodeTypeTests.Values.ToArray(), _functionPointerBindings.Values.ToArray(),
                    _calliBindings.Values.ToArray(), _delegateCreationBindings.Values.ToArray(),
                    _delegateInvokeBindings.Values.ToArray(),
                    metadataBindings.FieldDataByAssembly.GetValueOrDefault(node.Module.AssemblyName) ?? [],
                    _runtimeExternalBindings.Values.ToArray())
                { _exactStructOwnerIngress = node.Module.ImageDigest == primary.ImageDigest &&
                    !roots.Any(root => root.Identity.StableIdentity == node.Identity.StableIdentity),
                    _nullableInt32ParameterIngress = node.Signature.AggregateParameters?.Contains(NullableInt32Identity) == true &&
                    node.Module.ImageDigest == primary.ImageDigest && !roots.Any(root => root.Identity.StableIdentity == node.Identity.StableIdentity),
                    _nullableInt32ReturnIngress = node.Signature.AggregateReturn == NullableInt32Identity &&
                        node.Module.ImageDigest == primary.ImageDigest && !roots.Any(root => root.Identity.StableIdentity == node.Identity.StableIdentity),
                    _nullableInt32ReturnCalls = node.Module.ImageDigest == primary.ImageDigest && targets.Values.Any(target => target.AggregateReturn == NullableInt32Identity),
                    _externalEnumCarriers = ReferencedEnumCarriers(node.Module.Metadata, enumDeclarations, cancellationToken),
                    _externalScalarValues = ReferencedScalarValues(node.Module.Metadata, scalarDeclarations, cancellationToken) };
                ManagedCallGraphCompilationV1? argumentProofFailure = null;
                Dictionary<string, bool> argumentProofs = new Dictionary<string, bool>(StringComparer.Ordinal);
                HashSet<string> activeArgumentProofs = new HashSet<string>(StringComparer.Ordinal);
                bool ProveArgumentCall(ManagedGraphNode caller, int token, RestrictedCilProvenanceV1 proofProvenance, CancellationToken tokenCancellation) {
                    tokenCancellation.ThrowIfCancellationRequested();
                    ManagedCallGraphEdgeV1? edge = orderedEdges.FirstOrDefault(edge => edge.CallerIdentity == caller.Identity.StableIdentity &&
                        edge.MetadataToken == token && !edge.IsDispatchCandidate && !edge.IsFunctionPointerTarget);
                    if (edge is null || !nodesByIdentity.TryGetValue(edge.CalleeIdentity, out ManagedGraphNode? callee) ||
                        callee.Signature.Receiver is not null || !IsPrimitiveConstructorInput(callee.Signature.ReturnType) ||
                        callee.Signature.AggregateReturn is not (null or NullableInt32Identity) || callee.Signature.Parameters.Any(type => !IsPrimitiveConstructorInput(type))) return false;
                    bool nullableReturn = callee.Signature.AggregateReturn == NullableInt32Identity;
                    if (callee.Signature.AggregateParameters?.Any(identity => identity is not null) == true &&
                        (caller.Signature.AggregateReturn != NullableInt32Identity || caller.Module.ImageDigest != primary.ImageDigest ||
                         callee.Module.ImageDigest != primary.ImageDigest ||
                         roots.Any(root => root.Identity.StableIdentity == callee.Identity.StableIdentity) ||
                         nullableReturn && !IsExactNullableInt32Return(callee.Module.Metadata, callee.Module.Reader, callee.MethodHandle, callee.Signature, callee.GenericTypeArguments, callee.GenericMethodArguments))) return false;
                    // Nominal producer composition is confined to the primary private
                    // producer world. A scalar proof never gains nullable call authority.
                    if (nullableReturn && (caller.Signature.AggregateReturn != NullableInt32Identity ||
                        caller.Module.ImageDigest != primary.ImageDigest || callee.Module.ImageDigest != primary.ImageDigest ||
                        roots.Any(root => root.Identity.StableIdentity == callee.Identity.StableIdentity))) return false;
                    string key = callee.Identity.StableIdentity;
                    if (argumentProofs.TryGetValue(key, out bool previous)) return previous;
                    if (activeArgumentProofs.Contains(key)) return false;
                    if (activeArgumentProofs.Count >= 64 || ++resolutionSteps > budgets.MaximumMetadataResolutionSteps) {
                        argumentProofFailure = GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2002",
                            "The bounded helper-proof depth or metadata-resolution budget was exhausted.", methodIdentity);
                        return false;
                    }
                    if (!TryResolveManagedDefinition(modules, caller.Module, token, caller.GenericTypeArguments,
                        caller.GenericMethodArguments, out ManagedModuleContext? helperModule, out _, out MethodDefinitionHandle helperMethod,
                        out IReadOnlyList<RestrictedCilGenericArgumentV1>? helperTypes, out IReadOnlyList<RestrictedCilGenericArgumentV1>? helperArguments, out _) ||
                        // Both exact signed scalar contexts reuse the primitive
                        // signature filter above and the typed definition proof
                        // below. Context discovery alone never admits the body.
                        helperTypes.Count != 0 && (helperTypes.Count != 1 ||
                            helperTypes[0] != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32) &&
                            helperTypes[0] != new RestrictedCilGenericArgumentV1("System.Int64", RestrictedCilTypeV1.Int64)) ||
                        helperArguments.Count != 0 && (helperArguments.Count != 1 ||
                            helperArguments[0] != new RestrictedCilGenericArgumentV1("System.Int32", RestrictedCilTypeV1.Int32) &&
                            helperArguments[0] != new RestrictedCilGenericArgumentV1("System.Int64", RestrictedCilTypeV1.Int64)) ||
                        !helperTypes.SequenceEqual(callee.GenericTypeArguments) ||
                        !helperArguments.SequenceEqual(callee.GenericMethodArguments) ||
                        helperModule.ImageDigest != callee.Module.ImageDigest || MetadataTokens.GetToken(helperMethod) != callee.Identity.MetadataToken)
                        return false;
                    Dictionary<int, RestrictedCilManagedCallTargetV1> helperTargets = orderedEdges.Where(edge => edge.CallerIdentity == key && !edge.IsDispatchCandidate && !edge.IsFunctionPointerTarget)
                        .GroupBy(edge => edge.MetadataToken).ToDictionary(group => group.Key, group => {
                            ManagedGraphNode destination = nodesByIdentity[group.First().CalleeIdentity];
                            return new RestrictedCilManagedCallTargetV1(group.Key, destination.Identity.StableIdentity,
                                destination.Signature.Parameters.Select(StackType).ToArray(), StackType(destination.Signature.ReturnType),
                                destination.Signature.AggregateReturn, destination.Signature.AggregateParameters, destination.Signature.Receiver);
                        });
                    RestrictedCilImporterV1 proofImporter = new RestrictedCilImporterV1(_budgets, _matrix, RestrictedCilImportModeV1.ScalarControlFlowV2, helperTargets, _graphBudgets);
                    RestrictedCilManagedCallTargetV1 target = new RestrictedCilManagedCallTargetV1(token, key, callee.Signature.Parameters.Select(StackType).ToArray(),
                        StackType(callee.Signature.ReturnType), callee.Signature.AggregateReturn, callee.Signature.AggregateParameters, callee.Signature.Receiver);
                    activeArgumentProofs.Add(key);
                    bool valid;
                    try {
                        // Authority belongs to this exact resolved definition, including
                        // scalar dependencies; discovery and selected external bodies do
                        // not grant the primary non-root owner exception.
                        proofImporter._exactStructOwnerIngress = callee.Module.ImageDigest == primary.ImageDigest &&
                            !roots.Any(root => root.Identity.StableIdentity == callee.Identity.StableIdentity);
                        if (nullableReturn) {
                            proofImporter._nullableInt32ReturnIngress = true;
                            proofImporter._nullableInt32ParameterIngress = callee.Signature.AggregateParameters?.Contains(NullableInt32Identity) == true;
                            proofImporter._nullableInt32ReturnCalls = helperTargets.Values.Any(value => value.AggregateReturn == NullableInt32Identity);
                            proofImporter._externalArgumentHelperProof = (dependency, dependencyProvenance, dependencyCancellation) =>
                                ProveArgumentCall(callee, dependency, dependencyProvenance, dependencyCancellation);
                            // Reuse the same exact signature, purity, nominal stack and
                            // original IL/CFG/SSA/budget checks as the production import.
                            RestrictedCilImportResultV1 proof = proofImporter.ImportImageCore(helperModule.PeImage,
                                new(callee.Identity.DeclaringType, callee.Identity.MethodName, MetadataTokens.GetToken(helperMethod),
                                    helperTypes, helperArguments), tokenCancellation, helperModule.SourceIdentity, null, helperModule);
                            if (proof.Status == RestrictedCilImportStatusV1.BudgetExhausted)
                                argumentProofFailure ??= FromImportFailure(proof);
                            valid = proof.Status == RestrictedCilImportStatusV1.Success;
                        } else {
                            valid = proofImporter.IsPureConstructorArgumentDefinition(helperModule.Metadata, helperModule.Reader,
                                MetadataTokens.GetToken(helperMethod), target, proofProvenance, tokenCancellation,
                                dependency => ProveArgumentCall(callee, dependency, proofProvenance, tokenCancellation), helperArguments, helperTypes,
                                failure => argumentProofFailure ??= FromImportFailure(failure));
                        }
                    } finally { activeArgumentProofs.Remove(key); }
                    argumentProofs[key] = valid;
                    return valid;
                }
                importer._externalArgumentHelperProof = (token, proofProvenance, tokenCancellation) =>
                    ProveArgumentCall(node, token, proofProvenance, tokenCancellation);
                if (importer._exactStructOwnerIngress && IsExactStructScalarMethod(node.Module.Metadata,
                    node.Module.Metadata.GetMethodDefinition(node.MethodHandle), node.GenericTypeArguments, node.GenericMethodArguments)) {
                    // Discovery admitted only the shape. Production import requires
                    // the same exact graph edge, recursive purity and typed-body proof
                    // even if this scalar node sorts before its consuming producer.
                    ManagedCallGraphEdgeV1? incoming = orderedEdges.FirstOrDefault(edge => edge.CalleeIdentity == node.Identity.StableIdentity &&
                        !edge.IsDispatchCandidate && !edge.IsFunctionPointerTarget);
                    importer._exactStructScalarMethodIngress = incoming is not null &&
                        ProveArgumentCall(nodesByIdentity[incoming.CallerIdentity], incoming.MetadataToken,
                            new(node.Module.SourceIdentity, node.Module.ImageDigest, node.Identity.StableIdentity,
                                $"0x{node.Identity.MetadataToken:x8}", _matrix.ContractDigest, RestrictedCilSupportMatrixV1.OptionsDigest(_budgets),
                                $"{node.Identity.DeclaringType}.{node.Identity.MethodName}"), cancellationToken);
                    if (argumentProofFailure is not null) return argumentProofFailure;
                }
                RestrictedCilImportResultV1 imported = importer.ImportImageCore(node.Module.PeImage,
                    new(node.Identity.DeclaringType, node.Identity.MethodName, node.Identity.MetadataToken,
                        node.GenericTypeArguments, node.GenericMethodArguments), cancellationToken, node.Module.SourceIdentity,
                    ehPlansByIdentity.GetValueOrDefault(methodIdentity), node.Module);
                importer._externalArgumentHelperProof = null;
                if (argumentProofFailure is not null) return argumentProofFailure;
                if (imported.Status != RestrictedCilImportStatusV1.Success) return FromImportFailure(imported);
                foreach (int allocationOffset in imported.Program!.Instructions
                    .Where(static instruction => instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_alloc")
                    .Select(static instruction => instruction.SourceSpan?.StartOffset ?? -1).Distinct())
                {
                    DecodedInstruction? allocationInstruction = decodedByIdentity[methodIdentity].SingleOrDefault(
                        instruction => instruction.Offset == allocationOffset && instruction.Encoding == 0x73);
                    if (allocationInstruction is null ||
                        !nodeAllocations.TryGetValue(allocationInstruction.Token, out RestrictedCilAllocationBindingV1? binding) ||
                        binding.IsScalarValueProjection || binding.TypeDescriptor.Kind != HybridCpuManagedTypeKindV1.Class)
                        return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCCIL1414",
                            "Lowered object allocation lacks its exact class TypeDescriptor binding.",
                            OffsetIdentity(imported.Provenance!, allocationOffset));
                    allocationTypeUses.Add(new(methodIdentity, allocationOffset,
                        binding.TypeDescriptor.TypeId, binding.TypeHandle));
                }
                string[] runtimeHelperTargets = imported.Program!.Instructions
                    .Select(static instruction => instruction.Annotation.BranchTargetSymbolName)
                    .Where(static target => target is not null &&
                        HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(target) is
                            { Support: HybridCpuManagedAbiSupportV1.Supported })
                    .Select(static target => target!)
                    .Distinct(StringComparer.Ordinal).ToArray();
                compiled.Add(new(node.Identity, imported, orderedEdges.Where(edge => edge.CallerIdentity == methodIdentity &&
                        !edge.IsFunctionPointerTarget)
                    .Select(static edge => edge.CalleeIdentity).Concat(runtimeHelperTargets)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
                TestOnlyGcCorrelationV1.ObserveImportedMethod(compiled[^1]);
                TestOnlyCheckNumCallerEdgeCorrelationV1.ObserveImportedMethod(compiled[^1]);
            }

            string[] rootIdentities = roots.Select(static root => root.Identity.StableIdentity).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            string contractText = recursionOptions.Enabled
                ? $"{ManagedCallGraphContractText}|{ManagedBoundedRecursionExtensionText}|{ManagedBoundedRecursionContractV1.Default.ContractDigest}|{recursionOptions.OptionsDigest}"
                : ManagedCallGraphContractText;
            int exactGenericInstantiationCount = processed.Count(identity =>
                nodesByIdentity[identity].GenericTypeArguments.Count != 0 ||
                nodesByIdentity[identity].GenericMethodArguments.Count != 0);
            string frontendOptionsDigest = HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Hash(
                HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Serialize(new
                {
                    Schema = "hybridcpu.managed-frontend-options/v1",
                    Matrix = _matrix.ContractDigest,
                    ImportOptions = RestrictedCilSupportMatrixV1.OptionsDigest(_budgets),
                    GraphBudgets = budgets
                }));
            contractText += "|frontend-options=" + frontendOptionsDigest;
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.Encoding is >= NullableInt32New and <= NullableInitializeStack)))
                contractText += "|exact-corelib-nullable-int32-local/v1|opaque-packed-i8|checked-local-accessors|no-general-nullable-abi";
if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableParameter || row.Encoding is NullableInt32ParameterValue or NullableInt32ParameterPresence)))
contractText += "|primary-private-nullable-int32-parameter/v48|selected-pure-helper-scaled-ancestor-bound|selected-pure-helper-affine-ancestor-bound|selected-pure-helper-dependent-induction|selected-pure-helper-nested-induction|selected-pure-helper-sequential-induction|selected-pure-helper-int64-induction|selected-pure-helper-uint64-induction|selected-pure-helper-uint-induction|selected-pure-helper-inclusive-induction|selected-pure-helper-descending-induction|selected-pure-helper-signed-constant-seed|selected-pure-helper-positive-constant-stride|selected-pure-helper-immutable-i4-bound|selected-pure-helper-counted-induction|counted-mixed-signed-scalar-helper-arity3|exact-signed-scalar-type-method-context|counted-present-unary-binary-i8-helper|exact-pure-getter|selected-external-one-or-two-i4-helper-default|exact-method-or-type-int32-proof|lazy-local-copy-typed-i4-expressions|exact-conditional-proof-exits|explicit-immutable-parameter-presence-payload|exact-guarded-checked-payload|either-region-order-early-return|constant-or-immutable-i4-argument|packed-i8-ingress|no-public-external-nullable-abi";
            if (nodesByIdentity.Values.Any(node => node.Signature.AggregateReturn == NullableInt32Identity))
                contractText += "|primary-private-nullable-int32-return/v23|exact-primary-struct-signed-owner-context|exact-constrained-method-closed-signed-owner-context|exact-selected-int32-generic-fallback-argument|exact-int32-generic-default-local-zero-store-context|exact-selected-nullable-empty-local-constructor-context|exact-selected-nullable-method-context-member-local-proof|exact-unary-or-binary-nullable-method-parameter-int32-struct-constraint|exact-selected-i4-i8-type-method-context|exact-first-nullable-input-or-signed-scalars|fault-free-map-argument-ssa-and-budgeted-initializers|map-budgeted-pure-i4-default-expression|managed-il-no-implicit-synchronization|budgeted-exact-scalar-and-nullable-dependency-proof|one-to-eight-signed-i4-i8-register-carriers|exact-packed-i8-nominal-return|primary-call-owned-targets|pure-forward-switch-or-exact-counted-producer|counted-body-forward-switch-targets|nominal-counter-bound-facts-excluded|no-public-root-external-nullable-return";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableWide && !row.NullableUnsigned)))
                contractText += "|exact-corelib-nullable-int64-local/v1|full-i8-presence-pair|component-ssa-phis|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableUnsigned && !row.NullableWide && row.NullableNarrowBits == 0)))
                contractText += "|exact-corelib-nullable-uint32-local/v1|opaque-packed-i4-bits|unsigned-source-widening|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableUnsigned && row.NullableWide)))
                contractText += "|exact-corelib-nullable-uint64-local/v1|full-payload-presence-pair|component-ssa|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableNarrowBits == 16 && row.NullableUnsigned && !row.NullableChar)))
                contractText += "|exact-corelib-nullable-uint16-local/v1|bounded-u2-payload|constructor-default-truncation|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableNarrowBits == 16 && !row.NullableUnsigned)))
                contractText += "|exact-corelib-nullable-int16-local/v1|bounded-i2-payload|signed-accessor-default-extension|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableNarrowBits == 8 && row.NullableUnsigned && !row.NullableBoolean)))
                contractText += "|exact-corelib-nullable-uint8-local/v1|bounded-byte-payload|constructor-default-truncation|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableNarrowBits == 8 && !row.NullableUnsigned)))
                contractText += "|exact-corelib-nullable-int8-local/v1|bounded-sbyte-payload|signed-accessor-default-extension|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableChar)))
                contractText += "|exact-corelib-nullable-char-local/v1|distinct-char-nominal|bounded-u2-payload|constructor-default-truncation|no-nullable-abi";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.NullableBoolean)))
                contractText += "|exact-corelib-nullable-boolean-local/v1|distinct-boolean-nominal|cli-byte-payload-default|no-nullable-abi";
            foreach (string enumIdentity in decodedByIdentity.Values.SelectMany(rows => rows).Select(row => row.NullableEnumIdentity).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                contractText += (IsNullableEnumInt8(enumIdentity) ? "|exact-source-owned-nullable-enum-int8-local/v1|" : IsNullableEnumUInt8(enumIdentity) ? "|exact-source-owned-nullable-enum-uint8-local/v1|" : IsNullableEnumInt16(enumIdentity) ? "|exact-source-owned-nullable-enum-int16-local/v1|" : IsNullableEnumUInt16(enumIdentity) ? "|exact-source-owned-nullable-enum-uint16-local/v1|" : IsNullableEnumUInt32(enumIdentity) ? "|exact-source-owned-nullable-enum-uint32-local/v1|" : IsNullableEnumUInt64(enumIdentity) ? "|exact-source-owned-nullable-enum-uint64-local/v1|" : IsNullableEnumInt64(enumIdentity) ? "|exact-source-owned-nullable-enum-int64-local/v1|" : "|exact-source-owned-nullable-enum-int32-local/v1|") + enumIdentity;
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.Encoding == NullableCheckedValue)))
                contractText += "|checked-nullable-value/v1|invalid-operation-exception|native-eh-transfer";
            if (decodedByIdentity.Values.Any(rows => rows.Any(row => row.Encoding == NullableDefaultStack)))
                contractText += "|computed-nullable-default/v1|eager-typed-forward-cfg|erased-local-loan";
            if (exactGenericInstantiationCount != 0)
                contractText = $"{contractText}|{ExactAotGenericsContractV1.Default.ContractDigest}";
            if (exceptionMessagePlan is not null) contractText += "|virtual-slot-plan=" + exceptionMessagePlan.PlanDigest;
            foreach (ManagedVirtualSlotPlanV1? plan in closedVirtualPlans.Values.OrderBy(static row => row.SlotId))
                contractText += "|virtual-slot-plan=" + plan.PlanDigest;
            if (literalPlan is not null) contractText += "|string-literal-plan=" + literalPlan.PlanDigest;
            HybridCpuManagedTypeDescriptorV1[] orderedTypes = metadataBindings.Descriptors.Values.ToArray();
            if (literalPlan is not null &&
                !orderedTypes.Any(static descriptor => descriptor.StableIdentity == "System.String"))
            {
                HybridCpuManagedTypeDescriptorV1 stringDescriptor = literalPlan.Bindings[0].TypeDescriptor;
                if (orderedTypes.Any(descriptor => descriptor.TypeId == stringDescriptor.TypeId))
                    return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCCIL1413",
                        "The image-owned System.String descriptor collides with an existing managed TypeId.");
                orderedTypes = [.. orderedTypes, stringDescriptor];
            }
            orderedTypes = orderedTypes.OrderBy(static descriptor => descriptor.TypeId).ToArray();
            ManagedTypeUniverseRowV1[] typeRows = orderedTypes.Select((descriptor, index) =>
                new ManagedTypeUniverseRowV1(checked((ulong)(index + 1)), descriptor.TypeId,
                    descriptor.BaseTypeId ?? 0, descriptor.StableIdentity, descriptor)).ToArray();
            string typeUniverseDigest = HybridCpuPlatformContractV1.Hash(string.Join('|',
                "hybridcpu.managed-type-universe/v1", HybridCpuPlatformContractV1.ContractDigest,
                string.Join(';', typeRows.Select(static row => $"{row.TypeHandle}:{row.TypeId}:{row.BaseTypeId}:{row.Descriptor!.DescriptorDigest}"))));
            ManagedTypeUniversePlanV1 typeUniverse = new ManagedTypeUniversePlanV1(typeRows, typeUniverseDigest);
            contractText += "|type-universe=" + typeUniverse.PlanDigest;
            ManagedArrayTypeUseV1[] arrayTypeUses = processed.SelectMany(methodIdentity =>
            {
                ManagedGraphNode node = nodesByIdentity[methodIdentity];
                Dictionary<int, RestrictedCilArrayTypeBindingV1> bindings = (metadataBindings.ArraysByAssembly.GetValueOrDefault(node.Module.AssemblyName) ?? [])
                    .ToDictionary(static binding => binding.ElementTypeMetadataToken);
                foreach ((int token, RestrictedCilArrayTypeBindingV1 binding) in _arrayBindings)
                    bindings[token] = binding;
                return decodedByIdentity[methodIdentity]
                    .Where(static instruction => instruction.Encoding == 0x8d)
                    .Select(instruction => bindings.TryGetValue(instruction.Token, out RestrictedCilArrayTypeBindingV1? binding)
                        ? new ManagedArrayTypeUseV1(methodIdentity, instruction.Offset,
                            binding.TypeDescriptor.TypeId, binding.TypeHandle)
                        : throw new InvalidOperationException($"Reachable newarr at {methodIdentity}@IL_{instruction.Offset:x4} lacks its exact binding."));
            }).Distinct().OrderBy(static use => use.CallerIdentity, StringComparer.Ordinal)
                .ThenBy(static use => use.CilOffset).ToArray();
            foreach (ManagedArrayTypeUseV1 use in arrayTypeUses)
                contractText += $"|newarr={use.CallerIdentity}:IL_{use.CilOffset:x4}:{use.TypeId}:{use.TypeHandle}";
            ManagedAllocationTypeUseV1[] exactAllocationTypeUses = allocationTypeUses.Distinct()
                .OrderBy(static use => use.CallerIdentity, StringComparer.Ordinal)
                .ThenBy(static use => use.CilOffset).ToArray();
            foreach (ManagedAllocationTypeUseV1 use in exactAllocationTypeUses)
                contractText += $"|newobj={use.CallerIdentity}:IL_{use.CilOffset:x4}:{use.TypeId}:{use.TypeHandle}";
            IReadOnlyList<ManagedClosedInterfacePlanV1> closedInterfacePlans = RequiredClosedInterfacePlans(metadataBindings,
                processed.Select(identity => nodesByIdentity[identity].Identity.DeclaringType).Concat(
                    decodedByIdentity.SelectMany(pair => pair.Value.Where(instruction => instruction.Encoding is >= 0x7b and <= 0x80)
                        .Select(instruction => MetadataFieldOwner(nodesByIdentity[pair.Key].Module.Metadata, instruction.Token)))));
            foreach (ManagedClosedInterfacePlanV1 plan in closedInterfacePlans) contractText += "|closed-interface-plan=" + plan.PlanDigest;
            foreach (ManagedDispatchCallPlanV1? plan in dispatchCallPlans.OrderBy(static plan => plan.PlanDigest, StringComparer.Ordinal))
                contractText += "|dispatch-call-plan=" + plan.PlanDigest;
            string inputClosureDigest = HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Hash(
                HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Serialize(new
                {
                    Schema = "hybridcpu.managed-input-closure/v1",
                    Primary = new { primary.AssemblyName, primary.ImageDigest },
                    Dependencies = dependencies.Modules.OrderBy(static module => module.AssemblyName, StringComparer.Ordinal)
                        .Select(static module => new { module.AssemblyName, module.ImageDigest }).ToArray(),
                    MetadataOnly = metadataOnly.Modules.OrderBy(static module => module.AssemblyName, StringComparer.Ordinal)
                        .Select(static module => new { module.AssemblyName, module.ImageDigest }).ToArray()
                }));
            contractText += "|input-closure=" + inputClosureDigest;
            foreach (ManagedModuleContext? input in metadataOnly.Modules.OrderBy(static module => module.AssemblyName, StringComparer.Ordinal))
                contractText += $"|metadata-only={input.AssemblyName}:{input.ImageDigest}";
            string graphDigest = Hash(string.Join('|', contractText,
                string.Join(';', rootIdentities), string.Join(';', processed.Order(StringComparer.Ordinal)),
                string.Join(';', orderedEdges.Select(static edge => edge.StableId)), string.Join(';', compilationOrder),
                string.Join(';', recursionProofs.Select(static proof => proof.ProofDigest)), maximumDynamicDepth));
            ManagedCallGraphEvidenceV1 graph = new ManagedCallGraphEvidenceV1("hybridcpu.managed-call-graph/v1", 1, bodyWorld.Mode,
                rootIdentities, processed.Order(StringComparer.Ordinal).ToArray(), orderedEdges, sccs,
                compilationOrder, maximumDepth, graphDigest, Hash(contractText), recursionProofs,
                maximumDynamicDepth, recursionOptions.Enabled ? recursionOptions.OptionsDigest : string.Empty,
                recursionOptions.Enabled ? recursionOptions.MaximumStackBytes : 0,
                exactGenericInstantiationCount,
                exactGenericInstantiationCount == 0 ? string.Empty : ExactAotGenericsContractV1.Default.ContractDigest);
            RestrictedCilFieldDataBindingV1[] fieldData = metadataBindings.FieldDataByAssembly.Values
                .SelectMany(static rows => rows).OrderBy(static row => row.DataHandle).ToArray();
            (ManagedProfileClosureEvidenceV1? profileClosure, string? profileFailure) =
                ManagedProfileClosureContractV1.Validate(compiled, rootIdentities, graph.GraphDigest);
            if (profileFailure is not null)
                return GraphFailure(RestrictedCilImportStatusV1.Unsupported, "HCSCF-PROFILE2001", profileFailure);
            cancellationToken.ThrowIfCancellationRequested();
            return new(RestrictedCilImportStatusV1.Success, compiled, graph, [],
                [.. (exceptionMessagePlan is null ? Array.Empty<ManagedVirtualSlotPlanV1>() : [exceptionMessagePlan]),
                 .. closedVirtualPlans.Values.OrderBy(static row => row.SlotId)], closedInterfacePlans, literalPlan, typeUniverse,
                dispatchCallPlans.OrderBy(static plan => plan.CallerIdentity, StringComparer.Ordinal)
                    .ThenBy(static plan => plan.CilOffset).ToArray(), fieldData, profileClosure, arrayTypeUses,
                exactAllocationTypeUses);
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentOutOfRangeException)
        {
            return GraphFailure(RestrictedCilImportStatusV1.InvalidInput, "HCSCF-BODYWORLD0003",
                "The managed body-world metadata or method body is malformed.");
        }
        catch (OverflowException)
        {
            return GraphFailure(RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-BUDGET2008",
                "A deterministic body-world counter overflowed.");
        }
    }

    private static bool TryResolveManagedDefinition(
        IReadOnlyList<ManagedModuleContext> modules,
        ManagedModuleContext sourceModule,
        int token,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeContext,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodContext,
        out ManagedModuleContext targetModule,
        out TypeDefinitionHandle typeHandle,
        out MethodDefinitionHandle methodHandle,
        out IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments,
        out IReadOnlyList<RestrictedCilGenericArgumentV1> methodArguments,
        out string reason)
    {
        targetModule = sourceModule;
        MetadataReader metadata = sourceModule.Metadata;
        typeHandle = default;
        methodHandle = default;
        typeArguments = [];
        methodArguments = [];
        EntityHandle entity;
        try { entity = MetadataTokens.EntityHandle(token); }
        catch (ArgumentException)
        {
            reason = "The direct-call metadata token is invalid.";
            return false;
        }
        if (entity.Kind == HandleKind.MethodDefinition)
        {
            methodHandle = (MethodDefinitionHandle)entity;
            typeHandle = FindDeclaringTypeHandle(metadata, methodHandle);
            if (metadata.GetTypeDefinition(typeHandle).GetGenericParameters().Count != 0)
                typeArguments = typeContext ?? [];
            reason = string.Empty;
            return true;
        }
        if (entity.Kind == HandleKind.MethodSpecification)
        {
            MethodSpecificationHandle specificationHandle = (MethodSpecificationHandle)entity;
            MethodSpecification specification = metadata.GetMethodSpecification(specificationHandle);
            if (!TryParseMethodSpecificationArguments(metadata, specificationHandle, typeContext, methodContext,
                    out methodArguments, out reason))
                return false;
            int definitionToken = MetadataTokens.GetToken(specification.Method);
            if (!TryResolveManagedDefinition(modules, sourceModule, definitionToken, typeContext, methodContext,
                    out targetModule, out typeHandle, out methodHandle, out typeArguments,
                    out IReadOnlyList<RestrictedCilGenericArgumentV1> nestedArguments, out reason))
                return false;
            if (nestedArguments.Count != 0)
            {
                reason = "Nested MethodSpec method references are not admitted by exact AOT generics V1.";
                return false;
            }
            int arity = targetModule.Metadata.GetMethodDefinition(methodHandle).GetGenericParameters().Count;
            if (arity != methodArguments.Count)
            {
                reason = "The MethodSpec argument count does not match its generic method definition.";
                return false;
            }
            return true;
        }
        if (entity.Kind == HandleKind.MemberReference)
        {
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)entity);
            if (member.Parent.Kind == HandleKind.TypeDefinition)
            {
                typeHandle = (TypeDefinitionHandle)member.Parent;
            }
            else if (member.Parent.Kind == HandleKind.TypeReference)
            {
                TypeReferenceHandle typeReferenceHandle = (TypeReferenceHandle)member.Parent;
                string referencedType = NestedTypeName(metadata, typeReferenceHandle);
                string? referencedAssembly = ReferencedAssemblyName(metadata, typeReferenceHandle);
                (ManagedModuleContext Module, TypeDefinitionHandle Type)[] typeMatches = modules
                    .Where(module => referencedAssembly is null || module.AssemblyName == referencedAssembly)
                    .SelectMany(module => module.Metadata.TypeDefinitions
                        .Where(candidate => NestedTypeName(module.Metadata, candidate) == referencedType)
                        .Select(candidate => (module, candidate))).ToArray();
                if (typeMatches.Length != 1)
                {
                    reason = $"The type reference '{referencedType}' in assembly '{referencedAssembly ?? "<unspecified>"}' did not resolve to exactly one presented managed module body.";
                    return false;
                }
                targetModule = typeMatches[0].Module;
                typeHandle = typeMatches[0].Type;
            }
            else if (member.Parent.Kind == HandleKind.TypeSpecification)
            {
                if (!TryParseConstructedTypeSpecification(metadata, (TypeSpecificationHandle)member.Parent,
                        typeContext, methodContext, out string definitionIdentity, out EntityHandle genericDefinition, out typeArguments, out reason))
                    return false;
                string? definitionAssembly = genericDefinition.Kind == HandleKind.TypeReference
                    ? ReferencedAssemblyName(metadata, (TypeReferenceHandle)genericDefinition) : sourceModule.AssemblyName;
                if (string.IsNullOrWhiteSpace(definitionAssembly)) {
                    reason = "The constructed generic type has no exact assembly scope.";
                    return false;
                }
                (ManagedModuleContext Module, TypeDefinitionHandle Type)[] typeMatches = modules.Where(module => genericDefinition.Kind == HandleKind.TypeDefinition
                        ? ReferenceEquals(module, sourceModule) : module.AssemblyName == definitionAssembly)
                    .SelectMany(module => module.Metadata.TypeDefinitions
                        .Where(candidate => NestedTypeName(module.Metadata, candidate) == definitionIdentity)
                        .Select(candidate => (Module: module, Type: candidate))).ToArray();
                if (typeMatches.Length != 1 ||
                    typeMatches[0].Module.Metadata.GetTypeDefinition(typeMatches[0].Type).GetGenericParameters().Count != typeArguments.Count)
                {
                    reason = $"The constructed generic type '{definitionIdentity}' in assembly '{definitionAssembly}' did not resolve to one selected body with exact arity.";
                    return false;
                }
                targetModule = typeMatches[0].Module;
                typeHandle = typeMatches[0].Type;
            }
            else
            {
                reason = "The direct managed callee is outside the configured module body world.";
                return false;
            }
            MetadataReader targetMetadata = targetModule.Metadata;
            string name = metadata.GetString(member.Name);
            MethodDefinitionHandle[] named = targetMetadata.GetTypeDefinition(typeHandle).GetMethods()
                .Where(candidate => string.Equals(targetMetadata.GetString(targetMetadata.GetMethodDefinition(candidate).Name), name,
                    StringComparison.Ordinal)).ToArray();
            IReadOnlyList<RestrictedCilGenericArgumentV1> exactTypeArguments = typeArguments;
            ExactSignatureNames signatures = new ExactSignatureNames(exactTypeArguments);
            string referenceSignature = signatures.Method(metadata, member.Signature);
            MethodDefinitionHandle[] matches = named.Where(candidate =>
            {
                string definitionSignature = signatures.Method(targetMetadata,
                    targetMetadata.GetMethodDefinition(candidate).Signature);
                return referenceSignature == definitionSignature;
            }).ToArray();
            if (matches.Length != 1)
            {
                reason = "The same-module member reference did not resolve to exactly one managed body.";
                return false;
            }
            methodHandle = matches[0];
            reason = string.Empty;
            return true;
        }
        reason = "The call token is not an exact direct managed method.";
        return false;
    }

    private static TypeDefinitionHandle FindDeclaringTypeHandle(MetadataReader metadata, MethodDefinitionHandle method)
    {
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
            if (metadata.GetTypeDefinition(typeHandle).GetMethods().Contains(method)) return typeHandle;
        throw new BadImageFormatException("A method definition has no declaring type.");
    }

    private static string? ReferencedAssemblyName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        EntityHandle scope = metadata.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
            scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference
            ? metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : null;
    }

    private static bool IsExactCallvirtTarget(MetadataReader metadata, TypeDefinitionHandle typeHandle,
        MethodDefinitionHandle methodHandle)
    {
        MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
        return !method.Attributes.HasFlag(MethodAttributes.Abstract) &&
            (!method.Attributes.HasFlag(MethodAttributes.Virtual) ||
             method.Attributes.HasFlag(MethodAttributes.Final) ||
             type.Attributes.HasFlag(TypeAttributes.Sealed));
    }

    private static ManagedCallGraphSccV1[] ComputeManagedSccs(
        IEnumerable<string> methods,
        IReadOnlyList<ManagedCallGraphEdgeV1> edges)
    {
        string[] orderedMethods = methods.Order(StringComparer.Ordinal).ToArray();
        Dictionary<string, string[]> successors = orderedMethods.ToDictionary(method => method,
            method => edges.Where(edge => edge.CallerIdentity == method).Select(static edge => edge.CalleeIdentity)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        Dictionary<string, int> indexByMethod = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, int> lowByMethod = new Dictionary<string, int>(StringComparer.Ordinal);
        Stack<string> stack = new Stack<string>();
        HashSet<string> onStack = new HashSet<string>(StringComparer.Ordinal);
        List<string[]> components = new List<string[]>();
        int nextIndex = 0;
        void Visit(string method)
        {
            indexByMethod[method] = nextIndex;
            lowByMethod[method] = nextIndex++;
            stack.Push(method);
            onStack.Add(method);
            foreach (string successor in successors[method])
            {
                if (!indexByMethod.ContainsKey(successor))
                {
                    Visit(successor);
                    lowByMethod[method] = Math.Min(lowByMethod[method], lowByMethod[successor]);
                }
                else if (onStack.Contains(successor))
                    lowByMethod[method] = Math.Min(lowByMethod[method], indexByMethod[successor]);
            }
            if (lowByMethod[method] != indexByMethod[method]) return;
            List<string> component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            } while (current != method);
            components.Add(component.Order(StringComparer.Ordinal).ToArray());
        }
        foreach (string method in orderedMethods)
            if (!indexByMethod.ContainsKey(method)) Visit(method);
        return components.OrderBy(static component => component[0], StringComparer.Ordinal).Select(component =>
        {
            bool recursive = component.Length > 1 || edges.Any(edge => edge.CallerIdentity == component[0] && edge.CalleeIdentity == component[0]);
            string stableId = $"scc:{Hash(string.Join('|', component))}";
            return new ManagedCallGraphSccV1(stableId, component, recursive);
        }).ToArray();
    }

    private static string DescribeShortestCycles(
        ManagedCallGraphSccV1 scc,
        IReadOnlyList<ManagedCallGraphEdgeV1> edges)
    {
        HashSet<string> members = scc.MethodIdentities.ToHashSet(StringComparer.Ordinal);
        ManagedCallGraphEdgeV1[] internalEdges = edges
            .Where(edge => members.Contains(edge.CallerIdentity) && members.Contains(edge.CalleeIdentity))
            .OrderBy(static edge => edge.CallerIdentity, StringComparer.Ordinal)
            .ThenBy(static edge => edge.CallerIlOffset)
            .ThenBy(static edge => edge.CalleeIdentity, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, ManagedCallGraphEdgeV1[]> outgoing = members.ToDictionary(method => method,
            method => internalEdges.Where(edge => edge.CallerIdentity == method).ToArray(), StringComparer.Ordinal);

        Dictionary<string, ManagedCallGraphEdgeV1[]> candidates = new Dictionary<string, ManagedCallGraphEdgeV1[]>(StringComparer.Ordinal);
        foreach (ManagedCallGraphEdgeV1 seed in internalEdges)
        {
            ManagedCallGraphEdgeV1[] candidate;
            if (seed.CallerIdentity == seed.CalleeIdentity)
            {
                candidate = [seed];
            }
            else
            {
                Queue<string> queue = new Queue<string>();
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal) { seed.CalleeIdentity };
                Dictionary<string, ManagedCallGraphEdgeV1> previous = new Dictionary<string, ManagedCallGraphEdgeV1>(StringComparer.Ordinal);
                queue.Enqueue(seed.CalleeIdentity);
                while (queue.Count != 0 && !visited.Contains(seed.CallerIdentity))
                {
                    string current = queue.Dequeue();
                    foreach (ManagedCallGraphEdgeV1 edge in outgoing[current])
                    {
                        if (!visited.Add(edge.CalleeIdentity)) continue;
                        previous.Add(edge.CalleeIdentity, edge);
                        queue.Enqueue(edge.CalleeIdentity);
                    }
                }
                if (!visited.Contains(seed.CallerIdentity)) continue;

                List<ManagedCallGraphEdgeV1> returnPath = new List<ManagedCallGraphEdgeV1>();
                for (string current = seed.CallerIdentity; current != seed.CalleeIdentity;)
                {
                    ManagedCallGraphEdgeV1 edge = previous[current];
                    returnPath.Add(edge);
                    current = edge.CallerIdentity;
                }
                returnPath.Reverse();
                candidate = [seed, .. returnPath];
            }

            string key = CanonicalCycleKey(candidate);
            candidates.TryAdd(key, candidate);
        }

        if (candidates.Count == 0) return "unavailable";
        const int maximumDisplayedCycles = 4;
        return string.Join("; ", candidates.OrderBy(static item => item.Value.Length)
            .ThenBy(static item => item.Key, StringComparer.Ordinal)
            .Take(maximumDisplayedCycles)
            .Select((item, index) => $"#{index + 1} {DescribeCycle(item.Value)}"));
    }

    private static string CanonicalCycleKey(IReadOnlyList<ManagedCallGraphEdgeV1> cycle)
    {
        string? canonicalKey = null;
        for (int start = 0; start < cycle.Count; start++)
        {
            string key = string.Join('|', Enumerable.Range(0, cycle.Count)
                .Select(index => cycle[(start + index) % cycle.Count].StableId));
            if (canonicalKey is null || StringComparer.Ordinal.Compare(key, canonicalKey) < 0)
                canonicalKey = key;
        }

        return canonicalKey ?? string.Empty;
    }

    private static string DescribeCycle(IReadOnlyList<ManagedCallGraphEdgeV1> cycle)
    {
        const int maximumDisplayedEdges = 8;
        string witness = DisplayMethodIdentity(cycle[0].CallerIdentity);
        foreach (ManagedCallGraphEdgeV1 edge in cycle.Take(maximumDisplayedEdges))
            witness += $" --IL_{edge.CallerIlOffset:x4}--> {DisplayMethodIdentity(edge.CalleeIdentity)}";
        if (cycle.Count > maximumDisplayedEdges)
            witness += $" --...({cycle.Count - maximumDisplayedEdges} edges omitted)...-->";
        return witness;
    }

    private static string DisplayMethodIdentity(string identity)
    {
        int separator = identity.IndexOf(':', "mmid:".Length);
        string display = separator >= 0 && separator + 1 < identity.Length ? identity[(separator + 1)..] : identity;
        int instanceSignature = display.IndexOf("instance(", StringComparison.Ordinal);
        int staticSignature = display.IndexOf('(');
        int signature = instanceSignature >= 0 ? instanceSignature : staticSignature;
        return signature > 0 ? display[..signature] : display;
    }

    private static string[] BuildCalleeFirstOrder(
        IEnumerable<string> methods,
        IReadOnlyList<ManagedCallGraphEdgeV1> edges)
    {
        Dictionary<string, string[]> successors = methods.ToDictionary(method => method,
            method => edges.Where(edge => edge.CallerIdentity == method).Select(static edge => edge.CalleeIdentity)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
        List<string> result = new List<string>();
        void Visit(string method)
        {
            if (!visited.Add(method)) return;
            foreach (string successor in successors[method]) Visit(successor);
            result.Add(method);
        }
        foreach (string method in successors.Keys.Order(StringComparer.Ordinal)) Visit(method);
        return result.ToArray();
    }

    private static int ComputeCondensationDepth(
        IReadOnlyList<ManagedCallGraphSccV1> sccs,
        IReadOnlyList<ManagedCallGraphEdgeV1> edges,
        IReadOnlyDictionary<string, int>? recursiveWeights)
    {
        Dictionary<string, ManagedCallGraphSccV1> byMethod = sccs.SelectMany(scc =>
            scc.MethodIdentities.Select(method => (method, scc))).ToDictionary(static item => item.method,
                static item => item.scc, StringComparer.Ordinal);
        Dictionary<string, string[]> successors = sccs.ToDictionary(static scc => scc.StableId, scc =>
            edges.Where(edge => scc.MethodIdentities.Contains(edge.CallerIdentity, StringComparer.Ordinal))
                .Select(edge => byMethod[edge.CalleeIdentity].StableId).Where(target => target != scc.StableId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        Dictionary<string, int> memo = new Dictionary<string, int>(StringComparer.Ordinal);
        int Depth(string sccId)
        {
            if (memo.TryGetValue(sccId, out int depth)) return depth;
            int weight = recursiveWeights is not null && recursiveWeights.TryGetValue(sccId, out int bounded) ? bounded : 1;
            depth = successors[sccId].Length == 0 ? weight : checked(weight + successors[sccId].Max(Depth));
            memo.Add(sccId, depth);
            return depth;
        }
        return successors.Count == 0 ? 0 : successors.Keys.Max(Depth);
    }

    private static ManagedRecursionProofResult ProveBoundedRecursion(
        ManagedCallGraphSccV1 scc,
        IReadOnlyList<ManagedCallGraphEdgeV1> edges,
        IReadOnlyDictionary<string, ManagedGraphNode> nodes,
        IReadOnlyDictionary<string, IReadOnlyList<DecodedInstruction>> decoded,
        ManagedBoundedRecursionOptionsV1 options)
    {
        if (scc.MethodIdentities.Count > ManagedBoundedRecursionContractV1.MaximumMethodsPerRecursiveScc)
            return ProofFailure("HCSCF-RECURSION2001", "The recursive SCC method-count budget was exhausted.", scc.StableId);
        HashSet<string> members = scc.MethodIdentities.ToHashSet(StringComparer.Ordinal);
        foreach (string method in scc.MethodIdentities)
        {
            ManagedGraphNode node = nodes[method];
            if (node.Signature.ReturnType != RestrictedCilTypeV1.Int32 ||
                node.Signature.Parameters.Count != 1 || node.Signature.Parameters[0] != RestrictedCilTypeV1.Int32)
                return ProofFailure("HCSCF-RECURSION1003",
                    "Bounded recursion V1 requires exact static Int32(Int32) SCC methods.", method);
            ManagedCallGraphEdgeV1[] internalEdges = edges.Where(edge => edge.CallerIdentity == method && members.Contains(edge.CalleeIdentity)).ToArray();
            if (internalEdges.Length != 1)
                return ProofFailure("HCSCF-RECURSION1004",
                    "Every recursive SCC method must contain exactly one recursive edge.", method);
            IReadOnlyList<DecodedInstruction> instructions = decoded[method];
            int callIndex = instructions.ToList().FindIndex(instruction => instruction.Offset == internalEdges[0].CallerIlOffset);
            if (callIndex < 3 || !IsArgumentZero(instructions[callIndex - 3]) ||
                !IsConstant(instructions[callIndex - 2], 1) || instructions[callIndex - 1].Encoding != 0x59)
                return ProofFailure("HCSCF-RECURSION1005",
                    "A recursive edge must receive the exact arg0 - 1 countdown transition.",
                    $"{method}@IL_{internalEdges[0].CallerIlOffset:x4}");
            int guardIndex = Enumerable.Range(1, callIndex - 1).FirstOrDefault(index =>
                IsArgumentZero(instructions[index - 1]) && instructions[index].Encoding is 0x2c or 0x39 &&
                instructions[index].BranchTarget > internalEdges[0].CallerIlOffset, -1);
            bool guarded = guardIndex >= 0 &&
                !instructions.Take(guardIndex - 1).Any(instruction => ControlFlowTargets(instruction).Any()) &&
                !instructions.Any(instruction => ControlFlowTargets(instruction).Any(target => target <= instruction.Offset));
            if (!guarded)
                return ProofFailure("HCSCF-RECURSION1006",
                    "The recursive edge is not dominated by an arg0 == 0 terminating path.", method);
        }

        List<int> ingress = new List<int>();
        foreach (ManagedCallGraphEdgeV1 edge in edges.Where(edge => members.Contains(edge.CalleeIdentity) && !members.Contains(edge.CallerIdentity)))
        {
            IReadOnlyList<DecodedInstruction> instructions = decoded[edge.CallerIdentity];
            int callIndex = instructions.ToList().FindIndex(instruction => instruction.Offset == edge.CallerIlOffset);
            if (callIndex < 1 || !TryGetInt32Constant(instructions[callIndex - 1], out int value) || value < 0)
                return ProofFailure("HCSCF-RECURSION1007",
                    "Every recursive SCC ingress must pass one non-negative compile-time Int32 constant.",
                    $"{edge.CallerIdentity}@IL_{edge.CallerIlOffset:x4}");
            ingress.Add(value);
        }
        if (ingress.Count == 0)
            return ProofFailure("HCSCF-RECURSION1008",
                "A recursive SCC requires at least one proven constant ingress edge from outside the SCC.", scc.StableId);
        int maximumInvocations = checked(ingress.Max() + 1);
        if (maximumInvocations > options.MaximumDynamicDepth)
            return ProofFailure("HCSCF-RECURSION2003",
                $"The proven SCC invocation bound {maximumInvocations} exceeds the configured V1 depth limit {options.MaximumDynamicDepth}.", scc.StableId,
                RestrictedCilImportStatusV1.BudgetExhausted);
        int[] orderedIngress = ingress.Order().ToArray();
        string digest = Hash(string.Join('|', ManagedBoundedRecursionContractV1.Default.ContractDigest,
            options.OptionsDigest, scc.StableId, string.Join(';', scc.MethodIdentities), maximumInvocations,
            ManagedBoundedRecursionContractV1.CountdownStep, string.Join(',', orderedIngress)));
        return new(new(scc.StableId, scc.MethodIdentities, maximumInvocations,
            ManagedBoundedRecursionContractV1.CountdownStep, orderedIngress, digest), null);
    }

    private static bool IsArgumentZero(DecodedInstruction instruction) =>
        instruction.Encoding == 0x02 || instruction.Encoding == 0x0e && instruction.Literal == 0;

    private static bool IsConstant(DecodedInstruction instruction, int expected) =>
        TryGetInt32Constant(instruction, out int value) && value == expected;

    private static bool TryGetInt32Constant(DecodedInstruction instruction, out int value)
    {
        if (instruction.Encoding is >= 0x15 and <= 0x20 && instruction.Literal is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)instruction.Literal;
            return true;
        }
        value = 0;
        return false;
    }

    private static ManagedRecursionProofResult ProofFailure(string code, string message, string identity,
        RestrictedCilImportStatusV1 status = RestrictedCilImportStatusV1.Unsupported) =>
        new(null, GraphFailure(status, code, message, identity));

    private static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static ManagedCallGraphCompilationV1 FromImportFailure(RestrictedCilImportResultV1 failure) =>
        new(failure.Status, [], null, failure.Diagnostics);

    private static ManagedCallGraphCompilationV1 GraphFailure(
        RestrictedCilImportStatusV1 status,
        string code,
        string message,
        string? identity = null) =>
        new(status, [], null, [new(code, BoundGraphDiagnosticMessage(message), identity)]);

    public static string BoundGraphDiagnosticMessage(string message)
    {
        if (Encoding.UTF8.GetByteCount(message) <= MaximumGraphDiagnosticUtf8Bytes)
            return message;

        const string suffix = " ...[diagnostic truncated to deterministic UTF-8 budget]";
        int payloadBudget = MaximumGraphDiagnosticUtf8Bytes - Encoding.UTF8.GetByteCount(suffix);
        int low = 0;
        int high = message.Length;
        while (low < high)
        {
            int candidate = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(message.AsSpan(0, candidate)) <= payloadBudget)
                low = candidate;
            else
                high = candidate - 1;
        }

        if (low < message.Length && low > 0 && char.IsHighSurrogate(message[low - 1]))
            low--;
        return message[..low] + suffix;
    }

    private sealed record ManagedGraphNode(
        ManagedModuleContext Module,
        TypeDefinitionHandle TypeHandle,
        MethodDefinitionHandle MethodHandle,
        ManagedMethodIdentityV1 Identity,
        MethodSignature Signature,
        IReadOnlyList<RestrictedCilGenericArgumentV1> GenericTypeArguments,
        IReadOnlyList<RestrictedCilGenericArgumentV1> GenericMethodArguments);


    private sealed record ManagedRecursionProofResult(
        ManagedRecursionProofV1? Proof,
        ManagedCallGraphCompilationV1? Failure);
}
