using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

internal static class ClosedInterfaceSmoke
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        byte[] pe = File.ReadAllBytes(typeof(ClosedComparable).Assembly.Location);
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        ManagedBodyWorldModuleV1[] references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "mscorlib.dll" }
            .Select(name => new ManagedBodyWorldModuleV1(File.ReadAllBytes(Path.Combine(runtime, name)), name)).ToArray();
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedBodyWorldV1 primitiveWorld = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            pe, "primitive-interface-candidates", [new(nameof(PrimitiveDispatchCaller), nameof(PrimitiveDispatchCaller.Invoke))], [],
            MetadataOnlyModules: references);
        ManagedCallGraphCompilationV1 primitive = importer.ImportBodyWorld(primitiveWorld);
        ManagedBodyWorldV1 stringWorld = primitiveWorld with { Roots = [new(nameof(StringDispatchCaller), nameof(StringDispatchCaller.Invoke))] };
        ManagedCallGraphCompilationV1 stringDispatch = importer.ImportBodyWorld(stringWorld);
        Check(stringDispatch.Status == RestrictedCilImportStatusV1.Success && stringDispatch.DispatchCallPlans is
            [{ RuntimeExternal: false, Candidates.Count: 2 }], "Exact String interface binding: " + string.Join(';', stringDispatch.Diagnostics));
        ManagedDispatchCallPlanV1 stringPlan = stringDispatch.DispatchCallPlans!.Single();
        int[] expectedMethods = new[] { typeof(StringDispatchA), typeof(StringDispatchB) }
            .Select(type => type.GetMethod("Write", [typeof(string)])!.MetadataToken).Order().ToArray();
        Check(stringDispatch.Methods.Where(method => method.Identity.MethodName == "Write").Select(method => method.Identity.MetadataToken).Order()
            .SequenceEqual(expectedMethods), "String interface dispatch must not admit Object overload bodies with the same stack carrier.");
        Check(stringPlan.SlotId == HybridCPU.ManagedRuntime.HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(
            stringPlan.InterfaceTypeId, nameof(IStringDispatchProbe) + ".Write", "instance(System.String):System.Void"),
            "String slot identity must retain the exact signature.");
        ManagedCallGraphCompilationV1 byteArrayDispatch =importer.ImportBodyWorld(primitiveWorld with
        { Roots=[new(nameof(ByteArrayDispatchCaller),nameof(ByteArrayDispatchCaller.Invoke))] });
        Check(byteArrayDispatch.Status==RestrictedCilImportStatusV1.Success&&byteArrayDispatch.DispatchCallPlans is
            [{RuntimeExternal:false,Candidates.Count:2}],"Exact byte[] interface binding: "+string.Join(';',byteArrayDispatch.Diagnostics));
        ManagedDispatchCallPlanV1 byteArrayPlan =byteArrayDispatch.DispatchCallPlans!.Single();
        Check(byteArrayPlan.SlotId==HybridCPU.ManagedRuntime.HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(
                byteArrayPlan.InterfaceTypeId,nameof(IByteArrayDispatchProbe)+".Write","instance(System.Byte[]):System.Void")&&
              byteArrayDispatch.Methods.Where(method=>method.Identity.MethodName=="Write").All(method=>
                  method.Identity.MetadataToken!=typeof(ByteArrayDispatchA).GetMethod("Write",[typeof(sbyte[])])!.MetadataToken),
            "byte[] slot/candidates must remain distinct from sbyte[] despite ObjectReference carriers.");
        PersistedAssemblyBuilder childBuilder = new PersistedAssemblyBuilder(new AssemblyName("DerivedStringMetadata"), typeof(object).Assembly);
        childBuilder.DefineDynamicModule("DerivedStringMetadata").DefineType("DerivedStringA", TypeAttributes.Public, typeof(StringDispatchA)).CreateType();
        using MemoryStream childBytes = new MemoryStream(); childBuilder.Save(childBytes);
        ManagedCallGraphCompilationV1 withChild = importer.ImportBodyWorld(stringWorld with
        { MetadataOnlyModules = [.. references, new(childBytes.ToArray(), "derived-string-metadata")] });
        Check(withChild.DispatchCallPlans is [{ RuntimeExternal: true, Candidates.Count: 0 }],
            "A metadata-only descendant must invalidate the non-sealed leaf proof.");
        Check(primitive.Status == RestrictedCilImportStatusV1.Success && primitive.DispatchCallPlans is
            [{ RuntimeExternal: false, Candidates.Count: 2 }] &&
            primitive.Methods.Count(method => method.Identity.MethodName == "Ping") == 2,
            "Exact sealed primitive implementors must enter candidate body reachability: " + string.Join(';', primitive.Diagnostics));
        ManagedCallGraphCompilationV1 defaultDispatch = importer.ImportBodyWorld(primitiveWorld with
        { Roots = [new(nameof(DefaultDispatchCaller), nameof(DefaultDispatchCaller.Invoke))] });
        Check(defaultDispatch.Status == RestrictedCilImportStatusV1.Success && defaultDispatch.DispatchCallPlans is
            [{ RuntimeExternal: false, Candidates.Count: 2 }] &&
            defaultDispatch.DispatchCallPlans.Single().Candidates.Any(candidate =>
                candidate.RuntimeTypeIdentity == nameof(DefaultDispatchInherited) &&
                candidate.ImplementationIdentity.Contains(nameof(IPrimitiveDefaultDispatch), StringComparison.Ordinal)) &&
            defaultDispatch.DispatchCallPlans.Single().Candidates.Any(candidate =>
                candidate.RuntimeTypeIdentity == nameof(DefaultDispatchOverride) &&
                candidate.ImplementationIdentity.Contains(nameof(DefaultDispatchOverride), StringComparison.Ordinal)),
            "A leaf exact DIM call must map an inherited default body and an explicit override to their receiver types: " +
            string.Join(';', defaultDispatch.Diagnostics));
        ScalarControlFlowV2LinkedProgramV1 primitiveLink = new ScalarControlFlowV2ObjectLinkerV1().Link(primitive, primitive.Graph!.RootIdentities.Single());
        Check(!primitiveLink.Diagnostics.Any(d => d.Code == "HCSCF-LINK4011") && primitiveLink.RestrictedImage is null,
            "Exact image-owned dispatch binding must reach real backend/link validation, without authorizing missing runtime helpers: " + string.Join(';', primitiveLink.Diagnostics));
        PersistedAssemblyBuilder derivedBuilder = new PersistedAssemblyBuilder(new AssemblyName("DerivedInterfaceMetadata"), typeof(object).Assembly);
        ModuleBuilder derivedModule = derivedBuilder.DefineDynamicModule("DerivedInterfaceMetadata");
        TypeBuilder derivedType = derivedModule.DefineType("IDerivedPrimitive", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        derivedType.AddInterfaceImplementation(typeof(IPrimitiveDispatchProbe));
        derivedType.CreateType();
        using MemoryStream derivedBytes = new MemoryStream();
        derivedBuilder.Save(derivedBytes);
        ManagedCallGraphCompilationV1 inherited = importer.ImportBodyWorld(primitiveWorld with
        { MetadataOnlyModules = [.. references, new(derivedBytes.ToArray(), "derived-interface-metadata")] });
        Check(inherited.DispatchCallPlans is [{ RuntimeExternal: true, Candidates.Count: 0 }] &&
            new ScalarControlFlowV2ObjectLinkerV1().Link(inherited, inherited.Graph!.RootIdentities.Single()).Diagnostics.Any(d => d.Code == "HCSCF-LINK4011"),
            "Metadata-only derived interface must prevent publishing a partial direct implementor set.");
        ManagedDispatchCallPlanV1 dispatchPlan = primitive.DispatchCallPlans!.Single();
        ulong interfaceTypeId = primitive.TypeUniverse!.Rows.Single(row => row.StableIdentity == nameof(IPrimitiveDispatchProbe)).TypeId;
        ulong expectedSlot = HybridCPU.ManagedRuntime.HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(
            interfaceTypeId, nameof(IPrimitiveDispatchProbe) + ".Ping", "instance():System.Int32");
        Check(dispatchPlan.InterfaceTypeId == interfaceTypeId && dispatchPlan.SlotId == expectedSlot,
            "Primitive interface call plan must use runtime TypeId and canonical runtime slot, not metadata row IDs.");
        IrProgram dispatchProgram = primitive.Methods.Single(method => method.Identity.MethodName == nameof(PrimitiveDispatchCaller.Invoke)).Import.Program!;
        Dictionary<string, ulong> values = dispatchProgram.ValueFlow.Values.Where(value => value.StableId.EndsWith(":abi", StringComparison.Ordinal))
            .ToDictionary(value => value.StableId, _ => 0UL);
        ulong Read(IrOperand operand) => operand.Kind == IrOperandKind.Constant ? operand.Value :
            operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 0 ? 0 : values[operand.Name];
        bool verifiedOperands = false;
        bool checkedReceiver = false;
        foreach (IrInstruction instruction in dispatchProgram.Instructions)
        {
            IReadOnlyList<IrOperand> operands = instruction.Operands;
            int callOperandBase = instruction.Opcode == HybridCpuOpcode.JALR && operands.Count != 0 &&
                operands[0].Name.Contains(":long-call-target-abi:value", StringComparison.Ordinal) ? 1 : 0;
            if (instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_null_check")
            {
                Check(!checkedReceiver && Read(operands[callOperandBase]) == 0,
                    "Interface null check must occur once with the original receiver.");
                checkedReceiver = true;
                // Inspect the successful continuation; the throwing helper's native implementation
                // is a separate image obligation, not simulated by this IR structural regression.
                continue;
            }
            if (instruction.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_resolve_interface")
            {
                Check(checkedReceiver, "Interface lookup must never precede the managed receiver null check.");
                Check(Read(operands[callOperandBase + 1]) == interfaceTypeId &&
                    Read(operands[callOperandBase + 2]) == expectedSlot,
                    "Materialized resolver call operands must match exact runtime plan IDs.");
                verifiedOperands = true;
                break;
            }
            if (instruction.Opcode == HybridCpuOpcode.AUIPC &&
                instruction.StableIdentity.EndsWith(":long-call-high", StringComparison.Ordinal))
                continue;
            ulong result = instruction.Opcode switch
            {
                HybridCpuOpcode.ADDI or HybridCpuOpcode.ADD => unchecked(Read(operands[0]) + Read(operands[1])),
                HybridCpuOpcode.SLLI => Read(operands[0]) << (int)Read(operands[1]),
                HybridCpuOpcode.ORI => Read(operands[0]) | Read(operands[1]),
                _ => throw new Exception("Unexpected primitive dispatch prefix: " + instruction.Opcode)
            };
            values[instruction.Annotation.Defs.Single(operand => operand.Kind == IrOperandKind.VirtualValue).Name] = result;
        }
        Check(verifiedOperands, "Primitive call must retain a resolver call for its two implementations.");
        ManagedCallGraphCompilationV1 reorderedPrimitive = importer.ImportBodyWorld(primitiveWorld with { MetadataOnlyModules = references.Reverse().ToArray() });
        Check(reorderedPrimitive.DispatchCallPlans!.Single().PlanDigest == dispatchPlan.PlanDigest,
            "Runtime interface ID binding must be independent of metadata input order.");
        // Exercise object metadata construction separately; this does not authorize the external call site.
        ManagedDispatchObjectV1.Emit(primitive with { DispatchCallPlans = [dispatchPlan, dispatchPlan] });
        ManagedDispatchTypeObjectArtifactV1 descriptorObject = ManagedDispatchTypeObjectV1.Emit(primitive);
        ManagedDispatchCallPlanV1 externalServicePlan = dispatchPlan with
        {
            InterfaceTypeId = 7,
            Candidates = [new("runtime-service", "__hybridcpu_managed_guest_service")],
            RuntimeExternal = false,
            PlanDigest = new string('7', 64)
        };
        ManagedCallGraphCompilationV1 mixed = primitive with { DispatchCallPlans = [dispatchPlan, externalServicePlan] };
        Check(ManagedDispatchTypeObjectV1.Emit(mixed).ObjectArtifact.Bytes.SequenceEqual(descriptorObject.ObjectArtifact.Bytes) &&
              ManagedDispatchObjectV1.Emit(mixed).Bytes.SequenceEqual(ManagedDispatchObjectV1.Emit(primitive).Bytes),
            "Runtime-external provider IDs must not enter image dispatch descriptor/table closure.");
        bool mixedServiceRejected = false;
        try { ManagedDispatchTypeObjectV1.Emit(mixed with { DispatchCallPlans = [externalServicePlan with
            { Candidates = [.. externalServicePlan.Candidates, dispatchPlan.Candidates[0]] }] }); }
        catch (ArgumentException) { mixedServiceRejected = true; }
        Check(mixedServiceRejected, "Mixed runtime-service/image candidate ownership must fail closed.");
        HybridCpuStaticLinkArtifactV1 descriptorLink = new HybridCpuStaticLinkerV1().Link([new(ManagedDispatchTypeObjectV1.ModuleIdentity, descriptorObject.ObjectArtifact.Bytes)]);
        Check(descriptorLink.Status == HybridCpuLinkStatusV1.Success && descriptorObject.Rows.Count == 3,
            "Dispatch type object must link both implementations and their interface: " + string.Join(';', descriptorLink.Diagnostics));
        HybridCpuManagedTypeRegistrationV1[] registrations = ManagedDispatchTypeObjectV1.Registrations(descriptorObject, descriptorLink);
        Dictionary<ulong, ulong> imageHandles = descriptorObject.Rows.Select((row, index) => (row.Descriptor.TypeId, Handle: (ulong)(index + 101)))
            .ToDictionary(row => row.TypeId, row => row.Handle);
        HybridCpuManagedTypeSystemBuildV1 loadedTypes = HybridCPU.ManagedRuntime.HybridCpuManagedImageTypeLoaderV1.Load(descriptorLink.ImageBytes, registrations, imageHandles);
        Check(loadedTypes.IsSuccess, "Linked dispatch descriptors must install in runtime type system: " + loadedTypes.Reason);
        DeterministicRuntimeKernelV1 bootstrapKernel = new HybridCPU.RuntimeKernel.DeterministicRuntimeKernelV1();
        Check(bootstrapKernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "Bootstrap regression kernel boot.");
        int bootstrapCalls = 0;
        Dictionary<string, HybridCpuRuntimeHelperEntryV1> bootstrapHelpers = new Dictionary<string, HybridCPU.ManagedRuntime.HybridCpuRuntimeHelperEntryV1>
        { ["__hybridcpu_runtime_bootstrap"] = _ => { bootstrapCalls++; return true; } };
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrapDescriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "runtime-entry", "managed-entry",
            runtimeHelpers: [new("__hybridcpu_runtime_bootstrap",
                HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper("__hybridcpu_runtime_bootstrap")!.Signature, true)],
            managedTypes: registrations);
        HybridCpuManagedBootstrapRuntimeV1 bootstrapRuntime = new HybridCPU.ManagedRuntime.HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest);
        Check(!bootstrapRuntime.Bootstrap(bootstrapDescriptor, bootstrapKernel, bootstrapHelpers).IsSuccess && bootstrapCalls == 0,
            "Bootstrap must not claim registered types or invoke helpers without the installed image type system.");
        HybridCpuManagedBootstrapResultV1 bootstrapResult = bootstrapRuntime.Bootstrap(bootstrapDescriptor, bootstrapKernel, bootstrapHelpers, loadedTypes.TypeSystem);
        Check(bootstrapResult.IsSuccess && bootstrapCalls == 1 && bootstrapResult.RegisteredTypes!.Count == registrations.Length,
            "Decoded exact runtime types must satisfy bootstrap registration checks.");
        HybridCpuManagedTypeRegistrationV1[] wrongRegistrations = registrations.ToArray();
        wrongRegistrations[0] = wrongRegistrations[0] with { DescriptorDigest = new string('f', 64) };
        HybridCpuImageRuntimeBootstrapDescriptorV1 wrongBootstrap = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "runtime-entry", "managed-entry",
            runtimeHelpers: bootstrapDescriptor.RuntimeHelpers, managedTypes: wrongRegistrations);
        Check(!bootstrapRuntime.Bootstrap(wrongBootstrap, bootstrapKernel, bootstrapHelpers, loadedTypes.TypeSystem).IsSuccess && bootstrapCalls == 1,
            "Rehashed bootstrap with a mismatched type descriptor must fail before helper effects.");
        foreach (HybridCpuManagedTypeRegistrationV1 registration in registrations)
            Check(loadedTypes.TypeSystem!.TypeHandle(registration.TypeId) == imageHandles[registration.TypeId] &&
                loadedTypes.TypeSystem.Resolve(registration.TypeId)!.DescriptorDigest == registration.DescriptorDigest,
                "Runtime installation must preserve image handles (including gaps) and exact descriptor identity.");
        foreach (ManagedDispatchCandidatePlanV1 candidate in dispatchPlan.Candidates)
            Check(loadedTypes.TypeSystem!.IsAssignable(primitive.TypeUniverse.Rows.Single(row => row.StableIdentity == candidate.RuntimeTypeIdentity).TypeId,
                interfaceTypeId), "Decoded runtime receiver must implement the dispatch interface.");
        HybridCpuManagedTypeRegistrationV1[] badRegistration = registrations.ToArray();
        badRegistration[0] = badRegistration[0] with { MetadataSizeBytes = badRegistration[0].MetadataSizeBytes - 1 };
        Check(!HybridCPU.ManagedRuntime.HybridCpuManagedImageTypeLoaderV1.Load(descriptorLink.ImageBytes, badRegistration, imageHandles).IsSuccess,
            "Truncated descriptor must not publish a partial runtime type system.");
        Dictionary<ulong, ulong> duplicateHandles = imageHandles.ToDictionary(row => row.Key, _ => 1UL);
        Check(!HybridCPU.ManagedRuntime.HybridCpuManagedImageTypeLoaderV1.Load(descriptorLink.ImageBytes, registrations, duplicateHandles).IsSuccess &&
            !HybridCPU.ManagedRuntime.HybridCpuManagedImageTypeLoaderV1.Load(descriptorLink.ImageBytes, registrations, new Dictionary<ulong, ulong>()).IsSuccess,
            "Missing/duplicate image handles must fail closed, never trigger runtime renumbering.");
        byte[] corruptMetadata = descriptorLink.ImageBytes.ToArray();
        corruptMetadata[registrations[0].MetadataOffsetBytes] = 0xff;
        Check(!HybridCPU.ManagedRuntime.HybridCpuManagedImageTypeLoaderV1.Load(corruptMetadata, registrations, imageHandles).IsSuccess,
            "Corrupted descriptor framing must fail before runtime installation.");
        foreach (ManagedDispatchTypeImageRowV1 row in descriptorObject.Rows)
        {
            HybridCpuLinkedSymbolV1 symbol = descriptorLink.Symbols.Single(symbol => symbol.Name == row.Symbol);
            byte[] expected = new HybridCpuManagedTypeMetadataEncoderV1().Encode([row.Descriptor]).Bytes;
            HybridCpuManagedTypeRegistrationV1 registration = registrations.Single(registration => registration.TypeId == row.Descriptor.TypeId);
            Check(registration.MetadataOffsetBytes == (int)(symbol.Address - descriptorLink.ImageBase) &&
                registration.MetadataSizeBytes == row.SizeBytes && registration.DescriptorDigest == row.Descriptor.DescriptorDigest,
                "Production bootstrap registration must retain the exact linked range and descriptor digest.");
            Check(symbol.Size == (ulong)row.SizeBytes && descriptorLink.ImageBytes.AsSpan(
                checked((int)(symbol.Address - descriptorLink.ImageBase)), row.SizeBytes).SequenceEqual(expected),
                "Linked descriptor offset/size must identify exact existing type metadata bytes.");
        }
        Check(descriptorObject.ObjectArtifact.Bytes.AsSpan().SequenceEqual(ManagedDispatchTypeObjectV1.Emit(
            primitive with { TypeUniverse = primitive.TypeUniverse with { Rows = primitive.TypeUniverse.Rows.Reverse().ToArray() } }).ObjectArtifact.Bytes),
            "Dispatch descriptor HCO must be independent of input row order.");
        bool missingDependencyRejected = false;
        try { ManagedDispatchTypeObjectV1.Emit(primitive with { TypeUniverse = primitive.TypeUniverse with
            { Rows = primitive.TypeUniverse.Rows.Where(row => row.TypeId != interfaceTypeId).ToArray() } }); }
        catch (ArgumentException) { missingDependencyRejected = true; }
        Check(missingDependencyRejected, "Missing interface descriptor dependency must fail closed.");
        string candidateType = dispatchPlan.Candidates[0].RuntimeTypeIdentity;
        ManagedTypeUniverseRowV1 candidateRow = primitive.TypeUniverse.Rows.Single(row => row.StableIdentity == candidateType);
        bool shapeRejected = false;
        try { ManagedDispatchTypeObjectV1.Emit(primitive with { TypeUniverse = primitive.TypeUniverse with
            { Rows = primitive.TypeUniverse.Rows.Select(row => row.StableIdentity == candidateType
                ? row with { Descriptor = row.Descriptor! with { Kind = HybridCpuManagedTypeKindV1.ValueType } } : row).ToArray() } }); }
        catch (ArgumentException error) when (error.Message.Contains("shape-aware", StringComparison.Ordinal)) { shapeRejected = true; }
        Check(shapeRejected, "Shape-bearing dispatch types must not lose their metadata extensions.");
        HybridCpuManagedTypeDescriptorV1 noInterface = candidateRow.Descriptor! with { InterfaceTypeIds = [] };
        noInterface = noInterface with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(noInterface) };
        foreach (ManagedTypeUniverseRowV1? invalidRow in new[]
        {
            candidateRow with { Descriptor = null },
            candidateRow with { Descriptor = candidateRow.Descriptor! with { InstanceSizeBytes = candidateRow.Descriptor!.InstanceSizeBytes + 8 } },
            candidateRow with { Descriptor = noInterface }
        })
        {
            bool rejected = false;
            try
            {
                ManagedDispatchObjectV1.Emit(primitive with { TypeUniverse = primitive.TypeUniverse with
                { Rows = primitive.TypeUniverse.Rows.Select(row => row.StableIdentity == candidateType ? invalidRow : row).ToArray() } });
            }
            catch (ArgumentException error) when (error.Message.Contains("lacks an exact descriptor", StringComparison.Ordinal))
            { rejected = true; }
            Check(rejected, "Missing, stale or non-implementing receiver descriptor must reject dispatch metadata.");
        }
        ManagedDispatchCallPlanV1 conflictingPlan = dispatchPlan with
        {
            Candidates = [dispatchPlan.Candidates[0] with { ImplementationIdentity = dispatchPlan.Candidates[1].ImplementationIdentity }]
        };
        foreach (ManagedDispatchCallPlanV1[]? plans in new[] { new[] { dispatchPlan, conflictingPlan }, new[] { conflictingPlan, dispatchPlan } })
        {
            bool rejected = false;
            try { ManagedDispatchObjectV1.Emit(primitive with { DispatchCallPlans = plans }); }
            catch (ArgumentException error) when (error.Message.Contains("Conflicting interface dispatch implementations", StringComparison.Ordinal))
            { rejected = true; }
            Check(rejected, "Conflicting interface targets must fail closed regardless of call-plan order.");
        }
        ManagedCallGraphCompilationV1 external = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            pe, "external-dispatch-proof", [new(nameof(ExternalDispatchCaller), nameof(ExternalDispatchCaller.Invoke))], []));
        Check(external.Status == RestrictedCilImportStatusV1.Success &&
            external.DispatchCallPlans is [ { RuntimeExternal: true, Candidates.Count: 0 } ],
            "External interface call must retain its unresolved call-site plan.");
        ScalarControlFlowV2LinkedProgramV1 externalLink = new ScalarControlFlowV2ObjectLinkerV1().Link(external, external.Graph!.RootIdentities.Single());
        Check(externalLink.RestrictedImage is null && externalLink.Diagnostics.Any(d =>
            d.Code == "HCSCF-LINK4011" && d.Message.Contains("IExternalDispatchProbe.Ping", StringComparison.Ordinal)),
            "Missing external implementation must fail closed naming the actual member, not an unrelated declaration.");
        ManagedBodyWorldV1 World(string name, IReadOnlyList<ManagedBodyWorldModuleV1>? refs) => new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "closed-interface-smoke",
            [new(typeof(ClosedComparable).FullName!, name)], [], MetadataOnlyModules: refs);
        ManagedCallGraphCompilationV1 missing = importer.ImportBodyWorld(World(nameof(ClosedComparable.Ping), null));
        Check(missing.Diagnostics.Any(d => d.Code == "HCCIL1016"), "Missing interface metadata must retain cctor gate");
        ManagedCallGraphCompilationV1 missingTarget = importer.ImportBodyWorld(World(nameof(ClosedComparable.Ping), [references[1]]));
        Check(missingTarget.Diagnostics.Any(d => d.Code == "HCCIL1016"), "A forwarder without its target must not invent a definition");
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(World(nameof(ClosedComparable.Ping), references));
        Check(graph.Status == RestrictedCilImportStatusV1.Success, "Closed interface graph: " + string.Join(';', graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Check(graph.Graph!.Edges.Any(edge => edge.IsTypeInitializerTarget), "Closed interface owner cctor must remain reachable");
        ManagedClosedInterfacePlanV1 plan = graph.ClosedInterfacePlans!.Single();
        Check(plan.StableIdentity.StartsWith("[System.Private.CoreLib]System.IComparable`1<", StringComparison.Ordinal) &&
            plan.TypeArguments.Single() == $"[{typeof(ClosedComparable).Assembly.GetName().Name}]ClosedComparable" &&
            plan.MethodDeclarations.Single().Contains("::CompareToinstance(["), "Forwarding and VAR substitution must be exact");
        Check(graph.Methods.All(method => method.Identity.DeclaringType == typeof(ClosedComparable).FullName),
            "Metadata-only methods must not enter the executable graph");
        ManagedCallGraphCompilationV1 again = importer.ImportBodyWorld(World(nameof(ClosedComparable.Ping), references.Reverse().ToArray()));
        Check(again.Status == RestrictedCilImportStatusV1.Success && again.ClosedInterfacePlans!.Single().PlanDigest == plan.PlanDigest &&
            again.Graph!.GraphDigest == graph.Graph.GraphDigest, "Metadata order must not affect plans or graph");
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph.RootIdentities.Single());
        Check(!linked.Diagnostics.Any(d => d.Code == "HCSCF-LINK4011") &&
            graph.Methods.SelectMany(method => method.Import.Program!.Instructions).All(instruction =>
                instruction.Annotation.BranchTargetSymbolName != "__hybridcpu_managed_resolve_interface"),
            "An unused declaration plan must not demand runtime interface dispatch authority: " +
            string.Join(';', linked.Diagnostics));
        ManagedCallGraphCompilationV1 receiver = importer.ImportBodyWorld(World(".ctor", references));
        Check(receiver.Status == RestrictedCilImportStatusV1.Success &&
            receiver.Methods.Single(method => method.Identity.MethodName == ".ctor").Import.ReceiverAbi is not null &&
            receiver.Methods.Single(method => method.Identity.MethodName == ".ctor").Identity.CanonicalSignature.Contains("ClosedComparable&"),
            "Struct constructor must use the typed receiver loan: " + string.Join(';', receiver.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        // CoreLib grows with the host runtime. Use the existing small dependency
        // so byte-budget rejection cannot hide the duplicate-identity control.
        ManagedBodyWorldModuleV1 dependency = new ManagedBodyWorldModuleV1(File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location), "metadata-callee");
        const long metadataByteBudget = 32 * 1024 * 1024;
        Check(65L * dependency.PeImage.Length <= metadataByteBudget,
            "Small metadata fixture must remain within the byte budget for duplicate/count controls");
        ManagedCallGraphCompilationV1 duplicate = importer.ImportBodyWorld(World(nameof(ClosedComparable.Ping), [dependency, dependency]));
        Check(duplicate.Status == RestrictedCilImportStatusV1.InvalidInput && duplicate.Graph is null &&
            duplicate.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD0003") &&
            !duplicate.Diagnostics.Any(d => d.Code == "HCSCF-BUDGET2010"), "Duplicate metadata assembly must fail closed before graph construction");
        ManagedCallGraphCompilationV1 overBudget = importer.ImportBodyWorld(World(nameof(ClosedComparable.Ping), Enumerable.Repeat(dependency, 65).ToArray()));
        Check(overBudget.Status == RestrictedCilImportStatusV1.BudgetExhausted && overBudget.Graph is null &&
            overBudget.Diagnostics.Any(d => d.Code == "HCSCF-BUDGET2010"), "Metadata input count must be bounded before PE loading");
        int byteOverflowCount = checked((int)(metadataByteBudget / references[0].PeImage.Length + 1));
        Check(byteOverflowCount <= 64, "Byte-budget control must stay within the module-count budget");
        ManagedCallGraphCompilationV1 byteOverflow = importer.ImportBodyWorld(World(nameof(ClosedComparable.Ping), Enumerable.Repeat(references[0], byteOverflowCount).ToArray()));
        Check(byteOverflow.Status == RestrictedCilImportStatusV1.BudgetExhausted && byteOverflow.Graph is null &&
            byteOverflow.Diagnostics.Any(d => d.Code == "HCSCF-BUDGET2010") &&
            !byteOverflow.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD0003"), "Metadata byte budget must precede duplicate PE loading");
        Console.WriteLine($"PASS metadata admission controls: duplicate=HCSCF-BODYWORLD0003; count=65; byte-overflow-count={byteOverflowCount}; dependency-bytes={dependency.PeImage.Length}; failed=0; skipped=0");
        ManagedCallGraphCompilationV1 unrelated = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "unrelated-metadata",
            [new(typeof(UnsignedDivisionFixture).FullName!, nameof(UnsignedDivisionFixture.Word))], [], MetadataOnlyModules: references));
        Check(unrelated.Status == RestrictedCilImportStatusV1.Success && unrelated.ClosedInterfacePlans!.Count == 0 &&
            new ScalarControlFlowV2ObjectLinkerV1().Link(unrelated, unrelated.Graph!.RootIdentities.Single()).Status == ScalarControlFlowV2LinkStatusV1.Success,
            "Unreachable interface declarations must not contaminate a scalar graph");
        ManagedBodyWorldV1 callWorld = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "metadata-authority",
            [new(typeof(MetadataOnlyCaller).FullName!, nameof(MetadataOnlyCaller.Read))], [], MetadataOnlyModules: [dependency]);
        ManagedCallGraphCompilationV1 unavailable = importer.ImportBodyWorld(callWorld);
        Check(unavailable.Diagnostics.Any(d => d.Code == "HCSCF-BODYWORLD1002") && unavailable.Graph is null,
            "An available metadata-only CIL method must NOT be executable: " + string.Join(';', unavailable.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Check(importer.ImportBodyWorld(callWorld with { MetadataOnlyModules = null, DependencyModules = [dependency] }).Status == RestrictedCilImportStatusV1.Success,
            "The same method must resolve only after explicit body-world admission");

        // Inspect declaration products without exposing test-only APIs.
        Type importerType = typeof(RestrictedCilImporterV1);
        Type context = importerType.GetNestedType("ManagedModuleContext", BindingFlags.NonPublic)!;
        List<IDisposable> disposable = new List<IDisposable>();
        Array Contexts(IEnumerable<ManagedBodyWorldModuleV1> inputs)
        {
            ManagedBodyWorldModuleV1[] rows = inputs.ToArray();
            Array array = Array.CreateInstance(context, rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                IDisposable item = (IDisposable)Activator.CreateInstance(context, new object[] { rows[i].PeImage, rows[i].SourceIdentity })!;
                disposable.Add(item); array.SetValue(item, i);
            }
            return array;
        }
        try
        {
            byte[] twinA = Twin("ScopeA"), twinB = Twin("ScopeB");
            Array owners = Contexts([new(pe, "owner"), new(twinA, "A"), new(twinB, "B")]);
            Array metadata = Contexts(references);
            object result = importerType.GetMethod("BuildMetadataBindings", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { owners, metadata })!;
            IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1> descriptors = (IReadOnlyDictionary<string, HybridCpuManagedTypeDescriptorV1>)result.GetType().GetProperty("Descriptors")!.GetValue(result)!;
            HybridCpuManagedTypeDescriptorV1 owner = descriptors[nameof(ClosedComparable)];
            HybridCpuManagedTypeDescriptorV1 iface = descriptors[plan.StableIdentity];
            Check(owner.InterfaceTypeIds.SequenceEqual(new[] { iface.TypeId }) && owner.InstanceSizeBytes == 4 && owner.ValueTypeShape is not null,
                "Owner must retain its exact interface and inline payload");
            Check(descriptors.ContainsKey("[ScopeA]ITwin`1<[ScopeA]Twin>") && descriptors.ContainsKey("[ScopeB]ITwin`1<[ScopeB]Twin>"),
                "Same-named definitions in different assemblies must not alias closed-interface identities");
            Check(descriptors["[ScopeA]ITwin`1<[ScopeA]Twin>"].TypeId != descriptors["[ScopeB]ITwin`1<[ScopeB]Twin>"].TypeId,
                "Closed interface IDs must be assembly scoped");
            Check(!descriptors.ContainsKey(typeof(OpenInterfaceOwner<>).FullName!) &&
                !descriptors.ContainsKey(nameof(DefaultInterfaceOwner)) && !descriptors.ContainsKey(nameof(ConstrainedInterfaceOwner)) &&
                !descriptors.ContainsKey(nameof(RefLikeInterfaceOwner)),
                "Open definitions, default interface bodies, byref-like arguments and generic constraints must remain closed");
        }
        finally { foreach (IDisposable item in disposable) item.Dispose(); }
        IComparable<ClosedComparable> a = new ClosedComparable(-5);
        Check(a.CompareTo(new ClosedComparable(9)) < 0 && ClosedComparable.Ping() == 7,
            "CoreCLR reference compare and initialization behavior");
        Console.WriteLine("PASS closed interface metadata, forwarders/VAR/scoped IDs, retained cctor, metadata-only and receiver/link gates");
    }

    private static byte[] Twin(string assembly)
    {
        PersistedAssemblyBuilder builder = new PersistedAssemblyBuilder(new AssemblyName(assembly), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule(assembly);
        TypeBuilder iface = module.DefineType("ITwin`1", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        iface.DefineGenericParameters("T");
        Type itype = iface.CreateType()!;
        TypeBuilder twin = module.DefineType("Twin", TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed, typeof(ValueType));
        twin.DefineField("Value", typeof(int), FieldAttributes.Public);
        Type argument = twin.CreateType()!;
        // Distinct owner names keep legacy non-generic owner identities unambiguous.
        TypeBuilder owner = module.DefineType(assembly + "Owner", TypeAttributes.Public, typeof(object));
        owner.AddInterfaceImplementation(itype.MakeGenericType(argument)); owner.CreateType();
        using MemoryStream stream = new MemoryStream(); builder.Save(stream); return stream.ToArray();
    }
}

public readonly struct ClosedComparable : IComparable<ClosedComparable>
{
    private readonly int _value;
    private static readonly int Marker;
    static ClosedComparable() { Marker = 7; }
    public ClosedComparable(int value) { _value = value; }
    public int CompareTo(ClosedComparable other) => _value.CompareTo(other._value);
    public static int Ping() => Marker;
}
public class OpenInterfaceOwner<T> : IComparable<T> { public int CompareTo(T? other) => 0; }
public interface IDefaultDeclaration<T> { int Value(T value) => 1; }
public class DefaultInterfaceOwner : IDefaultDeclaration<int> { }
public interface IConstrainedDeclaration<T> where T : class { int Value(T value); }
public class ConstrainedInterfaceOwner : IConstrainedDeclaration<string> { public int Value(string value) => 1; }
public ref struct RefLikeInterfaceOwner : IComparable<RefLikeInterfaceOwner>
{
    public int Value;
    public int CompareTo(RefLikeInterfaceOwner other) => Value.CompareTo(other.Value);
}
public static class MetadataOnlyCaller { public static int Read() => ManagedPortSmoke.Dependency.MetadataOnlyCallee.Read(); }
public interface IExternalDispatchProbe { int Ping(); }
public static class ExternalDispatchCaller { public static int Invoke(IExternalDispatchProbe value) => value.Ping(); }
public interface IPrimitiveDispatchProbe { int Ping(); }
public sealed class PrimitiveDispatchA : IPrimitiveDispatchProbe { public int Ping() => 7; }
public sealed class PrimitiveDispatchB : IPrimitiveDispatchProbe { public int Ping() => 9; }
public static class PrimitiveDispatchCaller { public static int Invoke(IPrimitiveDispatchProbe value) => value.Ping(); }
public interface IPrimitiveDefaultDispatch { int Value() => 11; }
public sealed class DefaultDispatchInherited : IPrimitiveDefaultDispatch { }
public sealed class DefaultDispatchOverride : IPrimitiveDefaultDispatch { public int Value() => 13; }
public static class DefaultDispatchCaller { public static int Invoke(IPrimitiveDefaultDispatch value) => value.Value(); }
public interface IStringDispatchProbe { void Write(string message); }
public class StringDispatchA : IStringDispatchProbe
{
    public void Write(string message) { }
    public void Write(object message) { }
}
public sealed class StringDispatchB : IStringDispatchProbe
{
    public void Write(string message) { }
    public void Write(object message) { }
}
public static class StringDispatchCaller { public static void Invoke(IStringDispatchProbe target, string message) => target.Write(message); }
public interface IByteArrayDispatchProbe { void Write(byte[] value); }
public sealed class ByteArrayDispatchA:IByteArrayDispatchProbe { public void Write(byte[] value){} public void Write(sbyte[] value){} }
public sealed class ByteArrayDispatchB:IByteArrayDispatchProbe { public void Write(byte[] value){} }
public static class ByteArrayDispatchCaller { public static void Invoke(IByteArrayDispatchProbe target,byte[] value)=>target.Write(value); }
